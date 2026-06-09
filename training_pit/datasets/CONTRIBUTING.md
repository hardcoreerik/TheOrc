# Contributing Training Examples

> **Status:** Phase 2 ACTIVE — data collection in progress.
> Phase 3 training is BLOCKED pending minimum dataset thresholds.
> See [DATASET_STRATEGY.md](../DATASET_STRATEGY.md) for the full strategy,
> source-tier rationale, and Phase 3 unblock conditions.

---

## What This Folder Contains

Production training data — the JSONL files that feed the LoRA fine-tuning job.
These files are **not committed to git** (they are local-only, may be large, and
contain model outputs that should not be versioned).

| File | Purpose | Status |
|------|---------|--------|
| `train_v1.jsonl` | Reviewed positive training examples | Not yet created |
| `eval_v1.jsonl` | Held-out eval split (never used for training) | Not yet created |
| `negative_v1.jsonl` | Failure-mode and collapse examples | Not yet created |

The `staging/` subfolder holds converted-but-not-yet-reviewed examples.
The `examples/` folder (sibling to this one) is committed — it contains a small
number of hand-curated reference examples that illustrate the schema and quality bar.

---

## Three-Tier Source Strategy

All examples in `train_v1.jsonl` must come from one of three tiers, in priority order:

### Tier 1 — Real TheOrc captures (~80% of training set)
Auto-staged by `DatasetCapture.cs` during live swarm runs.
Must be reviewed before promotion to training data.

### Tier 2 — Hand-authored golden examples (~15%)
Written by a human to cover specific TheOrc workflows.
Must use the exact production `BOSS_SYSTEM_PROMPT`.

### Tier 3 — Synthetic edge-case examples (~5%, eval-only)
Used for eval regression and negative testing only.
Tagged `"source": "synthetic"`, `"quality": "rejected"`.
**Never promote to `train_v1.jsonl`.**

See `DATASET_STRATEGY.md` for full rationale and coverage targets.

---

## Why No Public Datasets

Public coding instruction datasets (HuggingFace, ShareGPT, WizardCoder, etc.)
are intentionally excluded from the first Training Pit LoRA:

- They use the wrong prompt format (not the TheOrc `BOSS_SYSTEM_PROMPT → JSON plan` pattern)
- They contain single-task requests, not multi-agent decomposition
- They may contain the exact failure modes we are training out
- With a small dataset (150–200 examples), off-distribution examples have outsized dilution effect

Public datasets may be considered for later worker-goblin adapters, not for the boss planning adapter.

---

## Phase 3 Gate — Training Is Blocked Until

| Condition | Required |
|-----------|---------|
| Reviewed positive examples in `train_v1.jsonl` | ≥ 150 |
| Reviewed negative/collapse examples in `negative_v1.jsonl` | ≥ 25 |
| Fixed eval prompts in `evals/` | ≥ 20 (already met) |
| All training data: `validate_dataset.py` passed | 0 errors |
| All training data: `sanitize_dataset.py` passed | 0 rejects |

---

## How to Add Examples

### Method 1: Auto-capture from live swarm runs (primary path)

`DatasetCapture.cs` automatically stages boss plans after every swarm run:

- Score ≥ 70 → `plan_capture_good_<runId>_<score>.json` in `.orc/swarm/dataset-staging/`
- Score ≤ 39 → `plan_capture_bad_<runId>_<score>.json` in `.orc/swarm/dataset-staging/`
- Score 40–69 → silently skipped (marginal, too noisy)

To convert staged captures:

```bash
# Convert all good captures to chat-JSONL
python training_pit/scripts/convert_plan_captures.py

# Review the output in training_pit/datasets/staging/converted_<ts>.jsonl
# Validate and sanitize:
python training_pit/scripts/validate_dataset.py training_pit/datasets/staging/converted_<ts>.jsonl
python training_pit/scripts/sanitize_dataset.py training_pit/datasets/staging/converted_<ts>.jsonl

# After manual review, append to train_v1.jsonl:
cat training_pit/datasets/staging/converted_<ts>.jsonl >> training_pit/datasets/train_v1.jsonl
```

**Do not blindly append all converts.** Review each example for:
- Goal is specific enough to be a useful training signal
- Plan is actually good — correct roles, named files, useful descriptions
- No hallucinated APIs or imaginary module names
- No sensitive data (paths, IPs, credentials)

### Method 2: Hand-authored golden examples

Write examples directly as canonical chat-JSONL (`DATASET_SCHEMA.md` format).
Requirements:

1. System message must use the exact `BOSS_SYSTEM_PROMPT` from
   `training_pit/scripts/convert_plan_captures.py`
2. User message format: `"Goal: <goal text>"` or `"<error log>\n\nGoal: <goal text>"`
3. Assistant message: compact JSON (no fences, no preamble)
4. Metadata: `source="manual"`, `quality="gold"` or `"silver"`, `created_by="user"`
5. Run validate + sanitize before promoting

See `training_pit/examples/chat_sft_good_*.jsonl` for reference examples.

### Method 3: Negative / collapse examples

Use for eval regression only — never for SFT training.

- Tag `"source": "synthetic"`, `"quality": "rejected"`
- Note in `"notes"` that this is eval-only and what failure mode it represents
- Store in `training_pit/examples/` for reference, or promote to `negative_v1.jsonl`

See `training_pit/examples/chat_sft_eval_collapse_001.jsonl` and
`training_pit/examples/chat_sft_synthetic_001.jsonl` for examples.

---

## Quality Bar for Positive Examples

| Criteria | Requirement |
|----------|-------------|
| Rubric score (composite) | ≥ 70 for silver, ≥ 90 for gold |
| Task count | 2–4 tasks (1 is collapse; 5+ is over-decomposition) |
| Description length per task | ≥ 60 chars |
| Filename in title | ≥ 75% of CODER/UIDEVELOPER tasks |
| API contract | shared types/function names consistent across tasks |
| Valid JSON | Required — no fences, no preamble |
| System prompt | Must be `BOSS_SYSTEM_PROMPT` exactly |
| Sensitive data | None (must pass sanitize_dataset.py with 0 rejects) |

---

## Chat-JSONL Format

Each line in `train_v1.jsonl` / `eval_v1.jsonl` is one chat example:

```json
{"messages":[{"role":"system","content":"<BOSS_SYSTEM_PROMPT>"},{"role":"user","content":"Goal: ..."},{"role":"assistant","content":"{\"plan\":\"...\",\"tasks\":[...]}"}],"metadata":{"category":"boss_planning","source":"swarm_capture","quality":"silver","contains_sensitive_data":false,"base_model_target":"gemma4:12b","created_by":"auto","notes":"..."}}
```

Plan captures (`PLAN_CAPTURE_SCHEMA.md` format) must be converted to this format
via `convert_plan_captures.py` before being added to the training dataset.

---

## Validation Before Training

Always run both scripts before using a dataset file:

```bash
python training_pit/scripts/validate_dataset.py training_pit/datasets/train_v1.jsonl
python training_pit/scripts/sanitize_dataset.py training_pit/datasets/train_v1.jsonl
```

Both must pass clean (0 errors, 0 rejects) before a file is used in a training job.

---

## Version History

| Version | Date | Notes |
|---------|------|-------|
| 1.0 | 2026-06-09 | Initial contributing guide |
| 1.1 | 2026-06-09 | Updated paths fine_tuning/ → training_pit/; aligned with canonical chat-JSONL schema |
| 1.2 | 2026-06-09 | Three-tier source strategy, Phase 3 gate, no-public-datasets policy, synthetic % corrected 25% → 5% eval-only |
