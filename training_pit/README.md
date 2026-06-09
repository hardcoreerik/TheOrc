# The Training Pit

> **Phase status: PHASE 2 ACTIVE — Data collection in progress.**
> `DatasetCapture.cs` is live. Boss plans are being auto-staged after every swarm run.
> Phase 3 training is **blocked** pending ≥150 reviewed positive examples.
> See `DATASET_STRATEGY.md` for the Phase 3 gate checklist and dataset source strategy.
>
> **Live training is not yet implemented. Do not add training scripts or launch jobs
> until Phase 3 is explicitly unblocked. See ARCHITECTURE.md for the full roadmap.**

---

## What The Training Pit Is

The Training Pit is not a generic fine-tuning folder. It is the long-term system for
evolving TheOrc's models — specifically the boss model and goblin workers — through
structured data collection, evaluation, and adapter management.

The goal is to move TheOrc's behavior improvements from prompt engineering (which has
a ceiling) into the model weights themselves, where they survive any prompt and work
reliably across goals, domains, and model versions.

---

## What Training Fixes That Prompt Engineering Cannot

| Problem | Prompt Fix | Fine-Tune Fix |
|---|---|---|
| Boss collapse to single empty task | BossPromptSupplement + retry | Bake multi-task decomposition into weights |
| Plan quality degrades on complex goals | Escalation prompt | Model generalises from diverse examples |
| Few-shot context consumes ~1800 tokens | Can't reduce | Remove examples; behavior is intrinsic |
| Hallucinated files/APIs in responses | Explicit instructions | Train on examples that never invent things |
| Windows/PowerShell defaults wrong | Per-task reminders | Train on PS-native examples |
| Worker delegation quality varies | Role descriptions | Worker-specific adapters |
| New models need per-model calibration | New BossPromptSupplement | Adapter generalises |

---

## Current State (Phase 2 Active)

- [x] QAT base model pulled and deployed (`theorc-boss:gemma4` on Ollama server)
- [x] Canonical dataset schema defined (chat JSONL — `DATASET_SCHEMA.md`)
- [x] Plan capture schema defined (`PLAN_CAPTURE_SCHEMA.md`)
- [x] Eval rubric: plan quality + boss behavior (`EVAL_RUBRIC.md`)
- [x] LoRA / QLoRA job config templates (`configs/`)
- [x] Adapter registry schema (`adapters/registry.json`)
- [x] Reference examples (`examples/`) — 4 positive, 2 eval-only
- [x] Eval prompt starter sets (`evals/`)
- [x] Utility scripts (`scripts/`)
- [x] Hardware guide, model compatibility, safety docs
- [x] **`DatasetCapture.cs` live** — auto-staging boss plans after every swarm run
- [x] **Dataset strategy documented** (`DATASET_STRATEGY.md`) — three-tier sources, Phase 3 gate
- [x] **Role architecture documented** (`ROLE_ARCHITECTURE.md`) — logical/execution role split, alias map

---

## Planned Pipeline

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

## File Inventory

```
training_pit/
  README.md                       ← this file
  ARCHITECTURE.md                 ← full system design and phase roadmap
  DATASET_SCHEMA.md               ← canonical chat-JSONL training format
  PLAN_CAPTURE_SCHEMA.md          ← specialized boss/swarm plan capture format
  EVAL_RUBRIC.md                  ← plan quality + boss behavior rubrics
  MODEL_COMPATIBILITY.md          ← base model compatibility rules and states
  HARDWARE_GUIDE.md               ← VRAM tiers, WSL2, local training setup
  SAFETY_AND_PRIVACY.md           ← what not to train on, sanitizer requirements

  configs/
    lora_job_template.json        ← LoRA training job config (draft/planned)
    qlora_job_template.json       ← QLoRA 4-bit config for 12–16 GB VRAM
    base_model_compat.json        ← compatibility matrix with verified/inferred states

  datasets/
    .gitkeep                      ← datasets NOT committed to git
    CONTRIBUTING.md               ← capture process, quality bar, format spec

  examples/
    plan_capture_good_001.json    ← high-quality plan (Combo A, score 85)
    plan_capture_good_002.json    ← good plan (Combo E, score 68)
    plan_capture_bad_001.json     ← collapse pattern (score 5, DPO contrastive pair)
    chat_sft_good_001.jsonl       ← chat JSONL: boss planning, no hallucination
    chat_sft_good_002.jsonl       ← chat JSONL: debugging + PowerShell

  evals/
    boss_behavior_eval_prompts.jsonl   ← 10 starter boss behavior eval prompts
    plan_quality_eval_prompts.jsonl    ← plan/swarm quality eval prompts
    scorecards/
      .gitkeep                         ← future before/after eval results go here

  adapters/
    local/
      .gitkeep                    ← locally trained adapters
    imported/
      .gitkeep                    ← externally sourced adapters
    registry.json                 ← adapter registry (status, targets, eval state)

  scripts/
    validate_dataset.py           ← validates JSONL format, fields, roles
    sanitize_dataset.py           ← scans for secrets, reports suspicious lines
    check_hardware.py             ← detects GPU/RAM, prints training tier estimate
    check_model_compatibility.py  ← reads base_model_compat.json, prints status
    inspect_adapter.py            ← reads adapter_config.json, prints base model info
    convert_plan_captures.py      ← converts .orc/swarm/dataset-staging/ captures → chat-JSONL
    _generate_examples.py         ← regenerates examples/ with canonical BOSS_SYSTEM_PROMPT
    _make_test_fixtures.py        ← creates synthetic plan captures for e2e pipeline testing
```

---

## Do Not

- Do not add `trainer.py` or any Unsloth/transformers training scripts yet
- Do not add `requirements-train.txt` or heavy ML deps to the main project
- Do not launch training jobs until Phase 2 data collection is complete and validated
- Do not commit raw dataset JSONL files — add `datasets/*.jsonl` to `.gitignore` when Phase 2 starts
- Do not mark an adapter `approved` until it improves eval results over the base model
  without increasing hallucination or unsafe behavior

---

*Last updated: 2026-06-09 — Phase 1 complete. Aligned with The Training Pit architecture.*
