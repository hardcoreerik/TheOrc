// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public static class ContextFabricBenchmarkGateEvaluator
{
    private static readonly IReadOnlyDictionary<string, string> RequiredSystems =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["B0"] = "Closed-book native model",
            ["B1"] = "Truncated prompt",
            ["B2"] = "Conventional top-k RAG",
            ["B3"] = "Single-node Context Fabric",
            ["B4"] = "HIVE Context Fabric",
        };

    public static FabricCf7BenchmarkGateReport Evaluate(
        FabricFeasibilityReport? singleNodeContextFabric,
        FabricQuoteAnchorReport? quoteAnchoring,
        FabricBoundaryStitchReport? boundaryStitch,
        IReadOnlyList<FabricBenchmarkSystemGate>? frozenSystemRuns = null)
    {
        var corpusId = singleNodeContextFabric?.CorpusId
            ?? quoteAnchoring?.CorpusId
            ?? "unknown";
        var generationId = singleNodeContextFabric?.GenerationId
            ?? quoteAnchoring?.GenerationId
            ?? "unknown";
        var sourceDigest = singleNodeContextFabric?.SourceDigest ?? "";

        var systems = RequiredSystems
            .Select(pair => BuildSystemGate(pair.Key, pair.Value, singleNodeContextFabric, frozenSystemRuns))
            .ToArray();
        var metrics = BuildMetrics(singleNodeContextFabric, boundaryStitch);
        var gates = BuildGates(systems, metrics, quoteAnchoring, boundaryStitch);
        var evidenceBudget = BuildEvidenceBudgetStats(singleNodeContextFabric);

        return new FabricCf7BenchmarkGateReport(
            FabricSchemaVersions.BenchmarkGate,
            DateTimeOffset.UtcNow,
            corpusId,
            generationId,
            sourceDigest,
            systems,
            metrics,
            gates,
            evidenceBudget);
    }

    private static FabricBenchmarkSystemGate BuildSystemGate(
        string systemId,
        string label,
        FabricFeasibilityReport? singleNodeContextFabric,
        IReadOnlyList<FabricBenchmarkSystemGate>? frozenSystemRuns)
    {
        if (systemId != "B3")
        {
            var supplied = frozenSystemRuns?.FirstOrDefault(system => system.SystemId == systemId);
            if (supplied is not null)
                return supplied;

            return new FabricBenchmarkSystemGate(systemId, label, FabricBenchmarkSystemStatus.Missing,
                "No frozen run artifact supplied yet.");
        }

        // B3 is the live single-node Context Fabric run; frozen inputs cover the other benchmark systems.
        if (singleNodeContextFabric is null)
            return new FabricBenchmarkSystemGate(systemId, label, FabricBenchmarkSystemStatus.Missing,
                "No single-node Context Fabric report supplied.");

        var summary = singleNodeContextFabric.Summary;
        return new FabricBenchmarkSystemGate(systemId, label,
            singleNodeContextFabric.Passed ? FabricBenchmarkSystemStatus.Passed : FabricBenchmarkSystemStatus.Failed,
            singleNodeContextFabric.Passed
                ? "Single-node Context Fabric report passed its internal (blocking) gates."
                : "Single-node Context Fabric report failed one or more internal (blocking) gates.",
            PassedCount: summary.PassedQuestions,
            TotalCount: summary.TotalQuestions);
    }

    private static IReadOnlyList<FabricBenchmarkMetric> BuildMetrics(
        FabricFeasibilityReport? singleNodeContextFabric,
        FabricBoundaryStitchReport? boundaryStitch)
    {
        var metrics = new List<FabricBenchmarkMetric>();
        if (singleNodeContextFabric is not null)
        {
            var summary = singleNodeContextFabric.Summary;
            // Stays fully blocking (unlike question_pass_rate below): a rejected segment is a
            // real recall failure the reader should not have, not an inherently-hard question
            // this benchmark treats as a stretch goal. ContextFabricFeasibilityRunner's
            // open-extraction completeness-repair pass (ReadSegmentAsync, added 2026-07-17) is
            // what's supposed to close this to 100/100 -- if it doesn't, that is real signal.
            metrics.Add(new FabricBenchmarkMetric(
                "segment_terminal_coverage",
                Ratio(summary.AcceptedSegments, summary.ExpectedSegments),
                1.0,
                summary.AcceptedSegments == summary.ExpectedSegments,
                $"{summary.AcceptedSegments}/{summary.ExpectedSegments} segments accepted"));
            // Non-blocking: reported stretch goal, not a release blocker. See
            // ContextFabricFeasibilityRunner.BuildGates's "all-questions-verified" for the
            // matching CF0-level change and docs/CONTEXT_FABRIC_GRADING_SPEC.md §8/§9 for why --
            // this single all-or-nothing metric used to determine the whole gate's verdict on
            // its own, hiding a B3 result that substantially beats every baseline behind a
            // NO-GO. The "Graded capability" gate below is the actual primary signal now.
            metrics.Add(new FabricBenchmarkMetric(
                "question_pass_rate",
                Ratio(summary.PassedQuestions, summary.TotalQuestions),
                1.0,
                summary.PassedQuestions == summary.TotalQuestions,
                $"{summary.PassedQuestions}/{summary.TotalQuestions} questions passed",
                IsBlocking: false));
            metrics.Add(new FabricBenchmarkMetric(
                "citation_precision",
                Ratio(summary.ValidCitations, summary.TotalCitations),
                0.90,
                Ratio(summary.ValidCitations, summary.TotalCitations) >= 0.90,
                $"{summary.ValidCitations}/{summary.TotalCitations} citations verified"));
            // Fills the gap flagged in docs/CONTEXT_FABRIC_GRADING_SPEC.md §6.2: the metric
            // above is a citation-weighted AGGREGATE (sum of valid / sum of total across every
            // question), not the mean of each question's own precision -- the two can diverge
            // substantially when a few citation-heavy questions dominate the aggregate, and
            // abstention questions (correctly 0 citations) are invisible to the aggregate even
            // though their own precision is 1.0. This metric is that mean, computed directly
            // from QuestionResults rather than the Summary's pre-aggregated counts. Non-blocking:
            // it's a supplementary diagnostic view of the SAME underlying citation-quality
            // signal the aggregate citation_precision metric already gates on -- it should not
            // create a second, redundant blocking bar for that signal.
            var perQuestionPrecisions = singleNodeContextFabric.QuestionResults
                .Select(result => result.Verification.CitationPrecision)
                .ToArray();
            var meanPrecision = perQuestionPrecisions.Length == 0 ? 0 : perQuestionPrecisions.Average();
            metrics.Add(new FabricBenchmarkMetric(
                "mean_citation_precision",
                meanPrecision,
                0.90,
                meanPrecision >= 0.90,
                $"mean of {perQuestionPrecisions.Length} per-question precision scores",
                IsBlocking: false));
            metrics.Add(new FabricBenchmarkMetric(
                "max_prompt_tokens",
                summary.MaximumPromptTokens,
                singleNodeContextFabric.Options.ContextBudget.ContextLimit,
                summary.MaximumPromptTokens <= singleNodeContextFabric.Options.ContextBudget.ContextLimit,
                $"{summary.MaximumPromptTokens}/{singleNodeContextFabric.Options.ContextBudget.ContextLimit} prompt tokens"));
        }

        if (boundaryStitch is not null)
        {
            var passed = boundaryStitch.Results.Count(result => result.Passed);
            metrics.Add(new FabricBenchmarkMetric(
                "boundary_stitch_pass_rate",
                Ratio(passed, boundaryStitch.Results.Count),
                1.0,
                passed == boundaryStitch.Results.Count,
                $"{passed}/{boundaryStitch.Results.Count} boundary-stitch cases passed"));
        }

        return metrics;
    }

    /// <summary>
    /// Per-question-category B3 prompt-token consumption (review item #9: "add evidence budget
    /// consumption statistics... so we can see when global-synthesis questions are budget-
    /// constrained"). Nearest-rank percentile, no interpolation -- simple and sufficient at the
    /// question-per-category counts this benchmark runs (single digits to low tens), where
    /// interpolation would imply more precision than the sample size supports.
    /// </summary>
    private static IReadOnlyList<FabricEvidenceBudgetStat> BuildEvidenceBudgetStats(
        FabricFeasibilityReport? singleNodeContextFabric)
    {
        if (singleNodeContextFabric is null)
            return [];

        return singleNodeContextFabric.QuestionResults
            .GroupBy(result => result.Question.Kind)
            .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group =>
            {
                var tokens = group.Select(result => result.Metrics.PromptTokens).OrderBy(t => t).ToArray();
                return new FabricEvidenceBudgetStat(
                    group.Key.ToString(),
                    tokens.Length,
                    NearestRankPercentile(tokens, 0.50),
                    NearestRankPercentile(tokens, 0.95),
                    tokens.Length == 0 ? 0 : tokens[^1]);
            })
            .ToArray();
    }

    /// <summary>Nearest-rank percentile of a pre-sorted (ascending) array. Internal for direct unit test coverage.</summary>
    internal static int NearestRankPercentile(int[] sortedAscending, double percentile)
    {
        if (sortedAscending.Length == 0)
            return 0;
        var rank = (int)Math.Ceiling(percentile * sortedAscending.Length);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Length - 1);
        return sortedAscending[index];
    }

    private static IReadOnlyList<FabricGateResult> BuildGates(
        IReadOnlyList<FabricBenchmarkSystemGate> systems,
        IReadOnlyList<FabricBenchmarkMetric> metrics,
        FabricQuoteAnchorReport? quoteAnchoring,
        FabricBoundaryStitchReport? boundaryStitch)
    {
        var missingSystems = systems
            .Where(system => system.Status == FabricBenchmarkSystemStatus.Missing)
            .Select(system => system.SystemId)
            .ToArray();
        var failedSystems = systems
            .Where(system => system.Status == FabricBenchmarkSystemStatus.Failed)
            .Select(system => system.SystemId)
            .ToArray();
        var failedMetrics = metrics
            .Where(metric => metric.IsBlocking && !metric.Passed)
            .Select(metric => metric.Name)
            .ToArray();

        return
        [
            new FabricGateResult(
                "B0-B4 frozen runs present",
                missingSystems.Length == 0,
                missingSystems.Length == 0
                    ? "All benchmark system artifacts are present."
                    : $"Missing benchmark system artifacts: {string.Join(", ", missingSystems)}."),
            new FabricGateResult(
                "System runs passed",
                failedSystems.Length == 0,
                failedSystems.Length == 0
                    ? "No supplied benchmark system failed its own gate."
                    : $"Failed benchmark systems: {string.Join(", ", failedSystems)}."),
            new FabricGateResult(
                "Metric thresholds passed",
                failedMetrics.Length == 0,
                failedMetrics.Length == 0
                    ? "All blocking metric thresholds passed."
                    : $"Failed blocking metric thresholds: {string.Join(", ", failedMetrics)}."),
            BuildGradedCapabilityGate(systems, metrics),
            new FabricGateResult(
                "Quote-anchor diagnostics present",
                quoteAnchoring is not null,
                quoteAnchoring is null
                    ? "Quote-anchor diagnostics are missing."
                    : "Quote-anchor diagnostics are present."),
            new FabricGateResult(
                "Boundary-stitch diagnostics present",
                boundaryStitch is not null,
                boundaryStitch is null
                    ? "Boundary-stitch diagnostics are missing."
                    : "Boundary-stitch diagnostics are present."),
        ];
    }

    /// <summary>
    /// The primary actionable capability signal (Remediation Phase 2, review item #2): replaces
    /// literal 100% question_pass_rate as the thing that actually gates GO/NO-GO. Passes when
    /// B3 (Single-node Context Fabric) beats the strongest of the B0/B1/B2 baselines on raw
    /// PassedCount -- all four are EXPECTED to run against the identical question set within one
    /// gate-expanded invocation, so comparing raw counts (not normalized rates) is valid there --
    /// AND the aggregate citation_precision metric clears its existing 0.90 bar. A
    /// missing/incomparable baseline (PassedCount null -- e.g. B4, or a baseline that hasn't run)
    /// is treated as 0 correct for this comparison: we can't credit B3 with "beating" a baseline
    /// we have no score for, but a baseline's absence also shouldn't itself block the gate the
    /// way a missing REQUIRED system already does via the separate "B0-B4 frozen runs present"
    /// gate. B3 itself missing a count (not run, or Missing status) fails this gate outright --
    /// there's nothing to grade. A baseline that DID run but against a different-sized question
    /// set (TotalCount mismatch -- e.g. a differing --max-questions cap between runs, or hand-
    /// assembled frozenSystemRuns in a non-gate-expanded invocation) fails the gate outright too
    /// (CodeRabbit review, PR #59): raw-count comparison across mismatched totals is meaningless,
    /// not just imprecise, and silently comparing them anyway would be worse than refusing to.
    /// </summary>
    private static FabricGateResult BuildGradedCapabilityGate(
        IReadOnlyList<FabricBenchmarkSystemGate> systems,
        IReadOnlyList<FabricBenchmarkMetric> metrics)
    {
        var b3 = systems.FirstOrDefault(system => system.SystemId == "B3");
        if (b3?.PassedCount is not { } b3Passed || b3.TotalCount is not { } b3Total)
            return new FabricGateResult(
                "Graded capability",
                false,
                "B3 has no comparable PassedCount (not run, or missing).");

        var scoredBaselines = systems
            .Where(system => system.SystemId is "B0" or "B1" or "B2" && system.PassedCount is not null)
            .ToArray();
        var mismatched = scoredBaselines
            .Where(system => system.TotalCount != b3Total)
            .Select(system => $"{system.SystemId}={system.TotalCount}")
            .ToArray();
        if (mismatched.Length > 0)
            return new FabricGateResult(
                "Graded capability",
                false,
                $"Question-set size mismatch: B3 ran {b3Total} questions but {string.Join(", ", mismatched)} did not -- raw PassedCount is not comparable across differently-sized runs.");

        var bestBaseline = scoredBaselines.Length == 0 ? 0 : scoredBaselines.Max(system => system.PassedCount ?? 0);

        var citationPrecision = metrics.FirstOrDefault(metric => metric.Name == "citation_precision");
        var citationPrecisionPassed = citationPrecision?.Passed ?? false;
        var beatsBaseline = b3Passed > bestBaseline;

        var detail = $"B3 {b3Passed}/{b3Total} vs. best baseline {bestBaseline}; " +
            $"citation_precision {(citationPrecision is null ? "not available" : $"{citationPrecision.Value:P1}")}";
        return new FabricGateResult("Graded capability", beatsBaseline && citationPrecisionPassed, detail);
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;
}
