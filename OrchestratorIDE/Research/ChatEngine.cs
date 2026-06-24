// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Research;

/// <summary>
/// Conversation engine — originally built for the research chat tab, generalized so any
/// chat surface (research chat, a future general/uncensored chat panel, etc.) can reuse the
/// same proven multi-turn/streaming/tool-loop machinery instead of forking a parallel engine.
///
/// Supports two tool-execution paths:
///  1. Native — model returns tool_calls via the API (qwen, llama3, hermes, nemotron…)
///  2. ReAct  — model embeds XML tool_call blocks in plain text (fallback for all models)
///
/// Defaults reproduce the original research-chat-only behavior exactly (research system
/// prompt, WebSearchTool/FetchPageTool, temperature 0.2) so the existing call site
/// (`new ChatEngine(runtime, model)`) is completely unaffected. A caller that wants a plain,
/// unfiltered chat surface passes `systemPrompt: "", tools: []` explicitly -- note this is
/// "" (empty string), NOT null: null means "use the research defaults," only an explicit ""
/// means "inject no system message at all" (see ResolveSystemPrompt/PrependSystem below).
/// This engine itself never injects a system prompt or a toolset beyond what the caller asks
/// for; the research-chat defaults are a constructor default, not something hardcoded into
/// the turn logic below.
/// </summary>
public class ChatEngine
{
    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly IModelRuntime _runtime;
    private readonly List<ToolDefinition> _tools;

    // ── Conversation history ──────────────────────────────────────────────────
    private readonly List<AgentMessage> _history = [];

    // ── Active model (can be changed between turns) ───────────────────────────
    public string Model { get; set; }

    // ── Sampling/prompt settings — mutable, not constructor-only ──────────────
    // A caller exposing live UI controls for these (Open chat mode's system-prompt
    // textbox, temperature/top-p numeric fields) needs edits between sends to actually
    // take effect on the SAME engine instance -- recreating the engine per-send to pick
    // up new values would also wipe conversation history, which is a worse regression
    // than these being mutable. Tools intentionally stay constructor-only: no UI control
    // changes them mid-conversation, and a tool set swap mid-history is a much bigger
    // semantic change than a sampling-parameter tweak.
    public string? SystemPrompt { get; set; }
    public double  Temperature  { get; set; }
    public double? TopP         { get; set; }

    /// <summary>
    /// When true, prepends the current local date/time to the system prompt every turn, so
    /// the model has accurate grounding instead of (correctly) saying it has no way to know
    /// the date. Defaults to false -- this must NOT change Research mode's existing
    /// byte-identical-with-the-original-panel guarantee (see ChatEngineTests), so it's an
    /// explicit opt-in a caller turns on, not a default. Pure factual grounding, not content
    /// shaping, so it's fine to apply even in Open mode (which otherwise injects nothing) --
    /// this isn't "censoring" anything, it's the same kind of baseline fact most chat
    /// products give a model for free.
    /// </summary>
    public bool IncludeDateTimeContext { get; set; }

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

