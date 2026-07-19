// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Native Runtime v2.0 Phase D (docs/NATIVE_RUNTIME_V2_SPEC.md §3.4, Phase D): "a native-routed
/// job with native prerequisites unavailable fails closed — explicit native error, no Ollama
/// substitution." Unlike the rest of Phase D's proof lane, this specific invariant does not need
/// a real GGUF load: a real admission DENIAL happens in <see cref="RuntimeOrchestrator.EnsureAdmitted"/>
/// before <see cref="SessionManager"/> ever opens a model file, so the whole path down to
/// <see cref="NativeWithFallbackRuntime"/>'s no-fallback decision can be exercised for real, every
/// CI run, without hardware gating — a stronger and cheaper guarantee than an opt-in lane alone.
///
/// This closes a gap the existing coverage left open: <see cref="NativeRuntimeTestSupportTests"/>'s
/// FallbackCoordinator_* tests drive a synthetic delegate-based coordinator with a lambda that
/// throws a manufactured exception; they prove the coordinator's own branching logic, not that a
/// REAL admission denial from the REAL scheduler actually surfaces as
/// <see cref="RuntimeAdmissionDeniedException"/> through the REAL <see cref="NativeWithFallbackRuntime"/>
/// wrapper without ever touching the fallback. This test wires the real objects together instead.
/// </summary>
[TestFixture]
public sealed class NativeRuntimePhaseDTests
{
    [Test]
    public async Task RealAdmissionDenial_PropagatesThroughFallbackWrapper_WithoutInvokingFallback()
    {
        // Empty depot (root: "") is never actually consulted -- roleBindings below is supplied
        // explicitly, and NativeRoleRuntime.StreamRoleCompletionAsync checks roleBindings before
        // ever falling back to _depot.ResolveRole. The path itself is never opened either: denial
        // happens in EnsureAdmitted, strictly before SessionManager.LoadBindingAsync would try.
        var depot = ModelDepot.Scan("");
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Worker,
            new RuntimeModelAsset(
                Id: "phase-d-fake-base",
                Kind: RuntimeAssetKind.BaseModelGguf,
                Path: "does-not-exist.gguf",
                DisplayName: "does-not-exist.gguf",
                SizeBytes: 1_000_000,
                LastModifiedUtc: DateTimeOffset.UnixEpoch,
                SuggestedRoles: [RuntimeRole.Worker]),
            Adapter: null);

        // A real OrcScheduler making a real capacity decision against a real (zero) budget --
        // not the "no scheduler configured" fail-closed branch Phase A already covers, but an
        // actual TryAdmit denial, the scenario Phase D's negative test names explicitly.
        await using var native = new NativeRoleRuntime(
            depot,
            options: null,
            scheduler: new OrcScheduler(),
            budgetProvider: () => new VramBudget(TotalBytes: 0, ReservedBytes: 0),
            roleBindings: new Dictionary<RuntimeRole, RuntimeRoleBinding> { [RuntimeRole.Worker] = binding });

        var fallback = new RecordingFallbackRuntime();
        var fallbackInvoked = false;
        await using var wrapped = new NativeWithFallbackRuntime(
            native,
            RuntimeRole.Worker,
            fallback,
            onFallback: _ => fallbackInvoked = true);

        var history = new List<AgentMessage> { new() { Role = MessageRole.User, Content = "hello" } };

        var ex = Assert.ThrowsAsync<RuntimeAdmissionDeniedException>(async () =>
        {
            await foreach (var _ in wrapped.StreamCompletionAsync("worker", history))
            {
                Assert.Fail("No token should ever be yielded when admission is denied before any load.");
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Decision.Admitted, Is.False);
            Assert.That(fallback.StreamCompletionCallCount, Is.Zero,
                "admission denial must fail closed -- the fallback (Ollama, by convention) must never be invoked");
            Assert.That(fallbackInvoked, Is.False,
                "onFallback is the caller-visible signal a substitution happened; it must not fire on a fail-closed denial");
        });
    }

    /// <summary>Minimal IModelRuntime whose only job is to prove it was (or wasn't) called.</summary>
    private sealed class RecordingFallbackRuntime : IModelRuntime
    {
        public int StreamCompletionCallCount { get; private set; }

        public string RuntimeName => "RecordingFallback";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());

        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            IEnumerable<AgentMessage> history,
            IReadOnlyList<object>? tools = null,
            double temperature = 0.1,
            double? topP = null,
            int maxTokens = 4096,
            Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            StreamCompletionCallCount++;
            await Task.Yield();
            yield return "fallback output -- should never be reached in this test";
        }

        public RuntimeHealth GetHealth() => new(IsAvailable: true, RuntimeName: "RecordingFallback");

        public RuntimeStats GetStats() => new("RecordingFallback");
    }
}
