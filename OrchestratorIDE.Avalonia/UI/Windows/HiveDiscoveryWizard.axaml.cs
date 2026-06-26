// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.UI;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// HIVE_MEMBERSHIP_SPEC.md §7 — first-run/repair discovery wizard. Scans the LAN via
/// HiveBeacon.ScanAsync, groups results by HiveId, and lets the operator either join an
/// existing hive (normal pairing ceremony against a reachable node from that group, with the
/// same fingerprint-confirm step PairWithHostAsync uses) or found a new one (purely local --
/// no network call). A flat single-screen Window shown via ShowDialog&lt;bool&gt; (true =
/// HiveId changed, so the caller should refresh this node's beacon payload), matching the
/// established FirstRunWindow pattern rather than building new paged-wizard infrastructure
/// for one screen.
///
/// Three call sites per the spec's trigger conditions (§7.1): first HIVE start with
/// HiveRole.Unset (MainWindow.InitializeAsync, sequenced after first-run), manual "Repair
/// HIVE association" (HivePanel context menu), and a hiveid_mismatch pairing failure
/// (HivePanel.PairWithHostAsync) -- the latter two pass a non-null <paramref name="reason"/>
/// explaining why the wizard reopened, so it doesn't look like an unprompted nag.
///
/// All event handlers are <c>async void</c> (the Avalonia event signature), so each wraps its
/// body in try/catch -- an unobserved exception in an async void handler crashes the process.
/// A <c>_busy</c> guard prevents overlapping scan/join operations and keeps button enabled
/// state consistent across every exit path.
/// </summary>
public partial class HiveDiscoveryWizard : Window
{
    private sealed record HiveGroup(string HiveId, List<HiveBeaconMessage> Nodes);

    private List<HiveGroup> _groups = [];
    private HiveGroup? _selected;
    private bool _busy;

    public HiveDiscoveryWizard(string? reason = null)
    {
        InitializeComponent();

        // Diagnostic: show THIS machine's current hive so a mismatch is visible up front (the
        // wizard used to hide it, so an operator couldn't see why a Join was being refused).
        var id   = Services.Hive.HiveIdentity.Load();
        var self = id.HiveRole == Services.Hive.HiveRole.Unset
            ? "This machine hasn't joined a hive yet — Join any hive below, or Create one."
            : $"This machine is in hive {Short(id.HiveId)} ({id.HiveRole}). Joining a different hive " +
              "will prompt you to leave this one first.";
        TbReason.Text = string.IsNullOrEmpty(reason) ? self : reason + "\n" + self;

        Opened += async (_, _) => await SafeRescanAsync();
    }

    private static string Short(string id) => string.IsNullOrEmpty(id) ? "—" : (id.Length > 8 ? id[..8] : id);

    private async void BtnRescan_Click(object? sender, RoutedEventArgs e) => await SafeRescanAsync();

    private async System.Threading.Tasks.Task SafeRescanAsync()
    {
        if (_busy) return;
        _busy = true;
        BtnRescan.IsEnabled = false;
        BtnJoin.IsEnabled = false;
        try
        {
            await RescanAsync();
        }
        catch (Exception ex)
        {
            TbStatus.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _busy = false;
            BtnRescan.IsEnabled = true;
            // BtnJoin re-enabled only if a joinable group is still selected.
            BtnJoin.IsEnabled = _selected is not null && !string.IsNullOrEmpty(_selected.HiveId);
        }
    }

