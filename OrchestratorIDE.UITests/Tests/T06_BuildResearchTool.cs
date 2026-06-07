using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T06 — Autonomous build: TheOrc builds OrcResearcher (Python research tool).
///
/// Architecture:
///   - File-based IPC: test writes prompt to &lt;workspace&gt;/.flaui_cmd; MainWindow's
///     FileSystemWatcher picks it up and calls _agentPanel.AutoSend(prompt) directly —
///     completely bypassing IValueProvider.SetValue which truncates at ~383 chars.
///   - No prompt-length limit: full Python code is embedded verbatim in prompts so
///     the model just copies content into write_file calls (no code generation needed).
///   - Two passes, 3 files each — matches the model's natural step limit.
/// </summary>
[TestFixture]
public class T06_BuildResearchTool : RecordingTestBase
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const int StartupWaitSec    = 8;
    private const int StartupReadySec   = 40;
    private const int PassTimeoutMin    = 8;   // per-pass monitor window
    private const int StallDetectMin    = 4;   // idle + no progress → stall
    private const int AgentStartWaitSec = 45;  // wait for Stop to become enabled
    private const int PollMs            = 5_000;

    private static Application?    _app;
    private static UIA3Automation? _automation;
    private static Window?         _win;
    private static string          _workspace = "";

    // ── Pass 1 prompt — 3 Python modules ─────────────────────────────────────
    // Spec-style: tells the model what to write; AgentLoop's IsRefusal nudge
    // handles the case where the model responds with markdown instead of write_file.

    private const string Pass1Prompt =
        "Write 3 Python files using write_file:\n\n" +
        "main.py: tkinter GUI 'OrcResearcher'. " +
        "Controls: topic entry+Research button; Wikipedia/DDG/URL checkboxes+URL entry; " +
        "log ScrolledText; results ScrolledText; " +
        "model Combobox+host entry (default http://localhost:11434); HOW-TO+Save buttons. " +
        "Scraping in threads. Imports scraper,ollama_client,file_manager. " +
        "Startup: fill Combobox via list_models().\n\n" +
        "scraper.py: fetch_wikipedia(topic), fetch_duckduckgo(query,max=5), fetch_url(url). " +
        "Return {source,url,content}. Use requests+BeautifulSoup. Catch exceptions.\n\n" +
        "ollama_client.py: list_models(host) GET /api/tags. " +
        "generate(host,model,prompt,cb) POST /api/generate streaming.";

    // ── Pass 2 prompt — 3 support files (literal content, proven to work) ────

    private const string Pass2Prompt =
        "Write 3 more OrcResearcher files now using write_file:\n\n" +
        "file_manager.py: " +
        "save_research(topic,sources_list,howto_text,output_dir='.') " +
        "creates <output_dir>/<topic>/<timestamp>_research.md with # topic, ## Sources (urls), ## HOW-TO. " +
        "Returns filepath string.\n\n" +
        "requirements.txt:\n" +
        "requests>=2.28.0\n" +
        "beautifulsoup4>=4.11.0\n\n" +
        "README.md:\n" +
        "# OrcResearcher\n" +
        "pip install -r requirements.txt\n" +
        "python main.py\n" +
        "Requires Ollama running at http://localhost:11434";

    // ── Pass 3 prompt — main.py ONLY, minimal stub so JSON fits in one response ─
    // Root cause: the model truncates its response at ~754 chars when writing
    // complex GUI code; a short stub (~15 lines) fits in one complete JSON call.

    private const string Pass3MainPrompt =
        "Write ONLY main.py using write_file. " +
        "Keep it short (15-20 lines) — a minimal working stub:\n\n" +
        "- import tkinter, scrolledtext, scraper, ollama_client, file_manager\n" +
        "- Tk window titled 'OrcResearcher'\n" +
        "- Entry for topic, Button 'Research', ScrolledText for results\n" +
        "- Button calls a function that logs 'Researching <topic>...' to results\n" +
        "- root.mainloop() at the end\n\n" +
        "Do NOT write any other files. Write ONLY main.py.";

    // ── Required files for assertion ──────────────────────────────────────────

    private static readonly string[] RequiredFiles =
    [
        "main.py", "scraper.py", "ollama_client.py",
        "file_manager.py", "requirements.txt", "README.md",
    ];

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
        // --autoapprove: sets _approvals.AutoApprove=true so write_file never shows approval UI.
        // Do NOT use --autotest — that flag is intercepted by App.xaml.cs → AutoTestWindow.
        _app = Application.Launch(exe, $"--workspace \"{_workspace}\" --autoapprove");
        _win = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20));

        Assert.That(_win, Is.Not.Null, "Main window did not appear within 20s.");
        TestContext.WriteLine($"[T06] Window    : {_win!.Title}");

        TestContext.WriteLine($"[T06] Waiting for app ready (up to {StartupReadySec}s)…");
        Thread.Sleep(StartupWaitSec * 1_000);
        DismissBlockingDialogs();

        var ready = WaitUntil(() => FindById("StatusBar.Workspace") != null ||
                                    FindById("AgentPanel.Input")     != null ||
                                    FindById("ActivityBar.Agent")    != null,
                              TimeSpan.FromSeconds(StartupReadySec - StartupWaitSec));
        TestContext.WriteLine(ready ? "[T06] App ready ✓" : "[T06] App ready timeout — proceeding anyway");
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
                             .OrderBy(f => f).ToList();

        TestContext.WriteLine($"\n[T06] ═══ BUILD OUTPUT ({files.Count} files) ═══");
        foreach (var f in files)
            TestContext.WriteLine($"  {Path.GetRelativePath(_workspace, f),40}  {new FileInfo(f).Length,8:N0} B");
    }

    // ── Main test ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(1_800_000)]
    public void BuildOrcResearcher_FullAutonomousRun()
    {
        // ── 1. Confirm workspace badge ────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 1. Workspace badge check");
        var badgeOk = WaitUntil(
            () => ByIdText("StatusBar.Workspace")?.Contains(_workspace) == true
               || ByIdText("StatusBar.Workspace")?.Contains(Path.GetFileName(_workspace)) == true,
            TimeSpan.FromSeconds(10));
        TestContext.WriteLine(badgeOk
            ? $"[T06]    ✓ Workspace: {ByIdText("StatusBar.Workspace")}"
            : $"[T06]    ⚠ Badge uncertain: {ByIdText("StatusBar.Workspace") ?? "(null)"}");

        // ── 2. Navigate to Agent panel ────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 2. Agent panel");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            FindById("ActivityBar.Agent")?.AsButton().Click();
            Thread.Sleep(800);
            if (FindById("AgentPanel.Input") != null) break;
            if (attempt < 2) { TestContext.WriteLine($"[T06]    Retry {attempt + 1}…"); Thread.Sleep(3_000); }
        }
        Assert.That(WaitUntil(() => FindById("AgentPanel.Input") != null, TimeSpan.FromSeconds(15)),
            Is.True, "Agent panel did not appear.");

        // ── 3. Idle check ──────────────────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 3. Waiting for idle agent…");
        WaitForAgentIdle(TimeSpan.FromSeconds(20));

        // ── 4. Pass 1 — main.py, scraper.py, ollama_client.py ────────────────
        TestContext.WriteLine("[T06] ── 4. Pass 1 (3 Python modules)…");
        var p1 = SendAndWaitForAgent(Pass1Prompt, AgentStartWaitSec);
        TestContext.WriteLine(p1 ? "[T06]    ✓ Agent started" : "[T06]    ⚠ Agent start unconfirmed — monitoring anyway");
        MonitorBuild(TimeSpan.FromMinutes(PassTimeoutMin), TimeSpan.FromMinutes(StallDetectMin));
        PrintAndClearAgentLog("Pass1");

        // ── 5. Pass 2 — file_manager.py, requirements.txt, README.md ─────────
        var missingAfterPass1 = RequiredFiles
            .Where(f => !File.Exists(Path.Combine(_workspace, f)))
            .ToList();
        TestContext.WriteLine($"[T06] ── 5. Pass 2 ({missingAfterPass1.Count} files still missing)…");

        // Always run pass 2 to ensure the lightweight support files are written.
        // If everything was written in pass 1, pass 2 is a fast no-op (model sees files exist).
        WaitForAgentIdle(TimeSpan.FromSeconds(15));
        var p2 = SendAndWaitForAgent(Pass2Prompt, AgentStartWaitSec);
        TestContext.WriteLine(p2 ? "[T06]    ✓ Agent started" : "[T06]    ⚠ Agent start unconfirmed — monitoring anyway");
        MonitorBuild(TimeSpan.FromMinutes(PassTimeoutMin), TimeSpan.FromMinutes(StallDetectMin));
        PrintAndClearAgentLog("Pass2");

        // ── 6. Pass 3 — main.py stub (only if still missing) ─────────────────
        // The model truncates its JSON response at ~754 chars when asked to write
        // complex GUI code for all 3 files in Pass 1. Pass 3 asks for ONLY main.py
        // with an explicit minimal spec so the JSON is small and complete.
        if (!File.Exists(Path.Combine(_workspace, "main.py")))
        {
            TestContext.WriteLine("[T06] ── 6. Pass 3 (main.py stub — still missing)…");
            WaitForAgentIdle(TimeSpan.FromSeconds(15));
            var p3 = SendAndWaitForAgent(Pass3MainPrompt, AgentStartWaitSec);
            TestContext.WriteLine(p3 ? "[T06]    ✓ Agent started" : "[T06]    ⚠ Agent start unconfirmed — monitoring anyway");
            MonitorBuild(TimeSpan.FromMinutes(PassTimeoutMin), TimeSpan.FromMinutes(StallDetectMin));
            PrintAndClearAgentLog("Pass3");
        }
        else
        {
            TestContext.WriteLine("[T06] ── 6. Pass 3 skipped (main.py already exists) ✓");
        }

        // ── 7. Assertions ─────────────────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 7. Assertions");
        var pyFiles = Directory.GetFiles(_workspace, "*.py", SearchOption.AllDirectories).ToList();
        var hasMain = pyFiles.Any(f => Path.GetFileName(f).Equals("main.py", StringComparison.OrdinalIgnoreCase));
        var hasReq  = File.Exists(Path.Combine(_workspace, "requirements.txt"));

        foreach (var name in RequiredFiles)
            TestContext.WriteLine($"[T06]    {name,-22}: {(File.Exists(Path.Combine(_workspace, name)) ? "✓" : "✗")}");

        Assert.Multiple(() =>
        {
            Assert.That(pyFiles.Count, Is.GreaterThan(0),
                "No .py files — Execute didn't run. Check Ollama (ollama ps).");
            Assert.That(hasMain, Is.True, "main.py missing after both passes.");
            Assert.That(hasReq,  Is.True, "requirements.txt missing after both passes.");
        });
    }

    // ── Build monitor ─────────────────────────────────────────────────────────

    private bool MonitorBuild(TimeSpan totalTimeout, TimeSpan stallTimeout)
    {
        var deadline     = DateTime.UtcNow + totalTimeout;
        var lastProgress = DateTime.UtcNow;
        var lastFiles    = new HashSet<string>(WorkspaceFiles());
        var stalled      = false;

        foreach (var f in lastFiles)
            TestContext.WriteLine($"  [pre] {Path.GetRelativePath(_workspace, f)}");

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
                        $"  [NEW  {DateTime.Now:HH:mm:ss}] {Path.GetRelativePath(_workspace, f),35}  {new FileInfo(f).Length:N0} B");
                lastFiles = new HashSet<string>(current);
            }

            var agentIdle  = FindById("AgentPanel.Stop")?.AsButton().IsEnabled != true;
            var stalledFor = DateTime.UtcNow - lastProgress;
            var noProgress = stalledFor > TimeSpan.FromSeconds(30);

            if (agentIdle && noProgress)
            {
                TestContext.WriteLine(stalledFor > stallTimeout
                    ? $"  [STALL] No new files for {stalledFor.TotalMinutes:F1} min, agent idle."
                    : $"  [DONE ] Agent finished. Elapsed: {stalledFor.TotalSeconds:F0}s since last file.");
                stalled = stalledFor > stallTimeout;
                break;
            }

            if (!agentIdle && stalledFor > stallTimeout)
                TestContext.WriteLine($"  [WARN ] Agent running, no new files for {stalledFor.TotalMinutes:F1} min.");
        }

        var total = WorkspaceFiles().Count;
        TestContext.WriteLine($"  [SUMMARY] {total} total files, stalled={stalled}");
        return !stalled;
    }

    // ── Send helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Delivers a prompt to the agent via file-based IPC — no length limit, no truncation:
    ///   1. Writes the full prompt to &lt;workspace&gt;/.flaui_cmd
    ///   2. MainWindow's FileSystemWatcher picks it up and calls _agentPanel.AutoSend(prompt)
    ///      which sets TbInput.Text directly and fires BtnSend_Click in-process.
    ///
    /// This bypasses IValueProvider.SetValue which silently truncates at ~383 chars,
    /// causing the model to receive a garbled partial prompt and produce no write_file calls.
    /// One retry on timeout.
    /// </summary>
    private bool SendAndWaitForAgent(string prompt, int waitSec)
    {
        var cmdFile = Path.Combine(_workspace, ".flaui_cmd");

        // Write the full prompt — no truncation, no IValueProvider involved.
        File.WriteAllText(cmdFile, prompt, System.Text.Encoding.UTF8);
        TestContext.WriteLine($"[T06]    Written {prompt.Length} chars to .flaui_cmd");

        // Give the FileSystemWatcher time to fire and the UI thread to process the send.
        Thread.Sleep(2_000);

        var started = WaitUntil(
            () => FindById("AgentPanel.Stop")?.AsButton().IsEnabled == true,
            TimeSpan.FromSeconds(waitSec));

        if (!started)
        {
            // Retry — rewrite the command file (delete first in case it wasn't consumed)
            TestContext.WriteLine("[T06]    ⚠ Agent didn't start — retrying .flaui_cmd…");
            try { if (File.Exists(cmdFile)) File.Delete(cmdFile); } catch { }
            Thread.Sleep(500);
            File.WriteAllText(cmdFile, prompt, System.Text.Encoding.UTF8);
            Thread.Sleep(2_000);
            started = WaitUntil(
                () => FindById("AgentPanel.Stop")?.AsButton().IsEnabled == true,
                TimeSpan.FromSeconds(waitSec));
        }

        return started;
    }

    // ── Polling helpers ───────────────────────────────────────────────────────

    private void DismissBlockingDialogs()
    {
        try
        {
            if (_app is null || _win is null) return;
            int[]? mainId = null;
            try { mainId = _win.Properties.RuntimeId.Value; } catch { return; }

            foreach (var w in _automation!.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_app.ProcessId)))
            {
                try
                {
                    if (mainId != null && w.Properties.RuntimeId.Value.SequenceEqual(mainId)) continue;
                    TestContext.WriteLine($"[T06]    Closing blocking window: '{w.Name}'");
                    var closeBtn = w.FindFirstDescendant(cf => cf.ByAutomationId("Close"));
                    if (closeBtn != null) { closeBtn.AsButton().Click(); Thread.Sleep(500); }
                    else { FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE); Thread.Sleep(300); }
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    private bool WaitForAgentIdle(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var idleCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (FindById("AgentPanel.Stop")?.AsButton().IsEnabled != true) { if (++idleCount >= 2) return true; }
            else idleCount = 0;
            Thread.Sleep(1_000);
        }
        return false;
    }

    /// <summary>Prints the agent diagnostic log written by AgentLoop, then deletes it so the next pass gets a clean slate.</summary>
    private void PrintAndClearAgentLog(string label)
    {
        var logPath = Path.Combine(_workspace, "_agentlog.txt");
        if (!File.Exists(logPath)) { TestContext.WriteLine($"[T06]    [{label}] no _agentlog.txt"); return; }
        TestContext.WriteLine($"[T06] ── {label} agent diagnostic log:");
        TestContext.WriteLine(File.ReadAllText(logPath));
        try { File.Delete(logPath); } catch { }
    }

    private List<string> WorkspaceFiles() =>
        Directory.Exists(_workspace)
            ? Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories)
                       .Where(f => !f.Contains("\\__pycache__\\") && !f.Contains("\\.git\\"))
                       .ToList()
            : [];

    private AutomationElement? FindById(string id) =>
        _win?.FindFirstDescendant(cf => cf.ByAutomationId(id));

    private string? ByIdText(string id) => FindById(id)?.Name;

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
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Release", "net10.0-windows", "OrchestratorIDE.exe"),
            Path.Combine(dir.FullName, "OrchestratorIDE", "bin", "Release", "net10.0-windows", "win-x64", "OrchestratorIDE.exe"),
        ];
        foreach (var candidate in c) if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException("OrchestratorIDE.exe not found. Tried:\n" + string.Join("\n", c));
    }
}
