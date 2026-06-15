// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Read/write access to the <c>triage</c> table. All SQL parameterized via RepositoryBase.
///
/// Re-import safety: <see cref="Upsert"/> updates only judge-derived fields
/// (risk/score/rationale). It deliberately leaves <c>review_state</c>, <c>reviewed_by</c>,
/// and <c>reviewed_at</c> untouched so re-running the importer never erases a human's
/// approve/reject decision.
/// </summary>
public sealed class TriageRepository : RepositoryBase
{
    public TriageRepository(SqliteStore store) : base(store) { }

    /// <summary>Idempotent upsert keyed on (batch_id, capture_ref). Preserves review state.</summary>
    public void Upsert(TriageRecord t)
    {
        Execute(
            """
            INSERT INTO triage
                (capture_ref, batch_id, risk, score, rationale, review_state,
                 source_file, imported_at)
            VALUES
                ($capture_ref, $batch_id, $risk, $score, $rationale, 'pending',
                 $source_file, $imported_at)
            ON CONFLICT(batch_id, capture_ref) DO UPDATE SET
                risk        = excluded.risk,
                score       = excluded.score,
                rationale   = excluded.rationale,
                source_file = excluded.source_file,
                imported_at = excluded.imported_at
            """,
            ps =>
            {
                P(ps, "$capture_ref", t.CaptureRef);
                P(ps, "$batch_id",    t.BatchId);
                P(ps, "$risk",        t.Risk);
                P(ps, "$score",       t.Score);
                P(ps, "$rationale",   t.Rationale);
                P(ps, "$source_file", t.SourceFile);
                P(ps, "$imported_at", DateTime.UtcNow.ToString("o"));
            });
    }

    /// <summary>Records a human review decision (approve/reject) for one triage row.</summary>
    public void SetReviewState(string batchId, string captureRef, string state, string reviewer)
        => Execute(
            """
            UPDATE triage
            SET review_state = $state, reviewed_by = $by, reviewed_at = $at
            WHERE batch_id = $batch AND capture_ref = $ref
            """,
            ps =>
            {
                P(ps, "$state", state);
                P(ps, "$by",    reviewer);
                P(ps, "$at",    DateTime.UtcNow.ToString("o"));
                P(ps, "$batch", batchId);
                P(ps, "$ref",   captureRef);
            });

    /// <summary>The morning-after query: all unreviewed rows at a given risk, highest first.</summary>
    public List<TriageRow> PendingByRisk(string risk)
        => Query(
            """
            SELECT capture_ref, batch_id, risk, score, rationale, review_state, source_file
            FROM triage
            WHERE review_state = 'pending' AND risk = $risk
            ORDER BY score DESC
            """,
            Map,
            ps => P(ps, "$risk", risk));

    /// <summary>Count of pending rows, optionally filtered by risk.</summary>
    public int CountPending(string? risk = null)
        => risk is null
            ? Convert.ToInt32(Scalar("SELECT COUNT(*) FROM triage WHERE review_state = 'pending'") ?? 0)
            : Convert.ToInt32(
                Scalar("SELECT COUNT(*) FROM triage WHERE review_state = 'pending' AND risk = $r",
                    ps => P(ps, "$r", risk)) ?? 0);

    private static TriageRow Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        CaptureRef:  GetStr(r, "capture_ref")  ?? "",
        BatchId:     GetStr(r, "batch_id")     ?? "",
        Risk:        GetStr(r, "risk")         ?? "",
        Score:       GetInt(r, "score"),
        Rationale:   GetStr(r, "rationale"),
        ReviewState: GetStr(r, "review_state") ?? "pending",
        SourceFile:  GetStr(r, "source_file")  ?? "");
}

/// <summary>A triage row including its (DB-owned) review state.</summary>
public sealed record TriageRow(
    string  CaptureRef,
    string  BatchId,
    string  Risk,
    int?    Score,
    string? Rationale,
    string  ReviewState,
    string  SourceFile);
