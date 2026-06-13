using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// HIVE MIND visualizer (Phase A → H1): a war-camp constellation. "This PC"
/// sits at the center; peer nodes orbit as cards with a connection line that
/// pulses green when the node answers. Each card shows name, GPU/VRAM, lanes,
/// and what the node is working on. Built on the HiveHosts store + ProbeAsync;
/// when H1 lands the node API, the cards gain real GPU/lane/job data — the
/// layout doesn't change, only the data source.
/// </summary>
public partial class HivePanel : UserControl
{
    public string LocalUrl { get; set; } = "http://localhost:11434";

    // ── Event log ─────────────────────────────────────────────────────────────

    private HiveEventBus? _eventBus;
    private long _eventSeq = -1;
    private const int MaxEventRows = 150;
    private readonly DispatcherTimer _eventPoll = new() { Interval = TimeSpan.FromSeconds(2) };

    public HiveEventBus? EventBus
    {
        get => _eventBus;
        set
        {
            _eventBus = value;
            if (value is not null)
                _eventPoll.Start();
            else
                _eventPoll.Stop();
        }
    }

    private List<HiveHost> _hosts = [];
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// Warchief's base URL (e.g. "http://192.168.1.10:7079") so the This-PC
    /// context menu can copy it for workers and fetch /hive/tasks/status.
    /// Set by MainWindow after the HiveTaskQueue starts.
    /// </summary>
    public string? WarchiefBaseUrl { get; set; }

    /// <summary>
    /// Fired when the user picks "Set as Warchief target" on a peer node card.
    /// Arg: "http://host:7079". MainWindow subscribes to update HiveWarchiefUrl
    /// in settings and optionally restart the worker agent.
    /// </summary>
    public event Action<string>? OnWarchiefTargetSelected;

    /// <summary>
    /// HIVE MIND C2 — fired when the user clicks "⚡ Use RPC" on a node card.
    /// Args: list of "ip:port" strings to add to the coordinator's --rpc chain.
    /// MainWindow subscribes and restarts LlamaServerManager with these endpoints.
    /// </summary>
    public event Action<IReadOnlyList<string>>? OnApplyRpcWorkers;

    public HivePanel()
    {
        InitializeComponent();
        _poll.Tick += async (_, _) => await ProbeAndDraw();
        _eventPoll.Tick += (_, _) => PollEvents();
        Loaded   += async (_, _) => { Refresh(); await ProbeAndDraw(); _poll.Start(); };
        Unloaded += (_, _) => { _poll.Stop(); _eventPoll.Stop(); };
    }

    public void Refresh()
    {
        _hosts = HiveHosts.Load(LocalUrl);
        DrawConstellation();
    }

    private async Task ProbeAndDraw()
    {
        // Probe reachability, then HIVE API (VRAM/RpcPort/models) for alive remote nodes.
        await Task.WhenAll(_hosts.Select(h => HiveHosts.ProbeAsync(h)));
        await Task.WhenAll(_hosts
            .Where(h => h.Reachable == true && h.Name != "This PC")
            .Select(h => HiveHosts.ProbeHiveApiAsync(h)));
        var up = _hosts.Count(h => h.Reachable == true);
        HiveSummary.Text = $"{up}/{_hosts.Count} node{(_hosts.Count == 1 ? "" : "s")} online";
        DrawConstellation();
    }

    // ── Event log rendering ────────────────────────────────────────────────

    private void PollEvents()
    {
        if (_eventBus is null) return;
        var events = _eventBus.Since(_eventSeq);
        if (events.Length == 0) return;

        foreach (var ev in events)
        {
            _eventSeq = Math.Max(_eventSeq, ev.Seq);
            var color  = EventColor(ev.Type);
            var prefix = ev.Ts.ToLocalTime().ToString("HH:mm:ss");
            var worker = ev.WorkerId.Length > 0 ? $" {ev.WorkerId} ·" : "";
            var row    = new EventRow($"[{prefix}]{worker} {ev.Msg}", color);
            IcEventLog.Items.Add(row);
        }

        // Trim to MaxEventRows
        while (IcEventLog.Items.Count > MaxEventRows)
            IcEventLog.Items.RemoveAt(0);

        TbEventCount.Text = $"({IcEventLog.Items.Count})";
        SvEventLog.ScrollToEnd();
    }

