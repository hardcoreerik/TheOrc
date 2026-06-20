// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OrchestratorIDE.UI.Dialogs;

/// <summary>
/// Modal input dialog surfaced by the ask_user tool.
/// </summary>
public partial class UserInputDialog : Window
{
    public string Answer { get; private set; } = "";

    public UserInputDialog(string question)
    {
        InitializeComponent();
        TbQuestion.Text = question;

        var hint = ExtractHint(question);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            TbHint.Text = hint;
            TbHint.IsVisible = true;
        }

        Opened += (_, _) => Dispatcher.UIThread.Post(() => TbAnswer.Focus());
        UpdateOkButton();
    }

    public static async Task<string> ShowAsync(Window owner, string question, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return "";

        var dlg = new UserInputDialog(question);
        using var _ = ct.Register(() => Dispatcher.UIThread.Post(() => dlg.Close("")));
        return await dlg.ShowDialog<string>(owner);
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Answer = TbAnswer.Text?.Trim() ?? "";
        Close(Answer);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Answer = "";
        Close(Answer);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BtnCancel_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void TbAnswer_TextChanged(object? sender, TextChangedEventArgs e) => UpdateOkButton();

    private void UpdateOkButton() => BtnOk.IsEnabled = !string.IsNullOrWhiteSpace(TbAnswer.Text);

    private static string? ExtractHint(string question)
    {
        var start = question.LastIndexOf('(');
        var end = question.LastIndexOf(')');
        if (start >= 0 && end > start)
            return question[(start + 1)..end];
        return null;
    }
}
