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
        // BuildB3ReportAsync's scripted runtime answers everything correctly (a perfect
        // pass rate), which would NOT actually exercise the scenario this test is named
        // for. Force a genuinely imperfect Summary.PassedQuestions (Grok review, PR #59) --
        // the CF0-level blocking gates (segment coverage, citation precision, etc.) are
        // computed from the real underlying QuestionResults, not the overridden Summary,
        // so they're unaffected: Passed staying true here is proof that ONLY the
        // now-non-blocking all-questions-verified/question_pass_rate signal was holding it
        // back before, not a side effect of also faking the other gates.
        var perfectB3 = await BuildB3ReportAsync();
        Assert.That(perfectB3.Summary.TotalQuestions, Is.GreaterThan(3),
            "fixture needs enough questions for an imperfect-but-baseline-beating pass count");
        var imperfectSummary = perfectB3.Summary with
        {
            PassedQuestions = perfectB3.Summary.TotalQuestions - 1,
        };
        var b3 = perfectB3 with { Summary = imperfectSummary };

        // Full happy-path inputs (B4 + diagnostics) so the ONLY varying factor from the
        // pre-existing "everything passes" test is the imperfect pass rate -- otherwise a
        // missing B4/quote/stitch would independently sink ReadyForExpansion and the test
        // would prove nothing about the metric this PR actually changed.
        var fixture = DeterministicFabricCorpus.Create();
        var quote = new ContextFabricBenchmarkExpansionRunner(runtime: null)
            .RunQuoteAnchoringDiagnostics(fixture);
        var stitch = await new ContextFabricBenchmarkExpansionRunner(new ScriptedFabricRuntime())
            .RunBoundaryStitchDiagnosticsAsync(DeterministicFabricCorpus.CreateBoundaryStitchFixture());

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, quote, stitch,
        [
            SystemWithScore("B0", "Closed-book native model", passed: 1, total: 5),
            SystemWithScore("B1", "Truncated prompt", passed: 2, total: 5),
            SystemWithScore("B2", "Conventional top-k RAG", passed: 3, total: 5),
            new("B4", "HIVE Context Fabric", FabricBenchmarkSystemStatus.Passed, "Frozen run passed."),
        ]);

        var gradedGate = report.Gates.Single(gate => gate.Name == "Graded capability");
        var questionPassRateMetric = report.Metrics.Single(metric => metric.Name == "question_pass_rate");

        Assert.Multiple(() =>
        {
            Assert.That(questionPassRateMetric.Passed, Is.False,
                "the whole point of this test is a genuinely imperfect pass rate");
            Assert.That(questionPassRateMetric.IsBlocking, Is.False,
                "the old harsh metric is still reported but no longer blocks");
            // Imperfect pass rate (TotalQuestions - 1) still comfortably beats every
            // supplied baseline (max 3/5) and clears citation_precision from the real
            // scripted answers.
            Assert.That(gradedGate.Passed, Is.True, gradedGate.Detail);
            Assert.That(gradedGate.IsBlocking, Is.True);
            // The actual point: overall Passed is true DESPITE the imperfect pass rate.
            Assert.That(report.ReadyForExpansion, Is.True,
                "ReadyForExpansion must not be sunk by a non-blocking metric alone. Gates: " +
                string.Join("; ", report.Gates.Select(g => $"{g.Name}={g.Passed}(blocking={g.IsBlocking}):{g.Detail}")));
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
    public async Task GradedCapabilityGate_TreatsMissingBaseline_AsZero_NotAsBlocking()
    {
        // No frozenSystemRuns supplied at all -- B0/B1/B2 are all "Missing" with null
        // PassedCount. The graded gate must still PASS when B3 has any correct answers,
        // treating an absent baseline as 0 rather than refusing to compare or failing
        // outright -- a genuinely missing baseline is already flagged separately by
        // "B0-B4 frozen runs present" (Grok review, PR #59: the original version of this
        // test only asserted the gate was non-null, proving nothing about the "as zero"
        // claim in its own name).
        var b3 = await BuildB3ReportAsync();

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, null, null);

        var gradedGate = report.Gates.Single(gate => gate.Name == "Graded capability");

        Assert.That(gradedGate.Passed, Is.True, gradedGate.Detail);
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
            Assert.That(aggregate.Name, Is.Not.EqualTo(mean.Name));
            // Perfect scripted run: both should be at or near 1.0 here -- the genuine
            // divergence case is covered by the dedicated test below, since it needs a
            // deliberately lopsided citation distribution the scripted runtime doesn't
            // naturally produce.
            Assert.That(mean.Value, Is.GreaterThanOrEqualTo(0).And.LessThanOrEqualTo(1.0));
            Assert.That(mean.Target, Is.EqualTo(0.90));
            // Non-blocking (Grading Spec §8): reported alongside the aggregate but must not
            // independently gate NO-GO the way citation_precision itself still does.
            Assert.That(mean.IsBlocking, Is.False);
        });
    }

    [Test]
    public async Task MeanCitationPrecision_Diverges_From_Aggregate_When_Citation_Counts_Are_Lopsided()
    {
        // Grok review, PR #59: the previous version of this test only bounds-checked the
        // mean's value and never actually proved it can differ from the aggregate -- the
        // whole point of adding it (Grading Spec §6.2). Construct two questions with
        // deliberately lopsided citation counts: one with a single valid citation (its own
        // precision 1.0), one with zero valid out of 100 attempted (its own precision 0.0).
        // Mean of per-question precision = (1.0 + 0.0) / 2 = 0.5. Aggregate = (1 valid) /
        // (1 + 100 total) ~= 0.0099 -- the citation-heavy failing question dominates the
        // aggregate far more than its 1-in-2 share of the question count would suggest.
        var perfectB3 = await BuildB3ReportAsync();
        var template = perfectB3.QuestionResults[0];
        var goodQuestion = template with
        {
            Verification = new FabricVerificationResult(true, 1.0, ValidCitations: 1, TotalCitations: 1, [], []),
        };
        var badQuestion = template with
        {
            Verification = new FabricVerificationResult(false, 0.0, ValidCitations: 0, TotalCitations: 100, [], []),
        };
        var b3 = perfectB3 with
        {
            QuestionResults = [goodQuestion, badQuestion],
            Summary = perfectB3.Summary with
            {
                ValidCitations = 1,
                TotalCitations = 101,
            },
        };

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, null, null);

        var aggregate = report.Metrics.Single(metric => metric.Name == "citation_precision");
        var mean = report.Metrics.Single(metric => metric.Name == "mean_citation_precision");

        Assert.Multiple(() =>
        {
            Assert.That(mean.Value, Is.EqualTo(0.5).Within(0.0001));
            Assert.That(aggregate.Value, Is.EqualTo(1.0 / 101.0).Within(0.0001));
            Assert.That(mean.Value, Is.Not.EqualTo(aggregate.Value).Within(0.01),
                "the whole point: these must genuinely differ, not just be two names for the same number");
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
