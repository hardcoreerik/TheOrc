# The Training Pit

> **Phase status: Phase 3 is active.**
> The Training Pit now has a real Phase 3 trainer, local dataset lanes, adapter
> outputs, and a registered production Boss Planning LoRA (`lora_v1`).
>
> Public source of truth: `adapters/registry.json`.
> Local-only source of truth: `datasets/*.jsonl` and `outputs/`, which are
> intentionally gitignored because they contain large generated data and model
> artifacts.

---

## What The Training Pit Is

The Training Pit is TheOrc's model-improvement workspace. It turns reviewed
TheOrc behavior into datasets, trains LoRA adapters, evaluates those adapters
against base models, and records which adapters are safe to deploy.

It is not a generic fine-tuning folder. The goal is to move TheOrc's best
behavior out of prompt-only fixes and into durable model behavior: better boss
planning, cleaner worker delegation, stronger Windows/PowerShell defaults, fewer
hallucinated files or APIs, and clearer role boundaries.

---

## Current State

- [x] Dataset capture and review pipeline exists.
- [x] Canonical chat JSONL schema exists (`DATASET_SCHEMA.md`).
- [x] Plan capture schema exists (`PLAN_CAPTURE_SCHEMA.md`).
- [x] Eval rubric exists (`EVAL_RUBRIC.md`).
- [x] Dataset approval tooling exists (`scripts/review_captures.py`).
- [x] Preflight and suitability gates exist (`scripts/phase3_preflight.py`,
      `scripts/suitability_gate.py`).
- [x] Phase 3 trainer exists (`scripts/train_lora.py`).
- [x] Boss/tester dataset split tooling exists (`scripts/split_v2gold.py`).
- [x] Adapter registry exists (`adapters/registry.json`).
- [x] `lora_v1` is registered as the production Boss Planning LoRA.
- [x] Later local experiments (`lora_v2`, `lora_v3`, `lora_v4`) exist in the
      local artifact workspace, but only registered adapters should be treated as
      deployable.

Tracked repo files describe the pipeline and registry. Generated JSONL datasets,
training logs, checkpoints, adapters, and GGUF exports are local artifacts and
are not committed.

---

## Registered Adapter

`adapters/registry.json` currently registers:

| Adapter | Status | Base | Notes |
|---|---|---|---|
| `lora_v1` | `production` | `google/gemma-4-12b-it` | Boss Planning LoRA, trained on 900 examples and evaluated on 87 prompts. Registered target: `theorc-boss:gemma4-ft`. |

The registry records the deployed Ollama adapter path and evaluation summary.
Do not treat a local `outputs/lora_*` folder as production until it is reviewed
and registered.

---

## Local Artifact Lanes

These paths may exist in an operator's local checkout, but are intentionally
gitignored:

| Path | Purpose |
|---|---|
| `datasets/train_v1.jsonl` | Original reviewed training lane. |
| `datasets/train_v2gold.jsonl` | Larger mixed gold lane before boss/tester routing. |
| `datasets/train_v3gold.jsonl` | Clean boss-training lane produced by `split_v2gold.py`. |
| `datasets/train_tester_v1.jsonl` | Tester-worker seed lane separated out of mixed data. |
| `datasets/train_v4gold_merged.jsonl` | Current default train input for `train_lora.py`. |
| `outputs/lora_v*/` | Local training summaries, checkpoints, adapters, logs, and eval results. |

Because these files can be large and can contain generated model output, they
belong in the local training workspace, not in git.

---

## Useful Commands

Review captured data:

```powershell
python training_pit/scripts/review_captures.py --status
python training_pit/scripts/review_captures.py --list
python training_pit/scripts/review_captures.py --export-train
```

Check phase and dataset gates:

```powershell
python training_pit/scripts/phase3_preflight.py
python training_pit/scripts/suitability_gate.py training_pit/datasets/train_v4gold_merged.jsonl
```

Split mixed gold data into boss/tester lanes:

```powershell
python training_pit/scripts/split_v2gold.py --dry-run
python training_pit/scripts/split_v2gold.py
```

Run a trainer smoke check before spending GPU time:

```powershell
python training_pit/scripts/train_lora.py --dry-run
```

Run the default Phase 3 trainer:

```powershell
python training_pit/scripts/train_lora.py
```

---

## Pipeline

```text
Phase 1: Scaffolding
  schemas, rubrics, configs, examples, eval prompts, adapter registry

Phase 2: Data collection
  DatasetCapture.cs auto-stages boss plans after swarm runs

Phase 2.5: Review / approval valve
  review_captures.py approves, rejects, exports, validates, and sanitizes data

Phase 3: Training and evaluation
  train_lora.py trains QLoRA adapters
  suitability_gate.py blocks contaminated boss-training sets
  split_v2gold.py separates boss-clean examples from tester-worker examples
  eval scripts compare base vs adapter behavior

Phase 4: Deployment
  reviewed adapters are registered in adapters/registry.json
  deployment/runtime integration consumes registered artifacts only
```

---

## File Inventory

```text
training_pit/
  README.md
  ARCHITECTURE.md
  DATASET_SCHEMA.md
  PLAN_CAPTURE_SCHEMA.md
  EVAL_RUBRIC.md
  MODEL_COMPATIBILITY.md
  HARDWARE_GUIDE.md
  SAFETY_AND_PRIVACY.md

  adapters/
    registry.json
    local/
    imported/

  configs/
    lora_job_template.json
    qlora_job_template.json
    base_model_compat.json

  datasets/
    CONTRIBUTING.md
    manifests/reviewed_v1.json
    *.jsonl                  local-only, gitignored

  evals/
    boss_behavior_eval_prompts.jsonl
    plan_quality_eval_prompts.jsonl
    scorecards/

  examples/
    chat_sft_good_*.jsonl
    chat_sft_eval_*.jsonl
    plan_capture_*.json

  outputs/
    lora_v*/                 local-only, gitignored

  scripts/
    review_captures.py
    phase3_preflight.py
    validate_dataset.py
    sanitize_dataset.py
    suitability_gate.py
    split_v2gold.py
    train_lora.py
    eval_adapter.py
    inspect_adapter.py
    convert_plan_captures.py

  tests/
    test_review_captures.py
    test_phase3_preflight.py
```

---

## Do Not

- Do not commit raw dataset JSONL files.
- Do not commit `outputs/`, checkpoints, safetensors, GGUFs, or training logs.
- Do not mark an adapter `production` until it improves the target evals without
  introducing unsafe behavior or regressions that matter for its role.
- Do not train the boss model on tester-worker examples. Route those to the
  tester lane with `split_v2gold.py`.
- Do not treat local artifacts as shipped behavior unless they are recorded in
  `adapters/registry.json`.

---

*Last updated: 2026-06-19 — README refreshed to reflect Phase 3 trainer,
local artifact lanes, and registered `lora_v1` production adapter.*
