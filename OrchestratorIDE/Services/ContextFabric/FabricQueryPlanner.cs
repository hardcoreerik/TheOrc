// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricQueryPlanner(
    FabricSearchService searchService,
    DocumentGraphRepository graphRepository)
{
    public FabricQueryPlan BuildPlan(
        string query,
        string corpusId,
        string? mode = null,
        FabricQueryPlannerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query is required.", nameof(query));
        if (string.IsNullOrWhiteSpace(corpusId))
            throw new ArgumentException("Corpus id is required.", nameof(corpusId));

        var effective = options ?? new FabricQueryPlannerOptions();
        effective.Validate();

        var resolvedMode = string.IsNullOrWhiteSpace(mode) ? ClassifyMode(query) : mode.Trim().ToLowerInvariant();
        var hits = searchService.Search(query, corpusId, effective.RetrievalLimit);
        var summaryNodeIds = hits
            .Select(item => item.DocumentId)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(documentId =>
            {
                var highest = graphRepository.ListMemoryNodes(corpusId, documentId, limit: 16)
                    .GroupBy(node => node.Generation)
                    .OrderByDescending(group => group.Key)
                    .FirstOrDefault();
                return highest?.ToArray() ?? [];
            })
            .Select(node => node.NodeId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (summaryNodeIds.Length == 0)
        {
            summaryNodeIds = graphRepository.ListMemoryNodes(corpusId, limit: 32)
                .GroupBy(node => node.DocumentId, StringComparer.Ordinal)
                .SelectMany(group =>
                {
                    var highest = group
                        .GroupBy(node => node.Generation)
                        .OrderByDescending(level => level.Key)
                        .FirstOrDefault();
                    return highest?.Select(node => node.NodeId) ?? [];
                })
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var reopenedSegmentIds = resolvedMode == FabricQueryMode.Study
            ? ReopenSegments(query, summaryNodeIds, effective.MaxSourceOpens)
            : [];

        return new FabricQueryPlan(
            query.Trim(),
            corpusId.Trim(),
            resolvedMode,
            effective.MaxRounds,
            effective.MaxSourceOpens,
            effective.MaxPromptTokens,
            effective.ResponseTokenReserve,
            hits,
            summaryNodeIds,
            reopenedSegmentIds,
            reopenedSegmentIds.Count > 0);
    }

    private IReadOnlyList<string> ReopenSegments(string query, IReadOnlyList<string> summaryNodeIds, int maxSourceOpens)
    {
        var queryTokens = Tokenize(query);
        var reopened = new List<string>(maxSourceOpens);
        var pending = new Queue<string>(summaryNodeIds);
        var seenNodes = new HashSet<string>(StringComparer.Ordinal);
        var seenSegments = new HashSet<string>(StringComparer.Ordinal);

        while (pending.Count > 0 && reopened.Count < maxSourceOpens)
        {
            var nodeId = pending.Dequeue();
            if (!seenNodes.Add(nodeId))
                continue;

            var node = graphRepository.GetMemoryNode(nodeId);
            if (node is null)
                continue;

            var summaryCoverage = OverlapScore(queryTokens, Tokenize(node.SummaryText));
            if (node.CoverageStatus == FabricCoverageStatus.Complete && summaryCoverage >= 0.60)
                continue;

            foreach (var membership in graphRepository.ListMemoryMemberships(nodeId))
            {
                if (membership.ChildKind == "segment")
                {
                    if (seenSegments.Add(membership.ChildId))
                        reopened.Add(membership.ChildId);
                }
                else
                {
                    pending.Enqueue(membership.ChildId);
                }

                if (reopened.Count >= maxSourceOpens)
                    break;
            }
        }

        return reopened;
    }

    private static string ClassifyMode(string query)
    {
        var lower = query.ToLowerInvariant();
        return lower.Contains("compare", StringComparison.Ordinal) ||
               lower.Contains("across", StringComparison.Ordinal) ||
               lower.Contains("between", StringComparison.Ordinal) ||
               lower.Contains("change", StringComparison.Ordinal) ||
               lower.Contains("exception", StringComparison.Ordinal) ||
               lower.Contains("why", StringComparison.Ordinal) ||
               lower.Contains("how", StringComparison.Ordinal)
            ? FabricQueryMode.Study
            : FabricQueryMode.Quick;
    }

    private static HashSet<string> Tokenize(string text) => text
        .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '?', '!'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => item.ToLowerInvariant())
        .Where(item => item.Length >= 3)
        .ToHashSet(StringComparer.Ordinal);

    private static double OverlapScore(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
            return 0;

        var matches = left.Count(right.Contains);
        return matches / (double)left.Count;
    }
}
