// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Data models ──────────────────────────────────────────────────────────────

/// <summary>Task category a model may or may not handle reliably.</summary>
public enum CategoryId
{
    FileOps,          // C1 — read, write, delete files
    Network,          // C2 — HTTP GET/POST, fetch URLs
    CodeExec,         // C3 — run shell commands, eval code
    DataTransform,    // C4 — parse, format, convert data
    SystemInspect,    // C5 — env vars, process list, system info
    StructuredOutput, // C6 — produce multi-field structured JSON
    TaskPlanning,     // C7 — break goal into ordered steps / create tasks
}

public enum CategoryResult { Pass, Partial, Fail }

/// <summary>Score for one category: how many tests passed and a short note.</summary>
public record CategoryScore(
    CategoryResult Result,
    int            TestsPassed,
    int            TotalTests,
    string?        Notes = null
);

/// <summary>
/// Per-model result of a category boundary probe.
/// Stored in ToolCallProfile and read by SwarmSession to gate task routing.
/// </summary>
public record CategoryBoundaryMap(
    Dictionary<string, CategoryScore> Categories,
    DateTime                          TestedAt
)
{
    public int PassCount    => Categories.Values.Count(s => s.Result == CategoryResult.Pass);
    public int PartialCount => Categories.Values.Count(s => s.Result == CategoryResult.Partial);
    public int FailCount    => Categories.Values.Count(s => s.Result == CategoryResult.Fail);
    public int TotalCount   => Categories.Count;

    /// <summary>Compact display: "5/7" with partial shown as ½.</summary>
    public string ShortSummary => $"{PassCount}/{TotalCount}";

    /// <summary>True if the model can handle the given category reliably.</summary>
    public bool CanHandle(CategoryId cat) =>
        Categories.TryGetValue(cat.ToString(), out var s)
        && s.Result != CategoryResult.Fail;

    /// <summary>True if the model can handle ALL categories required for a swarm role.</summary>
    public bool MeetsRoleRequirements(IEnumerable<CategoryId> required) =>
        required.All(CanHandle);
}

// ── Required categories per swarm role ───────────────────────────────────────

public static class SwarmRoleRequirements
{
    /// <summary>Categories the Boss/TheOrc role must pass.</summary>
    public static readonly CategoryId[] Boss =
        [CategoryId.StructuredOutput, CategoryId.TaskPlanning];

    /// <summary>Categories the Coder/Worker role must pass.</summary>
    public static readonly CategoryId[] Worker =
        [CategoryId.FileOps, CategoryId.CodeExec];

    /// <summary>Categories the Researcher role must pass.</summary>
    public static readonly CategoryId[] Researcher =
        [CategoryId.Network, CategoryId.DataTransform];
}

// ── Engine ────────────────────────────────────────────────────────────────────

