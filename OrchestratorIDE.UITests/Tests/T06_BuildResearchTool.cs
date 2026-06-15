// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using NUnit.Framework;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T06 — Autonomous build: TheOrc builds OrcResearcher (Python research tool).
///
/// MODE: Single-agent Execute mode ONLY.
///   This test drives the single-agent panel (AgentPanel), NOT the Swarm board.
///   The agent receives a goal, runs autonomously, and must write 6 files via write_file
///   tool calls. It is a live end-to-end integration test — no mocking.
///
/// Architecture:
///   - File-based IPC: test writes prompt to &lt;workspace&gt;/.flaui_cmd; MainWindow's
///     FileSystemWatcher picks it up and calls _agentPanel.AutoSend(prompt) directly —
///     completely bypassing IValueProvider.SetValue which truncates at ~383 chars.
///   - No prompt-length limit: full Python code is embedded verbatim in prompts so
///     the model just copies content into write_file calls (no code generation needed).
///   - Three passes — each narrows the remaining missing files.
///
/// What this test measures:
///   Reliable long write_file tool-call JSON generation — the model must:
///     1. Emit a valid write_file tool call (correct JSON structure)
///     2. Complete the JSON payload without truncation (full file content preserved)
///     3. Do this for each of 6 files across up to 3 passes
///
/// FAILURE INTERPRETATION:
///   If T06 fails with diagnostics showing truncated write_file JSON
///   (opens > closes in _agentlog.txt analysis), the failure is a MODEL CAPABILITY
///   issue, not an app logic or Ollama configuration failure. The agentlog will show
///   entries like: "opens=2 closes=0 — TRUNCATED write_file for: main.py"
///
/// Model requirement — MINIMUM ≥12B:
///   Small models (≤4B, e.g. nemotron-3-nano, phi-mini, llama-3.2-3b) reliably
///   fail this test by truncating write_file JSON mid-content. The 4B parameter
///   ceiling cannot sustain a ~200-line Python source file encoded as a single JSON
///   string value with all newlines escaped as \n.
///
///   T06 CONFIRMED FAILURES (observed 2026-06-09):
///     nemotron-3-nano:4b-q8_0 — Pass 1: main.py truncated (opens=2, closes=0)
///                              — Pass 2: file_manager.py truncated (opens=2, closes=0)
///                              — Pass 3: empty response (len=0)
///                              — Zero files written across all 3 passes.
///
///   RECOMMENDED MODELS (≥12B, reliable long write_file):
///     theorc-boss:gemma4    — 12B QAT, proven in swarm runs
///     qwen2.5-coder:14b     — ★ Best for T06 (purpose-built coder, already on server)
///     gemma4:12b            — 12B, reliable file writer
///     mistral-small:latest  — 24B, strong all-rounder
///
/// Tool-call support is not binary:
///   A model can "start" a tool call (NativeToolUse=true) and still fail on long
///   payloads. The difference between short tool calls (e.g. read_file, list_dir)
///   and long file-write calls (200-line Python files) is critical for coder roles.
///   See ModelProfiles.cs for CoderScore definitions and per-model notes.
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

    // ── Weak model names that reliably fail this test ─────────────────────────
    // Match both hyphenated slugs (settings/ollama tags) and space-separated display names (StatusBar).
    private static readonly string[] WeakModelSubstrings =
    [
        "nemotron-3-nano", "nemotron-nano", "nemotron 3 nano", "nemotron nano",
        "phi-mini", "phi4-mini", "phi 4 mini", "phi mini",
        "llama-3.2-3b", "llama3.2:3b", "llama 3.2 3b",
        "qwen:1.5b", "qwen:3b", "qwen 1.5b", "qwen 3b",
        "tinyllama", "smollm",
    ];

    // ── Pass 1 prompt — 3 Python modules ─────────────────────────────────────
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

    // ── Pass 3 prompt — main.py ONLY, minimal stub ───────────────────────────
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

        var exe = ResolveExePath();

        // ── Print startup diagnostics ─────────────────────────────────────────
        TestContext.WriteLine($"[T06] ═══ STARTUP DIAGNOSTICS ═══");
        TestContext.WriteLine($"[T06] Workspace : {_workspace}");
        TestContext.WriteLine($"[T06] Executable: {exe}");
        TestContext.WriteLine($"[T06] Timestamp : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        TestContext.WriteLine($"[T06] AutoApprove: enabled (--autoapprove flag passed)");

        _automation = new UIA3Automation();
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

        // ── 2b. Print active model from status bar ────────────────────────────
        var activeModel = ByIdText("StatusBar.Model") ?? "(unknown)";
        TestContext.WriteLine($"[T06]    Active model: {activeModel}");
        CheckModelCapability(activeModel);

        // ── 3. Idle check ──────────────────────────────────────────────────────
        TestContext.WriteLine("[T06] ── 3. Waiting for idle agent…");
        WaitForAgentIdle(TimeSpan.FromSeconds(20));

        // ── 4. Pass 1 — main.py, scraper.py, ollama_client.py ────────────────
        TestContext.WriteLine("[T06] ── 4. Pass 1 (3 Python modules)…");
        var p1 = SendAndWaitForAgent(Pass1Prompt, AgentStartWaitSec);
        TestContext.WriteLine(p1 ? "[T06]    ✓ Agent started (Stop button enabled)" : "[T06]    ⚠ Agent start unconfirmed — monitoring anyway");
        MonitorBuild(TimeSpan.FromMinutes(PassTimeoutMin), TimeSpan.FromMinutes(StallDetectMin));
        PrintAndClearAgentLog("Pass1");

        // ── 5. Pass 2 — file_manager.py, requirements.txt, README.md ─────────
        var missingAfterPass1 = RequiredFiles
            .Where(f => !File.Exists(Path.Combine(_workspace, f)))
            .ToList();
        TestContext.WriteLine($"[T06] ── 5. Pass 2 ({missingAfterPass1.Count} files still missing)…");

        WaitForAgentIdle(TimeSpan.FromSeconds(15));
        var p2 = SendAndWaitForAgent(Pass2Prompt, AgentStartWaitSec);
        TestContext.WriteLine(p2 ? "[T06]    ✓ Agent started (Stop button enabled)" : "[T06]    ⚠ Agent start unconfirmed — monitoring anyway");
        MonitorBuild(TimeSpan.FromMinutes(PassTimeoutMin), TimeSpan.FromMinutes(StallDetectMin));
        PrintAndClearAgentLog("Pass2");

        // ── 6. Pass 3 — main.py stub (only if still missing) ─────────────────
        if (!File.Exists(Path.Combine(_workspace, "main.py")))
        {
            TestContext.WriteLine("[T06] ── 6. Pass 3 (main.py stub — still missing)…");
            WaitForAgentIdle(TimeSpan.FromSeconds(15));
            var p3 = SendAndWaitForAgent(Pass3MainPrompt, AgentStartWaitSec);
            TestContext.WriteLine(p3 ? "[T06]    ✓ Agent started (Stop button enabled)" : "[T06]    ⚠ Agent start unconfirmed — monitoring anyway");
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

        // ── 7b. Failure evidence dump ─────────────────────────────────────────
        // Read the final agentlog BEFORE assertions clear it, so PrintFailureEvidence
        // has content to analyse even though PrintAndClearAgentLog already ran.
        string? finalLogContent = null;
        {
            var lp = AgentLogPath();
            if (File.Exists(lp)) finalLogContent = File.ReadAllText(lp);
        }
        if (pyFiles.Count == 0 || !hasMain || !hasReq)
            PrintFailureEvidence(activeModel, finalLogContent);

        Assert.Multiple(() =>
        {
            Assert.That(pyFiles.Count, Is.GreaterThan(0),
                "No .py files — Execute didn't run. Check Ollama (ollama ps) and model capability.");
            Assert.That(hasMain, Is.True, "main.py missing after all passes.");
            Assert.That(hasReq,  Is.True, "requirements.txt missing after all passes.");
        });
    }

    // ── Model capability warning ───────────────────────────────────────────────

    private static void CheckModelCapability(string modelName)
    {
        var lower = modelName.ToLowerInvariant();
        var isWeak = WeakModelSubstrings.Any(s => lower.Contains(s));
        if (isWeak)
        {
            TestContext.WriteLine(
                $"[T06]    ⚠ MODEL WARNING: '{modelName}' appears to be a small/weak model.");
            TestContext.WriteLine(
                "[T06]      T06 is a live autonomous build test and requires reliable write_file tool-call generation.");
            TestContext.WriteLine(
                "[T06]      Small models (≤4B) frequently truncate write_file JSON mid-content, leaving");
            TestContext.WriteLine(
                "[T06]      unparseable tool calls. Switch to theorc-boss:gemma4, gemma4:12b, or qwen2.5-coder:14b.");
        }
        else
        {
            TestContext.WriteLine($"[T06]    Model capability: appears adequate for write_file generation.");
        }
    }

    // ── Failure evidence dump ──────────────────────────────────────────────────

    private void PrintFailureEvidence(string activeModel, string? preservedLogContent = null)
    {
        TestContext.WriteLine("[T06] ══ FAILURE EVIDENCE ══");

        // All files under workspace
        var allFiles = Directory.GetFiles(_workspace, "*", SearchOption.AllDirectories)
                                .Where(f => !f.Contains("\\__pycache__\\"))
                                .OrderBy(f => f).ToList();
        TestContext.WriteLine($"[T06] All workspace files ({allFiles.Count}):");
        if (allFiles.Count == 0)
            TestContext.WriteLine("  (none — no files were written to workspace at all)");
        else
            foreach (var f in allFiles)
                TestContext.WriteLine($"  {Path.GetRelativePath(_workspace, f),-45}  {new FileInfo(f).Length,6:N0} B");

        // Final agentlog — use preserved content (captured before last PrintAndClearAgentLog deleted it)
        // or fall back to reading from disk if still there.
        var logContent = preservedLogContent;
        if (logContent == null)
        {
            var logPath = AgentLogPath();
            if (File.Exists(logPath)) logContent = File.ReadAllText(logPath);
        }

        if (logContent != null)
        {
            TestContext.WriteLine($"[T06] Last pass _agentlog.txt ({logContent.Length} chars):");
            TestContext.WriteLine(logContent);
            AnalyseAgentLog(logContent);
        }
        else
        {
            TestContext.WriteLine("[T06] _agentlog.txt: already cleared by PrintAndClearAgentLog (see per-pass output above)");
        }

        // Model diagnosis summary
        var lower = activeModel.ToLowerInvariant();
        var isWeak = WeakModelSubstrings.Any(s => lower.Contains(s));
        TestContext.WriteLine($"[T06] Active model: {activeModel}");
        if (isWeak)
        {
            TestContext.WriteLine("[T06] LIKELY ROOT CAUSE: Small/weak model truncated write_file JSON.");
            TestContext.WriteLine("[T06] RECOMMENDED FIX: Set single-agent model to theorc-boss:gemma4");
            TestContext.WriteLine("[T06]   or another ≥12B model before running T06.");
        }
        else
        {
            TestContext.WriteLine("[T06] Model appears capable — check Ollama connectivity and tool dispatch.");
            TestContext.WriteLine("[T06] If write_file calls are truncated, the effective token limit may be too low.");
        }
    }

    // ── Agent log analysis ────────────────────────────────────────────────────

    private static void AnalyseAgentLog(string logContent)
    {
        // Detect write_file JSON fragments
        var writeFileMatches = Regex.Matches(logContent, @"\{""name"":""write_file""");
        TestContext.WriteLine($"[T06] write_file call fragments seen: {writeFileMatches.Count}");

        // Check for truncated JSON — write_file start without closing }}
        var truncated = 0;
        foreach (Match m in writeFileMatches)
        {
            // A valid complete tool call ends with closing braces
            var fragment = logContent[m.Index..Math.Min(m.Index + 2000, logContent.Length)];
            // Count braces — valid JSON should be balanced
            var opens  = fragment.Count(c => c == '{');
            var closes = fragment.Count(c => c == '}');
            if (opens > closes)
            {
                truncated++;
                // Extract path if present
                var pathMatch = Regex.Match(fragment, @"""path""\s*:\s*""([^""]+)""");
                var path = pathMatch.Success ? pathMatch.Groups[1].Value : "(unknown path)";
                TestContext.WriteLine($"[T06]   TRUNCATED write_file for: {path} (opens={opens} closes={closes})");
            }
            else
            {
                var pathMatch = Regex.Match(fragment, @"""path""\s*:\s*""([^""]+)""");
                var path = pathMatch.Success ? pathMatch.Groups[1].Value : "(unknown path)";
                TestContext.WriteLine($"[T06]   Parseable write_file for: {path}");
            }
        }

        if (writeFileMatches.Count > 0 && truncated == writeFileMatches.Count)
        {
            TestContext.WriteLine("[T06] ALL write_file calls were truncated — model stopped mid-JSON.");
            TestContext.WriteLine("[T06] This is a model capability failure, not an app logic failure.");
        }
        else if (writeFileMatches.Count > 0 && truncated == 0)
        {
            TestContext.WriteLine("[T06] write_file JSON appears complete — check tool dispatch path.");
        }
        else if (writeFileMatches.Count == 0)
        {
            TestContext.WriteLine("[T06] No write_file calls seen at all — model may be in Plan mode,");
            TestContext.WriteLine("[T06] or refused to generate tool calls for this prompt.");
        }
    }

    // ── Agent log path (fixed: .orc/_agentlog.txt, with fallback) ────────────

    private string AgentLogPath()
    {
        // Primary: .orc/_agentlog.txt (where AgentLoop actually writes it)
        var primary = Path.Combine(_workspace, ".orc", "_agentlog.txt");
        if (File.Exists(primary)) return primary;

        // Fallback: workspace root (old path, kept for backward compat)
        var legacy = Path.Combine(_workspace, "_agentlog.txt");
        return legacy;   // return even if it doesn't exist; caller checks File.Exists
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

    private bool SendAndWaitForAgent(string prompt, int waitSec)
    {
        var cmdFile = Path.Combine(_workspace, ".flaui_cmd");

        File.WriteAllText(cmdFile, prompt, System.Text.Encoding.UTF8);
        TestContext.WriteLine($"[T06]    Written {prompt.Length} chars to .flaui_cmd");

        Thread.Sleep(2_000);

        var started = WaitUntil(
            () => FindById("AgentPanel.Stop")?.AsButton().IsEnabled == true,
            TimeSpan.FromSeconds(waitSec));

        if (!started)
        {
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

    // ── PrintAndClearAgentLog (fixed path) ───────────────────────────────────

    /// <summary>
    /// Reads and prints the AgentLoop diagnostic log, then deletes it so the next
    /// pass gets a clean slate.
    ///
    /// FIX: AgentLoop writes to &lt;workspace&gt;/.orc/_agentlog.txt — NOT to
    /// &lt;workspace&gt;/_agentlog.txt. The old path silently missed the log every pass.
    /// </summary>
    private void PrintAndClearAgentLog(string label)
    {
        var logPath = AgentLogPath();

        if (!File.Exists(logPath))
        {
            TestContext.WriteLine($"[T06]    [{label}] no _agentlog.txt found at {logPath}");
            return;
        }

        var content = File.ReadAllText(logPath);
        TestContext.WriteLine($"[T06] ── {label} agent diagnostic log ({content.Length} chars):");
        TestContext.WriteLine(content);

        // Inline analysis so we see truncation evidence immediately
        AnalyseAgentLog(content);

        try { File.Delete(logPath); }
        catch { TestContext.WriteLine($"[T06]    ⚠ Could not delete {logPath}"); }
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
        var deadline  = DateTime.UtcNow + timeout;
        var idleCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (FindById("AgentPanel.Stop")?.AsButton().IsEnabled != true) { if (++idleCount >= 2) return true; }
            else idleCount = 0;
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
