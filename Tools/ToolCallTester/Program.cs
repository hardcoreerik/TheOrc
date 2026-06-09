// ─────────────────────────────────────────────────────────────────────────────
//  tool-probe  — TheOrc tool-call capability tester
//
//  Usage:
//    tool-probe                                 Test all installed Ollama models
//    tool-probe --model qwen2.5-coder:14b       Test one model
//    tool-probe --host http://localhost:11434   Use a different Ollama host
//    tool-probe --json                          Output results as JSON to stdout
//    tool-probe --list                          List stored profiles (no new tests)
//
//  Exit codes:  0 = all tested models pass ≥3/5 in at least one mode
//               1 = one or more models fail both modes
//               2 = Ollama not reachable
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// ── CLI arg parse ─────────────────────────────────────────────────────────────

var cliArgs     = args.Length > 0 ? args : Environment.GetCommandLineArgs().Skip(1).ToArray();
var host        = ArgValue(cliArgs, "--host") ?? "http://localhost:11434";
var modelFilter = ArgValue(cliArgs, "--model");
var jsonOut     = cliArgs.Contains("--json");
var listOnly    = cliArgs.Contains("--list");
var helpMode    = cliArgs.Contains("--help") || cliArgs.Contains("-h");

if (helpMode) { PrintHelp(); return 0; }

Console.OutputEncoding = Encoding.UTF8;

// ── Banner ────────────────────────────────────────────────────────────────────
if (!jsonOut)
{
    C("⬡ TheOrc — Tool Call Probe", ConsoleColor.Green);
    C($"  Host: {host}", ConsoleColor.DarkGray);
    C("", ConsoleColor.Gray);
}

// ── List stored profiles ──────────────────────────────────────────────────────
if (listOnly)
{
    var stored = ProfileStore.LoadAll();
    if (stored.Count == 0)
    {
        C("No stored profiles found. Run without --list to test models.", ConsoleColor.DarkYellow);
        return 0;
    }

    C($"{"MODEL",-42} {"NATIVE",8} {"TEXT",6} {"MODE",-12} TESTED", ConsoleColor.DarkGray);
    C(new string('─', 80), ConsoleColor.DarkGray);
    foreach (var p in stored)
    {
        var modeColor = p.RecommendedModeEnum switch
        {
            ToolCallMode.Native   => ConsoleColor.Cyan,
            ToolCallMode.TextJson => ConsoleColor.Magenta,
            ToolCallMode.Both     => ConsoleColor.Green,
            _                     => ConsoleColor.DarkRed,
        };
        C($"{p.ModelId[..Math.Min(40, p.ModelId.Length)],   -42} " +
          $"{p.NativePasses,5}/5   {p.TextPasses,2}/5   ", ConsoleColor.Gray, newline: false);
        C($"{p.RecommendedMode,-12}", modeColor, newline: false);
        C($" {p.TestedAt:yyyy-MM-dd}", ConsoleColor.DarkGray);
    }
    return 0;
}

// ── Get installed models ──────────────────────────────────────────────────────
List<string> models;
try
{
    models = await GetInstalledModelsAsync(host);
}
catch (Exception ex)
{
    C($"✗ Cannot connect to Ollama at {host}: {ex.Message}", ConsoleColor.Red);
    return 2;
}

if (models.Count == 0)
{
    C("No models found — is Ollama running?", ConsoleColor.DarkYellow);
    return 2;
}

if (modelFilter != null)
{
    models = models.Where(m => m.Contains(modelFilter, StringComparison.OrdinalIgnoreCase)).ToList();
    if (models.Count == 0)
    {
        C($"No model matching '{modelFilter}' found.", ConsoleColor.DarkYellow);
        C($"Installed: {string.Join(", ", await GetInstalledModelsAsync(host))}", ConsoleColor.DarkGray);
        return 2;
    }
}

if (!jsonOut)
{
    C($"Found {models.Count} model(s) to test.", ConsoleColor.DarkGray);
    C("", ConsoleColor.Gray);
}

