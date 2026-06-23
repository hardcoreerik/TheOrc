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
/// Headless coverage for OrcChat -- the single merged chat surface (formerly separate
/// Research/Open modes, collapsed 2026-06-22 per user request). Verifies the actual engine
/// wiring: tools are always available, no system prompt is injected unless the user sets one,
/// and the system-prompt/temperature/top-p textboxes thread through to the runtime. Temp/Top-P
/// are plain TextBoxes, not NumericUpDown -- a real user testing session found NumericUpDown's
/// value invisible under this app's dark theme (its templated inner TextBox part doesn't
/// inherit the outer Foreground/Background), which headless tests had no way to catch since
/// they verify wiring, not visual rendering.
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
    public void DefaultState_showsSystemPromptControls_andOrcChatWelcome()
    {
        var panel = new ChatPanel();

        Assert.Multiple(() =>
        {
            Assert.That(Required<Border>(panel, "BdrOpenControls").IsVisible, Is.True);
            Assert.That(Required<TextBlock>(panel, "TxtWelcomeTitle").Text, Is.EqualTo("👋  OrcChat"));
        });
    }

    [AvaloniaTest]
    public async Task Send_withNoSystemPrompt_threadsToolsAndDateTimeOnly()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");
        // TbOpenSystemPrompt left empty deliberately.

        fake.Enqueue("ok");

        Required<TextBox>(panel, "TbInput").Text = "hello";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);

        // OrcChat turns on IncludeDateTimeContext (the model otherwise has no way to know the
        // date), so an empty system-prompt textbox no longer means "no system message at all"
        // -- it means "just the date/time line." Tools must still be available even with no
        // system prompt at all -- "no injected prompt" and "no tools" are separate concerns now.
        var first = fake.LastHistory!.First();
        Assert.Multiple(() =>
        {
            Assert.That(first.Role, Is.EqualTo(OrchestratorIDE.Models.MessageRole.System));
            Assert.That(first.Content, Does.Contain("Current date and time:"));
            Assert.That(fake.LastTools, Is.Not.Null.And.Count.GreaterThan(0));
        });
    }

    [AvaloniaTest]
    public async Task Send_threadsTextboxAndNumericValuesIntoTheRequest()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

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
            Assert.That(fake.LastTools, Is.Not.Null.And.Count.GreaterThan(0));

            // The system prompt textbox's value must reach the model as an actual system
            // message, alongside the always-on date/time grounding line.
            var systemMsg = fake.LastHistory!.First();
            Assert.That(systemMsg.Role, Is.EqualTo(OrchestratorIDE.Models.MessageRole.System));
            Assert.That(systemMsg.Content, Does.Contain("Current date and time:"));
            Assert.That(systemMsg.Content, Does.Contain("You are a pirate."));
        });
    }

    /// <summary>
    /// Reproduces the exact race grok-review caught (2026-06-22): a control change landing
    /// WHILE SendAsync is still awaiting a turn used to null out the field-referenced _engine
    /// from underneath the in-flight call's own `finally` unsubscribe, throwing a
    /// NullReferenceException. SendAsync captures the engine in a local before the await, so
    /// this must complete cleanly even when something else nulls _engine mid-send -- here
    /// reproduced via BtnClear, the remaining UI action that nulls/clears the engine while a
    /// send could be in flight (the original mode-toggle buttons no longer exist).
    ///
    /// Invokes the private SendAsync via reflection rather than through BtnSend_Click --
    /// the Click handler does `_ = SendAsync()` (fire-and-forget), so an exception thrown
    /// inside it becomes an unobserved task exception that .NET swallows by default instead
    /// of failing the test. Awaiting the reflected Task directly is what makes the exception
    /// observable to the test runner.
    /// </summary>
    [AvaloniaTest]
    public async Task ClearDuringInFlightSend_doesNotThrow()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        fake.Enqueue("this is a longer reply with several words so the await has room to land a clear in the middle of it");

        Required<TextBox>(panel, "TbInput").Text = "hello";

        var sendMethod = typeof(ChatPanel).GetMethod("SendAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("SendAsync method not found via reflection.");

        var sendTask = (Task)sendMethod.Invoke(panel, null)!;

        // Let SendAsync start and reach its first await (the model's streamed response)
        // before landing the clear mid-flight.
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(5);
        Dispatcher.UIThread.RunJobs();

        // Fires the Clear button's Click handler directly -- nulls _engine and cancels _cts
        // right here, mid-send.
        Click(Required<Button>(panel, "BtnClear"));
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
    /// Reproduces the InsertToolChip race grok-review caught (2026-06-23): a tool call's
    /// OnToolStart event can land after Clear has already reset the conversation mid-flight
    /// (the cancellation token is only checked at certain points in the engine's tool loop,
    /// not synchronously, so an in-flight tool call can still fire after _streamBox is
    /// nulled). Before the fix, InsertToolChip's "no anchor bubble" fallback would Add the
    /// orphan chip straight into whatever's in ChatStack now -- the freshly-reset welcome
    /// card. Starts a real send (so _streamBox is genuinely set, matching the real event-
    /// handler shape), clicks Clear mid-flight (nulling it, same as BtnClear_Click really
    /// does), then invokes the private OnToolStart handler directly to simulate the late-
    /// arriving tool-call event, asserting no chip lands in ChatStack.
    /// </summary>
    [AvaloniaTest]
    public async Task OnToolStart_afterClearMidSend_doesNotInsertOrphanChip()
    {
        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");

        fake.Enqueue("this is a longer reply with several words so the await has room to land a clear in the middle of it");

        Required<TextBox>(panel, "TbInput").Text = "hello";

        var sendMethod = typeof(ChatPanel).GetMethod("SendAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("SendAsync method not found via reflection.");
        var sendTask = (Task)sendMethod.Invoke(panel, null)!;

        Dispatcher.UIThread.RunJobs();
        await Task.Delay(5);
        Dispatcher.UIThread.RunJobs();

        Click(Required<Button>(panel, "BtnClear"));
        Dispatcher.UIThread.RunJobs();

        var childCountAfterClear = Required<StackPanel>(panel, "ChatStack").Children.Count;

        var onToolStart = typeof(ChatPanel).GetMethod("OnToolStart",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnToolStart method not found via reflection.");
        onToolStart.Invoke(panel, ["web_search", "{}"]);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(Required<StackPanel>(panel, "ChatStack").Children.Count, Is.EqualTo(childCountAfterClear));
            Assert.That(Required<Border>(panel, "BdrSearching").IsVisible, Is.False);
        });

        for (var i = 0; i < 200 && !sendTask.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
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
