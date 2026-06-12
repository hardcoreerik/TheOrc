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

    private List<HiveHost> _hosts = [];
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(8) };

    public HivePanel()
    {
        InitializeComponent();
        _poll.Tick += async (_, _) => await ProbeAndDraw();
        Loaded   += async (_, _) => { Refresh(); await ProbeAndDraw(); _poll.Start(); };
        Unloaded += (_, _) => _poll.Stop();
    }

    public void Refresh()
    {
        _hosts = HiveHosts.Load(LocalUrl);
        DrawConstellation();
    }

    private async Task ProbeAndDraw()
    {
        // Probe every host (This PC included) off the UI thread, then redraw.
        await Task.WhenAll(_hosts.Select(h => HiveHosts.ProbeAsync(h)));
        var up = _hosts.Count(h => h.Reachable == true);
        HiveSummary.Text = $"{up}/{_hosts.Count} node{(_hosts.Count == 1 ? "" : "s")} online";
        DrawConstellation();
    }

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
        // What this node offers / is doing (Phase A: model count; H1 adds GPU+lanes+job).
        string status = host.Reachable == false ? "offline"
                      : host.Models.Count > 0   ? $"{host.Models.Count} models ready"
                      : isCenter                ? "this machine" : "online";
        sp.Children.Add(new TextBlock
        {
            Text = status, FontFamily = new FontFamily("Segoe UI"), FontSize = 10,
            Foreground = new SolidColorBrush(alive
                ? Color.FromRgb(0xA8, 0xCC, 0x80) : Color.FromRgb(0x88, 0x88, 0x88)),
        });

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
        card.MouseLeftButtonUp += (_, _) => ShowDetail(host);
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

    // ── Actions ────────────────────────────────────────────────────────────

    private void ShowDetail(HiveHost host)
    {
        if (host.Name == "This PC")
        {
            MessageBox.Show($"This PC\n{host.Url}\n\n{host.Models.Count} models available to the hive.",
                "HIVE MIND — This PC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var r = MessageBox.Show(
            $"{host.Name}\n{host.Url}\n" +
            (host.Reachable == false ? "Offline" : $"{host.Models.Count} models online") +
            "\n\nRemove this node from the hive?",
            "HIVE MIND — Node", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            _hosts.RemoveAll(x => x.Name == host.Name && x.Url == host.Url);
            HiveHosts.Save(_hosts);
            DrawConstellation();
        }
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
}
