// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core.Runtime;

/// <summary>Lifecycle of one stage in a test run's pipeline (see TestRunTelemetryModel).</summary>
public enum TestStageStatus { Queued, Active, Completed, Warning, Failed, Skipped, Cancelled }

/// <summary>Overall run lifecycle for the visual test runner.</summary>
public enum TestRunPhase { Idle, Running, Paused, Cancelling, Completed, Failed, Cancelled }

/// <summary>Severity of one activity-feed entry.</summary>
public enum TestActivityKind { Info, Success, Warning, Failure }

/// <summary>
/// One stage in the run pipeline (e.g. "Initialize", one per benched model, "Save report").
/// Mutable on purpose: the telemetry model owns instances and updates them in place so the
/// timeline control can re-render from the same list without reallocating per tick.
/// </summary>
public sealed class TestRunStage(string id, string label)
{
    public string Id    { get; } = id;
    public string Label { get; } = label;
    public TestStageStatus Status { get; internal set; } = TestStageStatus.Queued;
    public DateTimeOffset? StartedUtc { get; internal set; }
    public DateTimeOffset? EndedUtc   { get; internal set; }
    /// <summary>Short user-facing detail ("12/24 cases", "3 failed"). Never model reasoning.</summary>
    public string Detail { get; internal set; } = "";
    /// <summary>Samples completed / total within this stage. Total 0 = stage has no sample count.</summary>
    public int SamplesCompleted { get; internal set; }
    public int SamplesTotal     { get; internal set; }
}

public sealed record TestActivityEntry(DateTimeOffset TimestampUtc, TestActivityKind Kind, string Message);

/// <summary>
/// Layer-2 of the visual test runner (see docs in ChatModelBenchWindow): converts raw execution
/// callbacks (stage started, sample completed, run finished) into one normalized, UI-consumable
/// state object. Deliberately free of any Avalonia dependency so it lives in Core, is shared via
/// the same NativeRuntime Compile-Include route as ModelBenchRunner, and is unit-testable without
/// a UI. NOT thread-safe: callers must marshal onto one thread (the UI thread in practice, via
/// Dispatcher.UIThread.Post — the same discipline ChatModelBenchWindow already used for its
/// status line). The activity feed is a capped ring so an arbitrarily long run cannot grow
/// memory without bound.
/// </summary>
public sealed class TestRunTelemetryModel
{
    public const int ActivityFeedCap = 200;

    private readonly List<TestRunStage> _stages = [];
    private readonly List<TestActivityEntry> _activity = [];
    private readonly Func<DateTimeOffset> _clock;

