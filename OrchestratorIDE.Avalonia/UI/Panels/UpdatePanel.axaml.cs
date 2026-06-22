// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Full-panel update UI: version check, inline build log, and Warchief-initiated fleet upgrades.
/// </summary>
public partial class UpdatePanel : UserControl
{
    private static readonly string DefaultSourceDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "source");

    private static readonly string StagingDir = Path.Combine(
        Path.GetTempPath(), "orc_update_staging");

    private static readonly HttpClient _http = BuildHttpClient();

    // ── Properties set by MainWindow ─────────────────────────────────────────

    public AppSettings? Settings    { get; set; }
    public bool         IsWarchief  { get; set; }
    public string       LocalNodeId { get; set; } = "";

    /// <summary>Wired by MainWindow to show a yes/no confirmation dialog.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────

    private UpdateChecker.UpdateResult?          _lastResult;
    private CancellationTokenSource?             _cts;
    private bool                                 _updateRunning;
    private readonly ObservableCollection<FleetRow> _fleetRows = [];

    private const int MaxLogLines = 1000;
    private readonly List<string> _logLines = [];

    // ── Constructor ───────────────────────────────────────────────────────────

    public UpdatePanel()
    {
        InitializeComponent();
        TbInstalledVersion.Text = "v" + UpdateChecker.CurrentVersion();
        IcFleet.ItemsSource = _fleetRows;
    }

    // ── Called by MainWindow on tab entry ─────────────────────────────────────

    public async void Refresh()
    {
        BdrFleet.IsVisible = IsWarchief;
        SetStatus("checking…", "#888888");
        try
        {
            var settings = Settings ?? AppSettings.Load();
            var result   = await UpdateChecker.CheckAsync(settings, force: false);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            SetStatus($"check failed: {ex.Message}", "#AA4444");
        }

        if (IsWarchief)
            _ = RefreshFleetAsync().ContinueWith(
                t => Dispatcher.UIThread.InvokeAsync(() => SetStatus($"fleet refresh error: {t.Exception?.GetBaseException().Message}", "#AA4444")),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
    }

    // ── Version check ─────────────────────────────────────────────────────────

    private async void BtnCheckNow_Click(object? sender, RoutedEventArgs e)
    {
        BtnCheckNow.IsEnabled = false;
        SetStatus("checking…", "#888888");
        try
        {
            var settings = Settings ?? AppSettings.Load();
            var result   = await UpdateChecker.CheckAsync(settings, force: true);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            SetStatus($"check failed: {ex.Message}", "#AA4444");
        }
        finally
        {
            BtnCheckNow.IsEnabled = true;
        }
    }

    private void ApplyResult(UpdateChecker.UpdateResult? result)
    {
        _lastResult = result;

        if (result is null)
        {
            TbLatestVersion.Text     = "—";
            TbReleaseName.Text       = "Could not reach GitHub.";
            TbReleaseName.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0x66, 0x66));
            SetStatus("check failed", "#AA6666");
            BtnUpdateNode.IsEnabled  = false;
            return;
        }

        TbLatestVersion.Text      = "v" + result.LatestVersion;
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

    private async void BtnUpdateNode_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateRunning) return;
        _updateRunning          = true;
        BtnUpdateNode.IsEnabled = false;
        BdrLog.IsVisible        = true;
        ResetSteps();
        ClearLog();

        await RunUpdateAsync();
    }

    private void BtnCancelUpdate_Click(object? sender, RoutedEventArgs e)
        => _cts?.Cancel();

    private async Task RunUpdateAsync()
    {
        _cts = new CancellationTokenSource();
        var settings = Settings ?? AppSettings.Load();

        var progress = new Progress<string>(msg =>
            Dispatcher.UIThread.Post(() => AppendLog(msg)));

        var updater = new SelfUpdater { CancellationToken = _cts.Token };

        try
        {
            ActivateStep(0);
            var assetUrl = await UpdateChecker.GetReleaseAssetUrlAsync();
            string? exePath = null;

            if (!string.IsNullOrEmpty(assetUrl))
            {
                AppendLog("Pre-built release asset found — downloading…");
                CompleteStep(0);
                CompleteStep(1);
                ActivateStep(2);
                exePath = await updater.DownloadReleaseAsync(assetUrl, StagingDir, progress);
                CompleteStep(2);
            }
            else
            {
                AppendLog("No pre-built asset found — building from source.");
                var hasSdk = await updater.CheckDotNetSdkAsync();

                if (!hasSdk)
                {
                    var confirm = ConfirmAsync != null
                        && await ConfirmAsync(
                            ".NET 10 SDK not found. It's required to build from source.\n\nDownload and install now?",
                            ".NET 10 SDK Required");

                    if (!confirm)
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

            ActivateStep(3);
            exePath ??= Path.Combine(
                StagingDir, OperatingSystem.IsWindows() ? "OrchestratorIDE.exe" : "OrchestratorIDE");

            if (!File.Exists(exePath))
                throw new FileNotFoundException($"Staged exe not found: {exePath}");

            AppendLog($"✓ Staged: {exePath}");
            CompleteStep(3);

            ActivateStep(4);
            AppendLog("Launching update script — TheOrc will restart in a moment…");
            updater.PrepareRelaunch(exePath);
            CompleteStep(4);

            SetStatus("relaunching…", "#76B900");
            UpdateProgress.Value = 100;

            await Task.Delay(1800, _cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime life)
                    life.Shutdown();
            });
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

    private async void BtnRefreshFleet_Click(object? sender, RoutedEventArgs e)
        => await RefreshFleetAsync();

    private async Task RefreshFleetAsync()
    {
        TbFleetStatus.Text       = "probing nodes…";
        BtnDeployFleet.IsEnabled = false;

        var peers     = HivePeerStore.Default.All();
        var rows      = new List<FleetRow>();
        var latestVer = _lastResult?.LatestVersion ?? "";

        foreach (var peer in peers)
        {
            var isLocal = peer.NodeId == LocalNodeId;
            var row = new FleetRow
            {
                NodeId      = peer.NodeId,
                DisplayName = (isLocal ? "★ " : "") + peer.Name,
                NameColor   = new SolidColorBrush(isLocal
                    ? Color.FromRgb(0x76, 0xB9, 0x00)
                    : Color.FromRgb(0xD4, 0xD4, 0xD4)),
                Address     = peer.LastKnownAddress,
            };

            if (isLocal)
            {
                row.Version     = "v" + UpdateChecker.CurrentVersion();
                row.StatusText  = string.IsNullOrEmpty(latestVer) ? "—"
                    : (row.Version.TrimStart('v') == latestVer ? "current" : "update avail");
                row.StatusColor = new SolidColorBrush(row.StatusText == "current"
                    ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xCC, 0xA7, 0x00));
            }
            else
            {
                row.Version     = "…";
                row.StatusText  = "probing";
                row.StatusColor = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            }

            rows.Add(row);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _fleetRows.Clear();
            foreach (var r in rows) _fleetRows.Add(r);
        });

        var tasks = rows
            .Where(r => r.NodeId != LocalNodeId && !string.IsNullOrEmpty(r.Address))
            .Select(async row =>
            {
                var host = row.Address.Split(':')[0];
                var ver  = await ProbeNodeVersionAsync(host);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    row.Version    = ver ?? "?";
                    row.StatusText = ver is null ? "unreachable"
                        : (string.IsNullOrEmpty(latestVer) ? "—"
                        : (ver.TrimStart('v') == latestVer ? "current" : "update avail"));
                    row.StatusColor = new SolidColorBrush(
                        ver is null                     ? Color.FromRgb(0x55, 0x55, 0x55) :
                        row.StatusText == "current"     ? Color.FromRgb(0x55, 0x55, 0x55) :
                                                          Color.FromRgb(0xCC, 0xA7, 0x00));
                });
            });

        await Task.WhenAll(tasks);

        bool anyOutdated = rows.Any(r => r.StatusText == "update avail");
        TbFleetStatus.Text       = $"— {rows.Count} node{(rows.Count == 1 ? "" : "s")}";
        BtnDeployFleet.IsEnabled = anyOutdated && _lastResult?.UpdateAvailable == true;
    }

    private static async Task<string?> ProbeNodeVersionAsync(string host)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var json = await _http.GetStringAsync(
                $"http://{host}:{HiveNodeServer.ApiPort}/hive/update/version", cts.Token);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var ver       = doc.RootElement.GetProperty("version").GetString() ?? "?";
            return "v" + ver;
        }
        catch { return null; }
    }

    // ── Fleet deploy ──────────────────────────────────────────────────────────

    private async void BtnDeployFleet_Click(object? sender, RoutedEventArgs e)
    {
        var confirmed = ConfirmAsync != null
            && await ConfirmAsync(
                "Send an update command to all outdated fleet nodes?\n\n" +
                "Each node will update itself and restart automatically.",
                "Deploy to Fleet");

        if (!confirmed) return;

        BtnDeployFleet.IsEnabled = false;
        TbFleetStatus.Text = "deploying…";

        var identity = HiveIdentity.Load();

        foreach (var row in _fleetRows.Where(r => r.StatusText == "update avail" && r.NodeId != LocalNodeId))
        {
            var host   = row.Address.Split(':')[0];
            var secret = HivePeerStore.Default.GetSharedSecret(row.NodeId);
            if (secret is null) continue;

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post,
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

        TbFleetStatus.Text = "deploy sent";
    }

    // ── Step visual helpers ───────────────────────────────────────────────────

    private void ActivateStep(int idx) => Dispatcher.UIThread.Post(() =>
    {
        var (bdr, icon) = GetStep(idx);
        bdr.Background  = new SolidColorBrush(Color.FromArgb(0x22, 0x76, 0xB9, 0x00));
        icon.Text       = "▶";
        icon.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
        UpdateProgress.Value = (idx * 100) / 5;
    });

    private void CompleteStep(int idx) => Dispatcher.UIThread.Post(() =>
    {
        var (bdr, icon) = GetStep(idx);
        bdr.Background  = Brushes.Transparent;
        icon.Text       = "✓";
        icon.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
        UpdateProgress.Value = ((idx + 1) * 100) / 5;
    });

    private (Border border, TextBlock icon) GetStep(int idx) => idx switch
    {
        0 => (StepBorder0, StepIcon0),
        1 => (StepBorder1, StepIcon1),
        2 => (StepBorder2, StepIcon2),
        3 => (StepBorder3, StepIcon3),
        4 => (StepBorder4, StepIcon4),
        _ => throw new ArgumentOutOfRangeException(nameof(idx)),
    };

    private void ResetSteps() => Dispatcher.UIThread.Post(() =>
    {
        for (int i = 0; i < 5; i++)
        {
            var (bdr, icon) = GetStep(i);
            bdr.Background  = Brushes.Transparent;
            icon.Text       = "○";
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        }
        UpdateProgress.Value = 0;
    });

    private void ResetForRetry() => Dispatcher.UIThread.Post(() =>
    {
        _updateRunning          = false;
        BtnUpdateNode.IsEnabled = _lastResult?.UpdateAvailable == true;
    });

    // ── Log helpers ───────────────────────────────────────────────────────────

    private void AppendLog(string msg)
    {
        _logLines.Add(msg);
        if (_logLines.Count > MaxLogLines)
            _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);

        TbLog.Text = string.Join("\n", _logLines) + "\n";

        // Scroll to bottom after layout pass
        Dispatcher.UIThread.Post(
            () => SvLog.Offset = new Avalonia.Vector(SvLog.Offset.X, double.MaxValue),
            DispatcherPriority.Render);
    }

    private void ClearLog()
    {
        _logLines.Clear();
        TbLog.Text = "";
    }

    // ── Status chip ───────────────────────────────────────────────────────────

    private void SetStatus(string text, string hex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TbStatusChip.Text       = text;
            TbStatusChip.Foreground = new SolidColorBrush(Color.Parse(hex));
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

public sealed class FleetRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string NodeId  { get; set; } = "";
    public string Address { get; set; } = "";

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set { _displayName = value; OnPropChanged(nameof(DisplayName)); }
    }

    private string _version = "…";
    public string Version
    {
        get => _version;
        set { _version = value; OnPropChanged(nameof(Version)); }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropChanged(nameof(StatusText)); }
    }

    private IBrush _nameColor = Brushes.White;
    public IBrush NameColor
    {
        get => _nameColor;
        set { _nameColor = value; OnPropChanged(nameof(NameColor)); }
    }

    private IBrush _statusColor = Brushes.Gray;
    public IBrush StatusColor
    {
        get => _statusColor;
        set { _statusColor = value; OnPropChanged(nameof(StatusColor)); }
    }
}
