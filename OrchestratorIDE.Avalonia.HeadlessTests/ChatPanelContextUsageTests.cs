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

/// <summary>
/// Headless coverage for the context-window usage indicator -- "how much of this model's
/// context am I using." FakeOllamaClient.ContextLengthToReturn stands in for a real
/// /api/show response (the real OllamaClient.GetContextLengthAsync was verified against a
/// live Ollama server separately: qwen2.5-coder:7b reports 32768, matching a raw /api/show
/// curl check exactly).
/// </summary>
[TestFixture]
public class ChatPanelContextUsageTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    [AvaloniaTest]
    public async Task SelectingAModel_withKnownContextLength_showsTheLimitImmediately()
    {
        var fake = new FakeOllamaClient { ContextLengthToReturn = 32768 };
        var panel = new ChatPanel { OllamaClient = fake };

        panel.SetModels(["fake-model:7b"], "fake-model:7b");
        for (var i = 0; i < 20 && !Required<Border>(panel, "BdrContextUsage").IsVisible; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.Multiple(() =>
        {
            Assert.That(Required<Border>(panel, "BdrContextUsage").IsVisible, Is.True);
            Assert.That(Required<TextBlock>(panel, "TxtContextUsage").Text, Is.EqualTo("Context: 0 / 32,768"));
        });
    }

    [AvaloniaTest]
    public void SelectingAModel_withNoKnownContextLength_keepsTheIndicatorHidden()
    {
        var fake = new FakeOllamaClient { ContextLengthToReturn = null };
        var panel = new ChatPanel { OllamaClient = fake };

        panel.SetModels(["fake-model:7b"], "fake-model:7b");
        Dispatcher.UIThread.RunJobs();

        Assert.That(Required<Border>(panel, "BdrContextUsage").IsVisible, Is.False);
    }

    [AvaloniaTest]
    public async Task AfterASend_displaysActualTokenUsage_fromTheRealOnUsageCallback()
    {
        var fake = new FakeOllamaClient { ContextLengthToReturn = 32768 };
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");
        for (var i = 0; i < 20 && !Required<Border>(panel, "BdrContextUsage").IsVisible; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        // FakeOllamaClient's scripted text turn invokes onUsage(10, wordCount) -- see
        // FakeOllamaClient.StreamCompletionAsync.
        fake.Enqueue("one two three");
        Required<TextBox>(panel, "TbInput").Text = "hello";
        Required<Button>(panel, "BtnSend").RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));

        for (var i = 0; i < 100 && fake.LastHistory is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        // 10 prompt + 3 completion tokens = 13 total, per FakeOllamaClient's onUsage(10, 3).
        Assert.That(Required<TextBlock>(panel, "TxtContextUsage").Text, Is.EqualTo("Context: 13 / 32,768 (0%)"));
    }

}
