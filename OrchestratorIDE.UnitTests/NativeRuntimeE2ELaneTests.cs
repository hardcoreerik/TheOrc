// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Native Runtime v2.0 Phase D (docs/NATIVE_RUNTIME_V2_SPEC.md §3.2/§3.3): the standardized
/// real-model E2E lane -- discovery, selection, session, ADMISSION (with a real scheduler and a
/// real live VRAM budget, not the `allowUnbudgetedExecution` harness opt-out every other gated
/// lane in this workstream uses), adapter lifecycle, inference, and telemetry read before/
/// mid-flight/after, with a retained evidence artifact. Cancellation (§3.2 item 6) and
/// no-silent-fallback (§3.2 item 8) are proven separately and more rigorously in
/// <see cref="NativeRuntimePhaseDCancellationTests"/> and <see cref="NativeRuntimePhaseDTests"/>;
/// this lane's job is proving the HAPPY PATH end-to-end as one real, admitted, measured run --
/// the piece those two don't cover, and the one still open per the spec's Phase D DoD.
/// </summary>
[TestFixture]
public sealed class NativeRuntimeE2ELaneTests
{
    [Test]
    public async Task FullLifecycle_DiscoveryThroughTelemetry_Succeeds_WithRetainedEvidence()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run the native E2E lane.");

        var root = Path.GetDirectoryName(Path.GetFullPath(ggufPath!));
        if (string.IsNullOrWhiteSpace(root))
            Assert.Fail("THEORC_TEST_GGUF must point to a GGUF file.");

        // 1. Discovery.
        var depot = ModelDepot.Scan(root!);

        // Real scheduler + a real live-queried budget -- deliberately NOT allowUnbudgetedExecution.
        // Every other THEORC_TEST_GGUF-gated lane in this workstream opts out of admission to
        // isolate load/generate/stats; this lane's whole point is proving admission for real,
        // on real hardware, as part of one continuous run.
        var liveBudget = NativeVramProbe.TryQueryLiveNvidiaBudget();
        if (liveBudget is null)
            Assert.Inconclusive("nvidia-smi unavailable on this box -- cannot exercise real admission here.");

        await using var runtime = new NativeRoleRuntime(
            depot,
            new RuntimeOptions(ContextLength: 2048, GpuLayers: -1),
            scheduler: new OrcScheduler(),
            budgetProvider: () => liveBudget);

        var snapshots = new List<NativeE2ELaneTelemetrySnapshot>
        {
            CaptureSnapshot("before", runtime),
        };

        string? output = null;
        string? errorMessage = null;
        var success = false;

        // Bounded: a real-hardware stall (driver/GPU issue) during this real streaming call
        // must fail the test with retained evidence, not hang the run indefinitely
        // (CodeRabbit finding on the PR that introduced this lane).
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            var messages = NativeRuntimeTestPrompt.BuildMessages(NativeRuntimeTestPrompt.PromptText);
            var sb = new StringBuilder();
            var capturedMidFlight = false;

            // 2-5: selection, session creation, admission, adapter lifecycle, and real inference
            // all happen inside this one call -- StreamRoleCompletionAsync is the real public
            // entry point a caller actually uses, not an internal shortcut.
            await foreach (var token in runtime.StreamRoleCompletionAsync(RuntimeRole.Worker, messages, maxTokens: 64, ct: cts.Token))
            {
                sb.Append(token);
                if (!capturedMidFlight)
                {
                    // 7 (partial): telemetry read WHILE the role is still resident and generating,
                    // not just before/after -- the conversation is only disposed once this
                    // foreach loop's enumerator completes or is disposed, so mid-loop is genuinely
                    // mid-flight, not a race.
                    snapshots.Add(CaptureSnapshot("mid-flight", runtime));
                    capturedMidFlight = true;
                }
            }

            output = sb.ToString();
            success = !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        // 7 (rest): telemetry after the conversation's own `using` scope has disposed it --
        // residency must show the role back at zero active, not a leaked count.
        snapshots.Add(CaptureSnapshot("after", runtime));

        var stats = runtime.GetStats(RuntimeRole.Worker);
        var binding = depot.ResolveRole(RuntimeRole.Worker);

        var result = new NativeE2ELaneRunResult(
            success,
            runtime.RuntimeName,
            RuntimeRole.Worker.ToString(),
            binding?.BaseModel.DisplayName ?? "unresolved",
            binding?.Adapter?.DisplayName,
            snapshots,
            stats.TokensPerSecond,
            stats.LastTimeToFirstToken,
            stats.EstimatedVramBytes,
            output,
            errorMessage);

        // Explicit workspace root: without it, WriteAsync falls back to %AppData%, scattering
        // evidence outside the repo instead of the discoverable .orc/native-e2e-lane/ convention
        // every other evidence store in this workstream uses (CodeRabbit finding).
        var evidencePath = await NativeE2ELaneEvidenceStore.WriteAsync(result, FindRepoRoot());
        TestContext.WriteLine($"Native E2E lane evidence written to: {evidencePath}");

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True, errorMessage ?? "E2E lane run failed with no output");
            Assert.That(File.Exists(evidencePath), Is.True, "evidence artifact must be retained regardless of outcome");
            Assert.That(snapshots, Has.Count.EqualTo(3), "expected before/mid-flight/after telemetry stages");
            Assert.That(snapshots[0].ResidentActiveCount ?? 0, Is.Zero, "nothing should be resident before the first call");
            Assert.That(snapshots[1].ResidentActiveCount, Is.EqualTo(1), "exactly one conversation should be active mid-flight");
            Assert.That(snapshots[2].ResidentActiveCount ?? 0, Is.Zero, "residency must return to baseline after disposal");
        });
    }

    private static NativeE2ELaneTelemetrySnapshot CaptureSnapshot(string stage, NativeRoleRuntime runtime)
    {
        var reservation = runtime.GetReservationSnapshot();
        var residency = runtime.GetResidencySnapshot();
        var workerResidency = residency.FirstOrDefault(r => r.Role == RuntimeRole.Worker);

        return new NativeE2ELaneTelemetrySnapshot(
            stage,
            reservation?.TotalBytes,
            reservation?.ReservedBytes,
            reservation?.AvailableBytes,
            workerResidency?.ActiveCount,
            workerResidency?.Status.ToString());
    }

    /// <summary>Walks up from the test binary's output directory to the repo root (marked by
    /// the solution file), so retained evidence lands in the discoverable .orc/native-e2e-lane/
    /// convention instead of NativeE2ELaneEvidenceStore's %AppData% fallback. Returns null (same
    /// fallback behavior as before) if no marker is found, e.g. a packaged test run outside a
    /// full checkout.</summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("OrchestratorIDE.slnx").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
