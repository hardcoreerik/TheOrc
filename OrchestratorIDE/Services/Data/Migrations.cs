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
        new Migration(4, "hive tasks + events", Sql004_Hive),
        new Migration(5, "code graph nodes/edges/fts/adr", Sql005_Graph),
        new Migration(6, "graph_adr step4 (title,decision,status,created_at,body)", Sql006_AdrV2),
        new Migration(7, "native campaign engine", Sql007_Campaigns),
        new Migration(8, "context fabric ingestion and segment search", Sql008_ContextFabric),
        new Migration(9, "context fabric segment integrity retrofit", Sql009_ContextFabricSegmentIntegrity),
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

    // ── v4 — Phase 4: HIVEMIND persistence (SECURITY-GATED) ──────────────────────
    // Persists what HiveTaskQueue held in memory only (5-min eviction). The columns
    // carry PROVENANCE — claimed_by_node is the HMAC-AUTHENTICATED NodeId (not the
    // wire-claimed worker name), authenticated flags whether the submitter was signed,
    // and claim_token ties a result to the claim it answered. retain_until drives the
    // retention sweep so durable hive history can never grow unbounded.
    // See docs/sql-migration/02_SECURITY_HIVEMIND.md — all wire-sourced strings are
    // length-capped and charset-sanitised in the repository write path, never trusted.
    private const string Sql004_Hive = """
        CREATE TABLE hive_tasks (
            id                INTEGER PRIMARY KEY,
            task_id           TEXT    NOT NULL,
            session_id        TEXT    NOT NULL,
            role              TEXT,
            title             TEXT,
            status            TEXT    NOT NULL,
            claimed_by_node   TEXT,
            claimed_by_worker TEXT,
            authenticated     INTEGER NOT NULL DEFAULT 0,
            claim_token       TEXT,
            result_blob       TEXT,
            duration_ms       INTEGER,
            error_msg         TEXT,
            enqueued_at       TEXT    NOT NULL,
            updated_at        TEXT    NOT NULL,
            retain_until      TEXT    NOT NULL,
            UNIQUE(session_id, task_id)
        );
        CREATE INDEX ix_hive_tasks_session ON hive_tasks(session_id);
        CREATE INDEX ix_hive_tasks_status  ON hive_tasks(status);
        CREATE INDEX ix_hive_tasks_node    ON hive_tasks(claimed_by_node);
        CREATE INDEX ix_hive_tasks_retain  ON hive_tasks(retain_until);

        CREATE TABLE hive_events (
            id                INTEGER PRIMARY KEY,
            session_id        TEXT,
            type              TEXT    NOT NULL,
            msg               TEXT,
            task_id           TEXT,
            worker_id         TEXT,
            submitted_by_node TEXT,
            authenticated     INTEGER NOT NULL DEFAULT 0,
            created_at        TEXT    NOT NULL,
            retain_until      TEXT    NOT NULL
        );
        CREATE INDEX ix_hive_events_session ON hive_events(session_id);
        CREATE INDEX ix_hive_events_retain  ON hive_events(retain_until);
        """;

    // ── v7 — Phase 3B native campaigns ────────────────────────────────────────
    private const string Sql007_Campaigns = """
        CREATE TABLE campaigns (
            campaign_id     TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            pack_id         TEXT NOT NULL,
            pack_version    TEXT NOT NULL,
            status          TEXT NOT NULL,
            definition_json TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL,
            retain_until    TEXT NOT NULL
        );
        CREATE INDEX ix_campaigns_status ON campaigns(status);
        CREATE INDEX ix_campaigns_retain ON campaigns(retain_until);

        CREATE TABLE campaign_work_units (
            campaign_id      TEXT NOT NULL REFERENCES campaigns(campaign_id) ON DELETE CASCADE,
            work_unit_id     TEXT NOT NULL,
            task_id          TEXT,
            title            TEXT,
            execution_kind   TEXT NOT NULL,
            status           TEXT NOT NULL,
            attempt          INTEGER NOT NULL DEFAULT 1,
            claimed_by_node  TEXT,
            result_json      TEXT,
            error_msg        TEXT,
            updated_at       TEXT NOT NULL,
            PRIMARY KEY (campaign_id, work_unit_id)
        );
        CREATE INDEX ix_campaign_units_status ON campaign_work_units(campaign_id, status);

        CREATE TABLE campaign_artifacts (
            campaign_id      TEXT NOT NULL REFERENCES campaigns(campaign_id) ON DELETE CASCADE,
            work_unit_id     TEXT,
            digest_sha256    TEXT NOT NULL,
            name             TEXT NOT NULL,
            size_bytes       INTEGER NOT NULL,
            media_type       TEXT,
            kind             TEXT,
            storage_path     TEXT,
            verified         INTEGER NOT NULL DEFAULT 0,
            created_at       TEXT NOT NULL,
            PRIMARY KEY (campaign_id, digest_sha256)
        );
        CREATE INDEX ix_campaign_artifacts_digest ON campaign_artifacts(digest_sha256);
        """;

    // ── v8 — Context Fabric deterministic ingestion ───────────────────────────
    private const string Sql008_ContextFabric = """
        CREATE TABLE fabric_corpora (
            corpus_id       TEXT PRIMARY KEY,
            name            TEXT NOT NULL,
            description     TEXT,
            policy_profile  TEXT NOT NULL,
            status          TEXT NOT NULL,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL
        );

        CREATE TABLE fabric_documents (
            document_id       TEXT PRIMARY KEY,
            corpus_id         TEXT NOT NULL REFERENCES fabric_corpora(corpus_id) ON DELETE CASCADE,
            source_digest     TEXT NOT NULL,
            normalized_digest TEXT NOT NULL,
            display_name      TEXT NOT NULL,
            media_type        TEXT NOT NULL,
            parser_id         TEXT NOT NULL,
            parser_version    TEXT NOT NULL,
            status            TEXT NOT NULL,
            warnings_json     TEXT NOT NULL,
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL,
            UNIQUE(corpus_id, source_digest, media_type, parser_id, parser_version)
        );
        CREATE INDEX ix_fabric_documents_corpus ON fabric_documents(corpus_id, display_name);
        CREATE INDEX ix_fabric_documents_source ON fabric_documents(source_digest);

        CREATE TABLE fabric_segments (
            segment_id          TEXT PRIMARY KEY,
            document_id         TEXT NOT NULL REFERENCES fabric_documents(document_id) ON DELETE CASCADE,
            ordinal             INTEGER NOT NULL,
            heading_path        TEXT,
            char_start          INTEGER NOT NULL,
            char_end            INTEGER NOT NULL,
            token_count         INTEGER NOT NULL,
            text_digest         TEXT NOT NULL,
            previous_segment_id TEXT,
            next_segment_id     TEXT,
            chunker_version     TEXT NOT NULL,
            UNIQUE(document_id, ordinal, chunker_version)
        );
        CREATE INDEX ix_fabric_segments_document ON fabric_segments(document_id, ordinal);

        CREATE TABLE fabric_segment_text (
            segment_id      TEXT PRIMARY KEY REFERENCES fabric_segments(segment_id) ON DELETE CASCADE,
            heading_path    TEXT,
            normalized_text TEXT NOT NULL
        );

        CREATE VIRTUAL TABLE fabric_segment_fts USING fts5(
            heading_path,
            normalized_text,
            content='fabric_segment_text',
            content_rowid='rowid',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER fabric_segment_text_ai AFTER INSERT ON fabric_segment_text BEGIN
            INSERT INTO fabric_segment_fts(rowid, heading_path, normalized_text)
            VALUES (new.rowid, new.heading_path, new.normalized_text);
        END;
        CREATE TRIGGER fabric_segment_text_ad AFTER DELETE ON fabric_segment_text BEGIN
            INSERT INTO fabric_segment_fts(fabric_segment_fts, rowid, heading_path, normalized_text)
            VALUES ('delete', old.rowid, old.heading_path, old.normalized_text);
        END;
        CREATE TRIGGER fabric_segment_text_au AFTER UPDATE ON fabric_segment_text BEGIN
            INSERT INTO fabric_segment_fts(fabric_segment_fts, rowid, heading_path, normalized_text)
            VALUES ('delete', old.rowid, old.heading_path, old.normalized_text);
            INSERT INTO fabric_segment_fts(rowid, heading_path, normalized_text)
            VALUES (new.rowid, new.heading_path, new.normalized_text);
        END;
        """;

    // v8 shipped without range constraints. Rebuild both linked tables so existing
    // databases and fresh installs converge on the same constrained schema.
    private const string Sql009_ContextFabricSegmentIntegrity = """
        DROP TRIGGER fabric_segment_text_ai;
        DROP TRIGGER fabric_segment_text_ad;
        DROP TRIGGER fabric_segment_text_au;
        DROP TABLE fabric_segment_fts;
        DROP INDEX ix_fabric_segments_document;

        ALTER TABLE fabric_segment_text RENAME TO fabric_segment_text_v8;
        ALTER TABLE fabric_segments RENAME TO fabric_segments_v8;

        CREATE TABLE fabric_segments (
            segment_id          TEXT PRIMARY KEY,
            document_id         TEXT NOT NULL REFERENCES fabric_documents(document_id) ON DELETE CASCADE,
            ordinal             INTEGER NOT NULL CHECK (ordinal >= 0),
            heading_path        TEXT,
            char_start          INTEGER NOT NULL CHECK (char_start >= 0),
            char_end            INTEGER NOT NULL CHECK (char_end >= char_start),
            token_count         INTEGER NOT NULL CHECK (token_count >= 0),
            text_digest         TEXT NOT NULL,
            previous_segment_id TEXT,
            next_segment_id     TEXT,
            chunker_version     TEXT NOT NULL,
            UNIQUE(document_id, ordinal, chunker_version)
        );
        CREATE INDEX ix_fabric_segments_document ON fabric_segments(document_id, ordinal);

        INSERT INTO fabric_segments
            (segment_id, document_id, ordinal, heading_path, char_start, char_end,
             token_count, text_digest, previous_segment_id, next_segment_id, chunker_version)
        SELECT segment_id, document_id, ordinal, heading_path, char_start, char_end,
               token_count, text_digest, previous_segment_id, next_segment_id, chunker_version
        FROM fabric_segments_v8;

        CREATE TABLE fabric_segment_text (
            segment_id      TEXT PRIMARY KEY REFERENCES fabric_segments(segment_id) ON DELETE CASCADE,
            heading_path    TEXT,
            normalized_text TEXT NOT NULL
        );
        INSERT INTO fabric_segment_text(segment_id, heading_path, normalized_text)
        SELECT segment_id, heading_path, normalized_text
        FROM fabric_segment_text_v8;

        DROP TABLE fabric_segment_text_v8;
        DROP TABLE fabric_segments_v8;

        CREATE VIRTUAL TABLE fabric_segment_fts USING fts5(
            heading_path,
            normalized_text,
            content='fabric_segment_text',
            content_rowid='rowid',
            tokenize='unicode61 remove_diacritics 2'
        );
        INSERT INTO fabric_segment_fts(rowid, heading_path, normalized_text)
        SELECT rowid, heading_path, normalized_text FROM fabric_segment_text;

        CREATE TRIGGER fabric_segment_text_ai AFTER INSERT ON fabric_segment_text BEGIN
            INSERT INTO fabric_segment_fts(rowid, heading_path, normalized_text)
            VALUES (new.rowid, new.heading_path, new.normalized_text);
        END;
        CREATE TRIGGER fabric_segment_text_ad AFTER DELETE ON fabric_segment_text BEGIN
            INSERT INTO fabric_segment_fts(fabric_segment_fts, rowid, heading_path, normalized_text)
            VALUES ('delete', old.rowid, old.heading_path, old.normalized_text);
        END;
        CREATE TRIGGER fabric_segment_text_au AFTER UPDATE ON fabric_segment_text BEGIN
            INSERT INTO fabric_segment_fts(fabric_segment_fts, rowid, heading_path, normalized_text)
            VALUES ('delete', old.rowid, old.heading_path, old.normalized_text);
            INSERT INTO fabric_segment_fts(rowid, heading_path, normalized_text)
            VALUES (new.rowid, new.heading_path, new.normalized_text);
        END;
        """;

    // ── v5 — CodeGraph v1 (C# structure + search index) ─────────────────────────
    // Tables per CodeGraph_v1.md. FTS5 for BM25 search over names (camelCase split
    // performed at write time in GraphRepository so natural language queries hit).
    // Triggers keep graph_fts in sync on node changes (external-content pattern).
    // New tables only; no changes to prior migrations.
    private const string Sql005_Graph = """
        CREATE TABLE graph_nodes (
            id            INTEGER PRIMARY KEY,
            project       TEXT    NOT NULL,
            label         TEXT    NOT NULL,
            name          TEXT    NOT NULL,
            qualified_name TEXT   NOT NULL,
            file_path     TEXT    NOT NULL,
            line_start    INTEGER NOT NULL,
            line_end      INTEGER NOT NULL,
            cyclomatic            INTEGER,
            cognitive             INTEGER,
            loop_depth            INTEGER,
            transitive_loop_depth INTEGER,
            linear_scan_in_loop   INTEGER,
            is_recursive          INTEGER DEFAULT 0,
            degree        INTEGER DEFAULT 0,
            UNIQUE(project, qualified_name)
        );
        CREATE INDEX ix_gn_project ON graph_nodes(project);
        CREATE INDEX ix_gn_label   ON graph_nodes(project, label);
        CREATE INDEX ix_gn_file    ON graph_nodes(project, file_path);

        CREATE TABLE graph_edges (
            id        INTEGER PRIMARY KEY,
            project   TEXT    NOT NULL,
            src_id    INTEGER NOT NULL REFERENCES graph_nodes(id) ON DELETE CASCADE,
            dst_id    INTEGER NOT NULL REFERENCES graph_nodes(id) ON DELETE CASCADE,
            edge_type TEXT    NOT NULL,
            UNIQUE(project, src_id, dst_id, edge_type)
        );
        CREATE INDEX ix_ge_src ON graph_edges(src_id, edge_type);
        CREATE INDEX ix_ge_dst ON graph_edges(dst_id, edge_type);

        CREATE VIRTUAL TABLE graph_fts USING fts5(
            name, qualified_name, file_path,
            content='graph_nodes', content_rowid='id',
            tokenize='unicode61'
        );

        CREATE TABLE graph_adr (
            id         INTEGER PRIMARY KEY,
            project    TEXT NOT NULL,
            section    TEXT NOT NULL,
            content    TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(project, section)
        );

        -- FTS sync triggers (support external content + manual repop with split tokens from C# side)
        CREATE TRIGGER graph_nodes_ai AFTER INSERT ON graph_nodes BEGIN
            INSERT INTO graph_fts(rowid, name, qualified_name, file_path)
            VALUES (new.id, new.name, new.qualified_name, new.file_path);
        END;
        CREATE TRIGGER graph_nodes_ad AFTER DELETE ON graph_nodes BEGIN
            INSERT INTO graph_fts(graph_fts, rowid, name, qualified_name, file_path)
            VALUES ('delete', old.id, old.name, old.qualified_name, old.file_path);
        END;
        CREATE TRIGGER graph_nodes_au AFTER UPDATE ON graph_nodes BEGIN
            INSERT INTO graph_fts(graph_fts, rowid, name, qualified_name, file_path)
            VALUES ('delete', old.id, old.name, old.qualified_name, old.file_path);
            INSERT INTO graph_fts(rowid, name, qualified_name, file_path)
            VALUES (new.id, new.name, new.qualified_name, new.file_path);
        END;
        """;

    // ── v6 — Evolve graph_adr for step 4 tool (title/decision/status/created_at + body)
    // Old (v5) was (section, content, updated_at, unique on project+section).
    // New supports list by created, get-by-id, plain adds (no section key), status enum.
    // Data is migrated where possible; old table dropped after.
    private const string Sql006_AdrV2 = """
        -- Backup old if exists, create new schema, copy data best-effort, drop old
        CREATE TABLE IF NOT EXISTS graph_adr_old AS SELECT * FROM graph_adr;

        DROP TABLE IF EXISTS graph_adr;

        CREATE TABLE graph_adr (
            id         INTEGER PRIMARY KEY,
            project    TEXT NOT NULL,
            title      TEXT NOT NULL,
            decision   TEXT NOT NULL,
            status     TEXT NOT NULL DEFAULT 'proposed',
            body       TEXT,
            created_at TEXT NOT NULL
        );
        CREATE INDEX ix_adr_project ON graph_adr(project);
        CREATE INDEX ix_adr_created ON graph_adr(created_at DESC);

        -- Migrate previous records if any (section -> title, content -> decision+body, updated -> created)
        INSERT INTO graph_adr (project, title, decision, status, body, created_at)
        SELECT
            project,
            COALESCE(section, 'untitled'),
            COALESCE(content, ''),
            'accepted',
            COALESCE(content, ''),
            COALESCE(updated_at, datetime('now'))
        FROM graph_adr_old
        WHERE project IS NOT NULL;

        DROP TABLE IF EXISTS graph_adr_old;
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
