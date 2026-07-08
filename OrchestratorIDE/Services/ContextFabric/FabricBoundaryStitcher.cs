// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricBoundaryStitcher
{
    private const int MaxRawOutputExcerptChars = 400;

    // Single source of truth lives in FabricContextBudget; see comment there for rationale.
    private const double TokenSafetyMargin = FabricContextBudget.TokenSafetyMargin;
    private readonly IRoleRuntime _runtime;
    private readonly FabricRunOptions _options;

    public FabricBoundaryStitcher(IRoleRuntime runtime, FabricRunOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? FabricRunOptions.Default;
        _options.Validate();
    }

    public async Task<FabricBoundaryStitchResult> StitchAsync(
        FabricBoundaryStitchCase testCase,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        var invocation = await InvokeAsync(testCase, ct).ConfigureAwait(false);
        if (!invocation.Metrics.Succeeded)
        {
            return new FabricBoundaryStitchResult(
                testCase.CaseId,
                false,
                "",
                [],
                [invocation.Metrics.Error ?? "stitch invocation failed"],
                invocation.Metrics);
        }

        try
        {
            var draft = FabricJson.ParseModelObject<FabricBoundaryStitchDraft>(invocation.Output);
            return ValidateDraft(testCase, draft, invocation.Metrics);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            return new FabricBoundaryStitchResult(
                testCase.CaseId,
                false,
                "",
                [],
                [$"invalid stitch JSON: {ex.Message}"],
                invocation.Metrics with { Succeeded = false, Error = ex.Message });
        }
    }

    private FabricBoundaryStitchResult ValidateDraft(
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

        // Exact substring/equality against ExpectedSummary/ExpectedLinkedFacts rejects any valid
        // paraphrase ("resulting in" vs "which resulted in") even when the content is fully
        // correct -- observed against a real model. Compare on key-fact anchor-word coverage
        // instead, mirroring the CF-6 acceptance harness: a missing fact's distinctive
        // nouns/numbers won't appear at all, so genuine content loss still fails. Expected facts
        // must be covered by the linkedFacts list itself -- a fact that survives only inside the
        // summary was not listed separately and still counts as missing.
        var linkedFactsText = string.Join(" ", draft.LinkedFacts ?? []);
        var combinedText = (draft.Summary ?? "") + " " + linkedFactsText;
        foreach (var expectedFact in testCase.ExpectedLinkedFacts)
        {
            if (!AnchorWordsCovered(expectedFact, linkedFactsText))
                errors.Add($"missing expected linked fact '{expectedFact}'");
        }

        foreach (var term in testCase.ForbiddenTerms)
        {
            if (combinedText.Contains(term, StringComparison.OrdinalIgnoreCase))
                errors.Add($"contains forbidden term '{term}'");
        }

        if (!AnchorWordsCovered(testCase.ExpectedSummary, draft.Summary ?? ""))
            errors.Add("summary did not preserve the expected stitched fact");

        return new FabricBoundaryStitchResult(
            testCase.CaseId,
            errors.Count == 0,
            draft.Summary ?? "",
            (draft.LinkedFacts ?? []).ToArray(),
            errors,
            metrics with { Succeeded = errors.Count == 0, Error = errors.Count == 0 ? null : string.Join("; ", errors) });
    }

    /// <summary>Anchor-word coverage check shared in spirit with Cf6AcceptanceRunner: every word of
    /// at least 5 characters in the expected text (compared by its first 6 characters, so simple
    /// inflections like "resulted"/"resulting" match) must appear in the actual output. Accepts
    /// legitimate paraphrase while still catching dropped facts (a missing fact's distinctive
    /// nouns/numbers won't appear at all, stemmed or not).</summary>
    private static bool AnchorWordsCovered(string expected, string actual)
    {
        var anchors = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '"', '\''))
            .Where(w => w.Length >= 5)
            .Select(w => w[..Math.Min(w.Length, 6)])
            .ToList();
        // An expected text with no anchor-length word can't be checked by coverage; fall back to
        // exact containment rather than unconditionally failing legitimate short-word facts.
        return anchors.Count > 0
            ? anchors.All(w => actual.Contains(w, StringComparison.OrdinalIgnoreCase))
            : actual.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase);
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
                "[FABRIC_STITCHER] Return one JSON object only. Merge ONLY the facts that span the boundary between " +
                "the two neighboring segments: when a pronoun, clause, or reference in one segment points to something " +
                "in the other, resolve it to the exact entity and keep BOTH the referring fact AND the fact it refers " +
                "to -- never drop the referenced fact (e.g. the statement an unresolved pronoun points back to). Do NOT " +
                "restate unrelated facts, do not invent facts, and do not rewrite supported values. List each linked " +
                "fact separately in linkedFacts. Output shape: " +
                "{\"schemaVersion\":\"cf0-stitch-1.0\",\"caseId\":\"...\",\"summary\":\"...\",\"linkedFacts\":[\"...\"]}"),
            UserMessage(FabricJson.Serialize(input)),
        };

        var rawPromptTokens = messages.Sum(message => ContextManager.EstimateTokens(message.Content));
        var gatePromptTokens = (int)Math.Ceiling(rawPromptTokens * TokenSafetyMargin);
        if (gatePromptTokens + _options.ReaderMaxTokens > _options.ContextBudget.ContextLimit)
            throw new FabricContextBudgetExceededException(
                $"stitch/{testCase.CaseId} requires up to {gatePromptTokens + _options.ReaderMaxTokens} tokens (margin-adjusted), exceeding {_options.ContextBudget.ContextLimit}.");

        var stopwatch = Stopwatch.StartNew();
        var output = new StringBuilder();
        var reportedPrompt = 0;
        var reportedCompletion = 0;
        try
        {
            await foreach (var token in _runtime.StreamRoleCompletionAsync(
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
                    reportedPrompt > 0 ? reportedPrompt : rawPromptTokens,
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
                    reportedPrompt > 0 ? reportedPrompt : rawPromptTokens,
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
        return compact.Length <= MaxRawOutputExcerptChars
            ? compact
            : compact[..MaxRawOutputExcerptChars] + "...";
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
