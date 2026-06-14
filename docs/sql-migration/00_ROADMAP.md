# JSON → SQL Migration — Roadmap

> **Status:** Planning. Nothing implemented yet. This is the north-star doc for the
> JSON-files → SQLite migration. Read this first, then [01_SCHEMA_DESIGN.md](01_SCHEMA_DESIGN.md)
> and [02_SECURITY_HIVEMIND.md](02_SECURITY_HIVEMIND.md).
>
> Companion to the top-level [ROADMAP.md](../ROADMAP.md) (this is the v1.6 "SQLite task board" line item, expanded).

---

## Why we are doing this

Today TheOrc keeps operational state as loose files:

- **2,286+** individual `plan_capture_*.json` files in `.orc/swarm/dataset-staging/`
- Per-batch triage state in `batch_*_triage.tsv` files
- Pit Boss plans as `training_pit/plans/*.json`
- Training-run history scattered across log files
- HIVEMIND task/event state held **in memory only** (`ConcurrentDictionary` in `HiveTaskQueue`, evicted after 5 min)

The pain: answering "how many HIGH-risk captures are pending review across all batches"
means opening thousands of files. There is no transactional state, no cross-batch query,
no durable hive history. A single indexed SQLite file fixes all of that.

## What we are NOT doing

**Training corpora stay as files.** The `*.jsonl` datasets (e.g.
`hardcorepc[6gb].synthetic.boss.638.jsonl`) are consumed by Python ML tooling that
expects JSONL on disk. We do **not** put training rows in SQL. Same for adapters,
model weights, and raw logs. SQL is for *operational metadata*, not the corpus.

> **Rule of thumb:** if a Python trainer reads it, it stays a file. If a human queries
> it or the UI filters it, it goes in SQL.

---

## Guardrails — how we avoid "SQL hell"

These are non-negotiable. Every PR in this initiative is checked against them.

1. **One database file, one owner process.** `training_pit/theorc.db`, opened by the
   WPF app. WAL mode (`PRAGMA journal_mode=WAL`) so reads never block the writer.
   No second process writes to it directly — remote nodes go through the app's HTTP
   layer, never touch the file.
2. **Files remain canonical during transition.** Phase 1 is *additive*: we import
   files into SQL and keep writing files too (dual-write). SQL becomes a queryable
   *index* before it becomes the *source of truth*. We do not delete a single JSON
   file until its table has been verified and a full re-import round-trips clean.
3. **Parameterized queries only. Always. No exceptions.** Every value goes through a
   `SqliteParameter`. There is never a reason to string-concatenate a value into SQL.
   This single rule closes the entire SQL-injection class. See
   [02_SECURITY_HIVEMIND.md](02_SECURITY_HIVEMIND.md).
4. **Versioned, forward-only migrations.** A `schema_migrations` table records applied
   versions. Migrations are numbered SQL scripts (`001_init.sql`, `002_…`). The app
   runs pending migrations on startup inside a transaction. No hand-editing of live schema.
5. **No heavyweight ORM.** Use `Microsoft.Data.Sqlite` directly (optionally a thin
   micro-ORM like Dapper for mapping). EF Core's migration machinery is overkill for a
   single-file desktop DB and invites the exact "hell" we're avoiding. One repository
   class per table; all SQL lives in those classes.
6. **Idempotent importers.** Every file→SQL importer is safe to re-run. Use
   `INSERT … ON CONFLICT(natural_key) DO UPDATE`. Re-importing the same staging folder
   twice must not duplicate rows.
7. **Bounded growth.** Every table that accepts machine-generated or remote-submitted
   rows has a retention/quota policy defined *before* it ships (see hive tables).

---

## Phases

### Phase 0 — Foundation (no behavior change)
- Add `Microsoft.Data.Sqlite` package.
- `SqliteStore` bootstrap: open `theorc.db`, enable WAL + foreign keys, run migrations.
- `schema_migrations` table + migration runner.
- Repository base class with a single parameterized-command helper (the only place
  that builds `SqliteCommand`s).
- **Exit criteria:** app launches, DB file created, migration v1 applied, zero feature change.

### Phase 1 — Captures + Triage (highest ROI, read-side first)
- Tables: `captures`, `triage`. See [01_SCHEMA_DESIGN.md](01_SCHEMA_DESIGN.md).
- One-shot importer: scan `.orc/swarm/dataset-staging/*.json` and `batch_*_triage.tsv`
  → upsert rows. Idempotent.
- `DatasetCapture.StageAsync` **dual-writes**: keep the JSON file (canonical) AND
  upsert a row. Still best-effort/error-swallowing — a DB failure must never break a swarm run.
- Training Pit dashboard gains a query-backed review view ("show all HIGH-risk pending").
- **Exit criteria:** review queries answered from SQL; files still written; full re-import round-trips.

### Phase 2 — Plans + Runs
- Tables: `plans` (Pit Boss `TrainingPlan`), `runs` (training-run history).
- `PitBossService.SavePlan/LoadPlans` and `PlanExecutorService` read/write SQL.
- Enables the deferred **plan-history panel** (re-launch a saved plan without the wizard).
- **Exit criteria:** plan history panel backed by SQL; run history queryable.

### Phase 3 — Datasets registry index
- Table: `datasets` — a cache/index over `training_pit/datasets/*.jsonl`.
- `TrainingPitRegistry.LoadDatasets` populates it; the `*.jsonl` files stay canonical.
- Speeds up Forge ComboBox population; enables "datasets by source/context/role" queries.
- **Exit criteria:** registry reads from index, falls back to file scan if index stale.

### Phase 4 — HIVEMIND persistence (SECURITY-GATED)
- Tables: `hive_tasks`, `hive_events`. Persist what is today in-memory in `HiveTaskQueue`.
- **Blocked on the auth + validation work in [02_SECURITY_HIVEMIND.md](02_SECURITY_HIVEMIND.md).**
  We do not persist remote-submitted worker data until shared-secret auth, input length
  caps, and per-node write quotas are in place. Durable storage of untrusted input is
  the single biggest risk vector in this whole migration — it gets its own gate.
- **Exit criteria:** hive history survives app restart; all security guardrails green.

### Phase 5 — Cutover (flip canonical, optional)
- Per-table decision: once a table has been dual-writing cleanly for a release cycle,
  optionally stop writing the file and make SQL canonical (with a one-button export-to-file).
- Never global. Each table flips independently, only after it has earned trust.

---

## Sequencing rules

- Ship phases in order. Each phase is independently shippable and reversible.
- A phase is "done" only when its exit criteria pass AND the guardrails checklist is green.
- Phase 4 cannot start until Phase 0–3 are stable AND the security doc's checklist is implemented.

## Open decisions (resolve before coding the phase that needs them)

- [ ] Dapper vs hand-rolled mapping. (Lean: Dapper for read mapping, raw commands for writes.)
- [ ] DB location: `training_pit/theorc.db` vs `.orc/theorc.db`. (Lean: `.orc/` — it's operational state, already gitignored.)
- [ ] Do we gitignore the DB? (Lean: yes — it's a rebuildable index in Phases 1–3.)
- [ ] Hive auth mechanism: shared secret header vs Tailscale-only ACL vs mTLS. (See security doc.)

## Decision log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-06-14 | Training `*.jsonl` corpora stay as files | ML tooling reads files; SQL is for metadata only |
| 2026-06-14 | Files stay canonical until per-table cutover | Reversible migration, no big-bang risk |
| 2026-06-14 | Phase 4 (hive persistence) is security-gated | Persisting unauthenticated remote input is the top risk |
