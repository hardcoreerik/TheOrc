// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NUnit.Framework;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// CF-5: sending a question while a corpus is attached must route through FabricAskService
/// (not the plain ChatEngine/Ollama path) and render a cited answer bubble with the coverage
/// line. Uses reflection to invoke SendAsync directly, matching ChatPanelModeToggleTests'
/// ClearDuringInFlightSend_doesNotThrow convention (BtnSend_Click is fire-and-forget, so
/// exceptions inside it would otherwise become unobserved task exceptions).
/// </summary>
[TestFixture]
public class OrcChatContextFabricQueryTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    [AvaloniaTest]
    public async Task SendingWithAttachedCorpus_RoutesThroughFabricAskService_AndRendersCoverageLine()
    {
        const string quote = "LANTERN is the assigned call sign.";
        using var harness = Cf5TestHarness.Create(quote);
        var panel = new ChatPanel();
        panel.SetFabricServices(
            harness.NewAskService(new FakeFabricRuntime(FakeFabricRuntime.BuildAnswerJson(quote, "seg-0"))),
            harness.NewOrchestrator(new FakeFabricRuntime("{}")),
            harness.NewLibraryViewModel(),
            webImporter: null,
            harness.WorkspaceRoot);

        OrcChatLibraryTests.InvokeAttach(panel, harness.Corpus.CorpusId);
        Dispatcher.UIThread.RunJobs();

        Required<TextBox>(panel, "TbInput").Text = "What is the call sign?";

        var sendMethod = typeof(ChatPanel).GetMethod("SendAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("SendAsync method not found via reflection.");
        var sendTask = (Task)sendMethod.Invoke(panel, null)!;

        for (var i = 0; i < 200 && !sendTask.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.That(sendTask.IsCompleted, Is.True, "SendAsync (fabric path) never completed.");
        Assert.That(sendTask.IsFaulted, Is.False, $"SendAsync threw: {sendTask.Exception?.InnerException}");

        var chatStack = Required<StackPanel>(panel, "ChatStack");
        var bubbleText = ExtractAllText(chatStack);
        // The answer bubble's Tag carries the raw answer text (same "Copy as Markdown" seam
        // OnTurnComplete uses for the plain-chat path) -- a reliable check independent of how
        // MarkdownView happens to materialize the prose into its own control tree.
        var assistantBubble = chatStack.Children.OfType<Border>().Last();

        Assert.Multiple(() =>
        {
            Assert.That(bubbleText, Does.Contain("Quick mode"));
            Assert.That(bubbleText, Does.Contain("citations verified"));
            Assert.That(assistantBubble.Tag, Is.EqualTo(quote));
        });
    }

    /// <summary>Walks the visual tree collecting every TextBlock's Text — the cited-answer
    /// bubble is built from plain Avalonia controls (no single string property to read back),
    /// same constraint ChatPanel's own MarkdownView-based bubbles have.</summary>
    private static string ExtractAllText(Control root)
    {
        var sb = new System.Text.StringBuilder();
        Walk(root);
        return sb.ToString();

        void Walk(Control control)
        {
            if (control is TextBlock tb && tb.Text is not null)
                sb.Append(tb.Text).Append(' ');
            foreach (var child in control.GetVisualChildren())
                if (child is Control c) Walk(c);
        }
    }
}
