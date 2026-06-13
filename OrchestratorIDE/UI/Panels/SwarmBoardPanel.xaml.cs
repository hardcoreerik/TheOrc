using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Swarm;
using OrchestratorIDE.Services.ToolCalls;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Hub-and-spoke visual board for the multi-agent Swarm feature.
///
/// Layout:
///   Left  — node diagram (Boss at top, 4 worker nodes below, canvas lines)
///   Right — tabbed live stream output per agent
///   Bottom strip — scrollable task card list
///
/// Gate: requires OLLAMA_NUM_PARALLEL ≥ 3. Model capability is validated via
/// GOBLIN MIND profiles (BossScore / CoderScore thresholds) — no hardcoded model names.
/// </summary>
public partial class SwarmBoardPanel : UserControl
{
    // ── Dependencies (set by MainWindow) ──────────────────────────────────────
    public OllamaClient? Ollama        { get; set; }

    /// <summary>HIVE MIND Phase 3: running task queue to attach to new SwarmSessions.</summary>
    public Services.Hive.HiveTaskQueue? HiveTaskQueue { get; set; }

    /// <summary>HIVE MIND: Ollama base URL the swarm runs against. null = This PC.</summary>
    private string? _runOnUrl;
    private string  _runOnName = "This PC";

    /// <summary>Populates the "Run on" hive picker from reachable hosts.</summary>
    public void SetHiveHosts(IReadOnlyList<Services.Hive.HiveHost> hosts)
    {
        CbRunOn.Items.Clear();
        foreach (var h in hosts)
        {
            CbRunOn.Items.Add(new System.Windows.Controls.ComboBoxItem
            {
                Content = h.Name + (h.Reachable == false ? " (offline)" : ""),
                Tag = h,
                IsEnabled = h.Name == "This PC" || h.Reachable != false,
            });
        }
        if (CbRunOn.SelectedIndex < 0 && CbRunOn.Items.Count > 0) CbRunOn.SelectedIndex = 0;
    }

