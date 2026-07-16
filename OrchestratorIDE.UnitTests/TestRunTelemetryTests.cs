// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class TestRunTelemetryTests
{
    private static TestRunTelemetryModel NewModel(Func<DateTimeOffset>? clock = null)
    {
        var m = new TestRunTelemetryModel(clock);
        m.StartRun([("init", "Initialize"), ("bench", "Bench model"), ("save", "Save report")], totalSamples: 10);
        return m;
    }

    [Test]
    public void StartRun_InitializesStagesQueuedAndPhaseRunning()
    {
        var m = NewModel();
        Assert.Multiple(() =>
        {
            Assert.That(m.Phase, Is.EqualTo(TestRunPhase.Running));
            Assert.That(m.Stages, Has.Count.EqualTo(3));
            Assert.That(m.Stages.All(s => s.Status == TestStageStatus.Queued), Is.True);
            Assert.That(m.TotalSamples, Is.EqualTo(10));
            Assert.That(m.CompletedSamples, Is.Zero);
        });
    }

    [Test]
    public void StageLifecycle_ActiveThenCompleted_TracksActiveStage()
    {
        var m = NewModel();
        m.StageStarted("bench", samplesInStage: 5);
        Assert.Multiple(() =>
        {
            Assert.That(m.ActiveStage?.Id, Is.EqualTo("bench"));
            Assert.That(m.ActiveStage?.Status, Is.EqualTo(TestStageStatus.Active));
        });

        m.StageEnded("bench", TestStageStatus.Completed, "5/5");
        Assert.Multiple(() =>
        {
            Assert.That(m.ActiveStage, Is.Null);
            Assert.That(m.Stages[1].Status, Is.EqualTo(TestStageStatus.Completed));
            Assert.That(m.Stages[1].Detail, Is.EqualTo("5/5"));
        });
    }

    [Test]
    public void SampleCompleted_CountsVerdictsAndStageProgress()
    {
        var m = NewModel();
        m.StageStarted("bench", samplesInStage: 4);
        m.SampleCompleted(TestActivityKind.Success);
        m.SampleCompleted(TestActivityKind.Failure);
        m.SampleCompleted(TestActivityKind.Warning);
        m.SampleCompleted(TestActivityKind.Failure, errored: true);

        Assert.Multiple(() =>
        {
            Assert.That(m.CompletedSamples, Is.EqualTo(4));
            Assert.That(m.PassedSamples,    Is.EqualTo(1));
            Assert.That(m.FailedSamples,    Is.EqualTo(2));
            Assert.That(m.WarningSamples,   Is.EqualTo(1));
            Assert.That(m.ErrorSamples,     Is.EqualTo(1));
            Assert.That(m.ActiveStage?.SamplesCompleted, Is.EqualTo(4));
            Assert.That(m.ActiveStage?.Detail, Is.EqualTo("4/4"));
            Assert.That(m.FractionComplete, Is.EqualTo(0.4).Within(1e-9));
        });
    }

    [Test]
    public void PauseResume_OnlyTogglesFromExpectedPhases()
    {
        var m = NewModel();
        m.PauseRun();
        Assert.That(m.Phase, Is.EqualTo(TestRunPhase.Paused));
        m.PauseRun(); // no-op when already paused
        Assert.That(m.Phase, Is.EqualTo(TestRunPhase.Paused));
        m.ResumeRun();
        Assert.That(m.Phase, Is.EqualTo(TestRunPhase.Running));
        m.ResumeRun(); // no-op when running
        Assert.That(m.Phase, Is.EqualTo(TestRunPhase.Running));
    }

    [Test]
    public void EndRun_Cancelled_MarksUnfinishedStagesCancelled()
    {
        var m = NewModel();
        m.StageStarted("init");
        m.StageEnded("init", TestStageStatus.Completed);
        m.StageStarted("bench", 5);
        m.BeginCancel();
        Assert.That(m.Phase, Is.EqualTo(TestRunPhase.Cancelling));

        m.EndRun(TestRunPhase.Cancelled, "Cancelled.");
        Assert.Multiple(() =>
        {
            Assert.That(m.Phase, Is.EqualTo(TestRunPhase.Cancelled));
            Assert.That(m.Stages[0].Status, Is.EqualTo(TestStageStatus.Completed));
            Assert.That(m.Stages[1].Status, Is.EqualTo(TestStageStatus.Cancelled));
            Assert.That(m.Stages[2].Status, Is.EqualTo(TestStageStatus.Cancelled));
            Assert.That(m.IsRunActive, Is.False);
        });
    }

    [Test]
    public void EndRun_Failed_MarksActiveStageFailedAndQueuedCancelled()
    {
        var m = NewModel();
        m.StageStarted("bench", 5);
        m.EndRun(TestRunPhase.Failed, "Bench failed: boom");
        Assert.Multiple(() =>
        {
            Assert.That(m.Stages[1].Status, Is.EqualTo(TestStageStatus.Failed));
            Assert.That(m.Stages[0].Status, Is.EqualTo(TestStageStatus.Cancelled));
            Assert.That(m.Stages[2].Status, Is.EqualTo(TestStageStatus.Cancelled));
        });
    }

    [Test]
    public void EndRun_RejectsNonTerminalPhase()
    {
        var m = NewModel();
        Assert.Throws<ArgumentOutOfRangeException>(() => m.EndRun(TestRunPhase.Running, "x"));
    }

    [Test]
    public void ActivityFeed_IsCappedSoLongRunsCannotGrowMemory()
    {
        var m = NewModel();
        for (var i = 0; i < TestRunTelemetryModel.ActivityFeedCap + 50; i++)
            m.AddActivity(TestActivityKind.Info, $"entry {i}");

        Assert.Multiple(() =>
        {
            Assert.That(m.Activity, Has.Count.EqualTo(TestRunTelemetryModel.ActivityFeedCap));
            Assert.That(m.Activity[^1].Message, Is.EqualTo($"entry {TestRunTelemetryModel.ActivityFeedCap + 49}"));
        });
    }

    [Test]
    public void Elapsed_UsesInjectedClock_AndFreezesAtEndRun()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var m = new TestRunTelemetryModel(() => now);
        m.StartRun([("a", "A")], 1);
        now = now.AddSeconds(90);
        Assert.That(m.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(90)));

        m.EndRun(TestRunPhase.Completed, "Done.");
        now = now.AddSeconds(500);
        Assert.That(m.Elapsed, Is.EqualTo(TimeSpan.FromSeconds(90)), "elapsed must freeze once the run ends");
    }

    [Test]
    public void Updated_FiresOnMutations()
    {
        var m = new TestRunTelemetryModel();
        var fired = 0;
        m.Updated += () => fired++;
        m.StartRun([("a", "A")], 2);
        m.StageStarted("a", 2);
        m.SampleCompleted(TestActivityKind.Success);
        Assert.That(fired, Is.GreaterThanOrEqualTo(3));
    }
}

