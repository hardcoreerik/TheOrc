// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
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

/// <summary>
/// A Pit Boss training plan row (mirrors Models/TrainingPlan.cs).
/// Upsert key: <c>plan_id</c>. File at training_pit/plans/plan_{PlanId}.json stays
/// canonical during transition; SQL is the queryable index and live execution state.
/// </summary>
public sealed record PlanRecord(
    string  PlanId,
    string  CreatedAt,
    string  Goal,
    string  Persona,
    string  Style,
    string? LanguagesJson,   // JSON array: ["csharp","python"]
    string? TaskMixJson,     // JSON object: {"code_review":0.6}
    int     DatasetTarget,
    string  DatasetSource,
    string  BaseModel,
    string  AdapterName,
    int     LoraRank,
    int     Epochs,
    double  LearningRate,
    string  Phase,           // PlanPhase enum name
    string  DatasetFile,
    string  AdapterPath,
    string? HiveJson,        // HiveStrategy blob (nullable)
    string  Notes);

/// <summary>
/// A training-run history row. One row per execution of dataset_gen, forge_train, or eval.
/// Written at run start (status="running"), updated to "complete"/"failed"/"cancelled" at end.
/// </summary>
public sealed record RunRecord(
    string  RunId,
    string? PlanId,
    string  Kind,            // dataset_gen | forge_train | eval
    string  Status,          // running | complete | failed | cancelled | stale
    string  StartedAt,
    string? EndedAt,
    string  Host,            // Environment.MachineName
    string? ArtifactPath,    // dataset/adapter path produced
    string? MetricsJson,
    string? LogPath);

/// <summary>
/// A datasets index row (Phase 3). Cache over training_pit/datasets/*.jsonl.
/// Mirrors TrainingPitRegistry.DatasetInfo field-for-field. Files stay canonical.
/// Upsert key: <c>file_path</c>. Re-index on every LoadDatasets scan (dual-write).
/// </summary>
public sealed record DatasetRecord(
    string FilePath,
    string Name,
    string Source,
    string Context,
    string DataType,
    string Role,
    bool   IsNewConvention,
    bool   InProgress,
    int    TrainCount,
    int    EvalCount,
    int    TotalCount,
    string LastModified,   // ISO-8601 UTC
    string IndexedAt);     // ISO-8601 UTC — when this row was last refreshed