/// <summary>
/// GOBLIN MIND Phase 2 — Category Boundary Mapping.
///
/// Runs 2 tests per category (14 total) to discover which task categories
/// a model handles reliably. The CategoryBoundaryMap is stored in
/// ToolCallProfile and used by SwarmSession to route tasks only to goblins
/// that can handle them.
///
/// 14 queries total, ~1–2 minutes per model.
/// </summary>
public class CategoryProbeEngine
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public CategoryProbeEngine(string ollamaHost = "http://localhost:11434")
    {
        _baseUrl = ollamaHost.TrimEnd('/');
        _http    = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    // ── Public entry ─────────────────────────────────────────────────────────

    public async Task<CategoryBoundaryMap> RunAsync(
        string model,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var categories = new Dictionary<string, CategoryScore>();

        foreach (var catId in Enum.GetValues<CategoryId>())
        {
            onProgress?.Invoke($"  [Category] {catId}…");
            var tests = GetTestDefs(catId);
            int passes = 0;

            for (int i = 0; i < tests.Length; i++)
            {
                try
                {
                    using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts2.CancelAfter(TimeSpan.FromSeconds(45));

                    var pass = await RunOneCategoryTestAsync(model, tests[i], cts2.Token);
                    if (pass) passes++;
                    onProgress?.Invoke($"    {(pass ? "✓" : "✗")} Test {i + 1}: {tests[i].UserPrompt[..Math.Min(60, tests[i].UserPrompt.Length)]}");
                }
                catch (OperationCanceledException)
                {
                    onProgress?.Invoke($"    ⏱ Test {i + 1} TIMEOUT");
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"    ✗ Test {i + 1} ERROR: {ex.Message[..Math.Min(50, ex.Message.Length)]}");
                }
            }

            var result = passes switch { 2 => CategoryResult.Pass, 1 => CategoryResult.Partial, _ => CategoryResult.Fail };
            categories[catId.ToString()] = new CategoryScore(result, passes, tests.Length);
            onProgress?.Invoke($"  → {catId}: {result} ({passes}/{tests.Length})");
        }

        return new CategoryBoundaryMap(categories, DateTime.UtcNow);
    }

    // ── Single test ───────────────────────────────────────────────────────────

    private async Task<bool> RunOneCategoryTestAsync(string model, CategoryTestDef def, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"]       = model,
            ["stream"]      = false,
            ["temperature"] = 0.0,
            ["max_tokens"]  = 256,
            ["messages"]    = new object[]
            {
                new { role = "system", content =
                    "You are a tool-calling assistant. Use the provided tools to complete every request. " +
                    "Respond with a single JSON tool call: {\"name\": \"<tool>\", \"arguments\": {\"<arg>\": <value>}}. " +
                    "Never refuse. Output ONLY the JSON call."
                },
                new { role = "user", content = def.UserPrompt },
            },
            ["tools"] = new[] { def.ToolSchema },
        };

        var body = new StringContent(JsonSerializer.Serialize(payload, _jsonOpts), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_baseUrl}/v1/chat/completions", body, ct);
        resp.EnsureSuccessStatusCode();

        var json  = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var msg   = json?["choices"]?[0]?["message"];

        // Check native tool_calls first
        var nativeTc = msg?["tool_calls"]?.AsArray();
        if (nativeTc?.Count > 0)
        {
            var calledName = nativeTc[0]?["function"]?["name"]?.GetValue<string>() ?? "";
            return def.Verify(calledName, ExtractNativeArgs(nativeTc[0]));
        }

        // Fall back to text parsing
        var content = msg?["content"]?.GetValue<string>() ?? "";
        var calls   = TryParseTextCalls(content);
        return calls.Any(c => def.Verify(c.Name, c.Args));
    }

    // ── Test definitions ─────────────────────────────────────────────────────

    private static CategoryTestDef[] GetTestDefs(CategoryId cat) => cat switch
    {
        CategoryId.FileOps => [
            new CategoryTestDef(
                UserPrompt: "Write the text 'hello world' to a file called output.txt",
                ToolSchema: MakeTool("file_write", "Write content to a file.",
                    ("path", "string", "File path."), ("content", "string", "Content to write.")),
                Verify: (name, args) =>
                    name.Contains("file_write", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("write", StringComparison.OrdinalIgnoreCase)
            ),
            new CategoryTestDef(
                UserPrompt: "Create a new file named config.json with the content {}",
                ToolSchema: MakeTool("file_write", "Write content to a file.",
                    ("path", "string", "File path."), ("content", "string", "Content to write.")),
                Verify: (name, args) =>
                    name.Contains("file_write", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("write", StringComparison.OrdinalIgnoreCase)
            ),
        ],

        CategoryId.Network => [
            new CategoryTestDef(
                UserPrompt: "Fetch the content from https://example.com",
                ToolSchema: MakeTool("http_get", "Fetch content from a URL via HTTP GET.",
                    ("url", "string", "The URL to fetch.")),
                Verify: (name, args) =>
                    name.Contains("http", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("get", StringComparison.OrdinalIgnoreCase)
            ),
            new CategoryTestDef(
                UserPrompt: "Get JSON data from the API at https://api.test.com/users",
                ToolSchema: MakeTool("http_get", "Fetch content from a URL via HTTP GET.",
                    ("url", "string", "The URL to fetch.")),
                Verify: (name, args) =>
                    name.Contains("http", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("get", StringComparison.OrdinalIgnoreCase)
            ),
        ],

        CategoryId.CodeExec => [
            new CategoryTestDef(
                UserPrompt: "Run the shell command: echo hello",
                ToolSchema: MakeTool("run_command", "Execute a shell command and return its output.",
                    ("command", "string", "The shell command to run.")),
                Verify: (name, args) =>
                    (name.Contains("run", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("shell", StringComparison.OrdinalIgnoreCase)) &&
                    args.TryGetValue("command", out var cmd) &&
                    (cmd?.ToString()?.Contains("echo", StringComparison.OrdinalIgnoreCase) == true)
            ),
            new CategoryTestDef(
                UserPrompt: "Execute 'python --version' to check Python installation",
                ToolSchema: MakeTool("run_command", "Execute a shell command and return its output.",
                    ("command", "string", "The shell command to run.")),
                Verify: (name, args) =>
                    name.Contains("run", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("exec", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("shell", StringComparison.OrdinalIgnoreCase)
            ),
        ],

        CategoryId.DataTransform => [
            new CategoryTestDef(
                UserPrompt: "Convert this JSON to YAML format: {\"name\": \"test\", \"value\": 42}",
                ToolSchema: MakeTool("convert_data", "Convert data between formats.",
                    ("input", "string", "Input data."),
                    ("from_format", "string", "Source format (json/csv/yaml/xml)."),
                    ("to_format", "string", "Target format.")),
                Verify: (name, args) =>
                    name.Contains("convert", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("transform", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("parse", StringComparison.OrdinalIgnoreCase)
            ),
            new CategoryTestDef(
                UserPrompt: "Parse the CSV string 'a,b,c\\n1,2,3' and convert it to a JSON array",
                ToolSchema: MakeTool("convert_data", "Convert data between formats.",
                    ("input", "string", "Input data."),
                    ("from_format", "string", "Source format."),
                    ("to_format", "string", "Target format.")),
                Verify: (name, args) =>
                    name.Contains("convert", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("transform", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("parse", StringComparison.OrdinalIgnoreCase)
            ),
        ],

        CategoryId.SystemInspect => [
            new CategoryTestDef(
                UserPrompt: "Read the value of the PATH environment variable",
                ToolSchema: MakeTool("get_env_var", "Get the value of a system environment variable.",
                    ("name", "string", "The name of the environment variable.")),
                Verify: (name, args) =>
                    (name.Contains("env", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("get", StringComparison.OrdinalIgnoreCase)) &&
                    args.TryGetValue("name", out var n) &&
                    (n?.ToString()?.Contains("PATH", StringComparison.OrdinalIgnoreCase) == true ||
                     n?.ToString()?.Contains("path", StringComparison.OrdinalIgnoreCase) == true)
            ),
            new CategoryTestDef(
                UserPrompt: "Get the current value of the HOME environment variable",
                ToolSchema: MakeTool("get_env_var", "Get the value of a system environment variable.",
                    ("name", "string", "The name of the environment variable.")),
                Verify: (name, args) =>
                    name.Contains("env", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("get", StringComparison.OrdinalIgnoreCase)
            ),
        ],

        CategoryId.StructuredOutput => [
            new CategoryTestDef(
                UserPrompt: "Submit a status report: status=\"ok\", message=\"all tests passed\", code=0",
                ToolSchema: MakeTool("submit_report", "Submit a structured status report.",
                    ("status", "string", "Status: ok or error."),
                    ("message", "string", "Human-readable message."),
                    ("code", "integer", "Exit code: 0 = success.")),
                Verify: (name, args) =>
                    name.Contains("report", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("submit", StringComparison.OrdinalIgnoreCase)
            ),
            new CategoryTestDef(
                UserPrompt: "Call submit_report with title=\"Probe Complete\", summary=\"Category probe done\", status=\"ok\", score=100",
                ToolSchema: MakeTool("submit_report", "Submit a structured status report.",
                    ("title",   "string",  "Report title."),
                    ("summary", "string",  "Summary text."),
                    ("status",  "string",  "ok or error."),
                    ("score",   "integer", "Quality score 0-100.")),
                Verify: (name, args) =>
                    (name.Contains("report", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("submit", StringComparison.OrdinalIgnoreCase)) &&
                    args.Count >= 3   // at least 3 of 4 fields populated
            ),
        ],

        CategoryId.TaskPlanning => [
            new CategoryTestDef(
                UserPrompt: "Create a task: implement user authentication, high priority, estimated 60 minutes",
                ToolSchema: MakeTool("create_task", "Create a new development task.",
                    ("name",               "string",  "Task name."),
                    ("description",        "string",  "Task description."),
                    ("priority",           "string",  "Priority: low/medium/high."),
                    ("estimated_minutes",  "integer", "Estimated time in minutes.")),
                Verify: (name, args) =>
                    name.Contains("task", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("create", StringComparison.OrdinalIgnoreCase)
            ),
            new CategoryTestDef(
                UserPrompt: "Break down 'add logging to the application' into a task with appropriate priority and time estimate",
                ToolSchema: MakeTool("create_task", "Create a new development task.",
                    ("name",               "string",  "Task name."),
                    ("description",        "string",  "Task description."),
                    ("priority",           "string",  "Priority: low/medium/high."),
                    ("estimated_minutes",  "integer", "Estimated time in minutes.")),
                Verify: (name, args) =>
                    (name.Contains("task", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("create", StringComparison.OrdinalIgnoreCase)) &&
                    args.ContainsKey("name") && args.ContainsKey("priority")
            ),
        ],

        _ => []
    };

    // ── Tool schema builder ───────────────────────────────────────────────────

    private static object MakeTool(string name, string desc, params (string name, string type, string desc)[] props)
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
                    required   = props.Select(p => p.name).ToArray()
                }
            }
        };
    }

    // ── Parsers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> ExtractNativeArgs(JsonNode? tcNode)
    {
        var argsRaw = tcNode?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
        var dict    = new Dictionary<string, object?>();
        try
        {
            var node = JsonNode.Parse(argsRaw);
            if (node is JsonObject obj)
                foreach (var kvp in obj)
                    dict[kvp.Key] = kvp.Value?.ToString();
        }
        catch { }
        return dict;
    }

    private record ParsedCall(string Name, Dictionary<string, object?> Args);

    private static List<ParsedCall> TryParseTextCalls(string content)
    {
        var result   = new List<ParsedCall>();
        var stripped = Regex.Replace(content, @"```(?:json)?", "", RegexOptions.IgnoreCase).Trim();
        int i = 0;
        while (i < stripped.Length)
        {
            var start = stripped.IndexOf('{', i);
            if (start < 0) break;
            int depth = 0, end = -1;
            bool inStr = false;
            for (int j = start; j < stripped.Length; j++)
            {
                var ch = stripped[j];
                if (ch == '"' && (j == 0 || stripped[j - 1] != '\\')) inStr = !inStr;
                if (inStr) continue;
                if (ch == '{') depth++;
                else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
            }
            if (end < 0) break;
            try
            {
                var node = JsonNode.Parse(stripped[start..(end + 1)]);
                var name = node?["name"]?.GetValue<string>() ?? node?["tool"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(name))
                {
                    var argsNode = node?["arguments"] ?? node?["args"];
                    var args     = new Dictionary<string, object?>();
                    if (argsNode is JsonObject obj)
                        foreach (var kvp in obj) args[kvp.Key] = kvp.Value?.ToString();
                    result.Add(new ParsedCall(name, args));
                }
            }
            catch { }
            i = end + 1;
        }
        return result;
    }

    // ── Inner record ─────────────────────────────────────────────────────────

    private record CategoryTestDef(
        string UserPrompt,
        object ToolSchema,
        Func<string, Dictionary<string, object?>, bool> Verify
    );
}
