// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama.Native;
using LLama.Sampling;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Role-aware native runtime surface for Phase 3 paths that need ModelDepot +
/// RuntimeOrchestrator semantics. This is intentionally separate from IModelRuntime:
/// model-name dispatch still belongs to Ollama/llama.cpp/main chat, while native
/// role execution resolves Boss/Worker/Researcher/Reviewer bindings locally.
/// </summary>
public interface IRoleRuntime
{
    string RuntimeName { get; }

    IAsyncEnumerable<string> StreamRoleCompletionAsync(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        CancellationToken ct = default);

    RuntimeHealth GetHealth(RuntimeRole? role = null);

    RuntimeStats GetStats(RuntimeRole? role = null);
}

/// <summary>
/// Native Runtime Phase 3 role facade. It keeps the existing safe ownership chain:
/// ModelDepot resolves a role binding, RuntimeOrchestrator loads the base model via
/// SessionManager, and AdapterManager returns a reference-counted role conversation.
/// </summary>
public sealed class NativeRoleRuntime : IRoleRuntime, IAsyncDisposable
{
    private static readonly JsonSerializerOptions _compactJson = new() { WriteIndented = false };
    private static readonly string[] _antiPrompts =
    [
        "<|user|>",
        "<|end|>",
        "<|im_end|>",
        "[/INST]",
        "\nUser:",
        "\nHuman:",
    ];

    private readonly ModelDepot _depot;
    private readonly RuntimeOptions _options;
    private readonly LLamaSharpRuntime _runtime;
    private readonly RuntimeOrchestrator _orchestrator;
    private readonly object _telemetryGate = new();
    private readonly Dictionary<RuntimeRole, RuntimeRoleBinding> _lastBindings = new();
    private readonly Dictionary<RuntimeRole, RuntimeStats> _lastStatsByRole = new();
    private bool _disposed;

    public NativeRoleRuntime(ModelDepot depot, RuntimeOptions? options = null)
        : this(depot, new LLamaSharpRuntime(), options, disposeRuntime: true)
    {
    }

    internal NativeRoleRuntime(
        ModelDepot depot,
        LLamaSharpRuntime runtime,
        RuntimeOptions? options = null,
        bool disposeRuntime = false)
    {
        _depot = depot ?? throw new ArgumentNullException(nameof(depot));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new RuntimeOptions();
        _orchestrator = new RuntimeOrchestrator(_runtime, disposeRuntime);
    }

    public string RuntimeName => "NativeRoleRuntime";

    public async IAsyncEnumerable<string> StreamRoleCompletionAsync(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(history);

        var binding = _depot.ResolveRole(role)
            ?? throw new InvalidOperationException($"No base GGUF resolved for runtime role {role}.");

        using var tracked = await _orchestrator
            .GetConversationForRoleAsync(_depot, role, _options, ct)
            .ConfigureAwait(false);

        lock (_telemetryGate)
            _lastBindings[role] = binding;

        var conversation = tracked.Inner;
        var executor = conversation.Executor;
        using var sampler = new DefaultSamplingPipeline
        {
            Temperature = (float)Math.Clamp(temperature, 0.0, 2.0),
        };

        var prompt = BuildPrompt(history, tools);
        var started = DateTime.UtcNow;
        var firstTokenAt = default(DateTime);
        var outputBuilder = new StringBuilder();

        conversation.Prompt(prompt, addBos: true, special: true);
        await executor.Infer(ct).ConfigureAwait(false);

        for (var i = 0; i < Math.Max(0, maxTokens); i++)
        {
            ct.ThrowIfCancellationRequested();

            var sampleIndex = conversation.GetSampleIndex(0);
            var token = sampler.Sample(executor.Context.NativeHandle, sampleIndex);
            if (IsStopToken(executor.Model.Vocab, token))
                break;

            sampler.Accept(token);

            var text = executor.Model.Vocab.LLamaTokenToString(token, isSpecialToken: false) ?? "";
            if (!string.IsNullOrEmpty(text))
            {
                if (firstTokenAt == default)
                    firstTokenAt = DateTime.UtcNow;

                outputBuilder.Append(text);
                yield return text;

                if (EndsWithAntiPrompt(outputBuilder))
                    break;
            }

            conversation.Prompt(token);
            await executor.Infer(ct).ConfigureAwait(false);
        }

        var elapsed = DateTime.UtcNow - started;
        var outputText = outputBuilder.ToString();
        var completionTokens = string.IsNullOrEmpty(outputText)
            ? 0
            : ContextManager.EstimateTokens(outputText);

        var stats = new RuntimeStats(
            RuntimeName,
            ActiveModel: $"{role}:{binding.BaseModel.DisplayName}",
            TokensPerSecond: elapsed.TotalSeconds > 0 && completionTokens > 0
                ? completionTokens / elapsed.TotalSeconds
                : null,
            LastTimeToFirstToken: firstTokenAt == default ? null : firstTokenAt - started,
            EstimatedVramBytes: binding.BaseModel.SizeBytes);

        lock (_telemetryGate)
            _lastStatsByRole[role] = stats;

        onUsage?.Invoke(ContextManager.EstimateTokens(prompt), completionTokens);

        if (onToolCall is not null)
            foreach (var tc in ToolCallTextParser.Parse(outputText))
                onToolCall(tc);
    }

