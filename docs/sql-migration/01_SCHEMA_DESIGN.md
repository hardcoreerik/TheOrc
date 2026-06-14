# JSON → SQL Migration — Schema Design

> Proposed SQLite schema and the file→table mapping. Grounded in the current code:
> `DatasetCapture.cs`, `TrainingPitRegistry.cs`, `PitBossService`/`TrainingPlan.cs`,
> `HiveTaskBundle.cs` / `HiveTaskQueue.cs`. Draft — refine per phase before writing migrations.

---

## Conventions

- SQLite, WAL mode, `PRAGMA foreign_keys=ON`.
- Timestamps stored as ISO-8601 UTC text (`yyyy-MM-ddTHH:mm:ssZ`) to match existing
  capture format (`DatasetCapture.BuildCapture` already emits this).
- Every table has a **natural key** (used for idempotent upsert) plus an integer PK.
- JSON blobs that we don't need to query stay as a `TEXT` column (e.g. the full plan
  node) — we index the *queryable* fields out into columns, keep the rest as a blob.

---

## Phase 1 — `captures`

Source: `.orc/swarm/dataset-staging/plan_capture_{good|bad}_{runId}_{score}.json`
(written by `DatasetCapture.BuildCapture`).

```sql
CREATE TABLE captures (
    id              INTEGER PRIMARY KEY,
    example_id      TEXT NOT NULL UNIQUE,   -- "ex_{runId}" — natural key
    run_id          TEXT NOT NULL,
    captured_at     TEXT NOT NULL,          -- ISO-8601 UTC
    source          TEXT NOT NULL,          -- "swarm_run"
    boss_model      TEXT NOT NULL,
    goal            TEXT NOT NULL,
    domain          TEXT,                   -- "general" until reviewed
    difficulty      INTEGER,
    quality_score   INTEGER NOT NULL,       -- composite
    example_class   TEXT,                   -- positive | negative
    failure_mode    TEXT,
    plan_json       TEXT,                   -- full boss plan node (not indexed)
    rubric_json     TEXT,                   -- rubric_scores blob
    annotator       TEXT DEFAULT 'auto',
    notes           TEXT DEFAULT '',
    source_file     TEXT NOT NULL,          -- path on disk (canonical during transition)
    imported_at     TEXT NOT NULL
);
CREATE INDEX ix_captures_score   ON captures(quality_score);
CREATE INDEX ix_captures_class   ON captures(example_class);
CREATE INDEX ix_captures_run     ON captures(run_id);
```

Upsert key: `example_id`.

## Phase 1 — `triage`

Source: `training_pit/batch_*_triage.tsv` (one row per judged capture; columns like
`RISK score id rationale`).

```sql
CREATE TABLE triage (
    id            INTEGER PRIMARY KEY,
    capture_ref   TEXT NOT NULL,            -- e.g. "NH260614_022050c6-T010"
    batch_id      TEXT NOT NULL,            -- "NH260614_022050c6"
    risk          TEXT NOT NULL,            -- HIGH | MEDIUM | LOW
    score         INTEGER,
    rationale     TEXT,
    review_state  TEXT DEFAULT 'pending',   -- pending | approved | rejected
    reviewed_by   TEXT,
    reviewed_at   TEXT,
    source_file   TEXT NOT NULL,
    imported_at   TEXT NOT NULL,
    UNIQUE(batch_id, capture_ref)
);
CREATE INDEX ix_triage_state ON triage(review_state);
CREATE INDEX ix_triage_risk  ON triage(risk);
```

> The win this unlocks: `SELECT * FROM triage WHERE risk='HIGH' AND review_state='pending'`
> replaces opening 6+ TSV files by hand.

---

## Phase 2 — `plans`

Source: Pit Boss `TrainingPlan` (`Models/TrainingPlan.cs`), today `training_pit/plans/*.json`.

```sql
CREATE TABLE plans (
    id              INTEGER PRIMARY KEY,
    plan_id         TEXT NOT NULL UNIQUE,   -- TrainingPlan.PlanId
    created_at      TEXT NOT NULL,
    goal            TEXT NOT NULL,
    persona         TEXT,
    style           TEXT,
    languages_json  TEXT,                   -- List<string>
    task_mix_json   TEXT,                   -- Dictionary<string,double>
    dataset_target  INTEGER,
    dataset_source  TEXT,
    base_model      TEXT,
    adapter_name    TEXT,
    lora_rank       INTEGER,
    epochs          INTEGER,
    learning_rate   REAL,
    phase           TEXT,                   -- PlanPhase enum
    dataset_file    TEXT,
    adapter_path    TEXT,
    hive_json       TEXT,                   -- HiveStrategy blob (nullable)
    notes           TEXT
);
```

Upsert key: `plan_id`. Enables the deferred **plan-history panel**.

## Phase 2 — `runs`

Training-run history (today scattered in logs). New durable record.

