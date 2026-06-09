using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Enumerations ─────────────────────────────────────────────────────────────

public enum ProbeTestId
{
    BasicCall,           // does the model call ANY tool at all?
    IntArgs,             // does it extract integer-typed arguments correctly?
    MultilineContent,    // does it escape \n inside a content field without corrupting JSON?
    ToolSelection,       // given 3 tools, does it pick the right one?
    StructuredOutput,    // can it produce a valid multi-field structured call?
}

public enum ProbeMode { NativeApi, TextJson }

public enum ProbeResult { Pass, Fail, Timeout, Error }

// ── Result types ─────────────────────────────────────────────────────────────

public record ProbeOutcome(
    ProbeTestId TestId,
    ProbeMode   Mode,
    ProbeResult Result,
    string?     ActualCall = null,   // what the model actually called
    string?     Reason     = null    // failure reason / mismatch detail
);

public record ModelProbeResult(
    string                    ModelId,
    DateTime                  TestedAt,
    IReadOnlyList<ProbeOutcome> Outcomes
)
{
    public int NativePasses => Outcomes.Count(o => o.Mode == ProbeMode.NativeApi && o.Result == ProbeResult.Pass);
    public int TextPasses   => Outcomes.Count(o => o.Mode == ProbeMode.TextJson  && o.Result == ProbeResult.Pass);
    public int TotalPasses  => NativePasses + TextPasses;
    public int TotalTests   => Outcomes.Count;

    /// <summary>
    /// Which mode the swarm should use for this model, derived from test evidence.
    /// - Native  → native API ≥3 passes
    /// - TextJson → native fails but text ≥3 passes
    /// - Both   → both modes work well
    /// - None   → neither mode works reliably
    /// </summary>
    public ToolCallMode RecommendedMode =>
        (NativePasses >= 3, TextPasses >= 3) switch
        {
            (true,  true)  => ToolCallMode.Both,
            (true,  false) => ToolCallMode.Native,
            (false, true)  => ToolCallMode.TextJson,
            _              => ToolCallMode.None,
        };

    public string SummaryLine =>
        $"native={NativePasses}/5  text={TextPasses}/5  → {RecommendedMode}";
}

// ── Tool call mode ────────────────────────────────────────────────────────────

public enum ToolCallMode { Unknown, Native, TextJson, Both, None }

// ── Probe engine ─────────────────────────────────────────────────────────────