// ── Run probes ────────────────────────────────────────────────────────────────
var allResults   = new List<ModelResult>();
var anyFailed    = false;
using var http   = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

foreach (var model in models)
{
    if (!jsonOut)
    {
        C($"── {model}", ConsoleColor.White);
    }

    var outcomes = new List<Outcome>();

    foreach (var testId in Enum.GetValues<TestId>())
    {
        foreach (var mode in new[] { Mode.Native, Mode.Text })
        {
            if (!jsonOut)
            {
                C($"  [{mode,-6}] {testId,-20}", ConsoleColor.DarkGray, newline: false);
                Console.Out.Flush();
            }

            Outcome outcome;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                outcome = await RunProbeAsync(http, host, model, testId, mode, cts.Token);
            }
            catch (OperationCanceledException)
            {
                outcome = new Outcome(testId, mode, Result.Timeout, Reason: "45s timeout");
            }
            catch (Exception ex)
            {
                outcome = new Outcome(testId, mode, Result.Error, Reason: ex.Message[..Math.Min(80, ex.Message.Length)]);
            }

            outcomes.Add(outcome);

            if (!jsonOut)
            {
                var color = outcome.Status == Result.Pass ? ConsoleColor.Green : ConsoleColor.Red;
                var icon  = outcome.Status switch { Result.Pass => "✓", Result.Timeout => "⏱", Result.Error => "⚠", _ => "✗" };
                C($" {icon} {outcome.Status}", color, newline: false);
                if (outcome.Reason != null) C($"  {outcome.Reason}", ConsoleColor.DarkGray, newline: false);
                C("", ConsoleColor.Gray);
            }
        }
    }

    var native = outcomes.Count(o => o.Mode == Mode.Native && o.Status == Result.Pass);
    var text   = outcomes.Count(o => o.Mode == Mode.Text   && o.Status == Result.Pass);
    var rec = (native >= 3, text >= 3) switch
    {
        (true,  true)  => ToolCallMode.Both,
        (true,  false) => ToolCallMode.Native,
        (false, true)  => ToolCallMode.TextJson,
        _              => ToolCallMode.None,
    };

    if (native < 3 && text < 3) anyFailed = true;

    var mr = new ModelResult(model, native, text, rec, outcomes);
    allResults.Add(mr);

    // Persist profile
    await ProfileStore.SaveAsync(model, native, text, rec, outcomes);

    if (!jsonOut)
    {
        var color = rec == ToolCallMode.None ? ConsoleColor.Red : ConsoleColor.Green;
        C($"  → native={native}/5  text={text}/5  dispatch={rec}", color);
        C("", ConsoleColor.Gray);
    }
}

