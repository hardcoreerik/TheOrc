// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _installPath = UninstallService.ReadInstallPath() ?? "";

        TbInstallPath.Text = string.IsNullOrWhiteSpace(_installPath)
            ? "(install location not found in registry)"
            : _installPath;

        BtnRemove.IsEnabled = !string.IsNullOrWhiteSpace(_installPath);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
        => Close();

    private async void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        BtnRemove.IsEnabled  = false;
        BtnCancel.IsEnabled  = false;
        BdrProgress.Visibility = Visibility.Visible;

        var removeUserData = ChkRemoveUserData.IsChecked == true;

        var log = new System.Text.StringBuilder();

        await Task.Run(() =>
        {
            UninstallService.Uninstall(_installPath, removeUserData, msg =>
            {
                log.AppendLine(msg);
                Dispatcher.InvokeAsync(() => TbProgress.Text = log.ToString());
            });
        });

        TbProgress.Text += "\nTheOrc has been removed. Click Close to exit.";
        BtnCancel.Content    = "Close";
        BtnCancel.IsEnabled  = true;
    }
}
