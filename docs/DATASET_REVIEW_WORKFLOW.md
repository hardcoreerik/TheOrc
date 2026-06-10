# TheOrc — Dataset Review Workflow

> **Status:** Phase 2 ACTIVE — Data collection in progress.
> This document describes the manual review process for promoting swarm captures
> to training data. No training job should be launched until Phase 3 is explicitly unblocked
> (requires ≥150 reviewed positive examples in `train_v1.jsonl`).

---

## Overview

Swarm boss plans are auto-staged by `DatasetCapture.cs` during live runs.
Staged captures must be reviewed before they become training data.
This document is the step-by-step procedure.

---

## Step 1 — Check What's Staged

```powershell
# List staged captures
Get-ChildItem .orc/swarm/dataset-staging/
```

Two types of files are staged:
- `plan_capture_good_<runId>_<score:D3>.json` — score ≥ 70, candidate positive
- `plan_capture_bad_<runId>_<score:D3>.json` — score ≤ 39, candidate negative

Score 40–69 captures are never staged (too marginal for clean training signal).

---

## Step 2 — Convert Staged Captures to Chat-JSONL

```bash
# Run from the repo root (in WSL2 or PowerShell with Python installed)
python training_pit/scripts/convert_plan_captures.py
```

Output lands in `training_pit/datasets/staging/converted_<timestamp>.jsonl`.

Each line in the output is one chat example in canonical format:

```json
{"messages":[
  {"role":"system","content":"<BOSS_SYSTEM_PROMPT>"},
  {"role":"user","content":"Goal: <goal text>"},
  {"role":"assistant","content":"{\"plan\":\"...\",\"tasks\":[...]}"}
],"metadata":{
  "category":"boss_planning",
  "source":"swarm_capture",
  "quality":"silver",
  "contains_sensitive_data":false,
  "base_model_target":"gemma4:12b",
  "created_by":"auto",
  "notes":"..."
}}
```

---

## Step 3 — Validate and Sanitize

Both scripts must pass before any example is promoted.

```bash
python training_pit/scripts/validate_dataset.py \
  training_pit/datasets/staging/converted_<ts>.jsonl

python training_pit/scripts/sanitize_dataset.py \
  training_pit/datasets/staging/converted_<ts>.jsonl
```

Fix any errors before continuing. Do not promote a file with validation errors.

### Valid field values

| Field | Valid values |
|---|---|
| `quality` | `gold`, `silver`, `draft`, `rejected` |
| `source` | `manual`, `corrected_model_output`, `terminal_log`, `repo_issue`, `swarm_capture`, `eval_failure`, `synthetic`, `imported` |
| `category` | `boss_planning`, `debugging`, `delegation`, `minimal_patching`, `powershell`, `esp_idf`, `ollama`, `openclaw`, `continue_config`, `react_dashboard`, `python_utility`, `validation_commands`, `uncertainty_handling`, `hallucination_resistance`, `code_review`, `imported_adapter_eval` |

> **Do not use** `good`, `edge`, or `bad` as quality values — these are not supported.

---

## Step 4 — Manual Review of Each Example

Open the converted JSONL and review each example line by line.

### Promote (keep) if all of these are true

- [ ] The user goal is **specific** — not a vague one-liner like "build an app"
- [ ] The assistant response is **valid JSON** — no markdown fences, no preamble text
- [ ] The plan has **2–4 tasks** — single-task collapse (1 task) disqualifies the example
- [ ] Each task description is **≥ 60 characters** — short descriptions carry no training signal
- [ ] CODER/UIDEVELOPER tasks reference **named output files** (≥ 75% of such tasks)
- [ ] No **hallucinated modules or APIs** — referenced names must be real things
- [ ] No **sensitive data** — file paths, IP addresses, passwords, credentials, usernames
- [ ] The plan **makes sense** as a decomposition of the stated goal

### Demote (reject or reclassify) if any of these are true

- The goal is too generic or hypothetical
- The plan is a single Execute task
- The assistant's response is not JSON (prose, markdown, chat)
- Any task description is a vague placeholder
- Hallucinated API or library names appear
- Contains real machine paths, credentials, or identifiable information

---

## Step 5 — Set Quality Labels

Edit the `quality` field for each example before promoting:

| Score | Label | Meaning |
|---|---|---|
| ≥ 90 composite | `gold` | Exemplary — clear goal, tight plan, named files, no gaps |
| 70–89 | `silver` | Good — usable training signal with minor imperfections |
| Below 70 | `draft` | Acceptable structure but too noisy; hold for future review |
| Failure / collapse | `rejected` | Never promote to `train_v1.jsonl`; move to `negative_v1.jsonl` |

Auto-captures start with `quality: "silver"` from the converter. Upgrade to `gold` if the
plan meets all quality bar criteria and you would be proud to show it as a reference example.

---

## Step 6 — Promote to Train or Negative Dataset

**For positive examples (quality: gold or silver):**

```bash
# Review the staging file first, then append selectively
# Do NOT blindly append the entire converted file
cat training_pit/datasets/staging/converted_<ts>.jsonl \
  >> training_pit/datasets/train_v1.jsonl
```

> ⚠️ **Do not blindly append.** Edit out rejected examples first. Remove any example
> that fails the review checklist. Only promote what you have personally read and approved.

**For collapse / failure examples (quality: rejected):**

```bash
cat training_pit/datasets/staging/converted_<ts>.jsonl \
  >> training_pit/datasets/negative_v1.jsonl
```

---

## Step 7 — Run Final Validation on the Dataset

After promoting, re-validate the full dataset file:

```bash
python training_pit/scripts/validate_dataset.py \
  training_pit/datasets/train_v1.jsonl

python training_pit/scripts/sanitize_dataset.py \
  training_pit/datasets/train_v1.jsonl
```

Both must pass 0 errors and 0 rejects before the dataset is used in training.

---

## Phase 3 Gate

Training is blocked until all of these conditions are met:

| Condition | Required | Status |
|---|---|---|
| Reviewed positive examples in `train_v1.jsonl` | ≥ 150 | 0 / 150 |
| Reviewed negative examples in `negative_v1.jsonl` | ≥ 25 | 0 / 25 |
| Eval prompts in `evals/` | ≥ 20 | Met |
| `validate_dataset.py` passes on `train_v1.jsonl` | 0 errors | N/A (file not yet created) |
| `sanitize_dataset.py` passes on `train_v1.jsonl` | 0 rejects | N/A (file not yet created) |

Do not start Phase 3 until the gate is met. See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md).

---

## Hand-Authored Golden Examples

You can also write examples directly without going through auto-capture.

Requirements:
1. System message must use the exact `BOSS_SYSTEM_PROMPT` from `convert_plan_captures.py`
2. User message format: `"Goal: <goal text>"` or `"<context>\n\nGoal: <goal text>"`
3. Assistant message: compact JSON, no fences, no preamble
4. Metadata: `source="manual"`, `quality="gold"` or `"silver"`, `created_by="user"`
5. Run validate + sanitize before promoting

See `training_pit/examples/chat_sft_good_*.jsonl` for reference examples.

---

## Negative / Collapse Examples

Use these for eval regression testing only — **never** for SFT training.

- Tag `source: "synthetic"` or `source: "eval_failure"`, `quality: "rejected"`
- Note in `"notes"` what failure mode this represents
- Promote to `training_pit/datasets/negative_v1.jsonl`

See `training_pit/examples/chat_sft_eval_collapse_001.jsonl` for an example.