    private async System.Threading.Tasks.Task RescanAsync()
    {
        TbStatus.Text = "Scanning for nearby hives…";
        LstHives.ItemsSource = null;
        _selected = null;

        var results = await HiveBeacon.ScanAsync();

        // Group by HiveId -- "" (HiveRole.Unset senders) get their own informational bucket,
        // not silently dropped, since seeing "3 other un-joined installs nearby" is itself
        // useful context for the operator deciding whether to found or wait.
        _groups = [.. results
            .GroupBy(m => m.HiveId)
            .Select(g => new HiveGroup(g.Key, [.. g]))
            .OrderByDescending(g => !string.IsNullOrEmpty(g.HiveId)) // real hives first
            .ThenByDescending(g => g.Nodes.Count)];

        if (_groups.Count == 0)
        {
            TbStatus.Text = "No TheOrc nodes found on this network.";
        }
        else
        {
            var hiveCount = _groups.Count(g => !string.IsNullOrEmpty(g.HiveId));
            TbStatus.Text = hiveCount > 0
                ? $"Found {hiveCount} hive(s)."
                : "Found nodes, but none have founded a hive yet.";
        }

        LstHives.ItemsSource = _groups.Select(DescribeGroup).ToList();
    }

    private static string DescribeGroup(HiveGroup g)
    {
        var names = string.Join(", ", g.Nodes.Select(n => n.Name).Take(5));
        var more  = g.Nodes.Count > 5 ? $" (+{g.Nodes.Count - 5} more)" : "";
        // Math.Min guards a HiveId shorter than 8 chars (always a GUID today, but defensive).
        return string.IsNullOrEmpty(g.HiveId)
            ? $"(no hive yet) — {g.Nodes.Count} node(s): {names}{more}"
            : $"Hive {g.HiveId[..Math.Min(8, g.HiveId.Length)]}… — {g.Nodes.Count} node(s): {names}{more}";
    }

