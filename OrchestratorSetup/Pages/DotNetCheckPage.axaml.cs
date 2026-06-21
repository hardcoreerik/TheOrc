// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

/// <summary>
/// Installer page that detects whether the .NET 10 SDK is on PATH and offers
/// to install it via the official dotnet-install script.
///
/// The SDK is not required to run TheOrc (which ships self-contained),
/// but IS required for the "Update from GitHub source" feature in the app.
/// This page is therefore informational / optional — CanLeave() always returns true.
///
/// INSTALLER_REVAMP_SPEC.md §3 — kept cross-platform: dotnet-install.ps1 on Windows,
/// dotnet-install.sh (the official cross-platform counterpart) elsewhere.
/// </summary>
public partial class DotNetCheckPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;
    private bool _installing;

    private const string DotnetInstallPs1Url = "https://dot.net/v1/dotnet-install.ps1";
    private const string DotnetInstallShUrl  = "https://dot.net/v1/dotnet-install.sh";

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
            CardInstall.IsVisible = false;
        }
        else
        {
            TxtSdkIcon.Text   = "⚪";
            TxtSdkStatus.Text = ".NET 10 SDK not found";
            TxtSdkDetail.Text = "TheOrc runs without it, but the \"Update from GitHub\" feature needs it. " +
                                "You can install it now or later.";
            CardInstall.IsVisible = true;
        }
    }

    // ── Install ───────────────────────────────────────────────────────────────

    private async void BtnInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;
        BtnInstall.IsEnabled = false;
        BtnInstall.Content   = "Installing…";

        try
        {
            if (OperatingSystem.IsWindows())
                await InstallViaPowerShellAsync();
            else
                await InstallViaShellScriptAsync();
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

    private async Task InstallViaPowerShellAsync()
    {
        Log("Downloading dotnet-install.ps1…");
        var tempScript = Path.Combine(Path.GetTempPath(), "dotnet-install.ps1");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var text = await http.GetStringAsync(DotnetInstallPs1Url);
        await File.WriteAllTextAsync(tempScript, text);

        Log("Running installer — this takes 1–3 minutes…");
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\" " +
                   $"-Channel 10.0 -InstallDir \"$env:ProgramFiles\\dotnet\"";

        await RunAndStreamAsync("powershell.exe", args);
    }

    private async Task InstallViaShellScriptAsync()
    {
        Log("Downloading dotnet-install.sh…");
        var tempScript = Path.Combine(Path.GetTempPath(), "dotnet-install.sh");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var text = await http.GetStringAsync(DotnetInstallShUrl);
        await File.WriteAllTextAsync(tempScript, text);
        File.SetUnixFileMode(tempScript,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Log("Running installer — this takes 1–3 minutes…");
        var installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        await RunAndStreamAsync("bash", $"\"{tempScript}\" --channel 10.0 --install-dir \"{installDir}\"");
    }

    private async Task RunAndStreamAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
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
            Log($"⚠ Installer exited with code {proc.ExitCode}." +
                (OperatingSystem.IsWindows() ? " Try running as administrator." : " Try running with sudo."));
            BtnInstall.Content   = "Retry";
            BtnInstall.IsEnabled = true;
        }
    }

    private async Task ReadStreamAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            if (!string.IsNullOrWhiteSpace(line))
            {
                var captured = line.TrimEnd();
                // Avalonia's synchronous-dispatch equivalent of WPF's Dispatcher.Invoke.
                await Dispatcher.UIThread.InvokeAsync(() => Log(captured));
            }
    }

    private void Log(string msg)
    {
        TbInstallLog.Text += msg + "\n";
    }

    // ── IInstallerPage ────────────────────────────────────────────────────────

    public bool CanLeave() => true;   // always optional — user can skip
}
