// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.Services.ContextFabric;

public static class FabricSchemaVersions
{
    public const string Corpus = "cf0-corpus-1.0";
    public const string EvidenceCard = "cf0-evidence-card-1.0";
    public const string Reduction = "cf0-reduction-1.0";
    public const string Answer = "cf0-answer-1.0";
    public const string Benchmark = "cf0-benchmark-1.0";
    public const string Stitch = "cf0-stitch-1.0";
    public const string QuoteDiagnostics = "cf0-quote-diagnostics-1.0";
    public const string StitchDiagnostics = "cf0-stitch-diagnostics-1.0";
    public const string ReaderPrompt = "cf0-reader-1.2";
    public const string ReducerPrompt = "cf0-reducer-1.0";
    public const string AnswerPrompt = "cf0-answer-1.2";
    public const string StitchPrompt = "cf0-stitch-1.0";
}

public sealed record FabricSegment(
    string SegmentId,
    int Ordinal,
    string Heading,
    string Text,
    string TextDigest,
    int EstimatedTokens);

public sealed record FabricCorpus(
    string CorpusId,
    string DocumentId,
    string GenerationId,
    string SourceDigest,
    string SchemaVersion,
    IReadOnlyList<FabricSegment> Segments,
    int EstimatedSourceTokens);

public sealed record FabricCitation
{
    public string SegmentId { get; init; } = "";
    public int CharStart { get; init; } = -1;
    public int CharEnd { get; init; } = -1;
    public string Quote { get; init; } = "";
    public string QuoteDigest { get; init; } = "";
}

public sealed record FabricClaim
{
    public string ClaimId { get; init; } = "";
    public string Type { get; init; } = "assertion";
    public string Text { get; init; } = "";
    public double Confidence { get; init; }
    public List<FabricCitation> Citations { get; init; } = [];
}

public sealed record FabricEvidenceCard
{
    public string SchemaVersion { get; init; } = FabricSchemaVersions.EvidenceCard;
    public string CorpusId { get; init; } = "";
    public string DocumentId { get; init; } = "";
    public string SegmentId { get; init; } = "";
    public string PromptVersion { get; init; } = FabricSchemaVersions.ReaderPrompt;
    public string Summary { get; init; } = "";
    public List<FabricClaim> Claims { get; init; } = [];
    public List<string> Entities { get; init; } = [];
    public List<string> Conflicts { get; init; } = [];
    public List<string> OpenQuestions { get; init; } = [];
}

public sealed record FabricReductionDraft
{
    public string SchemaVersion { get; init; } = FabricSchemaVersions.Reduction;
    public string Summary { get; init; } = "";
    public List<string> ClaimIds { get; init; } = [];
    public List<string> Conflicts { get; init; } = [];
}

public sealed record FabricReductionNode(
    string NodeId,
    string Level,
    IReadOnlyList<string> ChildIds,
    IReadOnlyList<string> CoveredSegmentIds,
    string Summary,
    IReadOnlyList<string> ClaimIds,
    IReadOnlyList<string> Conflicts,
    string CoverageDigest);

public enum FabricQuestionKind
{
    LocalFact,
    MultiHop,
    Contradiction,
    Exhaustive,
    Unanswerable,
}

public sealed record FabricBenchmarkQuestion(
    string QuestionId,
    FabricQuestionKind Kind,
    string Question,
    IReadOnlyList<string> ExpectedTerms,
    IReadOnlyList<string> ExpectedSegmentIds,
    bool ExpectAbstention = false);

public sealed record FabricAnswerClaim
{
    public string Text { get; init; } = "";
    public List<FabricCitation> Citations { get; init; } = [];
}

public sealed record FabricAnswerDraft
{
    public string SchemaVersion { get; init; } = FabricSchemaVersions.Answer;
    public string Answer { get; init; } = "";
    public bool Abstained { get; init; }
    public List<FabricAnswerClaim> Claims { get; init; } = [];
}

public sealed record FabricContextBudget(
    int ContextLimit = 8192,
    int ResponseReserve = 1536,
    int SystemReserve = 512)
{
    public int EvidenceLimit => ContextLimit - ResponseReserve - SystemReserve;

    public void Validate()
    {
        if (ContextLimit < 2048)
            throw new ArgumentOutOfRangeException(nameof(ContextLimit), "Context limit must be at least 2048 tokens.");
        if (ResponseReserve < 128)
            throw new ArgumentOutOfRangeException(nameof(ResponseReserve), "Response reserve must be at least 128 tokens.");
        if (SystemReserve < 128)
            throw new ArgumentOutOfRangeException(nameof(SystemReserve), "System reserve must be at least 128 tokens.");
        if (EvidenceLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(ContextLimit), "Context reserves leave no evidence budget.");
    }
}

public sealed record FabricRunOptions(
    FabricContextBudget ContextBudget,
    int ReaderMaxTokens = 1024,
    int ReducerMaxTokens = 768,
    int AnswerMaxTokens = 1536,
    int ReductionFanIn = 4,
    double Temperature = 0.0)
{
    public static FabricRunOptions Default { get; } = new(new FabricContextBudget());

    public void Validate()
    {
        ContextBudget.Validate();
        if (ReaderMaxTokens < 128)
            throw new ArgumentOutOfRangeException(nameof(ReaderMaxTokens), "Reader budget must be at least 128 tokens.");
        if (ReducerMaxTokens < 128)
            throw new ArgumentOutOfRangeException(nameof(ReducerMaxTokens), "Reducer budget must be at least 128 tokens.");
        if (AnswerMaxTokens < 128)
            throw new ArgumentOutOfRangeException(nameof(AnswerMaxTokens), "Answer budget must be at least 128 tokens.");
        if (ReductionFanIn < 2 || ReductionFanIn > 16)
            throw new ArgumentOutOfRangeException(nameof(ReductionFanIn), "Reduction fan-in must be between 2 and 16.");
        if (Temperature < 0 || Temperature > 2)
            throw new ArgumentOutOfRangeException(nameof(Temperature));
    }
}

