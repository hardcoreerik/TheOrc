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
    // 1.1: added FabricGateResult.IsBlocking, FabricBenchmarkMetric.IsBlocking,
    // FabricBenchmarkSystemGate.PassedCount/TotalCount, the "Graded capability" gate,
    // mean_citation_precision metric, and EvidenceBudgetStats (Remediation Phase 2 --
    // see docs/CONTEXT_FABRIC_GRADING_SPEC.md §8).
    public const string BenchmarkGate = "cf7-benchmark-gate-1.1";
    public const string Baseline = "cf7-baseline-1.0";
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
    public string SourceLabel { get; init; } = "";
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
    Paraphrased,
    MultiHop,
    GlobalSynthesis,
    Contradiction,
    Exhaustive,
    Unanswerable,
}

/// <summary>
/// ExhaustiveIsEntityScopedOverride is an optional, authored ground-truth annotation for
/// FabricQuestionKind.Exhaustive questions only (Remediation Phase 3, review item #4 --
/// "pre-compute the ground-truth classification... so the benchmark no longer relies on the
/// runtime heuristic"). When set, BuildExhaustiveAnswer
/// (ContextFabricFeasibilityRunner.cs) uses it directly instead of inferring entity-scoped
/// vs. category-wide from document frequency -- see
/// docs/CONTEXT_FABRIC_GRADING_SPEC.md §5.3 for the heuristic's known boundary-case risk this
/// exists to close. Null (the default, and every question in the current 150-question suite)
/// preserves existing behavior exactly -- Tier 1c's hyphenated-identifier anchor match already
/// covers every current Exhaustive question, so the heuristic's fallback path isn't actually
/// exercised by the live corpus today. Ignored for non-Exhaustive questions.
/// </summary>
public sealed record FabricBenchmarkQuestion(
    string QuestionId,
    FabricQuestionKind Kind,
    string Question,
    IReadOnlyList<string> ExpectedTerms,
    IReadOnlyList<string> ExpectedSegmentIds,
    bool ExpectAbstention = false,
    bool? ExhaustiveIsEntityScopedOverride = null);

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
    // ContextManager.EstimateTokens is a crude chars/4 heuristic that under-counts
    // token-dense, JSON-structured prompts. This margin is applied to the budget gate in
    // every CF inference call so an over-estimated prompt can't silently overflow the native
    // KV pool (the root-cause of NoKvSlot crashes on CF runs; see CONTEXT_FABRIC_BUG_HISTORY.md §7).
    // Single source of truth: referenced from ContextFabricFeasibilityRunner, FabricBoundaryStitcher,
    // and EvidencePackBuilder. Kept in FabricContextBudget so it compiles into OrchestratorIDE.NativeRuntime
    // (which links ContextFabricContracts.cs but not EvidencePackBuilder.cs).
    public const double TokenSafetyMargin = 1.15;

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
    // Observed live on the real Darwin PDF corpus: with genuine evidence in the prompt (as
    // opposed to an empty-evidence abstain, which needs very few tokens), the Reviewer's answer
    // -- prose explanation plus one citation object per claim -- routinely ran past 1536 tokens
    // and got cut off mid-string, producing invalid JSON ("Answer schema parse failed"). Keep in
    // sync with FabricQueryPlannerOptions.ResponseTokenReserve (EvidencePackBuilder's assumed
    // response headroom) -- that value must stay >= this one or the evidence packer under-reserves
    // room for the response the model is actually allowed to generate.
    int AnswerMaxTokens = 2048,
    int ReductionFanIn = 4,
    double Temperature = 0.0,
    // The frozen fixture marks every scored fact with a literal "EVIDENCE:" line, and the reader
    // is told to emit exactly one claim per marked line and nothing else. That checklist has no
    // equivalent in an ordinary, un-marked corpus (DeterministicExpandedFabricCorpus) -- followed
    // literally there, it would instruct the model to extract zero claims from every segment.
    // When true, the reader is instead told to find and cite every distinct factual claim it can
    // in ordinary prose, with no predefined line list to satisfy or fail against.
    bool OpenExtractionReading = false)
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

