using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core;

/// <summary>
/// Streams completions from Ollama using the OpenAI-compatible /v1/chat/completions endpoint.
/// Yields tokens as they arrive. Supports tool call parsing.
/// </summary>
public class OllamaClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public OllamaClient(string ollamaHost = "http://localhost:11434")
    {
        _baseUrl = ollamaHost.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
    }

    // ── Model discovery ──────────────────────────────────────────────────────

    public async Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default)
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

    // ── Streaming completion ─────────────────────────────────────────────────

    /// <summary>
    /// Streams content tokens. Fires onToolCall when a complete tool call is parsed.
    /// Yields each text delta as it arrives.
    /// </summary>
    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<ToolDefinition>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
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

        if (tools?.Count > 0)
            payload["tools"] = tools.Select(t => t.ToOllamaSchema()).ToList();

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
