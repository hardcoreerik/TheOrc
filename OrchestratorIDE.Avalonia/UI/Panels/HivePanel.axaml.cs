// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input.Platform;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UI.Panels;

public sealed record EventRow(string Text, IBrush Color);

public partial class HivePanel : UserControl
{
    // ── Public properties ─────────────────────────────────────────────────────
    public string LocalUrl    { get; set; } = "http://localhost:11434";
    public string LocalNodeId { get; set; } = "";
    public HiveNodeServer? NodeServer { get; set; }

    // ── Delegate callbacks injected by MainWindow ─────────────────────────────
    public Func<string, string, Task>?          AlertAsync   { get; set; }
    public Func<string, string, Task<bool>>?    ConfirmAsync { get; set; }
    public Func<string, string, Task<string?>>? InputAsync   { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<string>?                  OnWarchiefTargetSelected;
    public event Action<IReadOnlyList<string>>?   OnApplyRpcWorkers;

    // ── Event log ─────────────────────────────────────────────────────────────
    private const int MaxEventRows = 150;
    private readonly ObservableCollection<EventRow> _events = [];
    private readonly DispatcherTimer _eventPoll = new() { Interval = TimeSpan.FromSeconds(2) };
    private HiveEventBus? _eventBus;
    private long _eventSeq = -1;

    public HiveEventBus? EventBus
    {
        get => _eventBus;
        set
        {
            _eventBus = value;
            if (value is not null) _eventPoll.Start(); else _eventPoll.Stop();
        }
    }

    // ── Constellation ─────────────────────────────────────────────────────────
    private List<HiveHost> _hosts = [];
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };
    private string? _warchiefNodeId;

    // ── Discovered nodes (beacon / scan) ──────────────────────────────────────
    private readonly Dictionary<string, HiveBeaconMessage> _discovered = [];

    // ── Warchief public URL (set by MainWindow from Settings) ─────────────────
    public string? WarchiefBaseUrl { get; set; }

