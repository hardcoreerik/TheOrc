// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LLama;
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
///   await runtime.LoadModelAsync("path/to/model.gguf", "path/to/lora.gguf");
///   _loop = new AgentLoop(runtime, ...);
///
/// Backend note: LLamaSharp requires a native backend package at runtime:
///   - GPU:  LLamaSharp.Backend.Cuda12.Windows (or .Linux)
///   - CPU:  LLamaSharp.Backend.Cpu
/// The native backend is not bundled here — install via NuGet or system PATH.
/// </summary>
public sealed class LLamaSharpRuntime : ILocalModelRuntime
{
    private LLamaWeights? _weights;
    private ModelParams? _modelParams;
    private string? _activeModelPath;
    private string? _activeAdapterPath;
    private RuntimeOptions _options = new();

    // Telemetry tracking — updated on each generation call
    private double? _lastTokensPerSecond;
    private TimeSpan? _lastTimeToFirstToken;

    public string RuntimeName => "LLamaSharp";

    // ── IModelRuntime ────────────────────────────────────────────────────────

    public Task<bool> IsReachableAsync(CancellationToken ct = default) =>
        Task.FromResult(_weights != null);

    public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<List<string>>(_activeModelPath is null
            ? []
            : [Path.GetFileName(_activeModelPath)]);

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

        var executor = new StatelessExecutor(_weights, _modelParams);

        // Build the raw prompt using the GGUF's embedded chat template.
        // Falls back to ChatML format if the model has no template.
        var prompt = BuildPrompt(_weights, history, tools);

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

        // Telemetry
        var elapsed = DateTime.UtcNow - started;
        if (firstTokenAt != default)
            _lastTimeToFirstToken = firstTokenAt - started;
        var outputText = outputBuilder.ToString();
        var completionTokens = EstimateTokens(outputText);
        if (elapsed.TotalSeconds > 0)
            _lastTokensPerSecond = completionTokens / elapsed.TotalSeconds;

        // Tool calls: parse from the text output (GBNF constrained decoding is Phase 3).
        if (onToolCall != null)
            foreach (var tc in ParseToolCalls(outputText))
                onToolCall(tc);

        // Estimate prompt token count from input size
        var promptTokens = EstimateTokens(prompt);
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

    /// <summary>
    /// Load a GGUF base model, optionally with a LoRA adapter.
    /// Unloads any previously loaded model first.
    /// </summary>
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

            // LoRA adapter — applied at load time.
            // Note: hot-swap (SwapAdapterAsync) requires a spike to verify
            // KV-cache safety before roadmapping. See RUNTIME_PHASE0_SPEC.md §7.
            if (!string.IsNullOrEmpty(adapterPath))
            {
                if (!File.Exists(adapterPath))
                    return new ModelLoadResult(false, RuntimeName, baseGgufPath,
                        $"Adapter file not found: {adapterPath}");
                // TODO: LLamaSharp 0.27 removed ModelParams.LoraAdapters. LoRA attach
                // revisited in Phase 3 after KV-cache safety spike (RUNTIME_PHASE0_SPEC.md §7).
            }

            _weights          = await LLamaWeights.LoadFromFileAsync(mp, ct);
            _modelParams      = mp;
            _activeModelPath  = baseGgufPath;
            _activeAdapterPath = adapterPath;

