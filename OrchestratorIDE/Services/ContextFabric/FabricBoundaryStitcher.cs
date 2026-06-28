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