/// <summary>
/// IsBlocking defaults true so every existing gate keeps blocking overall Passed/
/// ReadyForExpansion status unless explicitly marked otherwise. Currently only
/// "all-questions-verified" (CF0, ContextFabricFeasibilityRunner.BuildGates) and the
/// question_pass_rate-derived portion of "Metric thresholds passed" (CF7,
/// ContextFabricBenchmarkGateEvaluator) are non-blocking -- literal 100% question pass
/// rate is kept as a reported stretch goal, not a release blocker, per
/// docs/CONTEXT_FABRIC_GRADING_SPEC.md §8/§9 (Remediation Phase 2).
/// </summary>
public sealed record FabricGateResult(string Name, bool Passed, string Detail, bool IsBlocking = true);

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
    public bool Passed => Gates.Count > 0 && Gates.Where(gate => gate.IsBlocking).All(gate => gate.Passed);
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

public enum FabricBenchmarkSystemStatus
{
    Passed,
    Failed,
    Missing,
}

/// <summary>
/// PassedCount/TotalCount are null when a system's per-question score isn't comparable --
/// B4 (a frozen, structurally-different acceptance artifact; see
/// docs/CONTEXT_FABRIC_GRADING_SPEC.md §2.1) never sets them. Populated for B0/B1/B2 by
/// ContextFabricBaselineRunner.ToSystemGate and for B3 by
/// ContextFabricBenchmarkGateEvaluator.BuildSystemGate, both counting the SAME question set
/// within one gate-expanded run, so PassedCount is directly comparable across systems without
/// normalization.
/// </summary>
public sealed record FabricBenchmarkSystemGate(
    string SystemId,
    string Label,
    FabricBenchmarkSystemStatus Status,
    string Detail,
    int? PassedCount = null,
    int? TotalCount = null);

/// <summary>See FabricGateResult's IsBlocking doc -- same rationale, same default.</summary>
public sealed record FabricBenchmarkMetric(
    string Name,
    double Value,
    double Target,
    bool Passed,
    string Detail,
    bool IsBlocking = true);

/// <summary>
/// Per-question-category evidence-budget consumption (Remediation Phase 2, review item #9 --
/// "add evidence budget consumption statistics so we can see when global-synthesis questions
/// are budget-constrained"). P50/P95 are computed over each category's B3 prompt-token counts
/// (FabricQuestionRunResult.Metrics.PromptTokens), nearest-rank method.
/// </summary>
public sealed record FabricEvidenceBudgetStat(
    string Category,
    int QuestionCount,
    int P50PromptTokens,
    int P95PromptTokens,
    int MaxPromptTokens);

public sealed record FabricCf7BenchmarkGateReport(
    string SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string CorpusId,
    string GenerationId,
    string SourceDigest,
    IReadOnlyList<FabricBenchmarkSystemGate> Systems,
    IReadOnlyList<FabricBenchmarkMetric> Metrics,
    IReadOnlyList<FabricGateResult> Gates,
    IReadOnlyList<FabricEvidenceBudgetStat> EvidenceBudget)
{
    public bool ReadyForExpansion => Gates.Count > 0 && Gates.Where(gate => gate.IsBlocking).All(gate => gate.Passed);
}

public sealed class FabricContextBudgetExceededException(string message) : InvalidOperationException(message);

// ── CF-6: exhaustive-query contracts ──────────────────────────────────────────

/// <summary>Input artifact for an exhaustive-query work unit: the question the worker must answer using
/// only its assigned source segment.</summary>
public sealed record FabricQueryQuestion(string QuestionId, string QuestionText);

/// <summary>Draft output from an exhaustive-query worker: per-segment answer fragment.</summary>
public sealed class FabricQueryFindingDraft
{
    public bool Relevant { get; init; } = false;
    public string? FindingText { get; init; }
    public List<FabricClaim> Claims { get; init; } = [];
}

/// <summary>Validated per-segment exhaustive-query finding after schema check.</summary>
public sealed record FabricQueryFinding(
    string QuestionId,
    string SegmentId,
    bool Relevant,
    string? FindingText,
    IReadOnlyList<FabricClaim> Claims,
    FabricCallMetrics Metrics);

// ── CF-6: citation-verifier contract ─────────────────────────────────────────

/// <summary>Per-claim result from the HIVE citation verifier work unit.</summary>
public sealed record FabricHiveVerificationItem(
    string ClaimId,
    string SegmentId,
    bool Passed,
    IReadOnlyList<string> Errors);

/// <summary>Output artifact from a HIVE verifier work unit.</summary>
public sealed record FabricHiveVerificationReport(
    string CorpusId,
    string DocumentId,
    string SegmentId,
    bool AllPassed,
    IReadOnlyList<FabricHiveVerificationItem> Items);
