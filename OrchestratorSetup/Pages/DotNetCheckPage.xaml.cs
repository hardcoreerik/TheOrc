using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

/// <summary>
/// Installer page that detects whether the .NET 10 SDK is on PATH and offers
/// to install it via the official dotnet-install.ps1 script.
///
/// The SDK is not required to run TheOrc (which ships self-contained),
/// but IS required for the "Update from GitHub source" feature in the app.
/// This page is therefore informational / optional — CanLeave() always returns true.
/// </summary>
public partial class DotNetCheckPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;
    private bool _installing;

    private const string DotnetInstallUrl = "https://dot.net/v1/dotnet-install.ps1";

    public DotNetCheckPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += async (_, _) => await DetectSdkAsync();
    }

    // ── Detection ─────────────────────────────────────────────────────────────

    private async Task DetectSdkAsync()
    {
        TxtSdkIcon.Text   = "⏳";
        TxtSdkStatus.Text = "Checking for .NET 10 SDK…";
        TxtSdkDetail.Text = "";

        var found   = false;
        var version = "";

        try
        {
            var psi = new ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                version = (await proc.StandardOutput.ReadToEndAsync()).Trim();
                await proc.WaitForExitAsync();
                found = proc.ExitCode == 0 && version.StartsWith("10");
            }
        }
        catch { }

        _vm.State.DotNetSdkDetected = found;
        UpdateDisplay(found, version);
    }

    private void UpdateDisplay(bool found, string version)
    {
        if (found)
        {
            TxtSdkIcon.Text   = "🟢";
            TxtSdkStatus.Text = $".NET SDK {version} detected";
            TxtSdkDetail.Text = "The auto-update feature will be able to rebuild TheOrc from source.";
            CardInstall.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtSdkIcon.Text   = "⚪";
            TxtSdkStatus.Text = ".NET 10 SDK not found";
            TxtSdkDetail.Text = "TheOrc runs without it, but the \"Update from GitHub\" feature needs it. " +
                                "You can install it now or later.";
            CardInstall.Visibility = Visibility.Visible;
        }
    }

    // ── Install ───────────────────────────────────────────────────────────────

    private async void BtnInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        BtnInstall.IsEnabled = false;
        BtnInstall.Content   = "Installing…";

        try
        {
            Log("Downloading dotnet-install.ps1…");
            var tempScript = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dotnet-install.ps1");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var text = await http.GetStringAsync(DotnetInstallUrl);
            await System.IO.File.WriteAllTextAsync(tempScript, text);

            Log("Running installer — this takes 1–3 minutes…");
            var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\" " +
                       $"-Channel 10.0 -InstallDir \"$env:ProgramFiles\\dotnet\"";

            var psi = new ProcessStartInfo("powershell.exe", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            var outTask = ReadStreamAsync(proc.StandardOutput);
            var errTask = ReadStreamAsync(proc.StandardError);
            await proc.WaitForExitAsync();
            await Task.WhenAll(outTask, errTask);

            if (proc.ExitCode == 0)
            {
                Log("✓ .NET 10 SDK installed.");
                await DetectSdkAsync();
            }
            else
            {
                Log($"⚠ Installer exited with code {proc.ExitCode}. Try running as administrator.");
                BtnInstall.Content   = "Retry";
                BtnInstall.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            Log($"❌ {ex.Message}");
            BtnInstall.Content   = "Retry";
            BtnInstall.IsEnabled = true;
        }
        finally
        {
            _installing = false;
        }
    }

    private async Task ReadStreamAsync(System.IO.StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            if (!string.IsNullOrWhiteSpace(line))
                Dispatcher.Invoke(() => Log(line.TrimEnd()));
    }

    private void Log(string msg)
    {
        TbInstallLog.Text += msg + "\n";
    }

    // ── IInstallerPage ────────────────────────────────────────────────────────

    public bool CanLeave() => true;   // always optional — user can skip
}