    /// <summary>Fired once per model call with (promptTokens, completionTokens) from the
    /// runtime's usage callback -- lets a caller track/display context-window consumption.
    /// Not cumulative across turns; the caller sums across calls if it wants a running
    /// total.</summary>
    public event Action<int, int>? OnUsage;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="systemPrompt">
    /// Fixed system prompt to prepend every turn. Pass null (the default) to reproduce the
    /// original research-chat behavior (ResearchToolset's base prompt, plus a ReAct-fallback
    /// suffix for models without known native tool-call support). Pass an explicit string
    /// (including "") for a caller-controlled prompt with no research framing at all.
    /// </param>
    /// <param name="tools">
    /// Tools available to the model. Pass null (the default) to reproduce the original
    /// research-chat toolset (WebSearchTool/FetchPageTool). Pass an empty list for a plain
    /// chat surface with no tools and no ReAct parsing (see RunTurnAsync) -- not the same as
    /// null, which still means "use the research defaults."
    /// </param>
    /// <param name="temperature">Defaults to 0.2, matching the original hardcoded value.</param>
    /// <param name="topP">Null (the runtime's own default) unless the caller overrides it.</param>
    public ChatEngine(
        IModelRuntime runtime, string model,
        string? systemPrompt = null, List<ToolDefinition>? tools = null,
        double temperature = 0.2, double? topP = null)
    {
        _runtime     = runtime;
        Model        = model;
        SystemPrompt = systemPrompt;
        _tools       = tools ?? ResearchToolset.GetTools(new WebSearchTool(), new FetchPageTool());
        Temperature  = temperature;
        TopP         = topP;
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
        var tools       = _tools;
        var systemMsg   = ResolveSystemPrompt();
        var fullHistory = PrependSystem(systemMsg);

        string fullText      = "";
        var    toolCallsNative = new List<ToolCall>();

        // ── Stream from model ───────────────────────────────────────────────
        await foreach (var token in _runtime.StreamCompletionAsync(
            Model,
            fullHistory,
            ToWireSchema(tools),
            temperature: Temperature,
            topP:        TopP,
            maxTokens:   4096,
            onToolCall: tc => toolCallsNative.Add(tc),
            onUsage: (p, c) => OnUsage?.Invoke(p, c),
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
        // Only attempted when tools exist -- a no-tools chat mode (general/uncensored chat,
        // tools: []) has no ReAct framing in its system prompt either, so parsing for
        // tool_call-shaped XML here would only ever find false positives and inject a
        // confusing "Unknown tool" result for ordinary text the model never intended as a
        // tool call.
        var reactCalls = tools.Count > 0 ? ResearchToolset.ParseReActCalls(fullText) : [];
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
        string?          systemMsg,
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
                ToWireSchema(tools),
                temperature: Temperature,
                topP:        TopP,
                maxTokens:   4096,
                onToolCall: tc => toolCalls.Add(tc),
                onUsage: (p, c) => OnUsage?.Invoke(p, c),
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
        string?                systemMsg,
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
                temperature: Temperature,
                topP:        TopP,
                maxTokens:   4096,
                onUsage: (p, c) => OnUsage?.Invoke(p, c),
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

    /// <summary>
    /// Resolves the actual system prompt for this turn: the caller's explicit value
    /// (SystemPrompt) if one was given, including an explicit "" meaning
    /// "no system prompt at all" (see PrependSystem); otherwise the original research-chat
    /// default. Null/"" is NOT the same state as the research default -- this is what lets
    /// a general/uncensored chat mode opt out of any injected prompt entirely rather than
    /// getting an empty-but-still-present system message. IncludeDateTimeContext, when set,
    /// prepends current date/time on top of whichever of those this resolves to -- including
    /// turning an otherwise-empty "" SystemPrompt into a non-empty system message containing
    /// just the date/time, since that's specifically the case (Open mode, no other
    /// instructions) where a model has zero way to know the date otherwise.
    /// </summary>
    /// <summary>
    /// Maps raw ToolDefinitions to the OpenAI/Ollama wire schema (type:"function", nested
    /// function.{name,description,parameters}) via ToOllamaSchema(). Without this, the runtime
    /// serializes ToolDefinition's own public properties directly -- a flat shape with no "type"
    /// field that Ollama's /v1/chat/completions tolerates silently (so this went unnoticed) but
    /// llama.cpp's stricter OpenAI-compat server rejects outright with a 500 "Missing tool type"
    /// error. AgentLoop avoids this because it runs tools through SchemaGenerator.GenerateForRole
    /// first, which calls a per-model-calibrated equivalent; ChatEngine doesn't need that heavier
    /// per-model format-learning machinery, just the plain correct shape.
    /// </summary>
    private static List<object> ToWireSchema(List<ToolDefinition> tools) =>
        tools.Select(t => t.ToOllamaSchema()).ToList();

    private string? ResolveSystemPrompt()
    {
        var basePrompt = SystemPrompt ?? BuildSystemPrompt();
        if (!IncludeDateTimeContext) return basePrompt;

        var dateTimeLine = $"Current date and time: {DateTime.Now:dddd, yyyy-MM-dd HH:mm} ({TimeZoneInfo.Local.StandardName}).";
        return string.IsNullOrEmpty(basePrompt) ? dateTimeLine : $"{dateTimeLine}\n\n{basePrompt}";
    }

    private string BuildSystemPrompt()
    {
        var known = ResearchToolset.KnownNativeToolSupport(Model);
        return known
            ? ResearchToolset.BaseSystemPrompt
            : ResearchToolset.BaseSystemPrompt + "\n\n" + ResearchToolset.ReActSystemPrompt;
    }

    /// <summary>
    /// Prepends a system message only when one is actually present -- a null/empty
    /// systemMsg means "no system prompt," not "a system message with empty content."
    /// This is the literal mechanism behind "uncensored chat never injects anything":
    /// when ResolveSystemPrompt() returns "" (an explicit caller choice), no system
    /// message reaches the model at all.
    /// </summary>
    private List<AgentMessage> PrependSystem(string? systemMsg)
    {
        var list = new List<AgentMessage>();
        if (!string.IsNullOrEmpty(systemMsg))
            list.Add(new() { Role = MessageRole.System, Content = systemMsg });
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
