// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
    // Avalonia's StorageProvider.OpenFolderPickerAsync replaces WPF's
    // Microsoft.Win32.OpenFolderDialog (Windows-only) -- this is the actual
    // cross-platform folder picker, native on every OS Avalonia supports.

    private async void BtnBrowseApp_Click(object? sender, RoutedEventArgs e)
    {
        var path = await BrowseFolderAsync("Select Application Installation Folder", TxtAppPath.Text);
        if (path is not null) TxtAppPath.Text = path;
    }

    private async void BtnBrowseModel_Click(object? sender, RoutedEventArgs e)
    {
        var path = await BrowseFolderAsync("Select Model Storage Folder", TxtModelPath.Text);
        if (path is not null) TxtModelPath.Text = path;
    }

    private async Task<string?> BrowseFolderAsync(string title, string? initialPath)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;

        // Matches the existing pattern in SettingsPanel.axaml.cs / FileExplorerPanel.axaml.cs --
        // no SuggestedStartLocation seeding there either; not worth a different convention here.
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    // ── TextChanged handlers ──────────────────────────────────────────────────

    private void TxtAppPath_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _vm.State.AppInstallPath = TxtAppPath.Text ?? "";
        RefreshDriveSpace(TxtAppPath.Text ?? "", TxtAppDriveSpace);
    }

    private void TxtModelPath_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _vm.State.ModelStoragePath = TxtModelPath.Text ?? "";
        RefreshDriveSpace(TxtModelPath.Text ?? "", TxtModelDriveSpace);
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

            var info   = new DriveInfo(Path.GetPathRoot(probe)!);
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
        TxtValidation.IsVisible = false;

        if (string.IsNullOrWhiteSpace(TxtAppPath.Text))
        {
            TxtValidation.Text      = "Application installation folder cannot be empty.";
            TxtValidation.IsVisible = true;
            return false;
        }
        if (string.IsNullOrWhiteSpace(TxtModelPath.Text))
        {
            TxtValidation.Text      = "Model storage folder cannot be empty.";
            TxtValidation.IsVisible = true;
            return false;
        }
        return true;
    }
}
