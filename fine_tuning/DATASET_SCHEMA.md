# Boss Planning Dataset — Schema Specification

> **Schema version:** 1.0
> **Status:** Defined, not yet populated by tooling.
> Data collection starts in Phase 2. See `README.md` for timeline.

---

## Overview

Each training example is a single JSON file representing one boss planning interaction:
a user's coding goal → the boss's decomposition plan → a quality assessment.

Two classes of examples are needed:
- **Positive examples** (`quality_score ≥ 70`): what a good plan looks like
- **Negative examples** (`quality_score < 40`): the failure modes the model must unlearn

---

## Top-Level Schema

```jsonc
{
  // ── Identity ──────────────────────────────────────────────────────────────
  "schema_version": "1.0",                // bump when schema changes
  "example_id": "ex_20260609_001",        // unique ID: ex_YYYYMMDD_NNN
  "run_id": "20260609_135107",            // swarm run ID from .orc/swarm/runs/
  "captured_at": "2026-06-09T13:51:07Z", // ISO 8601 UTC

  // ── Source ────────────────────────────────────────────────────────────────
  "source": "swarm_run",     // "swarm_run" | "manual" | "synthetic"
  "boss_model": "qwen2.5-coder:14b",   // model that produced this plan
  "benchmark_id": "CleanCSV",          // benchmark name if from a benchmark run, else null

  // ── Input ─────────────────────────────────────────────────────────────────
  "goal": "Build a CSV file cleaner tool with a Python tkinter GUI",
  "domain": "python_desktop",   // See domain taxonomy below
  "difficulty": 2,              // 1=simple, 2=moderate, 3=complex, 4=multi-system

  // ── Output ────────────────────────────────────────────────────────────────
  "plan": {
    // The raw JSON the boss produced (parsed into object, not string)
    "plan": "Build with Python/Tkinter: researcher gathers library docs, two coders split backend and UI.",
    "tasks": [
      {
        "role": "RESEARCHER",
        "priority": 1,
        "title": "Research pandas and tkinter file dialog APIs",
        "description": "Investigate: (1) pandas read_csv and DataFrame cleaning..."
      }
      // ... additional tasks
    ]
  },

  // ── Evaluation ────────────────────────────────────────────────────────────
  "quality_score": 85,          // composite 0–100, computed via EVAL_RUBRIC.md
  "rubric_scores": {            // per-dimension scores (see EVAL_RUBRIC.md)
    "task_count":        20,    // 0–20
    "description_depth": 25,    // 0–25
    "filename_presence": 15,    // 0–15
    "api_contract":      15,    // 0–15
    "domain_accuracy":   10,    // 0–10 (manual or heuristic)
    "json_validity":     15     // 0–15 (auto)
  },
  "example_class": "positive",  // "positive" | "negative" | "marginal"

  // ── Annotations ───────────────────────────────────────────────────────────
  "notes": "Strong decomposition. Researcher precedes coders correctly. API contract consistent.",
  "annotator": "auto",          // "auto" | "human:<initials>"
  "tags": ["well-decomposed", "correct-api-contract", "3-priority-levels"]
}
```

---

## Domain Taxonomy

Use one of these values for the `domain` field:

| Value | Description |
|---|---|
| `python_desktop` | Python + tkinter/PyQt/wx desktop apps |
| `python_cli` | Python command-line tools and scripts |
| `python_web` | Python web servers (FastAPI, Flask, Django) |
| `python_data` | Data processing, pandas, CSV, ETL |
| `python_ml` | Machine learning and model inference |
| `csharp_wpf` | C# WPF desktop applications |
| `csharp_api` | C# ASP.NET Web API / minimal API |
| `typescript_web` | TypeScript React / Next.js frontend |
| `typescript_node` | TypeScript Node.js backend |
| `fullstack` | Multi-language / frontend + backend |
| `devops` | CI/CD, Docker, deployment scripts |
| `general` | Domain doesn't fit a specific category |

---

## Difficulty Scale

| Level | Criteria |
|---|---|
| 1 — Simple | Single output file, no dependencies between tasks, <3 roles needed |
| 2 — Moderate | 2–4 files, clear researcher→coder dependency, standard libraries |
| 3 — Complex | 4+ files, cross-task API contracts, multiple integration points |
| 4 — Multi-system | Frontend + backend + DB, deployment config, cross-language |

---

## File Naming Convention

```
examples/
  {class}_{domain}_{NNN}.json
```

Examples:
- `good_python_data_001.json`
- `good_python_web_002.json`
- `bad_single_empty_task_001.json`   ← negative example, gemma4 collapse pattern
- `marginal_missing_filename_001.json`

Production datasets (not committed to git):
```
datasets/
  train_v1.jsonl     ← JSONL, one example per line, ≥150 examples
  eval_v1.jsonl      ← JSONL, held-out set, ≥30 examples
  negative_v1.jsonl  ← JSONL, failure modes only
```

---

## Auto-Capture Hook (Future)

When Phase 2 starts, add a capture trigger in `SwarmSession.RunBossDecomposeAsync`:

```csharp
// After ParseBossPlan succeeds and tasks.Count >= 2:
// 1. Score the plan using EvalRubric.Score(tasks, userGoal)
// 2. If score >= AUTO_CAPTURE_THRESHOLD (70), serialize to dataset staging dir
// 3. Log example_id in swarm trace for traceability
if (score >= AutoCaptureThreshold && _captureEnabled)
    await DatasetCapture.StageExampleAsync(runId, userGoal, raw, tasks, score);
```

The `DatasetCapture` class does not exist yet — this is a placeholder showing where
it hooks in. Do not implement until Phase 2.

---

## Negative Example Capture

The gemma4:12b collapse pattern (single task with empty description) is a high-value
negative example. The `IsBossUnderPlanned()` check already identifies these. When Phase 2
starts, failed plans that are retried should be staged as negative examples alongside
the successful retry as a positive.

---

## Schema Evolution

When fields need to change:
1. Bump `schema_version` in all new examples
2. Old examples keep their original version number
3. Any dataset loader must handle both versions
4. Add a migration note in this file under `## Version History`

## Version History

| Version | Date | Changes |
|---|---|---|
| 1.0 | 2026-06-09 | Initial schema |
