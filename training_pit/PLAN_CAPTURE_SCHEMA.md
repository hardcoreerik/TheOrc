# The Training Pit — Plan Capture Schema

> **Schema version:** 1.0
> **Status:** Defined. Not yet auto-populated (DatasetCapture.cs not built).
>
> This is a **specialized** format for capturing boss/swarm planning outputs, plan quality
> scores, failure modes, and DPO/ORPO contrastive pairs.
>
> It is NOT the canonical training format. For the format that gets fed to a LoRA trainer,
> see `DATASET_SCHEMA.md`. Plan captures can be converted to chat JSONL for SFT training
> or used as preference pairs for DPO/ORPO (future work — not yet implemented).

---

## What Plan Captures Are For

A plan capture records a single boss planning interaction:
`user goal → boss JSON plan → rubric score → quality class`

They serve three purposes:
1. **Data mining** — find high-quality plans to convert to chat-JSONL training examples
2. **Failure documentation** — record the collapse patterns the model must unlearn
3. **DPO/ORPO contrastive pairs** — pair a good and bad plan on the same goal for
   preference tuning (hypothesis; confirmed effectiveness requires implementation and testing)

---

## Schema

```jsonc
{
  // ── Identity ─────────────────────────────────────────────────────────────
  "schema_version": "1.0",
  "example_id": "ex_20260609_001",        // ex_YYYYMMDD_NNN
  "run_id": "20260609_135107",            // swarm run ID from .orc/swarm/runs/
  "captured_at": "2026-06-09T13:51:07Z",

  // ── Source ───────────────────────────────────────────────────────────────
  "source": "swarm_run",          // "swarm_run" | "manual" | "synthetic"
  "boss_model": "qwen2.5-coder:14b",
  "benchmark_id": "CleanCSV",     // benchmark name if applicable, else null

  // ── Input ────────────────────────────────────────────────────────────────
  "goal": "Build a CSV file cleaner tool with a Python tkinter GUI",
  "domain": "python_desktop",
  "difficulty": 2,                // 1=simple 2=moderate 3=complex 4=multi-system

  // ── Output ───────────────────────────────────────────────────────────────
  "plan": {
    "plan": "one-sentence approach",
    "tasks": [/* ... */]
  },

  // ── Evaluation ───────────────────────────────────────────────────────────
  "quality_score": 85,            // 0–100 composite (see EVAL_RUBRIC.md)
  "rubric_scores": {
    "task_count":        20,
    "description_depth": 25,
    "filename_presence": 15,
    "api_contract":      15,
    "domain_accuracy":   10,
    "json_validity":     15
  },
  "example_class": "positive",   // "positive" | "negative" | "marginal"

  // ── Annotations ──────────────────────────────────────────────────────────
  "failure_mode": null,           // null | "single_empty_task_collapse" | "off_domain" | "json_invalid" | "hallucinated_structure"
  "correct_plan_reference": null, // example_id of the correct plan (for contrastive pairs)
  "notes": "",
  "annotator": "auto",            // "auto" | "human:<initials>"
  "tags": []
}
```

---

## Domain Taxonomy

| Value | Description |
|---|---|
| `python_desktop` | Python + tkinter/PyQt/wx desktop apps |
| `python_cli` | Python command-line tools and scripts |
| `python_web` | Python web servers (FastAPI, Flask, Django) |
| `python_data` | Data processing, pandas, CSV, ETL |
| `python_ml` | Machine learning and model inference |
| `csharp_wpf` | C# WPF desktop applications |
| `csharp_api` | C# ASP.NET Web API |
| `typescript_web` | TypeScript React / Next.js frontend |
| `typescript_node` | TypeScript Node.js backend |
| `fullstack` | Multi-language / frontend + backend |
| `devops` | CI/CD, Docker, deployment scripts |
| `general` | Domain doesn't fit a specific category |

---

## Failure Modes

| Value | Description |
|---|---|
| `single_empty_task_collapse` | Boss produces one task titled "Execute goal" with empty description |
| `off_domain` | Tasks solve a different problem than the goal (Combo D web scraper pattern) |
| `json_invalid` | Output is not parseable JSON |
| `hallucinated_structure` | Tasks reference files/functions that cannot exist |
| `over_decomposition` | 5+ tasks with significant overlap |

---

## Auto-Capture Hook (Phase 2)

When Phase 2 starts, add to `SwarmSession.RunBossDecomposeAsync`:

```csharp
// After ParseBossPlan() succeeds:
// File: OrchestratorIDE/Services/Swarm/DatasetCapture.cs (NOT BUILT YET)
var score = EvalRubric.Score(tasks, userGoal).Composite;
if (score >= AutoCaptureThreshold || score <= NegativeCaptureThreshold)
    await DatasetCapture.StageExampleAsync(runId, userGoal, raw, tasks, score);
```

Constants (planned, not enforced yet):
- `AutoCaptureThreshold = 70` — stages as positive example
- `NegativeCaptureThreshold = 39` — stages as negative example

---

## Converting to Chat JSONL

A plan capture can be converted to a chat-JSONL training example:

```
plan_capture.goal           → messages[user].content   ("Goal: " + goal)
plan_capture.plan (JSON)    → messages[assistant].content
boss system prompt          → messages[system].content  (BossDecomposeSystemPrompt)
```

Metadata mapping:
- `category` = `"boss_planning"`
- `source` = `"swarm_capture"`
- `quality` = `"gold"` if score ≥ 90, `"silver"` if score 70–89

Do not convert marginal (40–69) or negative captures to chat JSONL.

---

## Contrastive Pairs for DPO/ORPO

Good/bad plan pairs on the same goal can be used for DPO or ORPO preference training.
The `correct_plan_reference` field links the negative example to its correct counterpart.

**This is future work. DPO/ORPO is not yet implemented.**

The claim that "~20 high-quality contrastive pairs significantly reduce collapse patterns"
is a reasonable hypothesis based on published DPO results on structured output tasks,
but it is **not a guaranteed outcome** for this specific model and task. It should be
treated as an implementation target and verified with before/after evals once implemented.

---

## File Naming

```
examples/
  plan_capture_good_NNN.json   — positive examples (score ≥ 70)
  plan_capture_bad_NNN.json    — negative examples (score ≤ 39)
  plan_capture_marginal_NNN.json — marginal (40–69, for review only)
```

---

## Version History

| Version | Date | Changes |
|---|---|---|
| 1.0 | 2026-06-09 | Extracted from DATASET_SCHEMA.md; reframed as specialized capture format |
