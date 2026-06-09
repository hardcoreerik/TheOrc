// ─────────────────────────────────────────────────────────────────────────────
//  tool-probe  — TheOrc GOBLIN MIND tool-call capability tester
//
//  Subcommands:
//    tool-probe [dispatch] [options]          Dispatch probe (5 tests × native+text)
//    tool-probe format     [options]          Format fingerprinting (5 variants)
//    tool-probe categories [options]          Category boundary mapping (7 categories)
//    tool-probe full       [options]          All probes — dispatch + format + categories
//    tool-probe evolve     --model X [opts]   Schema evolution (mutation search)
//    tool-probe list       [options]          Show stored profiles, no new tests
//
//  Options:
//    --model <id>       Only test this model (partial match OK)
//    --host  <url>      Ollama host (default: http://localhost:11434)
//    --json             Output results as JSON to stdout
//    --generations N    (evolve) Number of mutation generations (default: 3)
//    --tool  <name>     (evolve) Restrict to this tool name
//    --help / -h        Show help
//
//  Exit codes:  0 = all tested models pass
//               1 = one or more models fail
//               2 = Ollama not reachable / no models found
// ─────────────────────────────────────────────────────────────────────────────

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

// ── CLI arg parse ─────────────────────────────────────────────────────────────

var rawArgs   = args.Length > 0 ? args : Environment.GetCommandLineArgs().Skip(1).ToArray();
var knownSubs = new[] { "dispatch", "format", "categories", "full", "evolve", "list" };

var subcommand = rawArgs.FirstOrDefault(a =>
    !a.StartsWith('-') && knownSubs.Contains(a, StringComparer.OrdinalIgnoreCase)) ?? "dispatch";

var cliArgs = rawArgs
    .Where(a => !a.Equals(subcommand, StringComparison.OrdinalIgnoreCase))
    .ToArray();

// backward compat: --list flag
if (cliArgs.Contains("--list"))
{
    subcommand = "list";
    cliArgs = cliArgs.Where(a => a != "--list").ToArray();
}

var host        = ArgValue(cliArgs, "--host") ?? "http://localhost:11434";
var modelFilter = ArgValue(cliArgs, "--model");
var jsonOut     = cliArgs.Contains("--json");
var helpMode    = cliArgs.Contains("--help") || cliArgs.Contains("-h");
var generations = int.TryParse(ArgValue(cliArgs, "--generations"), out var g) ? g : 3;
var toolFilter  = ArgValue(cliArgs, "--tool");

if (helpMode) { PrintHelp(); return 0; }

// ── Banner ────────────────────────────────────────────────────────────────────
if (!jsonOut)
{
    C("⬡ TheOrc — GOBLIN MIND Tool Probe", ConsoleColor.Green);
    C($"  Host: {host}  |  Mode: {subcommand}", ConsoleColor.DarkGray);
    C("", ConsoleColor.Gray);
}

