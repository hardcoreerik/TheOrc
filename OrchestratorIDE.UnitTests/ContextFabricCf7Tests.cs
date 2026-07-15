// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricCf7Tests
{
    [Test]
    public async Task BenchmarkGate_FailsClosed_WhenRequiredSystemRunsAreMissing()
    {
        var (b3, quote, stitch) = await BuildBaselineReportsAsync();

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, quote, stitch);

        Assert.Multiple(() =>
        {
            Assert.That(report.SchemaVersion, Is.EqualTo(FabricSchemaVersions.BenchmarkGate));
            Assert.That(report.ReadyForExpansion, Is.False);
            Assert.That(report.Systems.Single(system => system.SystemId == "B3").Status,
                Is.EqualTo(FabricBenchmarkSystemStatus.Passed));
            Assert.That(report.Systems.Where(system => system.SystemId != "B3"),
                Has.All.Matches<FabricBenchmarkSystemGate>(
                    system => system.Status == FabricBenchmarkSystemStatus.Missing));
            Assert.That(report.Gates.Single(gate => gate.Name == "B0-B4 frozen runs present").Passed,
                Is.False);
            Assert.That(report.Gates.Single(gate => gate.Name == "Metric thresholds passed").Passed,
                Is.True);
        });
    }

    [Test]
    public async Task BenchmarkGate_Passes_WhenRequiredSystemRunsAndDiagnosticsPass()
    {
        var (b3, quote, stitch) = await BuildBaselineReportsAsync();

        // Real, below-B3 baseline scores (not the trivial "B3 > 0" comparison a scoreless
        // baseline would produce -- Grok review, PR #59) so "Graded capability" here is an
        // actual beats-the-baseline comparison, matching what a real gate-expanded run
        // supplies. b3 is the scripted runtime's perfect run (5/5 on this fixture); B0-B2
        // are deliberately below that.
        Assert.That(b3.Summary.TotalQuestions, Is.EqualTo(5),
            "baseline scores below assume this fixture's known question count");
        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, quote, stitch,
        [
            PassedSystem("B0", "Closed-book native model", passed: 1, total: 5),
            PassedSystem("B1", "Truncated prompt", passed: 2, total: 5),
            PassedSystem("B2", "Conventional top-k RAG", passed: 3, total: 5),
            // B4 deliberately has no comparable score -- see Grading Spec §2.1.
            new("B4", "HIVE Context Fabric", FabricBenchmarkSystemStatus.Passed, "Frozen run passed."),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(report.SchemaVersion, Is.EqualTo(FabricSchemaVersions.BenchmarkGate));
            Assert.That(report.ReadyForExpansion, Is.True);
            Assert.That(report.Systems, Has.All.Matches<FabricBenchmarkSystemGate>(
                system => system.Status == FabricBenchmarkSystemStatus.Passed));
            Assert.That(report.Gates, Has.All.Matches<FabricGateResult>(gate => gate.Passed));
            Assert.That(report.Gates.Single(gate => gate.Name == "Graded capability").Detail,
                Does.Contain("5/5").And.Contain("best baseline 3"),
                "confirms this is a real comparison, not a trivial B3-beats-zero pass");
        });
    }

    [Test]
    public void BenchmarkGateMarkdown_ListsSystemsMetricsAndNoGoVerdict()
    {
        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(
            singleNodeContextFabric: null,
            quoteAnchoring: null,
            boundaryStitch: null);

        var markdown = ContextFabricBenchmarkGateWriter.BuildMarkdown(report);

        Assert.Multiple(() =>
        {
            Assert.That(markdown, Does.Contain("Context Fabric CF-7 Benchmark Gate"));
            Assert.That(markdown, Does.Contain("NO-GO"));
            Assert.That(markdown, Does.Contain("B0"));
            Assert.That(markdown, Does.Contain("B4"));
            Assert.That(markdown, Does.Contain("B0-B4 frozen runs present"));
        });
    }

    [Test]
    public void ScaledCorpus_IsDeterministic_WithUniqueCanaries_AndFullExhaustiveGroundTruth()
    {
        var first = DeterministicFabricCorpus.Create(segmentCount: 24, backgroundLines: 30);
        var second = DeterministicFabricCorpus.Create(segmentCount: 24, backgroundLines: 30);
        var frozen = DeterministicFabricCorpus.Create();

        var exhaustive = first.Questions.Single(question => question.QuestionId == "exhaustive-archive-tokens");
        Assert.Multiple(() =>
        {
            Assert.That(first.Corpus.Segments, Has.Count.EqualTo(24));
            Assert.That(first.Corpus.SourceDigest, Is.EqualTo(second.Corpus.SourceDigest));
            Assert.That(first.Corpus.CorpusId, Is.EqualTo("cf0-synthetic-book-v1-x24b30"));
            Assert.That(frozen.Corpus.CorpusId, Is.EqualTo(DeterministicFabricCorpus.CorpusId),
                "the frozen 16-segment fixture identity must not change");
            Assert.That(exhaustive.ExpectedTerms, Has.Count.EqualTo(24));
            Assert.That(exhaustive.ExpectedTerms, Is.Unique);
            Assert.That(exhaustive.ExpectedSegmentIds, Has.Count.EqualTo(24));
            Assert.That(first.Corpus.Segments[16].Text, Does.Contain("relay checksum for section 017"),
                "segments beyond the curated 16 carry a unique deterministic canary");
            Assert.That(first.Corpus.EstimatedSourceTokens, Is.GreaterThan(frozen.Corpus.EstimatedSourceTokens));
        });
    }

    private static FabricBenchmarkSystemGate PassedSystem(string systemId, string label, int passed, int total) =>
        new(systemId, label, FabricBenchmarkSystemStatus.Passed, $"Frozen run passed: {passed}/{total} correct.",
            PassedCount: passed, TotalCount: total);

    private static async Task<(
        FabricFeasibilityReport B3,
        FabricQuoteAnchorReport Quote,
        FabricBoundaryStitchReport Stitch)> BuildBaselineReportsAsync()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime());
        var b3 = await runner.RunAsync(fixture);
        var quote = new ContextFabricBenchmarkExpansionRunner(runtime: null)
            .RunQuoteAnchoringDiagnostics(fixture);
        var stitch = await new ContextFabricBenchmarkExpansionRunner(new ScriptedFabricRuntime())
            .RunBoundaryStitchDiagnosticsAsync(DeterministicFabricCorpus.CreateBoundaryStitchFixture());
        return (b3, quote, stitch);
    }
}