// ── Output ────────────────────────────────────────────────────────────────────
if (jsonOut)
{
    var output = allResults.Select(r => new
    {
        model   = r.Model,
        native  = r.NativePasses,
        text    = r.TextPasses,
        mode    = r.Recommended.ToString(),
        tests   = r.Outcomes.Select(o => new { test = o.Test.ToString(), mode = o.Mode.ToString(), result = o.Status.ToString(), reason = o.Reason })
    });
    Console.WriteLine(JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
}
else
{
    C("─────────────────────────────────────────────────────────", ConsoleColor.DarkGray);
    C($"{"MODEL",-42} {"NAT",5} {"TXT",5}  DISPATCH", ConsoleColor.DarkGray);
    C(new string('─', 62), ConsoleColor.DarkGray);
    foreach (var r in allResults)
    {
        var color = r.Recommended == ToolCallMode.None ? ConsoleColor.Red : ConsoleColor.Green;
        C($"{r.Model[..Math.Min(40, r.Model.Length)], -42} {r.NativePasses,3}/5  {r.TextPasses,2}/5  ", ConsoleColor.Gray, newline: false);
        C(r.Recommended.ToString(), color);
    }
    C("", ConsoleColor.Gray);
    C($"Profiles saved → {ProfileStore.ProfilesPath}", ConsoleColor.DarkGray);
}

return anyFailed ? 1 : 0;

// ─────────────────────────────────────────────────────────────────────────────
// Probe implementation (self-contained)
// ─────────────────────────────────────────────────────────────────────────────

static async Task<Outcome> RunProbeAsync(HttpClient http, string host, string model,
    TestId testId, Mode mode, CancellationToken ct)
{
    var def = GetDef(testId);
    var sysPrompt = mode == Mode.Text ? BuildTextSysPrompt(def) :
        "You are a tool-calling assistant. Use the provided tools. Never refuse. Never explain.";

    var payload = new Dictionary<string, object?>
    {
        ["model"]       = model,
        ["stream"]      = false,
        ["temperature"] = 0.0,
        ["max_tokens"]  = 256,
        ["messages"]    = new object[]
        {
            new { role = "system", content = sysPrompt },
            new { role = "user",   content = def.Prompt },
        },
    };
    if (mode == Mode.Native) payload["tools"] = def.Tools;

    var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    var resp = await http.PostAsync($"{host}/v1/chat/completions", body, ct);
    resp.EnsureSuccessStatusCode();

    var json   = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    var choice = json?["choices"]?[0];
    var msg    = choice?["message"];

    string? toolName = null;
    Dictionary<string, object?> toolArgs = [];

    if (mode == Mode.Native)
    {
        var tcs = msg?["tool_calls"]?.AsArray();
        if (tcs != null && tcs.Count > 0)
        {
            toolName = tcs[0]?["function"]?["name"]?.GetValue<string>();
            toolArgs = ParseArgs(tcs[0]?["function"]?["arguments"]?.GetValue<string>() ?? "{}");
        }
        else
        {
            // Fallback: try text parse even in native mode
            var parsed = TextParse(msg?["content"]?.GetValue<string>() ?? "");
            if (parsed.Count > 0) { toolName = parsed[0].name; toolArgs = parsed[0].args; }
        }
    }
    else
    {
        var parsed = TextParse(msg?["content"]?.GetValue<string>() ?? "");
        if (parsed.Count > 0) { toolName = parsed[0].name; toolArgs = parsed[0].args; }
    }

    if (toolName == null)
        return new Outcome(testId, mode, Result.Fail, Reason: "no tool call in response");

    var reason = def.Verify(toolName, toolArgs);
    return reason == null
        ? new Outcome(testId, mode, Result.Pass, Actual: $"{toolName}()")
        : new Outcome(testId, mode, Result.Fail, Reason: reason);
}

// ── Test definitions ──────────────────────────────────────────────────────────

static Def GetDef(TestId id) => id switch
{
    TestId.BasicCall => new Def(
        "Call the echo tool with the message \"probe_42\".",
        Tools: [Tool("echo", "Echo a message.", [("message", "string", "The message.")])],
        Verify: (n, a) => n.Equals("echo", StringComparison.OrdinalIgnoreCase) && a.GetValueOrDefault("message")?.ToString()?.Contains("probe") == true ? null : $"expected echo(message=probe_42), got {n}({A(a)})"
    ),
    TestId.IntArgs => new Def(
        "Add 17 and 25 using the add_numbers tool.",
        Tools: [Tool("add_numbers", "Add two integers.", [("a", "integer", "First."), ("b", "integer", "Second.")])],
        Verify: (n, a) =>
        {
            if (!n.Equals("add_numbers", StringComparison.OrdinalIgnoreCase)) return $"wrong tool: {n}";
            var av = a.GetValueOrDefault("a")?.ToString();
            var bv = a.GetValueOrDefault("b")?.ToString();
            if (av != "17" && av != "17.0") return $"a={av} expected 17";
            if (bv != "25" && bv != "25.0") return $"b={bv} expected 25";
            return null;
        }
    ),
    TestId.MultilineContent => new Def(
        "Write a file named test.txt with two lines: first line 'hello', second line 'world'.",
        Tools: [Tool("write_text", "Write a file.", [("filename", "string", "File name."), ("content", "string", "File content.")])],
        Verify: (n, a) =>
        {
            if (!n.Equals("write_text", StringComparison.OrdinalIgnoreCase)) return $"wrong tool: {n}";
            var c = a.GetValueOrDefault("content")?.ToString() ?? "";
            if (!c.Contains("hello", StringComparison.OrdinalIgnoreCase)) return "content missing 'hello'";
            if (!c.Contains("world", StringComparison.OrdinalIgnoreCase)) return "content missing 'world'";
            return null;
        }
    ),
    TestId.ToolSelection => new Def(
        "List the files in the current directory.",
        Tools: [
            Tool("read_file",      "Read a file.", [("path", "string", "Path.")]),
            Tool("write_file",     "Write a file.", [("path", "string", "Path."), ("content", "string", "Content.")]),
            Tool("list_directory", "List files in a directory.", [("path", "string", "Path.")]),
        ],
        Verify: (n, _) => n.Equals("list_directory", StringComparison.OrdinalIgnoreCase) ||
                          n.Equals("list_files",     StringComparison.OrdinalIgnoreCase)
            ? null : $"expected list_directory, got '{n}'"
    ),
    TestId.StructuredOutput => new Def(
        "Report that the operation succeeded. Use status \"ok\", message \"probe complete\", and code 0.",
        Tools: [Tool("report_status", "Report a result.", [("status", "string", "ok or error."), ("message", "string", "Message."), ("code", "integer", "Exit code.")])],
        Verify: (n, a) =>
        {
            if (!n.Equals("report_status", StringComparison.OrdinalIgnoreCase)) return $"wrong tool: {n}";
            var st = a.GetValueOrDefault("status")?.ToString() ?? "";
            if (!new[] { "ok", "success", "pass" }.Any(v => st.Equals(v, StringComparison.OrdinalIgnoreCase))) return $"status='{st}' not ok/success";
            var code = a.GetValueOrDefault("code")?.ToString() ?? "";
            if (code != "0" && code != "0.0") return $"code='{code}' expected 0";
            return null;
        }
    ),
    _ => throw new ArgumentOutOfRangeException()
};

// ── Schema helpers ────────────────────────────────────────────────────────────

static object Tool(string name, string desc, (string, string, string)[] props) =>
    new
    {
        type     = "function",
        function = new
        {
            name,
            description = desc,
            parameters  = new
            {
                type       = "object",
                properties = props.ToDictionary(p => p.Item1, p => (object)new { type = p.Item2, description = p.Item3 }),
                required   = props.Select(p => p.Item1).ToArray(),
            }
        }
    };

static string BuildTextSysPrompt(Def def)
{
    var sb = new StringBuilder();
    sb.AppendLine("Output a single raw JSON tool call — no markdown, no explanation.");
    sb.AppendLine("Format: {\"name\": \"<tool>\", \"arguments\": {\"<key>\": <value>}}");
    sb.AppendLine("Available tools:");
    foreach (var t in def.Tools)
    {
        var fn = ((dynamic)t).function;
        sb.AppendLine($"  {fn.name}: {fn.description}");
    }
    return sb.ToString().TrimEnd();
}

// ── Text tool call parser ─────────────────────────────────────────────────────

static List<(string name, Dictionary<string, object?> args)> TextParse(string content)
{
    var result   = new List<(string, Dictionary<string, object?>)>();
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
            var name = node?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name))
                result.Add((name, ParseArgs(node?["arguments"]?.ToJsonString() ?? "{}")));
        }
        catch { }
        i = end + 1;
    }
    return result;
}