// ── List (no network needed) ──────────────────────────────────────────────────
if (subcommand == "list")
{
    var stored = ProfileStore.LoadAll();
    if (modelFilter != null)
        stored = stored.Where(p => p.ModelId.Contains(modelFilter, StringComparison.OrdinalIgnoreCase)).ToList();

    if (stored.Count == 0)
    {
        C("No stored profiles found. Run tool-probe to test models.", ConsoleColor.DarkYellow);
        return 0;
    }

    if (jsonOut)
    {
        Console.WriteLine(JsonSerializer.Serialize(stored,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return 0;
    }

    C($"{"MODEL",-42} {"DISPATCH",-10} {"FORMAT",-10} CATEGORIES", ConsoleColor.DarkGray);
    C(new string('─', 95), ConsoleColor.DarkGray);
    foreach (var p in stored)
    {
        var modeColor = p.RecommendedModeEnum switch
        {
            ToolCallMode.Native   => ConsoleColor.Cyan,
            ToolCallMode.TextJson => ConsoleColor.Magenta,
            ToolCallMode.Both     => ConsoleColor.Green,
            _                     => ConsoleColor.DarkRed,
        };
        var m = p.ModelId.Length > 40 ? p.ModelId[..40] : p.ModelId;
        C($"{m,-42} ", ConsoleColor.Gray, newline: false);
        C($"{p.RecommendedMode,-10}", modeColor, newline: false);
        C($" {p.PreferredFormat ?? "?",-10} ", ConsoleColor.Cyan, newline: false);
        C(p.CategorySummary ?? "not probed", ConsoleColor.Yellow);
    }
    C("", ConsoleColor.Gray);
    C($"Profiles → {ProfileStore.ProfilesPath}", ConsoleColor.DarkGray);
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

if (models.Count == 0) { C("No models found — is Ollama running?", ConsoleColor.DarkYellow); return 2; }

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

if (!jsonOut) C($"Found {models.Count} model(s) to test.\n", ConsoleColor.DarkGray);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

// ── Route ─────────────────────────────────────────────────────────────────────
bool anyFailed;
switch (subcommand.ToLowerInvariant())
{
    case "dispatch":   anyFailed = await RunDispatchAsync(models);    break;
    case "format":     anyFailed = await RunFormatAsync(models);      break;
    case "categories": anyFailed = await RunCategoriesAsync(models);  break;
    case "full":       anyFailed = await RunFullAsync(models);        break;
    case "evolve":     anyFailed = await RunEvolveAsync(models);      break;
    default:
        C($"Unknown subcommand '{subcommand}'. Use --help.", ConsoleColor.Red);
        anyFailed = true;
        break;
}

return anyFailed ? 1 : 0;

// =============================================================================
// DISPATCH PROBE
// =============================================================================

async Task<bool> RunDispatchAsync(IReadOnlyList<string> targetModels)
{
    var allResults = new List<ModelResult>();
    var failed     = false;

    foreach (var model in targetModels)
    {
        if (!jsonOut) C($"── {model}", ConsoleColor.White);
        var outcomes = new List<Outcome>();

        foreach (var testId in Enum.GetValues<TestId>())
        foreach (var mode   in new[] { Mode.Native, Mode.Text })
        {
            if (!jsonOut) { C($"  [{mode,-6}] {testId,-20}", ConsoleColor.DarkGray, newline: false); Console.Out.Flush(); }

            Outcome outcome;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                outcome = await RunProbeAsync(http, host, model, testId, mode, cts.Token);
            }
            catch (OperationCanceledException) { outcome = new Outcome(testId, mode, Result.Timeout, Reason: "45s timeout"); }
            catch (Exception ex)               { outcome = new Outcome(testId, mode, Result.Error,   Reason: ex.Message[..Math.Min(80, ex.Message.Length)]); }

            outcomes.Add(outcome);

            if (!jsonOut)
            {
                var col  = outcome.Status == Result.Pass ? ConsoleColor.Green : ConsoleColor.Red;
                var icon = outcome.Status switch { Result.Pass => "✓", Result.Timeout => "⏱", Result.Error => "⚠", _ => "✗" };
                C($" {icon} {outcome.Status}", col, newline: false);
                if (outcome.Reason != null) C($"  {outcome.Reason}", ConsoleColor.DarkGray, newline: false);
                C("", ConsoleColor.Gray);
            }
        }

        var native = outcomes.Count(o => o.Mode == Mode.Native && o.Status == Result.Pass);
        var text   = outcomes.Count(o => o.Mode == Mode.Text   && o.Status == Result.Pass);
        var rec    = (native >= 3, text >= 3) switch
        {
            (true,  true)  => ToolCallMode.Both,
            (true,  false) => ToolCallMode.Native,
            (false, true)  => ToolCallMode.TextJson,
            _              => ToolCallMode.None,
        };

        if (native < 3 && text < 3) failed = true;
        allResults.Add(new ModelResult(model, native, text, rec, outcomes));
        await ProfileStore.SaveDispatchAsync(model, native, text, rec, outcomes);

        if (!jsonOut)
        {
            C($"  → native={native}/5  text={text}/5  dispatch={rec}",
              rec == ToolCallMode.None ? ConsoleColor.Red : ConsoleColor.Green);
            C("", ConsoleColor.Gray);
        }
    }

    if (jsonOut)
    {
        Console.WriteLine(JsonSerializer.Serialize(allResults.Select(r => new
        {
            model  = r.Model,
            native = r.NativePasses,
            text   = r.TextPasses,
            mode   = r.Recommended.ToString(),
            tests  = r.Outcomes.Select(o => new { test = o.Test.ToString(), mode = o.Mode.ToString(), result = o.Status.ToString(), reason = o.Reason })
        }), new JsonSerializerOptions { WriteIndented = true }));
    }
    else
    {
        C(new string('─', 62), ConsoleColor.DarkGray);
        C($"{"MODEL",-42} {"NAT",5} {"TXT",5}  DISPATCH", ConsoleColor.DarkGray);
        C(new string('─', 62), ConsoleColor.DarkGray);
        foreach (var r in allResults)
        {
            var m = r.Model.Length > 40 ? r.Model[..40] : r.Model;
            C($"{m,-42} {r.NativePasses,3}/5  {r.TextPasses,2}/5  ", ConsoleColor.Gray, newline: false);
            C(r.Recommended.ToString(), r.Recommended == ToolCallMode.None ? ConsoleColor.Red : ConsoleColor.Green);
        }
        C("", ConsoleColor.Gray);
        C($"Profiles → {ProfileStore.ProfilesPath}", ConsoleColor.DarkGray);
    }

    return failed;
}

// =============================================================================
// FORMAT PROBE
// =============================================================================

async Task<bool> RunFormatAsync(IReadOnlyList<string> targetModels)
{
    var allResults = new List<FormatResult>();
    var failed     = false;

    foreach (var model in targetModels)
    {
        if (!jsonOut) C($"── {model}  [format fingerprint]", ConsoleColor.White);

        var scores  = new Dictionary<string, int>();
        var details = new Dictionary<string, string>();
        const string echoPrompt = "Call the echo tool with message \"format_probe\".";
        var echoTool = Tool("echo", "Echo a message back.", [("message", "string", "The message to echo.")]);

        foreach (var (fmtName, sysPrompt) in FormatVariants())
        {
            if (!jsonOut) { C($"  [fmt:{fmtName,-10}] ", ConsoleColor.DarkGray, newline: false); Console.Out.Flush(); }

            bool passed = false; string? failReason = null;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var payload = BuildFormatPayload(model, sysPrompt, echoPrompt, fmtName, echoTool);
                    var body    = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    var resp    = await http.PostAsync($"{host}/v1/chat/completions", body, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    var json    = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                    var msg     = json?["choices"]?[0]?["message"];
                    var (tn, ta) = ExtractToolCall(msg, fmtName);
                    if (tn != null && tn.Equals("echo", StringComparison.OrdinalIgnoreCase) && ta.ContainsKey("message"))
                    { passed = true; break; }
                    failReason = $"got {tn ?? "null"}";
                }
                catch (Exception ex) { failReason = ex.Message[..Math.Min(40, ex.Message.Length)]; }
            }

            scores[fmtName]  = passed ? 1 : 0;
            details[fmtName] = passed ? "pass" : (failReason ?? "fail");

            if (!jsonOut)
            {
                C(passed ? "✓" : "✗", passed ? ConsoleColor.Green : ConsoleColor.Red, newline: false);
                C($" {details[fmtName]}", ConsoleColor.DarkGray);
            }
        }

        var preferred = scores.Where(kv => kv.Value > 0).Select(kv => kv.Key).FirstOrDefault() ?? "openai";
        if (!scores.Values.Any(v => v > 0)) failed = true;

        allResults.Add(new FormatResult(model, preferred, scores, details));
        await ProfileStore.SaveFormatAsync(model, preferred, scores);

        if (!jsonOut)
        {
            C($"  → preferred: {preferred}", preferred == "openai" ? ConsoleColor.Cyan : ConsoleColor.Magenta);
            C("", ConsoleColor.Gray);
        }
    }

    if (jsonOut)
    {
        Console.WriteLine(JsonSerializer.Serialize(allResults,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    else
    {
        C(new string('─', 72), ConsoleColor.DarkGray);
        C($"{"MODEL",-42} {"PREFERRED",-12} OPENAI HERMES  BARE PYTHON  YAML", ConsoleColor.DarkGray);
        C(new string('─', 72), ConsoleColor.DarkGray);
        foreach (var r in allResults)
        {
            var m = r.Model.Length > 40 ? r.Model[..40] : r.Model;
            C($"{m,-42} {r.Preferred,-12}" +
              $"  {r.Scores.GetValueOrDefault("openai")}/1" +
              $"     {r.Scores.GetValueOrDefault("hermes")}/1" +
              $"    {r.Scores.GetValueOrDefault("bare")}/1" +
              $"     {r.Scores.GetValueOrDefault("python")}/1" +
              $"    {r.Scores.GetValueOrDefault("yaml")}/1", ConsoleColor.Gray);
        }
        C("", ConsoleColor.Gray);
        C($"Profiles → {ProfileStore.ProfilesPath}", ConsoleColor.DarkGray);
    }

    return failed;
}

// =============================================================================
// CATEGORY PROBE
// =============================================================================

async Task<bool> RunCategoriesAsync(IReadOnlyList<string> targetModels)
{
    var allResults = new List<CategoryProbeResult>();
    var failed     = false;

    foreach (var model in targetModels)
    {
        if (!jsonOut) C($"── {model}  [category probe]", ConsoleColor.White);

        var catResults = new Dictionary<string, string>();

        foreach (var catDef in CategoryDefs())
        {
            if (!jsonOut) { C($"  [{catDef.Id,-18}] ", ConsoleColor.DarkGray, newline: false); Console.Out.Flush(); }

            int passed = 0; string? failDetail = null;

            for (int ti = 0; ti < catDef.Tests.Count; ti++)
            {
                var test = catDef.Tests[ti];
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                    var payload   = new Dictionary<string, object?>
                    {
                        ["model"]       = model,
                        ["stream"]      = false,
                        ["temperature"] = 0.0,
                        ["max_tokens"]  = 200,
                        ["messages"]    = new object[]
                        {
                            new { role = "system", content = "You are a tool-calling assistant. Use the tool that best fits the request. Never explain. Never refuse." },
                            new { role = "user",   content = test.Prompt },
                        },
                        ["tools"] = test.Tools,
                    };
                    var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    var resp = await http.PostAsync($"{host}/v1/chat/completions", body, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                    var (tn, _) = ExtractToolCall(json?["choices"]?[0]?["message"], "openai");

                    if (tn == null)
                    {
                        failDetail ??= $"t{ti}:no_tool";
                    }
                    else if (test.ExpectedTools.Any(e => tn.Equals(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        passed++;
                    }
                    else
                    {
                        var confCat = AllCategoryTools()
                            .FirstOrDefault(ct => tn.Equals(ct.tool, StringComparison.OrdinalIgnoreCase)).cat;
                        failDetail ??= confCat != null ? $"t{ti}:confused→{confCat}" : $"t{ti}:wrong:{tn}";
                    }
                }
                catch (Exception ex) { failDetail ??= $"t{ti}:{ex.Message[..Math.Min(30, ex.Message.Length)]}"; }
            }

            var status = passed == catDef.Tests.Count ? "pass"
                       : passed > 0                   ? "partial"
                       : (failDetail?.Contains("confused") == true ? failDetail : "fail");
            catResults[catDef.Id] = status;

            if (!jsonOut)
            {
                var (icon, col) = status == "pass"    ? ("✓", ConsoleColor.Green)  :
                                  status == "partial" ? ("~", ConsoleColor.Yellow) :
                                                        ("✗", ConsoleColor.Red);
                C($" {icon}", col, newline: false);
                if (status != "pass" && failDetail != null) C($"  {failDetail}", ConsoleColor.DarkGray, newline: false);
                C("", ConsoleColor.Gray);
            }
        }

        if (catResults.Values.Any(v => v != "pass" && v != "partial")) failed = true;
        var summary = BuildCatSummary(catResults);
        allResults.Add(new CategoryProbeResult(model, catResults, summary));
        await ProfileStore.SaveCategoriesAsync(model, catResults, summary);

        if (!jsonOut) { C($"  → {summary}", ConsoleColor.Yellow); C("", ConsoleColor.Gray); }
    }

    if (jsonOut)
    {
        Console.WriteLine(JsonSerializer.Serialize(allResults,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    else
    {
        var catIds = new[] { "file_ops","network","code_exec","data_transform","system_inspect","structured_output","task_planning" };
        C(new string('─', 80), ConsoleColor.DarkGray);
        C($"{"MODEL",-42} FILE  NET  CODE  DATA  SYS  STRCT PLAN", ConsoleColor.DarkGray);
        C(new string('─', 80), ConsoleColor.DarkGray);
        foreach (var r in allResults)
        {
            var m = r.Model.Length > 40 ? r.Model[..40] : r.Model;
            C($"{m,-42}", ConsoleColor.Gray, newline: false);
            foreach (var cat in catIds)
            {
                var v = r.Results.GetValueOrDefault(cat, "?");
                var (icon, col) = v == "pass" ? ("✓", ConsoleColor.Green) :
                                  v == "partial" ? ("~", ConsoleColor.Yellow) : ("✗", ConsoleColor.Red);
                C($"  {icon}  ", col, newline: false);
            }
            C("", ConsoleColor.Gray);
        }
        C("", ConsoleColor.Gray);
        C($"Profiles → {ProfileStore.ProfilesPath}", ConsoleColor.DarkGray);
    }

    return failed;
}

// =============================================================================
// FULL GOBLIN MIND PROBE
// =============================================================================

async Task<bool> RunFullAsync(IReadOnlyList<string> targetModels)
{
    if (!jsonOut) { C("[ Phase 1/3: Dispatch Probe ]", ConsoleColor.Cyan); C("", ConsoleColor.Gray); }
    var f1 = await RunDispatchAsync(targetModels);

    if (!jsonOut) { C("\n[ Phase 2/3: Format Fingerprinting ]", ConsoleColor.Cyan); C("", ConsoleColor.Gray); }
    var f2 = await RunFormatAsync(targetModels);

    if (!jsonOut) { C("\n[ Phase 3/3: Category Mapping ]", ConsoleColor.Cyan); C("", ConsoleColor.Gray); }
    var f3 = await RunCategoriesAsync(targetModels);

    if (jsonOut)
    {
        var profiles = ProfileStore.LoadAll()
            .Where(p => targetModels.Any(m => m.Equals(p.ModelId, StringComparison.OrdinalIgnoreCase)));
        Console.WriteLine(JsonSerializer.Serialize(profiles,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    else
    {
        C("\n══════════════════════════════════════════════════════════════", ConsoleColor.Green);
        C(" ⬡  GOBLIN MIND — Full Profile Summary", ConsoleColor.Green);
        C("══════════════════════════════════════════════════════════════", ConsoleColor.Green);
        var profiles = ProfileStore.LoadAll()
            .Where(p => targetModels.Any(m => m.Equals(p.ModelId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        C($"\n{"MODEL",-42} {"DISPATCH",-10} {"FORMAT",-10} CATEGORIES", ConsoleColor.DarkGray);
        C(new string('─', 95), ConsoleColor.DarkGray);
        foreach (var p in profiles)
        {
            var m = p.ModelId.Length > 40 ? p.ModelId[..40] : p.ModelId;
            C($"{m,-42} ", ConsoleColor.Gray, newline: false);
            C($"{p.RecommendedMode,-10}", p.RecommendedModeEnum == ToolCallMode.None ? ConsoleColor.Red : ConsoleColor.Green, newline: false);
            C($" {p.PreferredFormat ?? "?",-10} ", ConsoleColor.Cyan, newline: false);
            C(p.CategorySummary ?? "not probed", ConsoleColor.Yellow);
        }
        C("", ConsoleColor.Gray);
        C($"Profiles → {ProfileStore.ProfilesPath}", ConsoleColor.DarkGray);
    }

    return f1 || f2 || f3;
}

// =============================================================================
// SCHEMA EVOLUTION
// =============================================================================

async Task<bool> RunEvolveAsync(IReadOnlyList<string> targetModels)
{
    if (targetModels.Count == 0) { C("evolve requires --model <id>", ConsoleColor.Red); return true; }
    if (targetModels.Count > 1)  { C("evolve runs one model at a time. Use --model to select one.", ConsoleColor.DarkYellow); return true; }

    var model = targetModels[0];
    if (!jsonOut) C($"── {model}  [schema evolution, {generations} generation(s)]", ConsoleColor.White);

    var evolveDefs = EvolveToolDefs()
        .Where(d => toolFilter == null || d.Name.Contains(toolFilter, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (evolveDefs.Count == 0)
    {
        C($"No tool matching '{toolFilter}'. Available: {string.Join(", ", EvolveToolDefs().Select(d => d.Name))}", ConsoleColor.DarkYellow);
        return true;
    }

    var allVariants = new List<EvolveVariantResult>();
    var anyFail     = false;

    foreach (var toolDef in evolveDefs)
    {
        if (!jsonOut) C($"\n  Tool: {toolDef.Name}", ConsoleColor.Cyan);

        var winners      = new List<string>();
        object? parentSchema = null;

        for (int gen = 0; gen < generations; gen++)
        {
            if (!jsonOut) C($"  ── Generation {gen + 1}/{generations}", ConsoleColor.DarkGray);

            foreach (var (variantId, mutName, schema) in GenerateMutations(toolDef, parentSchema, gen))
            {
                if (!jsonOut) { C($"    [{variantId,-14}] {mutName,-22} ", ConsoleColor.DarkGray, newline: false); Console.Out.Flush(); }

                bool passed = false; string? failReason = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    var payload   = new Dictionary<string, object?>
                    {
                        ["model"]       = model,
                        ["stream"]      = false,
                        ["temperature"] = 0.0,
                        ["max_tokens"]  = 200,
                        ["messages"]    = new object[]
                        {
                            new { role = "system", content = "You are a tool-calling assistant. Use the provided tools. Never refuse. Never explain." },
                            new { role = "user",   content = toolDef.Prompt },
                        },
                        ["tools"] = new[] { schema },
                    };
                    var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    var resp = await http.PostAsync($"{host}/v1/chat/completions", body, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    var json  = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                    var (tn, ta) = ExtractToolCall(json?["choices"]?[0]?["message"], "openai");
                    failReason   = tn == null ? "no tool call" : toolDef.Verify(tn, ta);
                    if (failReason == null)
                    {
                        passed = true;
                        winners.Add(variantId);
                        parentSchema ??= schema;
                    }
                }
                catch (Exception ex) { failReason = ex.Message[..Math.Min(40, ex.Message.Length)]; }

                allVariants.Add(new EvolveVariantResult(model, toolDef.Name, variantId, mutName, passed, failReason));

                if (!jsonOut)
                    C(passed ? "✓ PASS" : $"✗  {failReason}", passed ? ConsoleColor.Green : ConsoleColor.Red);
            }
        }

        if (winners.Count == 0) anyFail = true;

        if (!jsonOut)
        {
            var toolPassed = allVariants.Count(v => v.ToolName == toolDef.Name && v.Passed);
            var toolTested = allVariants.Count(v => v.ToolName == toolDef.Name);
            C($"\n  Result: {toolPassed}/{toolTested} variants passed",
              winners.Count > 0 ? ConsoleColor.Green : ConsoleColor.Red);
            if (winners.Count > 0)
                C($"  Best variant: {winners[0]}", ConsoleColor.Cyan);
        }
    }

    if (jsonOut)
        Console.WriteLine(JsonSerializer.Serialize(allVariants,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    else
    {
        var total = allVariants.Count; var passed = allVariants.Count(v => v.Passed);
        C($"\nEvolution complete: {passed}/{total} variants passed",
          passed > 0 ? ConsoleColor.Green : ConsoleColor.Red);
    }

    return anyFail;
}

// =============================================================================
// CORE DISPATCH PROBE
// =============================================================================

static async Task<Outcome> RunProbeAsync(HttpClient http, string host, string model,
    TestId testId, Mode mode, CancellationToken ct)
{
    var def       = GetDef(testId);
    var sysPrompt = mode == Mode.Text
        ? BuildTextSysPrompt(def)
        : "You are a tool-calling assistant. Use the provided tools. Never refuse. Never explain.";

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

    var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
    var msg  = json?["choices"]?[0]?["message"];

    // native tool_calls first, then text fallback
    string? toolName = null;
    Dictionary<string, object?> toolArgs = [];

    var tcs = msg?["tool_calls"]?.AsArray();
    if (tcs?.Count > 0)
    {
        toolName = tcs[0]?["function"]?["name"]?.GetValue<string>();
        toolArgs = ParseArgs(tcs[0]?["function"]?["arguments"]?.GetValue<string>() ?? "{}");
    }
    if (toolName == null)
    {
        (toolName, toolArgs) = ParseTextJson(msg?["content"]?.GetValue<string>() ?? "");
    }

    if (toolName == null)
        return new Outcome(testId, mode, Result.Fail, Reason: "no tool call in response");

    var reason = def.Verify(toolName, toolArgs);
    return reason == null
        ? new Outcome(testId, mode, Result.Pass, Actual: $"{toolName}()")
        : new Outcome(testId, mode, Result.Fail, Reason: reason);
}

// =============================================================================
// TEST DEFINITIONS
// =============================================================================

static Def GetDef(TestId id) => id switch
{
    TestId.BasicCall => new Def(
        "Call the echo tool with the message \"probe_42\".",
        Tools: [Tool("echo", "Echo a message.", [("message", "string", "The message.")])],
        Verify: (n, a) =>
            n.Equals("echo", StringComparison.OrdinalIgnoreCase) &&
            a.GetValueOrDefault("message")?.ToString()?.Contains("probe") == true
            ? null : $"expected echo(probe_42), got {n}({A(a)})"
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
        Tools:
        [
            Tool("read_file",      "Read a file.",                   [("path", "string", "Path.")]),
            Tool("write_file",     "Write a file.",                  [("path", "string", "Path."), ("content", "string", "Content.")]),
            Tool("list_directory", "List files in a directory.",     [("path", "string", "Path.")]),
        ],
        Verify: (n, _) =>
            n.Equals("list_directory", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("list_files",     StringComparison.OrdinalIgnoreCase)
            ? null : $"expected list_directory, got '{n}'"
    ),
    TestId.StructuredOutput => new Def(
        "Report that the operation succeeded. Use status \"ok\", message \"probe complete\", and code 0.",
        Tools: [Tool("report_status", "Report a result.",
            [("status","string","ok or error."), ("message","string","Message."), ("code","integer","Exit code.")])],
        Verify: (n, a) =>
        {
            if (!n.Equals("report_status", StringComparison.OrdinalIgnoreCase)) return $"wrong tool: {n}";
            var st = a.GetValueOrDefault("status")?.ToString() ?? "";
            if (st.TrimStart().StartsWith("{"))
            {
                try { var inner = JsonNode.Parse(st); st = inner?["status"]?.GetValue<string>() ?? st; } catch { }
            }
            if (!new[] { "ok","success","pass","complete" }
                    .Any(v => st.Equals(v, StringComparison.OrdinalIgnoreCase)))
                return $"unexpected status: '{st}'";
            var code = a.GetValueOrDefault("code")?.ToString() ?? "";
            if (code != "0" && code != "0.0" && code != "\"0\"") return $"code='{code}' expected 0";
            return null;
        }
    ),
    _ => throw new ArgumentOutOfRangeException()
};

// =============================================================================
// SCHEMA / TOOL HELPERS
// =============================================================================

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
                properties = props.Length == 0
                    ? (object)new { }
                    : props.ToDictionary(p => p.Item1, p => (object)new { type = p.Item2, description = p.Item3 }),
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

// =============================================================================
// TOOL CALL EXTRACTION — native API + format-aware text parsers
// =============================================================================

static (string? toolName, Dictionary<string, object?> args) ExtractToolCall(JsonNode? msg, string fmt)
{
    // Native API path
    var tcs = msg?["tool_calls"]?.AsArray();
    if (tcs?.Count > 0)
    {
        var fn = tcs[0]?["function"];
        var n  = fn?["name"]?.GetValue<string>();
        if (n != null) return (n, ParseArgs(fn?["arguments"]?.GetValue<string>() ?? "{}"));
    }

    // Text parse with format-aware fallback
    var content = msg?["content"]?.GetValue<string>() ?? "";
    return fmt switch
    {
        "hermes" => ParseHermes(content),
        "python" => ParsePython(content),
        "yaml"   => ParseYaml(content),
        _        => ParseTextJson(content),
    };
}

static (string? name, Dictionary<string, object?> args) ParseHermes(string content)
{
    var nameM  = Regex.Match(content, @"<name>(.*?)</name>",             RegexOptions.Singleline);
    var paramM = Regex.Match(content, @"<parameters>(.*?)</parameters>", RegexOptions.Singleline);
    if (!nameM.Success) return (null, []);
    return (nameM.Groups[1].Value.Trim(),
            paramM.Success ? ParseArgs(paramM.Groups[1].Value.Trim()) : []);
}

static (string? name, Dictionary<string, object?> args) ParsePython(string content)
{
    var m = Regex.Match(content, @"(\w+)\s*\(([^)]*)\)");
    if (!m.Success) return (null, []);
    var args = new Dictionary<string, object?>();
    foreach (var part in m.Groups[2].Value.Split(','))
    {
        var eq = part.IndexOf('=');
        if (eq < 0) continue;
        args[part[..eq].Trim()] = part[(eq+1)..].Trim().Trim('"', '\'');
    }
    return (m.Groups[1].Value, args);
}

static (string? name, Dictionary<string, object?> args) ParseYaml(string content)
{
    var toolM = Regex.Match(content, @"^tool:\s*(\S+)", RegexOptions.Multiline);
    if (!toolM.Success) return (null, []);
    var name = toolM.Groups[1].Value.Trim();
    var args = new Dictionary<string, object?>();
    var idx  = content.IndexOf("args:", StringComparison.OrdinalIgnoreCase);
    if (idx >= 0)
        foreach (Match m in Regex.Matches(content[(idx + 5)..], @"^\s{1,4}(\w+):\s*(.+)", RegexOptions.Multiline))
            args[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim().Trim('"', '\'');
    return (name, args);
}

static (string? name, Dictionary<string, object?> args) ParseTextJson(string content)
{
    var stripped = Regex.Replace(content, @"```(?:json)?", "", RegexOptions.IgnoreCase).Trim();
    int i = 0;
    while (i < stripped.Length)
    {
        var start = stripped.IndexOf('{', i);
        if (start < 0) break;
        int depth = 0, end = -1; bool inStr = false;
        for (int j = start; j < stripped.Length; j++)
        {
            var ch = stripped[j];
            if (ch == '"' && (j == 0 || stripped[j-1] != '\\')) inStr = !inStr;
            if (inStr) continue;
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
        }
        if (end < 0) break;
        try
        {
            var node = JsonNode.Parse(stripped[start..(end+1)]);
            var name = node?["name"]?.GetValue<string>()
                    ?? node?["tool"]?.GetValue<string>()
                    ?? node?["function"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name))
            {
                var argsNode = node?["arguments"] ?? node?["args"] ?? node?["parameters"] ?? node?["inputs"];
                if (argsNode == null)
                {
                    // flat format
                    var flat = new Dictionary<string, object?>();
                    if (node is JsonObject obj)
                        foreach (var kv in obj)
                            if (kv.Key != "name" && kv.Key != "tool" && kv.Key != "function")
                                flat[kv.Key] = kv.Value?.ToJsonString() ?? "";
                    if (flat.Count > 0) return (name, flat);
                }
                else
                {
                    return (name, ParseArgs(argsNode.ToJsonString()));
                }
            }
        }
        catch { }
        i = end + 1;
    }
    return (null, []);
}

static Dictionary<string, object?> ParseArgs(string json)
{
    var d = new Dictionary<string, object?>();
    try
    {
        var n = JsonNode.Parse(json);
        if (n is JsonObject obj)
            foreach (var kv in obj)
                d[kv.Key] = kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s)
                    ? s : kv.Value?.ToJsonString() ?? "";
    }
    catch { }
    return d;
}

static string A(Dictionary<string, object?> args) =>
    string.Join(",", args.Take(2).Select(kv => $"{kv.Key}={kv.Value}"));

// =============================================================================
// FORMAT PROBE HELPERS
// =============================================================================

static IReadOnlyList<(string name, string sysPrompt)> FormatVariants() =>
[
    ("openai",  "Output a single raw JSON tool call. Format: {\"name\": \"<tool>\", \"arguments\": {\"<key>\": <value>}}. No markdown. No explanation."),
    ("hermes",  "Output a tool call in XML: <tool_call><name>tool_name</name><parameters>{\"key\": value}</parameters></tool_call>. No explanation."),
    ("bare",    "Output a JSON tool call. Format: {\"tool\": \"<name>\", \"args\": {\"<key>\": <value>}}. No markdown. No explanation."),
    ("python",  "Output a Python-style function call only: tool_name(key=\"value\"). No explanation."),
    ("yaml",    "Output a YAML tool call:\ntool: tool_name\nargs:\n  key: value\nNo explanation. Just the YAML block."),
];

static Dictionary<string, object?> BuildFormatPayload(
    string model, string sysPrompt, string userPrompt, string fmt, object tool)
{
    var p = new Dictionary<string, object?>
    {
        ["model"]       = model,
        ["stream"]      = false,
        ["temperature"] = 0.0,
        ["max_tokens"]  = 150,
        ["messages"]    = new object[]
        {
            new { role = "system", content = sysPrompt },
            new { role = "user",   content = userPrompt },
        },
    };
    if (fmt == "openai") p["tools"] = new[] { tool };
    return p;
}

// =============================================================================
// CATEGORY PROBE HELPERS
// =============================================================================

static List<CatDef> CategoryDefs() =>
[
    new("file_ops", "File Operations",
    [
        new("Read the contents of the file at path /etc/hosts.",
            [Tool("read_file","Read a file.",[("path","string","File path.")]),
             Tool("write_file","Write a file.",[("path","string","Path."),("content","string","Content.")]),
             Tool("list_dir","List directory.",[("path","string","Path.")])], ["read_file"]),
        new("Write 'hello world' to the file test.txt.",
            [Tool("read_file","Read a file.",[("path","string","File path.")]),
             Tool("write_file","Write a file.",[("path","string","Path."),("content","string","Content.")]),
             Tool("list_dir","List directory.",[("path","string","Path.")])], ["write_file"]),
    ]),
    new("network", "Network",
    [
        new("Fetch the contents of https://example.com using HTTP GET.",
            [Tool("http_get","HTTP GET.",[("url","string","URL.")]),
             Tool("http_post","HTTP POST.",[("url","string","URL."),("body","string","Body.")]),
             Tool("read_file","Read a file.",[("path","string","Path.")])], ["http_get"]),
        new("Send a POST request to http://api.example.com/log with body '{\"event\":\"test\"}'.",
            [Tool("http_get","HTTP GET.",[("url","string","URL.")]),
             Tool("http_post","HTTP POST.",[("url","string","URL."),("body","string","Body.")]),
             Tool("read_file","Read a file.",[("path","string","Path.")])], ["http_post"]),
    ]),
    new("code_exec", "Code Execution",
    [
        new("Run the shell command 'ls -la' and return the output.",
            [Tool("run_shell","Execute a shell command.",[("command","string","Command.")]),
             Tool("eval_python","Evaluate Python.",[("code","string","Code.")]),
             Tool("read_file","Read a file.",[("path","string","Path.")])], ["run_shell"]),
        new("Evaluate the Python expression: sum(range(1, 11))",
            [Tool("run_shell","Execute a shell command.",[("command","string","Command.")]),
             Tool("eval_python","Evaluate Python.",[("code","string","Code.")]),
             Tool("read_file","Read a file.",[("path","string","Path.")])], ["eval_python"]),
    ]),
    new("data_transform", "Data Transform",
    [
        new("Parse this CSV and extract the second column: name,age,city\nAlice,30,NY\nBob,25,LA",
            [Tool("parse_csv","Parse CSV.",[("csv","string","CSV data."),("column","string","Column.")]),
             Tool("convert_json","Convert formats.",[("data","string","Data."),("format","string","Format.")]),
             Tool("run_shell","Run shell.",[("command","string","Command.")])], ["parse_csv"]),
        new("Convert this JSON to YAML format: {\"name\": \"Alice\", \"age\": 30}",
            [Tool("parse_csv","Parse CSV.",[("csv","string","CSV data."),("column","string","Column.")]),
             Tool("convert_json","Convert formats.",[("data","string","Data."),("format","string","Format.")]),
             Tool("run_shell","Run shell.",[("command","string","Command.")])], ["convert_json"]),
    ]),
    new("system_inspect", "System Inspect",
    [
        new("List all currently running processes on the system.",
            [Tool("list_processes","List running processes.",[]),
             Tool("get_env_vars","Get environment variables.",[]),
             Tool("read_file","Read a file.",[("path","string","Path.")])], ["list_processes"]),
        new("Show the current environment variables.",
            [Tool("list_processes","List running processes.",[]),
             Tool("get_env_vars","Get environment variables.",[]),
             Tool("read_file","Read a file.",[("path","string","Path.")])], ["get_env_vars"]),
    ]),
    new("structured_output", "Structured Output",
    [
        new("Report the operation status: status 'ok', message 'task complete', code 0.",
            [Tool("report_status","Report a structured result.",
                [("status","string","ok or error."),("message","string","Message."),("code","integer","Exit code.")])],
            ["report_status"]),
        new("Record a measurement: sensor 'temp', value 98.6, unit 'F', timestamp '2024-01-01T00:00:00Z'.",
            [Tool("record_measurement","Record a sensor measurement.",
                [("sensor","string","Sensor."),("value","number","Value."),("unit","string","Unit."),("timestamp","string","ISO timestamp.")])],
            ["record_measurement"]),
    ]),
    new("task_planning", "Task Planning",
    [
        new("Break down the goal 'build a REST API with authentication' into ordered sub-tasks.",
            [Tool("create_plan","Create an ordered task plan.",
                [("goal","string","Goal."),("steps","string","JSON array of steps.")]),
             Tool("write_file","Write a file.",[("path","string","Path."),("content","string","Content.")])],
            ["create_plan"]),
        new("Plan how to migrate a SQL database to a new schema with zero downtime.",
            [Tool("create_plan","Create an ordered task plan.",
                [("goal","string","Goal."),("steps","string","JSON array of steps.")]),
             Tool("write_file","Write a file.",[("path","string","Path."),("content","string","Content.")])],
            ["create_plan"]),
    ]),
];

static List<(string cat, string tool)> AllCategoryTools() =>
[
    ("file_ops","read_file"), ("file_ops","write_file"), ("file_ops","list_dir"),
    ("network","http_get"), ("network","http_post"),
    ("code_exec","run_shell"), ("code_exec","eval_python"),
    ("data_transform","parse_csv"), ("data_transform","convert_json"),
    ("system_inspect","list_processes"), ("system_inspect","get_env_vars"),
    ("structured_output","report_status"), ("structured_output","record_measurement"),
    ("task_planning","create_plan"),
];

static string BuildCatSummary(Dictionary<string, string> results)
{
    var labels = new (string id, string lbl)[]
    {
        ("file_ops","FILE"),("network","NET"),("code_exec","CODE"),
        ("data_transform","DATA"),("system_inspect","SYS"),
        ("structured_output","STRCT"),("task_planning","PLAN"),
    };
    return string.Join(" ", labels.Select(x =>
    {
        var v = results.GetValueOrDefault(x.id, "?");
        return $"{x.lbl}:{(v == "pass" ? "✓" : v == "partial" ? "~" : "✗")}";
    }));
}

// =============================================================================
// SCHEMA EVOLUTION HELPERS
// =============================================================================

static List<EvolveToolDef> EvolveToolDefs() =>
[
    new("add_numbers",
        "Add 17 and 25 using the add_numbers tool.",
        Tool("add_numbers","Add two integers.",
             [("a","integer","First number."),("b","integer","Second number.")]),
        (n, a) =>
        {
            if (!n.Equals("add_numbers", StringComparison.OrdinalIgnoreCase)) return $"wrong tool: {n}";
            var av = a.GetValueOrDefault("a")?.ToString();
            var bv = a.GetValueOrDefault("b")?.ToString();
            if (av != "17" && av != "17.0") return $"a={av} expected 17";
            if (bv != "25" && bv != "25.0") return $"b={bv} expected 25";
            return null;
        }),
    new("write_text",
        "Write a file named result.txt with content 'evolution complete'.",
        Tool("write_text","Write text content to a file.",
             [("filename","string","File name."),("content","string","Content.")]),
        (n, a) =>
            n.Equals("write_text", StringComparison.OrdinalIgnoreCase) &&
            a.GetValueOrDefault("filename")?.ToString()?.Contains("result") == true
            ? null : $"expected write_text(filename~result), got {n}"),
    new("report_status",
        "Report that the operation succeeded: status 'ok', message 'evolution done', code 0.",
        Tool("report_status","Report a structured result.",
             [("status","string","ok or error."),("message","string","Message."),("code","integer","Exit code.")]),
        (n, a) =>
        {
            if (!n.Equals("report_status", StringComparison.OrdinalIgnoreCase)) return $"wrong tool: {n}";
            var st = a.GetValueOrDefault("status")?.ToString() ?? "";
            return new[]{"ok","success","pass"}.Any(v => st.Equals(v, StringComparison.OrdinalIgnoreCase))
                ? null : $"status='{st}'";
        }),
];

static List<(string id, string mutation, object schema)> GenerateMutations(
    EvolveToolDef def, object? parentSchema, int gen)
{
    var list = new List<(string, string, object)>();
    list.Add(($"g{gen}_seed",     "Original",       def.BaseSchema));
    list.Add(($"g{gen}_terse",    "TerseDesc",      MutateTerseDesc(def.BaseSchema)));
    list.Add(($"g{gen}_reqfirst", "RequiredFirst",  MutateReqFirst(def.BaseSchema)));
    if (gen >= 1 && parentSchema != null)
        list.Add(($"g{gen}_parent",  "ParentBreed",   parentSchema));
    return list;
}

static object MutateTerseDesc(object schema)
{
    try
    {
        var node  = JsonNode.Parse(JsonSerializer.Serialize(schema))!;
        var props = node["function"]?["parameters"]?["properties"];
        if (props is JsonObject po)
            foreach (var key in po.Select(kv => kv.Key).ToList())
                if (po[key] is JsonObject propObj)
                    propObj["description"] = JsonValue.Create(key);
        return JsonSerializer.Deserialize<object>(node.ToJsonString())!;
    }
    catch { return schema; }
}

static object MutateReqFirst(object schema)
{
    try
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(schema))!;
        var fp   = node["function"]?["parameters"];
        if (fp is JsonObject po)
        {
            var t = po["type"]?.DeepClone();
            var r = po["required"]?.DeepClone();
            var p = po["properties"]?.DeepClone();
            po.Clear();
            if (t != null) po["type"]       = t;
            if (r != null) po["required"]   = r;
            if (p != null) po["properties"] = p;
        }
        return JsonSerializer.Deserialize<object>(node.ToJsonString())!;
    }
    catch { return schema; }
}

// =============================================================================
// MODEL DISCOVERY
// =============================================================================

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

// =============================================================================
// CONSOLE HELPERS
// =============================================================================

static void C(string text, ConsoleColor color, bool newline = true)
{
    Console.ForegroundColor = color;
    if (newline) Console.WriteLine(text);
    else         Console.Write(text);
    Console.ResetColor();
}

static string? ArgValue(string[] a, string key)
{
    var idx = Array.IndexOf(a, key);
    return idx >= 0 && idx + 1 < a.Length ? a[idx + 1] : null;
}

static void PrintHelp()
{
    Console.WriteLine("""
    ⬡ tool-probe — TheOrc GOBLIN MIND tool-call capability tester

    Usage:
      tool-probe [dispatch] [options]     Dispatch probe (5 tests × native + text)
      tool-probe format     [options]     Format fingerprinting (5 serialization variants)
      tool-probe categories [options]     Category boundary mapping (7 task categories)
      tool-probe full       [options]     All probes — dispatch + format + categories
      tool-probe evolve     [options]     Schema evolution (systematic mutation search)
      tool-probe list       [options]     Show stored profiles, no new tests
      tool-probe --help                   Show this message

    Options:
      --model <id>         Only test this model (partial match OK)
      --host  <url>        Ollama host (default: http://localhost:11434)
      --json               Output JSON to stdout (all subcommands)
      --generations N      (evolve) Number of mutation generations (default: 3)
      --tool  <name>       (evolve) Restrict to this tool name

    Exit codes:  0 = all pass  |  1 = failures  |  2 = Ollama unreachable

    Examples:
      tool-probe                               All models, dispatch probe
      tool-probe full --model qwen2.5          Full GOBLIN MIND profile
      tool-probe format --json                 Format probe, JSON output
      tool-probe categories --model llama3     Category map
      tool-probe evolve --model qwen2.5 --generations 5 --tool add_numbers
      tool-probe list                          Show all stored profiles

    Profiles stored at:
      %AppData%\OrchestratorIDE\tool-call-profiles.json
    """);
}

// =============================================================================
// PROFILE STORE — self-contained mirror with GOBLIN MIND fields
// =============================================================================

static class ProfileStore
{
    public static readonly string ProfilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "tool-call-profiles.json");

    private static readonly JsonSerializerOptions _json = new()
    { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task SaveDispatchAsync(string modelId, int native, int text,
        ToolCallMode rec, IEnumerable<Outcome> outcomes)
        => await MutateAsync(modelId, p =>
        {
            p.TestedAt        = DateTime.UtcNow;
            p.RecommendedMode = rec.ToString();
            p.NativePasses    = native;
            p.TextPasses      = text;
            p.TotalTests      = outcomes.Count();
            p.TestPassMap     = outcomes.ToDictionary(o => $"{o.Test}_{o.Mode}", o => o.Status == Result.Pass);
        });

    public static async Task SaveFormatAsync(string modelId, string preferred, Dictionary<string, int> scores)
        => await MutateAsync(modelId, p =>
        {
            p.PreferredFormat = preferred;
            p.FormatScores    = scores;
            if (p.TestedAt == default) p.TestedAt = DateTime.UtcNow;
        });

    public static async Task SaveCategoriesAsync(string modelId, Dictionary<string, string> results, string summary)
        => await MutateAsync(modelId, p =>
        {
            p.CategoryResults = results;
            p.CategorySummary = summary;
            if (p.TestedAt == default) p.TestedAt = DateTime.UtcNow;
        });

    private static async Task MutateAsync(string modelId, Action<StoredProfile> mutate)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
            var all = Load();
            if (!all.TryGetValue(modelId, out var p)) p = new StoredProfile { ModelId = modelId };
            mutate(p);
            all[modelId] = p;
            await File.WriteAllTextAsync(ProfilesPath, JsonSerializer.Serialize(all, _json));
        }
        catch { }
    }

    public static List<StoredProfile> LoadAll()
        => [.. Load().Values.OrderBy(p => p.ModelId)];

    private static Dictionary<string, StoredProfile> Load()
    {
        if (!File.Exists(ProfilesPath)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, StoredProfile>>(File.ReadAllText(ProfilesPath), _json) ?? []; }
        catch { return []; }
    }
}

class StoredProfile
{
    // Dispatch
    public string   ModelId          { get; set; } = "";
    public DateTime TestedAt         { get; set; }
    public string   RecommendedMode  { get; set; } = "Unknown";
    public int      NativePasses     { get; set; }
    public int      TextPasses       { get; set; }
    public int      TotalTests       { get; set; }
    public Dictionary<string, bool>     TestPassMap      { get; set; } = [];
    // Format fingerprint
    public string?                      PreferredFormat  { get; set; }
    public Dictionary<string, int>?     FormatScores     { get; set; }
    // Category map
    public Dictionary<string, string>?  CategoryResults  { get; set; }
    public string?                      CategorySummary  { get; set; }

    public ToolCallMode RecommendedModeEnum =>
        Enum.TryParse<ToolCallMode>(RecommendedMode, out var m) ? m : ToolCallMode.Unknown;
}

// =============================================================================
// DOMAIN TYPES
// =============================================================================

enum TestId   { BasicCall, IntArgs, MultilineContent, ToolSelection, StructuredOutput }
enum Mode     { Native, Text }
enum Result   { Pass, Fail, Timeout, Error }
enum ToolCallMode { Unknown, Native, TextJson, Both, None }

record Def(string Prompt, IReadOnlyList<object> Tools,
    Func<string, Dictionary<string, object?>, string?> Verify);
record Outcome(TestId Test, Mode Mode, Result Status,
    string? Actual = null, string? Reason = null);
record ModelResult(string Model, int NativePasses, int TextPasses,
    ToolCallMode Recommended, List<Outcome> Outcomes);
record FormatResult(string Model, string Preferred,
    Dictionary<string, int> Scores, Dictionary<string, string> Details);
record CategoryProbeResult(string Model,
    Dictionary<string, string> Results, string Summary);
record CatDef(string Id, string DisplayName, List<CatTest> Tests);
record CatTest(string Prompt, object[] Tools, string[] ExpectedTools);
record EvolveToolDef(string Name, string Prompt, object BaseSchema,
    Func<string, Dictionary<string, object?>, string?> Verify);
record EvolveVariantResult(string Model, string ToolName, string VariantId,
    string Mutation, bool Passed, string? FailReason);
