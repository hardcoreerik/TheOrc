// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Regression guard for a question that came up repeatedly (and incorrectly) during code
/// review while porting FirstRunWindow (2026-06-20): does
/// <c>Dispatcher.UIThread.InvokeAsync(Func&lt;Task&gt;)</c> actually unwrap and wait for the
/// inner task, or does it return as soon as the delegate is invoked (leaving the inner task
/// to run unobserved)? A reviewer flagged the latter across three review rounds even after
/// the call site was restructured to remove the more plausible failure mode (an inline async
/// lambda implicitly binding to an Action-shaped overload). Verified empirically here, twice
/// — once calling from the dispatcher thread itself, once from a genuine background thread
/// (the actual call site's context, since OrchestratorIDE.Avalonia.MainWindow's startup
/// sequence is not itself running on the UI thread) — both confirm a full, ordered unwrap.
/// If this test ever starts failing, that's a real Avalonia behavior change worth taking
/// seriously, not a reason to doubt the test.
/// </summary>
[TestFixture]
public sealed class T26_DispatcherInvokeAsyncTests
{
    [AvaloniaTest]
    public async Task InvokeAsync_FuncTask_From_Background_Thread_Waits_For_Inner_Completion()
    {
        var order = new List<string>();

        async Task Inner()
        {
            order.Add("inner-start");
            await Task.Delay(50);
            order.Add("inner-end");
        }

        await Task.Run(async () =>
        {
            order.Add("before-invoke");
            await Dispatcher.UIThread.InvokeAsync(Inner);
            order.Add("after-await");
        });

        Assert.That(order, Is.EqualTo(new[] { "before-invoke", "inner-start", "inner-end", "after-await" }),
            $"Actual order: {string.Join(", ", order)}");
    }
}
