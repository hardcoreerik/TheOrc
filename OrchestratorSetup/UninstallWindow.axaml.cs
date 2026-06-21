// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OrchestratorSetup.Services;

namespace OrchestratorSetup;

public partial class UninstallWindow : Window
{
    private string _installPath = "";

    public UninstallWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _installPath = UninstallService.ReadInstallPath() ?? "";

        TbInstallPath.Text = string.IsNullOrWhiteSpace(_installPath)
            ? "(install location not found in registry)"
            : _installPath;

        BtnRemove.IsEnabled = !string.IsNullOrWhiteSpace(_installPath);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async void BtnRemove_Click(object? sender, RoutedEventArgs e)
    {
        // async void: an unobserved exception here would crash the process (grok review
        // BLOCKER, 2026-06-21) -- this is a UI event handler, not a place that should ever
        // propagate a fault to the dispatcher's unhandled-exception path.
        try
        {
            BtnRemove.IsEnabled   = false;
            BtnCancel.IsEnabled   = false;
            BdrProgress.IsVisible = true;

            var removeUserData = ChkRemoveUserData.IsChecked == true;

            var log = new System.Text.StringBuilder();

            await Task.Run(() =>
            {
                UninstallService.Uninstall(_installPath, removeUserData, msg =>
                {
                    log.AppendLine(msg);
                    Dispatcher.UIThread.Post(() => TbProgress.Text = log.ToString());
                });
            });

            TbProgress.Text += "\nTheOrc has been removed. Click Close to exit.";
            BtnCancel.Content   = "Close";
            BtnCancel.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TbProgress.Text   += $"\n❌ Uninstall failed: {ex.Message}";
            BtnCancel.Content   = "Close";
            BtnCancel.IsEnabled = true;
        }
    }
}
