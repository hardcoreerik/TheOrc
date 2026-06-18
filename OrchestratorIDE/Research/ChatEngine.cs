// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Research;

/// <summary>
/// Conversation engine for the research chat tab.
///
/// Supports two tool-execution paths:
///  1. Native — model returns tool_calls via the API (qwen, llama3, hermes, nemotron…)
///  2. ReAct  — model embeds XML tool_call blocks in plain text (fallback for all models)
///
/// Both paths use the same WebSearchTool / FetchPageTool implementations.
/// </summary>
public class ChatEngine
{
    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly IModelRuntime _runtime;
    private readonly WebSearchTool _webSearch = new();
    private readonly FetchPageTool _fetchPage = new();

    // ── Conversation history ──────────────────────────────────────────────────
    private readonly List<AgentMessage> _history = [];

    // ── Active model (can be changed between turns) ───────────────────────────
    public string Model { get; set; }

    // ── Events for UI ─────────────────────────────────────────────────────────

    /// <summary>Fired for each streamed text token from the model.</summary>
    public event Action<string>? OnToken;

    /// <summary>Fired when a tool call starts. Args: toolName, argsJson.</summary>
    public event Action<string, string>? OnToolStart;

    /// <summary>Fired when a tool call completes. Args: toolName, result (truncated).</summary>
    public event Action<string, string>? OnToolComplete;

    /// <summary>Fired when the full turn (including all tool loops) is finished.</summary>
    public event Action<string>? OnTurnComplete;

    /// <summary>Fired when an error occurs.</summary>
    public event Action<string>? OnError;

    // ── Construction ──────────────────────────────────────────────────────────

