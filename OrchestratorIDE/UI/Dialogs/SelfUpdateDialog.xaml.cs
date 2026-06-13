using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services;

namespace OrchestratorIDE.UI.Dialogs;

/// <summary>
/// Step-by-step progress dialog for the self-update flow:
///   Check .NET SDK → Pull Source → Build → Stage → Relaunch
/// The dialog closes (and shuts down the app) when done.
/// </summary>
public partial class SelfUpdateDialog : Window
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly string DefaultSourceDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "source");

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly AppSettings _settings;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _relaunchPending;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SelfUpdateDialog(AppSettings settings, string latestVersion)
    {
        _settings = settings;
        InitializeComponent();
        TbVersionInfo.Text =
            $"Installed: v{UpdateChecker.CurrentVersion()}   →   Latest: v{latestVersion}";

        var sourceDir = string.IsNullOrEmpty(settings.SourceFolderPath)
            ? DefaultSourceDir
            : settings.SourceFolderPath;
        AppendLog($"Source folder : {sourceDir}");
        AppendLog($"Build output  : {StagingDir()}");
        AppendLog("");
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        BtnStart.IsEnabled = false;
        await RunUpdateAsync();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_relaunchPending)
        {
            // Already done — just close (script will still relaunch)
            Application.Current.Shutdown();
            return;
        }

        _cts?.Cancel();
        Close();
    }

    // ── Update flow ───────────────────────────────────────────────────────────

    private async Task RunUpdateAsync()
    {
        _cts = new CancellationTokenSource();

        var sourceDir = string.IsNullOrEmpty(_settings.SourceFolderPath)
            ? DefaultSourceDir
            : _settings.SourceFolderPath;
        var stagingDir = StagingDir();
        var progress   = new Progress<string>(msg => Dispatcher.Invoke(() => AppendLog(msg)));

        var updater = new SelfUpdater { CancellationToken = _cts.Token };

        try
        {
            // ── Step 0: .NET SDK ───────────────────────────────────────────
            ActivateStep(0);
            var hasSdk = await updater.CheckDotNetSdkAsync();

            if (!hasSdk)
            {
                var answer = MessageBox.Show(
                    ".NET 10 SDK not found on this machine.\n\n" +
                    "It is required to build TheOrc from source.\n\n" +
                    "Download and install it now?\n" +
                    "(~200 MB, internet required, may need admin rights)",
                    ".NET 10 SDK Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes);

                if (answer != MessageBoxResult.Yes)
                {
                    AppendLog("❌ Update cancelled — .NET 10 SDK not installed.");
                    ResetForRetry();
                    return;
                }

                await updater.InstallDotNetSdkAsync(progress);
            }
            else
            {
                AppendLog("✓ .NET 10 SDK detected.");
            }

            CompleteStep(0);

            // ── Step 1: Pull source ────────────────────────────────────────
            ActivateStep(1);
            await updater.PullSourceAsync(sourceDir, progress);
            CompleteStep(1);

            // ── Step 2: Build & publish ────────────────────────────────────
            ActivateStep(2);
            await updater.BuildAndPublishAsync(sourceDir, stagingDir, progress);
            CompleteStep(2);

            // ── Step 3: Verify staged exe ──────────────────────────────────
            ActivateStep(3);
            var newExe = Path.Combine(stagingDir, "OrchestratorIDE.exe");
            if (!File.Exists(newExe))
                throw new FileNotFoundException(
                    $"Published exe not found at: {newExe}\n" +
                    "The build may have written to a sub-folder — check the log.");

            AppendLog($"✓ Staged: {newExe}");
            CompleteStep(3);

            // ── Step 4: Relaunch ───────────────────────────────────────────
            ActivateStep(4);
            AppendLog("Launching update script — TheOrc will restart in a moment…");
            updater.PrepareRelaunch(newExe);
            CompleteStep(4);

            _relaunchPending = true;
            Dispatcher.Invoke(() =>
            {
                TbFinalStatus.Text       = "Update ready — closing and relaunching…";
                TbFinalStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
                BtnCancel.Content        = "Close Now";
                UpdateProgress.Value     = 100;
            });

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
            Dispatcher.Invoke(() =>
            {
                TbFinalStatus.Text       = "Update failed — check log above.";
                TbFinalStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
            });
            ResetForRetry();
        }
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

    // ── Log helpers ───────────────────────────────────────────────────────────

    private void AppendLog(string msg)
    {
        TbLog.AppendText(msg + "\n");
        TbLog.ScrollToEnd();
    }

    private void ResetForRetry()
    {
        Dispatcher.Invoke(() =>
        {
            _started           = false;
            BtnStart.IsEnabled = true;
            BtnStart.Content   = "⟳  Retry";
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StagingDir() =>
        Path.Combine(Path.GetTempPath(), "orc_update_staging");
}
