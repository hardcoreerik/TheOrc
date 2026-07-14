// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Batched;
using LLama.Common;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// ILocalModelRuntime for in-process GGUF inference via LLamaSharp (Phase 2).
///
/// This is the "no Ollama required" runtime. Models are loaded directly from
/// GGUF files — no server process, no HTTP. Tool calling uses text-format
/// parsing (same path as the OllamaClient LlamaCpp backend) until GBNF
/// constrained decoding is implemented in Phase 3.
///
/// Construction:
///   var runtime = new LLamaSharpRuntime();
///   await runtime.LoadModelAsync("path/to/model.gguf");
///   _loop = new AgentLoop(runtime, ...);
///
/// Backend packages are supplied by OrchestratorIDE.NativeRuntime. The CPU package also
/// carries current macOS ARM64 Metal libraries; CUDA 12 is packaged only for x64 Windows/Linux.
///
/// LoRA note: LLamaSharp 0.27 removed ModelParams.LoraAdapters. Adapter support
/// is deferred to Phase 3 (RUNTIME_PHASE0_SPEC.md §7).
/// </summary>
public sealed class LLamaSharpRuntime : ILocalModelRuntime
{
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private string? _activeModelPath;
    private string? _activeAdapterPath;
    private RuntimeOptions _options = new();

    // Telemetry tracking — updated on each completed generation call (not cancelled ones).
    private double? _lastTokensPerSecond;
    private TimeSpan? _lastTimeToFirstToken;

    // Cached result of the first LLamaTemplate probe: null = not yet tested,
    // true = model has a working embedded template, false = fall back to ChatML every time.
    // Avoids paying the exception cost on every call for templateless models.
    private bool? _hasEmbeddedTemplate;
    private bool _foldSystemIntoUser;
    private string? _lastPromptPath;

    public string RuntimeName => "LLamaSharp";

    // ── IModelRuntime ────────────────────────────────────────────────────────

    public Task<bool> IsReachableAsync(CancellationToken ct = default) =>
        Task.FromResult(_weights != null);

    public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<List<string>>(_activeModelPath is null
            ? []
            : [Path.GetFileName(_activeModelPath)]);

    public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<int?>(_weights is null ? null : _options.ContextLength);

    /// <summary>
    /// Phase 3 / AdapterManager seam (RUNTIME_PHASE0_SPEC.md §7a). AdapterManager needs raw
    /// LLamaWeights + IContextParams to build its own persistent per-role BatchedExecutor
    /// instances (separate from the StatelessExecutor this runtime uses for StreamCompletionAsync),
    /// but _weights/_modelParams must stay private to everyone else. This factory is the only
    /// seam: AdapterManager never touches either field directly, just asks for an executor.
    /// Internal because OrchestratorIDE.Core.Runtime compiles into the same assembly as
    /// AdapterManager via &lt;Compile Include&gt; — no InternalsVisibleTo needed.
    /// </summary>
    internal BatchedExecutor CreateBatchedExecutor()
    {
        if (_weights is null || _modelParams is null)
            throw new InvalidOperationException(
                "No model loaded. Call LoadModelAsync before requesting a BatchedExecutor.");
        return new BatchedExecutor(_weights, _modelParams);
    }

    // Bumped every time _weights is reassigned (new load or unload via DisposeAsync).
    // AdapterManager compares this against the generation it built its executors against —
    // a model reload disposes the previous LLamaWeights, so every BatchedExecutor built from
    // the old weights is now dangling and must be invalidated, not just the role being asked for.
    // volatile (not a plain auto-property — C# disallows volatile on those) because
    // AdapterManager reads this from its own lock, not LLamaSharpRuntime's; without it, a
    // reader thread isn't guaranteed to observe a writer thread's increment promptly.
    private volatile int _weightsGeneration;
    internal int WeightsGeneration => _weightsGeneration;

    /// <summary>
    /// NOTE: the <paramref name="model"/> parameter is unused for local runtimes —
    /// inference always runs against the GGUF loaded by LoadModelAsync.
    /// This matches the IModelRuntime contract where local implementations
    /// are model-instance singletons rather than name-routed dispatchers.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        double? topP = null,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_weights is null || _modelParams is null)
            throw new InvalidOperationException(
                "No model loaded. Call LoadModelAsync before streaming.");

