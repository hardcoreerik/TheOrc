// swarmcli — headless TheOrc swarm runner for Training Pit capture farming.
//
// Runs SwarmSession without the WPF shell so capture runs can be driven from
// a terminal or script. DatasetCapture stages qualifying boss plans exactly
// as it does in the app (immediately after decomposition, before workers run),
// so --plan-only is the fastest capture loop: decompose → stage → stop.
//
// Usage:
//   swarmcli --goal "<text>" [--workspace <dir>] [--plan-only]
//            [--boss <model>] [--coder <model>] [--researcher <model>]
//            [--host <url>] [--timeout <seconds>]
//
// Exit codes:
//   0  capture staged (good or bad — reviewable either way)
//   1  run error / boss returned no tasks
//   2  run finished but nothing staged (marginal 40–69 plan)
//   3  timeout

using System.IO;
using System.Linq;
using System.Text;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;

Console.OutputEncoding = Encoding.UTF8;

// ── Args ─────────────────────────────────────────────────────────────────────

string? goal       = null;
string  workspace  = Directory.GetCurrentDirectory();
string  boss       = "theorc-boss:gemma4";
string  coder      = "qwen2.5-coder:7b";
string? researcher = null;
string  host       = "http://localhost:11434";
int     timeoutSec = 600;
bool    planOnly   = false;

for (int i = 0; i < args.Length; i++)
{
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--goal":       goal       = Next();          break;
        case "--workspace":  workspace  = Next() ?? workspace; break;
        case "--boss":       boss       = Next() ?? boss;  break;
        case "--coder":      coder      = Next() ?? coder; break;
        case "--researcher": researcher = Next();          break;
        case "--host":       host       = Next() ?? host;  break;
        case "--timeout":    timeoutSec = int.TryParse(Next(), out var t) ? t : timeoutSec; break;
        case "--plan-only":  planOnly   = true;            break;
        case "-h" or "--help":
            Console.WriteLine("swarmcli --goal \"<text>\" [--workspace <dir>] [--plan-only] " +
                              "[--boss <m>] [--coder <m>] [--researcher <m>] [--host <url>] [--timeout <s>]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]} (try --help)");
            return 1;
    }
}

if (string.IsNullOrWhiteSpace(goal))
{
    Console.Error.WriteLine("Missing required --goal");
    return 1;
}

workspace = Path.GetFullPath(workspace);
if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"Workspace does not exist: {workspace}");
    return 1;
}

researcher ??= coder;

Console.WriteLine($"swarmcli — workspace: {workspace}");
Console.WriteLine($"  boss: {boss}  coder: {coder}  researcher: {researcher}");
Console.WriteLine($"  mode: {(planOnly ? "plan-only (stop after boss plan capture)" : "full run")}  timeout: {timeoutSec}s");
Console.WriteLine($"  goal: {goal}");
Console.WriteLine();

// ── Staging snapshot (to report what this run added) ─────────────────────────

var stagingDir = Path.Combine(workspace, ".orc", "swarm", "dataset-staging");
var before = Directory.Exists(stagingDir)
    ? Directory.GetFiles(stagingDir).Select(Path.GetFileName).ToHashSet()
    : [];

// ── Run ──────────────────────────────────────────────────────────────────────

var ollama  = new OllamaClient(host);
var session = new SwarmSession(ollama, boss, workspace, coder, researcher);

bool planSeen = false, errored = false;

session.OnActivity += (agent, msg) => Console.WriteLine($"  [{agent}] {msg}");
session.OnError    += msg => { errored = true; Console.Error.WriteLine($"  [ERROR] {msg}"); };

session.OnTasksPlanned += tasks =>
{
    planSeen = true;
    Console.WriteLine();
    Console.WriteLine($"── Boss plan: {tasks.Count} task(s) ──");
    foreach (var t in tasks)
        Console.WriteLine($"  • {t.Role,-12} {t.Title}");
    Console.WriteLine();

    if (planOnly)
    {
        Console.WriteLine("Plan captured (staging happens before this event) — stopping run.");
        session.Stop();
    }
};

session.OnSwarmComplete += _ => Console.WriteLine("Swarm complete.");

var runTask  = session.RunAsync(goal!);
var finished = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));

bool timedOut = finished != runTask;
if (timedOut)
{
    Console.Error.WriteLine($"Timeout after {timeoutSec}s — stopping run.");
    session.Stop();
    await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));
}
else
{
    try { await runTask; }
    catch (OperationCanceledException) { /* expected for plan-only stop */ }
    catch (Exception ex) { errored = true; Console.Error.WriteLine($"  [ERROR] {ex.Message}"); }
}

// ── Report staged captures ───────────────────────────────────────────────────

var added = Directory.Exists(stagingDir)
    ? Directory.GetFiles(stagingDir).Select(Path.GetFileName)
        .Where(f => f is not null && !before.Contains(f)).Cast<string>().OrderBy(f => f).ToList()
    : [];

Console.WriteLine();
if (added.Count > 0)
{
    Console.WriteLine($"Staged {added.Count} capture(s):");
    foreach (var f in added)
        Console.WriteLine($"  {Path.Combine(".orc", "swarm", "dataset-staging", f)}");
    return 0;
}

if (timedOut)            { Console.WriteLine("No capture staged (timeout).");                return 3; }
if (errored || !planSeen){ Console.WriteLine("No capture staged (run error / no plan).");    return 1; }
Console.WriteLine("No capture staged — plan likely scored marginal (40–69, silently skipped).");
return 2;
