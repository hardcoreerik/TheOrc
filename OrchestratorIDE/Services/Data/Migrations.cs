// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;

namespace OrchestratorIDE.Services.Data;

/// <summary>One forward-only schema migration. Versions are applied in ascending order.</summary>
internal sealed record Migration(int Version, string Description, string Sql);

/// <summary>
/// The ordered list of schema migrations. Forward-only: never edit a shipped migration,
/// always add a new one with the next version number. See docs/sql-migration/01_SCHEMA_DESIGN.md.
/// </summary>
internal static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new Migration(1, "captures + triage", Sql001_CapturesTriage),
        new Migration(2, "plans + runs",       Sql002_PlansRuns),
        new Migration(3, "datasets index",     Sql003_Datasets),
    ];

    // ── v1 — Phase 1: captures + triage ─────────────────────────────────────────
    // Mirrors DatasetCapture.BuildCapture (plan_capture_*.json) and the
    // batch_*_triage.tsv review files. Training *.jsonl corpora are NOT stored here.
    private const string Sql001_CapturesTriage = """
        CREATE TABLE captures (
            id            INTEGER PRIMARY KEY,
            example_id    TEXT    NOT NULL UNIQUE,
            run_id        TEXT    NOT NULL,
            captured_at   TEXT    NOT NULL,
            source        TEXT    NOT NULL,
            boss_model    TEXT    NOT NULL,
            goal          TEXT    NOT NULL,
            domain        TEXT,
            difficulty    INTEGER,
            quality_score INTEGER NOT NULL,
            example_class TEXT,
            failure_mode  TEXT,
            plan_json     TEXT,
            rubric_json   TEXT,
            annotator     TEXT    DEFAULT 'auto',
            notes         TEXT    DEFAULT '',
            source_file   TEXT    NOT NULL,
            imported_at   TEXT    NOT NULL
        );
        CREATE INDEX ix_captures_score ON captures(quality_score);
        CREATE INDEX ix_captures_class ON captures(example_class);
        CREATE INDEX ix_captures_run   ON captures(run_id);

        CREATE TABLE triage (
            id           INTEGER PRIMARY KEY,
            capture_ref  TEXT    NOT NULL,
            batch_id     TEXT    NOT NULL,
            risk         TEXT    NOT NULL,
            score        INTEGER,
            rationale    TEXT,
            review_state TEXT    DEFAULT 'pending',
            reviewed_by  TEXT,
            reviewed_at  TEXT,
            source_file  TEXT    NOT NULL,
            imported_at  TEXT    NOT NULL,
            UNIQUE(batch_id, capture_ref)
        );
        CREATE INDEX ix_triage_state ON triage(review_state);
        CREATE INDEX ix_triage_risk  ON triage(risk);
        """;

    // ── v2 — Phase 2: plans + runs ───────────────────────────────────────────────
    // plans: Pit Boss TrainingPlan objects (training_pit/plans/*.json).
    // runs:  training-run history (dataset_gen / forge_train / eval).
    private const string Sql002_PlansRuns = """
        CREATE TABLE plans (
            id              INTEGER PRIMARY KEY,
            plan_id         TEXT    NOT NULL UNIQUE,
            created_at      TEXT    NOT NULL,
            goal            TEXT    NOT NULL,
            persona         TEXT,
            style           TEXT,
            languages_json  TEXT,
            task_mix_json   TEXT,
            dataset_target  INTEGER,
            dataset_source  TEXT,
            base_model      TEXT,
            adapter_name    TEXT,
            lora_rank       INTEGER,
            epochs          INTEGER,
            learning_rate   REAL,
            phase           TEXT,
            dataset_file    TEXT,
            adapter_path    TEXT,
            hive_json       TEXT,
            notes           TEXT
        );

        CREATE TABLE runs (
            id            INTEGER PRIMARY KEY,
            run_id        TEXT    NOT NULL UNIQUE,
            plan_id       TEXT    REFERENCES plans(plan_id),
            kind          TEXT,
            status        TEXT,
            started_at    TEXT,
            ended_at      TEXT,
            host          TEXT,
            artifact_path TEXT,
            metrics_json  TEXT,
            log_path      TEXT
        );
        CREATE INDEX ix_runs_plan ON runs(plan_id);
        """;

    // ── v3 — Phase 3: datasets registry index ────────────────────────────────────
    // Cache/index over training_pit/datasets/*.jsonl. Files stay canonical.
    // Populated by TrainingPitRegistry.LoadDatasets (dual-write) and MetadataImporter.
    private const string Sql003_Datasets = """
        CREATE TABLE datasets (
            id                INTEGER PRIMARY KEY,
            file_path         TEXT    NOT NULL UNIQUE,
            name              TEXT    NOT NULL,
            source            TEXT,
            context           TEXT,
            data_type         TEXT,
            role              TEXT,
            is_new_convention INTEGER,
            in_progress       INTEGER,
            train_count       INTEGER,
            eval_count        INTEGER,
            total_count       INTEGER,
            last_modified     TEXT,
            indexed_at        TEXT    NOT NULL
        );
        CREATE INDEX ix_datasets_source ON datasets(source);
        CREATE INDEX ix_datasets_role   ON datasets(role);
        """;
}

/// <summary>
/// Applies pending migrations. The <c>schema_migrations</c> bookkeeping table is created
/// first (bootstrap), then each unapplied migration runs in its own transaction so a
/// failure leaves the DB at the last good version rather than half-applied.
/// </summary>
internal static class MigrationRunner
{
    public static void Apply(SqliteConnection conn)
    {
        EnsureBookkeeping(conn);
        var applied = AppliedVersions(conn);

        foreach (var m in Migrations.All.OrderBy(m => m.Version))
        {
            if (applied.Contains(m.Version)) continue;

            using var tx = conn.BeginTransaction();

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = m.Sql;
                cmd.ExecuteNonQuery();
            }

            using (var rec = conn.CreateCommand())
            {
                rec.Transaction = tx;
                rec.CommandText =
                    "INSERT INTO schema_migrations(version, applied_at, description) " +
                    "VALUES($v, $t, $d)";
                rec.Parameters.AddWithValue("$v", m.Version);
                rec.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                rec.Parameters.AddWithValue("$d", m.Description);
                rec.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private static void EnsureBookkeeping(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT NOT NULL,
                description TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static HashSet<int> AppliedVersions(SqliteConnection conn)
    {
        var set = new HashSet<int>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_migrations";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetInt32(0));
        return set;
    }
}
