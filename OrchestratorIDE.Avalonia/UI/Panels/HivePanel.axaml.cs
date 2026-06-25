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
using OrchestratorIDE.UI.Windows;
using OrchestratorIDE.UI.Controls;

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
    /// <summary>
    /// Raised when the discovery/repair wizard (opened from this panel) founded or joined a
    /// hive — i.e. this node's HiveId changed. MainWindow subscribes to re-broadcast the
    /// beacon payload so the new HiveId propagates (HIVE_MEMBERSHIP_SPEC.md §7; the beacon
    /// lives in MainWindow, not here).
    /// </summary>
    public event Action?                          OnHiveAssociationChanged;

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

    /// <summary>
    /// Disables the flowing-data/breathing-center animation, falling back to the static
    /// constellation rendering. Set by MainWindow from AppSettings.HiveLiteMode.
    /// </summary>
    public bool LiteMode { get; set; }

    // ── Constellation animation ("the warband marching") ───────────────────────
    // Deliberately a fixed ~20fps tick (not 60fps) -- this is ambient decoration on a
    // developer tool's background panel, not core functionality, so it trades visual
    // smoothness for a meaningfully lower CPU floor. LiteMode (Settings) skips this
    // entirely for machines where even that isn't worth it.
    // Node rendering + animation now live in HiveConstellationView (immediate-mode custom
    // control) — this panel maps hosts to role-shaped node visuals and handles click/right-click.

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

        HiveView.NodeClicked      += (_, e) => _ = ShowDetailAsync(e.Node.Host);
        HiveView.NodeRightClicked += (_, e) => ShowNodeMenu(e.Node);

        Loaded   += async (_, _) => { Refresh(); await ProbeAndDrawAsync(); _poll.Start(); };
        Unloaded += (_, _) => { _poll.Stop(); _eventPoll.Stop(); };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Refresh()
    {
        _hosts = HiveHosts.Load(LocalUrl);
        HiveHosts.MergePairedPeers(_hosts);   // show paired nodes, not just named Ollama hosts
        HiveHosts.Dedupe(_hosts);             // collapse LAN + Tailscale entries of one machine
        DrawConstellation();
    }

    private async Task ProbeAndDrawAsync()
    {
        // Re-merge each poll (idempotent) so a node paired AFTER this panel loaded -- e.g. a
        // headless Warband that just completed pairing -- appears within one poll interval
        // without needing an app restart. Dedupe collapses any same-machine LAN/Tailscale
        // duplicates a scan may have added since the last draw.
        HiveHosts.MergePairedPeers(_hosts);
        HiveHosts.Dedupe(_hosts);
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

    private void DrawConstellation()
    {
        var peers = _hosts.Where(x => x.Name != "This PC").ToList();
        var local = _hosts.FirstOrDefault(x => x.Name == "This PC")
                    ?? new HiveHost { Name = "This PC", Url = LocalUrl };

        // TheOrc's model: the local GUI machine IS the Warchief unless a DIFFERENT node was
        // explicitly elected/declared. So crown This PC when no Warchief is known yet, or when
        // the elected Warchief is this node.
        bool isLocalWarchief = string.IsNullOrEmpty(_warchiefNodeId) ||
                               (!string.IsNullOrEmpty(LocalNodeId) && _warchiefNodeId == LocalNodeId);

        string? warchiefIp = null;
        if (!isLocalWarchief && !string.IsNullOrEmpty(_warchiefNodeId))
        {
            var wcPeer = HivePeerStore.Default.Find(_warchiefNodeId);
            if (wcPeer?.LastKnownAddress?.Length > 0)
                warchiefIp = wcPeer.LastKnownAddress.Split(':')[0];
        }

        var nodes = new List<HiveNodeVisual>
        {
            MakeVisual(local, isCenter: true, isWarchief: isLocalWarchief),
        };
        foreach (var p in peers)
        {
            bool wc = warchiefIp is not null && SafeUriHost(p.Url) == warchiefIp;
            nodes.Add(MakeVisual(p, isCenter: false, isWarchief: wc));
        }

        TbNoPeers.IsVisible = peers.Count == 0;
        HiveView.LiteMode = LiteMode;
        HiveView.SetNodes(nodes);
    }

    /// <summary>Maps a host's probed lanes + Warchief flag to a role-shaped node visual.</summary>
    private static HiveNodeVisual MakeVisual(HiveHost host, bool isCenter, bool isWarchief)
    {
        string role = isWarchief ? "warchief"
                    : isCenter   ? "worker"            // local node that isn't the elected Warchief
                    : RoleFromLanes(host.Lanes);
        string state = host.Reachable == false ? "offline" : "online";

        string sub;
        if (state == "offline") sub = "offline";
        else
        {
            sub = role switch
            {
                "warchief" => "warchief",
                "worker"   => isCenter ? "this machine" : "node",
                _          => role,
            };
            if (host.VramFreeMb > 0) sub += $" · {host.VramFreeMb / 1024.0:F0}GB";
        }

        return new HiveNodeVisual
        {
            Host = host, Name = host.Name, Role = role, IsCenter = isCenter, State = state, SubLabel = sub,
        };
    }

    /// <summary>First lane → role shape. Empty lanes (accepts all) → generic worker. Lane-string
    /// matching mirrors the alias spirit of training_pit/ROLE_ARCHITECTURE.md.</summary>
    private static string RoleFromLanes(string[]? lanes)
    {
        if (lanes is null) return "worker";
        // Scan ALL lanes for a recognized role, skipping generic ones ("inference", "all", …) so
        // a node advertising e.g. ["inference","coder","researcher"] reads as Coder, not generic.
        foreach (var lane in lanes)
        {
            var l = lane.ToLowerInvariant();
            if (l.Contains("coder") || l.Contains("backend"))  return "coder";
            if (l.Contains("research"))                         return "research";
            if (l.Contains("ui") || l.Contains("frontend"))     return "ui";
            if (l.Contains("test") || l.Contains("qa"))         return "tester";
            if (l.Contains("review") || l.Contains("audit"))    return "reviewer";
        }
        return "worker";   // empty / only generic lanes → plain hexagon
    }

    /// <summary>Right-click handler — opens the existing per-node role/context menu at the
    /// pointer. (The menu content, including Declare Warchief, lives in BuildContextMenu.)</summary>
    private void ShowNodeMenu(HiveNodeVisual node)
    {
        var menu = BuildContextMenu(node.Host, node.IsCenter);
        menu.Placement = PlacementMode.Pointer;
        menu.Open(HiveView);
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
            cm.Items.Add(new Separator());
            // Mesh-authority Warchief declaration (HIVE_MEMBERSHIP_SPEC.md §6) -- a manual
            // override distinct from HiveElectionService's automatic failover. Only makes
            // sense from the local "This PC" card, since it's declaring THIS machine.
            cm.Items.Add(CmItem("👑  Declare this machine Warchief", () => _ = DeclareWarchiefAsync()));
            // Manual re-invocation of the discovery wizard (HIVE_MEMBERSHIP_SPEC.md §7.1) --
            // the same scan/join/create flow shown automatically at first HIVE start, in case
            // the operator skipped it then or wants to reassociate after a "Reset node
            // identity" (Settings → HIVE).
            cm.Items.Add(CmItem("🔧  Repair HIVE association", () => _ = OpenHiveDiscoveryWizardAsync()));
        }
        else
        {
            cm.Items.Add(CmItem("📋  Copy URL",            () => _ = CopyTextAsync(host.Url)));
            cm.Items.Add(CmItem("🌐  Open in browser",     () => OpenUrl(host.Url + "/api/tags")));
            cm.Items.Add(new Separator());
            // Pairing needs the HIVE port (7078), not Ollama (11434) -- gating this on the
            // general "alive" (Ollama-based) flag let the menu item look clickable for a node
            // whose HIVE node server wasn't actually running, producing a confusing 10s
            // timeout instead of a clear "this node's HIVE port isn't reachable" signal
            // (found 2026-06-21 from a live pairing-failure report).
            cm.Items.Add(CmItem("🤝  Pair with this node", () => _ = PairWithHostAsync(host), host.HiveApiReachable == true));
            cm.Items.Add(CmItem("⚡  Use as RPC worker",   () => ApplyRpcWorker(host), host.RpcPort > 0 && alive));
            // Renamed from "🎯 Set as Warchief" (2026-06-21) -- that label now collides with
            // the unrelated mesh-authority "Declare this machine Warchief" action above. This
            // item only ever pointed this machine's own swarm-task dispatcher at a remote
            // Ollama host; it never touched HiveElectionService or mesh authority at all.
            cm.Items.Add(CmItem("📤  Route my swarm tasks here", () => SetAsWarchiefTarget(host)));
            var acceptMenu = BuildAcceptControlSubmenu(host);
            if (acceptMenu is not null) cm.Items.Add(acceptMenu);
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("⟳  Probe now",           () => _ = ProbeOneAndDrawAsync(host)));
            cm.Items.Add(new Separator());
            cm.Items.Add(CmItem("✕  Remove from hive",    () => RemoveHost(host)));
        }

        return cm;
    }

    /// <summary>
    /// HIVE_MEMBERSHIP_SPEC.md §6.4 — AcceptControlFrom had no UI anywhere before this; every
    /// peer was permanently stuck at the pairing-time default (Ask) with no way to change it,
    /// which meant role-assignment (§6) could never actually auto-promote without a per-event
    /// click no matter what the user wanted. Returns null if <paramref name="host"/> doesn't
    /// resolve to a paired peer (nothing to configure for an unpaired node).
    /// </summary>
    private MenuItem? BuildAcceptControlSubmenu(HiveHost host)
    {
        var nodeId = HivePeerStore.Default.ResolveNodeIdForUrl(host.Url);
        if (string.IsNullOrEmpty(nodeId)) return null;
        var peer = HivePeerStore.Default.Find(nodeId);
        if (peer is null) return null;

        var submenu = new MenuItem { Header = "🔒  Auto-accept role assignment from this peer" };
        foreach (var policy in Enum.GetValues<HiveAcceptControlPolicy>())
        {
            var label = policy switch
            {
                HiveAcceptControlPolicy.Never     => "Never",
                HiveAcceptControlPolicy.Ask       => "Ask each time (default)",
                HiveAcceptControlPolicy.Allowlist => "Allowlist only",
                HiveAcceptControlPolicy.AnyPaired => "Always (any paired peer)",
                _                                  => policy.ToString(),
            };
            var prefix = peer.AcceptControlFrom == policy ? "● " : "○ ";
            submenu.Items.Add(CmItem(prefix + label, () =>
            {
                peer.AcceptControlFrom = policy;
                HivePeerStore.Default.AddOrUpdate(peer);
                AddEvent($"[{DateTime.Now:HH:mm:ss}] {peer.Name}: auto-accept role assignment set to {policy}.",
                    new SolidColorBrush(Colors.Gray));
            }));
        }
        return submenu;
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
        // "This PC"'s own fingerprint, shown so it can be read aloud/compared against
        // what a remote operator sees after pairing with this machine -- pairing's
        // approval response is unauthenticated at the transport level, so this manual
        // comparison is the actual defense against an on-path attacker (see
        // HivePairingClient.CompletePairing).
        var fingerprint = host.Name == "This PC"
            ? $"\nFingerprint: {HiveIdentity.Load().Fingerprint}" : "";
        await (AlertAsync?.Invoke(
            $"{host.Name}\n{host.Url}" +
            $"\nStatus: {(host.Reachable == false ? "offline" : "online")}" +
            vram + rpc + fingerprint + models +
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
        // Despite the method name (left as-is to avoid an unrelated rename churning the
        // diff -- the menu label is what users actually see, and that's already fixed),
        // this has nothing to do with mesh-authority Warchief/HiveElectionService. It only
        // points this machine's own swarm-task dispatcher at a remote Ollama host.
        var targetUri = new UriBuilder(host.Url) { Port = HiveTaskQueue.QueuePort }.ToString().TrimEnd('/');
        var ok = await (ConfirmAsync?.Invoke(
            $"Route this machine's swarm tasks to {host.Name}?\n\n{targetUri}\n\n" +
            $"This machine will send all swarm tasks to {host.Name} for distribution.",
            "HIVE MIND — Route Swarm Tasks") ?? Task.FromResult(false));
        if (ok) OnWarchiefTargetSelected?.Invoke(targetUri);
    }

    /// <summary>
    /// HIVE_MEMBERSHIP_SPEC.md §6.3 — a manual, human-invoked override that broadcasts a
    /// role-assignment request to every currently-paired peer, asking each to become a
    /// Worker. NOT the same mechanism as HiveElectionService's automatic Warchief failover
    /// -- refuses to run while an election is underway rather than racing it.
    /// </summary>
    private async Task DeclareWarchiefAsync()
    {
        var election = NodeServer?.ElectionService;
        if (election is not null && election.State != ElectionState.Normal)
        {
            await (AlertAsync?.Invoke(
                $"An election is currently in progress ({election.State}) — wait for it to resolve before declaring a Warchief manually.",
                "HIVE MIND — Declare Warchief") ?? Task.CompletedTask);
            return;
        }

        var peers = HivePeerStore.Default.All().Where(p => !p.Revoked).ToList();
        if (peers.Count == 0)
        {
            await (AlertAsync?.Invoke("No paired peers to promote — pair with at least one node first.",
                "HIVE MIND — Declare Warchief") ?? Task.CompletedTask);
            return;
        }

        var ok = await (ConfirmAsync?.Invoke(
            $"Declare this machine the hive's Warchief? This sends a role-assignment request to all " +
            $"{peers.Count} currently paired peer(s), asking each to set their role to Worker.\n\n" +
            "Peers configured to auto-accept will update immediately; others will show an approval prompt on their end.",
            "HIVE MIND — Declare Warchief") ?? Task.FromResult(false));
        if (!ok) return;

        var identity = HiveIdentity.Load();
        var peerStore = HivePeerStore.Default;
        foreach (var peer in peers)
        {
            var outcome = await HiveNodeServer.SendRoleAssignAsync(peer, HiveNodeRole.Worker, identity, peerStore);
            IBrush color = outcome switch
            {
                "accepted"         => Brushes.LimeGreen,
                "pending_approval" => new SolidColorBrush(Colors.DeepSkyBlue),
                "unreachable"      => Brushes.OrangeRed,
                _ when outcome.StartsWith("error:") => Brushes.OrangeRed,
                _                  => Brushes.Gray, // "rejected"
            };
            // AddEvent touches an ObservableCollection + TextBlock + ScrollViewer -- after an
            // await, the continuation isn't guaranteed to still be on the UI thread (contrast
            // PairWithHostAsync, which dispatches explicitly for the same reason). Matching
            // that established convention here too (grok review BLOCKER, 2026-06-21).
            await Dispatcher.UIThread.InvokeAsync(() =>
                AddEvent($"[{DateTime.Now:HH:mm:ss}] Role-assign to {peer.Name}: {outcome}", color));
        }
    }

    /// <summary>
    /// Opens the discovery wizard (HIVE_MEMBERSHIP_SPEC.md §7) -- manually via the "Repair
    /// HIVE association" context-menu item, or automatically after a hiveid_mismatch pairing
    /// failure (PairWithHostAsync, below). HivePanel is a UserControl, not a Window, so it
    /// needs its TopLevel to host the dialog -- mirrors CopyTextAsync's existing
    /// TopLevel.GetTopLevel(this) lookup just above.
    /// </summary>
    private async Task OpenHiveDiscoveryWizardAsync(string? reason = null)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            await (AlertAsync?.Invoke("Could not open the HIVE wizard — no parent window.",
                "HIVE MIND") ?? Task.CompletedTask);
            return;
        }
        var wizard = new HiveDiscoveryWizard(reason);
        var changed = await wizard.ShowDialog<bool>(owner);
        if (changed)
        {
            // Founded/joined a hive — let MainWindow re-broadcast the beacon with the new
            // HiveId, then redraw to reflect any newly-trusted peer.
            OnHiveAssociationChanged?.Invoke();
            DrawConstellation();
        }
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

    private async Task PairWithHostAsync(HiveHost host)
    {
        var targetHost = SafeUriHost(host.Url);
        var ok = await (ConfirmAsync?.Invoke(
            $"Send a pairing request to {host.Name} ({targetHost})?\n\n" +
            "That machine will need to approve it before this completes — " +
            "this will wait up to 2 minutes for a response.",
            "HIVE MIND — Pair") ?? Task.FromResult(false));
        if (!ok)
        {
            AddEvent($"[{DateTime.Now:HH:mm:ss}] Pairing with {host.Name} cancelled before sending.",
                new SolidColorBrush(Colors.Gray));
            return;
        }

        // The initiator side previously logged NOTHING to the Activity feed -- every status
        // update only ever went through a modal dialog. If that dialog was missed (not
        // focused, behind another window) or the operator just wasn't looking at the right
        // moment, there was no way to tell afterward whether pairing was even attempted, let
        // alone what happened. The responder side (OnPairingRequest, below) already logs
        // here; this brings the initiator side to parity (found 2026-06-21 from a live
        // confusing-popup report).
        AddEvent($"[{DateTime.Now:HH:mm:ss}] Pairing request sent to {host.Name} ({targetHost})…",
            new SolidColorBrush(Colors.DeepSkyBlue));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(125));
        // PairAsync never throws (it converts every failure mode, including
        // cancellation, into a Result) -- no try/catch needed here.
        var result = await HivePairingClient.PairAsync(targetHost, ct: cts.Token);

        // Explicit UI-thread dispatch for the visual-tree mutation, even though the
        // await above should already resume on this method's original (UI) context --
        // belt-and-suspenders per Grok CLI review, no ambiguity left either way.
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (result.Outcome == HivePairingClient.Outcome.Approved && result.Pending is { } pending)
            {
                // Trust is NOT yet persisted at this point (HivePairingClient.PendingTrust's
                // doc comment) -- GET /hive/pair/{sessionId} is unauthenticated, so this
                // fingerprint comparison is the actual defense against an on-path attacker
                // forging the "approved" response. Only ConfirmAndTrust() if the operator
                // explicitly confirms it matches what the target machine itself displays --
                // persisting before this check (as the first version of this code did) is
                // fail-open (Codex CLI BLOCKER, 2026-06-20).
                var confirmed = await (ConfirmAsync?.Invoke(
                    $"{host.Name} approved the pairing request.\n\n" +
                    $"Fingerprint: {pending.Fingerprint}\n\n" +
                    "Compare this against the fingerprint shown on the OTHER machine's own " +
                    "HIVE panel (click \"This PC\" there) before continuing — the approval " +
                    "response isn't transport-authenticated, so this comparison is the real " +
                    "check against an on-path attacker. Do they match?",
                    "HIVE MIND — Verify Fingerprint") ?? Task.FromResult(false));

                if (confirmed)
                {
                    HivePairingClient.ConfirmAndTrust(pending);
                    AddEvent($"[{DateTime.Now:HH:mm:ss}] ✓ Paired with {host.Name} (fingerprint verified).",
                        new SolidColorBrush(Colors.LimeGreen));
                    await (AlertAsync?.Invoke($"✓ Paired with {host.Name}.", "HIVE MIND — Pair")
                        ?? Task.CompletedTask);
                    DrawConstellation();
                }
                else
                {
                    AddEvent($"[{DateTime.Now:HH:mm:ss}] ✗ Pairing with {host.Name} declined — fingerprint mismatch or unconfirmed.",
                        new SolidColorBrush(Colors.OrangeRed));
                    await (AlertAsync?.Invoke(
                        "Pairing not completed — fingerprint mismatch (or unconfirmed). " +
                        "No peer was trusted.", "HIVE MIND — Pair") ?? Task.CompletedTask);
                }
                return;
            }

            var msg = result.Outcome switch
            {
                HivePairingClient.Outcome.Rejected =>
                    $"{host.Name} rejected the pairing request.",
                HivePairingClient.Outcome.Expired =>
                    "The pairing request expired before it was answered.",
                HivePairingClient.Outcome.TimedOut =>
                    "Timed out waiting for a response.",
                HivePairingClient.Outcome.AlreadyPaired =>
                    $"Already paired with {host.Name}.",
                _ => $"Pairing failed: {result.Message}",
            };
            AddEvent($"[{DateTime.Now:HH:mm:ss}] {msg}", new SolidColorBrush(Colors.OrangeRed));
            await (AlertAsync?.Invoke(msg, "HIVE MIND — Pair") ?? Task.CompletedTask);

            // HIVE_MEMBERSHIP_SPEC.md §7.1 auto-detected-problem trigger -- a hiveid_mismatch
            // refusal (either the request-time check in HiveNodeServer, or CompletePairing's
            // own double-check) is a strong signal the operator is trying to join a different
            // hive than the one this machine already belongs to. Keyed off the dedicated
            // Result.HiveIdConflict flag rather than substring-matching the human-readable
            // message, so a reworded message can't silently break this trigger.
            if (result.HiveIdConflict)
            {
                var openWizard = await (ConfirmAsync?.Invoke(
                    "This looks like a hive-identity conflict. Open the HIVE repair wizard to resolve it?",
                    "HIVE MIND — Hive Conflict") ?? Task.FromResult(false));
                if (openWizard)
                    await OpenHiveDiscoveryWizardAsync(
                        $"Reopened after a hive-identity conflict pairing with {host.Name}.");
            }
        });
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
        DrawConstellation();
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
        DrawConstellation();
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

        // Only add tailnet peers that actually run TheOrc — probe each one's HIVE node port
        // (7078) and skip anything that doesn't answer. This keeps non-TheOrc tailnet devices
        // (phones, routers, a NAS) OUT of the constellation. Found 2026-06-24: a phone on the
        // tailnet kept appearing as a phantom HIVE node because the scan auto-added every peer
        // unconditionally. (When the TheOrc mobile companion app exposes a node endpoint it will
        // answer this probe and join legitimately — see HIVE_VISUALS.md mobile-companion note.)
        var probes = peers.Select(async p =>
        {
            var addr = p.DnsName.Length > 0 ? p.DnsName : p.Ip;
            var info = await HiveNodeServer.ProbeAsync(addr, 2000);
            return (peer: p, addr, isOrc: info is not null);
        });
        var results = await Task.WhenAll(probes);

        int added = 0, skipped = 0;
        foreach (var (p, addr, isOrc) in results)
        {
            if (!isOrc) { skipped++; continue; }
            var name = TailscalePeers.ShortName(p.DnsName);
            var disp = name.Length > 0 ? name : p.Ip;
            var url  = $"http://{addr}:11434";
            // Skip if this machine is already present under ANY address (LAN, manual, paired).
            if (_hosts.Any(h => HiveHosts.SameMachine(h.Name, disp)
                                || h.Url == url
                                || (p.DnsName.Length > 0 && h.Hostname == p.DnsName)))
                continue;
            _hosts.Add(new HiveHost { Name = disp, Url = url, Hostname = p.DnsName, Source = "tailscale" });
            added++;
        }

        HiveHosts.MergePairedPeers(_hosts);
        HiveHosts.Dedupe(_hosts);   // collapse any same-machine LAN + Tailscale pair into one node
        HiveHosts.Save(_hosts.Where(h => h.Name != "This PC"));
        DrawConstellation();
        await (AlertAsync?.Invoke(
            peers.Count == 0
                ? "No Tailscale peers found (is Tailscale up and are other nodes online?)."
                : $"Found {peers.Count} Tailscale peer(s); added {added} TheOrc node(s)"
                  + (skipped > 0 ? $", skipped {skipped} non-TheOrc device(s)." : "."),
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

        // Log the moment the request ARRIVES, not just after the dialog resolves -- if the
        // confirm dialog is missed (not focused, appears behind another window, operator
        // away) the Activity feed previously showed nothing at all until/unless someone
        // eventually interacted with it (found 2026-06-21 alongside the matching initiator-
        // side gap in PairWithHostAsync, above).
        AddEvent($"[{DateTime.Now:HH:mm:ss}] Incoming pairing request from {req.InitiatorName} — awaiting approval…",
            new SolidColorBrush(Colors.DeepSkyBlue));

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

    /// <summary>
    /// Approval card for a role-assignment request from a peer whose AcceptControlFrom
    /// policy is Ask (HIVE_MEMBERSHIP_SPEC.md §6). Unlike pairing, there is no session/poll
    /// mechanism here -- the assigner's HTTP response already said "pending_approval" and is
    /// not waiting on this dialog's outcome; this just applies the role locally on approval.
    /// </summary>
    public async void OnRoleAssignRequest(string? assignerNodeId, HiveNodeRole newRole)
    {
        // async void: an unobserved exception here would crash the process. Catch broadly --
        // this is a UI notification handler, not a place that should ever propagate a fault
        // to the dispatcher's unhandled-exception path (grok review BLOCKER, 2026-06-21).
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(() => OnRoleAssignRequest(assignerNodeId, newRole));
                return;
            }

            assignerNodeId ??= "";
            var assignerName = HivePeerStore.Default.Find(assignerNodeId)?.Name
                ?? assignerNodeId[..Math.Min(8, assignerNodeId.Length)];
            AddEvent($"[{DateTime.Now:HH:mm:ss}] Role-assignment request from {assignerName} — requesting role: {newRole}…",
                new SolidColorBrush(Colors.DeepSkyBlue));

            var approved = await (ConfirmAsync?.Invoke(
                $"{assignerName} is requesting that this machine's role change to {newRole}.\n\nApprove?",
                "HIVE — Role Assignment Request") ?? Task.FromResult(false));

            if (approved) HiveIdentity.Load().SetSelfRole(newRole);

            var color = approved ? Brushes.LimeGreen : Brushes.OrangeRed;
            var label = approved ? $"approved — role now {newRole}" : "declined";
            AddEvent($"[{DateTime.Now:HH:mm:ss}] Role-assignment from {assignerName}: {label}", color);
        }
        catch { /* see comment above -- async void must never let an exception escape */ }
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
