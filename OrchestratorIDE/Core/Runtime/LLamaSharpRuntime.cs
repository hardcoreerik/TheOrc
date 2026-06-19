// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
/// Backend note: LLamaSharp requires a native backend package at runtime:
///   - GPU:  LLamaSharp.Backend.Cuda12.Windows (or .Linux)
///   - CPU:  LLamaSharp.Backend.Cpu
/// The native backend is not bundled here — install via NuGet or system PATH.
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

    // Fix #7: static so JsonSerializerOptions reflection cache survives across calls.
    private static readonly JsonSerializerOptions _compactJson = new() { WriteIndented = false };

    public string RuntimeName => "LLamaSharp";

    // ── IModelRuntime ────────────────────────────────────────────────────────

    public Task<bool> IsReachableAsync(CancellationToken ct = default) =>
        Task.FromResult(_weights != null);

    public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<List<string>>(_activeModelPath is null
            ? []
            : [Path.GetFileName(_activeModelPath)]);

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
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_weights is null || _modelParams is null)
            throw new InvalidOperationException(
                "No model loaded. Call LoadModelAsync before streaming.");

        // StatelessExecutor allocates a full KV-cache context per InferAsync call
        // (see Phase 3 backlog: pool or cache executor per loaded model).
        var executor = new StatelessExecutor(_weights, _modelParams);

        // Build the raw prompt using the GGUF's embedded chat template.
        // Falls back to ChatML format if the model has no template.
        var prompt = BuildPrompt(history, tools);

        var inferParams = new InferenceParams
        {
            MaxTokens = maxTokens,
            SamplingPipeline = new LLama.Sampling.DefaultSamplingPipeline { Temperature = (float)temperature },
            // Common end-of-turn markers across model families
            AntiPrompts = ["<|user|>", "<|end|>", "<|im_end|>", "[/INST]", "\nUser:", "\nHuman:"],
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

    // ── ILocalModelRuntime ───────────────────────────────────────────────────

    public async Task<ModelLoadResult> LoadModelAsync(
        string baseGgufPath,
        string? adapterPath = null,
        RuntimeOptions? options = null,
        CancellationToken ct = default)
    {
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
            return new ModelLoadResult(false, RuntimeName, baseGgufPath, ex.Message);
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
        _weights?.Dispose();
        _weights              = null;
        _modelParams          = null;
        _activeModelPath      = null;
        _activeAdapterPath    = null;
        _lastTokensPerSecond  = null;
        _lastTimeToFirstToken = null;
        _hasEmbeddedTemplate  = null;
        return ValueTask.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string? ActiveModelName => _activeModelPath is null
        ? null
        : Path.GetFileName(_activeModelPath);

    private static string ToRoleString(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User   => "user",
        MessageRole.Tool   => "tool",
        _                  => "assistant",
    };

    /// <summary>
    /// Converts the message history into a raw prompt string using the model's
    /// embedded GGUF chat template. Falls back to ChatML if the model has none.
    /// Tools are injected into the system message as a JSON block (Phase 2 text approach;
    /// Phase 3 will use GBNF grammar constraints instead).
    ///
    /// Template probe result is cached after the first call so templateless models
    /// skip the try/catch entirely on subsequent turns.
    /// </summary>
    private string BuildPrompt(
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools)
    {
        var messages = history.ToList();

        if (tools is { Count: > 0 })
        {
            var toolJson = JsonSerializer.Serialize(tools, _compactJson);
            var toolBlock = $"\n\nAvailable tools (call as JSON):\n{toolJson}";
            var sysIdx = messages.FindIndex(m => m.Role == MessageRole.System);
            if (sysIdx >= 0)
                // WithContent() centralises all-field copying — safe when AgentMessage grows.
                messages[sysIdx] = messages[sysIdx].WithContent(messages[sysIdx].Content + toolBlock);
            else
                messages.Insert(0, new AgentMessage
                    { Role = MessageRole.System, Content = toolBlock });
        }

        // Fast path: we already know this model has no embedded template.
        if (_hasEmbeddedTemplate == false)
            return BuildChatMLPrompt(messages);

        // Try the GGUF-embedded template. LLamaTemplate is stateful (Add() accumulates),
        // so a fresh instance is required per call. Construction reads the GGUF template
        // metadata once; Phase 3 will explore caching the parsed template string.
        try
        {
            var template = new LLamaTemplate(_weights!);
            foreach (var msg in messages)
                template.Add(ToRoleString(msg.Role), msg.Content ?? "");
            var result = Encoding.UTF8.GetString(template.Apply());
            _hasEmbeddedTemplate = true;
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only cache false if we haven't already confirmed a working template.
            // A failure after a successful probe is a runtime error, not a template-absent signal.
            if (_hasEmbeddedTemplate is null)
            {
                _hasEmbeddedTemplate = false;
                System.Diagnostics.Trace.TraceWarning(
                    $"[LLamaSharpRuntime] Template probe failed ({ex.GetType().Name}: {ex.Message}); " +
                    "falling back to ChatML for this session.");
            }
            return BuildChatMLPrompt(messages);
        }
    }

    private static string BuildChatMLPrompt(List<AgentMessage> messages)
    {
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

    private static List<ToolCall> ParseToolCalls(string text) => ToolCallTextParser.Parse(text);
}
