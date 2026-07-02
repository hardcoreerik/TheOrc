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

        var report = ContextFabricBenchmarkGateEvaluator.Evaluate(b3, quote, stitch,
        [
            PassedSystem("B0", "Closed-book native model"),
            PassedSystem("B1", "Truncated prompt"),
            PassedSystem("B2", "Conventional top-k RAG"),
            PassedSystem("B4", "HIVE Context Fabric"),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(report.SchemaVersion, Is.EqualTo(FabricSchemaVersions.BenchmarkGate));
            Assert.That(report.ReadyForExpansion, Is.True);
            Assert.That(report.Systems, Has.All.Matches<FabricBenchmarkSystemGate>(
                system => system.Status == FabricBenchmarkSystemStatus.Passed));
            Assert.That(report.Gates, Has.All.Matches<FabricGateResult>(gate => gate.Passed));
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

    private static FabricBenchmarkSystemGate PassedSystem(string systemId, string label) =>
        new(systemId, label, FabricBenchmarkSystemStatus.Passed, "Frozen run passed.");

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
