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
        FabricBoundaryStitchReport? boundaryStitch)
    {
        var corpusId = singleNodeContextFabric?.CorpusId
            ?? quoteAnchoring?.CorpusId
            ?? "unknown";
        var generationId = singleNodeContextFabric?.GenerationId
            ?? quoteAnchoring?.GenerationId
            ?? "unknown";
        var sourceDigest = singleNodeContextFabric?.SourceDigest ?? "";

        var systems = RequiredSystems
            .Select(pair => BuildSystemGate(pair.Key, pair.Value, singleNodeContextFabric))
            .ToArray();
        var metrics = BuildMetrics(singleNodeContextFabric, quoteAnchoring, boundaryStitch);
        var gates = BuildGates(systems, metrics, quoteAnchoring, boundaryStitch);

        return new FabricCf7BenchmarkGateReport(
            FabricSchemaVersions.BenchmarkGate,
            DateTimeOffset.UtcNow,
            corpusId,
            generationId,
            sourceDigest,
            systems,
            metrics,
            gates);
    }

    private static FabricBenchmarkSystemGate BuildSystemGate(
        string systemId,
        string label,
        FabricFeasibilityReport? singleNodeContextFabric)
    {
        if (systemId != "B3")
            return new FabricBenchmarkSystemGate(systemId, label, FabricBenchmarkSystemStatus.Missing,
                "No frozen run artifact supplied yet.");

        if (singleNodeContextFabric is null)
            return new FabricBenchmarkSystemGate(systemId, label, FabricBenchmarkSystemStatus.Missing,
                "No single-node Context Fabric report supplied.");

        return new FabricBenchmarkSystemGate(systemId, label,
            singleNodeContextFabric.Passed ? FabricBenchmarkSystemStatus.Passed : FabricBenchmarkSystemStatus.Failed,
            singleNodeContextFabric.Passed
                ? "Single-node Context Fabric report passed its internal gates."
                : "Single-node Context Fabric report failed one or more internal gates.");
    }

    private static IReadOnlyList<FabricBenchmarkMetric> BuildMetrics(
        FabricFeasibilityReport? singleNodeContextFabric,
        FabricQuoteAnchorReport? quoteAnchoring,
        FabricBoundaryStitchReport? boundaryStitch)
    {
        var metrics = new List<FabricBenchmarkMetric>();
        if (singleNodeContextFabric is not null)
        {
            var summary = singleNodeContextFabric.Summary;
            metrics.Add(new FabricBenchmarkMetric(
                "segment_terminal_coverage",
                Ratio(summary.AcceptedSegments, summary.ExpectedSegments),
                1.0,
                summary.AcceptedSegments == summary.ExpectedSegments,
                $"{summary.AcceptedSegments}/{summary.ExpectedSegments} segments accepted"));
            metrics.Add(new FabricBenchmarkMetric(
                "question_pass_rate",
                Ratio(summary.PassedQuestions, summary.TotalQuestions),
                1.0,
                summary.PassedQuestions == summary.TotalQuestions,
                $"{summary.PassedQuestions}/{summary.TotalQuestions} questions passed"));
            metrics.Add(new FabricBenchmarkMetric(
                "citation_precision",
                Ratio(summary.ValidCitations, summary.TotalCitations),
                0.90,
                Ratio(summary.ValidCitations, summary.TotalCitations) >= 0.90,
                $"{summary.ValidCitations}/{summary.TotalCitations} citations verified"));
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
            .Where(metric => !metric.Passed)
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
                    ? "All supplied metric thresholds passed."
                    : $"Failed metric thresholds: {string.Join(", ", failedMetrics)}."),
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

    private static double Ratio(int numerator, int denominator) =>
        denominator <= 0 ? 0 : numerator / (double)denominator;
}
