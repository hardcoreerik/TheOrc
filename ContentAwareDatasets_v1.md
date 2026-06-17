<!-- Copyright (C) 2025-present hardcoreerik / TheOrc contributors | SPDX-License-Identifier: AGPL-3.0-or-later -->
# TheOrc — Content-Aware Datasets (Training Pit) v1

**Target version:** v1.9.0
**Status:** Design spec
**Author:** design pass 2026-06-17
**Origin:** ORC ACADEMY v2 regression post-mortem — the boss adapter trained on
1,861 examples that *declared* `boss` but whose *content* was 57% tester-lane
(TESTER writing files). The data wasn't bad; it was misrouted. This spec makes
the gap between *declared purpose* and *actual content* a queryable, visible
property of the corpus.

Turn `training_pit/datasets/*.jsonl` from an opaque pile of files into a
**content-aware example pool**: one example can train multiple targets, and the
Training Pit surfaces the right datasets for whatever the user is training
(Boss, Worker·Coder, Worker·Tester, …) with a per-target fitness score.

---

## The mental model

A dataset is not a file. A dataset is a **query over a tagged pool of
examples**. The same 1,389 Cerebras examples answer three questions:

| User intent | Examples that surface |
|---|---|
| Train a **Boss** planner | 595 boss-clean |
| Train a **Worker · Tester** | 794 tester-gold |
| Train a **Worker · Coder · Python** | the Python coder slice |

Same atoms, different molecules. The `.jsonl` files stay canonical; SQL is the
index that makes them queryable per-target.

**This is SQL, not JSON sidecars.** It extends the v1.5 store pattern (Migration
v3 `datasets` is already a file-grain index; `captures` is already example-grain
for reviewed captures). We add the missing piece: a per-example, per-target
**suitability** grain, computed by the same deterministic rubric the A/B eval
uses.

---

## Architecture

```
training_pit/tools/
  score_suitability.py     Rubric → per-example suitability rows (Python, authoritative)
                           Reads datasets/*.jsonl, emits *.suitability.jsonl artifacts.
OrchestratorIDE/Services/Data/
  Migrations.cs            + Migration v7: example_suitability table
  SuitabilityRepository.cs Read/write example_suitability (extends RepositoryBase)
  DataRecords.cs           + ExampleSuitabilityRecord, SuitabilityQuery records
  MetadataImporter.cs      + ImportSuitability(): *.suitability.jsonl → table
OrchestratorIDE/Services/TrainingPit/
  VirtualDatasetService.cs Materialize a SuitabilityQuery → fresh train_{KEY}/eval_{KEY}
OrchestratorIDE.Avalonia/Views/
  TrainingPitView          Content-aware browser: target → role → filters → datasets
```

**Division of labor (matches existing pattern):**
- **Python owns the rubric.** Scoring logic already lives in
  `training_pit/scripts/eval_adapter.py` (`score_plan`) and the v2 audit. One
  source of truth, no C# port to drift from. The scorer reads canonical JSONL
  and writes `*.suitability.jsonl` artifacts next to each dataset.
- **C# owns SQL ingestion.** Exactly as `MetadataImporter` already turns capture
  TSV/JSON into the `captures`/`triage` tables, a new `ImportSuitability` turns
  `*.suitability.jsonl` into `example_suitability`. Files canonical, C# indexes.
- **Avalonia owns the browser**, reading via `SuitabilityRepository`.

---

## Data model (Migration v7)