    private void LstHives_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = LstHives.SelectedIndex;
        _selected = idx >= 0 && idx < _groups.Count ? _groups[idx] : null;
        // Joining a "no hive yet" bucket makes no sense -- those nodes have nothing to join
        // either. Only enable Join for a group that actually has a HiveId, and never mid-scan.
        BtnJoin.IsEnabled = !_busy && _selected is not null && !string.IsNullOrEmpty(_selected.HiveId);
    }

    /// <summary>
    /// Joins the selected hive via the normal pairing ceremony against the first reachable
    /// node in that group -- same fingerprint-confirm flow as HivePanel.PairWithHostAsync,
    /// duplicated rather than shared because that method is UI-Panel-shaped (uses the
    /// ConfirmAsync/AlertAsync delegate properties HivePanel exposes for MainWindow to wire
    /// up); this Window shows its own dialogs directly via DialogHelper.
    /// </summary>
    private async void BtnJoin_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy || _selected is null || _selected.Nodes.Count == 0) return;

        var target = _selected.Nodes[0];
        string targetHost;
        try { targetHost = new Uri(target.OllamaUrl).Host; }
        catch
        {
            await DialogHelper.ShowInfoAsync(this, "HIVE MIND — Join",
                $"Could not parse an address for {target.Name} ({target.OllamaUrl}).");
            return;
        }

        _busy = true;
        BtnJoin.IsEnabled = false;
        BtnRescan.IsEnabled = false;
        var joined = false;
        try
        {
            // If THIS machine already belongs to a DIFFERENT hive, the join would hit the §4.3
            // mismatch refusal and there'd be no way forward. Offer to leave the current hive
            // first (keeps keys + paired peers) so the join can adopt the target hive. This is
            // the deadlock the repair wizard exists to break — a node that founded its own hive
            // could otherwise never join another.
            var localHiveId = Services.Hive.HiveIdentity.Load().HiveId;
            if (!string.IsNullOrEmpty(localHiveId) && !string.IsNullOrEmpty(_selected.HiveId)
                && !string.Equals(localHiveId, _selected.HiveId, StringComparison.OrdinalIgnoreCase))
            {
                var leave = await DialogHelper.ShowYesNoAsync(this, "HIVE MIND — Leave current hive?",
                    $"This machine already belongs to a different hive (id {Short(localHiveId)}).\n\n" +
                    $"To join {target.Name}'s hive, this machine must first LEAVE its current hive. " +
                    "Your machine's keys and paired peers are kept — only the hive membership is " +
                    "reset, and you re-join by pairing below.\n\nLeave the current hive and join?");
                if (!leave)
                {
                    TbStatus.Text = "Join cancelled — still in the current hive.";
                    return;
                }
                Services.Hive.HiveIdentity.Load().LeaveHive();
                TbStatus.Text = "Left the current hive. Joining…";
            }

            TbStatus.Text = $"Sending pairing request to {target.Name} ({targetHost})…";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(125));
            var result = await HivePairingClient.PairAsync(targetHost, ct: cts.Token);

            if (result.Outcome == HivePairingClient.Outcome.Approved && result.Pending is { } pending)
            {
                // Trust is NOT yet persisted at this point -- same fail-closed fingerprint
                // comparison as HivePanel.PairWithHostAsync, for the same reason (the
                // unauthenticated GET /hive/pair/{sessionId} poll is the only place an on-path
                // attacker's forged "approved" response would be caught).
                var confirmed = await DialogHelper.ShowYesNoAsync(this, "HIVE MIND — Verify Fingerprint",
                    $"{target.Name} approved the pairing request.\n\n" +
                    $"Fingerprint: {pending.Fingerprint}\n\n" +
                    "Compare this against the fingerprint shown on the OTHER machine's own HIVE " +
                    "panel (click \"This PC\" there) before continuing. Do they match?");

                if (confirmed)
                {
                    HivePairingClient.ConfirmAndTrust(pending);
                    TbStatus.Text = $"✓ Joined the hive via {target.Name}.";
                    await DialogHelper.ShowInfoAsync(this, "HIVE MIND — Join", $"✓ Paired with {target.Name}.");
                    joined = true;
                    Close(true);
                    return;
                }

                TbStatus.Text = "Pairing not completed — fingerprint mismatch (or unconfirmed).";
                await DialogHelper.ShowInfoAsync(this, "HIVE MIND — Join",
                    "Pairing not completed — fingerprint mismatch (or unconfirmed). No peer was trusted.");
                return;
            }

            var msg = result.Outcome switch
            {
                HivePairingClient.Outcome.Rejected     => $"{target.Name} rejected the pairing request.",
                HivePairingClient.Outcome.Expired      => "The pairing request expired before it was answered.",
                HivePairingClient.Outcome.TimedOut     => "Timed out waiting for a response.",
                HivePairingClient.Outcome.AlreadyPaired => $"Already paired with {target.Name}.",
                _                                        => $"Pairing failed: {result.Message}",
            };
            TbStatus.Text = msg;
            await DialogHelper.ShowInfoAsync(this, "HIVE MIND — Join", msg);
        }
        catch (Exception ex)
        {
            TbStatus.Text = $"Join failed: {ex.Message}";
        }
        finally
        {
            // Always restore interactivity unless we already closed on success -- otherwise an
            // error path (or an exception mid-flow) would leave Join permanently greyed out.
            if (!joined)
            {
                _busy = false;
                BtnRescan.IsEnabled = true;
                BtnJoin.IsEnabled = _selected is not null && !string.IsNullOrEmpty(_selected.HiveId);
            }
        }
    }

    /// <summary>Founds a new hive -- purely local, no network call (HIVE_MEMBERSHIP_SPEC.md §4.2).</summary>
    private async void BtnCreate_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var identity = HiveIdentity.Load();
            if (!string.IsNullOrEmpty(identity.HiveId))
            {
                await DialogHelper.ShowInfoAsync(this, "HIVE MIND — Create",
                    "This machine already belongs to a hive — creating a new one isn't supported from here. " +
                    "Use \"Repair HIVE association\" if you need to start over.");
                return;
            }

            identity.SetHive(Guid.NewGuid().ToString(), HiveRole.Founder);
            Close(true);
        }
        catch (Exception ex)
        {
            TbStatus.Text = $"Could not create hive: {ex.Message}";
        }
    }

    private void BtnSkip_Click(object? sender, RoutedEventArgs e) => Close(false);
}
