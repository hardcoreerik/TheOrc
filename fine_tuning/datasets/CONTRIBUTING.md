# Contributing Training Examples

> **Status:** Phase 2 not started. This file documents the future process.
> Do not add examples to `train_v1.jsonl` until the data collection tooling is built.

---

## What Goes in This Folder

Production training data — the JSONL files that feed the LoRA fine-tuning job.
These files are **not committed to git** (they can be large and contain model outputs
that shouldn't be versioned). Add `datasets/*.jsonl` to `.gitignore` when Phase 2 starts.

The `examples/` folder (sibling to this one) DOES get committed — it contains a small
number of hand-curated reference examples that illustrate the schema and the quality bar.

---

## Expected Files (Phase 2)

| File | Purpose | Target size |
|---|---|---|
| `train_v1.jsonl` | Training split | ≥ 150 positive + 50 negative examples |
| `eval_v1.jsonl` | Eval/validation split | ≥ 30 examples (held-out from train) |
| `negative_v1.jsonl` | Failure modes only | 20–50 examples |

---

## How to Add Examples

### Method 1: Auto-capture from live swarm runs (Phase 2 tooling)

When `DatasetCapture.cs` is built, it will automatically stage examples when:
- Boss plan scores ≥ 70 on the EVAL_RUBRIC → staged as positive
- Boss plan scores ≤ 39 (underplanned/collapsed) → staged as negative

Staged files appear in `.orc/swarm/dataset-staging/` and must be reviewed before
moving to `fine_tuning/datasets/`.

### Method 2: Manual curation from benchmark runs

1. Find a high-quality run in `.orc/swarm/runs/<run_id>/plan.json`
2. Check the `autoScore` in `swarm-metrics.json` for that run
3. If score ≥ 70, format it as a dataset example using `DATASET_SCHEMA.md`
4. Save to `fine_tuning/datasets/staging/<example_id>.json`
5. After review, append to `train_v1.jsonl` or `eval_v1.jsonl`

### Method 3: Synthetic augmentation (Phase 3+)

For goals where organic examples are scarce (e.g. C# WPF, data pipelines), the boss
model can generate synthetic plans against templated goals. These must be:
- Scored ≥ 80 by the rubric
- Manually reviewed for correctness
- Tagged `"source": "synthetic"` in the JSON

Synthetic examples should not exceed 25% of the training set.

---

## Quality Bar

| Criteria | Requirement |
|---|---|
| Minimum rubric score (positive) | 70 |
| Maximum rubric score (negative) | 39 |
| Description length per task | ≥ 60 chars |
| Filename in title | ≥ 75% of tasks |
| Valid JSON | Required |
| Domain accuracy | No off-domain tasks |

Do not include marginal examples (40–69) in either split — they add noise.

---

## Dataset Balance

Maintain roughly:
- 75% positive examples
- 25% negative examples
- Across domains: ≥ 3 examples per domain in `domain_taxonomy`
- Difficulty distribution: ≥ 30% difficulty-2, ≥ 20% difficulty-3

---

## JSONL Format

Each line in `train_v1.jsonl` / `eval_v1.jsonl` is the dataset example object from
`DATASET_SCHEMA.md`, serialized as a single-line JSON string:

```
{"schema_version":"1.0","example_id":"ex_20260609_001","goal":"Build a CSV...","plan":{...},"quality_score":85,...}
{"schema_version":"1.0","example_id":"ex_20260609_002","goal":"Build a REST API...","plan":{...},"quality_score":72,...}
```

---

## Version History

| Version | Date | Notes |
|---|---|---|
| 1.0 | 2026-06-09 | Initial contributing guide |