[TestFixture]
public sealed class TestTimeEstimatorTests
{
    [Test]
    public void BeforeWarmup_ReportsCalculatingWithNoNumber()
    {
        var e = new TestTimeEstimator();
        e.AddSample(TimeSpan.FromSeconds(2));
        e.AddSample(TimeSpan.FromSeconds(2));

        var est = e.GetEstimate(remainingSamples: 10);
        Assert.Multiple(() =>
        {
            Assert.That(est.Confidence, Is.EqualTo(TestEstimateConfidence.Calculating));
            Assert.That(est.Remaining, Is.Null);
            Assert.That(est.AveragePerSample, Is.Not.Null, "avg per sample is still reportable while warming up");
        });
    }

    [Test]
    public void SteadySamples_ProjectRemainingAccurately()
    {
        var e = new TestTimeEstimator();
        for (var i = 0; i < 12; i++) e.AddSample(TimeSpan.FromSeconds(4));

        var est = e.GetEstimate(remainingSamples: 5);
        Assert.Multiple(() =>
        {
            Assert.That(est.Remaining, Is.Not.Null);
            Assert.That(est.Remaining!.Value.TotalSeconds, Is.EqualTo(20).Within(0.5));
            Assert.That(est.Confidence, Is.EqualTo(TestEstimateConfidence.High));
            Assert.That(est.AveragePerSample!.Value.TotalSeconds, Is.EqualTo(4).Within(1e-6));
        });
    }

