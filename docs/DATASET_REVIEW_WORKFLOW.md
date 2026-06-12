# TheOrc — Dataset Review Workflow

> This guide focuses on the review valve: how staged captures become approved manifest entries and safe JSONL exports. For the larger pipeline, see [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md). For terminology, see [GLOSSARY.md](GLOSSARY.md).

---

## What This Workflow Controls

This workflow exists to answer one narrow but important question:

```text
Which captures are allowed to become trainable data?
```

The current answer is: only captures represented as approved entries in `reviewed_v1.json`.

---

## The Actual Approval Path

```text
staged plan_capture_*.json
  -> inspect
  -> approve or reject in reviewed_v1.json
  -> export by split
  -> validate
  -> sanitize
  -> atomically replace final JSONL
```

This is manifest-first and fail-closed by design.

---

## Main Commands

List captures:

```powershell
python training_pit/scripts/review_captures.py --list
```

Inspect one capture:

```powershell
python training_pit/scripts/review_captures.py --inspect .orc/swarm/dataset-staging/plan_capture_good_20260611_123456_084.json
```

Approve:

```powershell
python training_pit/scripts/review_captures.py --approve .orc/swarm/dataset-staging/plan_capture_good_20260611_123456_084.json --split train --quality silver --note "Good decomposition"
```

Reject:

```powershell
python training_pit/scripts/review_captures.py --reject .orc/swarm/dataset-staging/plan_capture_bad_20260611_123456_005.json --note "Collapse pattern"
```

Export:

```powershell
python training_pit/scripts/review_captures.py --export-train
python training_pit/scripts/review_captures.py --export-eval
python training_pit/scripts/review_captures.py --export-negative
```

Status:

```powershell
python training_pit/scripts/review_captures.py --status
python training_pit/scripts/phase3_preflight.py
```

---

## Split Rules

The current script enforces split-specific quality constraints.

- `train`: `gold` or `silver`
- `eval`: `gold`, `silver`, `draft`, or `rejected`
- `negative`: `gold`, `silver`, `draft`, or `rejected`

This is why you should not treat the split and quality fields as free-form notes.

---

## Why Export Is Safe

`review_captures.py` does not overwrite final dataset files casually.

The export flow is:

1. convert approved captures
2. write a temp file
3. validate temp file
4. sanitize temp file
5. replace the final JSONL only if the earlier checks pass

If validation or sanitization fails, the final JSONL stays unchanged.

---

## Current Readiness

In the current repository state:

- manifest counts and JSONL counts agree
- validation passes
- sanitization passes
- duplicate checks pass
- eval isolation passes
- staging safety passes

That is why `phase3_preflight.py --json` currently reports `ready: true`.
