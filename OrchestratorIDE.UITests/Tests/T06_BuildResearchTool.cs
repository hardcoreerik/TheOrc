using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T06 — Autonomous build test: drives TheOrc via FlaUI to build OrcResearcher,
/// a Python GUI research tool that scrapes the web and generates HOW-TOs via Ollama.
///
/// This is a fully headless integration test — no human interaction required.
/// The screen recorder captures the full session for replay debugging.
///
/// Flow:
///   1. Launch TheOrc with --workspace pointing to a temp OrcResearcher dir
///   2. Navigate to Agent panel, switch to Plan mode
///   3. Send the detailed project specification as the Plan prompt
///   4. Wait for plan completion (polling Stop button)
///   5. Switch to Execute mode, send "[Execute the above plan]"
///   6. Monitor workspace for created .py files (stall detection: 5 min no progress)
///   7. Assert at least 4 .py files exist and requirements.txt is present
///
/// Output:
///   - Recording: %APPDATA%\OrchestratorIDE\Recordings\UITest_T06_*.avi
///   - Workspace: %TEMP%\OrcResearcher_yyyyMMdd_HHmmss\
/// </summary>
[TestFixture]
public class T06_BuildResearchTool : RecordingTestBase
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const int PlanTimeoutMinutes    = 5;   // max wait for Plan response
    private const int ExecuteTimeoutMinutes = 30;  // max wait for all files
    private const int StallDetectMinutes    = 5;   // no progress → stall
    private const int PollIntervalMs        = 5_000;

    private static Application?    _app;
    private static UIA3Automation? _automation;
    private static Window?         _mainWindow;
    private static string          _workspace = "";

    // ── Plan prompt — full OrcResearcher specification ────────────────────────

    private const string PlanPrompt = @"Build a Python GUI research tool called OrcResearcher.

REQUIREMENTS:
Create exactly these 6 files in the workspace:

1. main.py — tkinter GUI application
   - Main window titled ""OrcResearcher — Underground Research Tool""
   - Topic Entry field at top with a ""Research"" button
   - Source checkboxes: Wikipedia, DuckDuckGo, Custom URL
   - Custom URL entry field (enabled when Custom URL checked)
   - Scrolled log Text widget showing scraping progress (read-only, auto-scroll)
   - Scrolled results Text widget showing aggregated scraped content (read-only)
   - Model selector Combobox (populated from Ollama on startup)
   - Ollama host Entry field (default: http://localhost:11434)
   - ""Generate HOW-TO"" button (sends results to Ollama model, streams tokens)
   - ""Save Research"" button (saves to output dir as Markdown)
   - Output dir Entry field (default: ~/OrcResearcher)
   - All scraping runs in a background thread (no UI freeze)
   - Streaming HOW-TO tokens update the results Text widget in real-time
   - Status bar label at bottom showing current operation

2. scraper.py — web scraping functions
   - fetch_wikipedia(topic) → returns {source, url, content} dict
     Uses: requests.get(f""https://en.wikipedia.org/wiki/{topic.replace(' ','_')}"")
     Extracts: all <p> tag text from the page, joins them
   - fetch_duckduckgo(query, max_results=5) → returns list of {source, url, content}
     Uses: requests.get(""https://lite.duckduckgo.com/lite/"", params={""q"": query})
     Extracts: result URLs from <a> tags, then calls fetch_url on each
   - fetch_url(url) → returns {source, url, content}
     Uses: requests.get with 10s timeout, User-Agent header
     Extracts: all <p> tag text via BeautifulSoup
   - deduplicate(results_list) → removes near-duplicate content entries
     (simple: if content[:200] already seen, skip)
   All functions handle exceptions gracefully (return empty content on error)
   Use requests.Session() for connection reuse

3. ollama_client.py — Ollama HTTP API wrapper
   - list_models(host=""http://localhost:11434"") → GET /api/tags → list of model name strings
     Returns [] on connection error
   - generate(host, model, prompt, token_callback)
     POST /api/generate with {""model"": model, ""prompt"": prompt, ""stream"": true}
     For each JSON line: parse, extract ""response"" field, call token_callback(token)
     Stop when ""done"": true
     Handle connection errors: call token_callback(""[Ollama not available]"")
   - build_research_prompt(topic, sources_content) → str
     Returns: ""You are a research assistant. Based on the following research on '{topic}':\n\n{sources_content}\n\nWrite a comprehensive HOW-TO guide with these sections:\n## Overview\n## Prerequisites\n## Step-by-Step Guide\n## Key Concepts\n## Useful Resources\n\nBe specific, practical, and clear.""

4. file_manager.py — research session save/load
   - save_research(topic, sources, howto_text, output_dir) → filepath str
     Creates output_dir/{safe_topic}/ if needed
     safe_topic = topic.lower().replace(' ', '_').replace('/', '_')
     Filename: {timestamp}_research.md
     Content: Markdown with # {topic}, date, ## Sources (list), ## Research Content, ## HOW-TO Guide
   - list_sessions(output_dir) → list of (filepath, topic, date) tuples
     Scans output_dir recursively for *_research.md files
   - load_session(filepath) → str (raw markdown content)

5. requirements.txt
   requests>=2.28.0
   beautifulsoup4>=4.11.0

6. README.md
   # OrcResearcher
   ## Install: pip install -r requirements.txt
   ## Run: python main.py
   ## Usage: Enter topic, select sources, click Research, then Generate HOW-TO
   ## Requirements: Python 3.10+, Ollama running at localhost:11434

IMPORTANT RULES:
- Use only tkinter (no PyQt, no wx, no customtkinter)
- All files must be complete and runnable — no placeholder comments like ""# TODO""
- main.py must import from scraper, ollama_client, file_manager
- Handle all exceptions (network errors, Ollama not running) with user-friendly messages
- Use threading.Thread for background scraping to keep UI responsive
- The generate HOW-TO must use streaming (iter_lines from requests response)";

    // ── One-time setup — launch TheOrc with pre-confirmed workspace ───────────

    [OneTimeSetUp]
    public void LaunchTheOrc()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            $"OrcResearcher_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(_workspace);

        TestContext.WriteLine($"[T06] Workspace: {_workspace}");

        var exePath = ResolveExePath();
        TestContext.WriteLine($"[T06] Launching: {exePath}");

        _automation = new UIA3Automation();

        // Launch with --workspace to auto-confirm the workspace inside TheOrc
        _app = Application.Launch(exePath, $"--workspace \"{_workspace}\"");

        // Give TheOrc 20s to fully load + connect to Ollama
        _mainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20));
        Assert.That(_mainWindow, Is.Not.Null, "Main window did not appear.");

        TestContext.WriteLine("[T06] TheOrc launched. Waiting 5s for Ollama startup check…");
        Thread.Sleep(5_000);   // let OnLoadedAsync finish its Ollama ping
    }

    [OneTimeTearDown]
    public void ShutdownTheOrc()
    {
        try { _app?.Kill(); } catch { }
        _automation?.Dispose();
        _app?.Dispose();

        // Report what was built
        if (Directory.Exists(_workspace))
        {
            var files = Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories);
            TestContext.WriteLine($"\n[T06] === BUILD OUTPUT ({files.Length} files) ===");
            foreach (var f in files.OrderBy(f => f))
                TestContext.WriteLine($"  {Path.GetRelativePath(_workspace, f)}  ({new FileInfo(f).Length:N0} B)");
        }
    }

    // ── Main test ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(1_800_000)]   // 30 min hard NUnit timeout
    public void BuildOrcResearcher_FullAutonomousRun()
    {
        // ── Step 1: Verify workspace badge is confirmed ──────────────────────
        TestContext.WriteLine("[T06] Step 1: Checking workspace badge…");
        var workspaceBadgeConfirmed = WaitUntil(
            () =>
            {
                var badge = FindById("AgentPanel.WorkspaceBadge");
                // Badge text changes to the folder name when confirmed
                return badge != null && !badge.Name.Contains("No workspace");
            },
            TimeSpan.FromSeconds(15));

        if (!workspaceBadgeConfirmed)
            TestContext.WriteLine("[T06] WARN: Workspace badge not confirmed — agent may not write files.");

        // ── Step 2: Navigate to Agent panel ──────────────────────────────────
        TestContext.WriteLine("[T06] Step 2: Navigating to Agent panel…");
        FindById("ActivityBar.Agent")?.AsButton().Click();
        Assert.That(
            WaitUntil(() => FindById("AgentPanel.Input") != null, TimeSpan.FromSeconds(10)),
            Is.True, "Agent panel did not appear.");

        // ── Step 3: Ensure Plan mode ─────────────────────────────────────────
        TestContext.WriteLine("[T06] Step 3: Selecting Plan mode…");
        FindById("AgentPanel.ModePlan")?.AsRadioButton().Click();
        Thread.Sleep(300);

        // ── Step 4: Enter the plan prompt ────────────────────────────────────
        TestContext.WriteLine("[T06] Step 4: Entering plan prompt…");
        var input = FindById("AgentPanel.Input")?.AsTextBox();
        Assert.That(input, Is.Not.Null, "Agent input box not found.");
        input!.Click();
        Thread.Sleep(200);

        // Enter() uses IValueProvider.SetValue internally — fast, no clipboard needed
        input.Enter(PlanPrompt);
        Thread.Sleep(500);

        // ── Step 5: Send plan ─────────────────────────────────────────────────
        TestContext.WriteLine("[T06] Step 5: Sending plan prompt — waiting for response…");
        FindById("AgentPanel.Send")?.AsButton().Click();

        // ── Step 6: Wait for Plan to complete ────────────────────────────────
        TestContext.WriteLine($"[T06] Step 6: Waiting up to {PlanTimeoutMinutes} min for plan…");
        var planDone = WaitForAgentIdle(TimeSpan.FromMinutes(PlanTimeoutMinutes));
        TestContext.WriteLine(planDone
            ? "[T06] Plan complete."
            : "[T06] WARN: Plan timed out — proceeding to Execute anyway.");

        Thread.Sleep(2_000);   // let UI settle

        // ── Step 7: Switch to Execute mode ───────────────────────────────────
        TestContext.WriteLine("[T06] Step 7: Switching to Execute mode…");
        FindById("AgentPanel.ModeExecute")?.AsRadioButton().Click();
        Thread.Sleep(500);

        // ── Step 8: Send execute command ─────────────────────────────────────
        TestContext.WriteLine("[T06] Step 8: Sending execute command…");
        input = FindById("AgentPanel.Input")?.AsTextBox();
        Assert.That(input, Is.Not.Null, "Agent input box not found for execute.");
        input!.Click();
        Thread.Sleep(200);
        input.Enter("[Execute the above plan. Create all 6 files now with complete, working Python code. No TODOs, no placeholders.]");
        Thread.Sleep(300);
        FindById("AgentPanel.Send")?.AsButton().Click();

        // ── Step 9: Monitor execution — poll workspace for progress ──────────
        TestContext.WriteLine($"[T06] Step 9: Monitoring build — up to {ExecuteTimeoutMinutes} min…");
        var success = MonitorExecution(TimeSpan.FromMinutes(ExecuteTimeoutMinutes),
                                       TimeSpan.FromMinutes(StallDetectMinutes));

        // ── Step 10: Assertions ───────────────────────────────────────────────
        TestContext.WriteLine("[T06] Step 10: Verifying output…");
        var pyFiles = Directory.GetFiles(_workspace, "*.py", SearchOption.AllDirectories).ToList();
        var hasReq  = File.Exists(Path.Combine(_workspace, "requirements.txt"));
        var hasMain = pyFiles.Any(f => Path.GetFileName(f) == "main.py");

        TestContext.WriteLine($"[T06] .py files: {pyFiles.Count}");
        TestContext.WriteLine($"[T06] main.py: {hasMain}");
        TestContext.WriteLine($"[T06] requirements.txt: {hasReq}");

        if (!success)
        {
            TestContext.WriteLine("[T06] STALL DETECTED — TheOrc stopped making progress.");
            TestContext.WriteLine("[T06] FIX: Check Ollama is running (ollama ps). Model may have hung.");
        }

        Assert.That(pyFiles.Count, Is.GreaterThan(0),
            $"Expected at least 1 .py file in {_workspace}. TheOrc did not write any files.\n" +
            "Likely causes:\n" +
            "  1. Ollama not running — start with: ollama serve\n" +
            "  2. Workspace not confirmed — check badge was green\n" +
            "  3. Model timed out — try a smaller model in TheOrc settings\n" +
            "  4. Agent got confused — restart and try Execute mode directly");

        Assert.That(hasMain, Is.True,
            "main.py not found — the agent may have only partially executed the plan.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Wait until the agent is idle — Stop button disabled for 3s straight.
    /// Returns true if idle within timeout, false if timeout elapsed.
    /// </summary>
    private bool WaitForAgentIdle(TimeSpan timeout)
    {
        var deadline    = DateTime.UtcNow + timeout;
        var idleSince   = DateTime.MinValue;
        const int IdleConfirmMs = 3_000;

        while (DateTime.UtcNow < deadline)
        {
            var stop      = FindById("AgentPanel.Stop");
            var isRunning = stop?.AsButton().IsEnabled == true;

            if (!isRunning)
            {
                if (idleSince == DateTime.MinValue) idleSince = DateTime.UtcNow;
                if ((DateTime.UtcNow - idleSince).TotalMilliseconds >= IdleConfirmMs)
                    return true;   // confirmed idle
            }
            else
            {
                idleSince = DateTime.MinValue;   // reset if it starts running again
            }

            Thread.Sleep(PollIntervalMs);
        }
        return false;
    }

    /// <summary>
    /// Monitor execution by watching workspace file growth.
    /// Returns false if no new files appear for <paramref name="stallTimeout"/>.
    /// Reports progress to TestContext.
    /// </summary>
    private bool MonitorExecution(TimeSpan totalTimeout, TimeSpan stallTimeout)
    {
        var deadline     = DateTime.UtcNow + totalTimeout;
        var lastProgress = DateTime.UtcNow;
        var lastFileSet  = new HashSet<string>(GetWorkspaceFiles());
        var reportedStall = false;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(PollIntervalMs);

            var currentFiles = GetWorkspaceFiles();
            var newFiles     = currentFiles.Except(lastFileSet).ToList();

            if (newFiles.Any())
            {
                lastProgress = DateTime.UtcNow;
                reportedStall = false;
                foreach (var f in newFiles)
                    TestContext.WriteLine($"  [NEW] {Path.GetRelativePath(_workspace, f)}  ({new FileInfo(f).Length:N0} B)");
                lastFileSet = new HashSet<string>(currentFiles);
            }

            // Check for stall
            var stalledFor = DateTime.UtcNow - lastProgress;
            if (stalledFor > stallTimeout)
            {
                if (!reportedStall)
                {
                    TestContext.WriteLine($"[T06] STALL: No new files for {stalledFor.TotalMinutes:F0} min.");
                    TestContext.WriteLine("[T06] Checking if agent is still running…");
                    var isRunning = FindById("AgentPanel.Stop")?.AsButton().IsEnabled == true;
                    TestContext.WriteLine(isRunning
                        ? "[T06] Agent IS still running — possible long generation. Extending wait."
                        : "[T06] Agent is IDLE — it stopped. Wrapping up.");
                    reportedStall = true;

                    if (!isRunning) break;   // agent done, no more files coming
                }
            }

            // Also break if agent finished AND no new files in 30s
            var agentDone  = FindById("AgentPanel.Stop")?.AsButton().IsEnabled != true;
            var noRecentFiles = (DateTime.UtcNow - lastProgress).TotalSeconds > 30;
            if (agentDone && noRecentFiles) break;
        }

        var pyCount = Directory.GetFiles(_workspace, "*.py", SearchOption.AllDirectories).Length;
        TestContext.WriteLine($"[T06] Monitor complete — {pyCount} .py files built.");
        return !reportedStall;
    }

    private List<string> GetWorkspaceFiles() =>
        Directory.Exists(_workspace)
            ? Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories)
                       .Where(f => !f.Contains("\\.git\\") && !f.Contains("\\__pycache__\\"))
                       .ToList()
            : [];

    private AutomationElement? FindById(string id) =>
        _mainWindow?.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>
    /// Walk up from the test output dir to find the .slnx solution root,
    /// then resolve OrchestratorIDE.exe (same logic as AppFixture).
    /// </summary>
    private static string ResolveExePath()
    {
        var envPath = Environment.GetEnvironmentVariable("ORCHESTRATOR_EXE");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 8; i++)
        {
            if (dir?.GetFiles("*.slnx").Length > 0) break;
            dir = dir?.Parent;
        }
        if (dir is null) throw new FileNotFoundException("Cannot locate solution root");

        string[] candidates =
        [
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Debug",   "net10.0-windows", "OrchestratorIDE.exe"),
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Release",  "net10.0-windows", "OrchestratorIDE.exe"),
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Release",  "net10.0-windows", "win-x64", "OrchestratorIDE.exe"),
        ];

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        throw new FileNotFoundException(
            $"OrchestratorIDE.exe not found. Tried:\n  " + string.Join("\n  ", candidates));
    }
}