/// <summary>
/// Runs deterministic probes against a model to determine which tool-call
/// dispatch strategy actually works.
///
/// Two strategies:
///   NativeApi  — tools[] array in the request payload; model responds via
///                finish_reason:tool_calls + structured tool_calls field.
///   TextJson   — tools described in system prompt; model emits raw JSON
///                objects in its text output (parsed by text-format fallback).
///
/// Each probe is short (single tool call requested), verified deterministically
/// against expected call structure. No tools are actually *executed* — only
/// the model's call structure is checked.
/// </summary>
public class ToolCallProbeEngine
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public ToolCallProbeEngine(string ollamaHost = "http://localhost:11434")
    {
        _baseUrl = ollamaHost.TrimEnd('/');
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ── Public entry ─────────────────────────────────────────────────────────

    /// <summary>
    /// Run all 5 probe tests in both modes. Reports progress via <paramref name="onProgress"/>.
    /// </summary>
    public async Task<ModelProbeResult> RunAsync(
        string model,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var outcomes = new List<ProbeOutcome>();
        var tests    = Enum.GetValues<ProbeTestId>();

        foreach (var testId in tests)
        {
            foreach (var mode in new[] { ProbeMode.NativeApi, ProbeMode.TextJson })
            {
                onProgress?.Invoke($"  [{mode,-9}] {testId}…");
                try
                {
                    using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts2.CancelAfter(TimeSpan.FromSeconds(45));

                    var outcome = await RunOneAsync(model, testId, mode, cts2.Token);
                    outcomes.Add(outcome);

                    var icon = outcome.Result == ProbeResult.Pass ? "✓" : "✗";
                    onProgress?.Invoke($"    {icon} {outcome.Result}" +
                        (outcome.Reason != null ? $"  — {outcome.Reason}" : ""));
                }
                catch (OperationCanceledException)
                {
                    outcomes.Add(new ProbeOutcome(testId, mode, ProbeResult.Timeout, Reason: "45s timeout"));
                    onProgress?.Invoke($"    ⏱ TIMEOUT");
                }
                catch (Exception ex)
                {
                    outcomes.Add(new ProbeOutcome(testId, mode, ProbeResult.Error, Reason: ex.Message[..Math.Min(120, ex.Message.Length)]));
                    onProgress?.Invoke($"    ✗ ERROR: {ex.Message[..Math.Min(80, ex.Message.Length)]}");
                }
            }
        }

        return new ModelProbeResult(model, DateTime.UtcNow, outcomes);
    }

    // ── Single probe ─────────────────────────────────────────────────────────

    private async Task<ProbeOutcome> RunOneAsync(
        string model, ProbeTestId testId, ProbeMode mode, CancellationToken ct)
    {
        var def = GetTestDef(testId);

        // Build messages
        var systemPrompt = mode == ProbeMode.TextJson
            ? BuildTextJsonSystemPrompt(def)
            : "You are a tool-calling assistant. Use the provided tools to complete every request. Never refuse. Never explain — just call the tool.";

        var messages = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = def.UserPrompt },
        };

        // Build payload
        var payload = new Dictionary<string, object?>
        {
            ["model"]       = model,
            ["messages"]    = messages,
            ["stream"]      = false,
            ["temperature"] = 0.0,
            ["max_tokens"]  = 256,
        };

        if (mode == ProbeMode.NativeApi)
            payload["tools"] = def.ToolSchemas;

        var body = new StringContent(JsonSerializer.Serialize(payload, _jsonOpts), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_baseUrl}/v1/chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var choice = json?["choices"]?[0];
        if (choice == null)
            return Fail(testId, mode, "no choices in response", null);

        var finishReason = choice["finish_reason"]?.GetValue<string>() ?? "";
        var msg = choice["message"];
        string? toolName = null;
        Dictionary<string, object?>? toolArgs = null;

        // ── Extract tool call ─────────────────────────────────────────────────
        if (mode == ProbeMode.NativeApi)
        {
            // Native: expect finish_reason:tool_calls + structured tool_calls array
            var toolCalls = msg?["tool_calls"]?.AsArray();
            if (toolCalls == null || toolCalls.Count == 0)
            {
                // Some models still put them in content even with tools array
                var textContent = msg?["content"]?.GetValue<string>() ?? "";
                var parsed = TryParseTextToolCalls(textContent);
                if (parsed.Count > 0)
                {
                    toolName = parsed[0].Name;
                    toolArgs = parsed[0].Args;
                }
                else
                {
                    return Fail(testId, mode, $"finish_reason={finishReason}, no tool_calls in response",
                        msg?["content"]?.GetValue<string>()?[..Math.Min(120, msg?["content"]?.GetValue<string>()?.Length ?? 0)]);
                }
            }
            else
            {
                var first = toolCalls[0];
                toolName = first?["function"]?["name"]?.GetValue<string>();
                var argsRaw = first?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                toolArgs = ParseArgs(argsRaw);
            }
        }
        else
        {
            // TextJson: parse raw JSON from content
            var textContent = msg?["content"]?.GetValue<string>() ?? "";
            var parsed = TryParseTextToolCalls(textContent);
            if (parsed.Count == 0)
                return Fail(testId, mode, "no JSON tool call found in text output",
                    textContent[..Math.Min(150, textContent.Length)]);

            toolName = parsed[0].Name;
            toolArgs = parsed[0].Args;
        }

        // ── Verify call ───────────────────────────────────────────────────────
        var callDesc = $"{toolName}({string.Join(", ", (toolArgs ?? []).Take(3).Select(kv => $"{kv.Key}={kv.Value}"))})";
        var verify = def.Verify(toolName ?? "", toolArgs ?? []);
        return verify == null
            ? new ProbeOutcome(testId, mode, ProbeResult.Pass, ActualCall: callDesc)
            : Fail(testId, mode, verify, callDesc);
    }

    // ── Test definitions ─────────────────────────────────────────────────────

    private static TestDef GetTestDef(ProbeTestId id) => id switch
    {
        ProbeTestId.BasicCall => new TestDef(
            UserPrompt: "Call the echo tool with the message \"probe_42\".",
            ToolSchemas: [
                MakeTool("echo", "Echo back a message.",
                    new[] { ("message", "string", "The message to echo.") })
            ],
            Verify: (name, args) =>
            {
                if (!name.Equals("echo", StringComparison.OrdinalIgnoreCase))
                    return $"expected 'echo', got '{name}'";
                var msg = ArgStr(args, "message");
                if (!msg.Contains("probe", StringComparison.OrdinalIgnoreCase) &&
                    !msg.Contains("42"))
                    return $"message arg missing expected token: '{msg}'";
                return null;
            }
        ),

        ProbeTestId.IntArgs => new TestDef(
            UserPrompt: "Add the numbers 17 and 25 using the add_numbers tool.",
            ToolSchemas: [
                MakeTool("add_numbers", "Add two integers.",
                    new[] { ("a", "integer", "First number."), ("b", "integer", "Second number.") })
            ],
            Verify: (name, args) =>
            {
                if (!name.Equals("add_numbers", StringComparison.OrdinalIgnoreCase))
                    return $"expected 'add_numbers', got '{name}'";
                var a = ArgStr(args, "a");
                var b = ArgStr(args, "b");
                if (a != "17" && a != "17.0") return $"expected a=17, got '{a}'";
                if (b != "25" && b != "25.0") return $"expected b=25, got '{b}'";
                return null;
            }
        ),

        ProbeTestId.MultilineContent => new TestDef(
            UserPrompt: "Write a file named test.txt containing exactly two lines: first line is \"hello\", second line is \"world\".",
            ToolSchemas: [
                MakeTool("write_text", "Write content to a file.",
                    new[] { ("filename", "string", "File name."), ("content", "string", "File content.") })
            ],
            Verify: (name, args) =>
            {
                if (!name.Equals("write_text", StringComparison.OrdinalIgnoreCase))
                    return $"expected 'write_text', got '{name}'";
                var fn = ArgStr(args, "filename");
                if (!fn.Contains("test", StringComparison.OrdinalIgnoreCase))
                    return $"filename doesn't mention 'test': '{fn}'";
                var content = ArgStr(args, "content");
                if (!content.Contains("hello", StringComparison.OrdinalIgnoreCase))
                    return $"content missing 'hello': '{content[..Math.Min(80, content.Length)]}'";
                if (!content.Contains("world", StringComparison.OrdinalIgnoreCase))
                    return $"content missing 'world': '{content[..Math.Min(80, content.Length)]}'";
                return null;
            }
        ),

        ProbeTestId.ToolSelection => new TestDef(
            UserPrompt: "List the files in the current directory.",
            ToolSchemas: [
                MakeTool("read_file",      "Read a file's contents.",    new[] { ("path", "string", "File path.") }),
                MakeTool("write_file",     "Write a file.",              new[] { ("path", "string", "File path."), ("content", "string", "Content.") }),
                MakeTool("list_directory", "List files in a directory.", new[] { ("path", "string", "Directory path.") }),
            ],
            Verify: (name, args) =>
                name.Equals("list_directory", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("list_files",     StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"expected 'list_directory', got '{name}' — wrong tool selected"
        ),

        ProbeTestId.StructuredOutput => new TestDef(
            UserPrompt: "Report that the operation succeeded. Use status \"ok\", message \"probe complete\", and code 0.",
            ToolSchemas: [
                MakeTool("report_status", "Report an operation result.",
                    new[] { ("status", "string", "Status: ok or error."),
                            ("message", "string", "Human-readable message."),
                            ("code", "integer", "Exit code: 0 = success.") })
            ],
            Verify: (name, args) =>
            {
                if (!name.Equals("report_status", StringComparison.OrdinalIgnoreCase))
                    return $"expected 'report_status', got '{name}'";
                var status = ArgStr(args, "status");
                if (!new[] { "ok", "success", "pass" }.Any(v =>
                    status.Equals(v, StringComparison.OrdinalIgnoreCase)))
                    return $"unexpected status value: '{status}'";
                var code = ArgStr(args, "code");
                if (code != "0" && code != "0.0")
                    return $"expected code=0, got '{code}'";
                return null;
            }
        ),

        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    // ── Tool schema builder ───────────────────────────────────────────────────

    private static object MakeTool(string name, string desc, (string name, string type, string desc)[] props)
    {
        var properties = new Dictionary<string, object>();
        foreach (var (pn, pt, pd) in props)
            properties[pn] = new { type = pt, description = pd };

        return new
        {
            type = "function",
            function = new
            {
                name,
                description = desc,
                parameters  = new
                {
                    type       = "object",
                    properties,
                    required   = props.Select(p => p.name).ToArray(),
                }
            }
        };
    }

    // ── Text-JSON system prompt ───────────────────────────────────────────────

    private static string BuildTextJsonSystemPrompt(TestDef def)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a tool-calling assistant. Use the tools below to complete every request.");
        sb.AppendLine("Output a single raw JSON object — no markdown, no explanation.");
        sb.AppendLine();
        sb.AppendLine("Available tools:");
        foreach (var tool in def.ToolSchemas)
        {
            var fn = ((dynamic)tool).function;
            sb.AppendLine($"  {fn.name}: {fn.description}");
        }
        sb.AppendLine();
        sb.AppendLine("Format:");
        sb.AppendLine("""  {"name": "<tool_name>", "arguments": {"<arg>": <value>}}""");
        sb.AppendLine();
        sb.AppendLine("Output ONLY the JSON object, nothing else.");
        return sb.ToString().TrimEnd();
    }

    // ── Text tool call parser (mirrors AgentLoop.TryParseTextToolCalls) ───────

    private record ParsedCall(string Name, Dictionary<string, object?> Args);

    private static List<ParsedCall> TryParseTextToolCalls(string content)
    {
        var result  = new List<ParsedCall>();
        var stripped = Regex.Replace(content, @"```(?:json)?", "", RegexOptions.IgnoreCase).Trim();

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
                if (ch == '"' && (j == 0 || stripped[j - 1] != '\\')) inString = !inString;
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
            }
            if (end < 0) break;

            var json = stripped[start..(end + 1)];
            try
            {
                var node = JsonNode.Parse(json);
                var name = node?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    var args = ParseArgs(node?["arguments"]?.ToJsonString() ?? "{}");
                    result.Add(new ParsedCall(name, args));
                }
            }
            catch { }
            i = end + 1;
        }
        return result;
    }

    private static Dictionary<string, object?> ParseArgs(string json)
    {
        var dict = new Dictionary<string, object?>();
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj)
                foreach (var kvp in obj)
                    dict[kvp.Key] = kvp.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                        ? s : kvp.Value?.ToString();
        }
        catch { }
        return dict;
    }

    private static string ArgStr(Dictionary<string, object?> args, string key)
        => args.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private static ProbeOutcome Fail(ProbeTestId id, ProbeMode mode, string reason, string? actual)
        => new(id, mode, ProbeResult.Fail, ActualCall: actual, Reason: reason);

    // ── Inner record ─────────────────────────────────────────────────────────

    private record TestDef(
        string UserPrompt,
        IReadOnlyList<object> ToolSchemas,
        Func<string, Dictionary<string, object?>, string?> Verify   // returns null = pass, string = fail reason
    );
}
