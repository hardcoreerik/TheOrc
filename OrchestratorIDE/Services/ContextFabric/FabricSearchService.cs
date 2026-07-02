// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricSearchService(
    FabricLibraryRepository libraryRepository,
    DocumentGraphRepository graphRepository)
{
    public IReadOnlyList<FabricRetrievalHit> Search(string query, string? corpusId = null, int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var hits = new List<FabricRetrievalHit>(limit);
        var seenSegments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segmentHit in libraryRepository.Search(query, corpusId, limit))
        {
            hits.Add(new FabricRetrievalHit(
                segmentHit.CorpusId,
                segmentHit.DocumentId,
                segmentHit.DisplayName,
                segmentHit.SegmentId,
                segmentHit.Ordinal,
                segmentHit.HeadingPath,
                segmentHit.Text,
                "segment",
                null,
                null,
                null,
                segmentHit.BlockKind,
                segmentHit.PageNumber,
                segmentHit.SourceLocator,
                segmentHit.Confidence));
            seenSegments.Add(segmentHit.SegmentId);
            if (hits.Count >= limit)
                return hits;
        }

        foreach (var claimHit in graphRepository.SearchClaims(query, corpusId, limit))
        {
            if (!seenSegments.Add(claimHit.SegmentId))
                continue;

            var segment = libraryRepository.GetSegment(claimHit.SegmentId);
            if (segment is null)
                continue;

            hits.Add(new FabricRetrievalHit(
                claimHit.CorpusId,
                claimHit.DocumentId,
                claimHit.DisplayName,
                claimHit.SegmentId,
                segment.Ordinal,
                segment.HeadingPath,
                segment.Text,
                "claim",
                claimHit.ClaimId,
                claimHit.ClaimText,
                claimHit.VerificationStatus,
                segment.BlockKind,
                segment.PageNumber,
                segment.SourceLocator,
                segment.Confidence));
            if (hits.Count >= limit)
                break;
        }

        return hits;
    }
}
