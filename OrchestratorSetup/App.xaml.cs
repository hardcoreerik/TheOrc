// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;

namespace OrchestratorSetup;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{ex.Exception.Message}\n\n" +
                "The installer will close. Please report this at:\n" +
                "https://github.com/hardcoreerik/TheOrc/issues",
                "OrchestratorSetup — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        bool isUninstall = e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        if (isUninstall)
        {
            var win = new UninstallWindow();
            MainWindow = win;
            win.ShowDialog();
            Shutdown(0);
        }
        else
        {
            var win = new MainWindow();
            MainWindow = win;
            win.Show();
        }
    }
}
