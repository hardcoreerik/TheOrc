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

        foreach (var testCase in fixture.Cases)
        {
            ct.ThrowIfCancellationRequested();
            var invocation = await InvokeAsync(testCase, ct).ConfigureAwait(false);
            calls.Add(invocation.Metrics);
            if (!invocation.Metrics.Succeeded)
            {
                results.Add(new FabricBoundaryStitchResult(
                    testCase.CaseId,
                    false,
                    "",
                    [],
                    [invocation.Metrics.Error ?? "stitch invocation failed"],
                    invocation.Metrics));
                continue;
            }

            try
            {
                var draft = FabricJson.ParseModelObject<FabricBoundaryStitchDraft>(invocation.Output);
                results.Add(ValidateStitchDraft(testCase, draft, invocation.Metrics));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
                results.Add(new FabricBoundaryStitchResult(
                    testCase.CaseId,
                    false,
                    "",
                    [],
                    [$"invalid stitch JSON: {ex.Message}"],
                    invocation.Metrics with { Succeeded = false, Error = ex.Message }));
            }
        }

        return new FabricBoundaryStitchReport(
            FabricSchemaVersions.StitchDiagnostics,
            _runtime.RuntimeName,
            fixture.FixtureId,
            DateTimeOffset.UtcNow,
            results,
            calls);
    }

    private static FabricBoundaryStitchResult ValidateStitchDraft(
        FabricBoundaryStitchCase testCase,
        FabricBoundaryStitchDraft draft,
        FabricCallMetrics metrics)
    {
        var errors = new List<string>();
        if (!string.Equals(draft.SchemaVersion, FabricSchemaVersions.Stitch, StringComparison.Ordinal))
            errors.Add($"schemaVersion must be '{FabricSchemaVersions.Stitch}'");
        if (!string.Equals(draft.CaseId, testCase.CaseId, StringComparison.Ordinal))
            errors.Add($"caseId must be '{testCase.CaseId}'");
        if (string.IsNullOrWhiteSpace(draft.Summary))
            errors.Add("summary is required");

        if ((draft.LinkedFacts?.Count ?? 0) < testCase.ExpectedLinkedFacts.Count)
            errors.Add($"linkedFacts must contain at least {testCase.ExpectedLinkedFacts.Count} items");

        foreach (var term in testCase.ForbiddenTerms)
        {
            if ((draft.Summary?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (draft.LinkedFacts ?? []).Any(item => item.Contains(term, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"contains forbidden term '{term}'");
        }

        if (!(draft.Summary?.Contains(testCase.ExpectedSummary, StringComparison.OrdinalIgnoreCase) ?? false))
            errors.Add("summary did not preserve the expected stitched fact");

        return new FabricBoundaryStitchResult(
            testCase.CaseId,
            errors.Count == 0,
            draft.Summary ?? "",
            (draft.LinkedFacts ?? []).ToArray(),
            errors,
            metrics with { Succeeded = errors.Count == 0, Error = errors.Count == 0 ? null : string.Join("; ", errors) });
    }

    private async Task<(string Output, FabricCallMetrics Metrics)> InvokeAsync(
        FabricBoundaryStitchCase testCase,
        CancellationToken ct)
    {
        var input = new StitchInput(
            FabricSchemaVersions.Stitch,
            testCase.CaseId,
            testCase.LeftText,
            testCase.RightText);
        var messages = new AgentMessage[]
        {
            SystemMessage(
                "[FABRIC_STITCHER] Return one JSON object only. Merge only cross-boundary facts supported by the neighboring segments. " +
                "Do not invent facts and do not rewrite supported values. Output shape: " +
                "{\"schemaVersion\":\"cf0-stitch-1.0\",\"caseId\":\"...\",\"summary\":\"...\",\"linkedFacts\":[\"...\"]}"),
            UserMessage(FabricJson.Serialize(input)),
        };

        var promptTokens = messages.Sum(message => ContextManager.EstimateTokens(message.Content));
        if (promptTokens + _options.ReaderMaxTokens > _options.ContextBudget.ContextLimit)
            throw new FabricContextBudgetExceededException(
                $"stitch/{testCase.CaseId} requires up to {promptTokens + _options.ReaderMaxTokens} tokens, exceeding {_options.ContextBudget.ContextLimit}.");

        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder();
        var reportedPrompt = 0;
        var reportedCompletion = 0;
        try
        {
            await foreach (var token in _runtime!.StreamRoleCompletionAsync(
                RuntimeRole.Researcher,
                messages,
                temperature: _options.Temperature,
                maxTokens: _options.ReaderMaxTokens,
                onUsage: (prompt, completion) =>
                {
                    reportedPrompt = prompt;
                    reportedCompletion = completion;
                },
                ct: ct).ConfigureAwait(false))
            {
                output.Append(token);
            }

            stopwatch.Stop();
            return (
                output.ToString(),
                new FabricCallMetrics(
                    "stitch",
                    testCase.CaseId,
                    RuntimeRole.Researcher,
                    reportedPrompt > 0 ? reportedPrompt : promptTokens,
                    reportedCompletion > 0 ? reportedCompletion : ContextManager.EstimateTokens(output.ToString()),
                    _options.ContextBudget.ContextLimit,
                    stopwatch.ElapsedMilliseconds,
                    true,
                    PromptPath: ResolvePromptPath(RuntimeRole.Researcher),
                    RawOutputExcerpt: BuildRawOutputExcerpt(output.ToString())));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return (
                output.ToString(),
                new FabricCallMetrics(
                    "stitch",
                    testCase.CaseId,
                    RuntimeRole.Researcher,
                    reportedPrompt > 0 ? reportedPrompt : promptTokens,
                    reportedCompletion > 0 ? reportedCompletion : ContextManager.EstimateTokens(output.ToString()),
                    _options.ContextBudget.ContextLimit,
                    stopwatch.ElapsedMilliseconds,
                    false,
                    ex.Message,
                    ResolvePromptPath(RuntimeRole.Researcher),
                    BuildRawOutputExcerpt(output.ToString())));
        }
    }

    private string? ResolvePromptPath(RuntimeRole role) =>
        _runtime is IRoleRuntimeDiagnostics diagnostics
            ? diagnostics.GetLastPromptPath(role)
            : null;

    private static string? BuildRawOutputExcerpt(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        var compact = output
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        if (compact.Length <= MaxRawOutputExcerptChars)
            return compact;
        return compact[..MaxRawOutputExcerptChars] + "...";
    }

    private static AgentMessage SystemMessage(string content) => new()
    {
        Role = MessageRole.System,
        Content = content,
        Status = MessageStatus.Complete,
    };

    private static AgentMessage UserMessage(string content) => new()
    {
        Role = MessageRole.User,
        Content = content,
        Status = MessageStatus.Complete,
    };

    private sealed record StitchInput(
        string SchemaVersion,
        string CaseId,
        string LeftText,
        string RightText);
}
