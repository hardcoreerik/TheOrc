// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class ContextFabricBenchmarkExpansionRunner
{
    private const int MaxRawOutputExcerptChars = 400;
    private readonly IRoleRuntime? _runtime;
    private readonly FabricRunOptions _options;

    public ContextFabricBenchmarkExpansionRunner(IRoleRuntime? runtime, FabricRunOptions? options = null)
    {
        _runtime = runtime;
        _options = options ?? FabricRunOptions.Default;
        _options.Validate();
    }

    public FabricQuoteAnchorReport RunQuoteAnchoringDiagnostics(FabricBenchmarkFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var cases = DeterministicFabricCorpus.CreateQuoteAnchorCases(fixture);
        var segments = fixture.Corpus.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var results = new List<FabricQuoteAnchorResult>(cases.Count);

        foreach (var testCase in cases)
        {
            if (!segments.TryGetValue(testCase.SegmentId, out var segment))
                throw new InvalidDataException($"Unknown segment '{testCase.SegmentId}' for quote case '{testCase.CaseId}'.");

            var diagnostic = FabricEvidenceProcessor.AnalyzeQuoteAnchor(segment, testCase.CandidateQuote);
            var result = diagnostic with
            {
                CaseId = testCase.CaseId,
                SegmentId = testCase.SegmentId,
                CandidateQuote = testCase.CandidateQuote,
            };
            results.Add(result);
        }

        return new FabricQuoteAnchorReport(
            FabricSchemaVersions.QuoteDiagnostics,
            fixture.Corpus.CorpusId,
            fixture.Corpus.GenerationId,
            DateTimeOffset.UtcNow,
            results);
    }

    public async Task<FabricBoundaryStitchReport> RunBoundaryStitchDiagnosticsAsync(
        FabricBoundaryStitchFixture fixture,
        CancellationToken ct = default)
    {
        if (_runtime is null)
            throw new InvalidOperationException("Boundary stitch diagnostics require a runtime.");

        ArgumentNullException.ThrowIfNull(fixture);
        var results = new List<FabricBoundaryStitchResult>(fixture.Cases.Count);
        var calls = new List<FabricCallMetrics>(fixture.Cases.Count);
        var stitcher = new FabricBoundaryStitcher(_runtime, _options);

        foreach (var testCase in fixture.Cases)
        {
            ct.ThrowIfCancellationRequested();
            var result = await stitcher.StitchAsync(testCase, ct).ConfigureAwait(false);
            results.Add(result);
            calls.Add(result.Metrics);
        }

        return new FabricBoundaryStitchReport(
            FabricSchemaVersions.StitchDiagnostics,
            _runtime.RuntimeName,
            fixture.FixtureId,
            DateTimeOffset.UtcNow,
            results,
            calls);
    }

}