public sealed record FabricCallMetrics(
    string Stage,
    string ItemId,
    RuntimeRole Role,
    int PromptTokens,
    int CompletionTokens,
    int ContextLimit,
    long DurationMs,
    bool Succeeded,
    string? Error = null,
    string? PromptPath = null,
    string? RawOutputExcerpt = null)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
    public bool FitsContext => TotalTokens <= ContextLimit;
}

public sealed record FabricSegmentRunResult(
    string SegmentId,
    bool Accepted,
    FabricEvidenceCard? Card,
    IReadOnlyList<string> Errors,
    FabricCallMetrics Metrics);

public sealed record FabricCorpusReadReport(
    string RuntimeName,
    string CorpusId,
    string DocumentId,
    DateTimeOffset GeneratedAt,
    FabricRunOptions Options,
    IReadOnlyList<FabricSegmentRunResult> SegmentResults,
    IReadOnlyList<FabricCallMetrics> Calls);

public sealed record FabricVerificationResult(
    bool Passed,
    double CitationPrecision,
    int ValidCitations,
    int TotalCitations,
    IReadOnlyList<string> VerifiedSegmentIds,
    IReadOnlyList<string> Errors);

public sealed record FabricQuestionRunResult(
    FabricBenchmarkQuestion Question,
    FabricAnswerDraft? Answer,
    FabricVerificationResult Verification,
    IReadOnlyList<string> IncludedSegmentIds,
    FabricCallMetrics Metrics);

public sealed record FabricGateResult(string Name, bool Passed, string Detail);

public sealed record FabricFeasibilitySummary(
    int ExpectedSegments,
    int AcceptedSegments,
    int TotalQuestions,
    int PassedQuestions,
    int ValidCitations,
    int TotalCitations,
    int EstimatedSourceTokens,
    int MaximumPromptTokens,
    double SourceToWorkingContextRatio,
    long DurationMs);

public sealed record FabricBenchmarkLane(
    string Role,
    string ModelDisplayName,
    string AssetId,
    ModelAdmissionVerdict AdmissionVerdict,
    string FamilyLabel,
    double? ParametersB,
    IReadOnlyList<string> Reasons);

public sealed record FabricBenchmarkEnvironment(
    IReadOnlyList<FabricBenchmarkLane> Lanes);

public sealed record FabricFeasibilityReport(
    string SchemaVersion,
    string RuntimeName,
    string CorpusId,
    string GenerationId,
    string SourceDigest,
    DateTimeOffset GeneratedUtc,
    FabricRunOptions Options,
    IReadOnlyList<FabricSegmentRunResult> SegmentResults,
    IReadOnlyList<FabricReductionNode> Reductions,
    IReadOnlyList<FabricQuestionRunResult> QuestionResults,
    IReadOnlyList<FabricCallMetrics> Calls,
    IReadOnlyList<FabricGateResult> Gates,
    FabricFeasibilitySummary Summary,
    FabricBenchmarkEnvironment? Environment = null)
{
    public bool Passed => Gates.Count > 0 && Gates.All(gate => gate.Passed);
}

public sealed record FabricEvidenceValidationResult(
    bool IsValid,
    FabricEvidenceCard? Card,
    IReadOnlyList<string> Errors);

public enum FabricAnchorMode
{
    None,
    Exact,
    NormalizedExact,
    SoftCandidate,
}

public sealed record FabricQuoteAnchorCase(
    string CaseId,
    string SegmentId,
    string CandidateQuote,
    FabricAnchorMode ExpectedMode,
    bool ExpectedAccepted,
    string Notes);

public sealed record FabricQuoteAnchorResult(
    string CaseId,
    string SegmentId,
    string CandidateQuote,
    FabricAnchorMode Mode,
    bool Accepted,
    int? CharStart,
    int? CharEnd,
    double TokenOverlap,
    IReadOnlyList<string> Errors);

public sealed record FabricQuoteAnchorReport(
    string SchemaVersion,
    string CorpusId,
    string GenerationId,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<FabricQuoteAnchorResult> Results);

public sealed record FabricBoundaryStitchCase(
    string CaseId,
    string LeftText,
    string RightText,
    string ExpectedSummary,
    IReadOnlyList<string> ExpectedLinkedFacts,
    IReadOnlyList<string> ForbiddenTerms);

public sealed record FabricBoundaryStitchFixture(
    string FixtureId,
    IReadOnlyList<FabricBoundaryStitchCase> Cases);

public sealed record FabricBoundaryStitchDraft
{
    public string SchemaVersion { get; init; } = FabricSchemaVersions.Stitch;
    public string CaseId { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> LinkedFacts { get; init; } = [];
}

public sealed record FabricBoundaryStitchResult(
    string CaseId,
    bool Passed,
    string Summary,
    IReadOnlyList<string> LinkedFacts,
    IReadOnlyList<string> Errors,
    FabricCallMetrics Metrics);

public sealed record FabricBoundaryStitchReport(
    string SchemaVersion,
    string RuntimeName,
    string FixtureId,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<FabricBoundaryStitchResult> Results,
    IReadOnlyList<FabricCallMetrics> Calls);

public sealed class FabricContextBudgetExceededException(string message) : InvalidOperationException(message);
