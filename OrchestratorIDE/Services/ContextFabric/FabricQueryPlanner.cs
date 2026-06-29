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

        var normalizedQuery = query.Trim();
        var normalizedCorpusId = corpusId.Trim();
        var resolvedMode = string.IsNullOrWhiteSpace(mode)
            ? ClassifyMode(normalizedQuery)
            : NormalizeMode(mode);
        var hits = searchService.Search(normalizedQuery, normalizedCorpusId, effective.RetrievalLimit);
        var summaryNodeIds = hits
            .Select(item => item.DocumentId)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(documentId =>
            {
                var highest = graphRepository.ListMemoryNodes(normalizedCorpusId, documentId, limit: 16)
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
            summaryNodeIds = graphRepository.ListMemoryNodes(normalizedCorpusId, limit: 32)
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
            ? ReopenSegments(normalizedQuery, summaryNodeIds, effective.MaxSourceOpens)
            : [];

        return new FabricQueryPlan(
            normalizedQuery,
            normalizedCorpusId,
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

    private static string NormalizeMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        FabricQueryMode.Quick => FabricQueryMode.Quick,
        FabricQueryMode.Study => FabricQueryMode.Study,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), $"Unsupported Context Fabric query mode '{mode}'."),
    };

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
        return Tokenize(query).Overlaps(["compare", "across", "between", "change", "exception", "why", "how"])
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