```sql
-- Per-example, per-target fitness. One row per (dataset_file, example_hash).
-- Index/cache over the .jsonl pool — files remain canonical, rows are
-- recomputed whenever score_suitability.py re-runs (indexed_at tracks freshness).
CREATE TABLE example_suitability (
    id            INTEGER PRIMARY KEY,
    dataset_file  TEXT    NOT NULL,        -- canonical .jsonl this example lives in
    example_hash  TEXT    NOT NULL,        -- sha1 of normalized user+assistant content
    line_no       INTEGER NOT NULL,        -- 0-based line in dataset_file (materialize cursor)

    -- Derived content facts:
    derived_role  TEXT    NOT NULL,        -- boss|coder|tester|researcher|uidev|mixed|unknown
    polarity      TEXT    NOT NULL DEFAULT 'positive',  -- positive (do-this gold) | negative (avoid-this failure)
    language      TEXT,                    -- Python|C#|… (from metadata or sniffed), nullable
    task_type     TEXT,                    -- bugfix|refactor|tests|feature_plan|… nullable
    domain        TEXT,                    -- coarse topic: network-security|auth|payments|… (keyword-derived v1), nullable
    declared_source TEXT,                  -- cerebras_synthetic|swarm_capture|… (provenance)
    declared_target TEXT,                  -- what the generator claimed (for gap detection)

    -- Per-target fitness 0..100 (the scorer fills every column for every row):
    boss_fit        INTEGER NOT NULL DEFAULT 0,
    coder_fit       INTEGER NOT NULL DEFAULT 0,
    tester_fit      INTEGER NOT NULL DEFAULT 0,
    researcher_fit  INTEGER NOT NULL DEFAULT 0,
    uidev_fit       INTEGER NOT NULL DEFAULT 0,

    -- Cheap boolean rubric gates (reused from eval_adapter.score_plan):
    valid_json      INTEGER NOT NULL DEFAULT 0,
    roles_valid     INTEGER NOT NULL DEFAULT 0,
    files_named     INTEGER NOT NULL DEFAULT 0,
    no_tester_write INTEGER NOT NULL DEFAULT 0,

    indexed_at    TEXT    NOT NULL,
    UNIQUE(dataset_file, example_hash)
);
CREATE INDEX ix_es_role   ON example_suitability(derived_role, language);
CREATE INDEX ix_es_file    ON example_suitability(dataset_file);
CREATE INDEX ix_es_bossfit ON example_suitability(boss_fit);
CREATE INDEX ix_es_hash    ON example_suitability(example_hash);   -- cross-file dedup
```

Registered in `Migrations.All` as `new Migration(7, "example suitability", Sql007_Suitability)`.

`example_hash` enables **cross-file dedup** (the same example promoted into
multiple files counts once in a virtual set) and is the join key for "is this
example already in my eval set?" leakage checks.

---

## The suitability scorer (`score_suitability.py`)

A generalization of `eval_adapter.score_plan`. For each example it returns a
**vector**, not a label — because `no_tester_write` is a *negative* signal for
boss and a *positive* signal for tester, computed from the same content.

```python
def score_example(messages, meta) -> dict:
    plan = parse_first_json(assistant_text(messages))
    tasks = plan.get("tasks", []) if plan else []
    gates = rubric_gates(plan, tasks)          # valid_json, roles_valid, files_named, no_tester_write
    roles = [resolve_lane(t.get("role","")) for t in tasks]  # mirror ParseBossPlan alias map

    return {
      "derived_role": dominant_or_mixed(roles),
      "language":     meta.get("language"),
      "task_type":    meta.get("task_type"),
      "declared_target": meta.get("base_model_target") or meta.get("category"),
      # per-target fitness — opposite use of the same signals:
      "boss_fit":   fit_boss(gates, roles),       # needs valid plan, files named, NO tester-write
      "tester_fit": fit_tester(gates, roles, tasks), # needs a TESTER lane writing real tests
      "coder_fit":  fit_coder(gates, roles, tasks),  # CODER lane producing code in `language`
      "researcher_fit": fit_researcher(roles, tasks),
      "uidev_fit":  fit_uidev(roles, tasks),
      **gates,
    }
```

- **`resolve_lane`** is the *exact* alias map from
  `SwarmSession.ParseBossPlan` (RESEARCHER/ARCHITECT/PLANNER/REVIEWER/ANALYST →
  researcher; FRONTEND*/UI → uidev; QA/QUALITY_ASSURANCE → tester; everything
  else → coder). Keep these in sync — a shared `roles.json` the C# alias map and
  the Python scorer both read is the v2 hardening (out of scope here, noted).
- Output: `datasets/<name>.suitability.jsonl`, one row per example, sibling to
  the canonical file. Re-runnable, deterministic, no model calls.
- CLI: `python training_pit/tools/score_suitability.py [--only <glob>] [--db <path>]`.
  Default writes artifacts only; `--db` optionally direct-writes for headless runs.

---

## SQL ingestion (`MetadataImporter.ImportSuitability`)

Mirrors the existing capture-import path. Reads each `*.suitability.jsonl`,
upserts rows keyed on `(dataset_file, example_hash)`, stamps `indexed_at`, then
prunes rows older than the scan timestamp for that file (deleted examples drop
out — same `PruneOlderThan` pattern as `DatasetRepository`).

