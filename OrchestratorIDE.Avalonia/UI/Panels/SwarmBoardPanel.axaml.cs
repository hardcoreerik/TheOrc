// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.Services.Swarm;

namespace OrchestratorIDE.UI.Panels;

public partial class SwarmBoardPanel : UserControl
{
    // ── Public properties set by MainWindow ───────────────────────────────────
    public OllamaClient?   Ollama        { get; set; }
    public HiveTaskQueue?  HiveTaskQueue { get; set; }
    public string          ActiveModel   { get; set; } = "";
    public string?         WorkspaceRoot { get; set; }
    public string?         LocalUrl      { get; set; }
    public AppSettings?    Settings      { get; set; }

    // ── Delegate callbacks injected by MainWindow ─────────────────────────────
    public Func<string, string, Task>?          AlertAsync   { get; set; }
    public Func<string, string, Task<bool>>?    ConfirmAsync { get; set; }
    public Func<string, string, Task<string?>>? InputAsync   { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<string>? StatusChanged;
    public event Action<string>? OnActivity;

    // ── Swarm session ─────────────────────────────────────────────────────────
    private SwarmSession? _session;
    private string _runOnUrl  = "";
    private string _runOnName = "localhost";

    // ── Stream / tab state ────────────────────────────────────────────────────
    private readonly Dictionary<string, string> _streams   = new();
    private readonly Dictionary<string, string> _taskToTab = new();
    private string _activeTab = "boss";
    private double _streamFontSize = 12;
    private bool   _showThinking;

    // ── Diagram / pulse ───────────────────────────────────────────────────────
    private readonly DispatcherTimer _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _pulseTick;

    // ── Auto-config ───────────────────────────────────────────────────────────
    private SwarmModelConfig? _lastAutoConfig;

    // ── Slot / review ─────────────────────────────────────────────────────────
    private int  _detectedSlots = 1;
    private bool _localReviewMode;

    // ── Construction ──────────────────────────────────────────────────────────

    public SwarmBoardPanel()
    {
        InitializeComponent();
        _pulseTimer.Tick += (_, _) => PulseTick();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? s, RoutedEventArgs e)
    {
        RefreshGate();
        InitLocalReviewControls();
        CbRunOn.SelectedIndex = 0;
        TbStream.PointerWheelChanged += OnStreamWheel;
    }

    // ── Gate ─────────────────────────────────────────────────────────────────

    public void RefreshGate()
    {
        bool hasWorkspace = !string.IsNullOrEmpty(WorkspaceRoot) &&
                            Directory.Exists(WorkspaceRoot);
        BdrWorkspaceWarn.IsVisible = !hasWorkspace;
        BdrGateWarn.IsVisible      = false;
    }

    // ── ComboBox / SelectionChanged handlers (bound in AXAML) ────────────────

    private void CbRunOn_SelectionChanged(object? s, SelectionChangedEventArgs e) { }

    private void CbBossModel_SelectionChanged(object? s, SelectionChangedEventArgs e)
        => UpdateCapabilityBadges();

    private void CbWorkerModel_SelectionChanged(object? s, SelectionChangedEventArgs e)
        => UpdateCapabilityBadges();

    private void CbResearcherModel_SelectionChanged(object? s, SelectionChangedEventArgs e)
        => UpdateCapabilityBadges();

    private void CbLocalReviewMode_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        _localReviewMode             = CbLocalReviewMode.SelectedIndex > 0;
        TbLocalReviewModel.IsVisible = _localReviewMode;
    }

    // ── Slot controls ─────────────────────────────────────────────────────────

