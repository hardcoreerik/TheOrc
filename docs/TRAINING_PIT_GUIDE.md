# TheOrc — Training Pit Guide

> **Current status: PHASE 2.5 ACTIVE — Dataset Accumulation.**
> Phase 3 infrastructure is complete (`review_captures.py`, `phase3_preflight.py`, manifest tracking).
> We are now in the **dataset accumulation stage** — collecting real TheOrc swarm captures,
> reviewing them, and building toward the Phase 3 gate (150/20/25).
> Current counts: **17/150 train · 4/20 eval · 7/25 negative.**
> Do not add training scripts or launch training jobs until `phase3_preflight.py` exits 0.

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

Phase 2 (DONE):   Data collection infrastructure
  ├── Auto-capture boss plans via DatasetCapture.cs ← LIVE
  ├── EvalRubric scoring (positive ≥70, negative ≤39)
  └── validate_dataset.py + sanitize_dataset.py scripts

Phase 2.5 (ACTIVE): Dataset Accumulation
  ├── review_captures.py — manifest-driven approve/reject/export
  ├── reviewed_v1.json manifest — tracked in git; source of truth for reviewed decisions
  ├── Atomic export gate: validate + sanitize before writing JSONL
  ├── phase3_preflight.py — 9-check readiness gate (exit 0 = READY, 1 = BLOCKED)
  ├── BATCH_CAPTURE_PLAN.md — 20 designed live swarm prompts for next capture batch
  └── Phase 3 gate counters: 3/150 train, 0/25 negative, 0/20 eval

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

## Current Dataset Counts

| Item | Count | Required for Phase 3 |
|---|---|---|
| Reviewed positive examples in `train_v1.jsonl` | **17** | ≥ 150 |
| Reviewed negative examples in `negative_v1.jsonl` | **7** | ≥ 25 |
| Fixed eval prompts in `evals/` | 10+ | ≥ 20 (met) |

See `training_pit/BATCH_CAPTURE_PLAN.md` for the next 20 planned live swarm prompts.

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

## Dataset Pipeline (Phase 2.5 Active)

```
Live swarm run                           Hand-authored plan capture
      │                                         │
      ▼                                         ▼
DatasetCapture.cs → .orc/swarm/dataset-staging/    .orc/swarm/dataset-staging-manual/
                    (auto-staged, gitignored)       (hand-authored, gitignored)
                              │
                              ▼
review_captures.py --list
review_captures.py --inspect <capture>
review_captures.py --approve <capture> --split train --quality silver
      │ (decision stored in reviewed_v1.json manifest — NO JSONL written yet)
      ▼
review_captures.py --export-train
      │ atomic: convert → temp file → validate_dataset → sanitize_dataset → replace final
      ▼
training_pit/datasets/train_v1.jsonl  (gitignored; rebuilt from manifest)
```

See [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) for the full step-by-step process.

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
| `review_captures.py` | **Phase 2.5 valve** — list, inspect, approve, reject, export, status |
| `phase3_preflight.py` | **Phase 3 gate** — 9 readiness checks; exit 0 = READY, 1 = BLOCKED |
| `validate_dataset.py` | Validates JSONL format, fields, roles, quality labels |
| `sanitize_dataset.py` | Scans for secrets, credentials, suspicious content |
| `check_hardware.py` | Detects GPU/RAM, prints training tier estimate |
| `check_model_compatibility.py` | Reads `base_model_compat.json`, prints status |
| `convert_plan_captures.py` | Converts staging captures → chat-JSONL (used by review pipeline) |
| `inspect_adapter.py` | Reads `adapter_config.json`, prints base model info |

---

## What Not To Do

- Do **not** add `trainer.py` or Unsloth/transformers training scripts yet
- Do **not** add `requirements-train.txt` or heavy ML dependencies to the main project
- Do **not** launch training jobs until `python training_pit/scripts/phase3_preflight.py` exits 0
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
    manifests/
      reviewed_v1.json          Approval manifest — tracked in git
    (train_v1.jsonl)            Gitignored; rebuilt from manifest via --export-train
    (eval_v1.jsonl)             Gitignored; rebuilt from manifest via --export-eval
    (negative_v1.jsonl)         Gitignored; rebuilt from manifest via --export-negative

  examples/
    plan_capture_good_001.json  High-quality plan example
    plan_capture_bad_001.json   Collapse pattern example
    chat_sft_good_*.jsonl       5 positive chat SFT examples
    chat_sft_synthetic_001.jsonl Synthetic (eval-only, not in train)
    chat_sft_eval_collapse_001.jsonl Collapse eval negative

  scripts/                      review_captures, validate, sanitize, convert, check hardware
  tests/                        Unit tests for Training Pit scripts
  configs/                      LoRA/QLoRA job config templates
  evals/                        Boss eval prompts
  adapters/registry.json        Adapter registry
```
