// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
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
public sealed class NativeRoleRuntime : IRoleRuntime, IRoleRuntimeDiagnostics, IContextLengthProvider, IAsyncDisposable
{
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
    private readonly IReadOnlyDictionary<RuntimeRole, RuntimeRoleBinding> _roleBindings;
    private readonly LLamaSharpRuntime _runtime;
    private readonly RuntimeOrchestrator _orchestrator;
    private readonly object _telemetryGate = new();
    private readonly Dictionary<RuntimeRole, RuntimeHealth> _lastHealthByRole = new();
    private readonly Dictionary<RuntimeRole, RuntimeStats> _lastStatsByRole = new();
    private readonly Dictionary<RuntimeRole, string?> _lastPromptPathByRole = new();
    private bool _disposed;

    public NativeRoleRuntime(
        ModelDepot depot,
        RuntimeOptions? options = null,
        IOrcScheduler? scheduler = null,
        Func<VramBudget>? budgetProvider = null,
        IReadOnlyDictionary<RuntimeRole, RuntimeRoleBinding>? roleBindings = null)
        : this(
            depot,
            new LLamaSharpRuntime(),
            options,
            disposeRuntime: true,
            scheduler,
            budgetProvider,
            roleBindings)
    {
    }

    internal NativeRoleRuntime(
        ModelDepot depot,
        LLamaSharpRuntime runtime,
        RuntimeOptions? options = null,
        bool disposeRuntime = false,
        IOrcScheduler? scheduler = null,
        Func<VramBudget>? budgetProvider = null,
        IReadOnlyDictionary<RuntimeRole, RuntimeRoleBinding>? roleBindings = null)
    {
        _depot = depot ?? throw new ArgumentNullException(nameof(depot));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new RuntimeOptions();
        _roleBindings = roleBindings ?? new Dictionary<RuntimeRole, RuntimeRoleBinding>();
        _orchestrator = new RuntimeOrchestrator(_runtime, disposeRuntime, scheduler, budgetProvider);
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

        var binding = (_roleBindings.TryGetValue(role, out var configured) ? configured : _depot.ResolveRole(role))
            ?? throw new InvalidOperationException($"No base GGUF resolved for runtime role {role}.");

        await using var enumerator = StreamRoleCompletionCoreAsync(
                role,
                binding,
                history,
                tools,
                temperature,
                maxTokens,
                onToolCall,
                onUsage,
                ct)
            .GetAsyncEnumerator(ct);

        while (true)
        {
            string token;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    yield break;
                token = enumerator.Current;
            }

            catch (Exception ex)
            {
                RecordFailure(role, binding, ex.Message);
                throw;
            }

            yield return token;
        }
    }

    public RuntimeHealth GetHealth(RuntimeRole? role = null)
    {
        ThrowIfDisposed();

        if (role is { } r)
        {
            lock (_telemetryGate)
                if (_lastHealthByRole.TryGetValue(r, out var lastHealth))
                    return lastHealth;
        }

        return _runtime.GetHealth() with { RuntimeName = RuntimeName };
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

    public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<int?>(_options.ContextLength);

    public string? GetLastPromptPath(RuntimeRole role)
    {
        ThrowIfDisposed();

        lock (_telemetryGate)
            if (_lastPromptPathByRole.TryGetValue(role, out var promptPath))
                return promptPath;

        return _runtime.GetLastPromptPath();
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

    private async IAsyncEnumerable<string> StreamRoleCompletionCoreAsync(
        RuntimeRole role,
        RuntimeRoleBinding binding,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools,
        double temperature,
        int maxTokens,
        Action<ToolCall>? onToolCall,
        Action<int, int>? onUsage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var tracked = await _orchestrator
            .GetConversationForBindingAsync(binding, _options, ct)
            .ConfigureAwait(false);

        var conversation = tracked.Inner;
        var executor = conversation.Executor;
        using var sampler = new DefaultSamplingPipeline
        {
            Temperature = (float)Math.Clamp(temperature, 0.0, 2.0),
        };

        var prompt = _runtime.BuildPromptForLoadedModel(history, tools);
        var promptPath = _runtime.GetLastPromptPath() ?? "Unknown";
        var started = DateTime.UtcNow;
        var firstTokenAt = default(DateTime);
        var outputBuilder = new StringBuilder();

        conversation.Prompt(prompt, addBos: true, special: true);
        await InferUntilReadyAsync(conversation, ct).ConfigureAwait(false);

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
            await InferUntilReadyAsync(conversation, ct).ConfigureAwait(false);
        }

        var elapsed = DateTime.UtcNow - started;
        var outputText = outputBuilder.ToString();
        var completionTokens = string.IsNullOrEmpty(outputText)
            ? 0
            : ContextManager.EstimateTokens(outputText);

        var stats = new RuntimeStats(
            RuntimeName,
            ActiveModel: FormatActiveModel(binding),
            TokensPerSecond: elapsed.TotalSeconds > 0 && completionTokens > 0
                ? completionTokens / elapsed.TotalSeconds
                : null,
            LastTimeToFirstToken: firstTokenAt == default ? null : firstTokenAt - started,
            EstimatedVramBytes: EstimateBindingBytes(binding));

        var health = _runtime.GetHealth() with
        {
            RuntimeName = RuntimeName,
            ActiveModel = FormatActiveModel(binding),
            Message = binding.Adapter is null
                ? null
                : $"Adapter attached: {binding.Adapter.DisplayName}",
        };

        lock (_telemetryGate)
        {
            _lastHealthByRole[role] = health;
            _lastStatsByRole[role] = stats;
            _lastPromptPathByRole[role] = promptPath;
        }

        onUsage?.Invoke(ContextManager.EstimateTokens(prompt), completionTokens);

        if (onToolCall is not null)
            foreach (var tc in ToolCallTextParser.Parse(outputText))
                onToolCall(tc);
    }

    private static async Task InferUntilReadyAsync(LLama.Batched.Conversation conversation, CancellationToken ct)
    {
        var passes = 0;
        var noKvSlotRetries = 0;
        while (conversation.RequiresInference)
        {
            var result = await conversation.Executor.Infer(ct).ConfigureAwait(false);
            if (result == DecodeResult.NoKvSlot)
            {
                // LLamaSharp documents this as retryable (BatchedExecutor.Infer requeues the
                // batch internally rather than consuming it): "there is not enough memory for
                // inference, try disposing some conversation threads and running inference
                // again." A few cells may free up as other conversations finish disposing --
                // give that a bounded chance before failing hard. This is defense-in-depth only;
                // it cannot rescue a conversation whose own prompt alone overflows the KV pool
                // (see docs/CONTEXT_FABRIC_TEST_HARNESS.md §7).
                if (++noKvSlotRetries > 8)
                    throw new InvalidOperationException(
                        $"Native inference failed while draining a prompt batch: {result} (gave up after {noKvSlotRetries} retries).");
                await Task.Delay(TimeSpan.FromMilliseconds(50 * noKvSlotRetries), ct).ConfigureAwait(false);
                continue;
            }
            if (result != DecodeResult.Ok)
                throw new InvalidOperationException($"Native inference failed while draining a prompt batch: {result}.");
            if (++passes > 1024)
                throw new InvalidOperationException("Native inference did not drain the pending prompt batch.");
        }
    }

    private static string FormatActiveModel(RuntimeRoleBinding binding) =>
        binding.Adapter is null
            ? $"{binding.Role}:{binding.BaseModel.DisplayName}"
            : $"{binding.Role}:{binding.BaseModel.DisplayName} + {binding.Adapter.DisplayName}";

    private static long? EstimateBindingBytes(RuntimeRoleBinding binding)
    {
        var baseBytes = binding.BaseModel.SizeBytes ?? 0;
        var adapterBytes = binding.Adapter?.SizeBytes ?? 0;
        var total = baseBytes + adapterBytes;
        return total > 0 ? total : null;
    }

    private void RecordFailure(RuntimeRole role, RuntimeRoleBinding binding, string? runtimeMessage)
    {
        lock (_telemetryGate)
        {
            var message = runtimeMessage;
            if (message is null && _lastHealthByRole.TryGetValue(role, out var existing))
                message = existing.Message;

            _lastHealthByRole[role] = new RuntimeHealth(
                IsAvailable: false,
                RuntimeName: RuntimeName,
                ActiveModel: FormatActiveModel(binding),
                Message: message);

            _lastStatsByRole[role] = new RuntimeStats(
                RuntimeName,
                ActiveModel: FormatActiveModel(binding),
                EstimatedVramBytes: EstimateBindingBytes(binding));
            _lastPromptPathByRole[role] = _runtime.GetLastPromptPath();
        }
    }
}
