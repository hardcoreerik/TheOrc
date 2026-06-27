// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Tests;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

[TestFixture]
public class ChatPanelAttachmentTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    [AvaloniaTest]
    public void AddAttachmentsFromPaths_showsThePendingAttachmentRail()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpanel_attachment_{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "# hello");

        try
        {
            var panel = new ChatPanel();
            panel.AddAttachmentsFromPaths([path]);

            Assert.That(Required<ItemsControl>(panel, "PendingAttachmentsPanel").IsVisible, Is.True);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [AvaloniaTest]
    public async Task AttachmentOnlySend_isAllowed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatpanel_attachment_only_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+k2X8AAAAASUVORK5CYII="));

        try
        {
            var fake = new FakeOllamaClient();
            fake.Enqueue("done");

            var panel = new ChatPanel { OllamaClient = fake };
            panel.SetModels(["fake-model:7b"], "fake-model:7b");
            panel.AddAttachmentsFromPaths([path]);

            Required<Button>(panel, "BtnSend").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            for (var i = 0; i < 50 && fake.LastHistory is null; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.That(fake.LastHistory, Is.Not.Null);
            Assert.That(fake.LastHistory!.Last().Attachments.Count, Is.EqualTo(1));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