    private static SolidColorBrush EventColor(string type) => type switch
    {
        "task_queued"    => new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0x40)),
        "task_claimed"   => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xE0)),
        "task_executing" => new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
        "task_complete"  => new SolidColorBrush(Color.FromRgb(0xA8, 0xCC, 0x80)),
        "task_failed"    => new SolidColorBrush(Color.FromRgb(0xFF, 0x60, 0x60)),
        "task_timeout"   => new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40)),
        "task_requeued"  => new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40)),
        _                => new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
    };

    private sealed record EventRow(string Text, SolidColorBrush Color);

    // ── Constellation rendering ────────────────────────────────────────────

    private void HiveCanvas_SizeChanged(object s, SizeChangedEventArgs e) => DrawConstellation();

    private void DrawConstellation()
    {
        var c = HiveCanvas;
        c.Children.Clear();
        double w = c.ActualWidth, h = c.ActualHeight;
        if (w < 50 || h < 50 || _hosts.Count == 0) return;

        double cx = w / 2, cy = h / 2;
        var peers = _hosts.Where(x => x.Name != "This PC").ToList();
        var local = _hosts.FirstOrDefault(x => x.Name == "This PC");

        // Orbit radius scales to the smaller half-dimension, leaving card room.
        double radius = Math.Max(120, Math.Min(w, h) / 2 - 130);

        // Connection lines + peer cards arranged on a circle.
        for (int i = 0; i < peers.Count; i++)
        {
            double angle = -Math.PI / 2 + i * 2 * Math.PI / Math.Max(1, peers.Count);
            double px = cx + radius * Math.Cos(angle);
            double py = cy + radius * Math.Sin(angle);
            bool alive = peers[i].Reachable == true;

            var line = new Line
            {
                X1 = cx, Y1 = cy, X2 = px, Y2 = py,
                StrokeThickness = alive ? 2 : 1,
                Stroke = new SolidColorBrush(alive
                    ? Color.FromRgb(0x4E, 0xC9, 0x4E)
                    : Color.FromRgb(0x3A, 0x3A, 0x3A)),
                StrokeDashArray = alive ? null : [4.0, 4.0],
            };
            c.Children.Add(line);
            if (alive)
            {
                var pulse = new DoubleAnimation(0.35, 1.0, TimeSpan.FromSeconds(1.4))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                line.BeginAnimation(OpacityProperty, pulse);
            }

            var card = BuildNodeCard(peers[i], isCenter: false);
            Canvas.SetLeft(card, px - 95);
            Canvas.SetTop(card, py - 45);
            c.Children.Add(card);
        }

        // Center: This PC (always present).
        var center = BuildNodeCard(local ?? new HiveHost { Name = "This PC", Url = LocalUrl },
                                   isCenter: true);
        Canvas.SetLeft(center, cx - 105);
        Canvas.SetTop(center, cy - 50);
        c.Children.Add(center);

        if (peers.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "No peer nodes yet. Install TheOrc on another PC (Join HIVE MIND),\n" +
                       "or click “Join a node” to add one by address.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                FontSize = 12, TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(hint, cx - 160);
            Canvas.SetTop(hint, cy + 70);
            c.Children.Add(hint);
        }
    }

    private Border BuildNodeCard(HiveHost host, bool isCenter)
    {
        bool alive = host.Reachable == true || isCenter && host.Reachable != false;
        var accent = isCenter ? Color.FromRgb(0x76, 0xB9, 0x00)
                   : alive    ? Color.FromRgb(0x4E, 0xC9, 0x4E)
                              : Color.FromRgb(0x55, 0x55, 0x55);

        var sp = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };
        sp.Children.Add(new TextBlock
        {
            Text = (isCenter ? "★ " : alive ? "🟢 " : "⚪ ") + host.Name,
            FontFamily = new FontFamily("Consolas"), FontSize = isCenter ? 13 : 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(accent),
        });
        sp.Children.Add(new TextBlock
        {
            Text = (host.Source == "tailscale" ? "🔗 " : "") +
                   (host.Hostname.Length > 0 ? host.Hostname
                                             : host.Url.Replace("http://", "").Replace(":11434", "")),
            FontFamily = new FontFamily("Consolas"), FontSize = 9,
            Foreground = new SolidColorBrush(host.Source == "tailscale"
                ? Color.FromRgb(0x6A, 0x9A, 0xC8) : Color.FromRgb(0x77, 0x77, 0x77)),
            Margin = new Thickness(0, 1, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        // Status line: model count + VRAM + RPC badge.
        var statusParts = new List<string>();
        if (host.Reachable == false)          statusParts.Add("offline");
        else if (host.Models.Count > 0)       statusParts.Add($"{host.Models.Count} models");
        else if (isCenter)                    statusParts.Add("this machine");
        else                                  statusParts.Add("online");
        if (host.VramFreeMb > 0)              statusParts.Add($"{host.VramFreeMb / 1024.0:F0}GB VRAM");

        sp.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", statusParts),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            Foreground = new SolidColorBrush(alive
                ? Color.FromRgb(0xA8, 0xCC, 0x80) : Color.FromRgb(0x88, 0x88, 0x88)),
        });

        // ⚡ RPC badge + "Use RPC" button when the node runs llama-rpc-server.
        if (!isCenter && host.RpcPort > 0 && alive)
        {
            var rpcRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            rpcRow.Children.Add(new TextBlock
            {
                Text = $"⚡ RPC :{host.RpcPort}",
                FontFamily = new FontFamily("Consolas"), FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            var safeName = System.Text.RegularExpressions.Regex.Replace(host.Name, @"[^\w\-]", "_");
            var rpcBtn = new Button
            {
                Content = "Use RPC", FontSize = 9, Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x00)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40)),
                BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(rpcBtn, $"Hive.UseRpc.{safeName}");
            rpcBtn.Click += (_, _) => ApplyRpcWorker(host);
            rpcRow.Children.Add(rpcBtn);
            sp.Children.Add(rpcRow);
        }

        var card = new Border
        {
            Background = new SolidColorBrush(isCenter
                ? Color.FromRgb(0x12, 0x1A, 0x0A) : Color.FromRgb(0x10, 0x14, 0x10)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(isCenter ? 2 : 1),
            CornerRadius = new CornerRadius(6),
            Width = isCenter ? 210 : 190,
            Child = sp, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = BuildTooltip(host, isCenter),
        };
        card.MouseLeftButtonUp  += (_, _) => ShowDetail(host);
        card.ContextMenu         = BuildContextMenu(host, isCenter);
        return card;
    }

    private static string BuildTooltip(HiveHost host, bool isCenter)
    {
        var models = host.Models.Count > 0
            ? "\nModels: " + string.Join(", ", host.Models.Take(6)) +
              (host.Models.Count > 6 ? $" (+{host.Models.Count - 6})" : "")
            : "";
        return $"{host.Name}\n{host.Url}\n" +
               (host.Reachable == false ? "Status: offline" : "Status: online") +
               models +
               (isCenter ? "" : "\n\nClick to view / remove this node.");
    }

    // ── Context menus ──────────────────────────────────────────────────────

    private ContextMenu BuildContextMenu(HiveHost host, bool isCenter)
    {
        bool alive = host.Reachable == true || isCenter;

        var cm = new ContextMenu
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x0C, 0x12, 0x0C)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x2A)),
            BorderThickness = new Thickness(1),
            Foreground      = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
        };

        if (isCenter)
        {
            cm.Items.Add(CmItem("📋  Copy Warchief URL",
                () => CopyText(WarchiefBaseUrl ?? "", "Warchief URL copied — paste in worker settings")));
            cm.Items.Add(CmItem("📊  View queue status",
                () => _ = ShowQueueStatusAsync(), WarchiefBaseUrl is not null));
            cm.Items.Add(CmItem("🧹  Clear event log",
                () => ClearEventLog()));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("⟳  Probe all nodes",
                () => _ = ProbeAndDraw()));
        }
        else
        {
            cm.Items.Add(CmItem("📋  Copy URL",
                () => CopyText(host.Url, $"{host.Name} URL copied")));
            cm.Items.Add(CmItem("🌐  Open Ollama in browser",
                () => OpenUrl(host.Url + "/api/tags")));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("⚡  Use as RPC worker",
                () => ApplyRpcWorker(host),
                host.RpcPort > 0 && alive));
            cm.Items.Add(CmItem("🎯  Set as Warchief target",
                () => SetAsWarchiefTarget(host)));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("⟳  Probe now",
                () => _ = ProbeOneAndDrawAsync(host)));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("✕  Remove from hive",
                () => RemoveHost(host)));
        }

        return cm;
    }

    private static MenuItem CmItem(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header      = header,
            IsEnabled   = enabled,
            FontFamily  = new FontFamily("Segoe UI"),
            FontSize    = 12,
            Foreground  = new SolidColorBrush(enabled
                ? Color.FromRgb(0xCC, 0xCC, 0xCC) : Color.FromRgb(0x55, 0x55, 0x55)),
            Background  = Brushes.Transparent,
        };
        item.Click += (_, _) => action();
        return item;
    }

    // ── Context menu actions ───────────────────────────────────────────────

    private static void CopyText(string text, string confirmMsg)
    {
        if (string.IsNullOrEmpty(text)) return;
        Clipboard.SetText(text);
        MessageBox.Show(confirmMsg, "HIVE MIND", MessageBoxButton.OK, MessageBoxImage.None);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { UseShellExecute = true, FileName = url }); }
        catch (Exception ex)
        { MessageBox.Show($"Could not open browser: {ex.Message}", "HIVE MIND", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async Task ShowQueueStatusAsync()
    {
        if (WarchiefBaseUrl is null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json   = await http.GetStringAsync($"{WarchiefBaseUrl}/hive/tasks/status");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r      = doc.RootElement;
            int total  = r.TryGetProperty("total",      out var v) ? v.GetInt32() : 0;
            int pending= r.TryGetProperty("pending",    out v)     ? v.GetInt32() : 0;
            int active = r.TryGetProperty("inProgress", out v)     ? v.GetInt32() : 0;
            int done   = r.TryGetProperty("completed",  out v)     ? v.GetInt32() : 0;
            int failed = r.TryGetProperty("failed",     out v)     ? v.GetInt32() : 0;
            MessageBox.Show(
                $"Queue on {WarchiefBaseUrl}\n\n" +
                $"  Total     {total}\n" +
                $"  Pending   {pending}\n" +
                $"  Active    {active}\n" +
                $"  Completed {done}\n" +
                $"  Failed    {failed}",
                "HIVE MIND — Queue Status", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not reach queue: {ex.Message}",
                "HIVE MIND", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearEventLog()
    {
        IcEventLog.Items.Clear();
        _eventSeq    = _eventBus?.HeadSeq ?? -1;
        TbEventCount.Text = "(0)";
    }

    private void SetAsWarchiefTarget(HiveHost host)
    {
        var targetUri = new UriBuilder(host.Url) { Port = HiveTaskQueue.QueuePort }.ToString().TrimEnd('/');
        var result = MessageBox.Show(
            $"Set {host.Name} as this machine's Warchief?\n\n" +
            $"  {targetUri}\n\n" +
            "This machine will switch to Worker mode and send all swarm tasks to " +
            $"{host.Name} for distribution.",
            "HIVE MIND — Set Warchief Target",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            OnWarchiefTargetSelected?.Invoke(targetUri);
    }

    private void RemoveHost(HiveHost host)
    {
        var r = MessageBox.Show(
            $"Remove {host.Name} ({host.Url}) from the hive?",
            "HIVE MIND — Remove Node", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _hosts.RemoveAll(x => x.Name == host.Name && x.Url == host.Url);
        HiveHosts.Save(_hosts);
        DrawConstellation();
    }

    private async Task ProbeOneAndDrawAsync(HiveHost host)
    {
        await HiveHosts.ProbeAsync(host);
        if (host.Reachable == true && host.Name != "This PC")
            await HiveHosts.ProbeHiveApiAsync(host);
        DrawConstellation();
    }

    // ── Actions ────────────────────────────────────────────────────────────

    private void ShowDetail(HiveHost host)
    {
        var models = host.Models.Count > 0
            ? $"\n\nModels ({host.Models.Count}):\n" +
              string.Join(", ", host.Models.Take(10)) +
              (host.Models.Count > 10 ? $"\n(+{host.Models.Count - 10} more)" : "")
            : "";
        var vram = host.VramFreeMb > 0 ? $"\nVRAM free: {host.VramFreeMb / 1024.0:F1} GB" : "";
        var rpc  = host.RpcPort > 0   ? $"\nRPC: :{host.RpcPort}" : "";
        MessageBox.Show(
            $"{host.Name}\n{host.Url}" +
            $"\nStatus: {(host.Reachable == false ? "offline" : "online")}" +
            vram + rpc + models +
            (host.Name == "This PC" ? "" : "\n\nRight-click for actions."),
            $"HIVE MIND — {host.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Discovery: beacon callbacks + LAN scan ────────────────────────────────

    // Nodes seen via UDP beacon but not yet trusted (not in hive-hosts.json).
    private readonly Dictionary<string, Services.Hive.HiveBeaconMessage> _discovered = [];

    /// <summary>Called by MainWindow when its HiveBeacon fires OnNodeSeen.</summary>
    public void OnBeaconNodeSeen(Services.Hive.HiveBeaconMessage msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => OnBeaconNodeSeen(msg)); return; }
        // Skip nodes we already have in the hive (exact URL match).
        if (_hosts.Any(h => string.Equals(h.Url, msg.OllamaUrl, StringComparison.OrdinalIgnoreCase))) return;
        // Skip this machine's own beacon — it may advertise a LAN IP while stored as localhost.
        if (IsSelfUrl(msg.OllamaUrl)) return;
        _discovered[msg.OllamaUrl] = msg;
        RefreshDiscoveredStrip();
    }

    private static bool IsSelfUrl(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            if (host is "localhost" or "127.0.0.1") return true;
            var localIps = Services.Hive.HiveRpcWorker.LocalAddresses();
            return localIps.Any(ip => string.Equals(ip, host, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private async void BtnScanLan_Click(object s, RoutedEventArgs e)
    {
        BtnScanLan.IsEnabled = false;
        BtnScanLan.Content   = "📡 Scanning…";
        try
        {
            var found = await Services.Hive.HiveBeacon.ScanAsync(durationMs: 3000);
            foreach (var msg in found)
            {
                if (_hosts.Any(h => string.Equals(h.Url, msg.OllamaUrl, StringComparison.OrdinalIgnoreCase))) continue;
                _discovered[msg.OllamaUrl] = msg;
            }
            RefreshDiscoveredStrip();
            // Suppress the "nothing found" dialog when our own beacon is running —
            // ScanAsync returns [] when it can't bind the port, but OnBeaconNodeSeen
            // already populates _discovered via the long-lived listener.
            if (found.Count == 0 && _discovered.Count == 0)
                MessageBox.Show(
                    "No HIVE MIND nodes found on the LAN.\n\n" +
                    "Make sure TheOrc is installed and HIVE MIND is enabled on the other machine, " +
                    "then try again.",
                    "HIVE MIND — LAN Scan", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            BtnScanLan.IsEnabled = true;
            BtnScanLan.Content   = "📡 Scan LAN";
        }
    }

    private void RefreshDiscoveredStrip()
    {
        PnlDiscoveredNodes.Children.Clear();
        foreach (var msg in _discovered.Values)
        {
            var chip = BuildDiscoveredChip(msg);
            PnlDiscoveredNodes.Children.Add(chip);
        }
        BdrDiscovered.Visibility = _discovered.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildDiscoveredChip(Services.Hive.HiveBeaconMessage msg)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };

        sp.Children.Add(new TextBlock
        {
            Text       = $"⬡ {msg.Name}",
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0x40)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        var hostLabel = SafeUriHost(msg.OllamaUrl);
        sp.Children.Add(new TextBlock
        {
            Text       = $"{hostLabel} · {msg.Models.Length} models",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });

        var safeName = System.Text.RegularExpressions.Regex.Replace(msg.Name, @"[^\w\-]", "_");
        var addBtn = new Button
        {
            Content = "⊕ Trust & Add", FontSize = 10, Padding = new Thickness(8, 3, 8, 3),
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x3D, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xCC, 0x80)),
            BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(addBtn, $"Hive.TrustAndAdd.{safeName}");
        addBtn.Click += (_, _) => TrustAndAdd(msg);
        sp.Children.Add(addBtn);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x08)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x00)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4, 8, 4), Child = sp,
        };
    }

    private async void TrustAndAdd(Services.Hive.HiveBeaconMessage msg)
    {
        _discovered.Remove(msg.OllamaUrl);
        RefreshDiscoveredStrip();

        var host = new Services.Hive.HiveHost
        {
            Name   = msg.Name,
            Url    = msg.OllamaUrl,
            Source = "lan",
        };
        _hosts.Add(host);
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        await ProbeAndDraw();
    }

    private async void BtnAddNode_Click(object s, RoutedEventArgs e)
    {
        var dlg = new UI.Controls.UserInputDialog(
            "Join a hive node — enter \"NAME = host\" (e.g. HARDCOREPC = 192.168.1.20). " +
            "Other PCs running TheOrc with HIVE MIND enabled expose Ollama on port 11434.")
            { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Answer)) return;

        // Accept "name = host", "name=host:port", or bare "host".
        var raw = dlg.Answer.Trim();
        string name, addr;
        if (raw.Contains('='))
        {
            var parts = raw.Split('=', 2);
            name = parts[0].Trim(); addr = parts[1].Trim();
        }
        else { addr = raw; name = raw.Split(':')[0]; }

        if (!addr.Contains(':')) addr += ":11434";
        var url = addr.StartsWith("http") ? addr : $"http://{addr}";

        _hosts.Add(new HiveHost { Name = name, Url = url });
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        await ProbeAndDraw();
    }

    private async void BtnFindTailscale_Click(object s, RoutedEventArgs e)
    {
        if (!TailscalePeers.IsInstalled)
        {
            MessageBox.Show(
                "Tailscale was not found on this PC.\n\nTailscale gives your hive stable names " +
                "that work across networks. Install it on the PCs you want in the hive, then click again.",
                "HIVE MIND — Tailscale", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var peers = await Task.Run(TailscalePeers.Discover);
        int added = 0;
        foreach (var p in peers)
        {
            var name = TailscalePeers.ShortName(p.DnsName);
            // Prefer the MagicDNS name as the URL — it survives IP changes.
            var addr = p.DnsName.Length > 0 ? p.DnsName : p.Ip;
            var url = $"http://{addr}:11434";
            if (_hosts.Any(h => h.Url == url || h.Hostname == p.DnsName && p.DnsName.Length > 0)) continue;
            _hosts.Add(new HiveHost
            {
                Name = name.Length > 0 ? name : p.Ip,
                Url = url, Hostname = p.DnsName, Source = "tailscale",
            });
            added++;
        }
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        MessageBox.Show(
            peers.Count == 0 ? "No Tailscale peers found (is Tailscale up and are other nodes online?)."
                             : $"Found {peers.Count} Tailscale peer(s); added {added} new node(s).",
            "HIVE MIND — Tailscale", MessageBoxButton.OK, MessageBoxImage.Information);
        await ProbeAndDraw();
    }

    private void BtnRescan_Click(object s, RoutedEventArgs e) => _ = ProbeAndDraw();

    private static string SafeUriHost(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url.Replace("http://", "").Split(':')[0]; }
    }

    // ── RPC worker activation ─────────────────────────────────────────────────

    /// <summary>
    /// Called when the user clicks "Use RPC" on a single node card.
    /// Collects ALL alive RPC-capable nodes (not just the one clicked) and fires
    /// OnApplyRpcWorkers so the coordinator can restart llama-server with the full chain.
    /// </summary>
    private void ApplyRpcWorker(HiveHost clickedHost)
    {
        // Build the full list of alive RPC endpoints.
        var endpoints = _hosts
            .Where(h => h.RpcPort > 0 && h.Reachable == true && h.Name != "This PC")
            .Select(h =>
            {
                var ip = new Uri(h.Url).Host;
                return $"{ip}:{h.RpcPort}";
            })
            .ToList();

        if (endpoints.Count == 0)
        {
            // Fallback: use the clicked host even if probe hasn't updated RpcPort yet.
            var ip = new Uri(clickedHost.Url).Host;
            endpoints = [$"{ip}:{clickedHost.RpcPort}"];
        }

        var summary = string.Join(", ", endpoints);
        var result = MessageBox.Show(
            $"Chain llama-server with {endpoints.Count} RPC worker(s):\n\n  {summary}\n\n" +
            "This will RESTART llama-server with these RPC endpoints. " +
            "The combined VRAM of all nodes will be available for one large model.\n\n" +
            "Proceed?",
            "HIVE MIND — Apply RPC Workers",
            MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
            OnApplyRpcWorkers?.Invoke(endpoints);
    }
}