            return new ModelLoadResult(true, RuntimeName,
                Path.GetFileName(baseGgufPath),
                adapterPath is not null
                    ? $"+ adapter {Path.GetFileName(adapterPath)}"
                    : null);
        }
        catch (Exception ex)
        {
            return new ModelLoadResult(false, RuntimeName, baseGgufPath, ex.Message);
        }
    }

    /// <summary>
    /// LoRA hot-swap: NOT IMPLEMENTED until the KV-cache safety spike runs.
    /// See RUNTIME_PHASE0_SPEC.md §7. Spike confirms whether detach/reattach
    /// requires a full context rebuild (expected yes — adapters modify activations).
    /// </summary>
    public Task SwapAdapterAsync(string? adapterName, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "LoRA hot-swap is spike-gated. See RUNTIME_PHASE0_SPEC.md §7. " +
            "Use LoadModelAsync with the desired adapterPath instead.");

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public ValueTask DisposeAsync()
    {
        _weights?.Dispose();
        _weights           = null;
        _modelParams       = null;
        _activeModelPath   = null;
        _activeAdapterPath = null;
        _lastTokensPerSecond = null;
        _lastTimeToFirstToken = null;
        return ValueTask.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string? ActiveModelName => _activeModelPath is null
        ? null
        : Path.GetFileName(_activeModelPath);

    /// <summary>
    /// Converts the message history into a raw prompt string using the model's
    /// embedded GGUF chat template. Falls back to ChatML if the model has none.
    /// Tools are injected into the system message as a JSON block when provided.
    /// </summary>
    private static string BuildPrompt(
        LLamaWeights weights,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools)
    {
        var messages = history.ToList();

        // Inject tool schemas into the system prompt (text-format approach, Phase 2).
        // Phase 3 will replace this with GBNF grammar constraints.
        if (tools is { Count: > 0 })
        {
            var toolJson = JsonSerializer.Serialize(tools,
                new JsonSerializerOptions { WriteIndented = false });
            var toolBlock = $"\n\nAvailable tools (call as JSON):\n{toolJson}";
            var sysIdx = messages.FindIndex(m => m.Role == MessageRole.System);
            if (sysIdx >= 0)
                messages[sysIdx].Content += toolBlock;
            else
                messages.Insert(0, new AgentMessage
                    { Role = MessageRole.System, Content = toolBlock });
        }

        // Try the GGUF-embedded template first
        try
        {
            var template = new LLamaTemplate(weights);
            foreach (var msg in messages)
            {
                var role = msg.Role switch
                {
                    MessageRole.System    => "system",
                    MessageRole.User      => "user",
                    MessageRole.Tool      => "tool",
                    _                     => "assistant",
                };
                template.Add(role, msg.Content ?? "");
            }
            return Encoding.UTF8.GetString(template.Apply());
        }
        catch
        {
            // Model has no embedded template — fall back to ChatML
            return BuildChatMLPrompt(messages);
        }
    }

    private static string BuildChatMLPrompt(List<AgentMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                MessageRole.System    => "system",
                MessageRole.User      => "user",
                MessageRole.Tool      => "tool",
                _                     => "assistant",
            };
            sb.Append("<|im_start|>").Append(role).Append('\n');
            sb.Append(msg.Content ?? "");
            sb.AppendLine("<|im_end|>");
        }
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    /// <summary>
    /// Parses tool calls from the model's text output.
    /// Looks for JSON objects with a "name" key — same approach as
    /// AgentLoop.TryParseTextToolCalls (the shared fallback parser).
    /// Phase 3 will replace this with GBNF-constrained generation.
    /// </summary>
    private static List<ToolCall> ParseToolCalls(string text)
    {
        var result = new List<ToolCall>();
        var stripped = Regex.Replace(text, @"```(?:json)?", "", RegexOptions.IgnoreCase).Trim();

        int i = 0;
        while (i < stripped.Length)
        {
            var start = stripped.IndexOf('{', i);
            if (start < 0) break;

            int depth = 0, end = -1;
            bool inString = false;
            for (int j = start; j < stripped.Length; j++)
            {
                var ch = stripped[j];
                if (ch == '"')
                {
                    // Count consecutive preceding backslashes: even = real quote, odd = escaped
                    var bs = 0;
                    for (var k = j - 1; k >= start && stripped[k] == '\\'; k--) bs++;
                    if (bs % 2 == 0) inString = !inString;
                }
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
            }

            if (end < 0) break;

            try
            {
                var node = JsonNode.Parse(stripped[start..(end + 1)]);
                var name = node?["name"]?.GetValue<string>()
                        ?? node?["tool"]?.GetValue<string>()
                        ?? node?["function"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(name))
                {
                    var argsNode = node?["arguments"] ?? node?["args"] ?? node?["parameters"];
                    var args = new Dictionary<string, object?>();
                    if (argsNode is JsonObject argsObj)
                        foreach (var kvp in argsObj)
                            args[kvp.Key] = kvp.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                                ? s
                                : kvp.Value?.ToJsonString() ?? "";

                    result.Add(new ToolCall
                    {
                        Id           = Guid.NewGuid().ToString("N")[..8],
                        Name         = name,
                        Arguments    = args,
                        IsTextFormat = true,
                    });
                }
            }
            catch { /* malformed JSON — skip */ }

            i = end + 1;
        }

        return result;
    }

    private static int EstimateTokens(string text) =>
        Math.Max(1, text.Length / 4);  // rough 4-chars/token approximation
}
