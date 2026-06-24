// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Tests;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Headless coverage for ChatPanel's text-selection/copy feature -- plain TextBlock has no
/// built-in text selection at all, which was the actual gap behind "give me the ability to
/// select text and copy to clipboard." Covers the structural pieces (SelectableTextBlock
/// swap, per-bubble context menu, raw-markdown Tag) and an end-to-end clipboard round-trip
/// through a real (headless) Window/TopLevel, since TopLevel.GetTopLevel returns null for a
/// ChatPanel with no Window parent.
/// </summary>
[TestFixture]
public class ChatPanelTextSelectionTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // ── Pure stripping logic ─────────────────────────────────────────────────────

    [Test]
    public void StripMarkdownForPlainCopy_removesBoldItalicCodeAndListMarkers()
    {
        var input = "**Safety First:** turn off power.\n- `valve` check\n# Heading";
        var result = ChatPanel.StripMarkdownForPlainCopy(input);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Not.Contain("**"));
            Assert.That(result, Does.Not.Contain("`"));
            Assert.That(result, Does.Not.Contain("# "));
            Assert.That(result, Does.Contain("Safety First:"));
            Assert.That(result, Does.Contain("valve"));
            Assert.That(result, Does.Contain("Heading"));
        });
    }

    // ── Structural: user bubble ──────────────────────────────────────────────────

    [AvaloniaTest]
    public async Task UserBubble_isSelectable_andHasACopyContextMenu()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        fake.Enqueue("hi");
        Required<TextBox>(panel, "TbInput").Text = "hello there";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);

        // First bubble in ChatStack is the user's own message.
        var chatStack = Required<StackPanel>(panel, "ChatStack");
        var userBubble = chatStack.Children.OfType<Border>().First();

        Assert.Multiple(() =>
        {
            Assert.That(userBubble.Child, Is.InstanceOf<SelectableTextBlock>());
            Assert.That(userBubble.Tag, Is.EqualTo("hello there"));
            Assert.That(userBubble.ContextMenu, Is.Not.Null);

            var items = userBubble.ContextMenu!.ItemsSource!.Cast<MenuItem>().ToList();
            Assert.That(items.Select(i => i.Header), Is.EqualTo(new object[] { "Copy", "Copy as Markdown" }));
        });
    }

    // ── Structural: assistant bubble ─────────────────────────────────────────────

    [AvaloniaTest]
    public async Task AssistantBubble_tagHoldsRawMarkdown_afterTurnCompletes()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        fake.Enqueue("**bold reply** with `code`");
        Required<TextBox>(panel, "TbInput").Text = "hello";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);
        // Give OnTurnComplete's Dispatcher.UIThread.InvokeAsync a chance to run.
        for (var i = 0; i < 50; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        var chatStack = Required<StackPanel>(panel, "ChatStack");
        var assistantBubble = chatStack.Children.OfType<Border>().Skip(1).First();

        // FakeOllamaClient streams word-by-word with a trailing space after every word
        // (including the last) -- a quirk of the fake's streaming simulation, not of real
        // model output. Trim before comparing.
        Assert.Multiple(() =>
        {
            Assert.That(((string)assistantBubble.Tag!).Trim(), Is.EqualTo("**bold reply** with `code`"));
            Assert.That(assistantBubble.ContextMenu, Is.Not.Null);
        });
    }

    // ── End-to-end clipboard round-trip ──────────────────────────────────────────

    [AvaloniaTest]
    public async Task CopyAsMarkdown_putsRawSourceOnClipboard_CopyPutsStrippedVersion()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        // Host in a real (headless) Window so TopLevel.GetTopLevel(panel) resolves to
        // something with a Clipboard -- a bare ChatPanel with no Window parent has none.
        var window = new Window { Content = panel };
        window.Show();

        fake.Enqueue("**bold** reply");
        Required<TextBox>(panel, "TbInput").Text = "hello";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);
        for (var i = 0; i < 50; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        var chatStack = Required<StackPanel>(panel, "ChatStack");
        var assistantBubble = chatStack.Children.OfType<Border>().Skip(1).First();
        var items = assistantBubble.ContextMenu!.ItemsSource!.Cast<MenuItem>().ToList();
        var copyPlain     = items.Single(i => (string)i.Header! == "Copy");
        var copyMarkdown  = items.Single(i => (string)i.Header! == "Copy as Markdown");

        var clipboard = window.Clipboard ?? throw new InvalidOperationException("No clipboard on headless window.");

        copyMarkdown.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(20);
        var markdownCopy = await clipboard.TryGetTextAsync();

        copyPlain.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(20);
        var plainCopy = await clipboard.TryGetTextAsync();

        // Same trailing-space quirk as above -- trim before comparing.
        Assert.Multiple(() =>
        {
            Assert.That(markdownCopy?.Trim(), Is.EqualTo("**bold** reply"));
            Assert.That(plainCopy?.Trim(), Is.EqualTo("bold reply"));
        });
    }

    private static async Task WaitForCapture(FakeOllamaClient fake)
    {
        for (var i = 0; i < 100 && fake.LastHistory is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
