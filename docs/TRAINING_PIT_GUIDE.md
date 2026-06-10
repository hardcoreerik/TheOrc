# TheOrc — Training Pit Guide

> **Current status: PHASE 2 ACTIVE — Data collection in progress.**
> Phase 3 training is **BLOCKED** pending ≥150 reviewed positive examples.
> Live training has not yet been implemented.
> Do not add training scripts or launch training jobs until Phase 3 is explicitly unblocked.

---

## What the Training Pit Is

The Training Pit is the system for improving TheOrc's models through structured data
collection, evaluation, and adapter management.

The goal is to move TheOrc's behavior improvements from prompt engineering (which has a
ceiling) into the model weights themselves — through LoRA/QLoRA fine-tuning — where they
survive any prompt and work reliably across goals, domains, and model versions.

---

## Why Fine-Tuning Instead of Just Prompting

| Problem | Prompt fix | Fine-tune fix |
|---|---|---|
| Boss collapse to single empty task | BossPromptSupplement + retry | Bake multi-task decomposition into weights |
| Plan quality degrades on complex goals | Escalation prompt | Model generalizes from diverse examples |
| Few-shot context consumes ~1800 tokens | Cannot reduce | Remove examples; behavior becomes intrinsic |
| Hallucinated files/APIs in responses | Explicit instructions | Train on examples that never invent things |
| Windows/PowerShell defaults wrong | Per-task reminders | Train on PS-native examples |

---

## Phase Roadmap

```
Phase 1 (DONE):   Scaffolding
  └── schemas, rubrics, configs, scripts, eval prompts, adapter registry

Phase 2 (ACTIVE): Data collection
  ├── Auto-capture boss plans via DatasetCapture.cs ← LIVE
  ├── Manual curation and annotation of examples
  ├── Negative example mining (collapse patterns, hallucinations)
  ├── Validate + sanitize via scripts/validate_dataset.py
  └── Gate: ≥150 reviewed positive + ≥25 negative examples before Phase 3

Phase 3 (BLOCKED): Training
  ├── LoRA fine-tune via Unsloth on QAT base
  ├── Eval loop using boss_behavior_eval_prompts.jsonl
  ├── Export adapter to GGUF
  └── Register in adapters/registry.json

Phase 4 (FUTURE): Deployment
  ├── ModelProfiles entry for fine-tuned model
  ├── A/B path in SwarmSession (base vs adapter)
  └── Before/after benchmark vs swarm-metrics.json baselines
```

---

## Current Phase 2 Counters

| Item | Count | Required for Phase 3 |
|---|---|---|
| Reviewed positive examples in `train_v1.jsonl` | 0 | ≥ 150 |
| Reviewed negative examples in `negative_v1.jsonl` | 0 | ≥ 25 |
| Fixed eval prompts in `evals/` | 10+ | ≥ 20 (met) |

---

## What the First LoRA Targets

The first LoRA adapter is **not** a general coding improvement.

Gemma 4 12B is already a capable coder and researcher. The problem is narrow:
as TheOrc's boss/planner, it collapses single-task plans, produces vague descriptions,
and omits filenames when given open-ended goals.

**First LoRA target:** given a user's coding goal, produce a valid JSON swarm plan —
2–4 tasks, concrete descriptions, named output files, consistent API contracts.

Everything in the dataset must teach this specific behavior. General coding examples
are a distraction for this first adapter.

---

## The Base Model

| Item | Details |
|---|---|
| Base model | `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0` |
| Deployed as | `theorc-boss:gemma4` via Ollama Modelfile |
| Modelfile | `temperature=0.2`, `think=false`, `num_ctx=16384`, few-shot examples |
| BF16 size (for training) | 26.7 GB (confirmed, Google AI for Developers docs) |
| Q4_0 GGUF size (for inference) | 6.7 GB |
| Training method | QLoRA (NF4 4-bit) — LoRA on BF16 requires ~26.7 GB, does not fit at 16 GB |

`theorc-boss:gemma4` is a **Modelfile wrapper** (QAT + few-shot calibration).
It is **not** a LoRA-trained model. Phase 3 LoRA training is what will produce
a real adapter — after the 150-example threshold is met.

---

## How Captures Are Auto-Staged

`DatasetCapture.cs` (in `OrchestratorIDE/Services/Swarm/`) evaluates every boss plan
using `EvalRubric.Score()` after each swarm run:

