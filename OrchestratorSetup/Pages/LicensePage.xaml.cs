// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class LicensePage : UserControl, IInstallerPage
{
    public LicensePage(InstallerViewModel vm) => InitializeComponent();

    private void ChkAgree_Changed(object sender, RoutedEventArgs e) { /* triggers CanLeave re-eval */ }

    public bool CanLeave()
    {
        if (ChkAgree.IsChecked != true)
        {
            MessageBox.Show(
                "You must accept the license agreement to continue.",
                "License Required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }
}
