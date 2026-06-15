// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Full-panel update UI: version check, inline build log, and Warchief-initiated fleet upgrades.
/// </summary>
public partial class UpdatePanel : System.Windows.Controls.UserControl
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly string DefaultSourceDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "source");

    private static readonly string StagingDir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "orc_update_staging");

    private static readonly HttpClient _http = BuildHttpClient();

    // ── Properties set by MainWindow ──────────────────────────────────────────

    public AppSettings?  Settings      { get; set; }
    public bool          IsWarchief    { get; set; }
    public string        LocalNodeId   { get; set; } = "";

    // ── State ─────────────────────────────────────────────────────────────────

    private UpdateChecker.UpdateResult? _lastResult;
    private CancellationTokenSource?    _cts;
    private bool                        _updateRunning;

    // ── Constructor ───────────────────────────────────────────────────────────

    public UpdatePanel()
    {
        InitializeComponent();
        TbInstalledVersion.Text = "v" + UpdateChecker.CurrentVersion();
    }

    // ── Called by MainWindow on tab entry ─────────────────────────────────────

    public async void Refresh()
    {
        BdrFleet.Visibility = IsWarchief ? Visibility.Visible : Visibility.Collapsed;

        SetStatus("checking…", "#888888");
        var settings = Settings ?? AppSettings.Load();
        var result   = await UpdateChecker.CheckAsync(settings, force: false);

        ApplyResult(result);

        if (IsWarchief)
            _ = RefreshFleetAsync();
    }

    // ── Version check ─────────────────────────────────────────────────────────

    private async void BtnCheckNow_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckNow.IsEnabled = false;
        SetStatus("checking…", "#888888");

        var settings = Settings ?? AppSettings.Load();
        var result   = await UpdateChecker.CheckAsync(settings, force: true);
        ApplyResult(result);

        BtnCheckNow.IsEnabled = true;
    }

    private void ApplyResult(UpdateChecker.UpdateResult? result)
    {
        _lastResult = result;

        if (result is null)
        {
            TbLatestVersion.Text    = "—";
            TbReleaseName.Text      = "Could not reach GitHub.";
            TbReleaseName.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0x66, 0x66));
            SetStatus("check failed", "#AA6666");
            BtnUpdateNode.IsEnabled = false;
            return;
        }

        TbLatestVersion.Text     = "v" + result.LatestVersion;
        TbLatestVersion.Foreground = result.UpdateAvailable
            ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

        if (result.UpdateAvailable)
        {
            TbReleaseName.Text       = result.ReleaseName;
            TbReleaseName.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
            SetStatus("update available", "#76B900");
            BtnUpdateNode.IsEnabled  = !_updateRunning;
        }
        else
        {
            TbReleaseName.Text       = "You're running the latest release.";
            TbReleaseName.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            SetStatus("up to date", "#76B900");
            BtnUpdateNode.IsEnabled  = false;
        }
    }

    // ── Local node update ─────────────────────────────────────────────────────

    private async void BtnUpdateNode_Click(object sender, RoutedEventArgs e)
    {
        if (_updateRunning) return;
        _updateRunning        = true;
        BtnUpdateNode.IsEnabled = false;
        BdrLog.Visibility     = Visibility.Visible;
        ResetSteps();
        ClearLog();

        await RunUpdateAsync();
    }

    private async void BtnCancelUpdate_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private async Task RunUpdateAsync()
    {
        _cts = new CancellationTokenSource();
        var settings = Settings ?? AppSettings.Load();

        var progress = new Progress<string>(msg => Dispatcher.Invoke(() => AppendLog(msg)));
        var updater  = new SelfUpdater { CancellationToken = _cts.Token };

        try
        {
            // ── Step 0: Check for pre-built asset first ──────────────────────
            ActivateStep(0);
            var assetUrl = await UpdateChecker.GetReleaseAssetUrlAsync();
            string? exePath = null;

            if (!string.IsNullOrEmpty(assetUrl))
            {
                AppendLog("Pre-built release asset found — downloading…");
                CompleteStep(0);

                // Steps 1–2 not needed for direct download; skip visually
                CompleteStep(1);
                ActivateStep(2);
                exePath = await updater.DownloadReleaseAsync(assetUrl, StagingDir, progress);
                CompleteStep(2);
            }
            else
            {
                // Fall back: build from source
                AppendLog("No pre-built asset found — building from source.");
                var hasSdk = await updater.CheckDotNetSdkAsync();

                if (!hasSdk)
                {
                    var answer = MessageBox.Show(
                        ".NET 10 SDK not found. It's required to build from source.\n\nDownload and install now?",
                        ".NET 10 SDK Required",
                        MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

                    if (answer != MessageBoxResult.Yes)
                    {
                        AppendLog("❌ Update cancelled — .NET 10 SDK not installed.");
                        ResetForRetry(); return;
                    }

                    await updater.InstallDotNetSdkAsync(progress);
                }
                else
                {
                    AppendLog("✓ .NET 10 SDK detected.");
                }
                CompleteStep(0);

                ActivateStep(1);
                var sourceDir = string.IsNullOrEmpty(settings.SourceFolderPath)
                    ? DefaultSourceDir : settings.SourceFolderPath;
                await updater.PullSourceAsync(sourceDir, progress);
                CompleteStep(1);

                ActivateStep(2);
                await updater.BuildAndPublishAsync(sourceDir, StagingDir, progress);
                CompleteStep(2);
            }

            // ── Step 3: Verify staged exe ────────────────────────────────────
            ActivateStep(3);
            exePath ??= System.IO.Path.Combine(StagingDir, "OrchestratorIDE.exe");

            if (!System.IO.File.Exists(exePath))
                throw new System.IO.FileNotFoundException($"Staged exe not found: {exePath}");

            AppendLog($"✓ Staged: {exePath}");
            CompleteStep(3);

            // ── Step 4: Relaunch ─────────────────────────────────────────────
            ActivateStep(4);
            AppendLog("Launching update script — TheOrc will restart in a moment…");
            updater.PrepareRelaunch(exePath);
            CompleteStep(4);

            SetStatus("relaunching…", "#76B900");
            UpdateProgress.Value = 100;

            await Task.Delay(1800, _cts.Token);
            Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (OperationCanceledException)
        {
            AppendLog("Update cancelled.");
            ResetForRetry();
        }
        catch (Exception ex)
        {
            AppendLog($"❌ {ex.Message}");
            if (ex.InnerException != null)
                AppendLog($"   {ex.InnerException.Message}");
            SetStatus("update failed", "#F44747");
            ResetForRetry();
        }
    }

    // ── Fleet version check ───────────────────────────────────────────────────

    private async void BtnRefreshFleet_Click(object sender, RoutedEventArgs e)
        => await RefreshFleetAsync();

    private async Task RefreshFleetAsync()
    {
        TbFleetStatus.Text = "probing nodes…";
        BtnDeployFleet.IsEnabled = false;

        var peers = HivePeerStore.Default.All();
        var rows  = new List<FleetRow>();
        var latestVer = _lastResult?.LatestVersion ?? "";
        bool anyOutdated = false;

        foreach (var peer in peers)
        {
            var isLocal = peer.NodeId == LocalNodeId;
            var row = new FleetRow
            {
                NodeId      = peer.NodeId,
                DisplayName = (peer.NodeId == LocalNodeId ? "★ " : "") + peer.Name,
                NameColor   = new SolidColorBrush(isLocal
                    ? Color.FromRgb(0x76, 0xB9, 0x00)
                    : Color.FromRgb(0xD4, 0xD4, 0xD4)),
                Address     = peer.LastKnownAddress,
            };

            if (isLocal)
            {
                row.Version    = "v" + UpdateChecker.CurrentVersion();
                row.StatusText = string.IsNullOrEmpty(latestVer) ? "—"
                    : (row.Version.TrimStart('v') == latestVer ? "current" : "update avail");
                row.StatusColor = new SolidColorBrush(row.StatusText == "current"
                    ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xCC, 0xA7, 0x00));
            }
            else
            {
                row.Version    = "…";
                row.StatusText = "probing";
                row.StatusColor = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            }

            rows.Add(row);
        }

        IcFleet.ItemsSource = rows;

        // Probe each non-local peer in parallel
        var tasks = rows
            .Where(r => r.NodeId != LocalNodeId && !string.IsNullOrEmpty(r.Address))
            .Select(async row =>
            {
                var host = row.Address.Split(':')[0];
                var ver  = await ProbeNodeVersionAsync(host);
                Dispatcher.Invoke(() =>
                {
                    row.Version    = ver ?? "?";
                    row.StatusText = ver is null ? "unreachable"
                        : (string.IsNullOrEmpty(latestVer) ? "—"
                        : (ver.TrimStart('v') == latestVer ? "current" : "update avail"));
                    row.StatusColor = new SolidColorBrush(
                        ver is null             ? Color.FromRgb(0x55, 0x55, 0x55) :
                        row.StatusText == "current" ? Color.FromRgb(0x55, 0x55, 0x55) :
                                                  Color.FromRgb(0xCC, 0xA7, 0x00));
                    IcFleet.Items.Refresh();
                });
            });

        await Task.WhenAll(tasks);

        anyOutdated = rows.Any(r => r.StatusText == "update avail");
        TbFleetStatus.Text = $"— {rows.Count} node{(rows.Count == 1 ? "" : "s")}";
        BtnDeployFleet.IsEnabled = anyOutdated && _lastResult?.UpdateAvailable == true;
    }

    private static async Task<string?> ProbeNodeVersionAsync(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var json = await _http.GetStringAsync(
                $"http://{host}:{HiveNodeServer.ApiPort}/hive/update/version", cts.Token);
            var doc  = System.Text.Json.JsonDocument.Parse(json);
            var ver  = doc.RootElement.GetProperty("version").GetString() ?? "?";
            return "v" + ver;
        }
        catch { return null; }
    }

    // ── Fleet deploy ──────────────────────────────────────────────────────────

    private async void BtnDeployFleet_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Send an update command to all outdated fleet nodes?\n\n" +
            "Each node will update itself and restart automatically.",
            "Deploy to Fleet",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (answer != MessageBoxResult.Yes) return;

        BtnDeployFleet.IsEnabled = false;
        TbFleetStatus.Text = "deploying…";

        var rows = IcFleet.ItemsSource as List<FleetRow> ?? [];
        var identity = HiveIdentity.Load();

        foreach (var row in rows.Where(r => r.StatusText == "update avail" && r.NodeId != LocalNodeId))
        {
            var host    = row.Address.Split(':')[0];
            var peer    = HivePeerStore.Default.Find(row.NodeId);
            if (peer is null) continue;

            var secret = HivePeerStore.Default.GetSharedSecret(row.NodeId);
            if (secret is null) continue;

            try
            {
                var req  = new HttpRequestMessage(HttpMethod.Post,
                    $"http://{host}:{HiveNodeServer.ApiPort}/hive/update/deploy")
                {
                    Content = new ByteArrayContent([]),
                };
                req.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                HiveAuthMiddleware.SignRequest(req, [], identity.NodeId, secret);

                using var resp = await _http.SendAsync(req,
                    new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                row.StatusText  = resp.IsSuccessStatusCode ? "deploying…" : $"err {(int)resp.StatusCode}";
                row.StatusColor = new SolidColorBrush(resp.IsSuccessStatusCode
                    ? Color.FromRgb(0x76, 0xB9, 0x00) : Color.FromRgb(0xF4, 0x47, 0x47));
            }
            catch
            {
                row.StatusText  = "unreachable";
                row.StatusColor = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            }
        }

        IcFleet.Items.Refresh();
        TbFleetStatus.Text = "deploy sent";
    }

    // ── Step visual helpers ───────────────────────────────────────────────────

    private void ActivateStep(int idx)
    {
        Dispatcher.Invoke(() =>
        {
            var (bdr, icon) = GetStep(idx);
            bdr.Background  = new SolidColorBrush(Color.FromArgb(0x22, 0x76, 0xB9, 0x00));
            icon.Text       = "▶";
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
            UpdateProgress.Value = (idx * 100) / 5;
        });
    }

    private void CompleteStep(int idx)
    {
        Dispatcher.Invoke(() =>
        {
            var (bdr, icon) = GetStep(idx);
            bdr.Background  = Brushes.Transparent;
            icon.Text       = "✓";
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
            UpdateProgress.Value = ((idx + 1) * 100) / 5;
        });
    }

    private (Border border, TextBlock icon) GetStep(int idx) => idx switch
    {
        0 => (StepBorder0, StepIcon0),
        1 => (StepBorder1, StepIcon1),
        2 => (StepBorder2, StepIcon2),
        3 => (StepBorder3, StepIcon3),
        4 => (StepBorder4, StepIcon4),
        _ => throw new ArgumentOutOfRangeException(nameof(idx)),
    };

    private void ResetSteps()
    {
        for (int i = 0; i < 5; i++)
        {
            var (bdr, icon) = GetStep(i);
            bdr.Background  = Brushes.Transparent;
            icon.Text       = "○";
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        }
        UpdateProgress.Value = 0;
    }

    private void ResetForRetry()
    {
        Dispatcher.Invoke(() =>
        {
            _updateRunning          = false;
            BtnUpdateNode.IsEnabled = _lastResult?.UpdateAvailable == true;
            BtnCancelUpdate.Content = "✕ Cancel";
        });
    }

    // ── Log helpers ───────────────────────────────────────────────────────────

    private void AppendLog(string msg)
    {
        TbLog.AppendText(msg + "\n");
        TbLog.ScrollToEnd();
    }

    private void ClearLog() => TbLog.Clear();

    // ── Status chip ───────────────────────────────────────────────────────────

    private void SetStatus(string text, string hex)
    {
        Dispatcher.Invoke(() =>
        {
            TbStatusChip.Text       = text;
            TbStatusChip.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));
        });
    }

    // ── Statics ───────────────────────────────────────────────────────────────

    private static HttpClient BuildHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("TheOrc/update");
        return c;
    }
}

// ── Fleet row view-model ──────────────────────────────────────────────────────

internal sealed class FleetRow
{
    public string NodeId      { get; set; } = "";
    public string Address     { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version     { get; set; } = "…";
    public string StatusText  { get; set; } = "";
    public Brush  NameColor   { get; set; } = Brushes.White;
    public Brush  StatusColor { get; set; } = Brushes.Gray;
}
