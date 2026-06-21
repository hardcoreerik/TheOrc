// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OrchestratorSetup.Services;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class CompletePage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    public CompletePage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += (_, _) => PopulateSummary();
    }

    private void PopulateSummary()
    {
        // Profile is no longer chosen here -- INSTALLER_REVAMP_SPEC.md §3 dropped the
        // installer's Profile page entirely in favor of the app's own FirstRunWindow, the
        // single source of truth for .agent.md / coding profile (grok review MINOR,
        // 2026-06-21: a "Profile" summary row here would have implied a choice the user
        // never actually made in this wizard).
        var model = _vm.AllModels.FirstOrDefault(m => m.Id == _vm.State.SelectedModelId);

        TxtSummaryModel.Text = model?.Name ?? _vm.State.SelectedModelId;
        TxtSummaryPath.Text  = _vm.State.AppInstallPath;

        // Sync checkboxes to state defaults
        ChkLaunch.IsChecked          = _vm.State.LaunchAfterInstall;
        ChkDesktopShortcut.IsChecked = _vm.State.CreateDesktopShortcut;
        ChkStartMenu.IsChecked       = _vm.State.CreateStartMenuShortcut;

        TxtHiveStatus.Text = _vm.State.JoinHiveMind
            ? "HIVE MIND ports were opened during install. If you declined a firewall/admin prompt, retry below."
            : "HIVE MIND was not enabled during install. Click below to enable it now.";
    }

    // ── Checkbox handlers ─────────────────────────────────────────────────────

    private void ChkLaunch_Changed(object? sender, RoutedEventArgs e)
        => _vm.State.LaunchAfterInstall = ChkLaunch.IsChecked == true;

    private void ChkDesktop_Changed(object? sender, RoutedEventArgs e)
        => _vm.State.CreateDesktopShortcut = ChkDesktopShortcut.IsChecked == true;

    private void ChkStartMenu_Changed(object? sender, RoutedEventArgs e)
        => _vm.State.CreateStartMenuShortcut = ChkStartMenu.IsChecked == true;

    // ── HIVE MIND networking ──────────────────────────────────────────────────
    // INSTALLER_REVAMP_SPEC.md §3/§5.7 — users must not have to open ports manually; this
    // button retries the SAME automatic, elevation-prompting path HiveEnroller already runs
    // during install (UAC/sudo is expected and fine), for "I declined the prompt" or "it
    // failed" without requiring a full reinstall. Manual instructions remain HiveEnroller's
    // own internal last resort, not something this button asks the user to do directly.

    private async void BtnConfigureHive_Click(object? sender, RoutedEventArgs e)
    {
        // async void: an unobserved exception here would crash the process (grok review
        // BLOCKER, 2026-06-21). This try/catch is the ONLY exception guard for the call
        // below -- IPlatformInstaller.ConfigureFirewallAsync deliberately does NOT swallow
        // internally (see WindowsPlatformInstaller's own comment on that method: the
        // install-time caller in InstallOrchestrator relies on exceptions propagating to
        // fail the install step, so swallowing in the shared implementation would have
        // silently changed that caller's behavior too).
        try
        {
            BtnConfigureHive.IsEnabled = false;
            TxtHiveStatus.Text = "Configuring HIVE ports…";

            // INSTALLER_REVAMP_SPEC.md §7 Phase 2 -- goes through IPlatformInstaller now.
            var ok = await PlatformInstaller.Current.ConfigureFirewallAsync(
                msg => Dispatcher.UIThread.Post(() => TxtHiveStatus.Text = msg), CancellationToken.None);

            TxtHiveStatus.Text = ok
                ? "✓ HIVE MIND ports are open. Other TheOrc installs on this network can now find this machine."
                : "Could not configure ports automatically. Open TheOrc → Settings → HIVE MIND to retry, or see the app's HIVE_MIND_SPEC.md for manual steps.";
        }
        catch (Exception ex)
        {
            TxtHiveStatus.Text = $"Could not configure ports: {ex.Message}";
        }
        finally
        {
            BtnConfigureHive.IsEnabled = true;
        }
    }

    // ── Finish ────────────────────────────────────────────────────────────────

    private void BtnFinish_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm.State.LaunchAfterInstall)
        {
            // INSTALLER_REVAMP_SPEC.md §7 Phase 2 -- goes through IPlatformInstaller now.
            // PlatformInstaller.Current can throw PlatformNotSupportedException on an OS
            // without an implementation yet (Phases 4-5); the old OperatingSystem.IsWindows()
            // ternary it replaced never threw, so this needs its own guard to stay a "no
            // behavior change" refactor (grok review MINOR, 2026-06-21). Not reachable on
            // Windows today, same reasoning as UninstallWindow.OnLoaded's identical guard.
            string exePath;
            try { exePath = PlatformInstaller.Current.LaunchCommand(_vm.State.AppInstallPath); }
            catch (Exception ex)
            {
                TxtLaunchStatus.Text      = $"Could not determine the launch path: {ex.Message}";
                TxtLaunchStatus.IsVisible = true;
                return;
            }
            if (File.Exists(exePath))
            {
                try { Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    TxtLaunchStatus.Text      = $"Could not launch TheOrc: {ex.Message}";
                    TxtLaunchStatus.IsVisible = true;
                    return;
                }
            }
            else
            {
                TxtLaunchStatus.Text      = $"OrchestratorIDE was not found at:\n{exePath}";
                TxtLaunchStatus.IsVisible = true;
                return;
            }
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown(0);
    }

    public bool CanLeave() => true;
}
