// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace OrchestratorIDE.UI.Controls;

public partial class DiffViewer : UserControl
{
    public event Action? Approved;
    public event Action? Rejected;

    private static readonly IBrush AddBg     = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x1A));
    private static readonly IBrush RemoveBg  = new SolidColorBrush(Color.FromRgb(0x3A, 0x1A, 0x1A));
    private static readonly IBrush NeutralBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    public DiffViewer()
    {
        InitializeComponent();
    }

    public void Load(string filePath, string oldText, string newText, string reason = "")
    {
        TbFilePath.Text = string.IsNullOrEmpty(filePath) ? "untitled" : filePath;
        TbReason.Text = string.IsNullOrEmpty(reason) ? "" : $"Reason: {reason}";

        var diff  = InlineDiffBuilder.Diff(oldText, newText);
        var lines = new ObservableCollection<DiffLineVm>();

        int oldNum = 1, newNum = 1;
        int added = 0, removed = 0;

        foreach (var line in diff.Lines)
        {
            var vm = new DiffLineVm();
            switch (line.Type)
            {
                case ChangeType.Inserted:
                    vm.Gutter      = "+";
                    vm.GutterColor = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                    vm.TextColor   = new SolidColorBrush(Colors.White);
                    vm.RowBrush    = AddBg;
                    vm.NewLineNum  = newNum++.ToString();
                    added++;
                    break;
                case ChangeType.Deleted:
                    vm.Gutter      = "-";
                    vm.GutterColor = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                    vm.TextColor   = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0xAA));
                    vm.RowBrush    = RemoveBg;
                    vm.OldLineNum  = oldNum++.ToString();
                    removed++;
                    break;
                default:
                    vm.Gutter      = " ";
                    vm.GutterColor = Brushes.Transparent;
                    vm.TextColor   = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                    vm.RowBrush    = NeutralBg;
                    vm.OldLineNum  = oldNum++.ToString();
                    vm.NewLineNum  = newNum++.ToString();
                    break;
            }
            vm.Text = line.Text;
            lines.Add(vm);
        }

        DiffLines.ItemsSource = lines;
        TbStats.Text = $"+{added} / -{removed}";
        TbStats.Foreground = added > 0
            ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
            : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
    }

    private void BtnApprove_Click(object? sender, RoutedEventArgs e) => Approved?.Invoke();
    private void BtnReject_Click(object? sender, RoutedEventArgs e)  => Rejected?.Invoke();
}

public class DiffLineVm
{
    public string OldLineNum  { get; set; } = "";
    public string NewLineNum  { get; set; } = "";
    public string Gutter      { get; set; } = " ";
    public IBrush GutterColor { get; set; } = Brushes.Gray;
    public string Text        { get; set; } = "";
    public IBrush TextColor   { get; set; } = Brushes.White;
    public IBrush RowBrush    { get; set; } = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
}
