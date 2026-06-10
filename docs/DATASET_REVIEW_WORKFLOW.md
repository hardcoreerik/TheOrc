# TheOrc — Dataset Review Workflow

> **Status:** Phase 2.5 ACTIVE — Dataset Review / Approval Valve is live.
> Use `review_captures.py` (not manual cat-append) to promote captures to training data.
> Phase 3 training is **BLOCKED** until Phase 3 gate thresholds are met.
> Do not start Phase 3 until `--status` shows ALL GATES MET.

---

## Overview

Swarm boss plans are auto-staged by `DatasetCapture.cs` during live runs.
Staged captures must be reviewed and explicitly approved before they become training data.
The approval valve (`review_captures.py`) enforces this — it validates, sanitizes,
and writes JSONL files atomically, and refuses to export if any gate check fails.

**No example enters `train_v1.jsonl` without passing this pipeline.**

---

## Quick Reference

```powershell
# Run from the repo root (PowerShell or WSL2 bash)

# See what's staged and what's been reviewed
python training_pit/scripts/review_captures.py --list

# Full detail on one capture (plan, validation, sanitizer results)
python training_pit/scripts/review_captures.py --inspect .orc/swarm/dataset-staging/plan_capture_good_<runId>_<score>.json

# Approve for training (quality must be gold or silver for the train split)
python training_pit/scripts/review_captures.py --approve .orc/swarm/dataset-staging/plan_capture_good_<runId>_<score>.json \
    --split train --quality silver --note "Good decomp, correct filenames"

# Reject a capture
python training_pit/scripts/review_captures.py --reject .orc/swarm/dataset-staging/plan_capture_bad_<runId>_<score>.json \
    --note "Collapse pattern — single empty task"

# Export to JSONL (atomic: validate + sanitize gate runs before write)
python training_pit/scripts/review_captures.py --export-train
python training_pit/scripts/review_captures.py --export-eval
python training_pit/scripts/review_captures.py --export-negative

# Phase 3 gate counters
python training_pit/scripts/review_captures.py --status
```

---

## Step-by-Step Workflow

### Step 1 — Check What's Staged

```powershell
python training_pit/scripts/review_captures.py --list
```

Output shows each capture with its manifest status:

```
FILE                                          SCORE CLASS     STATUS    SPLIT     QUALITY
plan_capture_good_20260601_135107_085.json       85 positive  pending   -         -
plan_capture_bad_20260601_140200_005.json          5 negative  pending   -         -
```

Two types of files are staged by `DatasetCapture.cs`:
- `plan_capture_good_<runId>_<score:D3>.json` — score ≥ 70, candidate positive
- `plan_capture_bad_<runId>_<score:D3>.json` — score ≤ 39, candidate negative

Score 40–69 captures are never staged (too marginal for clean training signal).

---

### Step 2 — Inspect a Capture

```powershell
python training_pit/scripts/review_captures.py --inspect \
    .orc/swarm/dataset-staging/plan_capture_good_<runId>_<score>.json
```

`--inspect` shows:
- Full capture JSON (goal, plan, rubric scores, failure mode, notes)
- Current manifest status (approved / rejected / pending)
- Conversion preview (what the chat-JSONL example will look like)
- Validation and sanitizer results on the converted example

Use this to make the approve/reject decision before touching the manifest.

---

### Step 3 — Review Checklist

**Approve (promote) if ALL of these are true:**

- [ ] The user goal is **specific** — not a vague one-liner like "build an app"
- [ ] The assistant response is **valid JSON** — no markdown fences, no preamble text
- [ ] The plan has **2–4 tasks** — single-task collapse (1 task) disqualifies
- [ ] Each task description is **≥ 60 characters** — short descriptions carry no training signal
- [ ] CODER/UIDEVELOPER tasks reference **named output files** (≥ 75% of such tasks)
- [ ] No **hallucinated modules or APIs** — referenced names must be real things
- [ ] No **sensitive data** — file paths, IP addresses, passwords, credentials, usernames
- [ ] The plan **makes sense** as a decomposition of the stated goal

**Reject if ANY of these are true:**

- The goal is too generic or hypothetical
- The plan is a single "Execute goal" task
- The assistant's response is not valid JSON
- Any task description is a vague placeholder
- Hallucinated API or library names appear
- Contains real machine paths, credentials, or identifiable data

---

### Step 4 — Approve or Reject

**Approve:**

```powershell
python training_pit/scripts/review_captures.py \
    --approve .orc/swarm/dataset-staging/plan_capture_good_<runId>_<score>.json \
    --split train \
    --quality silver \
    --note "Optional reviewer note"
```

**Reject:**

```powershell
python training_pit/scripts/review_captures.py \
    --reject .orc/swarm/dataset-staging/plan_capture_bad_<runId>_<score>.json \
    --note "Collapse pattern — single empty task"
```

**Approval is recorded in the manifest only.**
No JSONL file is written until you run `--export-*`. This is intentional —
you can approve a batch of captures and review the manifest before committing.

**Approval rules:**

| Split | Allowed quality values |
|---|---|
| `train` | `gold`, `silver` only |
| `eval` | `gold`, `silver`, `draft`, `rejected` |
| `negative` | `gold`, `silver`, `draft`, `rejected` |

#### Quality labels