- Score ≥ 70 → `plan_capture_good_<runId>_<score>.json` in `.orc/swarm/dataset-staging/`
- Score ≤ 39 → `plan_capture_bad_<runId>_<score>.json` in the same folder
- Score 40–69 → silently skipped (marginal; too noisy to be useful training signal)

Staged captures are gitignored and local-only.

---

## Dataset Pipeline (Phase 2 Active Work)

```
Live swarm run
      │
      ▼
DatasetCapture.cs → .orc/swarm/dataset-staging/ (raw JSON captures)
      │
      ▼
convert_plan_captures.py → training_pit/datasets/staging/converted_<ts>.jsonl
      │
      ▼
Manual review — check for: specific goal, good plan, no hallucinations, no PII
      │
      ▼
validate_dataset.py + sanitize_dataset.py → must pass 0 errors, 0 rejects
      │
      ▼
Promote to training_pit/datasets/train_v1.jsonl
```

See [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) for the step-by-step review process.

---

## Training Hardware

| Hardware | Training mode | Notes |
|---|---|---|
| 16 GB VRAM (RTX 5070 Ti — dev machine) | QLoRA NF4 | ≈12–14 GB active; fits with headroom |
| 16 GB VRAM | LoRA BF16 | ❌ Does not fit — BF16 requires ~26.7 GB |
| 24 GB+ VRAM | LoRA BF16 | Fits with headroom |
| 12 GB VRAM | QLoRA NF4 | Marginal — works with careful batch sizing |

Training is done in **WSL2**, not Windows native. PyTorch + CUDA + Unsloth are Linux-optimized.

See `training_pit/HARDWARE_GUIDE.md` for full VRAM tier table and training time estimates.

---

## Key Scripts

All scripts are in `training_pit/scripts/`:

| Script | Purpose |
|---|---|
| `validate_dataset.py` | Validates JSONL format, fields, roles, quality labels |
| `sanitize_dataset.py` | Scans for secrets, credentials, suspicious content |
| `check_hardware.py` | Detects GPU/RAM, prints training tier estimate |
| `check_model_compatibility.py` | Reads `base_model_compat.json`, prints status |
| `convert_plan_captures.py` | Converts staging captures → chat-JSONL |
| `inspect_adapter.py` | Reads `adapter_config.json`, prints base model info |

---

## What Not To Do

- Do **not** add `trainer.py` or Unsloth/transformers training scripts yet
- Do **not** add `requirements-train.txt` or heavy ML dependencies to the main project
- Do **not** launch training jobs until Phase 3 is explicitly unblocked
- Do **not** commit raw dataset JSONL files — they are local-only and gitignored
- Do **not** mark an adapter `approved` until it improves eval results over the base model
- Do **not** add new swarm execution roles beyond the existing four (RESEARCHER, CODER, UIDEVELOPER, TESTER)

---

## File Map

```
training_pit/
  README.md                     Phase status, pipeline overview
  ARCHITECTURE.md               Full system design
  DATASET_SCHEMA.md             Canonical chat-JSONL training format
  PLAN_CAPTURE_SCHEMA.md        Boss/swarm plan capture format
  EVAL_RUBRIC.md                Plan quality + boss behavior rubrics
  DATASET_STRATEGY.md           Three-tier source strategy, Phase 3 gate
  ROLE_ARCHITECTURE.md          Logical/execution role split
  MODEL_COMPATIBILITY.md        Base model states
  HARDWARE_GUIDE.md             VRAM tiers, WSL2, training setup
  SAFETY_AND_PRIVACY.md         What not to train on

  datasets/
    CONTRIBUTING.md             How to add examples, quality bar
    (train_v1.jsonl)            Not yet created; gitignored
    (eval_v1.jsonl)             Not yet created; gitignored

  examples/
    plan_capture_good_001.json  High-quality plan example
    plan_capture_bad_001.json   Collapse pattern example
    chat_sft_good_*.jsonl       5 positive chat SFT examples
    chat_sft_synthetic_001.jsonl Synthetic (eval-only, not in train)
    chat_sft_eval_collapse_001.jsonl Collapse eval negative

  scripts/                      validate, sanitize, convert, check hardware
  configs/                      LoRA/QLoRA job config templates
  evals/                        Boss eval prompts
  adapters/registry.json        Adapter registry
```