---

## Query surface (`SuitabilityRepository`)

The browser is three parameterized queries. All read-only, all via
`RepositoryBase.Query`/`Scalar`.

| Method | SQL shape | Powers |
|---|---|---|
| `CountForTarget(SuitabilityQuery q)` | `SELECT COUNT(*) … WHERE {role}_fit >= :floor AND (lang) AND (task)` | the "1,134 examples match" headline |
| `DatasetsForTarget(q)` | `… GROUP BY dataset_file` → per-file matched count + fit band | the dataset cards |
| `MatchingExamples(q)` | `SELECT dataset_file, line_no, example_hash …` | materialization cursor |

```csharp
public sealed record SuitabilityQuery(
    string Target,            // "boss" | "coder" | "tester" | "researcher" | "uidev"
    int    FitFloor = 70,     // min {target}_fit
    string? Language = null,  // null = any
    string? TaskType = null,
    bool   DedupByHash = true);
```

`{Target}` maps to the `{target}_fit` column via a fixed switch (no string
interpolation into SQL — the column name comes from an allow-list, args are
parameters).

---

## Virtual datasets (`VirtualDatasetService`)

"Build virtual training set" = run `MatchingExamples(q)`, dedup by
`example_hash`, read those exact lines back from the **canonical** `.jsonl`
files, then write a fresh pair honoring the registry convention:

```
training_pit/datasets/train_<KEY>.jsonl
training_pit/datasets/eval_<KEY>.jsonl
```

where `<KEY>` encodes the query (e.g. `worker_tester_py`). Stratified split
(reuse `finalize_training_set.py` logic). **Leakage guard:** no `example_hash`
may appear in both train and eval; cross-check against any existing
`eval_*.jsonl` selected as held-out. The new pair is then a normal Forge input —
the Training Pit's existing train flow is unchanged.

This is the payoff: the 794 tester examples that *poisoned* boss v2 become, with
two clicks, `train_worker_tester.jsonl` — ORC ACADEMY's next adapter, for free.

---

## Browser UX (Avalonia Training Pit)

1. **Training target** — segmented control: Boss · Worker · Judge(future).
2. If Worker → **Worker type** dropdown: Coder · Tester · Researcher · UI developer · Reviewer(future).
3. **Filters** — language, task type, quality/fit floor.
4. **Headline** — `CountForTarget` result: "N examples across M datasets match."
5. **Dataset cards** — `DatasetsForTarget`: name, provenance, fit bar
   (`matched / total`), and the **declared-vs-derived gap flag** when
   `declared_target != target` (the "declared boss, content is tester" warning).
6. **Actions** — Preview examples · Build virtual training set → Forge.

Mockup rendered in the design session 2026-06-17.

---

## Plain-language purpose lines

Every dataset (and every filtered view) gets a one-sentence, human-readable
description so a user never has to decode `cerebras[api].synthetic.boss.1534`.
The line is **computed on read** from the derived facts — deterministic, always
fresh, never hand-written and never stale.

`PurposeLine(card)` assembles four slots:

```
{Polarity} {provenance} that teach {target_phrase}{domain/task qualifier}.
```

| Slot | Source | Examples |
|---|---|---|
| Polarity | `polarity` | "Examples" (positive) · "Negative examples of" (negative) |
| Provenance | `declared_source` | "synthetically generated prompts" · "real captured swarm runs" |
| target_phrase | `derived_role` | see map below |
| qualifier | `domain` / `language` / `task_type` | "in Python" · "for network-security code" · "focused on bug-fixing" |

`target_phrase` map (the plain-English heart):

| derived_role | phrase |
|---|---|
| boss | "the Boss model (Gemma 4 12B) to break a goal into tasks and delegate each to the right worker" |
| coder | "a Worker model to write implementation code from a delegated task" |
| tester | "a Worker model to verify and review code without writing production files" |
| reviewer | "a Worker model to act as a code reviewer" |
| researcher | "a Worker model to investigate APIs and docs without writing code" |
| uidev | "a Worker model to build UI and styling" |

Produced lines (real cases from the current corpus):

- *"Synthetically generated prompts that teach the Boss model (Gemma 4 12B) to
  break a goal into tasks and delegate each to the right worker."*
