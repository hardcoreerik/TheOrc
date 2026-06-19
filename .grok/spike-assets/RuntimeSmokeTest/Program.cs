// RUNTIME_SWITCH_PLAN.md Stage 1 — manual smoke test of LLamaSharpRuntime itself.
// Throwaway harness, not shipped code. References the real OrchestratorIDE.dll so this
// exercises the actual shipped class, not a reimplementation.
//
// Unlike the §7 HotSwapSpike (which used raw BatchedExecutor directly, bypassing
// LLamaSharpRuntime entirely), this harness goes through LLamaSharpRuntime.StreamCompletionAsync
// itself — the path nothing has ever exercised end-to-end against a real model before.
//
// Tests:
//   1. Plain message, no tools — confirm coherent streamed output.
//   2. Message + a tool definition — confirm the tool-injection block renders and a
//      text-format tool call gets parsed out of the model's output.
//   3. Run a second StreamCompletionAsync call — confirm the _hasEmbeddedTemplate cache
//      doesn't break anything on a repeat call (can't inspect the private field directly,
//      but a second call behaving identically to the first is the externally-observable proof).

using System.Diagnostics;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

var basePath = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("SMOKETEST_BASE_GGUF")
      ?? @"F:\Ai\GarfChat\checkpoints\android-test-models\Llama-3.2-3B-Instruct-uncensored.Q4_K_M.gguf";

if (!File.Exists(basePath))
{
    Console.WriteLine($"MISSING base model: {basePath} (pass as arg[0] or set SMOKETEST_BASE_GGUF)");
    return 1;
}

// CurrentDirectory (not AppContext.BaseDirectory + relative ".." arithmetic, which is fragile
// across dotnet run / dotnet exec / Release / VS launch profiles) -- dotnet run sets this to
// the project directory by default, which is exactly where a throwaway harness's log belongs.
// Default name matches what gets committed as evidence (RUNTIME_SWITCH_PLAN.md references this
// exact filename) -- override via arg[1] or SMOKETEST_RESULTS_FILE if needed.
var resultsFileName = args.Length > 1 ? args[1]
    : Environment.GetEnvironmentVariable("SMOKETEST_RESULTS_FILE") ?? "stage1-results.log";
var resultsPath = Path.Combine(Directory.GetCurrentDirectory(), resultsFileName);
var results = new System.Text.StringBuilder();
void Log(string line) { Console.WriteLine(line); results.AppendLine(line); }

await using var runtime = new LLamaSharpRuntime();

Log("=== Loading base model ===");
var sw = Stopwatch.StartNew();
var loadResult = await runtime.LoadModelAsync(basePath, options: new RuntimeOptions { ContextLength = 2048 });
Log($"LoadModelAsync: success={loadResult.Success} ({sw.ElapsedMilliseconds} ms) — {loadResult.Message ?? "(no message)"}");
if (!loadResult.Success)
{
    Log("Load failed, aborting.");
    File.WriteAllText(resultsPath, results.ToString());
    return 1;
}

// ── Test 1: plain message, no tools ──────────────────────────────────────
Log("\n=== Test 1: plain message, no tools ===");
var history1 = new List<AgentMessage>
{
    new() { Role = MessageRole.System, Content = "You are a helpful assistant. Keep replies to one sentence." },
    new() { Role = MessageRole.User,   Content = "Say hello and name one color." },
};

sw.Restart();
var output1 = new System.Text.StringBuilder();
var usage1 = (prompt: 0, completion: 0);
await foreach (var token in runtime.StreamCompletionAsync(
    "unused", history1, tools: null, temperature: 0.1, maxTokens: 64,
    onUsage: (p, c) => usage1 = (p, c)))
{
    output1.Append(token);
}
Log($"Elapsed: {sw.ElapsedMilliseconds} ms | usage: prompt={usage1.prompt} completion={usage1.completion}");
Log($"Output: {output1}");

var stats1 = runtime.GetStats();
Log($"GetStats(): TokensPerSecond={stats1.TokensPerSecond:F1} LastTimeToFirstToken={stats1.LastTimeToFirstToken?.TotalMilliseconds:F0}ms");

// ── Test 2: message + a tool definition ──────────────────────────────────
Log("\n=== Test 2: message + tool definition, expecting a tool call ===");
var tool = new ToolDefinition
{
    Name = "get_weather",
    Description = "Gets the current weather for a city. Call this whenever the user asks about weather.",
    Parameters = new Dictionary<string, ToolParameter>
    {
        ["city"] = new ToolParameter("string", "The city name"),
    },
    Required = ["city"],
};

var history2 = new List<AgentMessage>
{
    new() { Role = MessageRole.System, Content = "You are a helpful assistant with access to tools. Use the get_weather tool when asked about weather." },
    new() { Role = MessageRole.User,   Content = "What's the weather like in Tokyo?" },
};

ToolCall? capturedToolCall = null;
sw.Restart();
var output2 = new System.Text.StringBuilder();
await foreach (var token in runtime.StreamCompletionAsync(
    "unused", history2, tools: [tool.ToOllamaSchema()], temperature: 0.1, maxTokens: 128,
    onToolCall: tc => capturedToolCall = tc))
{
    output2.Append(token);
}
Log($"Elapsed: {sw.ElapsedMilliseconds} ms");
Log($"Raw output: {output2}");
Log($"Parsed tool call: {(capturedToolCall is null ? "(none parsed)" : $"{capturedToolCall.Name}({string.Join(", ", capturedToolCall.Arguments.Select(kv => $"{kv.Key}={kv.Value}"))})")}");

// ── Test 3: second call — confirm template-cache doesn't break anything ──
Log("\n=== Test 3: second StreamCompletionAsync call (template-probe cache path) ===");
sw.Restart();
var output3 = new System.Text.StringBuilder();
await foreach (var token in runtime.StreamCompletionAsync(
    "unused", history1, tools: null, temperature: 0.1, maxTokens: 64))
{
    output3.Append(token);
}
Log($"Elapsed: {sw.ElapsedMilliseconds} ms");
Log($"Output: {output3}");
Log($"Matches Test 1 output: {output3.ToString() == output1.ToString()}");

Log("\n=== DONE ===");
File.WriteAllText(resultsPath, results.ToString());
Console.WriteLine($"\nFull results also written to {Path.GetFullPath(resultsPath)}");
return 0;
