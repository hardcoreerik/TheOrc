// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
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
/// is deferred to Phase 3 (docs/RUNTIME_PHASE0_SPEC.md §7).
/// </summary>
public sealed class LLamaSharpRuntime : ILocalModelRuntime
{
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private ModelParams? _statelessModelParams;
    private string? _activeModelPath;
    private string? _activeAdapterPath;
    private RuntimeOptions _options = new();

    // Native Runtime v2.0 Phase B addendum (docs/NATIVE_RUNTIME_V2_SPEC.md) -- accumulates
    // real VRAM measurements parsed from llama.cpp's own load-time log lines. See
    // NativeLoadAllocationParser's doc comment for why this exists (WDDM makes nvidia-smi's
    // per-process query dead on Windows) and its known limitation (the underlying native log
    // sink is process-wide, not per-instance -- see LoadModelAsync's registration comment).
    private readonly NativeLoadAllocationAccumulator _loadMeasurement = new();

    // Telemetry tracking — updated on each completed generation call (not cancelled ones).
    private double? _lastTokensPerSecond;
    private TimeSpan? _lastTimeToFirstToken;

    // Cached result of the first LLamaTemplate probe: null = not yet tested,
    // true = model has a working embedded template, false = fall back to ChatML every time.
    // Avoids paying the exception cost on every call for templateless models.
    private bool? _hasEmbeddedTemplate;
    private bool _foldSystemIntoUser;
    private string? _lastPromptPath;

    // Cached result of scanning the GGUF's own tokenizer.chat_template for an
    // "enable_thinking" toggle (Qwen3/3.5-family reasoning models and similar).
    // See ApplyEmbeddedTemplate for why this needs to be applied manually.
    private bool? _templateSupportsThinkingSuppression;

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
    /// Phase 3 / AdapterManager seam (docs/RUNTIME_PHASE0_SPEC.md §7a). AdapterManager needs raw
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

    internal LLamaToken[] TokenizePromptForLoadedModel(
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools)
    {
        if (_weights is null)
            throw new InvalidOperationException(
                "No model loaded. Call LoadModelAsync before tokenizing a native prompt.");
        return _weights.Tokenize(
            BuildPromptForLoadedModel(history, tools),
            add_bos: true,
            special: true,
            Encoding.UTF8);
    }

    internal bool IsModelLoaded(string path) =>
        _activeModelPath is not null &&
        string.Equals(_activeModelPath, path, StringComparison.OrdinalIgnoreCase);

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
        if (_weights is null || _statelessModelParams is null)
            throw new InvalidOperationException(
                "No model loaded. Call LoadModelAsync before streaming.");

