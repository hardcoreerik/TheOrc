// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

/// <summary>
/// Welcome + License — merged per INSTALLER_REVAMP_SPEC.md §3 (folds the old separate
/// License page's accept-checkbox gate into the welcome screen to drop one click).
/// </summary>
public partial class WelcomePage : UserControl, IInstallerPage
{
    public WelcomePage(InstallerViewModel vm) => InitializeComponent();

    private void ChkAgree_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        /* triggers CanLeave re-eval on Next click; no immediate action needed */
    }

    public bool CanLeave()
    {
        // No WPF MessageBox in Avalonia (App.axaml.cs's doc comment) -- an inline TextBlock
        // matches the original LicensePage's validation intent without a dialog window,
        // same pattern InstallPathPage already uses for its own validation message.
        TxtValidation.IsVisible = ChkAgree.IsChecked != true;
        return ChkAgree.IsChecked == true;
    }
}
