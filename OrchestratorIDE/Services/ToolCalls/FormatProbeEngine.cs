// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Data models ──────────────────────────────────────────────────────────────

/// <summary>Which serialization format the model uses for tool calls.</summary>
public enum FormatVariant
{
    BareJson,      // {"name": "...", "arguments": {...}}   — most common fallback
    OpenAiJson,    // native tools[] array → structured tool_calls response
    HermesXml,     // <tool_call>{"name":"...","arguments":{...}}</tool_call>
    PythonStyle,   // tool_name(arg="value", arg2=42)
    YamlBlock,     // tool: name\nargs:\n  key: value
}

/// <summary>Score for one format variant: did it work, what was parsed, why it failed.</summary>
public record FormatScore(bool Passed, string? ParsedOutput, string? FailReason);

/// <summary>
/// Per-model result of a format probe run.
/// Stored in ToolCallProfile and read by AgentLoop to shape the system prompt.
/// </summary>
public record FormatFingerprint(
    FormatVariant                    PreferredFormat,
    Dictionary<string, FormatScore>  Scores,
    DateTime                         TestedAt
)
{
    public string ShortSummary =>
        $"{PreferredFormat} ({Scores.Values.Count(s => s.Passed)}/{Scores.Count} pass)";
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// GOBLIN MIND Phase 1 — Behavioral Format Fingerprinting.
///
/// Sends the same echo request in 5 serialization format variants and
/// measures which formats the model can reliably produce. Produces a
/// FormatFingerprint that AgentLoop uses to shape the tool-call system
/// prompt for text-JSON mode sessions.
///
/// 5 queries total, &lt;1 minute per model.
/// </summary>
public class FormatProbeEngine
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public FormatProbeEngine(string ollamaHost = "http://localhost:11434")
    {
        _baseUrl = ollamaHost.TrimEnd('/');
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ── Public entry ─────────────────────────────────────────────────────────

    public async Task<FormatFingerprint> RunAsync(
        string model,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var scores = new Dictionary<string, FormatScore>();

        foreach (var variant in Enum.GetValues<FormatVariant>())
        {
            onProgress?.Invoke($"  [Format] {variant}…");
            try
            {
                using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts2.CancelAfter(TimeSpan.FromSeconds(45));

                var score = await ProbeFormatAsync(model, variant, cts2.Token);
                scores[variant.ToString()] = score;
                var icon = score.Passed ? "✓" : "✗";
                onProgress?.Invoke($"    {icon} {variant} — {(score.Passed ? score.ParsedOutput : score.FailReason)}");
            }
            catch (OperationCanceledException)
            {
                scores[variant.ToString()] = new FormatScore(false, null, "timeout");
                onProgress?.Invoke($"    ⏱ {variant} TIMEOUT");
            }
            catch (Exception ex)
            {
                scores[variant.ToString()] = new FormatScore(false, null, ex.Message[..Math.Min(80, ex.Message.Length)]);
                onProgress?.Invoke($"    ✗ {variant} ERROR");
            }
        }

        var preferred = PickPreferred(scores);
        onProgress?.Invoke($"  → Preferred format: {preferred}");
        return new FormatFingerprint(preferred, scores, DateTime.UtcNow);
    }

    // ── Single format probe ───────────────────────────────────────────────────

    private async Task<FormatScore> ProbeFormatAsync(string model, FormatVariant variant, CancellationToken ct)
    {
        const string toolName   = "echo";
        const string probeToken = "fp_probe";

        var (systemPrompt, sendTools) = BuildFormatPrompt(variant, toolName);

        var payload = new Dictionary<string, object?>
        {
            ["model"]       = model,
            ["stream"]      = false,
            ["temperature"] = 0.0,
            ["max_tokens"]  = 200,
            ["messages"]    = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user",   content = $"Call the {toolName} tool with message \"{probeToken}\"." },
            },
        };

        if (sendTools)
        {
            payload["tools"] = new[]
            {
                new
                {
                    type     = "function",
                    function = new
                    {
                        name        = toolName,
                        description = "Echo a message back.",
                        parameters  = new
                        {
                            type       = "object",
                            properties = new { message = new { type = "string", description = "Message to echo." } },
                            required   = new[] { "message" }
                        }
                    }
                }
            };
        }

        var body = new StringContent(JsonSerializer.Serialize(payload, _jsonOpts), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_baseUrl}/v1/chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();

        var json  = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));

        // Check for native tool_calls array (OpenAI native mode)
        var nativeTc = json?["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
        if (nativeTc?.Count > 0)
        {
            var name = nativeTc[0]?["function"]?["name"]?.GetValue<string>() ?? "";
            if (name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                return new FormatScore(true, $"{toolName}(native-struct)", null);
        }

        var content = json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";

        return variant switch
        {
            FormatVariant.OpenAiJson  or
            FormatVariant.BareJson    => ParseBareJson(content, toolName),
            FormatVariant.HermesXml   => ParseHermesXml(content, toolName),
            FormatVariant.PythonStyle => ParsePython(content, toolName),
            FormatVariant.YamlBlock   => ParseYaml(content, toolName),
            _                         => new FormatScore(false, null, "unknown variant")
        };
    }

    // ── Format-specific system prompts ────────────────────────────────────────

    private static (string SystemPrompt, bool SendToolsArray) BuildFormatPrompt(FormatVariant variant, string toolName) => variant switch
    {
        FormatVariant.OpenAiJson => (
            "You are a tool-calling assistant. Use the provided tools to complete every request. " +
            "Respond with a single JSON object: {\"name\": \"<tool>\", \"arguments\": {\"<arg>\": <value>}}. " +
            "Output ONLY the JSON. No markdown, no explanation.",
            false),

        FormatVariant.BareJson => (
            "You are a tool-calling assistant. To call a tool output a bare JSON object:\n" +
            "{\"name\": \"<tool_name>\", \"arguments\": {\"<arg>\": <value>}}\n" +
            "Output ONLY the JSON object. No markdown fences, no explanation.",
            false),

        FormatVariant.HermesXml => (
            "You are a tool-calling assistant. Call tools using this XML format:\n" +
            "<tool_call>\n" +
            "{\"name\": \"<tool_name>\", \"arguments\": {\"<arg>\": <value>}}\n" +
            "</tool_call>\n" +
            "Output ONLY the <tool_call>…</tool_call> block. No explanation.",
            false),

        FormatVariant.PythonStyle => (
            $"You are a tool-calling assistant. To call a tool, output a Python-style function call:\n" +
            $"tool_name(argument_name=\"value\")\n" +
            $"Output ONLY the function call on a single line. No explanation.",
            false),

        FormatVariant.YamlBlock => (
            "You are a tool-calling assistant. To call a tool, output a YAML block:\n" +
            "tool: tool_name\n" +
            "args:\n" +
            "  argument_name: value\n" +
            "Output ONLY the YAML block. No explanation.",
            false),

        _ => ("You are a tool-calling assistant.", false)
    };

    // ── Per-format parsers ────────────────────────────────────────────────────

    private static FormatScore ParseBareJson(string content, string expectedTool)
    {
        var stripped = Regex.Replace(content, @"```(?:json)?", "", RegexOptions.IgnoreCase).Trim();
        try
        {
            var start = stripped.IndexOf('{');
            if (start < 0) return Fail("no JSON found");
            var node = JsonNode.Parse(stripped[start..]);
            var name = node?["name"]?.GetValue<string>()
                    ?? node?["tool"]?.GetValue<string>()
                    ?? node?["function"]?.GetValue<string>();
            return name?.Equals(expectedTool, StringComparison.OrdinalIgnoreCase) == true
                ? new FormatScore(true, $"{{\"name\":\"{name}\",...}}", null)
                : Fail($"tool name mismatch: '{name}'");
        }
        catch (Exception ex) { return Fail($"JSON parse: {ex.Message[..Math.Min(50, ex.Message.Length)]}"); }
    }

    private static FormatScore ParseHermesXml(string content, string expectedTool)
    {
        var match = Regex.Match(content, @"<tool_call>\s*([\s\S]*?)\s*</tool_call>", RegexOptions.IgnoreCase);
        if (!match.Success)
            return ParseBareJson(content, expectedTool);   // graceful fallback
        var inner = match.Groups[1].Value.Trim();
        try
        {
            var node = JsonNode.Parse(inner);
            var name = node?["name"]?.GetValue<string>() ?? node?["tool"]?.GetValue<string>();
            return name?.Equals(expectedTool, StringComparison.OrdinalIgnoreCase) == true
                ? new FormatScore(true, $"<tool_call>{name}</tool_call>", null)
                : Fail($"name inside XML: '{name}'");
        }
        catch { return Fail("could not parse JSON inside <tool_call>"); }
    }

    private static FormatScore ParsePython(string content, string expectedTool)
    {
        var match = Regex.Match(content.Trim(), @"^(\w+)\s*\(", RegexOptions.Multiline);
        if (!match.Success) return Fail("no Python-style call found");
        var name = match.Groups[1].Value;
        return name.Equals(expectedTool, StringComparison.OrdinalIgnoreCase)
            ? new FormatScore(true, $"{name}(...)", null)
            : Fail($"got '{name}', expected '{expectedTool}'");
    }

    private static FormatScore ParseYaml(string content, string expectedTool)
    {
        var match = Regex.Match(content, @"^(?:tool|name):\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!match.Success) return Fail("no YAML 'tool:' key found");
        var name = match.Groups[1].Value.Trim();
        return name.Equals(expectedTool, StringComparison.OrdinalIgnoreCase)
            ? new FormatScore(true, $"tool: {name}", null)
            : Fail($"got '{name}', expected '{expectedTool}'");
    }

    private static FormatScore Fail(string reason) => new(false, null, reason);

    // ── Preferred format selection ─────────────────────────────────────────────

    private static FormatVariant PickPreferred(Dictionary<string, FormatScore> scores)
    {
        // Reliability priority: BareJson > OpenAiJson > HermesXml > YamlBlock > PythonStyle
        var priority = new[] { FormatVariant.BareJson, FormatVariant.OpenAiJson, FormatVariant.HermesXml, FormatVariant.YamlBlock, FormatVariant.PythonStyle };
        foreach (var v in priority)
            if (scores.TryGetValue(v.ToString(), out var s) && s.Passed)
                return v;
        return FormatVariant.BareJson; // default even if none passed
    }

    // ── System prompt builder (called by AgentLoop) ───────────────────────────

    /// <summary>
    /// Builds the TOOL CALL FORMAT section of the execute system prompt
    /// using the model's proven preferred format. Replaces the hardcoded
    /// BareJson section in BuildExecuteSystemPrompt.
    /// </summary>
    public static string BuildToolFormatSection(FormatVariant preferred) => preferred switch
    {
        FormatVariant.HermesXml =>
            """

            TOOL CALL FORMAT (use this format exactly):
            Wrap every tool call in <tool_call>...</tool_call> XML:
            <tool_call>
            {"name": "write_file", "arguments": {"path": "Calculator.cs", "content": "..."}}
            </tool_call>
            Output ONLY the <tool_call> block — no explanation before or after it.
            """,

        FormatVariant.PythonStyle =>
            """

            TOOL CALL FORMAT (use this format exactly):
            Call tools using Python-style function calls on a single line:
            write_file(path="Calculator.cs", content="public class Calculator {...}")
            read_file(path="Program.cs")
            Output ONLY the function call — no explanation, no markdown.
            """,

        FormatVariant.YamlBlock =>
            """

            TOOL CALL FORMAT (use this format exactly):
            Call tools using a YAML block:
            tool: write_file
            args:
              path: Calculator.cs
              content: "public class Calculator { ... }"
            Output ONLY the YAML block — no explanation.
            """,

        _ =>   // BareJson, OpenAiJson, or default
            """

            TOOL CALL FORMAT (critical — follow exactly):
            To use a tool, output a raw JSON object on its own line. No markdown, no explanation before or after it.

            Examples:
            {"name": "write_file", "arguments": {"path": "Calculator.cs", "content": "public class Calculator\n{\n    public double Add(double a, double b) => a + b;\n}"}}
            {"name": "read_file", "arguments": {"path": "Program.cs"}}
            {"name": "list_files", "arguments": {"path": ".", "depth": 2}}
            """
    };
}
