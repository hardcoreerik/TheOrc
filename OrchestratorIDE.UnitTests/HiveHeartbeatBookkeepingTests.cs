// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Does a heartbeat for a live claim actually advance <c>LastHeartbeat</c>?
///
/// That question sat unanswered through three fleet-driven attempts at HV-3's heartbeat-timeout
/// failure (send timeout 5s->20s, a dedicated heartbeat thread, and the queue-side 200->409), of
/// which only one had a confirmed effect — each costing a worker restart, a cold 4.7 GB model
/// load and several minutes per iteration, with a final "validation" run that was void because
/// the probe job finished in 9.6s and the first beat is not due until claim+10s
/// (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-3, 2026-07-27).
///
/// It is answerable in milliseconds. These tests separate the two things the fleet could not:
/// DELIVERY (did a beat arrive) from BOOKKEEPING (did an arriving beat get credited), by driving
/// HeartbeatCoreAsync directly against a seeded queue entry.
/// </summary>
[TestFixture]
public sealed class HiveHeartbeatBookkeepingTests
{
    private const string TaskId = "campaign-1-unit-1";
    private const string Token  = "claim-token-abc";

    [Test]
    public async Task Heartbeat_ForALiveClaim_AdvancesLastHeartbeat()
    {
        // The core claim the whole watchdog rests on. If this ever fails, a healthy worker is
        // declared dead no matter how reliably it sends.
        var queue = new HiveTaskQueue();
        queue.SeedClaimedTaskForTest(TaskId, Token);

        var before = queue.GetLastHeartbeatForTest(TaskId);
        Assert.That(before, Is.Not.Null);

        // 40ms, not 20ms: DateTime.UtcNow advances in ~15.6ms increments on default Windows timer
        // resolution, so 20ms left two beats close enough to land on the same tick and fail
        // Is.GreaterThan intermittently in CI. Found by CodeRabbit's review of this PR.
        await Task.Delay(40);
        var outcome = await queue.HeartbeatCoreAsync(TaskId, Token);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(HiveTaskQueue.HeartbeatOutcome.Credited));
            Assert.That(queue.GetLastHeartbeatForTest(TaskId), Is.GreaterThan(before!.Value),
                "a credited beat must move LastHeartbeat forward, or the watchdog will re-queue a live job");
        });
    }

    [Test]
    public async Task Heartbeat_AfterRequeue_IsNotCredited()
    {
        // The silent cascade. Once the watchdog re-queues, Status leaves "claimed" while the
        // worker is still running and still beating. This must NOT be credited (the lease is
        // gone) and must be reported as a distinct outcome so the worker can act on it — the
        // previous code answered HTTP 200 here, which the worker read as success, so it kept
        // executing a lease it had lost while the task was re-claimed and run again.
        var queue = new HiveTaskQueue();
        queue.SeedClaimedTaskForTest(TaskId, Token);
        var before = queue.GetLastHeartbeatForTest(TaskId);

        queue.SetStatusForTest(TaskId, "pending");   // exactly what CheckTimeouts does on re-queue

        var outcome = await queue.HeartbeatCoreAsync(TaskId, Token);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(HiveTaskQueue.HeartbeatOutcome.NotClaimed));
            Assert.That(queue.GetLastHeartbeatForTest(TaskId), Is.EqualTo(before),
                "a beat for a lease that was taken away must not refresh the entry");
        });
    }

    [Test]
    public async Task Heartbeat_WithRotatedToken_IsRejectedAsStale()
    {
        // Re-claim rotates ClaimToken. The old worker's beats must be distinguishable from the
        // new claimant's, or a zombie could keep a re-assigned task alive.
        var queue = new HiveTaskQueue();
        queue.SeedClaimedTaskForTest(TaskId, Token);
        var before = queue.GetLastHeartbeatForTest(TaskId);

        var outcome = await queue.HeartbeatCoreAsync(TaskId, "some-older-token");

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(HiveTaskQueue.HeartbeatOutcome.StaleToken));
            Assert.That(queue.GetLastHeartbeatForTest(TaskId), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task Heartbeat_ForAnUnknownTask_IsNotClaimed()
    {
        var queue = new HiveTaskQueue();

        Assert.That(await queue.HeartbeatCoreAsync("no-such-task", Token),
                    Is.EqualTo(HiveTaskQueue.HeartbeatOutcome.NotClaimed));
    }

    [Test]
    public async Task RepeatedHeartbeats_KeepAdvancing_OverAJobLongerThanTheTimeoutWindow()
    {
        // The fleet scenario in miniature: a job that outlives HeartbeatTimeoutSec (45s) survives
        // only if EVERY beat is credited, not just the first. Simulated at speed — the point is
        // that repetition does not degrade, which no single-beat assertion covers.
        var queue = new HiveTaskQueue();
        queue.SeedClaimedTaskForTest(TaskId, Token);

        var last = queue.GetLastHeartbeatForTest(TaskId)!.Value;

        for (var beat = 0; beat < 5; beat++)
        {
            await Task.Delay(40);  // same tick-granularity reasoning as the single-beat test above
            var outcome = await queue.HeartbeatCoreAsync(TaskId, Token);
            var now = queue.GetLastHeartbeatForTest(TaskId)!.Value;

            Assert.That(outcome, Is.EqualTo(HiveTaskQueue.HeartbeatOutcome.Credited), $"beat {beat}");
            Assert.That(now, Is.GreaterThan(last), $"beat {beat} did not advance LastHeartbeat");
            last = now;
        }
    }
}
