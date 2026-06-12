# TheOrc — Training Pit Guide

> This guide explains how a swarm plan becomes reviewed data and how that data becomes an adapter. Read [ARCHITECTURE.md](ARCHITECTURE.md) for the system view and [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) for the manifest-driven approval workflow.

---

## What The Training Pit Is

The Training Pit is TheOrc's evidence pipeline for improving model behavior with reviewed local data.

Its job is to turn:

- live swarm planning behavior
- into staged captures
- into approved dataset rows
- into a training-ready export
- into an adapter produced by ORC ACADEMY

---

## The Current End-To-End Flow

```text
swarm run
  -> DatasetCapture stages plan_capture_*.json
  -> prescreen_captures.py flags mechanical defects
  -> judge_captures.py assigns fabrication-risk triage
  -> human review writes reviewed_v1.json
  -> review_captures.py exports train/eval/negative JSONL
  -> phase3_preflight.py validates readiness
  -> ORC ACADEMY launches train_lora.py
  -> adapter, checkpoints, summary, and logs are saved
```

---

## Dataset State Today

Verified from the current manifest and preflight output:

- train: 900 approved examples
- eval: 87 approved examples
- negative: 25 approved examples

Those counts pass the current minimum Phase 3 thresholds of:

- 150 train
- 20 eval
- 25 negative

The current panel and docs also treat the next quality milestone as roughly:

- 1,000 train
- 200 eval

---

## Capture Stage

`DatasetCapture.cs` runs inside `SwarmSession`.

It scores the boss plan and stages:

- high-quality positives at `>= 70`
- low-quality negatives at `<= 39`

Mid-band captures are skipped to avoid filling the dataset with ambiguous examples.

---

## Prescreen And Judge

The first two review passes are intentionally different.

### Prescreen

`prescreen_captures.py` is deterministic. It catches things a human should not have to burn attention on, such as invalid roles or TESTER write verbs.

### Judge

`judge_captures.py` is heuristic. It uses a local judge model to sort likely fabrication risk, but it does not make the final keep-or-throw-away decision.

---

## Manifest-Centered Review

The manifest is the source of truth:

- path: `training_pit/datasets/manifests/reviewed_v1.json`
- written by: `review_captures.py`
- consumed by: export and preflight

This matters because raw staging files do not become trainable data on their own. Approval lives in the manifest, and export is rebuilt from that manifest.

---

## Preflight Gate

`phase3_preflight.py` exists to make training fail closed.

It verifies:

- manifest validity
- threshold counts
- JSONL presence
- export consistency
- validation
- sanitizer pass
- duplicate safety
- eval isolation
- staging safety

The script does not start training. It only answers whether training is currently safe to begin.

---

## ORC ACADEMY

ORC ACADEMY is the operator-facing training surface in the app.

Verified GUI capabilities:

- start
- resume
- dry run
- VRAM cap
- heartbeat monitoring
- hang warning
- re-attach after app restart

Verified script-side behavior in `train_lora.py`:

- default base model: `google/gemma-4-12b-it`
- 4-bit NF4 loading
- checkpoint output
- progress heartbeat JSON
- final summary JSON

Some code comments and field names still use the older WARCHIEF FORGE naming, but the current product name is ORC ACADEMY.

---

## Supporting Tools

Two support scripts are especially important in the current workflow:

- `Tools/harvest_marker_watch.ps1` can stop NIGHT HARVEST automatically at the train-data marker
- `Tools/codex-review.ps1` can generate structured review output for code changes, which is useful when the Training Pit is farming documentation or refactor tasks and you want external review evidence

---

## What To Read Next

- [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) for manifest operations
- [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) for model evidence
- [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) for the planned distributed version of this loop
