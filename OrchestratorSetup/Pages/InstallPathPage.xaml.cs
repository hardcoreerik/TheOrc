// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class InstallPathPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    public InstallPathPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            TxtAppPath.Text   = _vm.State.AppInstallPath;
            TxtModelPath.Text = _vm.State.ModelStoragePath;
            RefreshDriveSpace(TxtAppPath.Text,   TxtAppDriveSpace);
            RefreshDriveSpace(TxtModelPath.Text, TxtModelDriveSpace);
        };
    }

    // ── Browse handlers ───────────────────────────────────────────────────────

    private void BtnBrowseApp_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select Application Installation Folder", TxtAppPath.Text);
        if (path is not null) TxtAppPath.Text = path;
    }

    private void BtnBrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFolder("Select Model Storage Folder", TxtModelPath.Text);
        if (path is not null) TxtModelPath.Text = path;
    }

    private static string? BrowseFolder(string description, string initialPath)
    {
        // Use WPF OpenFolderDialog (available .NET 8+)
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = description,
            InitialDirectory = Directory.Exists(initialPath) ? initialPath : null,
        };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    // ── TextChanged handlers ──────────────────────────────────────────────────

    private void TxtAppPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.State.AppInstallPath = TxtAppPath.Text;
        RefreshDriveSpace(TxtAppPath.Text, TxtAppDriveSpace);
    }

    private void TxtModelPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.State.ModelStoragePath = TxtModelPath.Text;
        RefreshDriveSpace(TxtModelPath.Text, TxtModelDriveSpace);
    }

    // ── Drive space display ───────────────────────────────────────────────────

    private static void RefreshDriveSpace(string path, TextBlock label)
    {
        try
        {
            // Walk up until we find an existing ancestor (path may not exist yet)
            var probe = path;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe) ?? "";

            if (string.IsNullOrEmpty(probe))
            {
                label.Text = "";
                return;
            }

            var info  = new DriveInfo(Path.GetPathRoot(probe)!);
            var freeGb = info.AvailableFreeSpace / 1_073_741_824.0;
            label.Text = $"Free space on {info.Name}: {freeGb:F1} GB";
        }
        catch
        {
            label.Text = "";
        }
    }

    // ── Validation ────────────────────────────────────────────────────────────

    public bool CanLeave()
    {
        TxtValidation.Visibility = System.Windows.Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(TxtAppPath.Text))
        {
            TxtValidation.Text       = "Application installation folder cannot be empty.";
            TxtValidation.Visibility = System.Windows.Visibility.Visible;
            return false;
        }
        if (string.IsNullOrWhiteSpace(TxtModelPath.Text))
        {
            TxtValidation.Text       = "Model storage folder cannot be empty.";
            TxtValidation.Visibility = System.Windows.Visibility.Visible;
            return false;
        }
        return true;
    }
}
