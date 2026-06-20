// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.UI.Dialogs;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

[TestFixture]
public sealed class T23_UserInputDialogTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static void Click(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public void Ask_user_dialog_shows_question_and_parenthetical_hint()
    {
        var dialog = new UserInputDialog("Install scope? (type 'system' or 'workspace')");

        Assert.Multiple(() =>
        {
            Assert.That(Required<TextBox>(dialog, "TbQuestion").Text, Does.Contain("Install scope?"));
            Assert.That(Required<TextBlock>(dialog, "TbHint").IsVisible, Is.True);
            Assert.That(Required<TextBlock>(dialog, "TbHint").Text, Is.EqualTo("type 'system' or 'workspace'"));
            Assert.That(Required<Button>(dialog, "BtnOk").IsEnabled, Is.False);
        });
    }

    [AvaloniaTest]
    public void Ask_user_dialog_enables_send_only_after_input()
    {
        var dialog = new UserInputDialog("Name the workspace");
        try
        {
            dialog.Show();
            Dispatcher.UIThread.RunJobs();

            var answerBox = Required<TextBox>(dialog, "TbAnswer");
            var sendButton = Required<Button>(dialog, "BtnOk");

            Assert.That(sendButton.IsEnabled, Is.False);

            answerBox.Text = "runtime-lab";
            Dispatcher.UIThread.RunJobs();

            Assert.That(sendButton.IsEnabled, Is.True);
        }
        finally
        {
            dialog.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
