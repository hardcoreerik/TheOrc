namespace OrchestratorIDE.Services.Data;

/// <summary>
/// A staged boss-plan capture row (mirrors DatasetCapture's plan_capture_*.json).
/// <c>PlanJson</c>/<c>RubricJson</c> hold the un-queried sub-objects verbatim.
/// </summary>
public sealed record CaptureRecord(
    string  ExampleId,
    string  RunId,
    string  CapturedAt,
    string  Source,
    string  BossModel,
    string  Goal,
    string? Domain,
    int?    Difficulty,
    int     QualityScore,
    string? ExampleClass,
    string? FailureMode,
    string? PlanJson,
    string? RubricJson,
    string  Annotator,
    string  Notes,
    string  SourceFile);

/// <summary>
/// A judge-triage row (mirrors one line of a batch_*_triage.tsv).
/// Review state (<c>pending|approved|rejected</c>) is human-owned and lives only in the
/// DB — it is NOT part of the source file and is preserved across re-imports.
/// </summary>
public sealed record TriageRecord(
    string  CaptureRef,
    string  BatchId,
    string  Risk,        // normalized upper-case: HIGH | MEDIUM | LOW
    int?    Score,
    string? Rationale,
    string  SourceFile);