    public RuntimeHealth GetHealth(RuntimeRole? role = null)
    {
        ThrowIfDisposed();

        var health = _runtime.GetHealth();
        RuntimeRoleBinding? binding = null;
        if (role is { } r)
        {
            lock (_telemetryGate)
                _lastBindings.TryGetValue(r, out binding);
        }

        return health with
        {
            RuntimeName = RuntimeName,
            ActiveModel = binding is null
                ? health.ActiveModel
                : $"{binding.Role}:{binding.BaseModel.DisplayName}",
        };
    }

    public RuntimeStats GetStats(RuntimeRole? role = null)
    {
        ThrowIfDisposed();

        if (role is { } r)
        {
            lock (_telemetryGate)
                if (_lastStatsByRole.TryGetValue(r, out var stats))
                    return stats;
        }

        var runtimeStats = _runtime.GetStats();
        return runtimeStats with { RuntimeName = RuntimeName };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _orchestrator.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NativeRoleRuntime));
    }

    private static string BuildPrompt(IEnumerable<AgentMessage> history, IReadOnlyList<object>? tools)
    {
        var messages = history.ToList();

        if (tools is { Count: > 0 })
        {
            var toolJson = JsonSerializer.Serialize(tools, _compactJson);
            var toolBlock = $"\n\nAvailable tools (call as JSON):\n{toolJson}";
            var sysIdx = messages.FindIndex(m => m.Role == MessageRole.System);
            if (sysIdx >= 0)
                messages[sysIdx] = messages[sysIdx].WithContent(messages[sysIdx].Content + toolBlock);
            else
                messages.Insert(0, new AgentMessage
                    { Role = MessageRole.System, Content = toolBlock });
        }

        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.Append("<|im_start|>").Append(ToRoleString(msg.Role)).Append('\n');
            sb.Append(msg.Content ?? "");
            sb.AppendLine("<|im_end|>");
        }
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    private static string ToRoleString(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Tool => "tool",
        _ => "assistant",
    };

    private static bool IsStopToken(SafeLlamaModelHandle.Vocabulary vocab, LLamaToken token) =>
        (vocab.EOS is { } eos && token.Equals(eos)) ||
        (vocab.EOT is { } eot && token.Equals(eot));

    private static bool EndsWithAntiPrompt(StringBuilder output)
    {
        if (output.Length == 0)
            return false;

        var text = output.ToString();
        return _antiPrompts.Any(marker =>
            text.EndsWith(marker, StringComparison.OrdinalIgnoreCase));
    }
}
