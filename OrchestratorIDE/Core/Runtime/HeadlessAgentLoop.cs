// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

public sealed record HeadlessAgentLimits(
    int MaxSteps = 12,
    int MaxTokensPerStep = 4096,
    TimeSpan? Timeout = null)
{
    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes(10);
}

public sealed record HeadlessTool(
    string Name,
    object Schema,
    Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task<string>> ExecuteAsync);

public sealed record HeadlessAgentEvent(
    string Type,
    int Step,
    string Message,
    DateTimeOffset Timestamp);

public sealed record HeadlessAgentResult(
    string Output,
    int Steps,
    int PromptTokens,
    int CompletionTokens,
    string TraceDigest,
    IReadOnlyList<HeadlessAgentEvent> Events);

/// <summary>
/// UI-free native agent loop used by Warbands and campaign workers. The host owns policy by
/// choosing the supplied tools; the loop never prompts, auto-approves, or invents shell access.
/// </summary>
public sealed class HeadlessAgentLoop(IRoleRuntime runtime)
{
    public async Task<HeadlessAgentResult> ExecuteAsync(
        RuntimeRole role,
        IReadOnlyList<AgentMessage> initialMessages,
        IReadOnlyList<HeadlessTool> tools,
        HeadlessAgentLimits? limits = null,
        Action<HeadlessAgentEvent>? onEvent = null,
        CancellationToken ct = default)
    {
        limits ??= new HeadlessAgentLimits();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limits.EffectiveTimeout);

        var messages = initialMessages.ToList();
        var events = new List<HeadlessAgentEvent>();
        var schemas = tools.Select(t => t.Schema).ToArray();
        var byName = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var trace = new StringBuilder();
        var output = "";
        var promptTokens = 0;
        var completionTokens = 0;
        var steps = 0;

        void Emit(string type, int step, string message)
        {
            var ev = new HeadlessAgentEvent(type, step, message, DateTimeOffset.UtcNow);
            events.Add(ev);
            onEvent?.Invoke(ev);
            trace.Append(type).Append('|').Append(step).Append('|').Append(message).Append('\n');
        }

        for (var step = 1; step <= Math.Max(1, limits.MaxSteps); step++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            steps = step;
            Emit("model_started", step, role.ToString());

            var pending = new List<ToolCall>();
            var content = new StringBuilder();
            await foreach (var token in runtime.StreamRoleCompletionAsync(
                role,
                messages,
                schemas.Length == 0 ? null : schemas,
                maxTokens: limits.MaxTokensPerStep,
                onToolCall: pending.Add,
                onUsage: (p, c) => { promptTokens += p; completionTokens += c; },
                ct: timeout.Token).ConfigureAwait(false))
            {
                content.Append(token);
            }

            output = content.ToString().Trim();
            if (pending.Count == 0 && output.Length > 0)
                pending.AddRange(ToolCallTextParser.Parse(output));

            var assistant = new AgentMessage
            {
                Role = MessageRole.Assistant,
                Content = output,
                ToolCalls = pending,
                Status = MessageStatus.Complete,
            };
            messages.Add(assistant);
            Emit("model_completed", step, $"chars={output.Length}; tools={pending.Count}");

            if (pending.Count == 0)
                break;

            foreach (var call in pending)
            {
                timeout.Token.ThrowIfCancellationRequested();
                string toolResult;
                if (!byName.TryGetValue(call.Name, out var tool))
                {
                    toolResult = $"[POLICY BLOCKED] Tool '{call.Name}' is not available on this Warband.";
                }
                else
                {
                    Emit("tool_started", step, call.Name);
                    toolResult = await tool.ExecuteAsync(call.Arguments, timeout.Token).ConfigureAwait(false);
                }

                messages.Add(new AgentMessage
                {
                    Role = call.IsTextFormat ? MessageRole.User : MessageRole.Tool,
                    Content = call.IsTextFormat
                        ? $"Tool result for {call.Name}:\n{toolResult}"
                        : toolResult,
                    ToolCallId = call.Id,
                    Status = MessageStatus.Complete,
                });
                Emit("tool_completed", step, $"{call.Name}; chars={toolResult.Length}");
            }
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace.ToString())))
            .ToLowerInvariant();
        return new HeadlessAgentResult(output, steps, promptTokens, completionTokens, digest, events);
    }

    public static object BuildToolSchema(string name, string description,
        IReadOnlyDictionary<string, object> properties, IReadOnlyList<string>? required = null) =>
        new
        {
            type = "function",
            function = new
            {
                name,
                description,
                parameters = new { type = "object", properties, required = required ?? [] },
            },
        };
}