        // StatelessExecutor allocates a full KV-cache context per InferAsync call
        // (see Phase 3 backlog: pool or cache executor per loaded model). StatelessExecutor
        // itself isn't IDisposable, but the native context it owns (.Context) is — confirmed
        // via reflection during the Stage 1 smoke test (RUNTIME_SWITCH_PLAN.md) after Grok
        // caught that this was never disposed, leaking native KV-cache memory on every call.
        //
        // Uses _statelessModelParams (SeqMax=1), NOT _modelParams (SeqMax=SequenceHardLimit) --
        // this executor only ever runs a single sequence per call, so reusing the persistent-
        // executor params would reserve the same hybrid-architecture rs-cache VRAM budget on
        // every stateless call for no benefit (CodeRabbit review, PR #56).
        var executor = new StatelessExecutor(_weights, _statelessModelParams);
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
        // Native Runtime v2.0 Phase B addendum: real measurement parsed from llama.cpp's own
        // load-time log lines (LLamaSharp still exposes no managed VRAM-query API directly).
        // Null until at least one recognized CUDA allocation line has been observed -- e.g. no
        // model loaded yet, or a non-CUDA backend whose log format this parser doesn't match.
        EstimatedVramBytes: _loadMeasurement.TotalBytes);

    /// <summary>Real VRAM measured from llama.cpp's own load-time log lines (Phase B
    /// addendum) -- see <see cref="NativeLoadAllocationAccumulator"/>. Internal: NativeRoleRuntime
    /// prefers this (exact, WDDM-proof) over <see cref="NativeVramProbe.TryQueryCurrentProcessVramBytes"/>
    /// (dead on Windows WDDM) when both are available.</summary>
    internal long? LastMeasuredVramBytes => _loadMeasurement.TotalBytes;

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
        // The native log sink now always runs _loadMeasurement.Observe (Phase B addendum —
        // parses real VRAM allocations from llama.cpp's own log lines, see
        // NativeLoadAllocationParser), combined with the pre-existing opt-in console mirror for
        // the Gemma-specific NoKvSlot investigation (docs/CONTEXT_FABRIC_BUG_HISTORY.md §7):
        // llama.cpp emits its own WARN/ERROR lines right when a decode fails to find a KV slot
        // (e.g. cell-count or batch-size detail our managed DecodeResult enum doesn't carry).
        // Reuses THEORC_KVCACHE_DIAGNOSTICS so a single env var turns on every diagnostic this
        // investigation has added. stdout, not stderr -- same PowerShell/Tee-Object hazard as
        // AdapterManager's LogKvDiagnostic.
        //
        // KNOWN LIMITATION: NativeBackendBootstrap's log sink is process-wide, not per-runtime
        // instance (see its own doc comment). If more than one LLamaSharpRuntime is loading
        // concurrently in the same process (e.g. the documented HIVE-worker + main-chat
        // "VRAM double-booking" dual-runtime configuration), each LoadModelAsync call
        // re-registers the sink and can steal it from another instance's in-flight load,
        // producing an inaccurate accumulation for whichever instance loses the race. Correct
        // for the common single-native-runtime case; a real fix needs per-instance log
        // correlation, which is a larger change than this addendum's scope.
        _loadMeasurement.Reset();
        var diagnosticsEnabled = Environment.GetEnvironmentVariable("THEORC_KVCACHE_DIAGNOSTICS") == "1";
        var backendReport = NativeBackendBootstrap.EnsureConfigured(line =>
        {
            _loadMeasurement.Observe(line);
            if (diagnosticsEnabled)
                Console.WriteLine($"[NativeLog] {line}");
        });

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
                // see docs/CONTEXT_FABRIC_BUG_HISTORY.md §7.
            };

            // StreamCompletionAsync's StatelessExecutor only ever runs one sequence per call
            // (CodeRabbit review, PR #56) -- it must NOT reuse `mp` above. llama.cpp allocates
            // its per-sequence recurrent-state ("rs cache") buffer sized by n_seq_max on hybrid
            // architectures, so reusing the persistent-executor params (SeqMax =
            // SequenceHardLimit = 40) would make every single stateless call reserve the same
            // ~2GB the persistent AdapterManager executors reserve once, wasting VRAM and
            // risking OOM under concurrent calls. Separate instance, native default SeqMax (1).
            var statelessMp = new ModelParams(baseGgufPath)
            {
                ContextSize   = (uint)_options.ContextLength,
                GpuLayerCount = _options.GpuLayers,
            };

            if (!string.IsNullOrEmpty(adapterPath))
            {
                if (!File.Exists(adapterPath))
                    return new ModelLoadResult(false, RuntimeName, baseGgufPath,
                        $"Adapter file not found: {adapterPath}");
                // TODO: LLamaSharp 0.27 removed ModelParams.LoraAdapters. LoRA attach
                // revisited in Phase 3 after KV-cache safety spike (docs/RUNTIME_PHASE0_SPEC.md §7).
                // The adapter path is stored so the UI can surface it, but it is NOT applied.
            }

            _weights              = await LLamaWeights.LoadFromFileAsync(mp, ct);
            _modelParams          = mp;
            _statelessModelParams = statelessMp;
            _activeModelPath      = baseGgufPath;
            _activeAdapterPath    = adapterPath;
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
    /// See docs/RUNTIME_PHASE0_SPEC.md §7.
    /// </summary>
    public Task SwapAdapterAsync(string? adapterName, CancellationToken ct = default) =>
        // Task.FromException so the exception surfaces via the returned Task, not synchronously.
        // A bare `=> throw` in a non-async Task-returning method bypasses async try/catch.
        Task.FromException(new NotSupportedException(
            "LoRA hot-swap is spike-gated. See docs/RUNTIME_PHASE0_SPEC.md §7. " +
            "Use LoadModelAsync with the desired adapterPath instead."));

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        var hadWeights = _weights is not null;
        _weights?.Dispose();
        _weights              = null;
        _modelParams          = null;
        _statelessModelParams = null;
        _activeModelPath      = null;
        _activeAdapterPath    = null;
        _lastTokensPerSecond  = null;
        _lastTimeToFirstToken = null;
        _hasEmbeddedTemplate  = null;
        _foldSystemIntoUser   = false;
        _lastPromptPath       = null;
        _templateSupportsThinkingSuppression = null;
        _loadMeasurement.Reset(); // stats must not report a since-unloaded model's VRAM
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
        var result = Encoding.UTF8.GetString(template.Apply());

        // Qwen3/3.5-family (and similar) GGUF chat templates end with:
        //   {%- if add_generation_prompt %}
        //       {{- '<|im_start|>assistant\n' }}
        //       {%- if enable_thinking is defined and enable_thinking is true %}
        //           {{- '<think>\n' }}
        //       {%- else %}
        //           {{- '<think>\n\n</think>\n\n' }}
        //       {%- endif %}
        //   {%- endif %}
        // i.e. the template's OWN designed way to suppress reasoning mode is to leave
        // enable_thinking unset, which pre-seeds an empty, already-closed <think></think>
        // block so the model starts generating its real answer immediately. LLamaSharp's
        // LLamaTemplate (a minimal Jinja subset, not a full interpreter) renders through
        // "<|im_start|>assistant\n" without error but does not evaluate this trailing
        // `enable_thinking is defined` conditional at all -- confirmed empirically: CF-7
        // reader calls against Qwen3.5-9B produced <think> blocks consuming ~2000 tokens
        // (near the reader's completion cap) on literally every one of 128 segments, with
        // zero cards ever successfully parsed as JSON (rawOutputExcerpt always started with
        // "<think>", promptPath was "EmbeddedTemplate" -- not a ChatML fallback). Since the
        // model free-generates its trained default (thinking) whenever the seed is absent,
        // append the exact same empty-think-block the template would have produced, so the
        // model behaves identically to enable_thinking=false in an official inference stack.
        // Detected generically via the raw chat_template text (not a filename/family check)
        // so this covers any future reasoning model using the same enable_thinking pattern.
        // Isolated try/catch: this is a best-effort enhancement on top of an already-successful
        // template render. A failure here (e.g. a malformed/unexpected Metadata entry) must not
        // propagate to BuildPromptForLoadedModel's outer catch, which treats ANY exception from
        // this method as "the embedded template itself doesn't work" and permanently falls back
        // to ChatML for the rest of the session (Grok review, PR #58) -- that would be a much
        // worse regression than simply not suppressing thinking mode this one call.
        try
        {
            _templateSupportsThinkingSuppression ??= _weights!.Metadata.TryGetValue("tokenizer.chat_template", out var rawTemplate)
                && SupportsThinkingSuppression(rawTemplate);
            if (_templateSupportsThinkingSuppression == true)
                result = ApplyThinkingSuppression(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"[LLamaSharpRuntime] Thinking-suppression detection failed ({ex.GetType().Name}: {ex.Message}); " +
                "continuing without it for this render.");
        }

        return result;
    }

    /// <summary>
    /// True if the GGUF's own chat template implements the `enable_thinking` opt-in pattern
    /// (Qwen3/3.5-family and similar reasoning models) by emitting the SAME literal empty-seed
    /// text `ApplyThinkingSuppression` appends. Requires both markers, not just the variable
    /// name alone (Grok review, PR #58): a template could reference `enable_thinking` for
    /// unrelated semantics or a differently-shaped seed, and blindly appending Qwen's exact
    /// `&lt;think&gt;\n\n&lt;/think&gt;\n\n` text to a template that doesn't actually use that
    /// shape would be wrong, not just redundant. Pure/static so it's testable without a loaded
    /// model.
    /// </summary>
    internal static bool SupportsThinkingSuppression(string? rawChatTemplate) =>
        rawChatTemplate is not null
        && rawChatTemplate.Contains("enable_thinking", StringComparison.Ordinal)
        && rawChatTemplate.Contains("<think>\\n\\n</think>\\n\\n", StringComparison.Ordinal);

    /// <summary>
    /// Appends the empty, pre-closed &lt;think&gt;&lt;/think&gt; block the template itself would
    /// render when `enable_thinking` is left unset (see ApplyEmbeddedTemplate for why this must
    /// be done manually). No-op if the rendered prompt doesn't end in a newline (an embedded
    /// template always ends its assistant-turn opener with one, so this guards against appending
    /// onto something that isn't the shape we expect) or if its TAIL already carries a
    /// `&lt;think&gt;`/`&lt;/think&gt;` marker (idempotency guard: protects against double-application,
    /// and against a future LLamaSharp version that DOES evaluate the template's own seed, which
    /// would otherwise produce two think blocks back to back). Deliberately checks only the
    /// trimmed tail, not the whole prompt (Grok review, PR #58): a whole-prompt scan would
    /// false-positive and skip suppression entirely whenever any earlier message -- conversation
    /// history, a document being analyzed, anything -- happens to mention the literal text
    /// "&lt;think&gt;" for unrelated reasons. Pure/static so it's testable directly.
    /// </summary>
    internal static string ApplyThinkingSuppression(string renderedPrompt)
    {
        if (!renderedPrompt.EndsWith('\n'))
            return renderedPrompt;
        var tail = renderedPrompt.TrimEnd();
        if (tail.EndsWith("<think>", StringComparison.Ordinal) || tail.EndsWith("</think>", StringComparison.Ordinal))
            return renderedPrompt;
        return renderedPrompt + "<think>\n\n</think>\n\n";
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
