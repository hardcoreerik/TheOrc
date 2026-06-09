using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core;

/// <summary>
/// Streams completions from a local inference server using the OpenAI-compatible
/// /v1/chat/completions endpoint. Works with both Ollama and llama.cpp server.
///
/// The streaming endpoint is identical for both backends. The only divergence
/// is model discovery: Ollama uses /api/tags, llama.cpp uses /v1/models.
/// Set <see cref="Backend"/> to switch between them.
/// </summary>
public class OllamaClient
{
    private readonly HttpClient _http;
    private string _baseUrl;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented               = false,
        DefaultIgnoreCondition      = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Base URL of the inference server (Ollama or llama.cpp).</summary>
    public string Host
    {
        get => _baseUrl;
        set => _baseUrl = value.TrimEnd('/');
    }

    /// <summary>
    /// Controls which model-list endpoint is used.
    /// Does NOT affect completion streaming (always /v1/chat/completions).
    /// </summary>
    public InferenceBackend Backend { get; set; } = InferenceBackend.Ollama;

    public OllamaClient(string host = "http://localhost:11434",
                        InferenceBackend backend = InferenceBackend.Ollama)
    {
        _baseUrl = host.TrimEnd('/');
        Backend  = backend;
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
    }

    // ── Model discovery ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of available model IDs from the inference server.
    /// Ollama   → GET /api/tags          (returns models[].name)
    /// LlamaCpp → GET /v1/models         (returns data[].id)
    /// </summary>
    public virtual Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default)
        => Backend == InferenceBackend.LlamaCpp
            ? GetLlamaCppModelsAsync(ct)
            : GetOllamaModelsAsync(ct);

    private async Task<List<string>> GetOllamaModelsAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            resp.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            return json?["models"]?.AsArray()
                .Select(m => m?["name"]?.GetValue<string>() ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [];
        }
        catch { return []; }
    }

    private async Task<List<string>> GetLlamaCppModelsAsync(CancellationToken ct)
    {
        try
        {
            // llama-server exposes OpenAI-compatible GET /v1/models
            var resp = await _http.GetAsync($"{_baseUrl}/v1/models", ct);
            resp.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            return json?["data"]?.AsArray()
                .Select(m => m?["id"]?.GetValue<string>() ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Quick connectivity check — returns true if the server answers /health (llama.cpp)
    /// or /api/tags (Ollama) within 3 seconds.
    /// </summary>
    public virtual async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var url = Backend == InferenceBackend.LlamaCpp
                ? $"{_baseUrl}/health"
                : $"{_baseUrl}/api/tags";
            var resp = await _http.GetAsync(url, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Model lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of models currently loaded in VRAM via Ollama's /api/ps endpoint.
    /// Returns empty list on non-Ollama backends or if the request fails.
    /// </summary>
    public async Task<List<string>> GetLoadedModelsAsync(CancellationToken ct = default)
    {
        if (Backend != InferenceBackend.Ollama) return [];
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/ps", ct);
            if (!resp.IsSuccessStatusCode) return [];
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            return json?["models"]?.AsArray()
                .Select(m => m?["name"]?.GetValue<string>() ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [];
        }
        catch { return []; }
    }

    /// <summary>
    /// Tells Ollama to immediately evict <paramref name="model"/> from VRAM.
    /// No-ops silently on non-Ollama backends or if the request fails.
    /// Call this after a researcher phase completes when the researcher model
    /// differs from the coder model, to free VRAM before the larger coder phase loads.
    /// </summary>
    public async Task EvictModelAsync(string model, CancellationToken ct = default)
    {
        if (Backend != InferenceBackend.Ollama) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { model, keep_alive = 0 });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            await _http.PostAsync($"{_baseUrl}/api/generate", content, ct);
        }
        catch { /* non-fatal — VRAM pressure will evict naturally */ }
    }

    /// <summary>
    /// Evicts <paramref name="model"/> and then verifies via /api/ps that it is no longer loaded.
    /// Returns true if the model was confirmed evicted (or was never loaded).
    /// Returns false if it is still present in VRAM after eviction.
    /// Logs the result for diagnostics; never throws.
    /// </summary>
    public async Task<bool> EvictAndVerifyAsync(string model, CancellationToken ct = default)
    {
        if (Backend != InferenceBackend.Ollama) return true;   // assume evicted on other backends
        try
        {
            await EvictModelAsync(model, ct);

            // Give Ollama up to 3s to release VRAM (eviction is async server-side)
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(500, ct);
                var loaded = await GetLoadedModelsAsync(ct);
                if (!loaded.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                                     m.StartsWith(model.Split(':')[0], StringComparison.OrdinalIgnoreCase)))
                    return true;   // confirmed evicted
            }

            // Still loaded after 3s — eviction may be deferred under load
            return false;
        }
        catch { return true; }  // assume evicted if /api/ps is unreachable
    }

    // ── Streaming completion ─────────────────────────────────────────────────

    /// <summary>
    /// Streams content tokens. Fires onToolCall when a complete tool call is parsed.
    /// Fires onUsage(promptTokens, completionTokens) at end of response.
    /// Yields each text delta as it arrives.
    /// </summary>
    public virtual async IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,   // promptTokens, completionTokens
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = history.Select(m => new
        {
            role = RoleName(m.Role),
            content = m.Content,
            tool_call_id = m.ToolCallId,
        }).ToList();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
        };

        // tools items are already Ollama-schema objects (built by AgentLoop via
        // ToolDefinition.ToOllamaSchema(), then optionally simplified by SchemaSimplifier)
        if (tools?.Count > 0)
            payload["tools"] = tools;

        var body = new StringContent(
            JsonSerializer.Serialize(payload, _json),
            Encoding.UTF8, "application/json");

        var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = body
        };

        HttpResponseMessage resp;
        string? sendError = null;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) { sendError = ex.Message; resp = null!; }
        if (sendError != null) { yield return $"[ERROR] {sendError}"; yield break; }

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            yield return $"[ERROR {(int)resp.StatusCode}] {err}";
            yield break;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        // Accumulate tool call fragments across chunks
        var toolCallAccum = new Dictionary<int, ToolCallBuilder>();
        int lastPromptTokens = 0, lastCompletionTokens = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;              // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line == "data: [DONE]") break;
            if (!line.StartsWith("data: ")) continue;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(line[6..]); }
            catch { continue; }

            // Track usage (may appear on any chunk, last one wins)
            var usageNode = chunk?["usage"];
            if (usageNode != null)
            {
                lastPromptTokens     = usageNode["prompt_tokens"]?.GetValue<int>()     ?? lastPromptTokens;
                lastCompletionTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? lastCompletionTokens;
            }

            var choices = chunk?["choices"]?.AsArray();
            if (choices == null || choices.Count == 0) continue;

            var delta = choices[0]?["delta"];
            if (delta == null) continue;

            // Text token
            var content = delta["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content))
                yield return content;

            // Tool call fragments
            var tcArray = delta["tool_calls"]?.AsArray();
            if (tcArray != null)
            {
                foreach (var tcChunk in tcArray)
                {
                    var idx = tcChunk?["index"]?.GetValue<int>() ?? 0;
                    if (!toolCallAccum.TryGetValue(idx, out var builder))
                    {
                        builder = new ToolCallBuilder();
                        toolCallAccum[idx] = builder;
                    }
                    builder.Append(tcChunk);
                }
            }

            // Finish reason — flush accumulated tool calls
            var finishReason = choices[0]?["finish_reason"]?.GetValue<string>();
            if (finishReason == "tool_calls" || finishReason == "stop")
            {
                foreach (var (_, builder) in toolCallAccum.OrderBy(kv => kv.Key))
                {
                    var tc = builder.Build();
                    if (tc != null) onToolCall?.Invoke(tc);
                }
                toolCallAccum.Clear();
            }
        }

        // Report usage at end of stream
        if (lastCompletionTokens > 0 || lastPromptTokens > 0)
            onUsage?.Invoke(lastPromptTokens, lastCompletionTokens);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string RoleName(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.System => "system",
        MessageRole.Tool => "tool",
        _ => "user"
    };

    private class ToolCallBuilder
    {
        private string? _id;
        private readonly StringBuilder _nameBuilder = new();
        private readonly StringBuilder _argsBuilder = new();

        public void Append(JsonNode? chunk)
        {
            if (chunk == null) return;
            if (chunk["id"]?.GetValue<string>() is { } id) _id = id;
            _nameBuilder.Append(chunk["function"]?["name"]?.GetValue<string>());
            _argsBuilder.Append(chunk["function"]?["arguments"]?.GetValue<string>());
        }

        public ToolCall? Build()
        {
            var name = _nameBuilder.ToString();
            if (string.IsNullOrEmpty(name)) return null;

            Dictionary<string, object?> args = [];
            try
            {
                var raw = _argsBuilder.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw, _json) ?? [];
            }
            catch { /* malformed args — use empty */ }

            return new ToolCall { Id = _id ?? Guid.NewGuid().ToString("N")[..8], Name = name, Arguments = args };
        }
    }
}

// ── Tool definition schema ───────────────────────────────────────────────────

public class ToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, ToolParameter> Parameters { get; set; } = [];
    public string[] Required { get; set; } = [];
    public bool RequiresApproval { get; set; } = false;
    public Func<Dictionary<string, object?>, CancellationToken, Task<string>>? Handler { get; set; }

    public object ToOllamaSchema() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = Description,
            parameters = new
            {
                type = "object",
                properties = Parameters.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new { type = kv.Value.Type, description = kv.Value.Description }),
                required = Required,
            }
        }
    };
}

public record ToolParameter(string Type, string Description);
