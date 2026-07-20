// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Native Runtime v2.0 Phase D (docs/NATIVE_RUNTIME_V2_SPEC.md, Phase D `/verify`): "discovery →
/// selection → session → admission → adapter → inference → <b>cancellation</b> → telemetry →
/// no-fallback." This covers the cancellation leg specifically, on a real loaded model:
/// cancelling mid-stream must both propagate the cancellation to the caller AND leave the
/// persistent per-role executor in a reusable state, not a broken one -- <see cref="AdapterManager"/>'s
/// per-role context is long-lived across calls (docs/RUNTIME_PHASE0_SPEC.md §7a), so a
/// cancelled request corrupting it would degrade every subsequent request on that role, not just
/// the one that was cancelled.
///
/// Gated on THEORC_TEST_GGUF -- unlike <see cref="NativeRuntimePhaseDTests"/>'s admission-denial
/// test, this genuinely needs a loaded model and live token generation to cancel mid-stream.
/// </summary>
[TestFixture]
public sealed class NativeRuntimePhaseDCancellationTests
{
    [Test]
    public async Task Cancellation_MidGeneration_PropagatesAndLeavesRoleReusable()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this cancellation lane.");

        var root = Path.GetDirectoryName(Path.GetFullPath(ggufPath!));
        if (string.IsNullOrWhiteSpace(root))
            Assert.Fail("THEORC_TEST_GGUF must point to a GGUF file.");

        // allowUnbudgetedExecution: this lane proves cancellation/lifecycle behavior, not
        // admission -- same sanctioned harness opt-out as the other THEORC_TEST_GGUF-gated
        // lanes in NativeRuntimeTestSupportTests.cs.
        await using var runtime = new NativeRoleRuntime(
            ModelDepot.Scan(root!),
            new RuntimeOptions(ContextLength: 2048, GpuLayers: -1),
            allowUnbudgetedExecution: true);

        var history = new List<AgentMessage>
        {
            new() { Role = MessageRole.User, Content = "Write a long, detailed short story about a dragon who learns to fly." },
        };

        using var cts = new CancellationTokenSource();
        await using var enumerator = runtime
            .StreamRoleCompletionAsync(RuntimeRole.Worker, history, maxTokens: 300, ct: cts.Token)
            .GetAsyncEnumerator(cts.Token);

        // Consume real tokens before cancelling -- proves this is a genuine mid-stream cancel,
        // not a cancel-before-any-output race. maxTokens: 300 on a real model guarantees at
        // least this many tokens are available to observe before natural completion.
        Assert.That(await enumerator.MoveNextAsync(), Is.True, "expected at least one token before cancelling");
        Assert.That(await enumerator.MoveNextAsync(), Is.True, "expected a second token before cancelling");

        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
                // Drain until the cancellation actually surfaces -- LLamaSharp's Infer(ct) may
                // not throw on the very next call if a decode step was already in flight.
            }
        });

        // The TrackedConversation's disposal (StreamRoleCompletionCoreAsync's `using var tracked`)
        // runs as part of the async-iterator state machine's cleanup when the enumerator itself
        // is disposed below -- same guarantee the load-measurement Suppress() fix (PR #77) relies
        // on for its own `using` scope. Residency must show the role back at zero active
        // conversations afterward, not a leaked count from the cancelled one.
        await enumerator.DisposeAsync();

        var residencyAfterCancel = runtime.GetResidencySnapshot();
        var workerResidency = residencyAfterCancel.FirstOrDefault(r => r.Role == RuntimeRole.Worker);
        Assert.That(workerResidency?.ActiveCount ?? 0, Is.Zero,
            "cancelling mid-generation must not leak the cancelled TrackedConversation's active count");

        // The real proof the persistent per-role executor was not left in a broken state: a
        // fresh, uncancelled request on the SAME role afterward must succeed normally.
        var secondAttempt = await NativeRuntimeTestRunner.RunRoleAsync(runtime, RuntimeRole.Worker, ggufPath!);
        Assert.That(secondAttempt.Success, Is.True,
            secondAttempt.ErrorMessage ?? "a role should serve a normal request after a prior cancellation on the same role");
    }

    /// <summary>
    /// Baseline control for <see cref="Cancellation_MidGeneration_PropagatesAndLeavesRoleReusable"/>:
    /// proves two sequential UNCANCELLED calls on the same persistent role executor succeed on
    /// their own, isolating that test's cleanup assertion to cancellation specifically rather
    /// than "a second call on this path is broken regardless." Also stands on its own as the
    /// only coverage of basic persistent-executor reuse across two calls -- no prior test in
    /// this workstream called the same NativeRoleRuntime instance twice.
    /// </summary>
    [Test]
    public async Task TwoSequentialUncancelledCalls_OnSameRole_BothSucceed()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this cancellation lane.");

        var root = Path.GetDirectoryName(Path.GetFullPath(ggufPath!));
        await using var runtime = new NativeRoleRuntime(
            ModelDepot.Scan(root!),
            new RuntimeOptions(ContextLength: 2048, GpuLayers: -1),
            allowUnbudgetedExecution: true);

        var first = await NativeRuntimeTestRunner.RunRoleAsync(runtime, RuntimeRole.Worker, ggufPath!);
        Assert.That(first.Success, Is.True, "first call: " + first.ErrorMessage);

        var second = await NativeRuntimeTestRunner.RunRoleAsync(runtime, RuntimeRole.Worker, ggufPath!);
        Assert.That(second.Success, Is.True, "second call (no cancellation involved): " + second.ErrorMessage);
    }
}
