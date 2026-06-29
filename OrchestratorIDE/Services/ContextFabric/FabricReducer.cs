// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricReducer(
    FabricLibraryRepository libraryRepository,
    DocumentGraphRepository graphRepository,
    FabricReducerOptions? options = null)
{
    private const string ReducerVersion = "fabric-reducer-1.0";
    private readonly FabricReducerOptions _options = options ?? new FabricReducerOptions();

    public FabricReductionResult ReduceDocument(string documentId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Document id is required.", nameof(documentId));

        _options.Validate();

        var document = libraryRepository.GetDocument(documentId)
            ?? throw new KeyNotFoundException($"Context Fabric document '{documentId}' does not exist.");
        var segments = libraryRepository.GetSegments(documentId);
        if (segments.Count == 0)
            throw new InvalidDataException($"Document '{documentId}' has no segments to reduce.");

        var claimsBySegment = graphRepository.ListClaimsForDocument(documentId, limit: 4_096)
            .GroupBy(item => item.SegmentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<FabricClaimEntry>)group.ToArray(), StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var nodes = new List<FabricMemoryNodeEntry>();
        var memberships = new Dictionary<string, IReadOnlyList<FabricMemoryMembershipEntry>>(StringComparer.Ordinal);

        var current = segments.Select((segment, index) => CreateLeaf(segment, index, claimsBySegment)).ToList();
        var generation = 0;
        while (current.Count > 0)
        {
            var next = new List<ReductionChild>();
            foreach (var group in Chunk(current, _options.FanIn))
            {
                var groupList = group.ToArray();
                var summary = BuildSummary(groupList);
                var expected = groupList.Length;
                var covered = groupList.Count(item => item.IsCovered);
                var nodeId = BuildNodeId(document.DocumentId, generation, groupList[0].Ordinal, groupList[^1].Ordinal, summary);
                var node = new FabricMemoryNodeEntry(
                    nodeId,
                    document.CorpusId,
                    document.DocumentId,
                    generation == 0 ? "section" : "summary",
                    $"{document.DisplayName} g{generation} {groupList[0].Ordinal}-{groupList[^1].Ordinal}",
                    summary,
                    generation,
                    _options.FanIn,
                    expected,
                    covered,
                    covered == expected ? FabricCoverageStatus.Complete : FabricCoverageStatus.Incomplete,
                    ReducerVersion,
                    now,
                    now);

                nodes.Add(node);
                memberships[node.NodeId] = groupList.Select((child, ordinal) => new FabricMemoryMembershipEntry(
                    node.NodeId,
                    child.Kind,
                    child.Id,
                    ordinal,
                    child.IsCovered)).ToArray();

                next.Add(new ReductionChild(
                    "memory",
                    node.NodeId,
                    groupList[0].Ordinal,
                    node.SummaryText,
                    node.CoverageStatus == FabricCoverageStatus.Complete));
            }

            if (next.Count == 1)
            {
                graphRepository.ReplaceMemoryNodesForDocument(document.DocumentId, nodes, memberships);
                return new FabricReductionResult(document.DocumentId, nodes, memberships.Values.SelectMany(item => item).ToArray(), next[0].Id);
            }

            current = next;
            generation++;
        }

        throw new InvalidOperationException("Reducer produced no memory nodes.");
    }

    private ReductionChild CreateLeaf(
        FabricSegmentEntry segment,
        int ordinal,
        IReadOnlyDictionary<string, IReadOnlyList<FabricClaimEntry>> claimsBySegment)
    {
        claimsBySegment.TryGetValue(segment.SegmentId, out var claims);
        var isCovered = claims is { Count: > 0 };
        var summary = claims is { Count: > 0 }
            ? string.Join(" ", claims.Select(item => item.ClaimText).Distinct(StringComparer.Ordinal)).Trim()
            : segment.Text.Trim();

        return new ReductionChild(
            "segment",
            segment.SegmentId,
            ordinal,
            TrimSummary(summary),
            isCovered);
    }

    private string BuildSummary(IReadOnlyList<ReductionChild> children)
    {
        var covered = children.Where(item => item.IsCovered).Select(item => item.SummaryText);
        var summaries = covered.Any() ? covered : children.Select(item => item.SummaryText);
        return TrimSummary(string.Join(" ", summaries));
    }

    private string TrimSummary(string text)
    {
        text = text.Trim();
        if (text.Length <= _options.MaxSummaryChars)
            return text;
        return text[..(_options.MaxSummaryChars - 3)].TrimEnd() + "...";
    }

    private static string BuildNodeId(string documentId, int generation, int startOrdinal, int endOrdinal, string summary) =>
        $"mem-{FabricHashing.Sha256($"{documentId}|{generation}|{startOrdinal}|{endOrdinal}|{summary}")[..24]}";

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> items, int size)
    {
        for (var index = 0; index < items.Count; index += size)
            yield return items.Skip(index).Take(size).ToArray();
    }

    private sealed record ReductionChild(
        string Kind,
        string Id,
        int Ordinal,
        string SummaryText,
        bool IsCovered);
}
