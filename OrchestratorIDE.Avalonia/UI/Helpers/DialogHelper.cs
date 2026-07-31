// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OrchestratorIDE.UI;

/// <summary>Yes/No decision plus whether the user opted to skip future prompts (see ShowToolApprovalAsync).</summary>
public readonly record struct ToolApprovalResult(bool Approved, bool AlwaysApprove);

/// <summary>
/// Lightweight async dialog helper — replaces WPF MessageBox.Show for cross-platform use.
/// All dialogs are modal and must be awaited on the UI thread.
/// </summary>
internal static class DialogHelper
{
    public static Task ShowInfoAsync(Window parent, string title, string message)
        => ShowCoreAsync(parent, title, message, yesNo: false);

    public static Task<bool> ShowYesNoAsync(Window parent, string title, string message)
        => ShowCoreAsync(parent, title, message, yesNo: true);

    /// <summary>
    /// Same as <see cref="ShowYesNoAsync"/> but with an "Auto Approve Tool" checkbox (found
    /// live 2026-07-30, during a long GUI test sweep across many models -- clicking Yes on the
    /// same tool-call shape dozens of times was pure friction). Checking it only takes effect
    /// together with Yes -- the caller decides what scope "future" means (ChatPanel resets it
    /// per conversation), this dialog only reports the user's checkbox choice at the moment of
    /// approval.
    /// </summary>
    public static async Task<ToolApprovalResult> ShowToolApprovalAsync(Window parent, string title, string message)
    {
        var tcs = new TaskCompletionSource<ToolApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var msg = new TextBlock
        {
            Text        = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground  = new SolidColorBrush(Color.Parse("#D4D4D4")),
            Margin      = new Thickness(16, 16, 16, 8),
        };

        var autoApprove = new CheckBox
        {
            Content    = "Auto Approve Tool",
            Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
            Margin     = new Thickness(16, 0, 16, 8),
        };

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(8),
        };

        var dlg = new Window
        {
            Title                 = title,
            Width                 = 440,
            MinHeight             = 140,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            Background            = new SolidColorBrush(Color.Parse("#1C1C1C")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new StackPanel { Children = { msg, autoApprove, buttons } },
        };

        Button MakeBtn(string label, bool result)
        {
            var b = new Button
            {
                Content = label,
                Width   = 80,
                Margin  = new Thickness(4, 0),
                Background  = new SolidColorBrush(Color.Parse("#2D2D2D")),
                Foreground  = new SolidColorBrush(Color.Parse("#D4D4D4")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            };
            b.Click += (_, _) =>
            {
                tcs.TrySetResult(new ToolApprovalResult(result, result && autoApprove.IsChecked == true));
                dlg.Close();
            };
            return b;
        }

        buttons.Children.Add(MakeBtn("Yes", true));
        buttons.Children.Add(MakeBtn("No",  false));

        dlg.Closed += (_, _) => tcs.TrySetResult(new ToolApprovalResult(false, false));

        await dlg.ShowDialog(parent);
        return await tcs.Task;
    }

    private static async Task<bool> ShowCoreAsync(Window parent, string title, string message, bool yesNo)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var msg = new TextBlock
        {
            Text        = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground  = new SolidColorBrush(Color.Parse("#D4D4D4")),
            Margin      = new Thickness(16, 16, 16, 8),
        };

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(8),
        };

        var dlg = new Window
        {
            Title                 = title,
            Width                 = 440,
            MinHeight             = 140,
            SizeToContent         = SizeToContent.Height,
            CanResize             = false,
            Background            = new SolidColorBrush(Color.Parse("#1C1C1C")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content               = new StackPanel { Children = { msg, buttons } },
        };

        Button MakeBtn(string label, bool result)
        {
            var b = new Button
            {
                Content = label,
                Width   = 80,
                Margin  = new Thickness(4, 0),
                Background  = new SolidColorBrush(Color.Parse("#2D2D2D")),
                Foreground  = new SolidColorBrush(Color.Parse("#D4D4D4")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
            };
            b.Click += (_, _) => { tcs.TrySetResult(result); dlg.Close(); };
            return b;
        }

        if (yesNo)
        {
            buttons.Children.Add(MakeBtn("Yes", true));
            buttons.Children.Add(MakeBtn("No",  false));
        }
        else
        {
            buttons.Children.Add(MakeBtn("OK", true));
        }

        dlg.Closed += (_, _) => tcs.TrySetResult(false);

        await dlg.ShowDialog(parent);
        return await tcs.Task;
    }
}
