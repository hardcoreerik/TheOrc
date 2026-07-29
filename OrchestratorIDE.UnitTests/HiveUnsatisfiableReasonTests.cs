// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// The queue-side half of the unsatisfiable-unit diagnostic: WHICH rejections deserve to be
/// reported as "this unit cannot run", and which are just normal dispatch.
///
/// That distinction is the whole risk in this feature, and getting it wrong was worse than not
/// having the feature at all. The first version recorded an <c>ExcludedWorkerIds</c> rejection as a
/// reason — but every HV driver pins its work units by excluding the other boxes, so a perfectly
/// satisfiable unit accumulated a "reason" from every worker it was deliberately kept away from and
/// then reported itself unrunnable. HV-5's own driver, having learned to trust the field, stopped
/// waiting and declared a healthy unit undiagnosable before its intended worker had even polled
/// (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-5, 2026-07-27).
///
/// So these tests are mostly about SILENCE being correct: a unit that is merely pinned, or waiting
/// on a lane, must report nothing.
/// </summary>
[TestFixture]
public sealed class HiveUnsatisfiableReasonTests
{
    private const string TaskId = "campaign-1-unit-1";

    private static HiveLeaseRequest Poll(
        string workerId,
        string[]? modelHashes = null,
        string[]? lanes = null) => new()
    {
        WorkerId = workerId,
        Lanes = lanes ?? [],
        Capabilities = new WorkerCapabilities
        {
            WorkerId = workerId,
            CpuCores = 16,
            AvailableMemoryMb = 32_000,
            FreeVramMb = 6_000,
            ExecutionKinds = [HiveExecutionKinds.NativeAgent],
            NativeModelHashes = modelHashes ?? ["a".PadRight(64, 'a')],
        },
    };

    [Test]
    public void APinnedUnit_ReportsNothingWhenAnExcludedWorkerPolls()
    {
        // THE regression. The unit is pinned to HardcorePC; HardcoreLaptopMSI polling it is normal
        // targeting, not a diagnosis. Reporting it here is what broke a healthy HV-5 run.
        var queue = new HiveTaskQueue();
        queue.SeedPendingCampaignUnitForTest(TaskId, new ResourceRequirements
        {
            ExcludedWorkerIds = ["HardcoreLaptopMSI"],
        });

        queue.RecordIneligibilityForTest(Poll("HardcoreLaptopMSI"));

        Assert.That(queue.GetUnsatisfiableReasonForTest(TaskId), Is.Null,
            "an exclusion is targeting, not unsatisfiability");
    }

    [Test]
    public void AUnitWaitingOnALane_ReportsNothing()
    {
        // A worker that only takes Researcher work declining a Coder unit is declining a ROLE.
        var queue = new HiveTaskQueue();
        queue.SeedPendingCampaignUnitForTest(TaskId, new ResourceRequirements());

        queue.RecordIneligibilityForTest(Poll("HardcorePC", lanes: ["researcher"]));

        Assert.That(queue.GetUnsatisfiableReasonForTest(TaskId), Is.Null);
    }

    [Test]
    public void ASatisfiableUnit_ReportsNothing()
    {
        var queue = new HiveTaskQueue();
        queue.SeedPendingCampaignUnitForTest(TaskId, new ResourceRequirements());

        queue.RecordIneligibilityForTest(Poll("HardcorePC"));

        Assert.That(queue.GetUnsatisfiableReasonForTest(TaskId), Is.Null);
    }

    [Test]
    public void AGenuinelyImpossibleUnit_NamesEveryWorkerThatDeclinedAndWhy()
    {
        // The case the feature exists for: a requirement no box can meet. Previously silent forever,
        // because campaign units are exempt from PendingTimeoutSec.
        var queue = new HiveTaskQueue();
        queue.SeedPendingCampaignUnitForTest(TaskId, new ResourceRequirements
        {
            NativeModelHash = new string('0', 64),
        });

        queue.RecordIneligibilityForTest(Poll("HardcorePC"));
        queue.RecordIneligibilityForTest(Poll("HardcoreLaptopMSI"));

        var reason = queue.GetUnsatisfiableReasonForTest(TaskId);
        Assert.That(reason, Is.Not.Null);
        Assert.That(reason, Does.Contain("HardcorePC"));
        Assert.That(reason, Does.Contain("HardcoreLaptopMSI"));
        Assert.That(reason, Does.Contain("native model hash"));
        // Scoped to what the queue can actually know. It has no live worker roster — it learns a
        // worker exists only when that worker polls — so a claim about "every live worker" would be
        // a claim about a set it cannot see, and wrong the moment a capable box was slow to poll.
        Assert.That(reason, Does.Contain("has polled"));
        Assert.That(reason, Does.Not.Contain("No live worker"));
    }

    [Test]
    public void RepeatedPollsFromTheSameWorker_DoNotAccumulate()
    {
        var queue = new HiveTaskQueue();
        queue.SeedPendingCampaignUnitForTest(TaskId, new ResourceRequirements
        {
            NativeModelHash = new string('0', 64),
        });

        for (var i = 0; i < 5; i++) queue.RecordIneligibilityForTest(Poll("HardcorePC"));

        var reason = queue.GetUnsatisfiableReasonForTest(TaskId)!;
        Assert.That(reason, Does.Contain("(1 declined)"),
            "one worker declining five times is still one worker");
    }

    [Test]
    public void AClaimedUnit_ReportsNothing()
    {
        // Whatever a unit accumulated while it waited must not follow it into execution, or the
        // diagnostic reads as stale the moment the unit actually runs.
        var queue = new HiveTaskQueue();
        queue.SeedPendingCampaignUnitForTest(TaskId, new ResourceRequirements
        {
            NativeModelHash = new string('0', 64),
        });
        queue.RecordIneligibilityForTest(Poll("HardcorePC"));
        Assert.That(queue.GetUnsatisfiableReasonForTest(TaskId), Is.Not.Null, "precondition");

        queue.SetStatusForTest(TaskId, "claimed");

        Assert.That(queue.GetUnsatisfiableReasonForTest(TaskId), Is.Null);
    }
}