- *"Examples that teach a Worker model to verify and review code without writing
  production files — drawn from the same data that over-taught the Boss."*
- *"Negative examples of network-security mistakes, used to teach the model what
  to avoid."* (polarity=negative, domain=network-security)

`domain` in v1 is a lightweight keyword pass over the goal text
(`security|auth|payment|database|concurrency|…` → coarse bucket); richer topic
labels are the v2 embedding job. Polarity comes free from `captures.example_class`
/ `failure_mode` for captured data and from the generator tag for synthetic.

---

## Pit Boss coverage awareness (the advisor)

Pit Boss ([[pit-boss-feature]]) stops opening with a blank "what do you want to
train?" and instead opens with **what you have and what's missing**. It consults
a coverage matrix and suggests the highest-value gap to fill.

### Coverage matrix

Rows = training targets (Boss, Coder, Tester, Researcher, UIDeveloper,
Reviewer…). Columns = the languages/domains the system actually operates in.
Each cell is a coverage band over `example_suitability`:

| Band | Rule | Source |
|---|---|---|
| none | 0 matching examples | — |
| thin | < 150 (below the v1 gate floor) | [[dataset-size-targets]] |
| adequate | 150–800 | |
| strong | > 800 with an eval split | |

### Demand vs supply — the gap engine

A gap is **demand the corpus doesn't supply**:

- **Supply** = `example_suitability` coverage bands (what we can already train).
- **Demand** = what TheOrc / the Swarm / the HIVEMIND have actually *touched*:
  - `captures.domain` + keyword pass over `captures.goal` — real swarm jobs.
  - `hive_tasks.role` / `title` — distributed worker history ([[hive-mind-phase3]]).
  - CodeGraph namespaces + languages ([[code-graph-theory]]) — what the open
    project *is* about.
- **Gap** = `demand present AND supply ∈ {none, thin}`, ranked by demand volume.

### Cold start → gets smarter

- **No project history** (fresh install): Pit Boss suggests the **base roster** —
  Boss, Coder, Tester, Researcher, UIDeveloper — each as a "generate a starter
  set" card. Always a sensible default.
- **With history**: suggestions become project-specific. *"The swarm has fixed 41
  Python bugs and run 23 review tasks, but you have no Worker·Reviewer dataset
  (0 examples). Generate a Reviewer starter set?"* The more the swarm works, the
  sharper the advice — coverage is a side effect of usage.

A suggestion is a pre-filled Pit Boss plan: target, role, language/domain, and a
recommended example count to clear the next band. Accepting it drops straight
into the existing 8-question wizard → dataset gen → Forge flow, or — when the
gap is fillable from data we already have — into `VirtualDatasetService` instead
of new generation.

`CoverageService` exposes `Matrix()`, `Gaps()`, and `SuggestNext()`; Pit Boss
calls `SuggestNext()` on open.

---

## Non-goals (v1)

- Embedding/semantic similarity over examples (→ v2, reuse CodeGraph's Ollama path).
- A shared `roles.json` unifying the C# alias map and Python scorer (recommended
  v2 hardening; v1 keeps them in sync by hand with a test).
- HIVE-distributed scoring (→ v3).
- Editing examples in-app (the .jsonl stays canonical; edits happen in tools).

---

## Build order (review each increment via tools/codex-review.ps1)

1. **Migration v7 + `SuitabilityRepository` + `ExampleSuitabilityRecord`**
   (+ unit tests over in-memory DB, mirroring existing repo tests).
2. **`score_suitability.py`** + golden test: re-derive the v2 audit numbers
   (cerebras 57.2% tester-write, 794 tester-gold) from the table, proving parity
   with the manual audit.
3. **`MetadataImporter.ImportSuitability`** + wire into the dataset index pass.
4. **`VirtualDatasetService`** materialize + leakage guard + stratified split.
5. **Purpose lines** (`PurposeLine` computed-on-read) + **`CoverageService`**
   (`Matrix`/`Gaps`/`SuggestNext`) + unit tests over the demand/supply join.
6. **Avalonia content-aware browser** bound to the queries; cards show the
   plain-language purpose line; Pit Boss opens on `SuggestNext()`.
7. **First real payoff:** materialize `train_worker_tester` from existing data,
   register the query, hand to Forge — ORC ACADEMY v4 (Tester worker).
