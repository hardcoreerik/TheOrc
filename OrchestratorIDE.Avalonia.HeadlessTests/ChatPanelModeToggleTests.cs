// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Tests;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Headless coverage for ChatPanel's Phase B2 Research/Open mode toggle -- both the visual
/// state transitions (button checked state, control visibility, welcome text) and the actual
/// engine wiring (does Open mode really send the textbox values through to the runtime, not
/// just look right). Replaces an attempted manual computer-use click-through that got tangled
/// in window/build mismatches; this is the durable, repeatable version of that verification.
/// Temp/Top-P are plain TextBoxes, not NumericUpDown -- a real user testing session found
/// NumericUpDown's value invisible under this app's dark theme (its templated inner TextBox
/// part doesn't inherit the outer Foreground/Background), which headless tests had no way to
/// catch since they verify wiring, not visual rendering.
/// </summary>
[TestFixture]
public class ChatPanelModeToggleTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public void DefaultState_isResearchMode()
    {
        var panel = new ChatPanel();

        Assert.Multiple(() =>
        {
            Assert.That(Required<ToggleButton>(panel, "BtnModeResearch").IsChecked, Is.True);
            Assert.That(Required<ToggleButton>(panel, "BtnModeOpen").IsChecked, Is.False);
            Assert.That(Required<Border>(panel, "BdrOpenControls").IsVisible, Is.False);
            Assert.That(Required<TextBlock>(panel, "TxtWelcomeTitle").Text, Is.EqualTo("👋  Ready to research"));
            Assert.That(Required<TextBlock>(panel, "TxtWelcomeTip").IsVisible, Is.True);
        });
    }

    [AvaloniaTest]
    public void ClickingOpenMode_revealsOpenControls_andSwapsWelcomeText()
    {
        var panel = new ChatPanel();

        Click(Required<ToggleButton>(panel, "BtnModeOpen"));
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(Required<ToggleButton>(panel, "BtnModeOpen").IsChecked, Is.True);
            Assert.That(Required<ToggleButton>(panel, "BtnModeResearch").IsChecked, Is.False);
            Assert.That(Required<Border>(panel, "BdrOpenControls").IsVisible, Is.True);
            Assert.That(Required<TextBlock>(panel, "TxtWelcomeTitle").Text, Is.EqualTo("👋  Open chat"));
            // The research-flavored example-prompt tip line doesn't apply to Open mode.
            Assert.That(Required<TextBlock>(panel, "TxtWelcomeTip").IsVisible, Is.False);
        });
    }

    [AvaloniaTest]
    public void ClickingBackToResearchMode_restoresOriginalState()
    {
        var panel = new ChatPanel();

        Click(Required<ToggleButton>(panel, "BtnModeOpen"));
        Dispatcher.UIThread.RunJobs();
        Click(Required<ToggleButton>(panel, "BtnModeResearch"));
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(Required<ToggleButton>(panel, "BtnModeResearch").IsChecked, Is.True);
            Assert.That(Required<Border>(panel, "BdrOpenControls").IsVisible, Is.False);
            Assert.That(Required<TextBlock>(panel, "TxtWelcomeTitle").Text, Is.EqualTo("👋  Ready to research"));
            Assert.That(Required<TextBlock>(panel, "TxtWelcomeTip").IsVisible, Is.True);
        });
    }

    [AvaloniaTest]
    public async Task ResearchMode_send_usesResearchDefaults_notOpenControls()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        fake.Enqueue("hi there");

        Required<TextBox>(panel, "TbInput").Text = "hello";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);

        // Research mode must use ChatEngine's own research defaults (temperature 0.2),
        // completely ignoring whatever happens to be sitting in the (hidden, irrelevant)
        // Open-mode controls.
        Assert.That(fake.LastTemperature, Is.EqualTo(0.2));
    }

    [AvaloniaTest]
    public async Task OpenMode_send_threadsTextboxAndNumericValuesIntoTheRequest()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        Click(Required<ToggleButton>(panel, "BtnModeOpen"));
        Dispatcher.UIThread.RunJobs();

        Required<TextBox>(panel, "TbOpenSystemPrompt").Text  = "You are a pirate.";
        Required<TextBox>(panel, "TbOpenTemperature").Text   = "1.2";
        Required<TextBox>(panel, "TbOpenTopP").Text          = "0.7";

        fake.Enqueue("arr");

        Required<TextBox>(panel, "TbInput").Text = "ahoy";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);

        Assert.Multiple(() =>
        {
            Assert.That(fake.LastTemperature, Is.EqualTo(1.2));
            Assert.That(fake.LastTopP, Is.EqualTo(0.7));

            // The system prompt textbox's value must reach the model as an actual system
            // message -- this is the literal mechanism behind "Open mode lets you set your
            // own prompt instead of getting the research one."
            var systemMsg = fake.LastHistory!.First();
            Assert.That(systemMsg.Role, Is.EqualTo(OrchestratorIDE.Models.MessageRole.System));
            Assert.That(systemMsg.Content, Is.EqualTo("You are a pirate."));
        });
    }

    [AvaloniaTest]
    public async Task OpenMode_send_withEmptySystemPrompt_injectsNoSystemMessage()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        Click(Required<ToggleButton>(panel, "BtnModeOpen"));
        Dispatcher.UIThread.RunJobs();
        // TbOpenSystemPrompt left empty deliberately.

        fake.Enqueue("ok");

        Required<TextBox>(panel, "TbInput").Text = "hello";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);

        // No system message at all -- the first message must be the user's own, per
        // ChatEngine's "explicit empty system prompt means inject nothing" contract
        // (see ChatEngineTests.ExplicitEmptyPromptAndTools_InjectsNothing).
        var first = fake.LastHistory!.First();
        Assert.That(first.Role, Is.EqualTo(OrchestratorIDE.Models.MessageRole.User));
    }

    [AvaloniaTest]
    public void ModeButtons_areDisabled_whileASendIsInFlight()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        // Multi-word response so SendAsync is still awaiting when we check -- FakeOllamaClient
        // streams word-by-word with a delay between each.
        fake.Enqueue("this is a longer reply with several words");

        Required<TextBox>(panel, "TbInput").Text = "hello";
        Click(Required<Button>(panel, "BtnSend"));
        Dispatcher.UIThread.RunJobs();   // let SendAsync start and reach its first await

        Assert.Multiple(() =>
        {
            Assert.That(Required<ToggleButton>(panel, "BtnModeResearch").IsEnabled, Is.False);
            Assert.That(Required<ToggleButton>(panel, "BtnModeOpen").IsEnabled, Is.False);
        });
    }

    /// <summary>
    /// Reproduces the exact race grok-review caught (2026-06-22): a mode-toggle click landing
    /// WHILE SendAsync is still awaiting a turn used to null out the field-referenced _engine
    /// from underneath the in-flight call's own `finally` unsubscribe, throwing a
    /// NullReferenceException. SendAsync now captures the engine in a local before the await,
    /// so this must complete cleanly even when the toggle fires mid-send.
    ///
    /// Invokes the private SendAsync via reflection rather than through BtnSend_Click --
    /// the Click handler does `_ = SendAsync()` (fire-and-forget), so an exception thrown
    /// inside it becomes an unobserved task exception that .NET swallows by default instead
    /// of failing the test. A first version of this test went through the button click and
    /// passed even with the bug still in place, for exactly that reason -- caught by manually
    /// reverting the fix and confirming the "passing" test didn't actually go red. Awaiting
    /// the reflected Task directly is what makes the exception observable to the test runner.
    /// </summary>
    [AvaloniaTest]
    public async Task ModeToggleDuringInFlightSend_doesNotThrow()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        fake.Enqueue("this is a longer reply with several words so the await has room to land a mode switch in the middle of it");

        Required<TextBox>(panel, "TbInput").Text = "hello";

        var sendMethod = typeof(ChatPanel).GetMethod("SendAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("SendAsync method not found via reflection.");

        var sendTask = (Task)sendMethod.Invoke(panel, null)!;

        // Let SendAsync start and reach its first await (the model's streamed response)
        // before landing the mode switch mid-flight.
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(5);
        Dispatcher.UIThread.RunJobs();

        // Fires the toggle's Click handler directly -- SetMode() nulls _engine and cancels
        // _cts right here, mid-send.
        Click(Required<ToggleButton>(panel, "BtnModeOpen"));
        Dispatcher.UIThread.RunJobs();

        // Pump until SendAsync actually finishes unwinding (cancelled), observing whatever
        // it throws instead of letting it become an unobserved task exception.
        for (var i = 0; i < 200 && !sendTask.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }

        Assert.That(sendTask.IsCompleted, Is.True, "SendAsync never completed.");
        Assert.That(sendTask.IsFaulted, Is.False,
            $"SendAsync threw: {sendTask.Exception?.InnerException}");
    }

    /// <summary>
    /// ChatPanel.SendAsync awaits the engine's full turn before returning, but the headless
    /// dispatcher needs a pump to actually run the queued continuations. A short bounded wait
    /// loop is simpler and less brittle than threading a TaskCompletionSource through
    /// FakeOllamaClient just for these tests.
    /// </summary>
    private static async Task WaitForCapture(FakeOllamaClient fake)
    {
        for (var i = 0; i < 100 && fake.LastHistory is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
