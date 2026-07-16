// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core.Runtime;

/// <summary>How trustworthy the current remaining-time figure is.</summary>
public enum TestEstimateConfidence
{
    /// <summary>Not enough completed samples yet — show "Calculating…", no number.</summary>
    Calculating,
    /// <summary>A number exists but per-sample durations are still volatile.</summary>
    Low,
    Medium,
    High,
}

/// <summary>One point-in-time remaining-time estimate. Remaining is null while Calculating.</summary>
public sealed record TestTimeEstimate(
    TimeSpan? Remaining,
    TestEstimateConfidence Confidence,
    TimeSpan? AveragePerSample,
    TimeSpan? RecentPerSample,
    /// <summary>User-facing source of the estimate, e.g. "sample throughput".</summary>
    string Basis);

/// <summary>
/// Layer-3 of the visual test runner: remaining-time estimation from per-sample durations.
/// Design goals (all unit-tested):
///   * cautious start — no number until <see cref="WarmupSamples"/> samples completed;
///   * blends overall mean with an exponentially weighted recent mean, so a throughput change
///     (e.g. a slower model taking over mid-bench) pulls the estimate without whiplash;
///   * output smoothing — the published Remaining moves fractionally toward the raw
///     recomputation each update instead of jumping, except when the raw value collapses
///     (run nearly done) where accuracy beats smoothness;
///   * confidence from sample count + coefficient of variation, so the UI can say how stable
///     the number is instead of displaying invented precision.
/// Pure arithmetic over durations the caller supplies — no clocks, no threads — so it can
/// never block or skew the test it measures.
/// </summary>
public sealed class TestTimeEstimator
{
    public const int    WarmupSamples = 3;
    private const double RecentAlpha   = 0.30;  // EWMA weight of the newest sample
    private const double RecentBlend   = 0.60;  // recent-vs-overall mix in the projection
    private const double SmoothFactor  = 0.35;  // fraction of the gap the published value closes per update

    private int      _count;
    private double   _sumSec;
    private double   _sumSqSec;
    private double   _ewmaSec;
    private double?  _publishedRemainingSec;

    /// <summary>Number of completed samples fed in so far.</summary>
    public int SampleCount => _count;

    /// <summary>Feed one completed sample's wall duration.</summary>
    public void AddSample(TimeSpan duration)
    {
        var sec = Math.Max(0, duration.TotalSeconds);
        _count++;
        _sumSec   += sec;
        _sumSqSec += sec * sec;
        _ewmaSec   = _count == 1 ? sec : RecentAlpha * sec + (1 - RecentAlpha) * _ewmaSec;
    }

    /// <summary>Clears all history (new run).</summary>
    public void Reset()
    {
        _count = 0;
        _sumSec = _sumSqSec = _ewmaSec = 0;
        _publishedRemainingSec = null;
    }

    /// <summary>
    /// Current estimate given how many samples remain. Call after each AddSample (or on a UI
    /// tick); each call advances the smoothing filter one step.
    /// </summary>
    public TestTimeEstimate GetEstimate(int remainingSamples)
    {
        if (_count < WarmupSamples || remainingSamples < 0)
            return new TestTimeEstimate(null, TestEstimateConfidence.Calculating, MeanOrNull(), RecentOrNull(),
                "sample throughput (warming up)");

        var mean   = _sumSec / _count;
        var perSec = RecentBlend * _ewmaSec + (1 - RecentBlend) * mean;
        var rawSec = perSec * remainingSamples;

        // Publish-side smoothing. Jump straight to raw when raw is lower than published and
        // small (finishing), otherwise close a fraction of the gap so the display can't
        // seesaw between drastically different values on one odd sample.
        if (_publishedRemainingSec is null || rawSec <= 1.0)
            _publishedRemainingSec = rawSec;
        else
            _publishedRemainingSec += (rawSec - _publishedRemainingSec.Value) * SmoothFactor;

        return new TestTimeEstimate(
            TimeSpan.FromSeconds(Math.Max(0, _publishedRemainingSec.Value)),
            ComputeConfidence(mean),
            MeanOrNull(),
            RecentOrNull(),
            "sample throughput");
    }

    private TestEstimateConfidence ComputeConfidence(double mean)
    {
        if (mean <= 0) return TestEstimateConfidence.Low;
        // Coefficient of variation from the running sums (population variance).
        var variance = Math.Max(0, _sumSqSec / _count - mean * mean);
        var cv = Math.Sqrt(variance) / mean;
        return (_count, cv) switch
        {
            ( < 6, _)      => TestEstimateConfidence.Low,
            (_, > 0.75)    => TestEstimateConfidence.Low,
            ( < 12, _)     => TestEstimateConfidence.Medium,
            (_, > 0.35)    => TestEstimateConfidence.Medium,
            _              => TestEstimateConfidence.High,
        };
    }

    private TimeSpan? MeanOrNull()   => _count == 0 ? null : TimeSpan.FromSeconds(_sumSec / _count);
    private TimeSpan? RecentOrNull() => _count == 0 ? null : TimeSpan.FromSeconds(_ewmaSec);
}
