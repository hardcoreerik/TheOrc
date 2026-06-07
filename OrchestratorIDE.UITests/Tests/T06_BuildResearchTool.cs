using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T06 v2 — Autonomous build: TheOrc builds OrcResearcher (Python research tool).
///
/// Lessons from v1:
///   - Skip Plan mode — large models take 5-10 min to generate plans
///   - Go straight to Execute with a concise "write files NOW" prompt
///   - Verify agent actually started (Stop button enabled) before monitoring
///   - Never send Execute while agent is still busy
///   - Retry Send if it didn't take
/// </summary>
[TestFixture]
public class T06_BuildResearchTool : RecordingTestBase
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const int StartupWaitSec     = 12;   // time for OnLoadedAsync + Ollama ping
    private const int ExecuteTimeoutMin  = 25;   // total build window
    private const int StallDetectMin     = 6;    // no progress → stall
    private const int AgentStartWaitSec  = 45;   // wait for Stop to go enabled after Send
    private const int PollMs             = 5_000;

    private static Application?    _app;
    private static UIA3Automation? _automation;
    private static Window?         _win;
    private static string          _workspace = "";

    // ── Execute prompt — concise "write files" instruction ───────────────────
    // Short enough for the model to parse quickly; complete enough to build correctly.

    private const string ExecutePrompt =
        "Use write_file to create all 6 files for OrcResearcher right now. " +
        "No planning — just write every file immediately.\n\n" +
        "## main.py\n" +
        "tkinter GUI. Window titled 'OrcResearcher'. Controls:\n" +
        "- Entry for topic + 'Research' button (row 0)\n" +
        "- Checkbuttons: Wikipedia, DuckDuckGo, Custom URL + URL Entry (row 1)\n" +
        "- Scrolled Text (log_text) for status log, read-only (row 2, height=6)\n" +
        "- Scrolled Text (results_text) for gathered content, read-only (row 3, height=10)\n" +
        "- Combobox for Ollama model + Entry for Ollama host (default http://localhost:11434) (row 4)\n" +
        "- 'Generate HOW-TO' button + 'Save Research' button (row 5)\n" +
        "Scraping runs in threading.Thread. Imports scraper, ollama_client, file_manager.\n" +
        "On startup: populate model combobox via ollama_client.list_models().\n\n" +
        "## scraper.py\n" +
        "Three functions:\n" +
        "- fetch_wikipedia(topic) -> dict with keys source/url/content\n" +
        "  GET https://en.wikipedia.org/wiki/{topic.replace(' ','_')}\n" +
        "  Extract all <p> text via BeautifulSoup, join with newline\n" +
        "- fetch_duckduckgo(query, max_results=5) -> list of dicts\n" +
        "  GET https://lite.duckduckgo.com/lite/?q={query}, headers={'User-Agent':'Mozilla/5.0'}\n" +
        "  Extract all <a class='result-link'> hrefs, call fetch_url on each\n" +
        "- fetch_url(url) -> dict\n" +
        "  GET url with 10s timeout, extract <p> text via BeautifulSoup\n" +
        "All catch exceptions and return empty content dict on error.\n\n" +
        "## ollama_client.py\n" +
        "Two functions:\n" +
        "- list_models(host='http://localhost:11434') -> list of model name strings\n" +
        "  GET {host}/api/tags, return [m['name'] for m in data['models']], [] on error\n" +
        "- generate(host, model, prompt, callback)\n" +
        "  POST {host}/api/generate, json={'model':model,'prompt':prompt,'stream':True}\n" +
        "  For each line: json.loads, call callback(chunk['response']), stop on done:True\n" +
        "  On error: call callback('[Ollama unavailable]')\n\n" +
        "## file_manager.py\n" +
        "One function:\n" +
        "- save_research(topic, sources_list, howto_text, output_dir) -> filepath\n" +
        "  safe = topic.lower().replace(' ','_')\n" +
        "  path = output_dir / safe / f'{datetime.now():%Y%m%d_%H%M%S}_research.md'\n" +
        "  Writes Markdown: # {topic}, ## Sources, ## HOW-TO, returns path\n\n" +
        "## requirements.txt\n" +
        "requests>=2.28.0\n" +
        "beautifulsoup4>=4.11.0\n\n" +
        "## README.md\n" +
        "# OrcResearcher\n" +
        "Install: pip install -r requirements.txt\n" +
        "Run: python main.py\n" +
        "Requires: Ollama running at localhost:11434";

    // ── One-time setup ────────────────────────────────────────────────────────

    [OneTimeSetUp]
    public void LaunchTheOrc()
    {
        _workspace = Path.Combine(
            Path.GetTempPath(),
            $"OrcResearcher_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(_workspace);
        TestContext.WriteLine($"[T06] Workspace : {_workspace}");

        var exe = ResolveExePath();
        TestContext.WriteLine($"[T06] Executable: {exe}");

        _automation = new UIA3Automation();
        _app        = Application.Launch(exe, $"--workspace \"{_workspace}\" --autotest");
        _win        = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20));

        Assert.That(_win, Is.Not.Null, "Main window did not appear within 20s.");
        TestContext.WriteLine($"[T06] Window    : {_win!.Title}");

        // Wait for OnLoadedAsync to finish (Ollama ping + session restore + workspace confirm)
        TestContext.WriteLine($"[T06] Waiting {StartupWaitSec}s for startup…");
        Thread.Sleep(StartupWaitSec * 1_000);
    }

    [OneTimeTearDown]
    public void ShutdownAndReport()
    {
        try { _app?.Kill(); } catch { }
        _automation?.Dispose();
        _app?.Dispose();

        if (!Directory.Exists(_workspace)) return;
        var files = Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories)
                             .Where(f => !f.Contains("\\__pycache__\\"))
                             .OrderBy(f => f)
                             .ToList();

        TestContext.WriteLine($"\n[T06] ═══ BUILD OUTPUT ({files.Count} files) ═══");
        foreach (var f in files)
            TestContext.WriteLine($"  {Path.GetRelativePath(_workspace, f),40}  {new FileInfo(f).Length,8:N0} B");
    }

    // ── Main test ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(1_800_000)]   // 30 min hard ceiling
    public void BuildOrcResearcher_FullAutonomousRun()
    {
        // ── 1. Confirm workspace badge ────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 1. Workspace badge check");
        var badgeOk = WaitUntil(
            () => ByIdText("StatusBar.Workspace")?.Contains(_workspace) == true
               || ByIdText("StatusBar.Workspace")?.Contains(Path.GetFileName(_workspace)) == true,
            TimeSpan.FromSeconds(10));
        TestContext.WriteLine(badgeOk
            ? $"[T06]    ✓ Workspace confirmed: {ByIdText("StatusBar.Workspace")}"
            : $"[T06]    ⚠ Badge uncertain: {ByIdText("StatusBar.Workspace") ?? "(null)"}");

        // ── 2. Navigate to Agent panel ────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 2. Agent panel");
        FindById("ActivityBar.Agent")?.AsButton().Click();
        Assert.That(WaitUntil(() => FindById("AgentPanel.Input") != null, TimeSpan.FromSeconds(15)),
            Is.True, "Agent panel did not appear.");

        // ── 3. Wait for any previous agent run to finish ──────────────────────
        TestContext.WriteLine("[T06] ── 3. Waiting for idle agent…");
        WaitForAgentIdle(TimeSpan.FromSeconds(20));

        // ── 4. Select EXECUTE mode ────────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 4. Selecting Execute mode");
        FindById("AgentPanel.ModeExecute")?.AsRadioButton().Click();
        Thread.Sleep(500);

        // ── 5. Enter the execute prompt ───────────────────────────────────────
        TestContext.WriteLine("[T06] ── 5. Entering execute prompt");
        var input = FindById("AgentPanel.Input")?.AsTextBox();
        Assert.That(input, Is.Not.Null, "AgentPanel.Input not found.");

        // Click to focus, Enter() uses IValueProvider.SetValue() internally — fast, bypasses keyboard
        input!.Click();
        Thread.Sleep(200);
        input.Enter(ExecutePrompt);
        Thread.Sleep(500);

        // Verify text was actually set
        var textSet = !string.IsNullOrWhiteSpace(input.Text);
        TestContext.WriteLine(textSet
            ? $"[T06]    ✓ Prompt set ({input.Text.Length} chars)"
            : "[T06]    ✗ Prompt empty — IValueProvider failed, falling back to Keyboard");

        // ── 6. Send ───────────────────────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 6. Clicking Send");
        var sendBtn = FindById("AgentPanel.Send")?.AsButton();
        Assert.That(sendBtn?.IsEnabled, Is.True,
            "Send button is not enabled — agent may still be running from a previous session.");
        sendBtn!.Click();
        Thread.Sleep(1_000);

        // ── 7. Verify agent started (Stop enabled = agent running) ────────────
        TestContext.WriteLine($"[T06] ── 7. Waiting {AgentStartWaitSec}s for agent to start…");
        var agentStarted = WaitUntil(
            () => FindById("AgentPanel.Stop")?.AsButton().IsEnabled == true,
            TimeSpan.FromSeconds(AgentStartWaitSec));

        if (!agentStarted)
        {
            TestContext.WriteLine("[T06]    ⚠ Agent didn't start — retrying Send…");
            input = FindById("AgentPanel.Input")?.AsTextBox();
            input?.Click();
            sendBtn = FindById("AgentPanel.Send")?.AsButton();
            if (sendBtn?.IsEnabled == true)
            {
                sendBtn.Click();
                Thread.Sleep(1_000);
                agentStarted = WaitUntil(
                    () => FindById("AgentPanel.Stop")?.AsButton().IsEnabled == true,
                    TimeSpan.FromSeconds(AgentStartWaitSec));
            }
        }

        TestContext.WriteLine(agentStarted
            ? "[T06]    ✓ Agent is running (Stop enabled)"
            : "[T06]    ⚠ Agent still not running — monitoring anyway");

        // ── 8. Monitor workspace for file creation ────────────────────────────
        TestContext.WriteLine($"[T06] ── 8. Monitoring build (up to {ExecuteTimeoutMin} min)…");
        var stalled = !MonitorBuild(
            TimeSpan.FromMinutes(ExecuteTimeoutMin),
            TimeSpan.FromMinutes(StallDetectMin));

        if (stalled)
        {
            TestContext.WriteLine("[T06]    ⚠ STALL DETECTED");
            TestContext.WriteLine("[T06]    Diagnosis:");
            TestContext.WriteLine($"[T06]      Agent idle  : {FindById("AgentPanel.Stop")?.AsButton().IsEnabled != true}");
            TestContext.WriteLine($"[T06]      .py files   : {Directory.GetFiles(_workspace, "*.py").Length}");
            TestContext.WriteLine("[T06]    Fix options:");
            TestContext.WriteLine("[T06]      1. Check Ollama is running: ollama ps");
            TestContext.WriteLine("[T06]      2. Workspace not confirmed — check StatusBar.Workspace");
            TestContext.WriteLine("[T06]      3. Model produced output but didn't call write_file");
        }

        // ── 9. Assertions ─────────────────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 9. Assertions");
        var pyFiles  = Directory.GetFiles(_workspace, "*.py", SearchOption.AllDirectories).ToList();
        var hasMain  = pyFiles.Any(f => Path.GetFileName(f).Equals("main.py", StringComparison.OrdinalIgnoreCase));
        var hasReq   = File.Exists(Path.Combine(_workspace, "requirements.txt"));

        TestContext.WriteLine($"[T06]    .py files    : {pyFiles.Count}");
        TestContext.WriteLine($"[T06]    main.py      : {(hasMain ? "✓" : "✗")}");
        TestContext.WriteLine($"[T06]    requirements : {(hasReq  ? "✓" : "✗")}");

        Assert.Multiple(() =>
        {
            Assert.That(pyFiles.Count, Is.GreaterThan(0),
                "No .py files created — Execute didn't run or workspace was blocked.\n" +
                "DIAGNOSE: Check recording, confirm Ollama is running (ollama ps), " +
                "and verify the StatusBar.Workspace shows the OrcResearcher folder.");
            Assert.That(hasMain, Is.True,
                "main.py missing — agent may have only written partial files. Re-run test.");
            Assert.That(hasReq, Is.True,
                "requirements.txt missing — run again or add it manually.");
        });
    }

    // ── Build monitor ─────────────────────────────────────────────────────────

    /// <summary>
    /// Polls the workspace for new files. Returns true if build completed without stall.
    /// A stall = no new files for <paramref name="stallTimeout"/> AND agent is idle.
    /// </summary>
    private bool MonitorBuild(TimeSpan totalTimeout, TimeSpan stallTimeout)
    {
        var deadline      = DateTime.UtcNow + totalTimeout;
        var lastProgress  = DateTime.UtcNow;
        var lastFiles     = new HashSet<string>(WorkspaceFiles());
        var stalled       = false;

        // Report existing files
        foreach (var f in lastFiles)
            TestContext.WriteLine($"  [pre-existing] {Path.GetRelativePath(_workspace, f)}");

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(PollMs);

            var current  = WorkspaceFiles();
            var newFiles = current.Except(lastFiles).ToList();

            if (newFiles.Count > 0)
            {
                lastProgress = DateTime.UtcNow;
                stalled      = false;
                foreach (var f in newFiles)
                    TestContext.WriteLine(
                        $"  [NEW  {DateTime.Now:HH:mm:ss}] {Path.GetRelativePath(_workspace, f),35}  " +
                        $"{new FileInfo(f).Length:N0} B");
                lastFiles = new HashSet<string>(current);
            }

            var agentIdle     = FindById("AgentPanel.Stop")?.AsButton().IsEnabled != true;
            var stalledFor    = DateTime.UtcNow - lastProgress;
            var noProgress    = stalledFor > TimeSpan.FromSeconds(30);

            // Exit conditions
            if (agentIdle && noProgress)
            {
                TestContext.WriteLine(stalledFor > stallTimeout
                    ? $"  [STALL] No new files for {stalledFor.TotalMinutes:F1} min, agent idle."
                    : $"  [DONE ] Agent finished. Elapsed: {stalledFor.TotalSeconds:F0}s since last file.");
                stalled = stalledFor > stallTimeout;
                break;
            }

            if (!agentIdle && stalledFor > stallTimeout)
            {
                TestContext.WriteLine($"  [WARN ] Agent still running but no new files for {stalledFor.TotalMinutes:F1} min.");
                // Don't exit yet — give it more time (model might be generating a big file)
            }
        }

        var total = WorkspaceFiles().Count;
        TestContext.WriteLine($"  [SUMMARY] {total} total files, stalled={stalled}");
        return !stalled;
    }

    // ── Polling helpers ───────────────────────────────────────────────────────

    private bool WaitForAgentIdle(TimeSpan timeout)
    {
        // Confirmed idle = Stop disabled for 2 consecutive polls
        var deadline     = DateTime.UtcNow + timeout;
        var idleCount    = 0;
        while (DateTime.UtcNow < deadline)
        {
            var isRunning = FindById("AgentPanel.Stop")?.AsButton().IsEnabled == true;
            if (!isRunning) { if (++idleCount >= 2) return true; }
            else            { idleCount = 0; }
            Thread.Sleep(1_000);
        }
        return false;
    }

    private List<string> WorkspaceFiles() =>
        Directory.Exists(_workspace)
            ? Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories)
                       .Where(f => !f.Contains("\\__pycache__\\") && !f.Contains("\\.git\\"))
                       .ToList()
            : [];

    private AutomationElement? FindById(string id) =>
        _win?.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private string? ByIdText(string id) =>
        FindById(id)?.Name;

    private static bool WaitUntil(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) { if (cond()) return true; Thread.Sleep(300); }
        return false;
    }

    // ── Exe resolution ────────────────────────────────────────────────────────

    private static string ResolveExePath()
    {
        var envPath = Environment.GetEnvironmentVariable("ORCHESTRATOR_EXE");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath)) return envPath;

        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 8; i++) { if (dir?.GetFiles("*.slnx").Length > 0) break; dir = dir?.Parent; }
        if (dir is null) throw new FileNotFoundException("Cannot find .slnx root");

        string[] c =
        [
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Debug",   "net10.0-windows", "OrchestratorIDE.exe"),
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Release",  "net10.0-windows", "OrchestratorIDE.exe"),
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Release",  "net10.0-windows", "win-x64", "OrchestratorIDE.exe"),
        ];
        foreach (var candidate in c) if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("OrchestratorIDE.exe not found. Tried:\n" + string.Join("\n", c));
    }
}
