// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// One reusable "storage location" settings row -- label, hint, a path textbox
/// (empty = use default), Browse, and Reset. Each category (model storage,
/// temp/scratch fallback, ...) is one instance of this control plus a couple
/// of lines in AppSettings.cs, instead of a hand-copied XAML block per
/// category (see SettingsPanel.axaml's "Native Model Root" row, which this
/// control's shape is based on).
/// </summary>
public partial class StoragePathRow : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<StoragePathRow, string>(nameof(Label), "");

    public static readonly StyledProperty<string> HintProperty =
        AvaloniaProperty.Register<StoragePathRow, string>(nameof(Hint), "");

    /// <summary>
    /// Shown (in italics, below the row) as "Currently: {value}" whenever PathText is empty,
    /// so the user can see the actual default folder in effect without having to set an
    /// override just to find out where things are landing today.
    /// </summary>
    public static readonly StyledProperty<string> DefaultDisplayProperty =
        AvaloniaProperty.Register<StoragePathRow, string>(nameof(DefaultDisplay), "");

    /// <summary>Dialog title shown by the Browse folder picker.</summary>
    public static readonly StyledProperty<string> BrowseTitleProperty =
        AvaloniaProperty.Register<StoragePathRow, string>(nameof(BrowseTitle), "Choose a folder");

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string DefaultDisplay
    {
        get => GetValue(DefaultDisplayProperty);
        set => SetValue(DefaultDisplayProperty, value);
    }

    public string BrowseTitle
    {
        get => GetValue(BrowseTitleProperty);
        set => SetValue(BrowseTitleProperty, value);
    }

    /// <summary>
    /// The raw override value -- empty means "use default" (mirrors AppSettings.ModelStoragePath's
    /// own empty-means-default convention). This is what a parent's LoadSettings/SaveSettings
    /// reads and writes, the same way it reads/writes a plain TextBox.Text elsewhere in
    /// SettingsPanel.axaml.cs.
    /// </summary>
    public string PathText
    {
        get => TbPath.Text ?? "";
        set => TbPath.Text = value;
    }

    static StoragePathRow()
    {
        LabelProperty.Changed.AddClassHandler<StoragePathRow>((v, _) => v.TbLabel.Text = v.Label);
        HintProperty.Changed.AddClassHandler<StoragePathRow>((v, _) => v.TbHint.Text = v.Hint);
        DefaultDisplayProperty.Changed.AddClassHandler<StoragePathRow>((v, _) => v.RefreshResolvedLine());
    }

    public StoragePathRow()
    {
        InitializeComponent();
        TbPath.TextChanged += (_, _) => RefreshResolvedLine();
    }

    private void RefreshResolvedLine()
    {
        TbResolved.Text = string.IsNullOrWhiteSpace(PathText)
            ? $"Currently: {DefaultDisplay}"
            : "";
    }

    private async void BtnBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = BrowseTitle, AllowMultiple = false });
        if (folders.Count > 0)
            PathText = folders[0].Path.LocalPath;
    }

    private void BtnReset_Click(object? sender, RoutedEventArgs e) => PathText = "";
}