| Score | Label | Meaning |
|---|---|---|
| ≥ 90 composite | `gold` | Exemplary — clear goal, tight plan, named files, no gaps |
| 70–89 | `silver` | Good — usable training signal with minor imperfections |
| Below 70 | `draft` | Acceptable structure but noisy; never goes in train split |
| Failure / collapse | `rejected` | Never in train; use for negative or eval split only |

Auto-captures start with `quality: "silver"` from the converter.
Upgrade to `gold` if the plan meets all quality criteria and you'd use it as a reference example.

> **Do not use** `good`, `edge`, or `bad` as quality values — these are not valid.

---

### Step 5 — Export to JSONL

After approving a batch, export the split(s) you updated:

```powershell
python training_pit/scripts/review_captures.py --export-train
```

The export pipeline (fail closed):

```
Approved manifest entries for split
        │
        ▼
Convert each capture → chat-JSONL (with reviewer quality override)
        │
        ▼
Write to .tmp file
        │
        ▼ validate_dataset.py — abort if any error
        │
        ▼ sanitize_dataset.py — abort if any REJECT pattern
        │
        ▼
Atomically replace train_v1.jsonl (only on full success)
```

If validation or sanitization fails, **the final file is left unchanged**.
Fix the flagged issues in the captures (or reject them) and retry.

---

### Step 6 — Check Phase 3 Gate

```powershell
python training_pit/scripts/review_captures.py --status
```

Output:

```
====================================================
  PHASE 2.5 — DATASET REVIEW STATUS
====================================================

  Staging captures found:   12
  Awaiting review:          8
  Reviewer-rejected:        2

  -- Phase 3 Gate ----------------------------------

  TRAIN      [##..................]   30/150   open
  EVAL       [####################]   20/20    GATE MET
  NEGATIVE   [########............]   10/25    open

  Phase 3 blocked. Need: 120 more train example(s), 15 more negative example(s).

  Manifest: training_pit\datasets\manifests\reviewed_v1.json
====================================================
```

**Do not start Phase 3 until all three gates show GATE MET.**

---

## Phase 3 Gate

Training is blocked until ALL of these conditions are met:

| Condition | Required | Status |
|---|---|---|
| Reviewed positive examples in `train_v1.jsonl` | ≥ 150 | 0 / 150 |
| Reviewed negative examples in `negative_v1.jsonl` | ≥ 25 | 0 / 25 |
| Eval prompts in `evals/` | ≥ 20 | Met |
| `validate_dataset.py` passes on `train_v1.jsonl` | 0 errors | Enforced by export gate |
| `sanitize_dataset.py` passes on `train_v1.jsonl` | 0 rejects | Enforced by export gate |

See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for the full Phase 3 decision framework.

---

## Hand-Authored Golden Examples

You can write examples directly without going through auto-capture.

Requirements:
1. System message must use the exact `BOSS_SYSTEM_PROMPT` from `convert_plan_captures.py`
2. User message format: `"Goal: <goal text>"` or `"<context>\n\nGoal: <goal text>"`
3. Assistant message: compact JSON, no fences, no preamble
4. Metadata: `source="manual"`, `quality="gold"` or `"silver"`, `created_by="user"`
5. Run `validate_dataset.py` and `sanitize_dataset.py` before promoting

To promote a hand-authored example, add it to a capture-like JSON in the staging dir
and run through the normal `--approve` / `--export-train` flow. Or append directly to
`train_v1.jsonl` and then run validate + sanitize manually to confirm it's clean.

See `training_pit/examples/chat_sft_good_*.jsonl` for reference examples.

---

## Negative / Collapse Examples

Negative captures (score ≤ 39) document failure modes for eval regression testing.
They are **never** used as SFT training examples (training on bad outputs teaches bad outputs).

**To promote a negative capture:**
```powershell
python training_pit/scripts/review_captures.py \
    --approve .orc/swarm/dataset-staging/plan_capture_bad_<runId>_<score>.json \
    --split negative \
    --quality rejected \
    --note "single_empty_task_collapse — gemma4:12b pattern"
python training_pit/scripts/review_captures.py --export-negative
```

---

## Valid Field Values

| Field | Valid values |
|---|---|
| `quality` | `gold`, `silver`, `draft`, `rejected` |
| `source` | `manual`, `corrected_model_output`, `terminal_log`, `repo_issue`, `swarm_capture`, `eval_failure`, `synthetic`, `imported` |
| `category` | `boss_planning`, `debugging`, `delegation`, `minimal_patching`, `powershell`, `esp_idf`, `ollama`, `openclaw`, `continue_config`, `react_dashboard`, `python_utility`, `validation_commands`, `uncertainty_handling`, `hallucination_resistance`, `code_review`, `imported_adapter_eval` |

> **Do not use** `good`, `edge`, or `bad` as quality values — these are not supported by `validate_dataset.py` and will fail the export gate.

---

## Manifest Location

The review manifest is tracked in git:

```
training_pit/datasets/manifests/reviewed_v1.json
```

It records every approve/reject decision with the reviewer, quality tier, split,
timestamp, and note. Commit the manifest after each review session.

Raw captures in `.orc/swarm/dataset-staging/` are gitignored and local-only.
The JSONL output files (`train_v1.jsonl`, `eval_v1.jsonl`, `negative_v1.jsonl`)
are also gitignored — they are rebuilt from the manifest on any machine.
