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

    /// <summary>
    /// Returns the loaded model's exact token count for the fully rendered prompt when the
    /// runtime can provide it. Runtimes without a native tokenizer return <see langword="null"/>
    /// so callers can retain their conservative fallback estimate.
    /// </summary>
    int? CountPromptTokens(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null) => null;

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

    /// <summary>
    /// Native Runtime v2.0 Phase C (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3) — point-in-time VRAM
    /// admission state, if this runtime tracks one. Default implementation returns null: fakes,
    /// scripted test runtimes, and the Ollama-backed fallback path have no admission concept of
    /// their own, and null is the honest answer for them, not a workaround.
    /// </summary>
    RuntimeReservationSnapshot? GetReservationSnapshot() => null;

    /// <summary>
    /// Native Runtime v2.0 Phase C (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3) — point-in-time adapter
    /// residency, if this runtime tracks one. Default implementation returns empty for the same
    /// reason as <see cref="GetReservationSnapshot"/>.
    /// </summary>
    IReadOnlyList<AdapterRoleResidency> GetResidencySnapshot() => [];
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

    /// <param name="allowUnbudgetedExecution">
    /// Native Runtime v2.0 Phase A (docs/NATIVE_RUNTIME_V2_SPEC.md §1.3): forwarded verbatim to
    /// <see cref="RuntimeOrchestrator"/>. Default <see langword="false"/> means admission fails
    /// closed when <paramref name="scheduler"/>/<paramref name="budgetProvider"/> are null,
    /// rather than loading unadmitted. Only pass <see langword="true"/> as a deliberate,
    /// caller-logged opt-out.
    /// </param>
    public NativeRoleRuntime(
        ModelDepot depot,
        RuntimeOptions? options = null,
        IOrcScheduler? scheduler = null,
        Func<VramBudget?>? budgetProvider = null,
        IReadOnlyDictionary<RuntimeRole, RuntimeRoleBinding>? roleBindings = null,
        bool allowUnbudgetedExecution = false)
        : this(
            depot,
            new LLamaSharpRuntime(),
            options,
            disposeRuntime: true,
            scheduler,
            budgetProvider,
            roleBindings,
            allowUnbudgetedExecution)
    {
    }

    internal NativeRoleRuntime(
        ModelDepot depot,
        LLamaSharpRuntime runtime,
        RuntimeOptions? options = null,
        bool disposeRuntime = false,
        IOrcScheduler? scheduler = null,
        Func<VramBudget?>? budgetProvider = null,
        IReadOnlyDictionary<RuntimeRole, RuntimeRoleBinding>? roleBindings = null,
        bool allowUnbudgetedExecution = false)
    {
        _depot = depot ?? throw new ArgumentNullException(nameof(depot));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new RuntimeOptions();
        _roleBindings = roleBindings ?? new Dictionary<RuntimeRole, RuntimeRoleBinding>();
        _orchestrator = new RuntimeOrchestrator(
            _runtime, disposeRuntime, scheduler, budgetProvider, allowUnbudgetedExecution);
    }

    public string RuntimeName => "NativeRoleRuntime";

    public IAsyncEnumerable<string> StreamRoleCompletionAsync(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        CancellationToken ct = default)
        => StreamRoleCompletionAsync(role, history, tools, temperature, maxTokens, onToolCall, onUsage, modelNameFilter: null, onModelResolved: null, ct);

    /// <summary>
    /// Overload, not an extra parameter on the interface-implementing method above -- C# implicit
    /// interface implementation requires an exact parameter-list match, so <see cref="IRoleRuntime"/>
    /// callers (and every fake implementing it) are unaffected; only <see cref="NativeWithFallbackRuntime"/>,
    /// which knows it's holding a concrete <see cref="NativeRoleRuntime"/>, calls this one.
    /// </summary>
    /// <param name="modelNameFilter">
    /// When set, restricts role resolution to base GGUFs whose
    /// <see cref="RuntimeModelAsset.DisplayName"/> contains this string (via
    /// <see cref="ModelDepot.WithBaseModelFilter"/>) -- e.g. OrcChat's selected model name.
    ///
    /// Found live 2026-07-30: <see cref="NativeWithFallbackRuntime.StreamCompletionAsync"/>
    /// previously ignored the caller's `model` argument entirely for the native path, always
    /// resolving whatever <see cref="ModelDepot.ResolveRole"/> picked for the role -- meaning
    /// switching models in OrcChat's dropdown had NO effect on which native GGUF actually ran.
    /// Filtering by the caller's requested model, and throwing (→ fallback to Ollama, which DOES
    /// respect `model`) when nothing local matches, means native either runs the model the user
    /// actually asked for or steps aside -- rather than silently substituting a different one
    /// picked purely by role. Scope limit: this only applies to the depot-resolution fallback,
    /// not a pre-configured roleBindings entry -- see the body's own comment on that boundary.
    /// </param>
    /// <param name="onModelResolved">
    /// Fires once, synchronously, with the resolved binding's <see cref="RuntimeModelAsset.DisplayName"/>
    /// as soon as it's known -- before generation starts, not after the stream ends. Deliberately
    /// NOT sourced from <see cref="GetHealth"/>/<see cref="GetStats"/> afterward (grok-review
    /// MINOR, 2026-07-30): those read shared per-role state (<c>_lastHealthByRole</c>) that a
    /// concurrent call on the same role can overwrite before this call's own stream finishes,
    /// which would misattribute or silently drop the "model changed" signal.
    /// </param>
    public async IAsyncEnumerable<string> StreamRoleCompletionAsync(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools,
        double temperature,
        int maxTokens,
        Action<ToolCall>? onToolCall,
        Action<int, int>? onUsage,
        string? modelNameFilter,
        Action<string>? onModelResolved,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(history);

        // grok-review MINOR, 2026-07-30: this must match the filter check below exactly --
        // IsNullOrWhiteSpace treats "" as "no filter," so the error message branch needs the
        // same test, not the narrower `is null`, or an empty-but-non-null filter would produce
        // a "no GGUF matches ''" message for a request that never actually filtered anything.
        var hasFilter = !string.IsNullOrWhiteSpace(modelNameFilter);

        // Known, documented scope limit (grok-review MINOR, 2026-07-30): modelNameFilter only
        // narrows the DEPOT fallback below -- a pre-configured _roleBindings entry still wins
        // regardless of the filter. Tried making the filter override roleBindings too and it
        // broke NativeRuntimePhaseDTests's real-admission-denial coverage, which depends on
        // roleBindings taking precedence unconditionally (see that test's own comment). Not a
        // live bug today: nothing constructs NativeWithFallbackRuntime (the only caller that
        // ever supplies a non-null modelNameFilter) for a role that also has a roleBindings
        // entry -- OrcChat's Boss role never has one; Researcher/Reviewer's Context-Fabric
        // bindings are consumed through NativeRoleRuntime directly, never through the wrapper.
        // Revisit if a future caller needs both a pre-bound role AND per-request model pinning
        // at once.
        var depot = hasFilter ? _depot.WithBaseModelFilter(modelNameFilter!) : _depot;
        RuntimeRoleBinding? binding = _roleBindings.TryGetValue(role, out var configured)
            ? configured
            : depot.ResolveRole(role);
        if (binding is null)
        {
            throw new InvalidOperationException(hasFilter
                ? $"No local GGUF matches requested model '{modelNameFilter}' for runtime role {role}."
                : $"No base GGUF resolved for runtime role {role}.");
        }

        // grok-review MINOR, 2026-07-30: fired HERE, from the binding this specific call just
        // resolved, not polled from GetHealth(role) after the stream finishes -- GetHealth reads
        // shared _lastHealthByRole[role] state that a DIFFERENT concurrent call on the same role
        // can overwrite in between, which would misattribute or drop the model-changed signal.
        // Swallowed deliberately: a caller's logging callback throwing must not turn an
        // otherwise-successful generation into a failure.
        try { onModelResolved?.Invoke(binding.BaseModel.DisplayName); } catch { /* observability only */ }

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

                // Native Runtime v2.0 Phase D (docs/NATIVE_RUNTIME_V2_SPEC.md, cancellation
                // leg): a request that did not complete cleanly -- cancelled mid-generation, or
                // failed for any other reason -- must not let the NEXT request on this role
                // reuse a potentially corrupted persistent executor. Confirmed live: cancelling
                // between a Prompt(token)/InferUntilReadyAsync pair left the executor unable to
                // drain a subsequent conversation's prompt batch at all (>1024 decode passes,
                // never NoKvSlot, never completing) even though this cancelled conversation's own
                // TrackedConversation disposed and its ActiveCount released normally -- the same
                // "disposed conversations not fully returning their cells" root cause already
                // documented for NoKvSlot (AdapterManager.cs's ForceRecycle comment), just
                // reached via cancellation instead. No CancellationToken passed here, matching
                // MarkRoleDegraded's own NoKvSlot call site: the mark must survive this very
                // request being cancelled, or the degraded executor would be silently reused by
                // the next one.
                await _orchestrator.MarkRoleDegraded(role).ConfigureAwait(false);

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

    /// <summary>Native Runtime v2.0 Phase C — forwards to <see cref="_orchestrator"/>.</summary>
    public RuntimeReservationSnapshot? GetReservationSnapshot()
    {
        ThrowIfDisposed();
        return _orchestrator.GetReservationSnapshot();
    }

    /// <summary>Native Runtime v2.0 Phase C — forwards to <see cref="_orchestrator"/>.</summary>
    public IReadOnlyList<AdapterRoleResidency> GetResidencySnapshot()
    {
        ThrowIfDisposed();
        return _orchestrator.GetResidencySnapshot();
    }

    /// <summary>
    /// Forwards to <see cref="_orchestrator"/>. Public entry point for
    /// <see cref="OrchestratorIDE.Services.Hive.HiveNodeServer"/>'s remote
    /// <c>POST /hive/roles/degrade</c> endpoint (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md
    /// HV-3 item 3) — previously this method was reachable only from the internal NoKvSlot/
    /// generation-failure paths inside this same class (<see cref="StreamRoleCompletionCoreAsync"/>,
    /// <see cref="InferUntilReadyAsync"/>).
    /// </summary>
    public Task MarkRoleDegraded(RuntimeRole role)
    {
        ThrowIfDisposed();
        return _orchestrator.MarkRoleDegraded(role);
    }

    public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<int?>(_options.ContextLength);

    public int? CountPromptTokens(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(history);
        var binding = _roleBindings.TryGetValue(role, out var configured)
            ? configured
            : _depot.ResolveRole(role);
        if (binding is null || !_runtime.IsModelLoaded(binding.BaseModel.Path))
            return null;
        return _runtime.TokenizePromptForLoadedModel(history, tools).Length;
    }

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

        var promptTokens = _runtime.TokenizePromptForLoadedModel(history, tools);
        var promptPath = _runtime.GetLastPromptPath() ?? "Unknown";
        var completionLimit = GetCompletionTokenLimit(
            promptTokens.Length,
            maxTokens,
            executor.Context.ContextSize);
        var started = DateTime.UtcNow;
        var firstTokenAt = default(DateTime);
        var outputBuilder = new StringBuilder();

        // Queue the same array used for the exact capacity check. Re-tokenizing the string here
        // would make the guard and the native request capable of disagreeing at the boundary.
        conversation.Prompt(promptTokens);
        await InferUntilReadyAsync(conversation, role, ct).ConfigureAwait(false);

        for (var i = 0; i < completionLimit; i++)
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
            await InferUntilReadyAsync(conversation, role, ct).ConfigureAwait(false);
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
            EstimatedVramBytes: MeasuredVramBytes());

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

        onUsage?.Invoke(promptTokens.Length, completionTokens);

        if (onToolCall is not null)
            foreach (var tc in ToolCallTextParser.Parse(outputText))
                onToolCall(tc);
    }

    internal static int GetCompletionTokenLimit(
        int promptTokenCount,
        int requestedMaxTokens,
        uint contextSize)
    {
        var available = (long)contextSize - promptTokenCount;
        if (available < 0)
            throw new InvalidOperationException(
                $"Rendered prompt is {promptTokenCount} tokens, exceeding the native context size of {contextSize}.");
        return (int)Math.Min(Math.Max(0, requestedMaxTokens), available);
    }

    private async Task InferUntilReadyAsync(
        LLama.Batched.Conversation conversation, RuntimeRole role, CancellationToken ct)
    {
        var passes = 0;
        var noKvSlotRetries = 0;
        while (conversation.RequiresInference)
        {
            var result = await conversation.Executor.Infer(ct).ConfigureAwait(false);
            if (result == DecodeResult.NoKvSlot)
            {
                // NoKvSlot means the pending batch could not reserve enough attention-KV cells.
                // Exact prompt accounting and the completion cap above prevent a single request
                // from exceeding its context. Keep the existing degraded-role recycle as
                // defense-in-depth for other causes such as temporary cross-conversation pressure,
                // and mark it on the first hit rather than waiting for the final retry.
                // Deliberately NOT passed this request's cancellation token: the mark must
                // survive the request being cancelled, or the degraded executor gets reused by
                // the next request (CodeRabbit finding, 2026-07-06). Cancellation still
                // propagates promptly via the Task.Delay below.
                await _orchestrator.MarkRoleDegraded(role).ConfigureAwait(false);

                // LLamaSharp documents this as retryable (BatchedExecutor.Infer requeues the
                // batch internally rather than consuming it): "there is not enough memory for
                // inference, try disposing some conversation threads and running inference
                // again." A few cells may free up as other conversations finish disposing --
                // give that a bounded chance before failing hard. This is defense-in-depth only;
                // it cannot rescue a conversation whose own prompt alone overflows the KV pool --
                // that case is expected to still fail here after exhausting retries, just ~1.8s
                // later with a diagnostic trail (THEORC_KVCACHE_DIAGNOSTICS=1) instead of
                // silently on the first hit.
                const int maxNoKvSlotRetries = 8;
                if (noKvSlotRetries >= maxNoKvSlotRetries)
                    throw new InvalidOperationException(
                        $"Native inference failed while draining a prompt batch: {result} (gave up after {noKvSlotRetries} retries).");
                noKvSlotRetries++;
                if (Environment.GetEnvironmentVariable("THEORC_KVCACHE_DIAGNOSTICS") == "1")
                    Console.WriteLine($"[KV-DIAG] NoKvSlot retry {noKvSlotRetries}/{maxNoKvSlotRetries}");
                await Task.Delay(TimeSpan.FromMilliseconds(50 * noKvSlotRetries), ct).ConfigureAwait(false);
                continue;
            }
            if (result != DecodeResult.Ok)
                throw new InvalidOperationException($"Native inference failed while draining a prompt batch: {result}.");
            // A successful decode ends the current NoKvSlot episode: the cap bounds CONSECUTIVE
            // failures, not the lifetime total, so a long drain with separately-recovered
            // episodes isn't failed by their sum (CodeRabbit finding, 2026-07-04).
            noKvSlotRetries = 0;
            if (++passes > 1024)
                throw new InvalidOperationException("Native inference did not drain the pending prompt batch.");
        }
    }

    private static string FormatActiveModel(RuntimeRoleBinding binding) =>
        binding.Adapter is null
            ? $"{binding.Role}:{binding.BaseModel.DisplayName}"
            : $"{binding.Role}:{binding.BaseModel.DisplayName} + {binding.Adapter.DisplayName}";

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
                EstimatedVramBytes: MeasuredVramBytes());
            _lastPromptPathByRole[role] = _runtime.GetLastPromptPath();
        }
    }

    /// <summary>
    /// Native Runtime v2.0 Phase B addendum (docs/NATIVE_RUNTIME_V2_SPEC.md): prefers
    /// <see cref="LLamaSharpRuntime.LastMeasuredVramBytes"/> — real numbers parsed from
    /// llama.cpp's own load-time log lines, exact and unaffected by WDDM — over
    /// <see cref="NativeVramProbe.TryQueryCurrentProcessVramBytes"/> (Phase C), which the
    /// same-day spike found returns null on effectively every Windows box (WDDM makes
    /// nvidia-smi's per-process query dead there). The nvidia-smi read stays as a fallback for
    /// whatever it can still cover (e.g. a future non-WDDM environment) — never worse than
    /// Phase C shipped, only more often non-null.
    /// </summary>
    private long? MeasuredVramBytes() =>
        _runtime.LastMeasuredVramBytes ?? NativeVramProbe.TryQueryCurrentProcessVramBytes();
}
