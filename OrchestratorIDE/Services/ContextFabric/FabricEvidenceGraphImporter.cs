// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricEvidenceGraphImporter(
    FabricLibraryRepository libraryRepository,
    DocumentGraphRepository graphRepository)
{
    public int ImportEvidenceCard(
        FabricEvidenceCard card,
        string verificationStatus = FabricVerificationStatus.Provisional)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (string.IsNullOrWhiteSpace(verificationStatus))
            throw new ArgumentException("Verification status is required.", nameof(verificationStatus));

        var document = libraryRepository.GetDocument(card.DocumentId)
            ?? throw new KeyNotFoundException($"Context Fabric document '{card.DocumentId}' does not exist.");
        var segment = libraryRepository.GetSegment(card.SegmentId)
            ?? throw new KeyNotFoundException($"Context Fabric segment '{card.SegmentId}' does not exist.");
        if (!segment.DocumentId.Equals(document.DocumentId, StringComparison.Ordinal))
            throw new InvalidDataException($"Segment '{segment.SegmentId}' does not belong to document '{document.DocumentId}'.");
        if (!card.SegmentId.Equals(segment.SegmentId, StringComparison.Ordinal) ||
            !card.DocumentId.Equals(document.DocumentId, StringComparison.Ordinal))
            throw new InvalidDataException("Evidence card document identity does not match the repository state.");

        var now = DateTimeOffset.UtcNow;
        var imported = 0;
        foreach (var claim in card.Claims)
        {
            if (claim is null) continue;

            var entry = new FabricClaimEntry(
                claim.ClaimId,
                document.CorpusId,
                document.DocumentId,
                segment.SegmentId,
                string.IsNullOrWhiteSpace(claim.Type) ? "assertion" : claim.Type,
                claim.Text,
                verificationStatus,
                claim.Confidence,
                now,
                now);

            var citations = (claim.Citations ?? [])
                .Where(citation => citation is not null)
                .Select((citation, index) => new FabricClaimCitationEntry(
                    claim.ClaimId,
                    index,
                    string.IsNullOrWhiteSpace(citation.SegmentId) ? segment.SegmentId : citation.SegmentId,
                    citation.CharStart,
                    citation.CharEnd,
                    citation.QuoteDigest,
                    citation.Quote))
                .ToArray();

            graphRepository.UpsertClaim(entry, citations);
            imported++;
        }

        foreach (var entity in card.Entities
                     .Where(item => !string.IsNullOrWhiteSpace(item))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var canonical = entity.Trim();
            var entityId = $"entity-{FabricHashing.Sha256($"{document.CorpusId}|{canonical.ToLowerInvariant()}")[..24]}";
            graphRepository.UpsertEntity(new FabricEntityEntry(
                entityId,
                document.CorpusId,
                canonical,
                null,
                verificationStatus,
                null,
                now,
                now));
        }

        return imported;
    }
}
