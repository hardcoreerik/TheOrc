# The Training Pit — Architecture

> **Status:** Phase 3 is active on this branch. The Training Pit has real review,
> dataset, training, and adapter-registry flows; production `lora_v1` is already
> registered, while later datasets and adapter runs remain local artifacts until
> promoted.

---

## System Overview

The Training Pit is TheOrc's model-improvement workspace. It sits between live
runtime behavior and adapter deployment:

```text
TheOrc runtime
  -> swarm plans / traces / captures
  -> review + curation
  -> boss/tester dataset lanes
  -> train + eval
  -> adapter registry decision
  -> deployment/runtime consumption
```

It is not a standalone ML framework. The Training Pit owns:

- capture and review workflow
- dataset schemas and split policy
- safety and suitability gates
- training entrypoints and local artifact layout
- adapter registration rules

It does not own the inference kernels themselves. Training and inference still
sit on top of external model stacks such as Hugging Face PEFT/TRL and the
runtime backends used by TheOrc.

---

## Architectural Position In The Product

```text
+---------------------- TheOrc runtime ----------------------+
| SwarmSession / reviewer / model runtimes                  |
| DatasetCapture stages plans from real work                |
+---------------------------+--------------------------------+
                            |
                            v
+---------------------- Training Pit ------------------------+
| review_captures.py / prescreen / judge / manifests        |
| JSONL exports / split_v2gold / suitability_gate           |
| train_lora.py / eval outputs / adapter registry           |
+---------------------------+--------------------------------+
                            |
                            v
+---------------------- Deployment --------------------------+
| adapters/registry.json                                    |
| current production path: Ollama-backed deployed adapter   |
| future path: Native Runtime consumes local assets direct  |
+-----------------------------------------------------------+
```

Today, deployment still means "registered and consumed by the app's active model
path," which remains Ollama-first in production. Native Runtime changes the
future deployment shape, but not the core Training Pit responsibilities.

---

## Data Shapes

Two core data formats matter:

1. `DATASET_SCHEMA.md`
   Canonical chat JSONL for SFT/LoRA-style training.

2. `PLAN_CAPTURE_SCHEMA.md`
   Boss/swarm plan capture format used by live dataset staging.

The committed repo stores schemas, examples, and manifests. Large JSONL datasets,
logs, checkpoints, safetensors, and GGUF outputs are intentionally local-only
and gitignored.

---

## Capture And Review Pipeline

The current capture path is real and shipped:

```text
swarm run
  -> DatasetCapture.StageAsync(...)
  -> staging files under .orc/swarm/dataset-staging
  -> prescreen / judge / human review
  -> reviewed_v1.json manifest
  -> export to JSONL lanes
```

### DatasetCapture

`DatasetCapture.cs` is live in the swarm path. It stages high-signal boss plans
after decomposition succeeds.

The rubric behavior remains:

- score `>= 70`: stage as good
- score `<= 39`: stage as bad
- score `40-69`: skip

Capture is best-effort so swarm execution does not fail just because data
collection had a problem.

### Review stack

The review workflow is layered:

- `prescreen_captures.py` catches deterministic mechanical issues
- `judge_captures.py` gives model-assisted triage
- `review_captures.py` is the real approval valve

The manifest is the durable source of review decisions. Exports are rebuilt from
manifest state, not by trusting raw staging files.

---

## Dataset Lanes

The old "one train file and one eval file forever" story is no longer accurate.
This branch now has multiple dataset lanes and routing steps.

Important lanes/operators:

- `train_v1.jsonl` / `eval_v1.jsonl`
  Original production-v1 boss lane used for the shipped adapter.

- `train_v2gold.jsonl` / `eval_v2gold.jsonl`
  Larger mixed gold lane that later required contamination review.

- `train_v3gold.jsonl` / `eval_v3gold.jsonl`
  Clean boss-training lane produced after suitability routing.

- `train_tester_v1.jsonl` / `eval_tester_v1.jsonl`
  Tester-lane seed data separated out of mixed examples.

- `train_v4gold_merged.jsonl`
  Current default train input for `train_lora.py` on this branch.

`split_v2gold.py` and `suitability_gate.py` exist because not every plausible
example is suitable for boss training. The big learned constraint is that
TESTER-lane write-task poison must be filtered out before future boss runs.

---

## Training And Safety Gates

Two important gates exist before or around training:

### Phase gate

`phase3_preflight.py` checks readiness conditions around manifests, exports,
validation, sanitization, duplicates, and split integrity.

This remains the branch's formal "is the workflow ready?" gate, but the existence
of real local training runs means docs must not pretend no training backend
exists.

### Suitability gate

`suitability_gate.py` is the newer contamination guard. It checks for patterns
that would teach the boss the wrong behavior, especially TESTER-lane write-task
poison and train/eval leakage.

That gate exists because the v2 dataset taught a real lesson: more examples are
not automatically better examples.

---

## Training Path

The current trainer is `scripts/train_lora.py`.

Important branch truth:

- it is real and runnable
- it defaults to `train_v4gold_merged.jsonl` for train and `eval_v3gold.jsonl`
  for eval
- it writes progress heartbeats and local training summaries
- it supports resume, seed control, VRAM cap, and suitability checks
- it produces local adapter/checkpoint artifacts under `outputs/`

This branch has local `lora_v2`, `lora_v3`, and `lora_v4` artifact lanes in
addition to the original production `lora_v1` history.

---

## Adapter Lifecycle

An adapter moves through four conceptual states:

1. trained locally
2. evaluated against the target behavior
3. registered in `adapters/registry.json`
4. consumed by the app's runtime/deployment path

Only registered adapters should be treated as product truth.

Current truth:

- `lora_v1` is registered production
- `lora_v2` was retired after suitability findings
- `lora_v3` completed and beat base, but did not beat the production `lora_v1`
  baseline
- later local artifacts should not be described as shipped unless the registry
  says so

---

## Integration Points With Runtime

The Training Pit touches the runtime in several places:

- `SwarmSession` and `DatasetCapture` for source data
- reviewer and role architecture for what counts as good/bad behavior
- `adapters/registry.json` for production truth
- the active inference/runtime path for actual adapter consumption

Today, production adapter deployment is still aligned with the Ollama-first path.
The newer Native Runtime work changes how future adapters may be consumed, but it
does not remove the need for review, suitability, evaluation, or registry
promotion.

---

## Phase Roadmap

| Phase | Status | Meaning on this branch |
|---|---|---|
| 1 — Scaffolding | Done | Schemas, rubrics, configs, examples, scripts, registry |
| 2 — Capture/review | Done | DatasetCapture, prescreen, judge, review, manifests, exports |
| 3 — Training/eval | Active | Real trainers, suitability routing, local artifact runs, production `lora_v1` history |
| 4 — Deployment/runtime integration | Partial | Registry and deployed production adapter exist; Native Runtime consumption is future-facing |

---

## Practical Truth Rules

When docs drift, use these rules:

- `adapters/registry.json` is the committed source of truth for what is actually
  production or approved.
- `outputs/` and `datasets/*.jsonl` are local truth for ongoing work, not public
  shipped claims.
- `README.md` should describe the current operator workflow.
- this file should describe the shape of the system, not freeze an old phase.

---

*Last updated: 2026-06-19 — architecture refreshed for active Phase 3 training,
multi-lane datasets, suitability gating, and production-vs-local adapter truth.*
