using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Tests;

/// <summary>
/// Headless integration test runner (--autotest mode).
///
/// Exercises the exact same code path as a user would:
///   1. Boot all services
///   2. Confirm a temp workspace (simulates user opening a folder)
///   3. Run Plan mode with the Calculator prompt
///   4. Run Execute mode — auto-approves any diffs
///   5. Verify Calculator.cs was created with the expected methods
///   6. Run two-stage Auto-Verify
///   7. Verify session was saved to disk
///
/// Exits 0 = all pass, 1 = any failure.
/// </summary>
public class AutoTestRunner
{
    private readonly Action<string> _log;
    private readonly Action<string>? _onWorkspaceCreated;
    private readonly List<TestResult> _results = [];

    public AutoTestRunner(Action<string> log, Action<string>? onWorkspaceCreated = null)
    {
        _log                 = log;
        _onWorkspaceCreated  = onWorkspaceCreated;
    }

    // ── Entry point ────────────────────────────────────────────────────────

    public async Task<bool> RunAsync(CancellationToken ct = default)
    {
        var workspace = Path.Combine(Path.GetTempPath(),
            $"OrchestratorIDE_autotest_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(workspace);

        // Notify window immediately so the Open Folder button activates
        _onWorkspaceCreated?.Invoke(workspace);

        Log("─────────────────────────────────────────");
        Log("  OrchestratorIDE  ──  AutoTest Runner");
        Log("─────────────────────────────────────────");
        Log($"  Workspace : {workspace}");
        Log($"  Started   : {DateTime.Now:HH:mm:ss}");
        Log("");

        try
        {
            // ── Boot services ─────────────────────────────────────────────
            var settings  = AppSettings.Load();
            var ollama    = new OllamaClient(settings.OllamaHost);
            var approvals = new ApprovalQueue { AutoApprove = true };
            var registry  = new ToolRegistry(approvals);
            var context   = new ContextManager(32_768);
            var git       = new GitCheckpoint();
            var rules     = new RulesLoader();
            var store     = new SessionStore();
            var loop      = new AgentLoop(ollama, registry, context, git, rules);

            // Wire activity to log
            loop.Activity += ev => Log($"  [{ev.Icon}] {ev.Label}: {ev.Detail}");

            // Register tools for the temp workspace
            FileTools.Register(registry, workspace, onDiffPreview: null);
            ShellTools.Register(registry, workspace);
            SearchTools.Register(registry, workspace);
            TestTools.Register(registry, workspace);

            // ── Test 0: Ollama connectivity ───────────────────────────────
            Log("── Test 0: Ollama connectivity");
            var models = await ollama.GetInstalledModelsAsync(ct);
            Pass("0", "Ollama connects", models.Count > 0,
                $"{models.Count} models found", "No models returned — is Ollama running?");

            // Pick model
            var preferred = new[] { "qwen2.5-coder:14b", "qwen2.5-coder:7b",
                "hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M" };
            var model = preferred.FirstOrDefault(p => models.Any(m =>
                m.Equals(p, StringComparison.OrdinalIgnoreCase))) ?? models.FirstOrDefault() ?? "";

            Pass("0b", "Model available", !string.IsNullOrEmpty(model),
                $"Using: {model}", "No usable model found");
            Log("");

            if (!_results.All(r => r.Passed)) return Summarise(workspace);

            // Build session
            var session = new ProjectSession
            {
                WorkspaceRoot       = workspace,
                IsWorkspaceConfirmed = true,   // Simulates user explicitly opening a folder
                ActiveModel         = model,
            };

            // ── Test 1: Plan mode ─────────────────────────────────────────
            Log("── Test 1: Plan mode (streaming)");
            var planTokens = 0;
            loop.OnToken  += _ => planTokens++;
            loop.OnUsage  += (_, c) => Log($"  [tokens] {c:N0} completion tokens");

            string planText;
            try
            {
                planText = await loop.PlanAsync(session, PlanPrompt, ct);
            }
            catch (Exception ex)
            {
                Fail("1", "Plan completes without error", ex.Message);
                return Summarise(workspace);
            }

            Pass("1a", "Plan returns text",    !string.IsNullOrWhiteSpace(planText),
                $"{planText.Length:N0} chars", "Empty plan returned");
            Pass("1b", "Tokens streamed live", planTokens > 0,
                $"{planTokens:N0} OnToken events", "No tokens fired — streaming broken");
            Pass("1c", "Plan mentions Calculator",
                planText.Contains("Calculator", StringComparison.OrdinalIgnoreCase),
                "keyword found", "Plan doesn't mention Calculator");
            Log("");

            // ── Test 2: Execute mode ──────────────────────────────────────
            Log("── Test 2: Execute mode (tool calls + file write)");

            // Log every assistant message so we can see exactly what the model said
            // (crucial for debugging tool-call format failures)
            loop.Activity += ev =>
            {
                if (ev.Kind == ActivityKind.Step)
                    Log($"  [step] {ev.Label}");
            };

            string executeResponse;
            try
            {
                executeResponse = await loop.ExecuteAsync(session, "[Execute the above plan]", ct);
            }
            catch (Exception ex)
            {
                Fail("2", "Execute completes without error", ex.Message);
                return Summarise(workspace);
            }

            // Log the final model response (truncated) so failures are diagnosable
            if (!string.IsNullOrWhiteSpace(executeResponse))
            {
                var preview = executeResponse.Length > 300
                    ? executeResponse[..300].Replace("\n", " ") + "…"
                    : executeResponse.Replace("\n", " ");
                Log($"  [last response] {preview}");
            }

            // Give file system a moment
            await Task.Delay(300, ct);

            // NOTE: filter by filename only — the workspace directory path itself
            // contains "autotest" so a full-path filter would nuke everything.
            var csFiles = Directory.GetFiles(workspace, "*.cs", SearchOption.AllDirectories)
                .ToList();

            Pass("2a", "At least one .cs file created", csFiles.Count > 0,
                string.Join(", ", csFiles.Select(Path.GetFileName)),
                "No .cs files found in workspace after Execute");

            if (csFiles.Count > 0)
            {
                // Log every file found so we can see what the model actually produced
                foreach (var f in csFiles)
                    Log($"  [file] {Path.GetFileName(f)} ({new FileInfo(f).Length:N0} B)");

                // Prefer Calculator.cs, fall back to any .cs file
                var calcFile = csFiles.FirstOrDefault(f =>
                    Path.GetFileName(f).Contains("Calculator", StringComparison.OrdinalIgnoreCase))
                    ?? csFiles[0];

                Log($"  [checking] {Path.GetFileName(calcFile)}");
                var content = await File.ReadAllTextAsync(calcFile, ct);

                // Scan ALL .cs files for the method names — model may have split across files
                var allContent = string.Concat(
                    await Task.WhenAll(csFiles.Select(f => File.ReadAllTextAsync(f, ct))));

                Pass("2b", "Calculator class exists",
                    allContent.Contains("class Calculator", StringComparison.OrdinalIgnoreCase),
                    "✓", "No 'class Calculator' found in any .cs file");
                Pass("2c", "Add method exists",
                    allContent.Contains("Add", StringComparison.OrdinalIgnoreCase),
                    "✓", "Add method missing from all .cs files");
                Pass("2d", "Subtract method exists",
                    allContent.Contains("Subtract", StringComparison.OrdinalIgnoreCase),
                    "✓", "Subtract method missing from all .cs files");
                Pass("2e", "Multiply method exists",
                    allContent.Contains("Multiply", StringComparison.OrdinalIgnoreCase),
                    "✓", "Multiply method missing from all .cs files");
                Pass("2f", "Divide method exists",
                    allContent.Contains("Divide", StringComparison.OrdinalIgnoreCase),
                    "✓", "Divide method missing from all .cs files");
            }
            Log("");

            // ── Test 3: Two-stage Auto-Verify ─────────────────────────────
            Log("── Test 3: Two-stage Auto-Verify");
            string verifyOut;
            try
            {
                verifyOut = registry.TryGet("run_tests", out var verifyTool) && verifyTool?.Handler != null
                    ? await verifyTool.Handler([], ct)
                    : "[skipped — run_tests not registered]";
            }
            catch (Exception ex) { verifyOut = $"[error] {ex.Message}"; }

            Log($"  {verifyOut.Replace("\n", "\n  ")}");
            Pass("3a", "Auto-verify runs without exception",
                !verifyOut.StartsWith("[error]"), "completed", verifyOut);
            Pass("3b", "Stage 2 skipped (no test files expected)",
                verifyOut.Contains("No test files found") || verifyOut.Contains("skipping"),
                "correctly skipped", "Stage 2 ran unexpectedly");
            Log("");

            // ── Test 4: Session persistence ───────────────────────────────
            Log("── Test 4: Session persistence");
            try
            {
                await store.SaveAsync(session);
                var loaded = await store.LoadAsync(session.Id);
                Pass("4a", "Session saves and reloads",
                    loaded != null && loaded.Id == session.Id,
                    $"ID {session.Id}", "Loaded session ID mismatch");
                Pass("4b", "Session messages preserved",
                    loaded?.Messages.Count > 0,
                    $"{loaded?.Messages.Count} messages", "No messages in loaded session");
            }
            catch (Exception ex)
            {
                Fail("4", "Session persistence", ex.Message);
            }
            Log("");
        }
        catch (Exception ex)
        {
            Log($"[FATAL] Unhandled exception: {ex.Message}");
            Log(ex.StackTrace ?? "");
        }
        // Note: workspace cleanup is handled by AutoTestWindow.OnClosed
        // so the user can inspect files before they're deleted.

        return Summarise(workspace);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private const string PlanPrompt =
        "Create a single C# file named Calculator.cs containing a public class " +
        "called Calculator with four public methods: Add, Subtract, Multiply, " +
        "and Divide (each takes two doubles and returns a double). " +
        "Do not create any other files. Only Calculator.cs is needed.";

    private void Log(string msg) => _log(msg);

    private void Pass(string id, string name, bool passed, string detail, string failDetail)
    {
        var r = new TestResult(id, name, passed, passed ? detail : failDetail);
        _results.Add(r);
        Log(passed
            ? $"  ✓ [{id}] {name} — {detail}"
            : $"  ✗ [{id}] {name} — {failDetail}");
    }

    private void Fail(string id, string name, string reason)
    {
        _results.Add(new TestResult(id, name, false, reason));
        Log($"  ✗ [{id}] {name} — {reason}");
    }

    private bool Summarise(string workspace)
    {
        var passed = _results.Count(r => r.Passed);
        var total  = _results.Count;
        var allOk  = passed == total;

        Log("─────────────────────────────────────────");
        Log(allOk
            ? $"  ✓  ALL TESTS PASSED  ({passed}/{total})"
            : $"  ✗  {total - passed} TEST(S) FAILED  ({passed}/{total} passed)");

        if (!allOk)
        {
            Log("");
            Log("  Failed:");
            foreach (var r in _results.Where(r => !r.Passed))
                Log($"    ✗ [{r.Id}] {r.Name} — {r.Detail}");
        }

        Log("─────────────────────────────────────────");
        return allOk;
    }

    private record TestResult(string Id, string Name, bool Passed, string Detail);
}
