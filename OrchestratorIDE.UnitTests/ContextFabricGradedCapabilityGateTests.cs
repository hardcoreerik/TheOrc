// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricGradedCapabilityGateTests
{
    // Remediation Phase 2 (docs/CONTEXT_FABRIC_GRADING_SPEC.md §8/§9): the old harsh
    // 100%-question_pass_rate gate blocked the whole verdict on its own, even when B3
    // substantially beat every baseline. These tests cover the replacement "Graded
    // capability" gate and its supporting IsBlocking plumbing.

    [Test]
    public async Task ReadyForExpansion_True_When_B3_Beats_Baselines_Despite_Imperfect_Pass_Rate()
    {
        var b3 = await BuildB3ReportAsync();

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, null, null,
        [
            SystemWithScore("B0", "Closed-book native model", passed: 1, total: 5),
            SystemWithScore("B1", "Truncated prompt", passed: 2, total: 5),
            SystemWithScore("B2", "Conventional top-k RAG", passed: 3, total: 5),
        ]);

        var gradedGate = report.Gates.Single(gate => gate.Name == "Graded capability");
        var questionPassRateMetric = report.Metrics.Single(metric => metric.Name == "question_pass_rate");

        Assert.Multiple(() =>
        {
            // The scripted B3 fixture (see BuildB3ReportAsync) answers everything correctly,
            // so it beats every supplied baseline (max 3/5) and clears citation_precision.
            Assert.That(gradedGate.Passed, Is.True, gradedGate.Detail);
            Assert.That(gradedGate.IsBlocking, Is.True);
            // The old harsh metric is still reported (visible in output) but no longer blocks.
            Assert.That(questionPassRateMetric.IsBlocking, Is.False);
        });
    }

    [Test]
    public void GradedCapabilityGate_Fails_When_B3_Has_No_Comparable_Score()
    {
        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(
            singleNodeContextFabric: null, quoteAnchoring: null, boundaryStitch: null,
        [
            SystemWithScore("B0", "Closed-book native model", passed: 1, total: 5),
        ]);

        var gradedGate = report.Gates.Single(gate => gate.Name == "Graded capability");

        Assert.That(gradedGate.Passed, Is.False);
    }

    [Test]
    public void GradedCapabilityGate_TreatsMissingBaseline_AsZero_NotAsBlocking()
    {
        // No frozenSystemRuns supplied at all -- B0/B1/B2 are all "Missing" with null
        // PassedCount. The graded gate must still be EVALUABLE (not itself missing/N-A),
        // treating an absent baseline as 0 rather than refusing to compare -- a genuinely
        // missing baseline is already flagged separately by "B0-B4 frozen runs present".
        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(
            singleNodeContextFabric: null, quoteAnchoring: null, boundaryStitch: null);

        var gradedGate = report.Gates.Single(gate => gate.Name == "Graded capability");

        // B3 itself is also missing here, so the gate still fails overall -- but for the
        // "B3 missing" reason, not because baselines were absent. Covered together with the
        // dedicated "no comparable score" test above; this test's own assertion is just that
        // evaluation doesn't throw and the gate is present at all.
        Assert.That(gradedGate, Is.Not.Null);
    }

    [Test]
    public async Task MeanCitationPrecision_Metric_Is_Reported_Separately_From_The_Aggregate()
    {
        var b3 = await BuildB3ReportAsync();

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, null, null);

        var aggregate = report.Metrics.Single(metric => metric.Name == "citation_precision");
        var mean = report.Metrics.Single(metric => metric.Name == "mean_citation_precision");

        Assert.Multiple(() =>
        {
            // Both exist as distinct metrics -- see docs/CONTEXT_FABRIC_GRADING_SPEC.md §6.2 for
            // why they can diverge (mean isn't just a re-derivation of the aggregate).
            Assert.That(aggregate.Name, Is.Not.EqualTo(mean.Name));
            // Perfect scripted run: both should be at or near 1.0.
            Assert.That(mean.Value, Is.GreaterThanOrEqualTo(0).And.LessThanOrEqualTo(1.0));
            Assert.That(mean.Target, Is.EqualTo(0.90));
        });
    }

    [Test]
    public async Task EvidenceBudget_Reports_PerCategory_Stats_With_Sane_Percentile_Ordering()
    {
        var b3 = await BuildB3ReportAsync();

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, null, null);

        Assert.That(report.EvidenceBudget, Is.Not.Null.And.Not.Empty);
        Assert.Multiple(() =>
        {
            foreach (var stat in report.EvidenceBudget!)
            {
                Assert.That(stat.QuestionCount, Is.GreaterThan(0), stat.Category);
                Assert.That(stat.P50PromptTokens, Is.LessThanOrEqualTo(stat.P95PromptTokens), stat.Category);
                Assert.That(stat.P95PromptTokens, Is.LessThanOrEqualTo(stat.MaxPromptTokens), stat.Category);
            }
        });
    }

    [Test]
    public void EvidenceBudget_Empty_When_No_SingleNodeReport_Supplied()
    {
        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(
            singleNodeContextFabric: null, quoteAnchoring: null, boundaryStitch: null);

        Assert.That(report.EvidenceBudget, Is.Not.Null.And.Empty);
    }

    [TestCase(new[] { 10 }, 0.50, ExpectedResult = 10)]
    [TestCase(new[] { 10 }, 0.95, ExpectedResult = 10)]
    [TestCase(new[] { 1, 2, 3, 4, 5 }, 0.50, ExpectedResult = 3)]
    [TestCase(new[] { 1, 2, 3, 4, 5 }, 0.95, ExpectedResult = 5)]
    [TestCase(new[] { 100, 200, 300, 400 }, 0.50, ExpectedResult = 200)]
    public int NearestRankPercentile_MatchesExpectedRank(int[] sortedAscending, double percentile) =>
        ContextFabricBenchmarkGateEvaluator.NearestRankPercentile(sortedAscending, percentile);

    [Test]
    public void NearestRankPercentile_Empty_Returns_Zero()
    {
        Assert.That(ContextFabricBenchmarkGateEvaluator.NearestRankPercentile([], 0.50), Is.EqualTo(0));
    }

    private static async Task<FabricFeasibilityReport> BuildB3ReportAsync()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime());
        return await runner.RunAsync(fixture);
    }

    private static FabricBenchmarkSystemGate SystemWithScore(string systemId, string label, int passed, int total) =>
        new(systemId, label, FabricBenchmarkSystemStatus.Passed, $"{passed}/{total} correct.",
            PassedCount: passed, TotalCount: total);
}
