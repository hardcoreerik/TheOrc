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
        var imports = BuildClaimImports(card, verificationStatus);
        foreach (var import in imports)
            graphRepository.UpsertClaim(import.Claim, import.Citations);

        if (imports.Count > 0)
            UpsertEntities(imports[0].Document, verificationStatus, imports[0].Card.Entities);
        return imports.Count;
    }

    /// <summary>
    /// Replaces one segment's claims with the given card's claims, leaving every other
    /// segment's claims in the document untouched -- unlike ImportEvidenceCard (which only
    /// upserts what's in the card and never removes a claim that dropped out between reads)
    /// or ReplaceDocumentEvidenceCards (which wipes the whole document). Used for scoped
    /// re-reads (see FabricNativeReaderService.ReadSegmentsAsync) so a retry that returns
    /// fewer claims than the previous read doesn't leave the old ones orphaned in the graph.
    /// </summary>
    /// <summary>
    /// CF-6 overload: replaces one segment's claims tagged with the given <paramref name="generationId"/>.
    /// Used by <see cref="FabricHiveCampaignImporter"/> so claims are traceable to a specific corpus
    /// generation; a re-index (new generationId) produces distinct rows that can be swept independently.
    /// </summary>
    public int ReplaceSegmentEvidenceCard(
        FabricEvidenceCard card,
        string verificationStatus,
        string? generationId)
    {
        ArgumentNullException.ThrowIfNull(card);
        var imports = BuildClaimImports(card, verificationStatus, generationId);

        graphRepository.ReplaceClaimsForSegment(
            card.DocumentId,
            card.SegmentId,
            imports.Select(item => item.Claim).ToArray(),
            imports.ToDictionary(
                item => item.Claim.ClaimId,
                item => (IReadOnlyList<FabricClaimCitationEntry>)item.Citations,
                StringComparer.Ordinal));

        if (imports.Count > 0)
            UpsertEntities(imports[0].Document, verificationStatus, imports[0].Card.Entities);
        return imports.Count;
    }

    public int ReplaceSegmentEvidenceCard(
        FabricEvidenceCard card,
        string verificationStatus = FabricVerificationStatus.Provisional)
    {
        ArgumentNullException.ThrowIfNull(card);
        var imports = BuildClaimImports(card, verificationStatus);

        graphRepository.ReplaceClaimsForSegment(
            card.DocumentId,
            card.SegmentId,
            imports.Select(item => item.Claim).ToArray(),
            imports.ToDictionary(
                item => item.Claim.ClaimId,
                item => (IReadOnlyList<FabricClaimCitationEntry>)item.Citations,
                StringComparer.Ordinal));

        if (imports.Count > 0)
            UpsertEntities(imports[0].Document, verificationStatus, imports[0].Card.Entities);
        return imports.Count;
    }

    public int ReplaceDocumentEvidenceCards(
        string documentId,
        IEnumerable<FabricEvidenceCard> cards,
        string verificationStatus = FabricVerificationStatus.Provisional)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Document id is required.", nameof(documentId));
        ArgumentNullException.ThrowIfNull(cards);

        var imports = cards
            .SelectMany(card => BuildClaimImports(card, verificationStatus))
            .ToArray();
        var document = imports.Length == 0 ? null : imports[0].Document;
        if (imports.Any(import => !string.Equals(import.Document.DocumentId, documentId, StringComparison.Ordinal) ||
                                  !string.Equals(import.Claim.DocumentId, documentId, StringComparison.Ordinal)))
            throw new InvalidDataException($"Evidence cards do not belong to document '{documentId}'.");

        graphRepository.ReplaceClaimsForDocument(
            documentId,
            imports.Select(item => item.Claim).ToArray(),
            imports.ToDictionary(
                item => item.Claim.ClaimId,
                item => (IReadOnlyList<FabricClaimCitationEntry>)item.Citations,
                StringComparer.Ordinal));

        if (document is not null)
            UpsertEntities(document, verificationStatus, imports.SelectMany(item => item.Card.Entities).ToArray());

        return imports.Length;
    }

    private IReadOnlyList<ClaimImport> BuildClaimImports(
        FabricEvidenceCard card,
        string verificationStatus,
        string? generationId = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (string.IsNullOrWhiteSpace(verificationStatus))
            throw new ArgumentException("Verification status is required.", nameof(verificationStatus));

        var document = libraryRepository.GetDocument(card.DocumentId)
            ?? throw new KeyNotFoundException($"Context Fabric document '{card.DocumentId}' does not exist.");
        var segment = libraryRepository.GetSegment(card.SegmentId)
            ?? throw new KeyNotFoundException($"Context Fabric segment '{card.SegmentId}' does not exist.");
        if (!card.CorpusId.Equals(document.CorpusId, StringComparison.Ordinal))
            throw new InvalidDataException("Evidence card corpus identity does not match the repository state.");
        if (!segment.DocumentId.Equals(document.DocumentId, StringComparison.Ordinal))
            throw new InvalidDataException($"Segment '{segment.SegmentId}' does not belong to document '{document.DocumentId}'.");
        if (!card.SegmentId.Equals(segment.SegmentId, StringComparison.Ordinal) ||
            !card.DocumentId.Equals(document.DocumentId, StringComparison.Ordinal))
            throw new InvalidDataException("Evidence card document identity does not match the repository state.");

        var now = DateTimeOffset.UtcNow;
        var imports = new List<ClaimImport>(card.Claims.Count);
        foreach (var (claim, claimIndex) in card.Claims.Select((item, index) => (item, index)))
        {
            if (claim is null) continue;
            var claimId = BuildScopedClaimId(
                document.CorpusId,
                document.DocumentId,
                segment.SegmentId,
                BuildLocalClaimId(claim, claimIndex));

            var entry = new FabricClaimEntry(
                claimId,
                document.CorpusId,
                document.DocumentId,
                segment.SegmentId,
                string.IsNullOrWhiteSpace(claim.Type) ? "assertion" : claim.Type,
                claim.Text,
                verificationStatus,
                claim.Confidence,
                now,
                now,
                GenerationId: generationId);

            var citations = (claim.Citations ?? [])
                .Where(citation => citation is not null)
                .Select((citation, index) => new FabricClaimCitationEntry(
                    claimId,
                    index,
                    ResolveCitationSegmentId(document.DocumentId, segment.SegmentId, citation),
                    citation.CharStart,
                    citation.CharEnd,
                    citation.QuoteDigest,
                    citation.Quote))
                .ToArray();
            imports.Add(new ClaimImport(document, card, entry, citations));
        }

        return imports;
    }

    private void UpsertEntities(
        FabricDocumentEntry document,
        string verificationStatus,
        IEnumerable<string> entities)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entity in entities
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
    }

    private string ResolveCitationSegmentId(string documentId, string defaultSegmentId, FabricCitation citation)
    {
        if (string.IsNullOrWhiteSpace(citation.SegmentId))
            return defaultSegmentId;

        var segment = libraryRepository.GetSegment(citation.SegmentId)
            ?? throw new KeyNotFoundException($"Context Fabric segment '{citation.SegmentId}' does not exist.");
        if (!string.Equals(segment.DocumentId, documentId, StringComparison.Ordinal))
            throw new InvalidDataException($"Citation segment '{citation.SegmentId}' does not belong to document '{documentId}'.");
        return segment.SegmentId;
    }

    private static string BuildLocalClaimId(FabricClaim claim, int claimIndex) =>
        string.IsNullOrWhiteSpace(claim.ClaimId)
            ? $"claim-{claimIndex + 1}-{FabricHashing.Sha256(claim.Text ?? "")[..12]}"
            : claim.ClaimId.Trim();

    private static string BuildScopedClaimId(string corpusId, string documentId, string segmentId, string claimId) =>
        $"claim-{FabricHashing.Sha256($"{corpusId}|{documentId}|{segmentId}|{claimId}")[..24]}";

    private sealed record ClaimImport(
        FabricDocumentEntry Document,
        FabricEvidenceCard Card,
        FabricClaimEntry Claim,
        IReadOnlyList<FabricClaimCitationEntry> Citations);
}