static Dictionary<string, object?> ParseArgs(string json)
{
    var d = new Dictionary<string, object?>();
    try
    {
        var n = JsonNode.Parse(json);
        if (n is JsonObject obj)
            foreach (var kv in obj)
                d[kv.Key] = kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : kv.Value?.ToString();
    }
    catch { }
    return d;
}

static string A(Dictionary<string, object?> args) =>
    string.Join(",", args.Take(2).Select(kv => $"{kv.Key}={kv.Value}"));

// ── Model discovery ───────────────────────────────────────────────────────────

static async Task<List<string>> GetInstalledModelsAsync(string host)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var resp = await http.GetAsync($"{host}/api/tags");
    resp.EnsureSuccessStatusCode();
    var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    return json?["models"]?.AsArray()
        .Select(m => m?["name"]?.GetValue<string>() ?? "")
        .Where(n => n.Length > 0)
        .ToList() ?? [];
}

// ── Console helpers ───────────────────────────────────────────────────────────

static void C(string text, ConsoleColor color, bool newline = true)
{
    Console.ForegroundColor = color;
    if (newline) Console.WriteLine(text);
    else         Console.Write(text);
    Console.ResetColor();
}

static string? ArgValue(string[] args, string key)
{
    var idx = Array.IndexOf(args, key);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static void PrintHelp()
{
    Console.WriteLine("""
    tool-probe — TheOrc tool call capability tester

    Usage:
      tool-probe                          Test all installed Ollama models
      tool-probe --model qwen2.5-coder:14b  Test one model
      tool-probe --host http://host:11434   Custom Ollama host
      tool-probe --json                   Output JSON to stdout
      tool-probe --list                   Show stored profiles (no new tests)
      tool-probe --help                   Show this message

    Exit codes:
      0  All models pass ≥3/5 in at least one mode
      1  One or more models fail both modes
      2  Ollama not reachable / no models found

    Profiles stored at:
      %AppData%\OrchestratorIDE\tool-call-profiles.json
    """);
}

// ─────────────────────────────────────────────────────────────────────────────
// Self-contained profile store (mirrors OrchestratorIDE.Services.ToolCalls)
// ─────────────────────────────────────────────────────────────────────────────

static class ProfileStore
{
    public static readonly string ProfilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "tool-call-profiles.json");

    private static readonly JsonSerializerOptions _json = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task SaveAsync(string modelId, int native, int text, ToolCallMode rec,
        IEnumerable<Outcome> outcomes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
            var all = Load();
            all[modelId] = new StoredProfile
            {
                ModelId         = modelId,
                TestedAt        = DateTime.UtcNow,
                RecommendedMode = rec.ToString(),
                NativePasses    = native,
                TextPasses      = text,
                TotalTests      = outcomes.Count(),
                TestPassMap     = outcomes.ToDictionary(
                    o => $"{o.Test}_{o.Mode}", o => o.Status == Result.Pass),
            };
            await File.WriteAllTextAsync(ProfilesPath, JsonSerializer.Serialize(all, _json));
        }
        catch { }
    }

    public static List<StoredProfile> LoadAll()
        => [.. Load().Values.OrderBy(p => p.ModelId)];

    private static Dictionary<string, StoredProfile> Load()
    {
        if (!File.Exists(ProfilesPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, StoredProfile>>(
                File.ReadAllText(ProfilesPath), _json) ?? [];
        }
        catch { return []; }
    }
}

