# JSON → SQL Migration

Reference docs for migrating TheOrc's operational metadata from loose JSON/TSV files
to a single SQLite database. **Phase 0–1 complete and live.**

Read in order:

1. **[00_ROADMAP.md](00_ROADMAP.md)** — why, scope, the "no SQL hell" guardrails, the
   5 phases, sequencing rules, open decisions, decision log. **Start here.**
2. **[01_SCHEMA_DESIGN.md](01_SCHEMA_DESIGN.md)** — proposed tables + file→table mapping.
3. **[02_SECURITY_HIVEMIND.md](02_SECURITY_HIVEMIND.md)** — SQL-injection rule + the
   HIVEMIND unauthenticated-endpoint threat model. Gates Phase 4.

## The one-paragraph version

Operational state (2,286+ capture files, triage TSVs, Pit Boss plans, run history,
in-memory hive state) moves into `theorc.db`. Training `*.jsonl` corpora **stay as files**.
Migration is additive and per-table reversible — files stay canonical until each table
earns cutover. Two security must-haves: parameterized queries everywhere (closes SQL
injection structurally), and auth + validation + quotas before any unauthenticated
HIVEMIND wire input is made durable (Phase 4 is gated on it).