    [Test]
    public void VolatileSamples_LowerConfidence()
    {
        var e = new TestTimeEstimator();
        double[] secs = [1, 20, 2, 30, 1, 25, 2, 28, 1, 24, 2, 27];
        foreach (var s in secs) e.AddSample(TimeSpan.FromSeconds(s));

        var est = e.GetEstimate(remainingSamples: 5);
        Assert.That(est.Confidence, Is.EqualTo(TestEstimateConfidence.Low));
    }

    [Test]
    public void OneOutlier_DoesNotWhiplashThePublishedEstimate()
    {
        var e = new TestTimeEstimator();
        for (var i = 0; i < 8; i++) e.AddSample(TimeSpan.FromSeconds(5));
        var before = e.GetEstimate(remainingSamples: 20).Remaining!.Value.TotalSeconds;

        e.AddSample(TimeSpan.FromSeconds(60)); // single stall
        var after = e.GetEstimate(remainingSamples: 19).Remaining!.Value.TotalSeconds;

        // Raw projection would jump massively; smoothing must keep the step bounded.
        Assert.That(after, Is.LessThan(before * 2.0),
            $"published estimate jumped from {before:0}s to {after:0}s on a single outlier");
    }

    [Test]
    public void ThroughputShift_PullsEstimateTowardNewRate()
    {
        var e = new TestTimeEstimator();
        for (var i = 0; i < 6; i++) e.AddSample(TimeSpan.FromSeconds(2));
        // Model swap: everything now takes 10s. Feed several and re-poll.
        double last = double.MaxValue;
        for (var i = 0; i < 10; i++)
        {
            e.AddSample(TimeSpan.FromSeconds(10));
            last = e.GetEstimate(remainingSamples: 10).Remaining!.Value.TotalSeconds;
        }
        // 10 remaining at ~10s each = 100s; blended estimate must have moved well above the
        // stale 2s-rate projection (20s).
        Assert.That(last, Is.GreaterThan(60));
    }

    [Test]
    public void NearCompletion_EstimateCollapsesToZero()
    {
        var e = new TestTimeEstimator();
        for (var i = 0; i < 10; i++) e.AddSample(TimeSpan.FromSeconds(3));
        var est = e.GetEstimate(remainingSamples: 0);
        Assert.That(est.Remaining!.Value.TotalSeconds, Is.EqualTo(0).Within(0.01));
    }

    [Test]
    public void RepeatedReads_WithUnchangedInputs_AreIdempotent()
    {
        var e = new TestTimeEstimator();
        for (var i = 0; i < 8; i++) e.AddSample(TimeSpan.FromSeconds(5));
        e.AddSample(TimeSpan.FromSeconds(60)); // create a gap between raw and published

        var first = e.GetEstimate(remainingSamples: 10);
        for (var i = 0; i < 50; i++) e.GetEstimate(remainingSamples: 10); // 60Hz-style polling
        var last = e.GetEstimate(remainingSamples: 10);

        Assert.That(last, Is.EqualTo(first),
            "polling frequency must not advance the smoothing filter when telemetry is unchanged");
    }

    [Test]
    public void Reset_ClearsHistory()
    {
        var e = new TestTimeEstimator();
        for (var i = 0; i < 10; i++) e.AddSample(TimeSpan.FromSeconds(3));
        e.Reset();
        Assert.Multiple(() =>
        {
            Assert.That(e.SampleCount, Is.Zero);
            Assert.That(e.GetEstimate(5).Confidence, Is.EqualTo(TestEstimateConfidence.Calculating));
        });
    }
}

[TestFixture]
public sealed class TestRunPauseGateTests
{
    [Test]
    public async Task NotPaused_ReturnsImmediately()
    {
        var gate = new TestRunPauseGate();
        var task = gate.WaitWhilePausedAsync();
        await task; // must not hang
        Assert.That(task.IsCompletedSuccessfully, Is.True);
    }

    [Test]
    public async Task Paused_BlocksUntilResume()
    {
        var gate = new TestRunPauseGate();
        gate.Pause();
        Assert.That(gate.IsPaused, Is.True);

        var waiter = gate.WaitWhilePausedAsync();
        await Task.Delay(50);
        Assert.That(waiter.IsCompleted, Is.False, "waiter must block while paused");

        gate.Resume();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(gate.IsPaused, Is.False);
    }

    [Test]
    public void CancelWhilePaused_ThrowsOperationCanceled()
    {
        var gate = new TestRunPauseGate();
        gate.Pause();
        using var cts = new CancellationTokenSource();
        var waiter = gate.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
