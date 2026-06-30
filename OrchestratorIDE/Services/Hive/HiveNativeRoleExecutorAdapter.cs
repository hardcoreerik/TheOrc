// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Bridges the shared native role runtime into the HIVE worker contract.</summary>
public sealed class HiveNativeRoleExecutorAdapter(IRoleRuntime inner, string workspaceRoot) : IHiveNativeRoleExecutor
{
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

        var report = await new ContextFabricFeasibilityRunner(inner).ReadCorpusAsync(corpus, ct).ConfigureAwait(false);
        var segmentResult = report.SegmentResults.SingleOrDefault()
            ?? throw new InvalidOperationException("Context Fabric reader produced no segment result.");
        if (!segmentResult.Accepted || segmentResult.Card is null)
            throw new InvalidOperationException(
                $"Context Fabric reader rejected segment '{corpus.Segments[0].SegmentId}': {string.Join("; ", segmentResult.Errors)}");

        var json = FabricJson.Serialize(segmentResult.Card);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "evidence-card.json"), json, ct).ConfigureAwait(false);

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

        var nodes = await new ContextFabricFeasibilityRunner(inner)
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

    private sealed record ReducerOutput(
        string CorpusId,
        string DocumentId,
        string GenerationId,
        int NodeCount,
        IReadOnlyList<FabricReductionNode> Nodes);

    private static string SafeSegment(string value)
    {
        var safe = new string(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').Take(96).ToArray());
        return safe.Length == 0 ? "unknown" : safe;
    }

    public ValueTask DisposeAsync() =>
        inner is IAsyncDisposable asyncDisposable ? asyncDisposable.DisposeAsync() : ValueTask.CompletedTask;
}
