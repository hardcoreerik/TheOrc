// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Bridges the shared native role runtime into the HIVE worker contract.</summary>
public sealed class HiveNativeRoleExecutorAdapter(IRoleRuntime inner, string workspaceRoot) : IHiveNativeRoleExecutor
{
    /// <summary>
    /// ModelAdmissionGate.EvaluateContextFabric admits reasoning-tuned models (DeepSeek-R1-distill,
    /// Qwen3, etc.) only as "Provisional" specifically because their visible &lt;think&gt; trace can
    /// consume the whole response budget before the required JSON object is emitted -- observed in
    /// production as "Model response contained an unterminated JSON object." FabricRunOptions.Default
    /// (1024 reader tokens) has no room for that trace. This budget gives it room; ResponseReserve is
    /// raised to match so the evidence-text budget math in FabricContextBudget stays consistent.
    /// </summary>
    private static readonly FabricRunOptions HiveDispatchOptions = new(
        new FabricContextBudget(ContextLimit: 8192, ResponseReserve: 4608, SystemReserve: 512),
        ReaderMaxTokens: 4096,
        ReducerMaxTokens: 2048,
        AnswerMaxTokens: 3072);


    public static RuntimeRole MapHiveRoleToRuntimeRole(string? hiveRole) =>
        (hiveRole ?? "").Trim().ToLowerInvariant() switch
        {
            "boss"       => RuntimeRole.Boss,
            "researcher" => RuntimeRole.Researcher,
            "reviewer"   => RuntimeRole.Reviewer,
            _            => RuntimeRole.Worker,
        };