class StoredProfile
{
    public string  ModelId         { get; set; } = "";
    public DateTime TestedAt       { get; set; }
    public string  RecommendedMode { get; set; } = "Unknown";
    public int     NativePasses    { get; set; }
    public int     TextPasses      { get; set; }
    public int     TotalTests      { get; set; }
    public Dictionary<string, bool> TestPassMap { get; set; } = [];

    public ToolCallMode RecommendedModeEnum =>
        Enum.TryParse<ToolCallMode>(RecommendedMode, out var m) ? m : ToolCallMode.Unknown;
}

// ─────────────────────────────────────────────────────────────────────────────
// Domain types
// ─────────────────────────────────────────────────────────────────────────────

enum TestId { BasicCall, IntArgs, MultilineContent, ToolSelection, StructuredOutput }
enum Mode   { Native, Text }
enum Result { Pass, Fail, Timeout, Error }
enum ToolCallMode { Unknown, Native, TextJson, Both, None }

record Def(string Prompt, IReadOnlyList<object> Tools, Func<string, Dictionary<string, object?>, string?> Verify);
record Outcome(TestId Test, Mode Mode, Result Status, string? Actual = null, string? Reason = null);
record ModelResult(string Model, int NativePasses, int TextPasses, ToolCallMode Recommended, List<Outcome> Outcomes);