    private async void CbRunOn_SelectionChanged(object s, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CbRunOn.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: Services.Hive.HiveHost h })
            return;

        _runOnName = h.Name;
        _runOnUrl  = h.Name == "This PC" ? null : h.Url;

        if (_runOnUrl is null)
        {
            // Back to This PC — restore local models.
            RepopulateModels(_localModels);
            StatusChanged?.Invoke("Swarm runs on This PC");
            return;
        }

        // Remote node: the model pickers MUST reflect THAT node's installed
        // models, not This PC's — otherwise the boss model (e.g. the local 12B)
        // gets sent to a node that doesn't have it (the 2026-06 hardcorepc 404).
        StatusChanged?.Invoke($"Querying {h.Name}'s models…");
        await Services.Hive.HiveHosts.ProbeAsync(h);
        if (h.Reachable == true && h.Models.Count > 0)
        {
            RepopulateModels(h.Models);
            StatusChanged?.Invoke(
                $"Swarm will run on {h.Name} — {h.Models.Count} models there. " +
                "Pick ones that fit its GPU.");
        }
        else
        {
            StatusChanged?.Invoke(
                $"⚠ {h.Name} is unreachable or has no models — swarm may fail. Re-pick a node.");
        }
    }

    public string        ActiveModel   { get; set; } = "";
    public string?       WorkspaceRoot { get; set; }
    /// <summary>Local Ollama base URL (e.g. "http://localhost:11434"). Used for HIVE MIND host loading.</summary>
    public string?       LocalUrl      { get; set; }
    public AppSettings?  Settings      { get; set; }   // for opening the probe window

    // ── Worker models (separate from boss/orchestrator model) ────────────────
    private string _workerModel     = "";  // Coder + UIDev
    private string _researcherModel = "";  // Researcher (may differ for VRAM savings)

    /// <summary>
    /// Populates the coder and researcher model dropdowns from the installed model list.
    /// Call this whenever the installed models change.
    /// </summary>
    public void SetModels(IReadOnlyList<string> models, string activeModel,
        string workerModel, string researcherModel = "")
    {
        void Populate(System.Windows.Controls.ComboBox cb, string saved)
        {
            cb.Items.Clear();
            foreach (var m in models) cb.Items.Add(m);
            var idx = Enumerable.Range(0, models.Count)
                                .FirstOrDefault(i => models[i] == saved, -1);
            cb.SelectedIndex = idx >= 0 ? idx : (models.Count > 0 ? 0 : -1);
        }

        Populate(CbBossModel,       activeModel);
        Populate(CbWorkerModel,     workerModel);
        Populate(CbResearcherModel, string.IsNullOrWhiteSpace(researcherModel) ? workerModel : researcherModel);

        _workerModel     = workerModel;
        _researcherModel = string.IsNullOrWhiteSpace(researcherModel) ? workerModel : researcherModel;
        _localModels     = models;   // remembered so RUN ON can restore "This PC"
    }

    private IReadOnlyList<string> _localModels = [];

    /// <summary>
    /// Repopulates the three model pickers from a specific list (a remote node's
    /// models when running there, or the local list for This PC). Keeps the
    /// current selection if it still exists, else picks the first available.
    /// </summary>
    private void RepopulateModels(IReadOnlyList<string> models)
    {
        void Fill(System.Windows.Controls.ComboBox cb)
        {
            var keep = cb.SelectedItem as string;
            cb.Items.Clear();
            foreach (var m in models) cb.Items.Add(m);
            var idx = keep != null ? models.ToList().IndexOf(keep) : -1;
            cb.SelectedIndex = idx >= 0 ? idx : (models.Count > 0 ? 0 : -1);
        }
        Fill(CbBossModel); Fill(CbWorkerModel); Fill(CbResearcherModel);
    }

    // ── Runtime state ─────────────────────────────────────────────────────────
    private SwarmSession?             _session;
    private string                    _activeTab  = "boss";
    private readonly DispatcherTimer  _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool                      _pulseOn;
    private string                    _lastOutputProjectDir = "";

    // Launch Pad state
    private string                    _lastStagingDir   = "";
    private IReadOnlyList<string>     _lastStagedFiles  = [];
    private string                    _lastRunGoal      = "";
    private CancellationTokenSource? _runCts;

    // Per-tab output buffers (thinking stripped out)
    private readonly Dictionary<string, string> _streams = new()
    {
        ["boss"] = "", ["researcher"] = "", ["coder"] = "", ["uidev"] = "", ["tester"] = "",
    };

    // Per-tab thinking buffers (<think>…</think> content)
    private readonly Dictionary<string, string> _thinkStreams = new()
    {
        ["boss"] = "", ["researcher"] = "", ["coder"] = "", ["uidev"] = "", ["tester"] = "",
    };

    // Per-tab raw remainder for incremental <think> tag parsing
    private readonly Dictionary<string, string> _rawPending = new()
    {
        ["boss"] = "", ["researcher"] = "", ["coder"] = "", ["uidev"] = "", ["tester"] = "",
    };

    // Per-tab: are we currently inside a <think> block?
    private readonly Dictionary<string, bool> _inThink = new()
    {
        ["boss"] = false, ["researcher"] = false, ["coder"] = false, ["uidev"] = false, ["tester"] = false,
    };

    // Whether the thinking pane is currently visible
    private bool _thinkVisible;

    // Stream font zoom (Ctrl+Wheel; Ctrl+0 resets)
    private double _streamFontSize = 12;

    // Task-id → tab key mapping (populated when tasks are planned)
    private readonly Dictionary<string, string> _taskTabMap = [];

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<string>? StatusChanged;
    public event Action<string>? OnActivity;              // swarm activity log entries → MainWindow
    public event Action<string>? BossModelChanged;        // fired when user picks a different boss/orchestrator model
    public event Action<string>? WorkerModelChanged;      // fired when user picks a different coder model
    public event Action<string>? ResearcherModelChanged;  // fired when user picks a different researcher model

    /// <summary>
    /// Fired when the user clicks "Open Folder" on the workspace warning banner.
    /// MainWindow handles it by opening the folder picker (same as single mode).
    /// </summary>
    public event Action? WorkspaceChangeRequested;

    // ── Construction ──────────────────────────────────────────────────────────

    public SwarmBoardPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        _pulseTimer.Tick += (_, _) =>
        {
            _pulseOn = !_pulseOn;
            PulseActiveDot();
        };

        // Capability badges track whatever model each slot currently shows.
        // Additive handlers — the named SelectionChanged handlers still run.
        CbBossModel.SelectionChanged       += (_, _) => UpdateCapabilityBadges();
        CbWorkerModel.SelectionChanged     += (_, _) => UpdateCapabilityBadges();
        CbResearcherModel.SelectionChanged += (_, _) => UpdateCapabilityBadges();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        TbSlots.Text = $"{slots} slot{(slots == 1 ? "" : "s")}";
        UpdateSlotButtons(slots);
        RefreshGate();
        UpdateCapabilityBadges();
        DiagramGrid.SizeChanged += (_, _) => DrawConnectionLines();

        // Ctrl+0 → reset stream font size
        KeyDown += (_, ke) =>
        {
            if (ke.Key == System.Windows.Input.Key.D0 &&
                (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                _streamFontSize = 12;
                ApplyStreamFontSize();
                ke.Handled = true;
            }
        };
    }

    // ── Stream zoom ──────────────────────────────────────────────────────────

    private void OnStreamWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
            return; // let the ScrollViewer handle normal scroll

        e.Handled = true; // prevent the ScrollViewer from scrolling

        // Each wheel notch is ±120; step by 1pt per notch
        var delta = e.Delta > 0 ? 1.0 : -1.0;
        _streamFontSize = Math.Clamp(_streamFontSize + delta, 8, 28);
        ApplyStreamFontSize();
    }

    private void ApplyStreamFontSize()
    {
        TbStream.FontSize          = _streamFontSize;
        TbStreamFontSize.Text      = $"{_streamFontSize:0}pt";
        TbStreamFontSize.Foreground = _streamFontSize == 12
            ? new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))  // default — dim
            : new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));  // non-default — green highlight
    }

    // ── Public API (called by MainWindow) ─────────────────────────────────────

    public void Refresh()
    {
        TbModelName.Text = string.IsNullOrEmpty(ActiveModel) ? "—" : ActiveModel;
        RefreshSlotLabel();
        RefreshGate();
    }

    // ── Metrics history (ConfigStats per configuration) ──────────────────────

    /// <summary>
    /// Lazily renders past-run stats per (boss|coder|researcher) configuration
    /// when the expander opens: runs, success, tester pass, avg duration,
    /// composite quality. Best configuration is highlighted. Pure store reads.
    /// </summary>
    private async void ExpMetricsHistory_Expanded(object sender, RoutedEventArgs e)
    {
        PnlMetricsHistory.Children.Clear();

        // Store read is file IO — keep it off the UI thread (codex finding)
        var stats = await Task.Run(() =>
            Services.Swarm.SwarmMetricsStore.GetConfigStats(minRuns: 1)
                .OrderByDescending(s => s.QualityScore)
                .ToList());

        if (stats.Count == 0)
        {
            PnlMetricsHistory.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No swarm runs recorded yet — stats appear after the first completed run.",
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            });
            return;
        }

        void Row(string text, string hex, bool bold = false)
            => PnlMetricsHistory.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = text, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                Margin = new Thickness(0, 1, 0, 1),
            });

        Row($"{"CONFIG (boss · coder · researcher)",-52} {"RUNS",4} {"OK",5} {"TESTER",6} {"AVG",6} {"QUAL",5}", "#76B900", bold: true);
        foreach (var (s, i) in stats.Select((s, i) => (s, i)))
        {
            string cfg = $"{Short(s.BossModel)} · {Short(s.CoderModel)} · {Short(s.ResearcherModel)}";
            if (cfg.Length > 52) cfg = cfg[..51] + "…";
            string line = $"{cfg,-52} {s.RunCount,4} {s.SuccessRate,5:P0} {s.TesterPassRate,6:P0} " +
                          $"{TimeSpan.FromSeconds(s.AvgDurationSeconds),6:m\\:ss} {s.QualityScore,5:F2}";
            Row(line, i == 0 ? "#4EC94E" : "#CCCCCC", bold: i == 0);
        }

        Row($"{stats.Sum(s => s.RunCount)} total runs · best configuration highlighted · " +
            $"data: {Services.Swarm.SwarmMetricsStore.MetricsFilePath}", "#666666");

        static string Short(string m) => m.Split(':')[0];
    }

    // ── GOBLIN MIND capability badges ─────────────────────────────────────────

    /// <summary>
    /// Refreshes the Format | Categories | Schema | Last-Probed badge line under
    /// each model picker from the persisted ToolCallProfileStore. Pure reads —
    /// never triggers a probe.
    /// </summary>
    private void UpdateCapabilityBadges()
    {
        SetBadge(TbBossBadges,       CbBossModel.SelectedItem as string);
        SetBadge(TbWorkerBadges,     CbWorkerModel.SelectedItem as string);
        SetBadge(TbResearcherBadges, CbResearcherModel.SelectedItem as string);

        static void SetBadge(System.Windows.Controls.TextBlock tb, string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                tb.Text = "";
                return;
            }

            var profile = Services.ToolCalls.ToolCallProfileStore.Load(model);
            if (profile is null)
            {
                tb.Text = "⚪ capabilities unknown — never probed";
                tb.ToolTip = "Run Probe Now to fingerprint this model's tool-call format and task categories.";
                return;
            }

            var parts = new List<string> { $"⚙ {profile.RecommendedMode}" };
            if (profile.FormatProfile is { } fmt)
                parts.Add($"fmt:{fmt.PreferredFormat}");
            if (profile.CategoryProfile is { } cats)
                parts.Add($"cats:{cats.ShortSummary}");
            if (profile.Simplification is not null)
                parts.Add("schema:reduced");

            int days = (int)profile.Age.TotalDays;
            string age = days == 0 ? "today" : $"{days}d ago";
            parts.Add(profile.IsStale ? $"⚠ probed {age} (stale)" : $"probed {age}");

            tb.Text = string.Join("  ·  ", parts);
            tb.ToolTip = profile.Summary;
        }
    }

    private void BtnProbeNow_Click(object sender, RoutedEventArgs e)
    {
        if (Settings is null)
        {
            StatusChanged?.Invoke("Probe window unavailable — settings not wired.");
            return;
        }

        var win = new Tests.ToolCallTestWindow(Settings)
        {
            Owner = Window.GetWindow(this),
        };
        // Refresh badges when the probe window closes — new results may exist
        win.Closed += (_, _) => UpdateCapabilityBadges();
        win.Show();
    }

    // ── Gate logic ────────────────────────────────────────────────────────────

    // ── Minimum capability thresholds for swarm roles ─────────────────────────
    // These are the floor scores below which a model is too unreliable for its role.
    // Based on observed swarm behaviour: BossScore < 5 means the decomposition is
    // too weak to route tasks correctly; CoderScore < 4 means write_file reliability
    // is too low to complete real tasks.
    private const int MinBossScore   = 5;
    private const int MinCoderScore  = 4;

    private bool IsCapable(out string reason)
    {
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        if (slots < 3)
        {
            reason = $"Swarm requires OLLAMA_NUM_PARALLEL ≥ 3 (currently {slots}).\nUse the slot picker below to set it, then restart Ollama.";
            return false;
        }

        // Boss model check — uses GOBLIN MIND profile, not model name
        if (!string.IsNullOrWhiteSpace(ActiveModel))
        {
            var bossProfile = ModelProfiles.Get(ActiveModel);

            if (bossProfile.BossScore < MinBossScore)
            {
                // Build a dynamic suggestion from installed models with good BossScore
                var goodBoss = ModelProfiles.All
                    .Where(kv => kv.Value.BossScore >= MinBossScore)
                    .OrderByDescending(kv => kv.Value.BossScore)
                    .Take(3)
                    .Select(kv => kv.Key)
                    .ToList();
                var suggestion = goodBoss.Count > 0
                    ? $"(e.g. {string.Join(", ", goodBoss)})"
                    : "choose a model with BossScore ≥ 5";
                reason = $"Boss model '{ActiveModel}' has a BossScore of {bossProfile.BossScore}/10 " +
                         $"(minimum {MinBossScore}). It will under-plan tasks.\n" +
                         $"Choose a model with stronger planning capability as boss {suggestion}.\n" +
                         $"Note: {ActiveModel} works well as a Coder or Researcher under a capable boss.";
                return false;
            }

            // If a live GOBLIN MIND probe exists, also check the PLAN category
            var liveMap = ToolCallProfileStore.GetCategoryMap(ActiveModel);
            if (liveMap is not null && liveMap.Categories.Count > 0)
            {
                if (liveMap.Categories.TryGetValue("PLAN", out var planScore) &&
                    planScore.Result == OrchestratorIDE.Services.ToolCalls.CategoryResult.Fail)
                {
                    reason = $"Boss model '{ActiveModel}' failed the PLAN category in the last GOBLIN MIND probe.\n" +
                             $"Run 'tool-probe categories --model {ActiveModel}' to re-check, or pick a different boss.";
                    return false;
                }
            }
        }

        // Coder/worker model check
        var workerModel = string.IsNullOrWhiteSpace(_workerModel) ? ActiveModel : _workerModel;
        if (!string.IsNullOrWhiteSpace(workerModel))
        {
            var coderProfile = ModelProfiles.Get(workerModel);
            if (coderProfile.CoderScore < MinCoderScore)
            {
                reason = $"Coder model '{workerModel}' has a CoderScore of {coderProfile.CoderScore}/10 " +
                         $"(minimum {MinCoderScore}).\nChoose a model with stronger coding capability.";
                return false;
            }
        }

        reason = "";
        return true;
    }

    private bool HasWorkspace()
        => !string.IsNullOrWhiteSpace(WorkspaceRoot);

    private void RefreshGate()
    {
        // Workspace banner — independent of model/slot gate
        BdrWorkspaceWarn.Visibility = HasWorkspace()
            ? Visibility.Collapsed
            : Visibility.Visible;

        var capable = IsCapable(out var reason);
        BdrGateWarn.Visibility = capable ? Visibility.Collapsed : Visibility.Visible;
        TbGateWarn.Text        = reason;
        TbModelName.Text       = string.IsNullOrEmpty(ActiveModel) ? "—" : ActiveModel;

        // Launch requires BOTH workspace AND model/slot capability
        BtnLaunch.IsEnabled = HasWorkspace() && capable;
    }

    private void RefreshSlotLabel()
    {
        var slots    = OllamaParallelHelper.DetectCurrentSlots();
        TbSlots.Text = $"{slots} slot{(slots == 1 ? "" : "s")}";
        if (IsLoaded) UpdateSlotButtons(slots);
    }

    // ── Slot selector (idle page) ─────────────────────────────────────────────

    /// <summary>
    /// Highlights the button matching <paramref name="activeSlots"/> and dims the rest.
    /// Called on load and after every slot change.
    /// </summary>
    private void UpdateSlotButtons(int activeSlots)
    {
        var buttons = new[] { (BtnSlot1, 1), (BtnSlot2, 2), (BtnSlot3, 3), (BtnSlot4, 4) };
        foreach (var (btn, n) in buttons)
        {
            bool isActive = n == activeSlots;
            btn.Background   = new SolidColorBrush(isActive
                ? Color.FromRgb(0x1F, 0x3D, 0x00)   // active: dark green
                : Color.FromRgb(0x1A, 0x1A, 0x1A));  // inactive: near-black
            btn.Foreground   = new SolidColorBrush(isActive
                ? Color.FromRgb(0x76, 0xB9, 0x00)   // active: NVIDIA green
                : Color.FromRgb(0x88, 0x88, 0x88));  // inactive: muted grey
            btn.BorderBrush  = new SolidColorBrush(isActive
                ? Color.FromRgb(0x76, 0xB9, 0x00)
                : Color.FromRgb(0x33, 0x33, 0x33));
        }
    }

    private void CbBossModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = CbBossModel.SelectedItem as string;
        if (!string.IsNullOrEmpty(model))
        {
            ActiveModel = model;
            BossModelChanged?.Invoke(model);
            RefreshGate();
        }
    }

    private void CbWorkerModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = CbWorkerModel.SelectedItem as string;
        if (!string.IsNullOrEmpty(model))
        {
            _workerModel = model;
            WorkerModelChanged?.Invoke(model);
            RefreshGate();
        }
    }

    private void CbResearcherModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = CbResearcherModel.SelectedItem as string;
        if (!string.IsNullOrEmpty(model))
        {
            _researcherModel = model;
            ResearcherModelChanged?.Invoke(model);
        }
    }

    private void BtnSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!int.TryParse(tag, out var n) || n < 1 || n > 4) return;

        OllamaParallelHelper.SetPermanently(n);

        // Refresh slot label in the header and selection highlight
        RefreshSlotLabel();

        // Refresh gate (may now be satisfied if user picked ≥ 3)
        RefreshGate();

        // Show the restart bar with the Restart Ollama button
        BtnRestartOllama.IsEnabled = true;
        BtnRestartOllama.Content   = "↻  Restart Ollama";
        PnlSlotRestart.Visibility  = Visibility.Visible;
    }

    private async void BtnRestartOllama_Click(object sender, RoutedEventArgs e)
    {
        BtnRestartOllama.IsEnabled = false;
        BtnRestartOllama.Content   = "Restarting…";

        await Task.Run(() =>
        {
            // Kill any running ollama processes
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("ollama"))
            {
                try { proc.Kill(); proc.WaitForExit(2000); } catch { /* ignore */ }
            }

            System.Threading.Thread.Sleep(1200);

            // Relaunch ollama serve in the background (inherits the updated user env var)
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "ollama",
                    Arguments              = "serve",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                });
            }
            catch
            {
                // ollama not in PATH — fall back to shell execution so Windows can find it
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = "ollama",
                        Arguments       = "serve",
                        UseShellExecute = true,
                    });
                }
                catch { /* give up gracefully */ }
            }
        });

        // Give the server ~2 s to come up before refreshing the gate
        await Task.Delay(2000);

        BtnRestartOllama.Content = "✓  Done";

        RefreshSlotLabel();
        RefreshGate();

        // Auto-hide the bar after a short confirmation delay
        await Task.Delay(1800);
        PnlSlotRestart.Visibility = Visibility.Collapsed;
    }

    // ── Workspace banner ──────────────────────────────────────────────────────

    private void BtnOpenWorkspace_Click(object sender, RoutedEventArgs e)
        => WorkspaceChangeRequested?.Invoke();

    // ── Auto-Config ───────────────────────────────────────────────────────────

    private SwarmModelConfig? _lastAutoConfig;

    private async void BtnAutoConfig_Click(object sender, RoutedEventArgs e)
    {
        BtnAutoConfig.IsEnabled = false;
        TbHardwareSummary.Text = "⏳ Detecting hardware…";
        TbHardwareSummary.Visibility = Visibility.Visible;
        BdrAutoConfigResult.Visibility = Visibility.Collapsed;
        BtnApplyAutoConfig.Visibility  = Visibility.Collapsed;

        try
        {
            var hw = await SwarmConfigAdvisor.DetectHardwareAsync();

            TbHardwareSummary.Text = hw.HasNvidiaSmi
                ? $"🖥 {hw.Summary}"
                : "⚠ nvidia-smi not found — using CPU-only profile";

            // Collect installed model names from the combo boxes (already populated)
            var models = new List<string>();
            foreach (var item in CbBossModel.Items) if (item is string s) models.Add(s);
            // Deduplicate (all three combos have same list)
            models = models.Distinct().ToList();

            if (models.Count == 0)
            {
                TbHardwareSummary.Text += "  |  No models loaded — pull a model first.";
                return;
            }

            var cfg = SwarmConfigAdvisor.Recommend(hw, models);
            _lastAutoConfig = cfg;

            if (cfg.IsEmpty)
            {
                TbHardwareSummary.Text += "  |  Could not find a suitable model config.";
                return;
            }

            // Source tag
            TbAutoConfigSource.Text = cfg.Source switch
            {
                ConfigSource.ObservedBest    => "🏅 Based on observed run history",
                ConfigSource.BenchmarkBased  => "📊 Based on benchmark scores",
                _                            => "⚙ Fallback minimal config",
            };
            TbAutoConfigTier.Text      = cfg.TierLabel;
            TbAutoConfigReasoning.Text = cfg.Reasoning;
            TbAutoConfigBoss.Text      = cfg.BossModel;
            TbAutoConfigCoder.Text     = cfg.CoderModel;
            TbAutoConfigResearcher.Text = cfg.ResearcherModel;
            TbAutoConfigTester.Text    = cfg.TesterModel;

            BdrAutoConfigResult.Visibility = Visibility.Visible;
            BtnApplyAutoConfig.Visibility  = Visibility.Visible;
        }
        finally
        {
            BtnAutoConfig.IsEnabled = true;
        }
    }

    private void BtnApplyAutoConfig_Click(object sender, RoutedEventArgs e)
    {
        if (_lastAutoConfig is null || _lastAutoConfig.IsEmpty) return;

        SelectComboItem(CbBossModel,       _lastAutoConfig.BossModel);
        SelectComboItem(CbWorkerModel,     _lastAutoConfig.CoderModel);
        SelectComboItem(CbResearcherModel, _lastAutoConfig.ResearcherModel);

        // Fire events so MainWindow updates its stored model strings
        BossModelChanged?.Invoke(_lastAutoConfig.BossModel);
        WorkerModelChanged?.Invoke(_lastAutoConfig.CoderModel);
        ResearcherModelChanged?.Invoke(_lastAutoConfig.ResearcherModel);

        BtnApplyAutoConfig.Content   = "✓  Applied";
        BtnApplyAutoConfig.IsEnabled = false;
    }

    private static void SelectComboItem(ComboBox cb, string value)
    {
        for (int i = 0; i < cb.Items.Count; i++)
        {
            if (cb.Items[i] is string s && s == value)
            {
                cb.SelectedIndex = i;
                return;
            }
        }
    }

    // ── Launch ────────────────────────────────────────────────────────────────

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        // Guard: workspace must be set before the swarm can write any files
        if (!HasWorkspace())
        {
            BdrWorkspaceWarn.Visibility = Visibility.Visible;
            return;
        }
        if (Ollama is null || !IsCapable(out _)) return;
        var goal = TbGoal.Text.Trim();
        if (string.IsNullOrWhiteSpace(goal)) return;

        StartSwarm(goal);
    }

    private void StartSwarm(string goal)
    {
        _lastRunGoal = goal;

        // Reset all stream / thinking buffers
        foreach (var key in _streams.Keys.ToList())
        {
            _streams[key]      = "";
            _thinkStreams[key] = "";
            _rawPending[key]   = "";
            _inThink[key]      = false;
        }
        _taskTabMap.Clear();

        // Reset thinking pane
        TbThinking.Text           = "";
        TbStream.Text             = "";
        TbThinkCount.Text         = "";
        _thinkVisible             = false;
        BdrThinking.Visibility    = Visibility.Collapsed;
        BdrDirective.Visibility   = Visibility.Collapsed;
        TaskCardPanel.Children.Clear();

        // Clear agent activity columns
        LogBoss.Children.Clear();
        LogResearcher.Children.Clear();
        LogCoder.Children.Clear();
        LogUIDev.Children.Clear();
        LogTester.Children.Clear();

        // Reset node states
        SetNodeIdle(NodeBoss);
        SetNodeIdle(NodeResearcher);
        SetNodeIdle(NodeCoder);
        SetNodeUIDev(NodeUIDev);
        SetNodeIdle(NodeTester);
        TbBossStatus.Text       = "Orchestrating…";
        TbResearcherStatus.Text = "idle";
        TbCoderStatus.Text      = "idle";
        TbUIDevStatus.Text      = "idle";
        TbTesterStatus.Text     = "idle";
        IcoResearcherThink.Visibility = Visibility.Collapsed;
        IcoCoderThink.Visibility      = Visibility.Collapsed;
        IcoUIDevThink.Visibility      = Visibility.Collapsed;
        IcoTesterThink.Visibility     = Visibility.Collapsed;

        // Reset tabs
        TabResearcher.Visibility = Visibility.Collapsed;
        TabCoder.Visibility      = Visibility.Collapsed;
        TabUIDev.Visibility      = Visibility.Collapsed;
        TabTester.Visibility     = Visibility.Collapsed;
        SelectTab("boss");

        // Show active board
        PnlIdle.Visibility            = Visibility.Collapsed;
        PnlActive.Visibility          = Visibility.Visible;
        BtnStopSwarm.Visibility       = Visibility.Visible;
        BtnLaunchProject.Visibility   = Visibility.Collapsed;
        _lastOutputProjectDir         = "";
        BdrDirective.Visibility       = Visibility.Visible;
        TbDirective.Text        = "";
        TbActiveGoal.Text       = $"Goal: {goal}";

        // Header: ACTIVE state
        SetHeaderActive(true);
        _pulseTimer.Start();

        // Boss = ActiveModel, Coder/UIDev = _workerModel, Researcher = _researcherModel
        var coderModel      = string.IsNullOrWhiteSpace(_workerModel)     ? ActiveModel    : _workerModel;
        var researcherModel = string.IsNullOrWhiteSpace(_researcherModel) ? coderModel     : _researcherModel;
        // HIVE MIND: route the swarm to the chosen node. A remote node gets a
        // fresh OllamaClient pointed at its URL; This PC keeps the injected one.
        var swarmOllama = _runOnUrl is null ? Ollama! : new OllamaClient(_runOnUrl);
        if (_runOnUrl is not null)
            OnActivity?.Invoke($"🐝 Running this swarm on {_runOnName} ({_runOnUrl})");
        _session = new SwarmSession(swarmOllama, ActiveModel, WorkspaceRoot, coderModel, researcherModel);

        // HIVE MIND Phase B: probe alive nodes and wire per-task routing.
        // Only runs when _runOnUrl is null (whole-swarm routing and per-task routing
        // are mutually exclusive — if the user picked a specific node, use that).
        if (_runOnUrl is null)
        {
            var localUrl = LocalUrl ?? "http://localhost:11434";
            var hiveHosts = Services.Hive.HiveHosts.Load(localUrl);
            var remoteHosts = hiveHosts.Where(h => h.Name != "This PC").ToList();
            if (remoteHosts.Count > 0)
            {
                // Probe remote nodes asynchronously — don't block swarm start.
                _ = Task.Run(async () =>
                {
                    await Task.WhenAll(hiveHosts.Select(h => Services.Hive.HiveHosts.ProbeAsync(h, 2)));
                    await Task.WhenAll(hiveHosts
                        .Where(h => h.Reachable == true)
                        .Select(h => Services.Hive.HiveHosts.ProbeHiveApiAsync(h)));
                    var alive = hiveHosts.Count(h => h.Reachable == true && h.Name != "This PC");
                    if (alive > 0)
                    {
                        _session.SetHiveHosts(hiveHosts, localUrl);
                        _ = Dispatcher.InvokeAsync(() =>
                            OnActivity?.Invoke($"🐝 HIVE MIND: {alive} remote node(s) online — per-task routing enabled"));
                    }
                });
            }
        }

        // HIVE MIND Phase 3: attach distributed task queue if running as Warchief
        if (HiveTaskQueue?.IsListening == true)
        {
            _session.SetDistributedQueue(HiveTaskQueue);
            OnActivity?.Invoke($"🐝 HIVE MIND distributed mode — Warchief task queue active on {HiveTaskQueue.BaseUrl}");
        }

        _session.OnBossToken     += OnBossToken;
        _session.OnWorkerToken   += OnWorkerToken;
        _session.OnTasksPlanned  += OnTasksPlanned;
        _session.OnTaskChanged   += OnTaskChanged;
        _session.OnSwarmComplete += OnSwarmComplete;
        _session.OnError         += OnError;
        _session.OnStopped       += () => Dispatcher.InvokeAsync(OnSwarmStopped);
        _session.OnActivity      += (agentKey, msg) => Dispatcher.InvokeAsync(() =>
        {
            AddAgentLog(agentKey, msg);
            OnActivity?.Invoke(msg);
        });
        _session.OnStagingReady  += (runId, stagingDir, files) =>
            Dispatcher.InvokeAsync(() => OnStagingReady(runId, stagingDir, files));

        _ = _session.RunAsync(goal);
        StatusChanged?.Invoke("Swarm active");
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    private void BtnStopSwarm_Click(object sender, RoutedEventArgs e)
    {
        _session?.Stop();
    }

    // ── Directive steering ────────────────────────────────────────────────────

    private void BtnSendDirective_Click(object sender, RoutedEventArgs e) => SendDirective();

    private void TbDirective_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            SendDirective();
        }
    }

    private void SendDirective()
    {
        var text = TbDirective.Text.Trim();
        if (string.IsNullOrWhiteSpace(text) || _session is null) return;
        _session.InjectDirective(text);
        TbDirective.Clear();
        // Switch to Boss tab so the user can see the directive echoed
        SelectTab("boss");
    }

    // ── Thinking toggle ───────────────────────────────────────────────────────

    private void BtnThinkToggle_Click(object sender, RoutedEventArgs e)
    {
        _thinkVisible = !_thinkVisible;
        BdrThinking.Visibility = _thinkVisible ? Visibility.Visible : Visibility.Collapsed;
        TbThinkLabel.Foreground = new SolidColorBrush(_thinkVisible
            ? Color.FromRgb(0x76, 0xB9, 0x00)
            : Color.FromRgb(0x44, 0x44, 0x44));
    }

    private void OnSwarmStopped()
    {
        _pulseTimer.Stop();
        SetHeaderActive(false);
        BtnStopSwarm.Visibility = Visibility.Collapsed;
        BtnNewRun.Visibility    = Visibility.Visible;
        SetNodeIdle(NodeBoss);
        TbBossStatus.Text = "Done ⬡";
    }

    // ── New Run ───────────────────────────────────────────────────────────────

    private void BtnNewRun_Click(object sender, RoutedEventArgs e)
    {
        // Stop any lingering session
        _session?.Stop();
        _session = null;

        // Clear stream buffers (same pattern as BtnLaunch_Click reset)
        foreach (var key in _streams.Keys.ToList())
        {
            _streams[key]      = "";
            _thinkStreams[key]  = "";
            _rawPending[key]   = "";
        }

        // Clear shared stream display
        TbStream.Text    = "";
        TbThinking.Text  = "";
        TbThinkCount.Text = "";
        _thinkVisible = false;

        // Reset task card strip and directive
        TaskCardPanel.Children.Clear();
        TbDirective.Text  = "";
        TbActiveGoal.Text = "";

        // Reset tabs
        TabResearcher.Visibility = Visibility.Collapsed;
        TabCoder.Visibility      = Visibility.Collapsed;
        TabUIDev.Visibility      = Visibility.Collapsed;
        TabTester.Visibility     = Visibility.Collapsed;
        TabResults.Visibility    = Visibility.Collapsed;
        SelectTab("boss");

        // Reset Launch Pad
        _lastStagingDir  = "";
        _lastStagedFiles = [];
        _lastRunGoal     = "";
        _runCts?.Cancel();
        _runCts = null;
        PnlResultFiles.Children.Clear();
        TbRunOutput.Text       = "";
        BdrRunError.Visibility = Visibility.Collapsed;
        BtnApplyToWorkspace.Content   = "✓  Apply to Workspace";
        BtnApplyToWorkspace.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        BtnRunProject.IsEnabled = true;

        // Reset node status labels
        TbBossStatus.Text       = "idle";
        TbResearcherStatus.Text = "idle";
        TbCoderStatus.Text      = "idle";
        TbUIDevStatus.Text      = "idle";
        TbTesterStatus.Text     = "idle";
        SetNodeIdle(NodeBoss);
        SetNodeIdle(NodeResearcher);
        SetNodeIdle(NodeCoder);
        SetNodeIdle(NodeUIDev);
        SetNodeIdle(NodeTester);

        // Hide active panel, show setup panel
        BtnNewRun.Visibility         = Visibility.Collapsed;
        BtnLaunchProject.Visibility  = Visibility.Collapsed;
        BtnStopSwarm.Visibility      = Visibility.Collapsed;
        BdrDirective.Visibility      = Visibility.Collapsed;
        PnlActive.Visibility         = Visibility.Collapsed;
        PnlIdle.Visibility           = Visibility.Visible;

        _lastOutputProjectDir = "";
        _pulseTimer.Stop();
        SetHeaderActive(false);
        StatusChanged?.Invoke("Swarm ready");
    }

    // ── SwarmSession event handlers ───────────────────────────────────────────

    // ── Thinking tag parser ───────────────────────────────────────────────────

    /// <summary>
    /// Incrementally parses tokens into thinking vs. output buckets.
    /// Handles &lt;think&gt;…&lt;/think&gt; blocks split across multiple tokens.
    /// </summary>
    private void ParseToken(string tabKey, string newToken)
    {
        _rawPending[tabKey] += newToken;
        var raw = _rawPending[tabKey];

        while (true)
        {
            if (_inThink[tabKey])
            {
                var closeIdx = raw.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (closeIdx >= 0)
                {
                    _thinkStreams[tabKey] += raw[..closeIdx];
                    raw = raw[(closeIdx + 8)..];  // len("</think>") == 8
                    _inThink[tabKey] = false;
                }
                else
                {
                    // Keep the last 8 chars in the pending buffer in case
                    // the close tag is split across the next token.
                    if (raw.Length > 8)
                    {
                        _thinkStreams[tabKey] += raw[..^8];
                        raw = raw[^8..];
                    }
                    break;
                }
            }
            else
            {
                var openIdx = raw.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (openIdx >= 0)
                {
                    _streams[tabKey] += raw[..openIdx];
                    raw = raw[(openIdx + 7)..];  // len("<think>") == 7
                    _inThink[tabKey] = true;
                }
                else
                {
                    // Keep the last 7 chars pending in case the open tag is split.
                    if (raw.Length > 7)
                    {
                        _streams[tabKey] += raw[..^7];
                        raw = raw[^7..];
                    }
                    break;
                }
            }
        }
        _rawPending[tabKey] = raw;
    }

    private void RefreshStreamDisplay(string tabKey)
    {
        TbStream.Text   = _streams[tabKey];
        TbThinking.Text = _thinkStreams[tabKey];

        var thinkLen = _thinkStreams[tabKey].Length;
        TbThinkCount.Text = thinkLen > 0 ? $"({thinkLen / 4:N0} tok)" : "";
        TbThinkLabel.Foreground = new SolidColorBrush(
            thinkLen > 0 ? Color.FromRgb(0x55, 0x88, 0x55) : Color.FromRgb(0x44, 0x44, 0x44));

        StreamScroll.ScrollToBottom();
    }

    // ── Token events ──────────────────────────────────────────────────────────

    private void OnBossToken(string token)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ParseToken("boss", token);
            if (_activeTab == "boss") RefreshStreamDisplay("boss");
        });
    }

    private void OnWorkerToken(string taskId, string token)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_taskTabMap.TryGetValue(taskId, out var tabKey)) return;
            ParseToken(tabKey, token);
            if (_activeTab == tabKey) RefreshStreamDisplay(tabKey);
        });
    }

    private void OnTasksPlanned(List<SwarmTask> tasks)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TbBossStatus.Text = $"Dispatched {tasks.Count}";

            foreach (var task in tasks)
            {
                // Map task ID to tab key
                var tabKey = task.Role switch
                {
                    SwarmWorkerRole.Researcher  => "researcher",
                    SwarmWorkerRole.Coder       => "coder",
                    SwarmWorkerRole.UIDeveloper => "uidev",
                    SwarmWorkerRole.Tester      => "tester",
                    _                           => "coder"
                };
                _taskTabMap[task.Id] = tabKey;

                // Show relevant tab button
                switch (task.Role)
                {
                    case SwarmWorkerRole.Researcher:  TabResearcher.Visibility = Visibility.Visible; break;
                    case SwarmWorkerRole.Coder:       TabCoder.Visibility      = Visibility.Visible; break;
                    case SwarmWorkerRole.UIDeveloper: TabUIDev.Visibility      = Visibility.Visible; break;
                    case SwarmWorkerRole.Tester:      TabTester.Visibility     = Visibility.Visible; break;
                }

                // Add task card to bottom strip
                TaskCardPanel.Children.Add(BuildTaskCard(task));
            }

            DrawConnectionLines();
        });
    }

    private void OnTaskChanged(SwarmTask task)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // Update worker node visual
            var (node, tbStatus, icoThink, coWorkPanel, tbLabel, tbQuestion, wpOptions, txtInput, btnSend, btnSteer)
                = task.Role switch
            {
                SwarmWorkerRole.Researcher  => (NodeResearcher,  TbResearcherStatus,  IcoResearcherThink,
                                                CoWorkResearcher, TbCoWorkLabelResearcher, TbQuestionResearcher,
                                                WpOptionsResearcher, TxtCoWorkResearcher, BtnCoWorkSendResearcher,
                                                BtnSteerResearcher),
                SwarmWorkerRole.Coder       => (NodeCoder,  TbCoderStatus,  IcoCoderThink,
                                                CoWorkCoder, TbCoWorkLabelCoder, TbQuestionCoder,
                                                WpOptionsCoder, TxtCoWorkCoder, BtnCoWorkSendCoder,
                                                BtnSteerCoder),
                SwarmWorkerRole.UIDeveloper => (NodeUIDev,  TbUIDevStatus,  IcoUIDevThink,
                                                CoWorkUIDev, TbCoWorkLabelUIDev, TbQuestionUIDev,
                                                WpOptionsUIDev, TxtCoWorkUIDev, BtnCoWorkSendUIDev,
                                                BtnSteerUIDev),
                SwarmWorkerRole.Tester      => (NodeTester, TbTesterStatus, IcoTesterThink,
                                                CoWorkTester, TbCoWorkLabelTester, TbQuestionTester,
                                                WpOptionsTester, TxtCoWorkTester, BtnCoWorkSendTester,
                                                BtnSteerTester),
                _ => default
            };

            if (node is null) return;
            UpdateWorkerNode(node, tbStatus, icoThink, task);
            UpdateCoWorkPanel(task, coWorkPanel, tbLabel, tbQuestion, wpOptions, txtInput, btnSend, btnSteer);

            // Refresh task card in bottom strip (find by tag)
            foreach (Border card in TaskCardPanel.Children)
            {
                if (card.Tag as string == task.Id)
                {
                    RefreshTaskCard(card, task);
                    break;
                }
            }
        });
    }

    // ── Co-Work UI helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Updates the per-column co-work panel to reflect the current task state:
    /// • WaitingForUser  → amber banner with question + option chips + reply input
    /// • InProgress      → hidden panel + visible steer button
    /// • Done            → blue "follow up" input
    /// • Pending/Error   → hidden
    /// </summary>
    private void UpdateCoWorkPanel(
        SwarmTask task,
        Border coWorkPanel, TextBlock tbLabel, TextBlock tbQuestion,
        WrapPanel wpOptions, TextBox txtInput, Button btnSend, Button btnSteer)
    {
        var roleColor = task.RoleColor; // hex string like "#4A9FD9"
        var roleBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                            .ConvertFromString(roleColor)!;

        switch (task.Status)
        {
            case SwarmTaskStatus.WaitingForUser:
                // Amber ask-user banner
                coWorkPanel.Visibility      = Visibility.Visible;
                coWorkPanel.Background      = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0x1A, 0x17, 0x00));
                coWorkPanel.BorderBrush     = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0xF0, 0xC0, 0x60));
                tbLabel.Text                = $"⏸ {task.RoleLabel} IS ASKING:";
                tbLabel.Foreground          = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0xF0, 0xC0, 0x60));
                btnSend.Background          = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0x2A, 0x27, 0x00));
                btnSend.Foreground          = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0xF0, 0xC0, 0x60));
                btnSend.BorderBrush         = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0xF0, 0xC0, 0x60));
                tbQuestion.Text             = task.PendingQuestion ?? "";
                tbQuestion.Visibility       = Visibility.Visible;
                tbQuestion.Foreground       = System.Windows.Media.Brushes.LightYellow;

                // Build option chips
                wpOptions.Children.Clear();
                if (task.PendingOptions is { Count: > 0 } opts)
                {
                    foreach (var opt in opts)
                    {
                        var chip = new Button
                        {
                            Content         = opt,
                            Margin          = new Thickness(0, 0, 6, 4),
                            Padding         = new Thickness(8, 3, 8, 3),
                            Background      = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0x2A, 0x27, 0x00)),
                            Foreground      = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0xF0, 0xC0, 0x60)),
                            BorderBrush     = new System.Windows.Media.SolidColorBrush(
                                                System.Windows.Media.Color.FromRgb(0x80, 0x70, 0x00)),
                            FontFamily      = new System.Windows.Media.FontFamily("Consolas"),
                            FontSize        = 10,
                            Cursor          = System.Windows.Input.Cursors.Hand,
                            Tag             = txtInput, // so the chip can fill the text box
                        };
                        chip.Click += (_, _) => { if (chip.Tag is TextBox tb) tb.Text = opt; };
                        wpOptions.Children.Add(chip);
                    }
                    wpOptions.Visibility = Visibility.Visible;
                }
                else
                {
                    wpOptions.Visibility = Visibility.Collapsed;
                }

                btnSteer.Visibility = Visibility.Collapsed;
                txtInput.Clear();
                txtInput.Focus();
                break;

            case SwarmTaskStatus.InProgress:
                coWorkPanel.Visibility  = Visibility.Collapsed;
                btnSteer.Visibility     = Visibility.Visible;
                break;

            case SwarmTaskStatus.Done:
                // Restore to role color for follow-up chat
                coWorkPanel.Visibility  = Visibility.Visible;
                coWorkPanel.Background  = new System.Windows.Media.SolidColorBrush(
                                            System.Windows.Media.Color.FromRgb(0x06, 0x08, 0x10));
                coWorkPanel.BorderBrush = roleBrush;
                tbLabel.Text            = $"💬 Ask {task.RoleLabel} a follow-up:";
                tbLabel.Foreground      = roleBrush;
                btnSend.Background      = new System.Windows.Media.SolidColorBrush(
                                            System.Windows.Media.Color.FromRgb(0x0A, 0x12, 0x1A));
                btnSend.Foreground      = roleBrush;
                btnSend.BorderBrush     = roleBrush;
                tbQuestion.Visibility   = Visibility.Collapsed;
                wpOptions.Visibility    = Visibility.Collapsed;
                btnSteer.Visibility     = Visibility.Collapsed;
                txtInput.Clear();
                break;

            default:
                coWorkPanel.Visibility  = Visibility.Collapsed;
                btnSteer.Visibility     = Visibility.Collapsed;
                break;
        }
    }

    // ── Co-Work event handlers ────────────────────────────────────────────────

    // Steer buttons (show while running)
    private void BtnSteerResearcher_Click(object sender, RoutedEventArgs e) => OpenSteerInput(SwarmWorkerRole.Researcher, TxtCoWorkResearcher);
    private void BtnSteerCoder_Click     (object sender, RoutedEventArgs e) => OpenSteerInput(SwarmWorkerRole.Coder,       TxtCoWorkCoder);
    private void BtnSteerUIDev_Click     (object sender, RoutedEventArgs e) => OpenSteerInput(SwarmWorkerRole.UIDeveloper, TxtCoWorkUIDev);
    private void BtnSteerTester_Click    (object sender, RoutedEventArgs e) => OpenSteerInput(SwarmWorkerRole.Tester,      TxtCoWorkTester);

    private void OpenSteerInput(SwarmWorkerRole role, TextBox txtInput)
    {
        var (coWorkPanel, tbLabel, tbQuestion, wpOptions, btnSend, btnSteer, coWorkColor)
            = role switch
        {
            SwarmWorkerRole.Researcher  => (CoWorkResearcher, TbCoWorkLabelResearcher, TbQuestionResearcher,
                                            WpOptionsResearcher, BtnCoWorkSendResearcher, BtnSteerResearcher, "#4A9FD9"),
            SwarmWorkerRole.Coder       => (CoWorkCoder,      TbCoWorkLabelCoder,      TbQuestionCoder,
                                            WpOptionsCoder,      BtnCoWorkSendCoder,      BtnSteerCoder,      "#76B900"),
            SwarmWorkerRole.UIDeveloper => (CoWorkUIDev,      TbCoWorkLabelUIDev,      TbQuestionUIDev,
                                            WpOptionsUIDev,      BtnCoWorkSendUIDev,      BtnSteerUIDev,      "#C586C0"),
            SwarmWorkerRole.Tester      => (CoWorkTester,     TbCoWorkLabelTester,     TbQuestionTester,
                                            WpOptionsTester,     BtnCoWorkSendTester,     BtnSteerTester,     "#F0C060"),
            _ => default
        };

        if (coWorkPanel is null) return;
        var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                        .ConvertFromString(coWorkColor)!;
        coWorkPanel.Visibility  = Visibility.Visible;
        coWorkPanel.Background  = new System.Windows.Media.SolidColorBrush(
                                    System.Windows.Media.Color.FromRgb(0x06, 0x08, 0x10));
        coWorkPanel.BorderBrush = brush;
        tbLabel.Text            = $"📤 Steer {role} (injects on next step):";
        tbLabel.Foreground      = brush;
        btnSend.Foreground      = brush;
        btnSend.BorderBrush     = brush;
        tbQuestion.Visibility   = Visibility.Collapsed;
        wpOptions.Visibility    = Visibility.Collapsed;
        btnSteer.Visibility     = Visibility.Collapsed;
        txtInput.Clear();
        txtInput.Focus();
    }

    // Send buttons
    private void BtnCoWorkSendResearcher_Click(object sender, RoutedEventArgs e) => HandleCoWorkSend(SwarmWorkerRole.Researcher,  TxtCoWorkResearcher);
    private void BtnCoWorkSendCoder_Click     (object sender, RoutedEventArgs e) => HandleCoWorkSend(SwarmWorkerRole.Coder,       TxtCoWorkCoder);
    private void BtnCoWorkSendUIDev_Click     (object sender, RoutedEventArgs e) => HandleCoWorkSend(SwarmWorkerRole.UIDeveloper, TxtCoWorkUIDev);
    private void BtnCoWorkSendTester_Click    (object sender, RoutedEventArgs e) => HandleCoWorkSend(SwarmWorkerRole.Tester,      TxtCoWorkTester);

    // Enter key in any co-work input → send
    private void TxtCoWork_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;
        e.Handled = true;

        var role = sender switch
        {
            TextBox tb when tb == TxtCoWorkResearcher => SwarmWorkerRole.Researcher,
            TextBox tb when tb == TxtCoWorkCoder      => SwarmWorkerRole.Coder,
            TextBox tb when tb == TxtCoWorkUIDev      => SwarmWorkerRole.UIDeveloper,
            TextBox tb when tb == TxtCoWorkTester     => SwarmWorkerRole.Tester,
            _ => (SwarmWorkerRole?)null
        };
        if (role.HasValue)
            HandleCoWorkSend(role.Value, (TextBox)sender);
    }

    /// <summary>
    /// Routes the co-work input depending on task state:
    /// • WaitingForUser  → ProvideWorkerReply
    /// • InProgress      → SteerWorker  (inject on next step)
    /// • Done            → ContinueWorkerAsync  (follow-up conversation)
    /// </summary>
    private void HandleCoWorkSend(SwarmWorkerRole role, TextBox txtInput)
    {
        if (_session is null) return;
        var text = txtInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        var task = _session.Tasks.FirstOrDefault(t => t.Role == role);
        if (task is null) return;

        txtInput.Clear();

        switch (task.Status)
        {
            case SwarmTaskStatus.WaitingForUser:
                _session.ProvideWorkerReply(task.Id, text);
                AddAgentLog(AgentKeyForRole(role), $"💬 You replied: {text}");
                break;

            case SwarmTaskStatus.InProgress:
                _session.SteerWorker(task.Id, text);
                AddAgentLog(AgentKeyForRole(role), $"📤 Steer queued: {text}");
                // Hide the steer input until next state change
                var cwPanel = role switch
                {
                    SwarmWorkerRole.Researcher  => CoWorkResearcher,
                    SwarmWorkerRole.Coder       => CoWorkCoder,
                    SwarmWorkerRole.UIDeveloper => CoWorkUIDev,
                    SwarmWorkerRole.Tester      => CoWorkTester,
                    _                           => null
                };
                if (cwPanel is not null) cwPanel.Visibility = Visibility.Collapsed;
                var steerBtn = role switch
                {
                    SwarmWorkerRole.Researcher  => BtnSteerResearcher,
                    SwarmWorkerRole.Coder       => BtnSteerCoder,
                    SwarmWorkerRole.UIDeveloper => BtnSteerUIDev,
                    SwarmWorkerRole.Tester      => BtnSteerTester,
                    _                           => null
                };
                if (steerBtn is not null) steerBtn.Visibility = Visibility.Visible;
                break;

            case SwarmTaskStatus.Done:
                AddAgentLog(AgentKeyForRole(role), $"💬 You: {text}");
                _ = _session.ContinueWorkerAsync(task.Id, text);
                break;
        }
    }

    private static string AgentKeyForRole(SwarmWorkerRole role) => role switch
    {
        SwarmWorkerRole.Researcher  => "researcher",
        SwarmWorkerRole.Coder       => "coder",
        SwarmWorkerRole.UIDeveloper => "uidev",
        SwarmWorkerRole.Tester      => "tester",
        _                           => "boss"
    };

    private void OnSwarmComplete(string merged)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _streams["boss"] += "\n\n──── SWARM COMPLETE ────\n\n" + merged;
            if (_activeTab == "boss") TbStream.Text = _streams["boss"];
            TbBossStatus.Text = "Delivered ✓";
            SetNodeDone(NodeBoss);
            StatusChanged?.Invoke("Swarm complete");

            // Show launch button and Results tab
            _lastOutputProjectDir = _session?.GetOutputProjectDir() ?? "";
            if (!string.IsNullOrWhiteSpace(_lastOutputProjectDir) &&
                System.IO.Directory.Exists(_lastOutputProjectDir))
            {
                BtnLaunchProject.Visibility = Visibility.Visible;
            }
            TabResults.Visibility = Visibility.Visible;
            SelectTab("results");
        });
    }

    private void OnError(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _streams["boss"] += $"\n\n[ERROR] {message}";
            if (_activeTab == "boss") TbStream.Text = _streams["boss"];
            StatusChanged?.Invoke($"Swarm: {message}");
        });
    }

    private void OnStagingReady(string runId, string stagingDir, IReadOnlyList<string> files)
    {
        // Store for Launch Pad
        _lastStagingDir  = stagingDir;
        _lastStagedFiles = files;

        // Populate Launch Pad file list
        PnlResultFiles.Children.Clear();
        foreach (var f in files.Take(50))
        {
            PnlResultFiles.Children.Add(new TextBlock
            {
                Text         = "  • " + f,
                FontSize     = 10,
                FontFamily   = new FontFamily("Consolas"),
                Foreground   = new SolidColorBrush(Color.FromRgb(0x77, 0xB9, 0x77)),
                Margin       = new Thickness(0, 1, 0, 1),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        if (files.Count > 50)
            PnlResultFiles.Children.Add(new TextBlock
            {
                Text       = $"  … and {files.Count - 50} more",
                FontSize   = 10,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x77, 0x55)),
                Margin     = new Thickness(0, 1, 0, 1),
            });

        // Append a staging summary to the boss stream
        var summary = new System.Text.StringBuilder();
        summary.AppendLine();
        summary.AppendLine("──── STAGED FILES READY FOR REVIEW ────");
        summary.AppendLine($"Run:     {runId}");
        summary.AppendLine($"Staging: {stagingDir}");
        summary.AppendLine($"Files:   {files.Count}");
        summary.AppendLine();
        foreach (var f in files.Take(30))
            summary.AppendLine($"  • {f}");
        if (files.Count > 30)
            summary.AppendLine($"  … and {files.Count - 30} more");
        summary.AppendLine();
        summary.AppendLine("Switch to the ▶ Results tab to run, inspect, and apply the output.");

        _streams["boss"] += summary.ToString();
        if (_activeTab == "boss") TbStream.Text = _streams["boss"];
        StatusChanged?.Invoke($"Swarm staged {files.Count} file(s) — Results tab ready");
    }

    // ── Tab switching ─────────────────────────────────────────────────────────

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            SelectTab(tag);
    }

    private void SelectTab(string key)
    {
        _activeTab = key;

        // Results tab shows the Launch Pad; all others show the stream
        bool isResults = key == "results";
        PnlLaunchPad.Visibility = isResults ? Visibility.Visible  : Visibility.Collapsed;
        StreamScroll.Visibility = isResults ? Visibility.Collapsed : Visibility.Visible;

        if (!isResults) RefreshStreamDisplay(key);

        // Highlight active tab buttons
        foreach (var btn in new[] { TabBoss, TabResearcher, TabCoder, TabUIDev, TabTester })
        {
            var isActive = btn.Tag as string == key;
            btn.BorderBrush = isActive
                ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                : new SolidColorBrush(Colors.Transparent);
            btn.Foreground = isActive
                ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                : (Brush)FindResource("Br.Text.Muted");
        }

        // Results tab stays green-tinted even when not active (to signal results are ready)
        TabResults.BorderBrush = isResults
            ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
            : new SolidColorBrush(Colors.Transparent);
        TabResults.Foreground = isResults
            ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x4A, 0x7A, 0x00));
    }

    // ── Node visual helpers ───────────────────────────────────────────────────

    private static void SetNodeIdle(Border node)
    {
        node.Background   = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x14));
        node.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x2A));
    }

    private static void SetNodeUIDev(Border node) => SetNodeIdle(node);  // same idle style

    private static void SetNodeActive(Border node)
    {
        node.Background   = new SolidColorBrush(Color.FromRgb(0x1F, 0x3D, 0x00));
        node.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
    }

    private static void SetNodeDone(Border node)
    {
        node.Background   = new SolidColorBrush(Color.FromRgb(0x14, 0x2A, 0x14));
        node.BorderBrush  = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E));
    }

    private static void SetNodeError(Border node)
    {
        node.Background   = new SolidColorBrush(Color.FromRgb(0x2A, 0x14, 0x14));
        node.BorderBrush  = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
    }

    private static void UpdateWorkerNode(Border node, TextBlock statusLabel, TextBlock thinkIcon, SwarmTask task)
    {
        switch (task.Status)
        {
            case SwarmTaskStatus.InProgress:
                SetNodeActive(node);
                statusLabel.Text          = "thinking…";
                statusLabel.Foreground    = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
                thinkIcon.Visibility      = Visibility.Visible;
                break;
            case SwarmTaskStatus.Done:
                SetNodeDone(node);
                statusLabel.Text          = "done ✓";
                statusLabel.Foreground    = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E));
                thinkIcon.Visibility      = Visibility.Collapsed;
                break;
            case SwarmTaskStatus.Error:
                SetNodeError(node);
                statusLabel.Text          = "error ✗";
                statusLabel.Foreground    = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                thinkIcon.Visibility      = Visibility.Collapsed;
                break;
            default:
                SetNodeIdle(node);
                statusLabel.Text          = "idle";
                statusLabel.Foreground    = (Brush)node.FindResource("Br.Text.Muted");
                thinkIcon.Visibility      = Visibility.Collapsed;
                break;
        }
    }

    // ── Canvas connection lines ───────────────────────────────────────────────

    private void DrawConnectionLines()
    {
        NodeCanvas.Children.Clear();
        if (!IsLoaded) return;

        Dispatcher.InvokeAsync(DrawLines, DispatcherPriority.Loaded);
    }

    private void DrawLines()
    {
        NodeCanvas.Children.Clear();

        Point BossBottom()
        {
            var tl = NodeBoss.TranslatePoint(new Point(0, 0), NodeCanvas);
            return new Point(tl.X + NodeBoss.ActualWidth / 2, tl.Y + NodeBoss.ActualHeight);
        }

        Point WorkerTop(Border worker)
        {
            var tl = worker.TranslatePoint(new Point(0, 0), NodeCanvas);
            return new Point(tl.X + worker.ActualWidth / 2, tl.Y);
        }

        var lineColor = new SolidColorBrush(Color.FromRgb(0x2A, 0x5A, 0x1A));
        var dashArray = new DoubleCollection { 5, 4 };

        var bossBottom = BossBottom();

        foreach (var worker in new[] { NodeResearcher, NodeCoder, NodeUIDev, NodeTester })
        {
            if (worker.ActualWidth == 0) continue;   // not yet laid out
            var wTop = WorkerTop(worker);

            // Mid-point for bent line
            var midY = (bossBottom.Y + wTop.Y) / 2;

            var line1 = new Line
            {
                X1 = bossBottom.X, Y1 = bossBottom.Y,
                X2 = bossBottom.X, Y2 = midY,
                Stroke = lineColor, StrokeThickness = 1.5,
                StrokeDashArray = dashArray,
            };
            var line2 = new Line
            {
                X1 = bossBottom.X, Y1 = midY,
                X2 = wTop.X,        Y2 = midY,
                Stroke = lineColor, StrokeThickness = 1.5,
                StrokeDashArray = dashArray,
            };
            var line3 = new Line
            {
                X1 = wTop.X, Y1 = midY,
                X2 = wTop.X, Y2 = wTop.Y,
                Stroke = lineColor, StrokeThickness = 1.5,
                StrokeDashArray = dashArray,
            };
            NodeCanvas.Children.Add(line1);
            NodeCanvas.Children.Add(line2);
            NodeCanvas.Children.Add(line3);
        }
    }

    // ── Header status helpers ─────────────────────────────────────────────────

    private void SetHeaderActive(bool active)
    {
        if (active)
        {
            StatusDot.Fill          = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
            TbStatusChip.Text       = "ACTIVE";
            TbStatusChip.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
        }
        else
        {
            StatusDot.Fill          = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            TbStatusChip.Text       = "IDLE";
            TbStatusChip.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
    }

    private void PulseActiveDot()
    {
        // Only animate if session is running
        if (_session is not { IsRunning: true }) return;
        var color = _pulseOn
            ? Color.FromRgb(0x76, 0xB9, 0x00)
            : Color.FromRgb(0x3A, 0x6A, 0x00);
        StatusDot.Fill = new SolidColorBrush(color);
    }

    // ── Task cards ────────────────────────────────────────────────────────────

    private Border BuildTaskCard(SwarmTask task)
    {
        var statusText = new TextBlock
        {
            Text       = $"{task.StatusIcon} {task.Title}",
            FontSize   = 11,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth   = 160,
        };

        var roleLabel = new TextBlock
        {
            Text       = $"{task.RoleIcon} {task.RoleLabel}",
            FontSize   = 9,
            FontFamily = new FontFamily("Segoe UI"),
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(task.RoleColor)),
            Margin     = new Thickness(0, 2, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(statusText);
        stack.Children.Add(roleLabel);

        // Node badge — shows which machine executed this task (Phase 3 distributed)
        // or which node it was assigned to (Phase B routing)
        var nodeName = task.ExecutedByNodeId ?? task.TargetNodeName;
        if (!string.IsNullOrEmpty(nodeName) && nodeName != "This PC")
        {
            var nodeBadge = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(0x30, 0x76, 0xB9, 0x00)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(4, 1, 4, 1),
                Margin          = new Thickness(0, 3, 0, 0),
                Child           = new TextBlock
                {
                    Text       = $"🖥 {nodeName}",
                    FontSize   = 9,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
                },
                Name = "NodeBadge",
            };
            stack.Children.Add(nodeBadge);
        }

        var card = new Border
        {
            Tag             = task.Id,
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x1E, 0x14)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(10, 6, 10, 6),
            Margin          = new Thickness(0, 4, 8, 4),
            Cursor          = System.Windows.Input.Cursors.Hand,
            Child           = stack,
            MinWidth        = 140,
        };

        // Click card → switch to that worker's stream tab
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (_taskTabMap.TryGetValue(task.Id, out var tabKey))
                SelectTab(tabKey);
        };

        return card;
    }

    private static void RefreshTaskCard(Border card, SwarmTask task)
    {
        if (card.Child is not StackPanel sp) return;
        if (sp.Children[0] is TextBlock title)
            title.Text = $"{task.StatusIcon} {task.Title}";

        card.BorderBrush = task.Status switch
        {
            SwarmTaskStatus.InProgress => new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
            SwarmTaskStatus.Done       => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E)),
            SwarmTaskStatus.Error      => new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47)),
            _                          => new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
        };

        // Add or update node badge when a worker claims the task (ExecutedByNodeId arrives)
        var nodeName = task.ExecutedByNodeId ?? task.TargetNodeName;
        if (string.IsNullOrEmpty(nodeName) || nodeName == "This PC") return;

        // Check if badge already exists (Name="NodeBadge" set in BuildTaskCard)
        var existingBadge = sp.Children.OfType<Border>()
            .FirstOrDefault(b => b.Name == "NodeBadge");

        if (existingBadge is not null)
        {
            // Update text (node may have changed from assigned to actual executor)
            if (existingBadge.Child is TextBlock tb) tb.Text = $"🖥 {nodeName}";
        }
        else
        {
            // Add badge — happens when ExecutedByNodeId is set after task completion
            sp.Children.Add(new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(0x30, 0x76, 0xB9, 0x00)),
                BorderBrush     = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(3),
                Padding         = new Thickness(4, 1, 4, 1),
                Margin          = new Thickness(0, 3, 0, 0),
                Name            = "NodeBadge",
                Child           = new TextBlock
                {
                    Text       = $"🖥 {nodeName}",
                    FontSize   = 9,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
                },
            });
        }
    }

    // ── Agent activity columns ────────────────────────────────────────────────

    private void AddAgentLog(string agentKey, string msg)
    {
        var (panel, scroll, fg) = agentKey switch
        {
            "researcher" => (LogResearcher, ScrollResearcher, Color.FromRgb(0x4A, 0x9F, 0xD9)),
            "coder"      => (LogCoder,      ScrollCoder,      Color.FromRgb(0x76, 0xB9, 0x00)),
            "uidev"      => (LogUIDev,      ScrollUIDev,      Color.FromRgb(0xC5, 0x86, 0xC0)),
            "tester"     => (LogTester,     ScrollTester,     Color.FromRgb(0xF0, 0xC0, 0x60)),
            _            => (LogBoss,       ScrollBoss,       Color.FromRgb(0x76, 0xB9, 0x00)),
        };

        // Warnings and errors get a distinct colour
        Color textColor;
        if (msg.StartsWith("⚠") || msg.StartsWith("ERROR"))
            textColor = Color.FromRgb(0xCC, 0xA7, 0x00);
        else if (msg.StartsWith("→"))
            textColor = Color.FromRgb(0x4E, 0xC9, 0x4E);
        else
            textColor = Color.FromRgb(0x88, 0x99, 0x88);

        var entry = new TextBlock
        {
            Text         = msg,
            FontSize     = 10,
            FontFamily   = new FontFamily("Segoe UI"),
            Foreground   = new SolidColorBrush(textColor),
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 1, 0, 1),
        };
        panel.Children.Add(entry);
        scroll.ScrollToBottom();
    }

    // ── Launch project (post-completion) ─────────────────────────────────────

    private void BtnLaunchProject_Click(object sender, RoutedEventArgs e)
        => SelectTab("results");

    // ── Launch Pad: Run, Open, Apply, Fix ─────────────────────────────────────

    private async void BtnRunProject_Click(object sender, RoutedEventArgs e)
        => await RunProjectAsync();

    private void BtnOpenResultFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = string.IsNullOrWhiteSpace(_lastStagingDir)
            ? _lastOutputProjectDir
            : _lastStagingDir;
        if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer", Arguments = $"\"{dir}\"", UseShellExecute = true,
            });
        }
    }

    private void BtnApplyToWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastStagingDir) || string.IsNullOrWhiteSpace(WorkspaceRoot))
        {
            StatusChanged?.Invoke("No staging directory or workspace to apply to.");
            return;
        }
        try
        {
            var files = System.IO.Directory.GetFiles(
                _lastStagingDir, "*", System.IO.SearchOption.AllDirectories);
            int copied = 0;
            foreach (var src in files)
            {
                var rel = System.IO.Path.GetRelativePath(_lastStagingDir, src);
                var dst = System.IO.Path.Combine(WorkspaceRoot, rel);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
                System.IO.File.Copy(src, dst, overwrite: true);
                copied++;
            }
            BtnApplyToWorkspace.Content    = $"✓  {copied} files applied";
            BtnApplyToWorkspace.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
            StatusChanged?.Invoke($"Applied {copied} file(s) to workspace");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Apply failed: {ex.Message}");
        }
    }

    private void BtnFixThis_Click(object sender, RoutedEventArgs e)
    {
        var output = TbRunOutput.Text;
        if (output.Length > 3000) output = "…" + output[^3000..];

        var fixGoal = $"Fix the runtime error in the project.\n" +
                      $"Error: {TbRunError.Text}\n\n" +
                      $"Program output:\n{output}";

        // Switch to idle state and pre-fill the goal
        BtnNewRun_Click(this, new RoutedEventArgs());
        TbGoal.Text = fixGoal;
    }

    // ── Async run with output capture ─────────────────────────────────────────

    private async Task RunProjectAsync()
    {
        var projectDir = string.IsNullOrWhiteSpace(_lastStagingDir)
            ? _lastOutputProjectDir
            : _lastStagingDir;

        if (string.IsNullOrWhiteSpace(projectDir) || !System.IO.Directory.Exists(projectDir))
        {
            TbRunOutput.Text = "(No project directory found.)";
            return;
        }

        TbRunOutput.Text       = "";
        BdrRunError.Visibility = Visibility.Collapsed;
        BtnRunProject.IsEnabled = false;

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        var entry = DetectEntryPoint(projectDir);
        if (entry is null)
        {
            AppendRunOutput("No runnable entry point detected — opening folder in Explorer.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer", Arguments = $"\"{projectDir}\"", UseShellExecute = true,
            });
            BtnRunProject.IsEnabled = true;
            return;
        }

        if (!entry.CaptureOutput)
        {
            // GUI exe — launch and forget
            AppendRunOutput($"▶ Launching {System.IO.Path.GetFileName(entry.Launcher)}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = entry.Launcher, WorkingDirectory = projectDir, UseShellExecute = true,
            });
            BtnRunProject.IsEnabled = true;
            return;
        }

        AppendRunOutput($"▶ {entry.Launcher} {entry.Args}");
        AppendRunOutput($"  (in {projectDir})");
        AppendRunOutput("");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = entry.Launcher,
            Arguments              = entry.Args,
            WorkingDirectory       = projectDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi)!;

            var stdoutTask = ReadStreamAsync(proc.StandardOutput,
                line => Dispatcher.InvokeAsync(() => AppendRunOutput(line)), ct);
            var stderrTask = ReadStreamAsync(proc.StandardError,
                line => Dispatcher.InvokeAsync(() => AppendRunOutput(line, isError: true)), ct);

            await Task.WhenAll(stdoutTask, stderrTask);
            await proc.WaitForExitAsync(ct);

            int code = proc.ExitCode;
            AppendRunOutput("");
            AppendRunOutput($"─── exited {(code == 0 ? "✓ ok" : $"✗ code {code}")} ───");

            if (code != 0)
                ShowRunError($"Process exited with code {code}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppendRunOutput($"\nError launching: {ex.Message}");
            ShowRunError(ex.Message);
        }
        finally
        {
            _ = Dispatcher.InvokeAsync(() => BtnRunProject.IsEnabled = true);
        }
    }

    private static async Task ReadStreamAsync(
        System.IO.StreamReader reader,
        Action<string> onLine,
        CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null
                   && !ct.IsCancellationRequested)
            {
                onLine(line);
            }
        }
        catch (OperationCanceledException) { }
    }

    private record EntryPoint(string Launcher, string Args, bool CaptureOutput);

    private static EntryPoint? DetectEntryPoint(string projectDir)
    {
        // Python scripts — capture stdout/stderr
        foreach (var name in new[] { "main.py", "app.py", "run.py" })
        {
            foreach (var sub in new[] { "", "src" })
            {
                var p = sub.Length > 0
                    ? System.IO.Path.Combine(projectDir, sub, name)
                    : System.IO.Path.Combine(projectDir, name);
                if (System.IO.File.Exists(p))
                    return new("python", $"\"{p}\"", CaptureOutput: true);
            }
        }
        // Node scripts
        foreach (var name in new[] { "index.js", "app.js" })
        {
            foreach (var sub in new[] { "", "src" })
            {
                var p = sub.Length > 0
                    ? System.IO.Path.Combine(projectDir, sub, name)
                    : System.IO.Path.Combine(projectDir, name);
                if (System.IO.File.Exists(p))
                    return new("node", $"\"{p}\"", CaptureOutput: true);
            }
        }
        // Exe — launch without capture (may be a GUI app)
        var exes = System.IO.Directory.GetFiles(
            projectDir, "*.exe", System.IO.SearchOption.TopDirectoryOnly);
        if (exes.Length > 0)
            return new(exes[0], "", CaptureOutput: false);

        return null;
    }

    private void AppendRunOutput(string line, bool isError = false)
    {
        TbRunOutput.Text += (isError ? "! " : "") + line + "\n";
        RunOutputScroll.ScrollToBottom();
    }

    private void ShowRunError(string message)
    {
        TbRunError.Text        = message;
        BdrRunError.Visibility = Visibility.Visible;
    }
}