    public async IAsyncEnumerable<string> StreamRoleCompletionAsync(
        string hiveRole,
        IReadOnlyList<AgentMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var role = MapHiveRoleToRuntimeRole(hiveRole);
        IAsyncEnumerator<string> enumerator;
        try
        {
            enumerator = inner.StreamRoleCompletionAsync(role, messages, ct: ct).GetAsyncEnumerator(ct);
        }
        catch (RuntimeAdmissionDeniedException ex)
        {
            throw new HiveNativeRoleAdmissionDeniedException(ex.Decision.Reason ?? ex.Message, ex);
        }

        await using (enumerator)
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (RuntimeAdmissionDeniedException ex)
                {
                    throw new HiveNativeRoleAdmissionDeniedException(ex.Decision.Reason ?? ex.Message, ex);
                }

                if (!hasNext) yield break;
                yield return enumerator.Current;
            }
        }
    }

    public string DescribeTelemetry(string hiveRole)
    {
        var role = MapHiveRoleToRuntimeRole(hiveRole);
        var health = inner.GetHealth(role);
        var stats = inner.GetStats(role);
        var parts = new List<string> { $"runtime={inner.RuntimeName}" };
        if (!string.IsNullOrWhiteSpace(health.ActiveModel)) parts.Add($"model={health.ActiveModel}");
        if (stats.LastTimeToFirstToken is { } ttft) parts.Add($"ttft={ttft.TotalMilliseconds:F0}ms");
        if (stats.TokensPerSecond is { } tps) parts.Add($"tok/s={tps:F1}");
        if (stats.EstimatedVramBytes is { } bytes) parts.Add($"est_vram={bytes / (1024.0 * 1024 * 1024):F1}GB");
        if (!string.IsNullOrWhiteSpace(health.Message)) parts.Add($"status={health.Message}");
        return $" ({string.Join(", ", parts)})";
    }

    public async Task<HiveNativeAgentExecution> ExecuteAgentAsync(
        HiveTaskBundle bundle,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct)
    {
        var safeCampaign = SafeSegment(bundle.CampaignId.Length > 0 ? bundle.CampaignId : "legacy");
        var safeUnit = SafeSegment(bundle.WorkUnitId.Length > 0 ? bundle.WorkUnitId : bundle.TaskId);
        var outputDirectory = Path.Combine(workspaceRoot, ".orc", "remote-work", safeCampaign, safeUnit);
        Directory.CreateDirectory(outputDirectory);
        var loop = new HeadlessAgentLoop(inner);
        var result = await loop.ExecuteAsync(
            MapHiveRoleToRuntimeRole(bundle.NativeRole.Length > 0 ? bundle.NativeRole : bundle.Role),
            messages,
            NativeWorkerToolProfile.Create(outputDirectory),
            new HeadlessAgentLimits(
                MaxSteps: 12,
                MaxTokensPerStep: 4096,
                Timeout: TimeSpan.FromMilliseconds(Math.Max(1_000, bundle.TimeoutMs))),
            ct: ct).ConfigureAwait(false);
        return new HiveNativeAgentExecution(result.Output, outputDirectory, result.Steps,
            result.PromptTokens, result.CompletionTokens, result.TraceDigest);
    }

    public async Task<HiveNativeAgentExecution> ExecuteContextFabricReaderAsync(
        HiveTaskBundle bundle,
        FabricCorpus corpus,
        CancellationToken ct)
    {
        var safeCampaign = SafeSegment(bundle.CampaignId.Length > 0 ? bundle.CampaignId : "legacy");
        var safeUnit = SafeSegment(bundle.WorkUnitId.Length > 0 ? bundle.WorkUnitId : bundle.TaskId);
        var outputDirectory = Path.Combine(workspaceRoot, ".orc", "remote-work", safeCampaign, safeUnit);
        Directory.CreateDirectory(outputDirectory);

        if (corpus.Segments.Count != 1)
            throw new InvalidOperationException(
                $"Context Fabric reader requires a single-segment corpus, got {corpus.Segments.Count}. " +
                "Use CampaignTemplates.StageReaderCorpusAsync to split a multi-segment corpus first.");

        var report = await new ContextFabricFeasibilityRunner(inner, HiveDispatchOptions)
            .ReadCorpusAsync(corpus, ct).ConfigureAwait(false);
        var segmentResult = report.SegmentResults.SingleOrDefault()
            ?? throw new InvalidOperationException("Context Fabric reader produced no segment result.");
        if (!segmentResult.Accepted || segmentResult.Card is null)
            throw new InvalidOperationException(
                $"Context Fabric reader rejected segment '{corpus.Segments[0].SegmentId}': {string.Join("; ", segmentResult.Errors)}");

        var json = FabricJson.Serialize(segmentResult.Card);
        // Downstream fetchers (FetchVerifierInputsAsync, FetchReducerInputsAsync) match on
        // Name.EndsWith(".evidence-card.json") -- a bare "evidence-card.json" has no leading dot to
        // match, so every verifier/reducer unit failed with "no '.evidence-card.json' input
        // artifact" despite the reader itself succeeding. The segment-id prefix also matches the
        // "{segmentId}.corpus.json" convention used for every other staged artifact in this pack.
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, $"{corpus.Segments[0].SegmentId}.evidence-card.json"), json, ct)
            .ConfigureAwait(false);

        return new HiveNativeAgentExecution(json, outputDirectory, Steps: 1,
            segmentResult.Metrics.PromptTokens, segmentResult.Metrics.CompletionTokens,
            FabricHashing.Sha256(json));
    }

    public async Task<HiveNativeAgentExecution> ExecuteContextFabricReducerAsync(
        HiveTaskBundle bundle,
        FabricCorpus corpusMeta,
        IReadOnlyList<FabricEvidenceCard> cards,
        CancellationToken ct)
    {
        if (cards.Count == 0)
            throw new InvalidOperationException("Context Fabric reducer received no evidence cards to reduce.");

        var safeCampaign = SafeSegment(bundle.CampaignId.Length > 0 ? bundle.CampaignId : "legacy");
        var safeUnit = SafeSegment(bundle.WorkUnitId.Length > 0 ? bundle.WorkUnitId : bundle.TaskId);
        var outputDirectory = Path.Combine(workspaceRoot, ".orc", "remote-work", safeCampaign, safeUnit);
        Directory.CreateDirectory(outputDirectory);

        var nodes = await new ContextFabricFeasibilityRunner(inner, HiveDispatchOptions)
            .ReduceEvidenceCardsAsync(corpusMeta, cards, ct).ConfigureAwait(false);

        var json = FabricJson.Serialize(new ReducerOutput(
            corpusMeta.CorpusId,
            corpusMeta.DocumentId,
            corpusMeta.GenerationId,
            nodes.Count,
            nodes));
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "reduction-nodes.json"), json, ct)
            .ConfigureAwait(false);

        var promptTokens = 0;
        var completionTokens = 0;
        return new HiveNativeAgentExecution(json, outputDirectory, Steps: nodes.Count,
            promptTokens, completionTokens, FabricHashing.Sha256(json));
    }

    public async Task<HiveNativeAgentExecution> ExecuteContextFabricStitcherAsync(
        HiveTaskBundle bundle,
        FabricCorpus leftCorpus,
        FabricCorpus rightCorpus,
        CancellationToken ct)
    {
        if (leftCorpus.Segments.Count != 1 || rightCorpus.Segments.Count != 1)
            throw new InvalidOperationException(
                $"Context Fabric stitcher requires single-segment corpora; got left={leftCorpus.Segments.Count} right={rightCorpus.Segments.Count}.");

        var safeCampaign = SafeSegment(bundle.CampaignId.Length > 0 ? bundle.CampaignId : "legacy");
        var safeUnit = SafeSegment(bundle.WorkUnitId.Length > 0 ? bundle.WorkUnitId : bundle.TaskId);
        var outputDirectory = Path.Combine(workspaceRoot, ".orc", "remote-work", safeCampaign, safeUnit);
        Directory.CreateDirectory(outputDirectory);

        var left = leftCorpus.Segments[0];
        var right = rightCorpus.Segments[0];
        var caseId = $"stitch-{left.SegmentId}-{right.SegmentId}";
        var testCase = new FabricBoundaryStitchCase(
            caseId,
            left.Text,
            right.Text,
            ExpectedSummary: "",
            ExpectedLinkedFacts: [],
            ForbiddenTerms: []);

        var result = await new FabricBoundaryStitcher(inner, HiveDispatchOptions)
            .StitchAsync(testCase, ct).ConfigureAwait(false);
        var json = FabricJson.Serialize(new StitchOutput(
            leftCorpus.CorpusId,
            leftCorpus.DocumentId,
            left.SegmentId,
            right.SegmentId,
            result.Passed,
            result.Summary,
            result.LinkedFacts));
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "stitch-result.json"), json, ct)
            .ConfigureAwait(false);

        return new HiveNativeAgentExecution(json, outputDirectory, Steps: 1,
            result.Metrics.PromptTokens, result.Metrics.CompletionTokens, FabricHashing.Sha256(json));
    }

    public async Task<HiveNativeAgentExecution> ExecuteContextFabricVerifierAsync(
        HiveTaskBundle bundle,
        FabricEvidenceCard card,
        FabricCorpus sourceCorpus,
        CancellationToken ct)
    {
        if (sourceCorpus.Segments.Count != 1)
            throw new InvalidOperationException(
                $"Context Fabric verifier requires a single-segment source corpus, got {sourceCorpus.Segments.Count}.");

        var safeCampaign = SafeSegment(bundle.CampaignId.Length > 0 ? bundle.CampaignId : "legacy");
        var safeUnit = SafeSegment(bundle.WorkUnitId.Length > 0 ? bundle.WorkUnitId : bundle.TaskId);
        var outputDirectory = Path.Combine(workspaceRoot, ".orc", "remote-work", safeCampaign, safeUnit);
        Directory.CreateDirectory(outputDirectory);

        var segment = sourceCorpus.Segments[0];
        var items = new List<FabricHiveVerificationItem>(card.Claims.Count);
        foreach (var claim in card.Claims)
        {
            if (claim is null) continue;
            var errors = new List<string>();
            foreach (var citation in claim.Citations ?? [])
            {
                if (citation is null) continue;
                if (!string.IsNullOrWhiteSpace(citation.Quote))
                {
                    var pos = segment.Text.IndexOf(citation.Quote, StringComparison.Ordinal);
                    if (pos < 0)
                        errors.Add($"Quote not found in source: '{citation.Quote[..Math.Min(80, citation.Quote.Length)]}'");
                    else if (citation.CharStart >= 0 && citation.CharStart != pos)
                        errors.Add($"CharStart mismatch: expected {pos}, got {citation.CharStart}");
                    if (!string.IsNullOrWhiteSpace(citation.QuoteDigest))
                    {
                        var expectedDigest = FabricHashing.Sha256(citation.Quote);
                        if (!string.Equals(expectedDigest, citation.QuoteDigest, StringComparison.OrdinalIgnoreCase))
                            errors.Add($"QuoteDigest mismatch for claim {claim.ClaimId}");
                    }
                }
            }
            items.Add(new FabricHiveVerificationItem(
                claim.ClaimId ?? "",
                segment.SegmentId,
                errors.Count == 0,
                errors));
        }

        var report = new FabricHiveVerificationReport(
            sourceCorpus.CorpusId,
            sourceCorpus.DocumentId,
            segment.SegmentId,
            items.All(item => item.Passed),
            items);
        var json = FabricJson.Serialize(report);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verification-result.json"), json, ct)
            .ConfigureAwait(false);

        return new HiveNativeAgentExecution(json, outputDirectory, Steps: items.Count,
            0, 0, FabricHashing.Sha256(json));
    }

    public async Task<HiveNativeAgentExecution> ExecuteContextFabricQueryAsync(
        HiveTaskBundle bundle,
        string questionId,
        string questionText,
        FabricCorpus corpus,
        FabricEvidenceCard? card,
        CancellationToken ct)
    {
        var safeCampaign = SafeSegment(bundle.CampaignId.Length > 0 ? bundle.CampaignId : "legacy");
        var safeUnit = SafeSegment(bundle.WorkUnitId.Length > 0 ? bundle.WorkUnitId : bundle.TaskId);
        var outputDirectory = Path.Combine(workspaceRoot, ".orc", "remote-work", safeCampaign, safeUnit);
        Directory.CreateDirectory(outputDirectory);

        var finding = card is not null
            ? ContextFabricFeasibilityRunner.QueryEvidenceCard(card, questionId, questionText)
            : await new ContextFabricFeasibilityRunner(inner, HiveDispatchOptions)
                .QuerySegmentAsync(corpus, questionId, questionText, ct).ConfigureAwait(false);

        var json = FabricJson.Serialize(finding);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "query-finding.json"), json, ct)
            .ConfigureAwait(false);

        return new HiveNativeAgentExecution(json, outputDirectory,
            Steps: finding.Relevant ? 1 : 0,
            finding.Metrics.PromptTokens, finding.Metrics.CompletionTokens,
            FabricHashing.Sha256(json));
    }

    private sealed record ReducerOutput(
        string CorpusId,
        string DocumentId,
        string GenerationId,
        int NodeCount,
        IReadOnlyList<FabricReductionNode> Nodes);

    private sealed record StitchOutput(
        string CorpusId,
        string DocumentId,
        string LeftSegmentId,
        string RightSegmentId,
        bool Passed,
        string Summary,
        IReadOnlyList<string> LinkedFacts);

    private static string SafeSegment(string value)
    {
        var safe = new string(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').Take(96).ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }

    public ValueTask DisposeAsync() =>
        inner is IAsyncDisposable asyncDisposable ? asyncDisposable.DisposeAsync() : ValueTask.CompletedTask;
}