    public ChatEngine(IModelRuntime runtime, string model)
    {
        _runtime = runtime;
        Model   = model;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public IReadOnlyList<AgentMessage> History => _history;

    public void ClearHistory() => _history.Clear();

    /// <summary>
    /// Sends a user message, runs the tool loop, fires OnToken/OnToolStart/
    /// OnToolComplete events, then fires OnTurnComplete with the full final text.
    /// </summary>
    public async Task SendAsync(string userMessage, CancellationToken ct = default)
    {
        _history.Add(new AgentMessage { Role = MessageRole.User, Content = userMessage });

        try
        {
            await RunTurnAsync(ct);
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("Cancelled.");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Error: {ex.Message}");
        }
    }

    // ── Turn execution ────────────────────────────────────────────────────────

    private async Task RunTurnAsync(CancellationToken ct)
    {
        var tools       = ResearchToolset.GetTools(_webSearch, _fetchPage);
        var systemMsg   = BuildSystemPrompt();
        var fullHistory = PrependSystem(systemMsg);

        string fullText      = "";
        var    toolCallsNative = new List<ToolCall>();

        // ── Stream from model ───────────────────────────────────────────────
        await foreach (var token in _runtime.StreamCompletionAsync(
            Model,
            fullHistory,
            tools,
            temperature: 0.2,
            maxTokens:   4096,
            onToolCall: tc => toolCallsNative.Add(tc),
            ct: ct))
        {
            fullText += token;
            OnToken?.Invoke(token);
        }

        // ── Path 1: Native tool calling ─────────────────────────────────────
        if (toolCallsNative.Count > 0)
        {
            await RunNativeToolLoop(fullText, toolCallsNative, tools, systemMsg, ct);
            return;
        }

        // ── Path 2: ReAct fallback ──────────────────────────────────────────
        var reactCalls = ResearchToolset.ParseReActCalls(fullText);
        if (reactCalls.Count > 0)
        {
            await RunReActLoop(fullText, reactCalls, tools, systemMsg, ct);
            return;
        }

        // ── No tool calls — plain response ──────────────────────────────────
        _history.Add(new AgentMessage
        {
            Role    = MessageRole.Assistant,
            Content = fullText,
            Status  = MessageStatus.Complete,
        });
        OnTurnComplete?.Invoke(fullText);
    }

    // ── Native tool loop ──────────────────────────────────────────────────────

    private async Task RunNativeToolLoop(
        string           assistantText,
        List<ToolCall>   toolCalls,
        List<ToolDefinition> tools,
        string           systemMsg,
        CancellationToken ct)
    {
        const int MaxIterations = 6;
        int iterations = 0;

        string lastText = assistantText;

        while (toolCalls.Count > 0 && iterations++ < MaxIterations)
        {
            // Record the assistant turn (may have no text content if it jumped straight to tools)
            _history.Add(new AgentMessage
            {
                Role      = MessageRole.Assistant,
                Content   = lastText,
                ToolCalls = new List<ToolCall>(toolCalls),
                Status    = MessageStatus.Complete,
            });

            // Execute each tool call and inject results
            foreach (var tc in toolCalls)
            {
                OnToolStart?.Invoke(tc.Name, ArgsToJson(tc.Arguments));

                var result = await ExecuteTool(tc, tools, ct);
                tc.Result = result;
                tc.Status = ToolCallStatus.Complete;

                OnToolComplete?.Invoke(tc.Name, Truncate(result, 200));

                _history.Add(new AgentMessage
                {
                    Role       = MessageRole.Tool,
                    Content    = result,
                    ToolCallId = tc.Id,
                    Status     = MessageStatus.Complete,
                });
            }

            // Continue generation — model now has tool results
            lastText  = "";
            toolCalls = [];

            await foreach (var token in _runtime.StreamCompletionAsync(
                Model,
                PrependSystem(systemMsg),
                tools,
                temperature: 0.2,
                maxTokens:   4096,
                onToolCall: tc => toolCalls.Add(tc),
                ct: ct))
            {
                lastText += token;
                OnToken?.Invoke(token);
            }
        }

        // Final text — record and signal complete
        _history.Add(new AgentMessage
        {
            Role    = MessageRole.Assistant,
            Content = lastText,
            Status  = MessageStatus.Complete,
        });
        OnTurnComplete?.Invoke(lastText);
    }

    // ── ReAct loop ────────────────────────────────────────────────────────────

    private async Task RunReActLoop(
        string                 initialText,
        List<ToolCallRequest>  reactCalls,
        List<ToolDefinition>   tools,
        string                 systemMsg,
        CancellationToken      ct)
    {
        const int MaxIterations = 6;
        int iterations = 0;

        string  pendingText = initialText;
        var     pendingCalls = reactCalls;

        while (pendingCalls.Count > 0 && iterations++ < MaxIterations)
        {
            // Strip the tool_call XML from the visible text before storing
            var cleanText = pendingText;
            foreach (var call in pendingCalls)
                cleanText = cleanText.Replace(call.FullMatch, "").Trim();

            _history.Add(new AgentMessage
            {
                Role    = MessageRole.Assistant,
                Content = cleanText,
                Status  = MessageStatus.Complete,
            });

            // Execute each ReAct call and inject results
            var resultParts = new System.Text.StringBuilder();
            foreach (var call in pendingCalls)
            {
                OnToolStart?.Invoke(call.Name, ArgsToJson(call.Args));

                var tc = new ToolCall { Name = call.Name, Arguments = call.Args };
                var result = await ExecuteTool(tc, tools, ct);

                OnToolComplete?.Invoke(call.Name, Truncate(result, 200));

                resultParts.AppendLine($"<tool_result name=\"{call.Name}\">");
                resultParts.AppendLine(result);
                resultParts.AppendLine("</tool_result>");
            }

            // Inject results as a user message (ReAct pattern)
            _history.Add(new AgentMessage
            {
                Role    = MessageRole.User,
                Content = resultParts.ToString(),
                Status  = MessageStatus.Complete,
            });

            // Continue — model writes the next segment
            pendingText  = "";
            pendingCalls = [];

            await foreach (var token in _runtime.StreamCompletionAsync(
                Model,
                PrependSystem(systemMsg),
                null,           // no tools in ReAct continuation — we parse text
                temperature: 0.2,
                maxTokens:   4096,
                ct: ct))
            {
                pendingText += token;
                OnToken?.Invoke(token);
            }

            pendingCalls = ResearchToolset.ParseReActCalls(pendingText);
        }

        // Final text
        var finalClean = pendingText;
        foreach (var call in ResearchToolset.ParseReActCalls(finalClean))
            finalClean = finalClean.Replace(call.FullMatch, "").Trim();

        _history.Add(new AgentMessage
        {
            Role    = MessageRole.Assistant,
            Content = finalClean,
            Status  = MessageStatus.Complete,
        });
        OnTurnComplete?.Invoke(finalClean);
    }

    // ── Tool execution ────────────────────────────────────────────────────────

    private static async Task<string> ExecuteTool(
        ToolCall             tc,
        List<ToolDefinition> tools,
        CancellationToken    ct)
    {
        var def = tools.FirstOrDefault(t =>
            t.Name.Equals(tc.Name, StringComparison.OrdinalIgnoreCase));

        if (def?.Handler is null)
            return $"Unknown tool: {tc.Name}";

        try
        {
            return await def.Handler(tc.Arguments, ct);
        }
        catch (Exception ex)
        {
            return $"Tool error ({tc.Name}): {ex.Message}";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildSystemPrompt()
    {
        var known = ResearchToolset.KnownNativeToolSupport(Model);
        return known
            ? ResearchToolset.BaseSystemPrompt
            : ResearchToolset.BaseSystemPrompt + "\n\n" + ResearchToolset.ReActSystemPrompt;
    }

    private List<AgentMessage> PrependSystem(string systemMsg)
    {
        var list = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = systemMsg }
        };
        list.AddRange(_history);
        return list;
    }

    private static string ArgsToJson(Dictionary<string, object?> args)
    {
        try { return System.Text.Json.JsonSerializer.Serialize(args); }
        catch { return "{}"; }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
