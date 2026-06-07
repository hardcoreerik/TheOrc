using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;

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

    // ── Runtime state ─────────────────────────────────────────────────────────
    private SwarmSession?             _session;
    private string                    _activeTab  = "boss";
    private readonly DispatcherTimer  _pulseTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool                      _pulseOn;

    // Per-tab stream buffers (tab key → text)
    private readonly Dictionary<string, string> _streams = new()
    {
        ["boss"]       = "",
        ["researcher"] = "",
        ["coder"]      = "",
        ["uidev"]      = "",
    };

    // Task-id → tab key mapping (populated when tasks are planned)
    private readonly Dictionary<string, string> _taskTabMap = [];

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<string>? StatusChanged;

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
        if (!ActiveModel.Contains("nemotron", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Swarm requires NVIDIA Nemotron Mini.\nSwitch model in Settings → Model.";
            return false;
        }
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        if (slots < 3)
        {
            reason = $"Swarm requires OLLAMA_NUM_PARALLEL ≥ 3 (currently {slots}).\nConfigure in Settings → Ollama.";
            return false;
        }
        reason = "";
        return true;
    }

    private void RefreshGate()
    {
        var capable = IsCapable(out var reason);
        BtnLaunch.IsEnabled      = capable;
        BdrGateWarn.Visibility   = capable ? Visibility.Collapsed : Visibility.Visible;
        TbGateWarn.Text          = reason;
        TbModelName.Text         = string.IsNullOrEmpty(ActiveModel) ? "—" : ActiveModel;
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

    private void BtnSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!int.TryParse(tag, out var n) || n < 1 || n > 4) return;

        OllamaParallelHelper.SetPermanently(n);

        // Refresh slot label in the header and selection highlight
        RefreshSlotLabel();

        // Refresh gate (may now be satisfied if user picked ≥ 3)
        RefreshGate();

        // Show the restart note so the user knows Ollama needs a restart
        TbSlotRestartNote.Visibility = Visibility.Visible;
    }

    // ── Launch ────────────────────────────────────────────────────────────────

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (Ollama is null || !IsCapable(out _)) return;
        var goal = TbGoal.Text.Trim();
        if (string.IsNullOrWhiteSpace(goal)) return;

        StartSwarm(goal);
    }

    private void StartSwarm(string goal)
    {
        // Reset streams
        foreach (var key in _streams.Keys.ToList()) _streams[key] = "";
        _taskTabMap.Clear();
        TaskCardPanel.Children.Clear();

        // Reset node states
        SetNodeIdle(NodeBoss);
        SetNodeIdle(NodeResearcher);
        SetNodeIdle(NodeCoder);
        SetNodeUIDev(NodeUIDev);
        TbBossStatus.Text       = "Planning…";
        TbResearcherStatus.Text = "idle";
        TbCoderStatus.Text      = "idle";
        TbUIDevStatus.Text      = "idle";

        // Reset tabs
        TabResearcher.Visibility = Visibility.Collapsed;
        TabCoder.Visibility      = Visibility.Collapsed;
        TabUIDev.Visibility      = Visibility.Collapsed;
        SelectTab("boss");

        // Show active board
        PnlIdle.Visibility   = Visibility.Collapsed;
        PnlActive.Visibility = Visibility.Visible;
        BtnStopSwarm.Visibility = Visibility.Visible;
        TbActiveGoal.Text    = $"Goal: {goal}";

        // Header: ACTIVE state
        SetHeaderActive(true);
        _pulseTimer.Start();

        // Wire and launch session
        _session = new SwarmSession(Ollama!, ActiveModel, WorkspaceRoot);

        _session.OnBossToken     += OnBossToken;
        _session.OnWorkerToken   += OnWorkerToken;
        _session.OnTasksPlanned  += OnTasksPlanned;
        _session.OnTaskChanged   += OnTaskChanged;
        _session.OnSwarmComplete += OnSwarmComplete;
        _session.OnError         += OnError;
        _session.OnStopped       += () => Dispatcher.InvokeAsync(OnSwarmStopped);

        _ = _session.RunAsync(goal);
        StatusChanged?.Invoke("Swarm active");
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    private void BtnStopSwarm_Click(object sender, RoutedEventArgs e)
    {
        _session?.Stop();
    }

    private void OnSwarmStopped()
    {
        _pulseTimer.Stop();
        SetHeaderActive(false);
        BtnStopSwarm.Visibility = Visibility.Collapsed;
        SetNodeIdle(NodeBoss);
        TbBossStatus.Text = "Done";
    }

    // ── SwarmSession event handlers ───────────────────────────────────────────

    private void OnBossToken(string token)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _streams["boss"] += token;
            if (_activeTab == "boss") TbStream.Text = _streams["boss"];
            StreamScroll.ScrollToBottom();
        });
    }

    private void OnWorkerToken(string taskId, string token)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!_taskTabMap.TryGetValue(taskId, out var tabKey)) return;
            _streams[tabKey] += token;
            if (_activeTab == tabKey) TbStream.Text = _streams[tabKey];
            StreamScroll.ScrollToBottom();
        });
    }

    private void OnTasksPlanned(List<SwarmTask> tasks)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TbBossStatus.Text = $"{tasks.Count} tasks";

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
            switch (task.Role)
            {
                case SwarmWorkerRole.Researcher:
                    UpdateWorkerNode(NodeResearcher, TbResearcherStatus, task);
                    break;
                case SwarmWorkerRole.Coder:
                    UpdateWorkerNode(NodeCoder, TbCoderStatus, task);
                    break;
                case SwarmWorkerRole.UIDeveloper:
                    UpdateWorkerNode(NodeUIDev, TbUIDevStatus, task);
                    break;
            }

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

    private void OnSwarmComplete(string merged)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _streams["boss"] += "\n\n──── SWARM COMPLETE ────\n\n" + merged;
            if (_activeTab == "boss") TbStream.Text = _streams["boss"];
            TbBossStatus.Text = "Complete ✓";
            SetNodeDone(NodeBoss);
            StatusChanged?.Invoke("Swarm complete");
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
        _activeTab   = key;
        TbStream.Text = _streams.TryGetValue(key, out var txt) ? txt : "";
        StreamScroll.ScrollToBottom();

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

    private static void UpdateWorkerNode(Border node, TextBlock statusLabel, SwarmTask task)
    {
        switch (task.Status)
        {
            case SwarmTaskStatus.InProgress:
                SetNodeActive(node);
                statusLabel.Text       = task.Title.Length > 18 ? task.Title[..15] + "…" : task.Title;
                statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
                break;
            case SwarmTaskStatus.Done:
                SetNodeDone(node);
                statusLabel.Text       = "done ✓";
                statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x4E));
                break;
            case SwarmTaskStatus.Error:
                SetNodeError(node);
                statusLabel.Text       = "error ✗";
                statusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                break;
            default:
                SetNodeIdle(node);
                statusLabel.Text       = "idle";
                statusLabel.Foreground = (Brush)node.FindResource("Br.Text.Muted");
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
}