```sql
CREATE TABLE runs (
    id            INTEGER PRIMARY KEY,
    run_id        TEXT NOT NULL UNIQUE,
    plan_id       TEXT REFERENCES plans(plan_id),
    kind          TEXT,                     -- dataset_gen | forge_train | eval
    status        TEXT,                     -- running | complete | failed
    started_at    TEXT,
    ended_at      TEXT,
    host          TEXT,                     -- machine name (mainpc, HARDCOREPC, …)
    artifact_path TEXT,                     -- dataset/adapter produced
    metrics_json  TEXT,                     -- loss, accuracy, etc.
    log_path      TEXT
);
CREATE INDEX ix_runs_plan ON runs(plan_id);
```

---

## Phase 3 — `datasets` (registry index)

Source: `training_pit/datasets/*.jsonl`, indexed by `TrainingPitRegistry.LoadDatasets`.
Files stay canonical; this is a cache.

```sql
CREATE TABLE datasets (
    id               INTEGER PRIMARY KEY,
    file_path        TEXT NOT NULL UNIQUE,
    name             TEXT NOT NULL,
    source           TEXT,                  -- parsed: "hardcorepc"
    context          TEXT,                  -- parsed: "6gb"
    data_type        TEXT,                  -- synthetic | captured | normalized | raw
    role             TEXT,                  -- "boss"
    is_new_convention INTEGER,              -- 0/1
    in_progress      INTEGER,               -- 0/1  (.work.jsonl)
    train_count      INTEGER,
    eval_count       INTEGER,
    total_count      INTEGER,
    last_modified    TEXT,
    indexed_at       TEXT
);
```

Upsert key: `file_path`. Mirrors `TrainingPitRegistry.DatasetInfo` field-for-field.

---

## Phase 4 — HIVEMIND (SECURITY-GATED — do not build before security checklist)

Persists what `HiveTaskQueue` holds in memory. These tables accept data derived from
**remote, currently-unauthenticated** worker submissions — see
[02_SECURITY_HIVEMIND.md](02_SECURITY_HIVEMIND.md). Note the hard length caps and the
retention column; both are mandatory here, not optional.

```sql
CREATE TABLE hive_tasks (
    id            INTEGER PRIMARY KEY,
    task_id       TEXT NOT NULL UNIQUE,
    session_id    TEXT NOT NULL,
    role          TEXT NOT NULL,
    title         TEXT NOT NULL,            -- CAP 512 chars at write time
    status        TEXT NOT NULL,            -- pending|claimed|completed|failed|timeout
    claimed_by    TEXT,                     -- worker-supplied; CAP 128; validated charset
    claimed_at    TEXT,
    enqueued_at   TEXT NOT NULL,
    completed_at  TEXT,
    duration_ms   INTEGER,
    result_len    INTEGER,                  -- store length, not necessarily full blob
    result_blob   TEXT,                     -- optional; CAP enforced; nullable
    retain_until  TEXT                      -- retention horizon (bounded growth)
);
CREATE INDEX ix_hive_tasks_session ON hive_tasks(session_id);

CREATE TABLE hive_events (
    seq           INTEGER PRIMARY KEY,      -- mirrors HiveEvent.Seq
    ts            TEXT NOT NULL,
    type          TEXT NOT NULL,            -- task_queued|task_claimed|…
    msg           TEXT,                     -- CAP 1024
    task_id       TEXT,
    worker_id     TEXT,                     -- CAP 128; validated
    session_id    TEXT,
    retain_until  TEXT
);
CREATE INDEX ix_hive_events_session ON hive_events(session_id);
```

**Mandatory before these ship:** caps enforced in the repository write path (truncate +
log, never trust the wire length), a per-worker row quota, and a retention sweep that
deletes rows past `retain_until`.

---

## `schema_migrations` (Phase 0)

```sql
CREATE TABLE schema_migrations (
    version     INTEGER PRIMARY KEY,
    applied_at  TEXT NOT NULL,
    description TEXT
);
```

---

## File → Table mapping summary

| Today (file)                                   | Table         | Phase | Canonical after migration |
|------------------------------------------------|---------------|-------|----------------------------|
| `dataset-staging/plan_capture_*.json`          | `captures`    | 1     | File (until P5)            |
| `batch_*_triage.tsv`                           | `triage`      | 1     | SQL (review state is new)  |
| `training_pit/plans/*.json`                    | `plans`       | 2     | SQL                        |
| training run logs                              | `runs`        | 2     | SQL                        |
| `training_pit/datasets/*.jsonl`                | `datasets`    | 3     | **File — always** (index)  |
| `HiveTaskQueue` in-memory dict                 | `hive_tasks`  | 4     | SQL                        |
| `HiveEventBus` ring buffer                     | `hive_events` | 4     | SQL                        |
| `*.jsonl` training corpora                     | — none —      | —     | **File — never migrated**  |