        // StatelessExecutor allocates a full KV-cache context per InferAsync call
        // (see Phase 3 backlog: pool or cache executor per loaded model). StatelessExecutor
        // itself isn't IDisposable, but the native context it owns (.Context) is — confirmed
        // via reflection during the Stage 1 smoke test (RUNTIME_SWITCH_PLAN.md) after Grok
        // caught that this was never disposed, leaking native KV-cache memory on every call.
        var executor = new StatelessExecutor(_weights, _modelParams);
        try
        {
            // Build the raw prompt using the GGUF's embedded chat template.
            // Falls back to ChatML format if the model has no template.
            var prompt = BuildPromptForLoadedModel(history, tools);

            // TopP is init-only on DefaultSamplingPipeline -- must be set in the initializer,
            // not assigned after construction. Only overridden when explicitly set, otherwise
            // omitted from the initializer entirely so the pipeline's own default applies,
            // same as before this parameter existed.
            var samplingPipeline = topP is { } p
                ? new LLama.Sampling.DefaultSamplingPipeline { Temperature = (float)temperature, TopP = (float)p }
                : new LLama.Sampling.DefaultSamplingPipeline { Temperature = (float)temperature };

            var inferParams = new InferenceParams
            {
                MaxTokens = maxTokens,
                SamplingPipeline = samplingPipeline,
                // Common end-of-turn markers across model families
                AntiPrompts = ["<|user|>", "<|end|>", "<|im_end|>", "<end_of_turn>", "<turn|>", "[/INST]", "\nUser:", "\nHuman:"],
            };

            var outputBuilder = new StringBuilder();
            var firstTokenAt = default(DateTime);
            var started = DateTime.UtcNow;

            await foreach (var token in executor.InferAsync(prompt, inferParams, ct))
            {
                if (firstTokenAt == default) firstTokenAt = DateTime.UtcNow;
                outputBuilder.Append(token);
                yield return token;
            }

            // Telemetry — only updated when at least one token arrived (not on pre-first-token cancel).
            var elapsed = DateTime.UtcNow - started;
            if (firstTokenAt != default)
                _lastTimeToFirstToken = firstTokenAt - started;
            var outputText = outputBuilder.ToString();
            var completionTokens = ContextManager.EstimateTokens(outputText);
            if (elapsed.TotalSeconds > 0)
                _lastTokensPerSecond = completionTokens / elapsed.TotalSeconds;

            // Tool calls: parse from the text output (GBNF constrained decoding is Phase 3).
            if (onToolCall != null)
                foreach (var tc in ParseToolCalls(outputText))
                    onToolCall(tc);

            var promptTokens = ContextManager.EstimateTokens(prompt);
            onUsage?.Invoke(promptTokens, completionTokens);
        }
        finally
        {
            // Runs even on cancellation or an exception from InferAsync — the native context
            // must not leak just because the caller cancelled mid-stream.
            executor.Context.Dispose();
        }
    }

    public RuntimeHealth GetHealth() => new(
        IsAvailable: _weights != null,
        RuntimeName: RuntimeName,
        ActiveModel: ActiveModelName,
        Message: _weights != null
            ? $"In-process · ctx={_options.ContextLength}"
            : "No model loaded");

    public RuntimeStats GetStats() => new(
        RuntimeName: RuntimeName,
        ActiveModel: ActiveModelName,
        TokensPerSecond: _lastTokensPerSecond,
        LastTimeToFirstToken: _lastTimeToFirstToken,
        EstimatedVramBytes: null);  // LLamaSharp doesn't expose per-process VRAM via managed API

    internal string? GetLastPromptPath() => _lastPromptPath;

    // ── ILocalModelRuntime ───────────────────────────────────────────────────

    public async Task<ModelLoadResult> LoadModelAsync(
        string baseGgufPath,
        string? adapterPath = null,
        RuntimeOptions? options = null,
        CancellationToken ct = default)
    {
        // Pin backend selection (CUDA preference on driver-only machines) before the first
        // NativeApi touch. Idempotent — callers that already surfaced the report pay nothing.
        // Captured (not discarded) so a load failure below can report exactly what the backend
        // pre-flight found/tried, instead of only the generic NativeApi TypeInitializationException.
        //
        // Opt-in native log sink for the Gemma-specific NoKvSlot investigation
        // (docs/CONTEXT_FABRIC_TEST_HARNESS.md §7): llama.cpp emits its own WARN/ERROR lines
        // right when a decode fails to find a KV slot (e.g. cell-count or batch-size detail our
        // managed DecodeResult enum doesn't carry). Reuses THEORC_KVCACHE_DIAGNOSTICS so a single
        // env var turns on every diagnostic this investigation has added. stdout, not stderr --
        // same PowerShell/Tee-Object hazard as AdapterManager's LogKvDiagnostic.
        var backendReport = Environment.GetEnvironmentVariable("THEORC_KVCACHE_DIAGNOSTICS") == "1"
            ? NativeBackendBootstrap.EnsureConfigured(line => Console.WriteLine($"[NativeLog] {line}"))
            : NativeBackendBootstrap.EnsureConfigured();

        await DisposeAsync();  // unload previous model

        _options = options ?? new();

        if (!File.Exists(baseGgufPath))
            return new ModelLoadResult(false, RuntimeName, baseGgufPath,
                $"Model file not found: {baseGgufPath}");

        try
        {
            var mp = new ModelParams(baseGgufPath)
            {
                ContextSize   = (uint)_options.ContextLength,
                GpuLayerCount = _options.GpuLayers,
                // Default n_seq_max is 1. AdapterManager mints a fresh, never-recycled sequence
                // id per conversation (see AdapterManager.SequenceHardLimit) and only tears down
                // the executor once minted count approaches that cap -- an assumption that only
                // held by accident for plain-transformer architectures, where llama.cpp tolerates
                // seq_id >= n_seq_max in the unified KV-cache path. Recurrent/hybrid architectures
                // (e.g. Qwen3.5's Gated Delta Net layers) validate seq_id strictly against
                // n_seq_max and fail find_slot/init_batch on literally the second conversation
                // (seq_id=1) otherwise -- 100% reproducible, not load-dependent. Matching SeqMax to
                // the hard limit makes the recycle contract hold for every architecture.
                SeqMax        = (uint)AdapterManager.SequenceHardLimit,
                // SwaFull deliberately left at its native default (true). false was tried during
                // the NoKvSlot investigation to shrink SWA-layer cache 6x on Gemma-3-class
                // architectures, but that shrinks the SWA cache to min(fullContext, n_swa +
                // UBatchSize) -- with the default UBatchSize=512, that's ~1536 cells, not 8192.
                // Context Fabric's Reviewer/Answer-stage prompts routinely run 6,000+ tokens
                // (they re-include the evidence pack plus the Researcher's draft for review),
                // so every one of those calls failed NoKvSlot outright -- a single prompt too
                // large for the undersized SWA window, not a cumulative-pressure problem
                // (confirmed live: force-recycling to a fresh, empty pool before each call did
                // not help even one call succeed). Making UBatchSize large enough to cover these
                // prompts would erase most of the intended memory saving anyway, since Context
                // Fabric's prompts are designed to use most of the context budget. Reverted;
                // see docs/CONTEXT_FABRIC_TEST_HARNESS.md §7.
            };

            if (!string.IsNullOrEmpty(adapterPath))
            {
                if (!File.Exists(adapterPath))
                    return new ModelLoadResult(false, RuntimeName, baseGgufPath,
                        $"Adapter file not found: {adapterPath}");
                // TODO: LLamaSharp 0.27 removed ModelParams.LoraAdapters. LoRA attach
                // revisited in Phase 3 after KV-cache safety spike (RUNTIME_PHASE0_SPEC.md §7).
                // The adapter path is stored so the UI can surface it, but it is NOT applied.
            }

            _weights          = await LLamaWeights.LoadFromFileAsync(mp, ct);
            _modelParams      = mp;
            _activeModelPath  = baseGgufPath;
            _activeAdapterPath = adapterPath;
            Interlocked.Increment(ref _weightsGeneration);

            // Fix #3: do not claim adapter is active — LoRA is not applied in Phase 2.
            var detail = adapterPath is not null
                ? $"(adapter deferred to Phase 3: {Path.GetFileName(adapterPath)})"
                : null;
            return new ModelLoadResult(true, RuntimeName, Path.GetFileName(baseGgufPath), detail);
        }
        catch (OperationCanceledException)
        {
            // Fix #2: let cancellation propagate rather than converting to a failure result.
            throw;
        }
        catch (Exception ex)
        {
            // Append the backend-selection report (CUDA-driver detection, cuda12 DLL pre-flight
            // results, which backend was actually selected) — it was already computed above and
            // previously discarded. On a native-load failure this is exactly the detail needed
            // to tell "no CUDA-capable driver" from "packaged cuda12 DLL chain rejected" from
            // "selection succeeded but the real load still failed anyway".
            var backendDetail = $"backend: {backendReport.Verdict}" +
                (backendReport.Log.Count > 0 ? $" [{string.Join("; ", backendReport.Log)}]" : "");
            return new ModelLoadResult(false, RuntimeName, baseGgufPath,
                $"{FormatLoadFailure(ex)} | {backendDetail}");
        }
    }

    /// <summary>
    /// LoRA hot-swap: NOT IMPLEMENTED until the KV-cache safety spike runs.
    /// See RUNTIME_PHASE0_SPEC.md §7.
    /// </summary>
    public Task SwapAdapterAsync(string? adapterName, CancellationToken ct = default) =>
        // Task.FromException so the exception surfaces via the returned Task, not synchronously.
        // A bare `=> throw` in a non-async Task-returning method bypasses async try/catch.
        Task.FromException(new NotSupportedException(
            "LoRA hot-swap is spike-gated. See RUNTIME_PHASE0_SPEC.md §7. " +
            "Use LoadModelAsync with the desired adapterPath instead."));

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        var hadWeights = _weights is not null;
        _weights?.Dispose();
        _weights              = null;
        _modelParams          = null;
        _activeModelPath      = null;
        _activeAdapterPath    = null;
        _lastTokensPerSecond  = null;
        _lastTimeToFirstToken = null;
        _hasEmbeddedTemplate  = null;
        _foldSystemIntoUser   = false;
        _lastPromptPath       = null;
        if (hadWeights) Interlocked.Increment(ref _weightsGeneration);
        return ValueTask.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string? ActiveModelName => _activeModelPath is null
        ? null
        : Path.GetFileName(_activeModelPath);

    /// <summary>
    /// Converts the message history into a raw prompt string using the model's
    /// embedded GGUF chat template. Falls back to ChatML if the model has none.
    /// Tools are injected into the system message as a JSON block (Phase 2 text approach;
    /// Phase 3 will use GBNF grammar constraints instead).
    ///
    /// Template probe result is cached after the first call so templateless models
    /// skip the try/catch entirely on subsequent turns.
    /// </summary>
    internal string BuildPromptForLoadedModel(
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools)
    {
        var messages = NativePromptBuilder.PrepareMessages(history, tools);

        // Fast path: we already know this model has no embedded template.
        if (_hasEmbeddedTemplate == false)
            return BuildFallbackPrompt(messages);

        if (_weights is null)
            throw new InvalidOperationException(
                "No model loaded. Call LoadModelAsync before building a native prompt.");

        // Try the GGUF-embedded template. LLamaTemplate is stateful (Add() accumulates),
        // so a fresh instance is required per call. Construction reads the GGUF template
        // metadata once; Phase 3 will explore caching the parsed template string.
        try
        {
            var templateMessages = _foldSystemIntoUser
                ? NativePromptBuilder.FoldSystemIntoFirstUser(messages)
                : messages;
            var result = ApplyEmbeddedTemplate(templateMessages);
            _hasEmbeddedTemplate = true;
            _lastPromptPath = _foldSystemIntoUser ? "EmbeddedTemplate:SystemFolded" : "EmbeddedTemplate";
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var templateFailure = ex;
            if (!_foldSystemIntoUser && messages.Any(message => message.Role == MessageRole.System))
            {
                try
                {
                    var result = ApplyEmbeddedTemplate(NativePromptBuilder.FoldSystemIntoFirstUser(messages));
                    _hasEmbeddedTemplate = true;
                    _foldSystemIntoUser = true;
                    _lastPromptPath = "EmbeddedTemplate:SystemFolded";
                    return result;
                }
                catch (Exception retryEx) when (retryEx is not OutOfMemoryException)
                {
                    templateFailure = retryEx;
                }
            }

            // Only cache false if we haven't already confirmed a working template.
            // A failure after a successful probe is a runtime error, not a template-absent signal.
            if (_hasEmbeddedTemplate is null)
            {
                _hasEmbeddedTemplate = false;
                System.Diagnostics.Trace.TraceWarning(
                    $"[LLamaSharpRuntime] Template probe failed ({templateFailure.GetType().Name}: {templateFailure.Message}); " +
                    "falling back to ChatML for this session.");
            }
            return BuildFallbackPrompt(messages);
        }
    }

    private string BuildFallbackPrompt(List<AgentMessage> messages)
    {
        if (IsGemma4Model(_activeModelPath))
        {
            _lastPromptPath = "GemmaNativeFallback";
            return NativePromptBuilder.BuildGemma4Prompt(messages);
        }

        _lastPromptPath = "ChatMLFallback";
        return NativePromptBuilder.BuildChatMLPrompt(messages);
    }

    private static bool IsGemma4Model(string? modelPath)
    {
        var name = Path.GetFileName(modelPath ?? "").Replace('_', '-');
        return name.Contains("gemma4", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("gemma-4", StringComparison.OrdinalIgnoreCase);
    }

    private string ApplyEmbeddedTemplate(IEnumerable<AgentMessage> messages)
    {
        var template = new LLamaTemplate(_weights!) { AddAssistant = true };
        foreach (var msg in messages)
            template.Add(NativePromptBuilder.ToRoleString(msg.Role), msg.Content ?? "");
        return Encoding.UTF8.GetString(template.Apply());
    }

    private static string FormatLoadFailure(Exception ex)
    {
        // Walk the FULL chain, not just one level. A TypeInitializationException's
        // InnerException is often itself a wrapper (e.g. LLamaSharp's RuntimeError) with its
        // own InnerException carrying the actual root cause — truncating at one level silently
        // dropped exactly the detail needed to diagnose a native-library load failure (observed
        // 2026-07-04 on HARDCOREPC: every failure showed only "RuntimeError: Failed to load the
        // native library. Please check the log for more information." with the real reason cut off).
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" | Inner: ", parts);
    }

    private static List<ToolCall> ParseToolCalls(string text) => ToolCallTextParser.Parse(text);
}
