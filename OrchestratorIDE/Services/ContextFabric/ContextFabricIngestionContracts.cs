// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public static class FabricIngestionVersions
{
    public const string TextMarkdownParser = "fabric-text-markdown-1.0";
    public const string PdfTextParser = "fabric-pdf-text-1.0";
    public const string Segmenter = "fabric-segmenter-1.0";
}

public static class FabricVerificationStatus
{
    public const string Provisional = "provisional";
    public const string Verified = "verified";
    public const string Rejected = "rejected";
}

public sealed record FabricParsedBlock(
    int CharStart,
    int CharEnd,
    string? HeadingPath,
    string Text);

public sealed record FabricParsedDocument(
    string ParserId,
    string ParserVersion,
    string MediaType,
    string NormalizedText,
    IReadOnlyList<FabricParsedBlock> Blocks,
    IReadOnlyList<string> Warnings);

public sealed record FabricSegmentDraft(
    string SegmentId,
    int Ordinal,
    string? HeadingPath,
    int CharStart,
    int CharEnd,
    int TokenCount,
    string TextDigest,
    string Text,
    string? PreviousSegmentId,
    string? NextSegmentId,
    string ChunkerVersion);

public sealed record FabricCorpusEntry(
    string CorpusId,
    string Name,
    string? Description,
    string PolicyProfile,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FabricDocumentEntry(
    string DocumentId,
    string CorpusId,
    string SourceDigest,
    string NormalizedDigest,
    string DisplayName,
    string MediaType,
    string ParserId,
    string ParserVersion,
    string Status,
    IReadOnlyList<string> Warnings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FabricSegmentEntry(
    string SegmentId,
    string DocumentId,
    int Ordinal,
    string? HeadingPath,
    int CharStart,
    int CharEnd,
    int TokenCount,
    string TextDigest,
    string Text,
    string? PreviousSegmentId,
    string? NextSegmentId,
    string ChunkerVersion);

public sealed record FabricImportResult(
    FabricDocumentEntry Document,
    IReadOnlyList<FabricSegmentEntry> Segments,
    bool Rebuilt);

public sealed record FabricSearchHit(
    string CorpusId,
    string DocumentId,
    string DisplayName,
    string SegmentId,
    int Ordinal,
    string? HeadingPath,
    string Text,
    double Rank);

public sealed record FabricClaimEntry(
    string ClaimId,
    string CorpusId,
    string DocumentId,
    string SegmentId,
    string ClaimType,
    string ClaimText,
    string VerificationStatus,
    double? Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FabricClaimCitationEntry(
    string ClaimId,
    int Ordinal,
    string SegmentId,
    int CharStart,
    int CharEnd,
    string QuoteDigest,
    string QuoteText);

public sealed record FabricEntityEntry(
    string EntityId,
    string CorpusId,
    string CanonicalName,
    string? EntityType,
    string VerificationStatus,
    double? Confidence,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FabricRelationEntry(
    string RelationId,
    string CorpusId,
    string SourceEntityId,
    string TargetEntityId,
    string RelationType,
    string VerificationStatus,
    double? Confidence,
    int EvidenceCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FabricClaimSearchHit(
    string ClaimId,
    string CorpusId,
    string DocumentId,
    string SegmentId,
    string DisplayName,
    string ClaimType,
    string ClaimText,
    string VerificationStatus,
    double? Confidence,
    double Rank);

public sealed record FabricRetrievalHit(
    string CorpusId,
    string DocumentId,
    string DisplayName,
    string SegmentId,
    int Ordinal,
    string? HeadingPath,
    string Text,
    string RetrievalPath,
    string? ClaimId,
    string? ClaimText,
    string? VerificationStatus);

public sealed record FabricSegmenterOptions(
    int TargetTokens = 2_000,
    int MaximumTokens = 3_000,
    int OverlapTokens = 256)
{
    public void Validate()
    {
        if (TargetTokens < 64)
            throw new ArgumentOutOfRangeException(nameof(TargetTokens));
        if (MaximumTokens < TargetTokens)
            throw new ArgumentOutOfRangeException(nameof(MaximumTokens));
        if (OverlapTokens < 0 || OverlapTokens >= TargetTokens)
            throw new ArgumentOutOfRangeException(nameof(OverlapTokens));
    }
}

public sealed record FabricLibraryOptions(
    long MaximumSourceBytes = 32L * 1024 * 1024,
    FabricSegmenterOptions? Segmenter = null)
{
    public FabricSegmenterOptions EffectiveSegmenter => Segmenter ?? new FabricSegmenterOptions();

    public void Validate()
    {
        if (MaximumSourceBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumSourceBytes));
        EffectiveSegmenter.Validate();
    }
}
