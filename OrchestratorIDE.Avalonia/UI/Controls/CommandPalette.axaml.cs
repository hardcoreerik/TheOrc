// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace OrchestratorIDE.UI.Controls;

public partial class CommandPalette : UserControl
{
    public event Action<PaletteCommand>? CommandSelected;
    public event Action?                 Dismissed;

    private readonly List<PaletteCommand>                 _allCommands = [];
    private readonly ObservableCollection<PaletteCommand> _filtered    = [];

    public CommandPalette()
    {
        InitializeComponent();
        ResultsList.ItemsSource = _filtered;
        Loaded += (_, _) => SearchBox.Focus();
    }

    public void RegisterCommands(IEnumerable<PaletteCommand> commands)
    {
        _allCommands.Clear();
        _allCommands.AddRange(commands);
        Filter("");
    }

    private void Filter(string query)
    {
        SearchPlaceholder.IsVisible = string.IsNullOrEmpty(SearchBox.Text);
        _filtered.Clear();

        var q = query.Trim().ToLowerInvariant();
        var matches = string.IsNullOrEmpty(q)
            ? _allCommands
            : _allCommands.Where(c =>
                c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Detail.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Keywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)));

        foreach (var cmd in matches.OrderBy(c => c.SortOrder))
            _filtered.Add(cmd);

        if (_filtered.Count > 0)
            ResultsList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
        => Filter(SearchBox.Text ?? "");

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                if (ResultsList.SelectedIndex < _filtered.Count - 1)
                    ResultsList.SelectedIndex++;
                if (ResultsList.SelectedItem is not null)
                    ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                break;
            case Key.Up:
                e.Handled = true;
                if (ResultsList.SelectedIndex > 0)
                    ResultsList.SelectedIndex--;
                if (ResultsList.SelectedItem is not null)
                    ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                break;
            case Key.Enter:
                e.Handled = true;
                Execute();
                break;
            case Key.Escape:
                e.Handled = true;
                Dismissed?.Invoke();
                break;
        }
    }

    private void ResultsList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Dismissed?.Invoke(); }
        if (e.Key == Key.Enter)  { e.Handled = true; Execute(); }
    }

    private void ResultsList_DoubleTapped(object? sender, RoutedEventArgs e) => Execute();

    private void Execute()
    {
        if (ResultsList.SelectedItem is PaletteCommand cmd)
        {
            Dismissed?.Invoke();
            CommandSelected?.Invoke(cmd);
        }
    }
}
