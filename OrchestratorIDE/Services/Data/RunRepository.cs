// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Read/write access to the <c>runs</c> table. All SQL is parameterized via RepositoryBase.
///
/// Lifecycle: insert at run start (status="running"), then call
/// <see cref="UpdateStatus"/> to flip to "complete"/"failed"/"cancelled" on finish.
/// </summary>
public sealed class RunRepository : RepositoryBase
{
    public RunRepository(SqliteStore store) : base(store) { }

    /// <summary>Idempotent upsert keyed on <c>run_id</c>.</summary>
    public void Upsert(RunRecord r)
    {
        Execute(
            """
            INSERT INTO runs
                (run_id, plan_id, kind, status, started_at, ended_at,
                 host, artifact_path, metrics_json, log_path)
            VALUES
                ($run_id, $plan_id, $kind, $status, $started_at, $ended_at,
                 $host, $artifact_path, $metrics_json, $log_path)
            ON CONFLICT(run_id) DO UPDATE SET
                plan_id      = excluded.plan_id,
                kind         = excluded.kind,
                status       = excluded.status,
                started_at   = excluded.started_at,
                ended_at     = excluded.ended_at,
                host         = excluded.host,
                artifact_path = excluded.artifact_path,
                metrics_json = excluded.metrics_json,
                log_path     = excluded.log_path
            """,
            ps =>
            {
                P(ps, "$run_id",        r.RunId);
                P(ps, "$plan_id",       r.PlanId);
                P(ps, "$kind",          r.Kind);
                P(ps, "$status",        r.Status);
                P(ps, "$started_at",    r.StartedAt);
                P(ps, "$ended_at",      r.EndedAt);
                P(ps, "$host",          r.Host);
                P(ps, "$artifact_path", r.ArtifactPath);
                P(ps, "$metrics_json",  r.MetricsJson);
                P(ps, "$log_path",      r.LogPath);
            });
    }

    /// <summary>Stamps the terminal status and end time on an existing run row.</summary>
    public void UpdateStatus(string runId, string status, string? artifactPath = null)
        => Execute(
            """
            UPDATE runs
            SET status        = $status,
                ended_at      = $ended_at,
                artifact_path = COALESCE($artifact_path, artifact_path)
            WHERE run_id = $run_id
            """,
            ps =>
            {
                P(ps, "$status",        status);
                P(ps, "$ended_at",      DateTime.UtcNow.ToString("o"));
                P(ps, "$artifact_path", artifactPath);
                P(ps, "$run_id",        runId);
            });

    /// <summary>All runs for a given plan, newest first.</summary>
    public List<RunRecord> ByPlan(string planId)
        => Query(
            """
            SELECT run_id, plan_id, kind, status, started_at, ended_at,
                   host, artifact_path, metrics_json, log_path
            FROM runs WHERE plan_id = $plan ORDER BY started_at DESC
            """,
            Map,
            ps => P(ps, "$plan", planId));

    /// <summary>Runs currently marked "running" (e.g. to detect stale runs on restart).</summary>
    public List<RunRecord> ActiveRuns()
        => Query(
            """
            SELECT run_id, plan_id, kind, status, started_at, ended_at,
                   host, artifact_path, metrics_json, log_path
            FROM runs WHERE status = 'running' ORDER BY started_at DESC
            """,
            Map);

    private static RunRecord Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        RunId:        GetStr(r, "run_id")        ?? "",
        PlanId:       GetStr(r, "plan_id"),
        Kind:         GetStr(r, "kind")          ?? "",
        Status:       GetStr(r, "status")        ?? "",
        StartedAt:    GetStr(r, "started_at")    ?? "",
        EndedAt:      GetStr(r, "ended_at"),
        Host:         GetStr(r, "host")          ?? "",
        ArtifactPath: GetStr(r, "artifact_path"),
        MetricsJson:  GetStr(r, "metrics_json"),
        LogPath:      GetStr(r, "log_path"));
}