    // ─────────────────────────────────────────────────────────────────────────
    public HivePanel()
    {
        InitializeComponent();
        IcEventLog.ItemsSource = _events;

        _poll.Tick      += async (_, _) => await ProbeAndDrawAsync();
        _eventPoll.Tick += (_, _) => PollEvents();

        Loaded   += async (_, _) => { Refresh(); await ProbeAndDrawAsync(); _poll.Start(); };
        Unloaded += (_, _) => { _poll.Stop(); _eventPoll.Stop(); };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Refresh()
    {
        _hosts = HiveHosts.Load(LocalUrl);
        DrawConstellation();
    }

    private async Task ProbeAndDrawAsync()
    {
        await Task.WhenAll(_hosts.Select(h => HiveHosts.ProbeAsync(h)));
        await Task.WhenAll(_hosts
            .Where(h => h.Reachable == true && h.Name != "This PC")
            .Select(h => HiveHosts.ProbeHiveApiAsync(h)));

        var up = _hosts.Count(h => h.Reachable == true);
        HiveSummary.Text = $"{up}/{_hosts.Count} node{(_hosts.Count == 1 ? "" : "s")} online";
        DrawConstellation();
    }

    // ── Event log ─────────────────────────────────────────────────────────────

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
            AddEvent($"[{prefix}]{worker} {ev.Msg}", color);
        }
    }

    private void AddEvent(string text, IBrush color)
    {
        _events.Add(new EventRow(text, color));
        while (_events.Count > MaxEventRows) _events.RemoveAt(0);
        TbEventCount.Text = $"({_events.Count})";
        SvEventLog.ScrollToEnd();
    }

    private void ClearEventLog()
    {
        _events.Clear();
        _eventSeq = _eventBus?.HeadSeq ?? -1;
        TbEventCount.Text = "(0)";
    }

    private static IBrush EventColor(string type) => type switch
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

    // ── Constellation drawing ─────────────────────────────────────────────────

    private void HiveCanvas_SizeChanged(object? s, SizeChangedEventArgs e) => DrawConstellation();

    private void DrawConstellation()
    {
        var c = HiveCanvas;
        c.Children.Clear();
        double w = c.Bounds.Width, h = c.Bounds.Height;
        if (w < 50 || h < 50 || _hosts.Count == 0) return;

        double cx = w / 2, cy = h / 2;
        var peers = _hosts.Where(x => x.Name != "This PC").ToList();
        var local = _hosts.FirstOrDefault(x => x.Name == "This PC")
                    ?? new HiveHost { Name = "This PC", Url = LocalUrl };

        double radius = Math.Max(120, Math.Min(w, h) / 2 - 130);

        bool isLocalWarchief = !string.IsNullOrEmpty(_warchiefNodeId) &&
                               !string.IsNullOrEmpty(LocalNodeId) &&
                               _warchiefNodeId == LocalNodeId;

        string? warchiefIp = null;
        if (!isLocalWarchief && !string.IsNullOrEmpty(_warchiefNodeId))
        {
            var wcPeer = HivePeerStore.Default.Find(_warchiefNodeId);
            if (wcPeer?.LastKnownAddress?.Length > 0)
                warchiefIp = wcPeer.LastKnownAddress.Split(':')[0];
        }

        for (int i = 0; i < peers.Count; i++)
        {
            double angle = -Math.PI / 2 + i * 2 * Math.PI / Math.Max(1, peers.Count);
            double px = cx + radius * Math.Cos(angle);
            double py = cy + radius * Math.Sin(angle);
            bool alive = peers[i].Reachable == true;

            var line = new Line
            {
                StartPoint      = new Point(cx, cy),
                EndPoint        = new Point(px, py),
                StrokeThickness = alive ? 2 : 1,
                Stroke = new SolidColorBrush(alive
                    ? Color.FromRgb(0x4E, 0xC9, 0x4E)
                    : Color.FromRgb(0x3A, 0x3A, 0x3A)),
                Opacity = alive ? 0.85 : 1.0,
            };
            if (!alive)
                line.StrokeDashArray = new AvaloniaList<double> { 4.0, 4.0 };
            c.Children.Add(line);

            bool isPeerWarchief = warchiefIp is not null
                && SafeUriHost(peers[i].Url) == warchiefIp;
            var card = BuildNodeCard(peers[i], isCenter: false, isWarchief: isPeerWarchief);
            Canvas.SetLeft(card, px - 95);
            Canvas.SetTop(card, py - 45);
            c.Children.Add(card);
        }

        var center = BuildNodeCard(local, isCenter: true, isWarchief: isLocalWarchief);
        Canvas.SetLeft(center, cx - 105);
        Canvas.SetTop(center, cy - 50);
        c.Children.Add(center);

        if (peers.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "No peer nodes yet. Use 📡 Scan LAN to discover nodes\nor ➕ Add node to add one by address.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                FontSize = 12, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Width = 300,
            };
            Canvas.SetLeft(hint, cx - 150);
            Canvas.SetTop(hint, cy + 70);
            c.Children.Add(hint);
        }
    }

    private Border BuildNodeCard(HiveHost host, bool isCenter, bool isWarchief = false)
    {
        bool alive = host.Reachable == true || (isCenter && host.Reachable != false);
        var accent = isWarchief ? Color.FromRgb(0xFF, 0xD7, 0x00)
                   : isCenter   ? Color.FromRgb(0x76, 0xB9, 0x00)
                   : alive      ? Color.FromRgb(0x4E, 0xC9, 0x4E)
                                : Color.FromRgb(0x55, 0x55, 0x55);

        var sp = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };

        var prefix = isWarchief ? "👑 " : isCenter ? "★ " : alive ? "🟢 " : "⚪ ";
        sp.Children.Add(new TextBlock
        {
            Text       = prefix + host.Name,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = isCenter ? 13 : 12,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(accent),
        });

        if (isWarchief)
            sp.Children.Add(new TextBlock
            {
                Text       = "W A R C H I E F",
                FontFamily = new FontFamily("Consolas"), FontSize = 8,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)),
                Opacity = 0.75, Margin = new Thickness(0, 0, 0, 3),
            });

        var hostDisplay = host.Hostname?.Length > 0
            ? host.Hostname
            : host.Url.Replace("http://", "").Replace(":11434", "");
        sp.Children.Add(new TextBlock
        {
            Text       = (host.Source == "tailscale" ? "🔗 " : "") + hostDisplay,
            FontFamily = new FontFamily("Consolas"), FontSize = 9,
            Foreground = new SolidColorBrush(host.Source == "tailscale"
                ? Color.FromRgb(0x6A, 0x9A, 0xC8) : Color.FromRgb(0x77, 0x77, 0x77)),
            Margin = new Thickness(0, 1, 0, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var statusParts = new List<string>();
        if (host.Reachable == false)       statusParts.Add("offline");
        else if (host.Models?.Count > 0)   statusParts.Add($"{host.Models.Count} models");
        else if (isCenter)                 statusParts.Add("this machine");
        else                               statusParts.Add("online");
        if (host.VramFreeMb > 0)           statusParts.Add($"{host.VramFreeMb / 1024.0:F0}GB VRAM");

        sp.Children.Add(new TextBlock
        {
            Text       = string.Join(" · ", statusParts),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            Foreground = new SolidColorBrush(alive
                ? Color.FromRgb(0xA8, 0xCC, 0x80) : Color.FromRgb(0x88, 0x88, 0x88)),
        });

        if (!isCenter && host.RpcPort > 0 && alive)
        {
            var rpcRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 4, 0, 0),
            };
            rpcRow.Children.Add(new TextBlock
            {
                Text = $"⚡ RPC :{host.RpcPort}",
                FontFamily = new FontFamily("Consolas"), FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
            });
            var rpcBtn = new Button
            {
                Content = "Use RPC", FontSize = 9, Padding = new Thickness(6, 2, 6, 2),
                Background    = new SolidColorBrush(Color.FromRgb(0x2A, 0x1A, 0x00)),
                BorderBrush   = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40)),
                Foreground    = new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40)),
                BorderThickness = new Thickness(1),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            rpcBtn.Click += (_, _) => ApplyRpcWorker(host);
            rpcRow.Children.Add(rpcBtn);
            sp.Children.Add(rpcRow);
        }

        var card = new Border
        {
            Background = new SolidColorBrush(
                isWarchief ? Color.FromRgb(0x1A, 0x14, 0x00)
                : isCenter ? Color.FromRgb(0x12, 0x1A, 0x0A)
                           : Color.FromRgb(0x10, 0x14, 0x10)),
            BorderBrush     = new SolidColorBrush(accent),
            BorderThickness = new Thickness(isCenter || isWarchief ? 2 : 1),
            CornerRadius    = new CornerRadius(6),
            Width           = isCenter ? 210 : 190,
            Child           = sp,
            Cursor          = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(card, BuildTooltip(host, isCenter));
        card.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                _ = ShowDetailAsync(host);
        };
        card.ContextMenu = BuildContextMenu(host, isCenter);
        return card;
    }

    private static string BuildTooltip(HiveHost host, bool isCenter)
    {
        var models = (host.Models?.Count ?? 0) > 0
            ? "\nModels: " + string.Join(", ", host.Models!.Take(6)) +
              (host.Models!.Count > 6 ? $" (+{host.Models.Count - 6})" : "")
            : "";
        return $"{host.Name}\n{host.Url}\n" +
               (host.Reachable == false ? "Status: offline" : "Status: online") +
               models +
               (isCenter ? "" : "\n\nClick to view / remove this node.");
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private ContextMenu BuildContextMenu(HiveHost host, bool isCenter)
    {
        bool alive = host.Reachable == true || isCenter;
        var cm = new ContextMenu();

        if (isCenter)
        {
            cm.Items.Add(CmItem("📋  Copy Warchief URL",  () => _ = CopyTextAsync(WarchiefBaseUrl ?? "")));
            cm.Items.Add(CmItem("📊  View queue status",  () => _ = ShowQueueStatusAsync(), WarchiefBaseUrl is not null));
            cm.Items.Add(CmItem("🧹  Clear event log",    ClearEventLog));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("⟳  Probe all nodes",    () => _ = ProbeAndDrawAsync()));
        }
        else
        {
            cm.Items.Add(CmItem("📋  Copy URL",            () => _ = CopyTextAsync(host.Url)));
            cm.Items.Add(CmItem("🌐  Open in browser",     () => OpenUrl(host.Url + "/api/tags")));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("⚡  Use as RPC worker",   () => ApplyRpcWorker(host), host.RpcPort > 0 && alive));
            cm.Items.Add(CmItem("🎯  Set as Warchief",     () => SetAsWarchiefTarget(host)));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("⟳  Probe now",           () => _ = ProbeOneAndDrawAsync(host)));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("✕  Remove from hive",    () => RemoveHost(host)));
        }

        return cm;
    }

    private static MenuItem CmItem(string header, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // ── Context menu actions ───────────────────────────────────────────────────

    private async Task CopyTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { UseShellExecute = true, FileName = url }); }
        catch { /* ignore */ }
    }

    private async Task ShowDetailAsync(HiveHost host)
    {
        var models = (host.Models?.Count ?? 0) > 0
            ? $"\n\nModels ({host.Models!.Count}):\n" +
              string.Join(", ", host.Models.Take(10)) +
              (host.Models.Count > 10 ? $"\n(+{host.Models.Count - 10} more)" : "")
            : "";
        var vram = host.VramFreeMb > 0 ? $"\nVRAM free: {host.VramFreeMb / 1024.0:F1} GB" : "";
        var rpc  = host.RpcPort > 0   ? $"\nRPC: :{host.RpcPort}" : "";
        await (AlertAsync?.Invoke(
            $"{host.Name}\n{host.Url}" +
            $"\nStatus: {(host.Reachable == false ? "offline" : "online")}" +
            vram + rpc + models +
            (host.Name == "This PC" ? "" : "\n\nRight-click for actions."),
            $"HIVE MIND — {host.Name}") ?? Task.CompletedTask);
    }

    private async Task ShowQueueStatusAsync()
    {
        if (WarchiefBaseUrl is null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var json = await http.GetStringAsync($"{WarchiefBaseUrl}/hive/tasks/status");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            int total   = r.TryGetProperty("total",      out var v) ? v.GetInt32() : 0;
            int pending = r.TryGetProperty("pending",    out v)     ? v.GetInt32() : 0;
            int active  = r.TryGetProperty("inProgress", out v)     ? v.GetInt32() : 0;
            int done    = r.TryGetProperty("completed",  out v)     ? v.GetInt32() : 0;
            int failed  = r.TryGetProperty("failed",     out v)     ? v.GetInt32() : 0;
            await (AlertAsync?.Invoke(
                $"Queue on {WarchiefBaseUrl}\n\n" +
                $"  Total     {total}\n  Pending   {pending}\n" +
                $"  Active    {active}\n  Completed {done}\n  Failed    {failed}",
                "HIVE MIND — Queue Status") ?? Task.CompletedTask);
        }
        catch (Exception ex)
        {
            await (AlertAsync?.Invoke($"Could not reach queue: {ex.Message}", "HIVE MIND") ?? Task.CompletedTask);
        }
    }

    private async void SetAsWarchiefTarget(HiveHost host)
    {
        var targetUri = new UriBuilder(host.Url) { Port = HiveTaskQueue.QueuePort }.ToString().TrimEnd('/');
        var ok = await (ConfirmAsync?.Invoke(
            $"Set {host.Name} as this machine's Warchief?\n\n{targetUri}\n\n" +
            $"This machine will send all swarm tasks to {host.Name} for distribution.",
            "HIVE MIND — Set Warchief Target") ?? Task.FromResult(false));
        if (ok) OnWarchiefTargetSelected?.Invoke(targetUri);
    }

    private async void RemoveHost(HiveHost host)
    {
        var ok = await (ConfirmAsync?.Invoke(
            $"Remove {host.Name} ({host.Url}) from the hive?",
            "HIVE MIND — Remove Node") ?? Task.FromResult(false));
        if (!ok) return;
        _hosts.RemoveAll(x => x.Name == host.Name && x.Url == host.Url);
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        DrawConstellation();
    }

    private async Task ProbeOneAndDrawAsync(HiveHost host)
    {
        await HiveHosts.ProbeAsync(host);
        if (host.Reachable == true && host.Name != "This PC")
            await HiveHosts.ProbeHiveApiAsync(host);
        DrawConstellation();
    }

    private async void ApplyRpcWorker(HiveHost clickedHost)
    {
        var endpoints = _hosts
            .Where(h => h.RpcPort > 0 && h.Reachable == true && h.Name != "This PC")
            .Select(h => $"{SafeUriHost(h.Url)}:{h.RpcPort}")
            .ToList();
        if (endpoints.Count == 0)
            endpoints = [$"{SafeUriHost(clickedHost.Url)}:{clickedHost.RpcPort}"];

        var summary = string.Join(", ", endpoints);
        var ok = await (ConfirmAsync?.Invoke(
            $"Chain llama-server with {endpoints.Count} RPC worker(s):\n\n  {summary}\n\n" +
            $"This will RESTART llama-server with these RPC endpoints. Proceed?",
            "HIVE MIND — Apply RPC Workers") ?? Task.FromResult(false));
        if (ok) OnApplyRpcWorkers?.Invoke(endpoints);
    }

    // ── Discovery / Scan LAN ──────────────────────────────────────────────────

    private async void BtnScanLan_Click(object? s, RoutedEventArgs e)
    {
        BtnScanLan.IsEnabled = false;
        BtnScanLan.Content   = "📡 Scanning…";
        try
        {
            var found = await HiveBeacon.ScanAsync(durationMs: 3000);
            foreach (var msg in found)
            {
                if (_hosts.Any(h => string.Equals(h.Url, msg.OllamaUrl, StringComparison.OrdinalIgnoreCase))) continue;
                _discovered[msg.OllamaUrl] = msg;
            }
            RefreshDiscoveredStrip();
            if (found.Count == 0 && _discovered.Count == 0)
                await (AlertAsync?.Invoke(
                    "No HIVE MIND nodes found on the LAN.\n\nMake sure TheOrc is installed and HIVE MIND is enabled on the other machine, then try again.",
                    "HIVE MIND — LAN Scan") ?? Task.CompletedTask);
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
            PnlDiscoveredNodes.Children.Add(BuildDiscoveredChip(msg));
        BdrDiscovered.IsVisible = _discovered.Count > 0;
    }

    private Border BuildDiscoveredChip(HiveBeaconMessage msg)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = $"⬡ {msg.Name}",
            FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0x40)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"{SafeUriHost(msg.OllamaUrl)} · {msg.Models?.Length ?? 0} models",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        var addBtn = new Button
        {
            Content = "⊕ Trust & Add", FontSize = 10, Padding = new Thickness(8, 3, 8, 3),
            Background    = new SolidColorBrush(Color.FromRgb(0x1F, 0x3D, 0x00)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
            Foreground    = new SolidColorBrush(Color.FromRgb(0xA8, 0xCC, 0x80)),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        addBtn.Click += (_, _) => TrustAndAdd(msg);
        sp.Children.Add(addBtn);

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x08)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x00)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 4, 8, 4), Child = sp,
        };
    }

    private async void TrustAndAdd(HiveBeaconMessage msg)
    {
        _discovered.Remove(msg.OllamaUrl);
        RefreshDiscoveredStrip();
        var host = new HiveHost { Name = msg.Name, Url = msg.OllamaUrl, Source = "lan" };
        _hosts.Add(host);
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        await ProbeAndDrawAsync();
    }

    private async void BtnAddNode_Click(object? s, RoutedEventArgs e)
    {
        var answer = await (InputAsync?.Invoke(
            "Join a hive node",
            "Enter \"NAME = host\" (e.g. HARDCOREPC = 192.168.1.20). " +
            "Other PCs running TheOrc with HIVE MIND enabled expose Ollama on port 11434.")
            ?? Task.FromResult<string?>(null));
        if (string.IsNullOrWhiteSpace(answer)) return;

        var raw = answer.Trim();
        string name, addr;
        if (raw.Contains('=')) { var p = raw.Split('=', 2); name = p[0].Trim(); addr = p[1].Trim(); }
        else                   { addr = raw; name = raw.Split(':')[0]; }
        if (!addr.Contains(':')) addr += ":11434";
        var url = addr.StartsWith("http") ? addr : $"http://{addr}";
        _hosts.Add(new HiveHost { Name = name, Url = url });
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        await ProbeAndDrawAsync();
    }

    private async void BtnFindTailscale_Click(object? s, RoutedEventArgs e)
    {
        if (!TailscalePeers.IsInstalled)
        {
            await (AlertAsync?.Invoke(
                "Tailscale was not found on this PC.\n\nInstall it on the PCs you want in the hive, then click again.",
                "HIVE MIND — Tailscale") ?? Task.CompletedTask);
            return;
        }

        var peers = await Task.Run(TailscalePeers.Discover);
        int added = 0;
        foreach (var p in peers)
        {
            var name = TailscalePeers.ShortName(p.DnsName);
            var addr = p.DnsName.Length > 0 ? p.DnsName : p.Ip;
            var url  = $"http://{addr}:11434";
            if (_hosts.Any(h => h.Url == url || (h.Hostname == p.DnsName && p.DnsName.Length > 0))) continue;
            _hosts.Add(new HiveHost { Name = name.Length > 0 ? name : p.Ip, Url = url, Hostname = p.DnsName, Source = "tailscale" });
            added++;
        }
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        await (AlertAsync?.Invoke(
            peers.Count == 0
                ? "No Tailscale peers found (is Tailscale up and are other nodes online?)."
                : $"Found {peers.Count} Tailscale peer(s); added {added} new node(s).",
            "HIVE MIND — Tailscale") ?? Task.CompletedTask);
        await ProbeAndDrawAsync();
    }

    private void BtnRescan_Click(object? s, RoutedEventArgs e) => _ = ProbeAndDrawAsync();

    // ── HIVE pairing / election callbacks ─────────────────────────────────────

    public async void OnPairingRequest(string sessionId, HivePairingRequest req)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => OnPairingRequest(sessionId, req));
            return;
        }

        var msg = $"HIVE pairing request\n\nNode:        {req.InitiatorName}" +
                  $"\nFingerprint: {req.InitiatorFingerprint}\nSession:     {sessionId}" +
                  "\n\nApprove this node as a Worker?";

        var approved = await (ConfirmAsync?.Invoke(msg, "HIVE — Incoming Pairing Request") ?? Task.FromResult(false));

        if (approved)
        {
            var isMobile = await (ConfirmAsync?.Invoke(
                "Is this a mobile or Android device?\n\nMobile nodes are restricted to the 'researcher' lane and excluded from leader election.",
                "HIVE — Device Type") ?? Task.FromResult(false));
            var lanes = isMobile ? new[] { "researcher" } : Array.Empty<string>();
            NodeServer?.ApprovePairing(sessionId, HiveNodeRole.Worker, lanes, isMobile);
        }
        else
        {
            NodeServer?.RejectPairing(sessionId);
        }

        var color = approved ? Brushes.LimeGreen : Brushes.OrangeRed;
        var label = approved ? "approved" : "rejected";
        AddEvent($"[{DateTime.Now:HH:mm:ss}] Pairing {label}: {req.InitiatorName}", color);
    }

    public void OnElectionStateChanged(ElectionState state, string? warchiefNodeId)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(() => OnElectionStateChanged(state, warchiefNodeId));
            return;
        }
        _warchiefNodeId = warchiefNodeId;
        DrawConstellation();
        var label = state switch
        {
            ElectionState.TemporaryWarchief => "⚔ This node is now temporary Warchief",
            ElectionState.RecoverySync      => $"🔄 Warchief recovering ({warchiefNodeId?[..Math.Min(8, warchiefNodeId?.Length ?? 0)]}…)",
            ElectionState.Normal            => $"👑 Warchief: {warchiefNodeId?[..Math.Min(8, warchiefNodeId?.Length ?? 0)]}…",
            _                               => $"Election: {state}",
        };
        AddEvent($"[{DateTime.Now:HH:mm:ss}] {label}", new SolidColorBrush(Colors.DeepSkyBlue));
    }

    public void OnBeaconNodeSeen(HiveBeaconMessage msg)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.InvokeAsync(() => OnBeaconNodeSeen(msg)); return; }
        if (_hosts.Any(h => string.Equals(h.Url, msg.OllamaUrl, StringComparison.OrdinalIgnoreCase))) return;
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
            return HiveRpcWorker.LocalAddresses().Any(ip =>
                string.Equals(ip, host, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string SafeUriHost(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url.Replace("http://", "").Split(':')[0]; }
    }
}