    private void BtnSlot_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && int.TryParse(btn.Tag as string, out var slots))
        {
            _detectedSlots = slots;
            UpdateSlotButtons(slots);
            StatusChanged?.Invoke($"Parallel slots: {slots}");
        }
    }

    private void UpdateSlotButtons(int active)
    {
        var dim    = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var accent = new SolidColorBrush(Color.FromRgb(0xE8, 0xA0, 0x30));
        BtnSlot1.Background = BtnSlot2.Background = BtnSlot3.Background = BtnSlot4.Background = dim;
        if (active >= 1) BtnSlot1.Background = accent;
        if (active >= 2) BtnSlot2.Background = accent;
        if (active >= 3) BtnSlot3.Background = accent;
        if (active >= 4) BtnSlot4.Background = accent;
    }

    private async void BtnRestartOllama_Click(object? s, RoutedEventArgs e)
    {
        BtnRestartOllama.IsEnabled = false;
        BtnRestartOllama.Content   = "Restarting…";
        await Task.Run(() =>
        {
            foreach (var p in Process.GetProcessesByName("ollama"))
            {
                try { p.Kill(); p.WaitForExit(2000); } catch { }
            }
            System.Threading.Thread.Sleep(1200);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ollama", Arguments = "serve",
                    UseShellExecute = false, CreateNoWindow = true,
                });
            }
            catch
            {
                try { Process.Start(new ProcessStartInfo { FileName = "ollama", Arguments = "serve", UseShellExecute = true }); }
                catch { }
            }
        });
        await Task.Delay(2000);
        BtnRestartOllama.Content   = "✓  Done";
        BtnRestartOllama.IsEnabled = true;
        RefreshGate();
    }

    // ── Model pickers ─────────────────────────────────────────────────────────

    public void PopulateModelPickers(IReadOnlyList<string> models)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var prevBoss   = CbBossModel.SelectedItem       as string;
            var prevWorker = CbWorkerModel.SelectedItem     as string;
            var prevRes    = CbResearcherModel.SelectedItem as string;

            void Fill(ComboBox cb, string? prev)
            {
                cb.ItemsSource = models;
                cb.SelectedItem = (prev is not null && models.Contains(prev))
                    ? (object?)prev : models.FirstOrDefault();
            }

            Fill(CbBossModel,       prevBoss   ?? ActiveModel);
            Fill(CbWorkerModel,     prevWorker ?? ActiveModel);
            Fill(CbResearcherModel, prevRes    ?? ActiveModel);
            UpdateCapabilityBadges();
        });
    }

    private void UpdateCapabilityBadges()
    {
        TbBossBadges.Text       = ModelBadgeText(CbBossModel.SelectedItem       as string ?? "");
        TbWorkerBadges.Text     = ModelBadgeText(CbWorkerModel.SelectedItem     as string ?? "");
        TbResearcherBadges.Text = ModelBadgeText(CbResearcherModel.SelectedItem as string ?? "");
    }

    private static string ModelBadgeText(string m)
    {
        if (string.IsNullOrEmpty(m)) return "—";
        if (m.Contains("32b", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("70b", StringComparison.OrdinalIgnoreCase)) return "pro";
        if (m.Contains("14b", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("12b", StringComparison.OrdinalIgnoreCase)) return "mid";
        if (m.Contains("coder",  StringComparison.OrdinalIgnoreCase)) return "coder";
        if (m.Contains("boss",   StringComparison.OrdinalIgnoreCase)) return "boss";
        return "base";
    }

    private void BtnProbeBoss_Click(object? s, RoutedEventArgs e)
        => _ = ProbeModelAsync(CbBossModel.SelectedItem as string ?? "", TbBossBadges);
    private void BtnProbeWorker_Click(object? s, RoutedEventArgs e)
        => _ = ProbeModelAsync(CbWorkerModel.SelectedItem as string ?? "", TbWorkerBadges);
    private void BtnProbeResearcher_Click(object? s, RoutedEventArgs e)
        => _ = ProbeModelAsync(CbResearcherModel.SelectedItem as string ?? "", TbResearcherBadges);

    private async Task ProbeModelAsync(string model, TextBlock badge)
    {
        if (string.IsNullOrEmpty(model) || Ollama is null) return;
        badge.Text = "…";
        try
        {
            var loaded = await Ollama.GetLoadedModelsAsync();
            badge.Text = loaded.Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase))
                ? "✓ loaded" : "cached";
        }
        catch { badge.Text = "err"; }
    }

    // ── Auto-config ───────────────────────────────────────────────────────────

    private void BtnAutoConfig_Click(object? s, RoutedEventArgs e) => _ = RunAutoConfigAsync();

    private async Task RunAutoConfigAsync()
    {
        BtnAutoConfig.IsEnabled       = false;
        TbHardwareSummary.Text        = "Detecting hardware…";
        BdrAutoConfigResult.IsVisible = false;
        try
        {
            var hw  = await SwarmConfigAdvisor.DetectHardwareAsync();
            TbHardwareSummary.Text = hw.Summary;

            List<string> models = Ollama is not null ? await Ollama.GetInstalledModelsAsync() : [];
            var cfg    = SwarmConfigAdvisor.Recommend(hw, models);
            _lastAutoConfig = cfg;

            TbAutoConfigSource.Text = cfg.Source switch
            {
                ConfigSource.BenchmarkBased => "benchmark",
                ConfigSource.ObservedBest   => "observed",
                _                           => "fallback",
            };
            TbAutoConfigTier.Text       = cfg.TierLabel;
            TbAutoConfigReasoning.Text  = cfg.Reasoning;
            TbAutoConfigBoss.Text       = cfg.BossModel;
            TbAutoConfigCoder.Text      = cfg.CoderModel;
            TbAutoConfigResearcher.Text = cfg.ResearcherModel;
            TbAutoConfigTester.Text     = cfg.TesterModel;
            BdrAutoConfigResult.IsVisible = true;
        }
        catch (Exception ex)
        {
            TbHardwareSummary.Text = $"Detection failed: {ex.Message}";
        }
        finally
        {
            BtnAutoConfig.IsEnabled = true;
        }
    }

    private void BtnApplyAutoConfig_Click(object? s, RoutedEventArgs e)
    {
        if (_lastAutoConfig is null) return;
        if (!string.IsNullOrEmpty(_lastAutoConfig.BossModel))       CbBossModel.SelectedItem       = _lastAutoConfig.BossModel;
        if (!string.IsNullOrEmpty(_lastAutoConfig.CoderModel))      CbWorkerModel.SelectedItem     = _lastAutoConfig.CoderModel;
        if (!string.IsNullOrEmpty(_lastAutoConfig.ResearcherModel)) CbResearcherModel.SelectedItem = _lastAutoConfig.ResearcherModel;
        BdrAutoConfigResult.IsVisible = false;
        StatusChanged?.Invoke($"Auto-config applied — {_lastAutoConfig.TierLabel}.");
    }

    // ── Local review ──────────────────────────────────────────────────────────

    private void InitLocalReviewControls()
    {
        if (CbLocalReviewMode.Items.Count == 0)
        {
            CbLocalReviewMode.Items.Add("Disabled");
            CbLocalReviewMode.Items.Add("Lightweight");
            CbLocalReviewMode.Items.Add("Full review");
            CbLocalReviewMode.SelectedIndex = 0;
        }
    }

    private void BtnOpenWorkspace_Click(object? s, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(WorkspaceRoot))
            Process.Start(new ProcessStartInfo { FileName = WorkspaceRoot, UseShellExecute = true });
    }

    // ── Launch swarm ──────────────────────────────────────────────────────────

    private async void BtnLaunch_Click(object? s, RoutedEventArgs e)
    {
        var goal = TbGoal.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(goal))
        {
            await (AlertAsync?.Invoke("Please enter a goal.", "Launch Swarm") ?? Task.CompletedTask);
            return;
        }
        if (Ollama is null)
        {
            await (AlertAsync?.Invoke("Ollama client not configured.", "Launch Swarm") ?? Task.CompletedTask);
            return;
        }

        var bossModel   = CbBossModel.SelectedItem       as string ?? ActiveModel;
        var workerModel = CbWorkerModel.SelectedItem     as string ?? ActiveModel;
        var resModel    = CbResearcherModel.SelectedItem as string ?? ActiveModel;

        _runOnUrl  = LocalUrl ?? "http://localhost:11434";
        _runOnName = "localhost";

        _streams.Clear();
        _taskToTab.Clear();
        _activeTab = "boss";
        SwitchTab("boss");

        _session = new SwarmSession(new OllamaRuntime(Ollama), bossModel, WorkspaceRoot, workerModel, resModel);

        if (Settings is not null)
        {
            _session.ReviewGateEnabled     = Settings.SwarmReviewGateEnabled;
            _session.HiveWorktreeIsolation = Settings.HiveWorktreeIsolation;
            if (_localReviewMode)
            {
                _session.LocalReviewMode  = CbLocalReviewMode.SelectedItem as string ?? "";
                _session.LocalReviewModel = workerModel;
            }
        }

        if (HiveTaskQueue is not null)
        {
            _session.SetDistributedQueue(HiveTaskQueue);
            OnActivity?.Invoke("🐝 HIVE MIND distributed mode — Warchief queue active.");
        }

        _session.SandboxBypassRequestHandler = async (toolName, escapedPath, sandboxRoot, ct) =>
            await (ConfirmAsync?.Invoke(
                $"Allow '{toolName}' to write outside sandbox?\n\n{escapedPath}",
                "Sandbox Bypass Request") ?? Task.FromResult(false));

        WireSessionEvents(_session);
        EnterActiveMode();
        TbActiveGoal.Text = goal;
        OnActivity?.Invoke($"🐝 Swarm running on {_runOnName} ({_runOnUrl})");
        _ = _session.RunAsync(goal);
    }

    // ── Session events ────────────────────────────────────────────────────────

    private void WireSessionEvents(SwarmSession s)
    {
        s.OnBossToken     += OnBossToken;
        s.OnWorkerToken   += OnWorkerToken;
        s.OnTasksPlanned  += OnTasksPlanned;
        s.OnTaskChanged   += OnTaskChanged;
        s.OnSwarmComplete += OnSwarmComplete;
        s.OnError         += OnError;
        s.OnStopped       += () => Dispatcher.UIThread.InvokeAsync(OnSwarmStopped);
        s.OnActivity      += (agentKey, msg) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            AppendLog(agentKey, $"[{agentKey}] {msg}");
            OnActivity?.Invoke(msg);
        });
        s.OnStagingReady += (runId, stagingDir, files) =>
            Dispatcher.UIThread.InvokeAsync(() => OnStagingReady(runId, stagingDir, files));
        s.OnGateResult += result =>
            Dispatcher.UIThread.InvokeAsync(() => ShowGateResult(result));
    }

    private void OnBossToken(string token)
    {
        lock (_streams) { _streams["boss"] = (_streams.GetValueOrDefault("boss", "")) + token; }
        if (_activeTab == "boss")
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                TbStream.Text = _streams.GetValueOrDefault("boss", "");
                StreamScroll.ScrollToEnd();
            });
    }

    private void OnWorkerToken(string taskId, string token)
    {
        if (!_taskToTab.TryGetValue(taskId, out var tab)) return;
        lock (_streams) { _streams[tab] = (_streams.GetValueOrDefault(tab, "")) + token; }
        if (_activeTab == tab)
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                TbStream.Text = _streams.GetValueOrDefault(tab, "");
                StreamScroll.ScrollToEnd();
            });
    }

    private void OnTasksPlanned(List<SwarmTask> tasks)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _taskToTab.Clear();
            foreach (var task in tasks)
            {
                var tab = RoleToTab(task.Role);
                _taskToTab[task.Id] = _taskToTab.ContainsValue(tab)
                    ? tab + "_" + task.Id[..4] : tab;
            }
            BuildTaskCardStrip(tasks);
        });
    }

    private void OnTaskChanged(SwarmTask task)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            SetNodeActive(task.Role, task.IsActive);

            var roleTab = RoleToTab(task.Role);
            if (task.Status == SwarmTaskStatus.InProgress)
                AppendLog(roleTab, $"⚡ Started: {task.Title}");
            else if (task.Status == SwarmTaskStatus.Done)
                AppendLog(roleTab, $"✓ Done: {task.Title}");
            else if (task.Status == SwarmTaskStatus.Error)
                AppendLog(roleTab, $"✗ Error: {task.ErrorMessage}");

            if (task.Status == SwarmTaskStatus.WaitingForUser && task.PendingQuestion is not null)
                ShowCoWork(task);
            else if (task.Status != SwarmTaskStatus.WaitingForUser)
                HideCoWork(task.Role);

            if (_session is not null) BuildTaskCardStrip(_session.Tasks);
        });
    }

    private void OnSwarmComplete(string merged)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            _streams["results"] = merged;
            SwitchTab("results");
            PnlLaunchPad.IsVisible = true;
            OnSwarmStopped();
        });
    }

    private void OnError(string message)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            SwitchTab("results");
            BdrRunError.IsVisible = true;
            TbRunError.Text       = message;
            AppendLog("boss", $"\n[ERROR] {message}\n");
        });
    }

    private void OnStagingReady(string runId, string stagingDir, IReadOnlyList<string> files)
    {
        PnlLaunchPad.IsVisible   = true;
        PnlResultFiles.IsVisible = files.Count > 0;
        if (files.Count > 0)
        {
            var list = string.Join("\n", files.Take(20).Select(f => $"  • {f}"));
            TbRunOutput.Text += $"\n=== Staged files ({files.Count}) ===\n{list}\n";
            RunOutputScroll.IsVisible = true;
        }
        SwitchTab("results");
    }

    private void ShowGateResult(ReviewGateService.GateResult result)
    {
        BdrGateResult.IsVisible = true;
        bool passed = result.Verdict == ReviewGateService.GateVerdict.Clean;
        TbGateBadge.Text       = passed ? "✅  Gate PASSED" : "⚠️  Gate FAILED";
        TbGateFindings.Text    = result.RawVerdict;
        TbGateBadge.Foreground = new SolidColorBrush(passed
            ? Color.FromRgb(0x76, 0xB9, 0x00) : Color.FromRgb(0xF4, 0x47, 0x47));
    }

    // ── Node pulse ────────────────────────────────────────────────────────────

    private void SetNodeActive(SwarmWorkerRole role, bool active)
    {
        var ico = role switch
        {
            SwarmWorkerRole.Researcher  => IcoResearcherThink,
            SwarmWorkerRole.Coder       => IcoCoderThink,
            SwarmWorkerRole.UIDeveloper => IcoUIDevThink,
            SwarmWorkerRole.Tester      => IcoTesterThink,
            _                           => null,
        };
        if (ico is not null) ico.IsVisible = active;
        DrawConnectionLines();
    }

    private void PulseTick()
    {
        _pulseTick = !_pulseTick;
        var opacity = _pulseTick ? 1.0 : 0.4;
        foreach (var ico in new TextBlock[] { IcoResearcherThink, IcoCoderThink, IcoUIDevThink, IcoTesterThink })
            if (ico.IsVisible) ico.Opacity = opacity;
    }

    private static string RoleToTab(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher  => "researcher",
        SwarmWorkerRole.Coder       => "coder",
        SwarmWorkerRole.UIDeveloper => "uidev",
        SwarmWorkerRole.Tester      => "tester",
        _                           => "boss",
    };

    // ── Tabs ──────────────────────────────────────────────────────────────────

    private void StreamTab_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn) SwitchTab(btn.Tag as string ?? "boss");
    }

    private void SwitchTab(string tab)
    {
        _activeTab    = tab;
        TbStream.Text = _streams.GetValueOrDefault(tab, "");
        StreamScroll.ScrollToEnd();

        foreach (var (tb, key) in new[]
        {
            (TabBoss, "boss"), (TabResearcher, "researcher"), (TabCoder, "coder"),
            (TabUIDev, "uidev"), (TabTester, "tester"), (TabResults, "results"),
        })
        {
            bool on = key == tab;
            tb.Background = new SolidColorBrush(on ? Color.FromRgb(0x1F, 0x3D, 0x00) : Color.FromRgb(0x0E, 0x0E, 0x0E));
            tb.Foreground = new SolidColorBrush(on ? Color.FromRgb(0x76, 0xB9, 0x00) : Color.FromRgb(0x88, 0x88, 0x88));
        }

        RunOutputScroll.IsVisible = tab == "results";
    }

    // ── Think toggle ──────────────────────────────────────────────────────────

    private void BtnThinkToggle_Click(object? s, RoutedEventArgs e)
    {
        _showThinking          = !_showThinking;
        BdrThinking.IsVisible  = _showThinking;
        BtnThinkToggle.Content = _showThinking ? "▼ Thinking" : "💭";
        TbThinkCount.Text      = "";
    }

    // ── Stream font zoom ──────────────────────────────────────────────────────

    private void OnStreamWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
        e.Handled = true;
        _streamFontSize    = Math.Clamp(_streamFontSize + (e.Delta.Y > 0 ? 1.0 : -1.0), 8, 28);
        TbStream.FontSize  = _streamFontSize;
        TbStreamFontSize.Text = $"{(int)_streamFontSize}";
    }

    // ── Active / idle mode ────────────────────────────────────────────────────

    private void EnterActiveMode()
    {
        PnlIdle.IsVisible          = false;
        PnlActive.IsVisible        = true;
        BtnStopSwarm.IsVisible     = true;
        BtnLaunchProject.IsVisible = false;
        BtnNewRun.IsVisible        = false;
        BdrDirective.IsVisible     = true;
        StatusDot.Fill             = new SolidColorBrush(Color.FromRgb(0xE8, 0xA0, 0x30));
        TbStatusChip.Text          = "RUNNING";
        TbModelName.Text           = CbBossModel.SelectedItem as string ?? "";

        foreach (var ico in new TextBlock[] { IcoResearcherThink, IcoCoderThink, IcoUIDevThink, IcoTesterThink })
            ico.IsVisible = false;
        LogBoss.Text = LogResearcher.Text = LogCoder.Text = LogUIDev.Text = LogTester.Text = "";
        TbRunOutput.Text        = "";
        TbThinking.Text         = "";
        PnlLaunchPad.IsVisible  = false;
        BdrGateResult.IsVisible = false;
        BdrRunError.IsVisible   = false;

        _pulseTimer.Start();
        DrawConnectionLines();
    }

    private void OnSwarmStopped()
    {
        _pulseTimer.Stop();
        BtnStopSwarm.IsVisible = false;
        BtnNewRun.IsVisible    = true;
        BdrDirective.IsVisible = false;
        StatusDot.Fill         = new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44));
        TbStatusChip.Text      = "DONE";
        var outDir = _session?.GetOutputProjectDir();
        BtnLaunchProject.IsVisible = !string.IsNullOrEmpty(outDir) && Directory.Exists(outDir);
        DrawConnectionLines();
    }

    // ── Swarm control ─────────────────────────────────────────────────────────

    private void BtnStopSwarm_Click(object? s, RoutedEventArgs e)
    {
        _session?.Stop();
        StatusChanged?.Invoke("Swarm stopped.");
    }

    private void BtnNewRun_Click(object? s, RoutedEventArgs e)
    {
        _session = null;
        PnlIdle.IsVisible          = true;
        PnlActive.IsVisible        = false;
        BtnStopSwarm.IsVisible     = false;
        BtnNewRun.IsVisible        = false;
        BdrDirective.IsVisible     = false;
        BtnLaunchProject.IsVisible = false;
        StatusDot.Fill             = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        TbStatusChip.Text          = "idle";
        RefreshGate();
    }

    private void BtnLaunchProject_Click(object? s, RoutedEventArgs e)
    {
        var dir = _session?.GetOutputProjectDir();
        if (!string.IsNullOrEmpty(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void BtnSendDirective_Click(object? s, RoutedEventArgs e)
    {
        var text = TbDirective.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text) || _session is null) return;
        _session.InjectDirective(text);
        TbDirective.Text = "";
        AppendLog("boss", $"\n[→ DIRECTIVE: {text}]\n");
    }

    // ── Diagram ───────────────────────────────────────────────────────────────

    private void DiagramGrid_SizeChanged(object? s, SizeChangedEventArgs e) => DrawConnectionLines();

    private void DrawConnectionLines()
    {
        NodeCanvas.Children.Clear();
        if (!PnlActive.IsVisible) return;

        var bc = GetControlCenter(NodeBoss);
        if (bc is null) return;

        foreach (var (node, ico) in new[]
        {
            (NodeResearcher, IcoResearcherThink),
            (NodeCoder,      IcoCoderThink),
            (NodeUIDev,      IcoUIDevThink),
            (NodeTester,     IcoTesterThink),
        })
        {
            var wc = GetControlCenter(node);
            if (wc is null) continue;

            bool active = ico.IsVisible;
            var line = new Line
            {
                StartPoint      = new Avalonia.Point(bc.Value.X, bc.Value.Y),
                EndPoint        = new Avalonia.Point(wc.Value.X, wc.Value.Y),
                StrokeThickness = active ? 2.0 : 1.0,
                Stroke = new SolidColorBrush(active
                    ? Color.FromRgb(0x4E, 0xC9, 0x4E)
                    : Color.FromRgb(0x3A, 0x3A, 0x3A)),
                Opacity = active ? 1.0 : 0.5,
            };
            if (!active) line.StrokeDashArray = new AvaloniaList<double> { 5.0, 4.0 };
            NodeCanvas.Children.Add(line);
        }
    }

    // Walk up to DiagramGrid to convert node-local bounds into canvas coordinate space.
    private Avalonia.Point? GetControlCenter(Control c)
    {
        double x = c.Bounds.Left + c.Bounds.Width  / 2;
        double y = c.Bounds.Top  + c.Bounds.Height / 2;
        var curr = c.Parent as Control;
        while (curr is not null && !ReferenceEquals(curr, DiagramGrid))
        {
            x += curr.Bounds.Left;
            y += curr.Bounds.Top;
            curr = curr.Parent as Control;
        }
        return curr is null ? null : new Avalonia.Point(x, y);
    }

    // ── Task card strip ───────────────────────────────────────────────────────

    private void BuildTaskCardStrip(List<SwarmTask> tasks)
    {
        TaskCardPanel.Children.Clear();
        foreach (var task in tasks)
        {
            var accent = task.Role switch
            {
                SwarmWorkerRole.Researcher  => Color.FromRgb(0x4E, 0xC9, 0xE0),
                SwarmWorkerRole.Coder       => Color.FromRgb(0x76, 0xB9, 0x00),
                SwarmWorkerRole.UIDeveloper => Color.FromRgb(0xCC, 0x99, 0xFF),
                SwarmWorkerRole.Tester      => Color.FromRgb(0xFF, 0xA5, 0x00),
                _                           => Color.FromRgb(0xE8, 0xA0, 0x30),
            };
            bool isDone    = task.Status == SwarmTaskStatus.Done;
            bool isRunning = task.Status == SwarmTaskStatus.InProgress;

            var sp = new StackPanel { Margin = new Avalonia.Thickness(1, 0, 1, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = task.StatusIcon, FontSize = 8, FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            sp.Children.Add(new TextBlock
            {
                Text = Truncate(task.Title, 18), FontSize = 8,
                Foreground = new SolidColorBrush(isDone
                    ? Color.FromRgb(0x60, 0xAA, 0x60)
                    : isRunning ? Color.FromRgb(0xE8, 0xA0, 0x30)
                                : Color.FromRgb(0x88, 0x88, 0x88)),
                TextWrapping = TextWrapping.Wrap, Width = 70,
                TextAlignment = TextAlignment.Center,
            });
            TaskCardPanel.Children.Add(new Border
            {
                Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
                BorderBrush     = new SolidColorBrush(isRunning ? accent : Color.FromRgb(0x2A, 0x2A, 0x2A)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius    = new Avalonia.CornerRadius(4),
                Padding         = new Avalonia.Thickness(4, 2, 4, 2),
                Child           = sp,
            });
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── Log helper ────────────────────────────────────────────────────────────

    private void AppendLog(string role, string msg)
    {
        var (tb, sv) = role switch
        {
            "boss"       => (LogBoss,       ScrollBoss),
            "researcher" => (LogResearcher,  ScrollResearcher),
            "coder"      => (LogCoder,       ScrollCoder),
            "uidev"      => (LogUIDev,       ScrollUIDev),
            "tester"     => (LogTester,      ScrollTester),
            _            => (LogBoss,        ScrollBoss),
        };
        Dispatcher.UIThread.InvokeAsync(() => { tb.Text += msg + "\n"; sv.ScrollToEnd(); });
    }

    // ── Launch pad ────────────────────────────────────────────────────────────

    private void BtnRunProject_Click(object? s, RoutedEventArgs e) => _ = RunProjectAsync();

    private async Task RunProjectAsync()
    {
        if (_session is null) return;
        BtnRunProject.IsEnabled   = false;
        TbRunOutput.Text          = "";
        RunOutputScroll.IsVisible = true;
        try
        {
            var task = _session.Tasks.LastOrDefault(t =>
                t.Role is SwarmWorkerRole.Tester or SwarmWorkerRole.Coder);
            if (task is not null)
                await _session.ContinueWorkerAsync(task.Id, "run the project and capture the output");
        }
        finally { BtnRunProject.IsEnabled = true; }
    }

    private void BtnOpenResultFolder_Click(object? s, RoutedEventArgs e)
    {
        var dir = _session?.GetOutputProjectDir();
        if (!string.IsNullOrEmpty(dir))
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private async void BtnApplyToWorkspace_Click(object? s, RoutedEventArgs e)
    {
        if (_session is null || string.IsNullOrEmpty(WorkspaceRoot)) return;
        var ok = await (ConfirmAsync?.Invoke(
            "Copy all generated files to the current workspace?\n\nThis will overwrite matching files.",
            "Apply to Workspace") ?? Task.FromResult(false));
        if (!ok) return;

        var srcDir = _session.GetOutputProjectDir();
        if (!string.IsNullOrEmpty(srcDir) && Directory.Exists(srcDir))
        {
            foreach (var file in Directory.GetFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel  = System.IO.Path.GetRelativePath(srcDir, file);
                var dest = System.IO.Path.Combine(WorkspaceRoot, rel);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }
        StatusChanged?.Invoke("Files applied to workspace.");
    }

    private void BtnFixThis_Click(object? s, RoutedEventArgs e) => _ = FixThisAsync();

    private async Task FixThisAsync()
    {
        if (_session is null) return;
        BtnFixThis.IsEnabled = false;
        try
        {
            var coderTask = _session.Tasks.LastOrDefault(t => t.Role == SwarmWorkerRole.Coder);
            if (coderTask is not null)
                await _session.ContinueWorkerAsync(coderTask.Id, "fix the error above");
        }
        finally { BtnFixThis.IsEnabled = true; }
    }

    // ── Co-work panels ────────────────────────────────────────────────────────

    private void ShowCoWork(SwarmTask task)
    {
        var (panel, tbLabel, tbQ, wp, tbInput, btnSend) = GetCoWorkControls(task.Role);
        if (panel is null) return;

        panel.IsVisible = true;
        if (tbLabel is not null) tbLabel.Text = $"{task.Role} — {task.Title}";
        if (tbQ    is not null) tbQ.Text      = task.PendingQuestion ?? "";

        if (wp is not null)
        {
            wp.Children.Clear();
            foreach (var opt in task.PendingOptions)
            {
                var optCopy = opt;
                var btn = new Button
                {
                    Content = optCopy,
                    Padding = new Avalonia.Thickness(8, 3, 8, 3),
                    Margin  = new Avalonia.Thickness(0, 0, 4, 4),
                    Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
                    Foreground      = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)),
                    BorderThickness = new Avalonia.Thickness(1), FontSize = 11,
                };
                btn.Click += (_, _) =>
                {
                    HideCoWork(task.Role);
                    _session?.ProvideWorkerReply(task.Id, optCopy);
                };
                wp.Children.Add(btn);
            }
        }

        if (btnSend is not null) btnSend.Tag = task;   // override AXAML string tag
    }

    private void HideCoWork(SwarmWorkerRole role)
    {
        var (panel, _, _, _, _, _) = GetCoWorkControls(role);
        if (panel is not null) panel.IsVisible = false;
    }

    private void BtnCoWorkSend_Click(object? s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not SwarmTask task) return;
        var (_, _, _, _, tbInput, _) = GetCoWorkControls(task.Role);
        var text = tbInput?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;
        if (tbInput is not null) tbInput.Text = "";
        HideCoWork(task.Role);
        _session?.ProvideWorkerReply(task.Id, text);
    }

    private void BtnSteer_Click(object? s, RoutedEventArgs e)
    {
        if (s is not Button btn || _session is null) return;
        var role = (btn.Tag as string) switch
        {
            "researcher" => (SwarmWorkerRole?)SwarmWorkerRole.Researcher,
            "coder"      => SwarmWorkerRole.Coder,
            "uidev"      => SwarmWorkerRole.UIDeveloper,
            "tester"     => SwarmWorkerRole.Tester,
            _            => null,
        };
        if (role is null) return;
        var task = _session.Tasks.FirstOrDefault(t => t.Role == role);
        if (task is null) return;
        _session.SteerWorker(task.Id, TbDirective.Text?.Trim() ?? "[steer]");
    }

    private (Border? Panel, TextBlock? Label, TextBlock? Question,
             WrapPanel? Options, TextBox? Input, Button? Send)
    GetCoWorkControls(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher  => (CoWorkResearcher, TbCoWorkLabelResearcher, TbQuestionResearcher,
                                        WpOptionsResearcher, TxtCoWorkResearcher, BtnCoWorkSendResearcher),
        SwarmWorkerRole.Coder       => (CoWorkCoder,      TbCoWorkLabelCoder,      TbQuestionCoder,
                                        WpOptionsCoder,   TxtCoWorkCoder,          BtnCoWorkSendCoder),
        SwarmWorkerRole.UIDeveloper => (CoWorkUIDev,      TbCoWorkLabelUIDev,      TbQuestionUIDev,
                                        WpOptionsUIDev,   TxtCoWorkUIDev,          BtnCoWorkSendUIDev),
        SwarmWorkerRole.Tester      => (CoWorkTester,     TbCoWorkLabelTester,     TbQuestionTester,
                                        WpOptionsTester,  TxtCoWorkTester,         BtnCoWorkSendTester),
        _                           => (null, null, null, null, null, null),
    };

    // ── Metrics history ───────────────────────────────────────────────────────

    public void SetMetricsHistory(IReadOnlyList<(string RunId, string Summary)> history)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            PnlMetricsHistory.Children.Clear();
            foreach (var (runId, summary) in history)
            {
                PnlMetricsHistory.Children.Add(new TextBlock
                {
                    Text = $"  {runId}  {summary}",
                    FontFamily = new Avalonia.Media.FontFamily("Consolas"), FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                    Margin = new Avalonia.Thickness(0, 0, 0, 3),
                });
            }
        });
    }
}
