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
        var hits = CollectHits(query, corpusId, limit, looseMatch: false);
        // The strict pass ANDs every token, which a realistic multi-word natural-language
        // question (CF-5's real input shape, as opposed to CF-1/CF-2's short keyword-style test
        // queries) will essentially never satisfy in a single segment/claim. Fall back to an
        // OR-joined, bm25-ranked pass only when the strict pass truly found nothing, so existing
        // exact-match test expectations (including the deliberate "lexical misses, claim-expand
        // catches it" cases) are unaffected.
        if (hits.Count == 0)
            hits = CollectHits(query, corpusId, limit, looseMatch: true);
        return hits;
    }

    private List<FabricRetrievalHit> CollectHits(string query, string? corpusId, int limit, bool looseMatch)
    {
        var hits = new List<FabricRetrievalHit>(limit);
        var seenSegments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segmentHit in libraryRepository.Search(query, corpusId, limit, looseMatch))
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

        foreach (var claimHit in graphRepository.SearchClaims(query, corpusId, limit, looseMatch))
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