    public TestRunTelemetryModel(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (static () => DateTimeOffset.UtcNow);

    /// <summary>Raised after any state mutation so views can refresh. Same-thread, synchronous.</summary>
    public event Action? Updated;

    public TestRunPhase Phase { get; private set; } = TestRunPhase.Idle;
    public IReadOnlyList<TestRunStage> Stages => _stages;
    public IReadOnlyList<TestActivityEntry> Activity => _activity;

    /// <summary>Stage currently Active, or null (e.g. between stages / not running).</summary>
    public TestRunStage? ActiveStage { get; private set; }

    /// <summary>Short description of what the system is doing right now (observable activity only).</summary>
    public string CurrentOperation { get; private set; } = "";

    public int TotalSamples     { get; private set; }
    public int CompletedSamples { get; private set; }
    public int PassedSamples    { get; private set; }
    public int FailedSamples    { get; private set; }
    public int WarningSamples   { get; private set; }
    public int ErrorSamples     { get; private set; }
    public int RetriedSamples   { get; private set; }

    public DateTimeOffset? RunStartedUtc { get; private set; }
    public DateTimeOffset? RunEndedUtc   { get; private set; }

    /// <summary>Total wall time of the run so far (frozen once the run ends).</summary>
    public TimeSpan Elapsed => RunStartedUtc is null
        ? TimeSpan.Zero
        : (RunEndedUtc ?? _clock()) - RunStartedUtc.Value;

    public double FractionComplete => TotalSamples <= 0 ? 0 : (double)CompletedSamples / TotalSamples;

    public bool IsRunActive => Phase is TestRunPhase.Running or TestRunPhase.Paused or TestRunPhase.Cancelling;

    // ── Run lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Resets all state and begins a new run with the given pipeline.</summary>
    public void StartRun(IEnumerable<(string Id, string Label)> stages, int totalSamples)
    {
        _stages.Clear();
        foreach (var (id, label) in stages) _stages.Add(new TestRunStage(id, label));
        _activity.Clear();
        ActiveStage      = null;
        CurrentOperation = "Starting…";
        TotalSamples     = totalSamples;
        CompletedSamples = PassedSamples = FailedSamples = WarningSamples = ErrorSamples = RetriedSamples = 0;
        RunStartedUtc    = _clock();
        RunEndedUtc      = null;
        Phase            = TestRunPhase.Running;
        RaiseUpdated();
    }

    public void PauseRun()
    {
        if (Phase != TestRunPhase.Running) return;
        Phase = TestRunPhase.Paused;
        AddActivity(TestActivityKind.Info, "Run paused.");
    }

    public void ResumeRun()
    {
        if (Phase != TestRunPhase.Paused) return;
        Phase = TestRunPhase.Running;
        AddActivity(TestActivityKind.Info, "Run resumed.");
    }

    public void BeginCancel()
    {
        if (!IsRunActive) return;
        Phase = TestRunPhase.Cancelling;
        AddActivity(TestActivityKind.Warning, "Cancelling run…");
    }

    /// <summary>
    /// Ends the run. Completed/Failed/Cancelled per <paramref name="outcome"/>; stages still
    /// Queued or Active are marked Cancelled (never silently "Completed") so the timeline is
    /// honest about what actually ran.
    /// </summary>
    public void EndRun(TestRunPhase outcome, string summary)
    {
        if (outcome is not (TestRunPhase.Completed or TestRunPhase.Failed or TestRunPhase.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "EndRun accepts a terminal phase only.");

        foreach (var s in _stages)
        {
            if (s.Status is TestStageStatus.Queued or TestStageStatus.Active)
            {
                s.Status   = outcome == TestRunPhase.Failed && s.Status == TestStageStatus.Active
                    ? TestStageStatus.Failed
                    : TestStageStatus.Cancelled;
                s.EndedUtc = _clock();
            }
        }

        ActiveStage      = null;
        CurrentOperation = summary;
        RunEndedUtc      = _clock();
        Phase            = outcome;
        AddActivity(
            outcome switch
            {
                TestRunPhase.Completed => TestActivityKind.Success,
                TestRunPhase.Failed    => TestActivityKind.Failure,
                _                      => TestActivityKind.Warning,
            },
            summary);
    }

    // ── Stages ────────────────────────────────────────────────────────────────

    public void StageStarted(string stageId, int samplesInStage = 0, string? operation = null)
    {
        var stage = FindStage(stageId);
        if (stage is null) return;
        stage.Status       = TestStageStatus.Active;
        stage.StartedUtc   = _clock();
        stage.SamplesTotal = samplesInStage;
        ActiveStage        = stage;
        CurrentOperation   = operation ?? stage.Label;
        AddActivity(TestActivityKind.Info, $"{stage.Label} started.");
    }

    public void StageEnded(string stageId, TestStageStatus status, string detail = "")
    {
        var stage = FindStage(stageId);
        if (stage is null) return;
        stage.Status   = status;
        stage.EndedUtc = _clock();
        stage.Detail   = detail;
        if (ReferenceEquals(ActiveStage, stage)) ActiveStage = null;
        AddActivity(
            status switch
            {
                TestStageStatus.Completed => TestActivityKind.Success,
                TestStageStatus.Warning   => TestActivityKind.Warning,
                TestStageStatus.Failed    => TestActivityKind.Failure,
                _                         => TestActivityKind.Info,
            },
            detail.Length > 0 ? $"{stage.Label}: {detail}" : $"{stage.Label} {StatusWord(status)}.");
    }

    private static string StatusWord(TestStageStatus s) => s switch
    {
        TestStageStatus.Completed => "completed",
        TestStageStatus.Warning   => "completed with warnings",
        TestStageStatus.Failed    => "failed",
        TestStageStatus.Skipped   => "skipped",
        TestStageStatus.Cancelled => "cancelled",
        _                         => s.ToString().ToLowerInvariant(),
    };

    private TestRunStage? FindStage(string id)
    {
        foreach (var s in _stages)
            if (string.Equals(s.Id, id, StringComparison.Ordinal)) return s;
        return null;
    }

    // ── Samples ───────────────────────────────────────────────────────────────

    /// <summary>Updates the "what is being processed right now" line without logging feed noise.</summary>
    public void SampleStarted(string operation)
    {
        CurrentOperation = operation;
        RaiseUpdated();
    }

    /// <summary>
    /// Records one finished sample. kind: Success=pass, Warning=refused/soft-fail,
    /// Failure=fail, and errored=true for infrastructure errors (timeouts, exceptions).
    /// </summary>
    public void SampleCompleted(TestActivityKind kind, bool errored = false, string? feedMessage = null)
    {
        CompletedSamples++;
        if (errored) ErrorSamples++;
        switch (kind)
        {
            case TestActivityKind.Success: PassedSamples++;  break;
            case TestActivityKind.Warning: WarningSamples++; break;
            case TestActivityKind.Failure: FailedSamples++;  break;
        }

        if (ActiveStage is { } stage)
        {
            stage.SamplesCompleted++;
            stage.Detail = stage.SamplesTotal > 0
                ? $"{stage.SamplesCompleted}/{stage.SamplesTotal}"
                : $"{stage.SamplesCompleted}";
        }

        if (feedMessage is not null) AddActivity(kind, feedMessage);
        else RaiseUpdated();
    }

    public void SampleRetried(string message)
    {
        RetriedSamples++;
        AddActivity(TestActivityKind.Warning, message);
    }

    // ── Activity feed ─────────────────────────────────────────────────────────

    public void AddActivity(TestActivityKind kind, string message)
    {
        _activity.Add(new TestActivityEntry(_clock(), kind, message));
        if (_activity.Count > ActivityFeedCap)
            _activity.RemoveRange(0, _activity.Count - ActivityFeedCap);
        RaiseUpdated();
    }

    private void RaiseUpdated() => Updated?.Invoke();
}
