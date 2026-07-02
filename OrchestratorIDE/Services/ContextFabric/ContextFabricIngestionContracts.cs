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

public static class FabricCoverageStatus
{
    public const string Complete = "complete";
    public const string Incomplete = "incomplete";
}

public static class FabricQueryMode
{
    public const string Quick = "quick";
    public const string Study = "study";
}

public static class FabricCitationVerificationLabel
{
    public const string Supported = "supported";
    public const string PartiallySupported = "partially_supported";
    public const string Contradicted = "contradicted";
    public const string CitationMismatch = "citation_mismatch";
    public const string Interpretive = "interpretive";
    public const string Unverifiable = "unverifiable";
}

public sealed record FabricParsedBlock(
    int CharStart,
    int CharEnd,
    string? HeadingPath,
    string Text,
    string BlockKind = "text",
    int? PageNumber = null,
    string? SourceLocator = null,
    double? Confidence = null);

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
    string ChunkerVersion,
    string BlockKind = "text",
    int? PageNumber = null,
    string? SourceLocator = null,
    double? Confidence = null);

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
    string ChunkerVersion,
    string BlockKind = "text",
    int? PageNumber = null,
    string? SourceLocator = null,
    double? Confidence = null);

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
    double Rank,
    string BlockKind = "text",
    int? PageNumber = null,
    string? SourceLocator = null,
    double? Confidence = null);

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
    DateTimeOffset UpdatedAt,
    /// <summary>CF-6: corpus generation that produced this claim. Null for claims from single-node
    /// (non-HIVE) reads. Enables the generation-safe HIVE importer to identify and replace stale-
    /// generation claims when a corpus is re-indexed in a new generation.</summary>
    string? GenerationId = null);

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
    string? VerificationStatus,
    string BlockKind = "text",
    int? PageNumber = null,
    string? SourceLocator = null,
    double? Confidence = null);

public sealed record FabricMemoryNodeEntry(
    string NodeId,
    string CorpusId,
    string DocumentId,
    string NodeType,
    string Title,
    string SummaryText,
    int Generation,
    int FanIn,
    int ExpectedChildCount,
    int CoveredChildCount,
    string CoverageStatus,
    string ReducerVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FabricMemoryMembershipEntry(
    string ParentNodeId,
    string ChildKind,
    string ChildId,
    int Ordinal,
    bool IsCovered);

public sealed record FabricReducerOptions(
    int FanIn = 4,
    int MaxSummaryChars = 320)
{
    public void Validate()
    {
        if (FanIn < 2 || FanIn > 16)
            throw new ArgumentOutOfRangeException(nameof(FanIn));
        if (MaxSummaryChars < 64)
            throw new ArgumentOutOfRangeException(nameof(MaxSummaryChars));
    }
}

public sealed record FabricReductionResult(
    string DocumentId,
    IReadOnlyList<FabricMemoryNodeEntry> Nodes,
    IReadOnlyList<FabricMemoryMembershipEntry> Memberships,
    string RootNodeId);

public sealed record FabricQueryPlannerOptions(
    int RetrievalLimit = 8,
    int MaxRounds = 2,
    int MaxSourceOpens = 6,
    int MaxPromptTokens = 8_192,
    int ResponseTokenReserve = 1_024)
{
    public void Validate()
    {
        if (RetrievalLimit < 1 || RetrievalLimit > 64)
            throw new ArgumentOutOfRangeException(nameof(RetrievalLimit));
        if (MaxRounds < 1 || MaxRounds > 8)
            throw new ArgumentOutOfRangeException(nameof(MaxRounds));
        if (MaxSourceOpens < 1 || MaxSourceOpens > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxSourceOpens));
        if (ResponseTokenReserve < 128 || ResponseTokenReserve >= MaxPromptTokens)
            throw new ArgumentOutOfRangeException(nameof(ResponseTokenReserve));
    }
}

public sealed record FabricQueryPlan(
    string Query,
    string CorpusId,
    string Mode,
    int MaxRounds,
    int MaxSourceOpens,
    int MaxPromptTokens,
    int ResponseTokenReserve,
    IReadOnlyList<FabricRetrievalHit> SeedHits,
    IReadOnlyList<string> SummaryNodeIds,
    IReadOnlyList<string> ReopenedSegmentIds,
    bool TriggeredSourceReopen);

public sealed record FabricEvidenceItem(
    string Kind,
    string Id,
    string Text,
    int TokenCount,
    bool FromSource,
    string Provenance);

public sealed record FabricEvidencePack(
    string Query,
    string Mode,
    int PromptTokenBudget,
    int ResponseTokenReserve,
    int UsedPromptTokens,
    IReadOnlyList<FabricEvidenceItem> Included,
    IReadOnlyList<FabricEvidenceItem> Excluded,
    bool WithinBudget,
    bool TriggeredSourceReopen);

public sealed record FabricCitationVerificationItem(
    string SegmentId,
    string Label,
    string QuoteText,
    int CharStart,
    int CharEnd,
    string Reason);

public sealed record FabricCitationVerificationResult(
    string ClaimText,
    string Label,
    IReadOnlyList<FabricCitationVerificationItem> Items,
    bool Repaired,
    IReadOnlyList<FabricCitation> EffectiveCitations);

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
