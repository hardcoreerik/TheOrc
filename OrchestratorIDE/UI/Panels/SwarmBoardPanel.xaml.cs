using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Swarm;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Hub-and-spoke visual board for the multi-agent Swarm feature.
///
/// Layout:
///   Left  — node diagram (Boss at top, 3 worker nodes below, canvas lines)
///   Right — tabbed live stream output per agent
///   Bottom strip — scrollable task card list
///
/// Gate: only enabled when activeModel contains "nemotron" AND OLLAMA_NUM_PARALLEL ≥ 3.
/// </summary>
public partial class SwarmBoardPanel : UserControl
{
    // ── Dependencies (set by MainWindow) ──────────────────────────────────────
    public OllamaClient? Ollama        { get; set; }
    public string        ActiveModel   { get; set; } = "";
    public string?       WorkspaceRoot { get; set; }

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
    }

    // ── Runtime state ─────────────────────────────────────────────────────────
    private SwarmSession?             _session;
    private string                    _activeTab  = "boss";
    private readonly DispatcherTimer  _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool                      _pulseOn;
    private string                    _lastOutputProjectDir = "";

    // Per-tab output buffers (thinking stripped out)
    private readonly Dictionary<string, string> _streams = new()
    {
        ["boss"] = "", ["researcher"] = "", ["coder"] = "", ["uidev"] = "",
    };

    // Per-tab thinking buffers (<think>…</think> content)
    private readonly Dictionary<string, string> _thinkStreams = new()
    {
        ["boss"] = "", ["researcher"] = "", ["coder"] = "", ["uidev"] = "",
    };

    // Per-tab raw remainder for incremental <think> tag parsing
    private readonly Dictionary<string, string> _rawPending = new()
    {
        ["boss"] = "", ["researcher"] = "", ["coder"] = "", ["uidev"] = "",
    };

    // Per-tab: are we currently inside a <think> block?
    private readonly Dictionary<string, bool> _inThink = new()
    {
        ["boss"] = false, ["researcher"] = false, ["coder"] = false, ["uidev"] = false,
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
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        TbSlots.Text = $"{slots} slot{(slots == 1 ? "" : "s")}";
        UpdateSlotButtons(slots);
        RefreshGate();
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

    // ── Gate logic ────────────────────────────────────────────────────────────

    private bool IsCapable(out string reason)
    {
        // Gate checks the worker (coder) model, not the boss — nemotron is the worker.
        var workerToCheck = string.IsNullOrWhiteSpace(_workerModel) ? ActiveModel : _workerModel;
        if (!workerToCheck.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Swarm requires an NVIDIA Nemotron model as the Coder worker.\nSet the Coder Model picker to a nemotron variant.";
            return false;
        }
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        if (slots < 3)
        {
            reason = $"Swarm requires OLLAMA_NUM_PARALLEL ≥ 3 (currently {slots}).\nUse the slot picker below to set it, then restart Ollama.";
            return false;
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

        // Reset node states
        SetNodeIdle(NodeBoss);
        SetNodeIdle(NodeResearcher);
        SetNodeIdle(NodeCoder);
        SetNodeUIDev(NodeUIDev);
        TbBossStatus.Text       = "Orchestrating…";
        TbResearcherStatus.Text = "idle";
        TbCoderStatus.Text      = "idle";
        TbUIDevStatus.Text      = "idle";
        IcoResearcherThink.Visibility = Visibility.Collapsed;
        IcoCoderThink.Visibility      = Visibility.Collapsed;
        IcoUIDevThink.Visibility      = Visibility.Collapsed;

        // Reset tabs
        TabResearcher.Visibility = Visibility.Collapsed;
        TabCoder.Visibility      = Visibility.Collapsed;
        TabUIDev.Visibility      = Visibility.Collapsed;
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
        _session = new SwarmSession(Ollama!, ActiveModel, WorkspaceRoot, coderModel, researcherModel);

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
        SetNodeIdle(NodeBoss);
        TbBossStatus.Text = "Done ⬡";
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
                    SwarmWorkerRole.UIDeveloper  => "uidev",
                    _                           => "coder"
                };
                _taskTabMap[task.Id] = tabKey;

                // Show relevant tab button
                switch (task.Role)
                {
                    case SwarmWorkerRole.Researcher:  TabResearcher.Visibility = Visibility.Visible; break;
                    case SwarmWorkerRole.Coder:       TabCoder.Visibility      = Visibility.Visible; break;
                    case SwarmWorkerRole.UIDeveloper: TabUIDev.Visibility      = Visibility.Visible; break;
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
                    _                           => null
                };
                if (cwPanel is not null) cwPanel.Visibility = Visibility.Collapsed;
                var steerBtn = role switch
                {
                    SwarmWorkerRole.Researcher  => BtnSteerResearcher,
                    SwarmWorkerRole.Coder       => BtnSteerCoder,
                    SwarmWorkerRole.UIDeveloper => BtnSteerUIDev,
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

            // Show launch button if there's a project directory to open
            _lastOutputProjectDir = _session?.GetOutputProjectDir() ?? "";
            if (!string.IsNullOrWhiteSpace(_lastOutputProjectDir) &&
                System.IO.Directory.Exists(_lastOutputProjectDir))
            {
                BtnLaunchProject.Visibility = Visibility.Visible;
            }
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

    // ── Tab switching ─────────────────────────────────────────────────────────

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
            SelectTab(tag);
    }

    private void SelectTab(string key)
    {
        _activeTab = key;
        RefreshStreamDisplay(key);

        // Highlight active tab button
        foreach (var btn in new[] { TabBoss, TabResearcher, TabCoder, TabUIDev })
        {
            var isActive = btn.Tag as string == key;
            btn.BorderBrush = isActive
                ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                : new SolidColorBrush(Colors.Transparent);
            btn.Foreground = isActive
                ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                : (Brush)FindResource("Br.Text.Muted");
        }
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

        foreach (var worker in new[] { NodeResearcher, NodeCoder, NodeUIDev })
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
    }

    // ── Agent activity columns ────────────────────────────────────────────────

    private void AddAgentLog(string agentKey, string msg)
    {
        var (panel, scroll, fg) = agentKey switch
        {
            "researcher" => (LogResearcher, ScrollResearcher, Color.FromRgb(0x4A, 0x9F, 0xD9)),
            "coder"      => (LogCoder,      ScrollCoder,      Color.FromRgb(0x76, 0xB9, 0x00)),
            "uidev"      => (LogUIDev,      ScrollUIDev,      Color.FromRgb(0xC5, 0x86, 0xC0)),
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
        => LaunchOutputProject(_lastOutputProjectDir);

    private static void LaunchOutputProject(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir) ||
            !System.IO.Directory.Exists(projectDir))
            return;

        // Detect entry point in priority order
        var candidates = new[]
        {
            // Python
            System.IO.Path.Combine(projectDir, "main.py"),
            System.IO.Path.Combine(projectDir, "app.py"),
            System.IO.Path.Combine(projectDir, "run.py"),
            System.IO.Path.Combine(projectDir, "src", "main.py"),
            System.IO.Path.Combine(projectDir, "src", "app.py"),
            // JavaScript / Node
            System.IO.Path.Combine(projectDir, "index.js"),
            System.IO.Path.Combine(projectDir, "src", "index.js"),
            System.IO.Path.Combine(projectDir, "app.js"),
        };

        // Check for Python files first
        foreach (var candidate in candidates)
        {
            if (!System.IO.File.Exists(candidate)) continue;

            var ext = System.IO.Path.GetExtension(candidate).ToLowerInvariant();
            if (ext == ".py")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "python",
                    Arguments       = $"\"{candidate}\"",
                    WorkingDirectory = projectDir,
                    UseShellExecute = true,
                });
                return;
            }
            if (ext == ".js")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "node",
                    Arguments       = $"\"{candidate}\"",
                    WorkingDirectory = projectDir,
                    UseShellExecute = true,
                });
                return;
            }
        }

        // Look for an .exe in the project dir
        var exeFiles = System.IO.Directory.GetFiles(projectDir, "*.exe",
                           System.IO.SearchOption.TopDirectoryOnly);
        if (exeFiles.Length > 0)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = exeFiles[0],
                WorkingDirectory = projectDir,
                UseShellExecute = true,
            });
            return;
        }

        // Fallback: open the folder in Explorer
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "explorer",
            Arguments       = $"\"{projectDir}\"",
            UseShellExecute = true,
        });
    }
}
