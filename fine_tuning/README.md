# TheOrc Fine-Tuning — Extension Point

> **Phase status: SCAFFOLDING ONLY.**
> This folder defines schemas, rubrics, and config formats for a future fine-tuning pipeline.
> No training code is implemented yet. Do not add training scripts or launch jobs until
> Phase 2 is explicitly started. See the roadmap below for what comes next.

---

## Why This Folder Exists

Prompt engineering (temperature, few-shot examples, retry logic) significantly improved
gemma4:12b as a boss model — but it has a ceiling. The under-planning behavior is a
**training-time distribution problem**: the model was not trained on multi-task agentic
decomposition at small scale. Prompt tricks work around it; fine-tuning fixes it.

This folder prepares the repo to support LoRA/QLoRA fine-tuning without requiring any
structural changes when that phase starts. The goal is to avoid painting the codebase
into a corner — a dataset format change later is expensive; setting up the schema now
costs almost nothing.

---

## What Fine-Tuning Solves That Prompt Engineering Cannot

| Problem | Prompt Fix | Fine-Tune Fix |
|---|---|---|
| gemma4:12b produces `"Execute goal"` | BossPromptSupplement + retry | Bake multi-task decomposition into weights |
| Plan quality degrades with complex goals | Escalation prompt | Model generalises from examples |
| Few-shot examples consume ~1800 tokens of context | Can't reduce them | Remove examples from context; behavior is intrinsic |
| Temperature tuning is model-version-specific | Per-model profiles | Fine-tuned model works at default temperature |
| New boss models need per-model calibration | New BossPromptSupplement | Generic fine-tune works across model families |

---

## Planned Training Pipeline (Future Phase 2)

```
Phase 1 (NOW): Scaffolding
  ├── Dataset schema (DATASET_SCHEMA.md)
  ├── Eval rubric (EVAL_RUBRIC.md)
  ├── LoRA/QLoRA config format (configs/)
  └── Example capture from live swarm runs (examples/)

Phase 2 (FUTURE): Data collection
  ├── Auto-capture high-scoring boss plans from swarm runs
  ├── Manual curation + annotation of edge cases
  ├── Negative example mining (the gemma4 collapse pattern)
  └── Target: ~200–500 curated goal→plan pairs

Phase 3 (FUTURE): Training
  ├── LoRA fine-tune via Unsloth on gemma4:12b base
  ├── Eval loop using EVAL_RUBRIC scoring
  ├── Export adapter to GGUF
  └── Deploy as theorc-boss:gemma4-ft in Ollama

Phase 4 (FUTURE): Integration
  ├── ModelProfiles entry for theorc-boss:gemma4-ft
  ├── A/B swap path in SwarmSession (prompt-tuned vs fine-tuned)
  └── Benchmark against swarm-metrics.json baselines
```

---

## Base Model Compatibility

See `configs/base_model_compat.json` for the full matrix. Key constraints:

| Base model | Min VRAM (train) | Min VRAM (infer) | Notes |
|---|---|---|---|
| `gemma4:12b` | 16 GB | 8 GB | Primary target. Unsloth template available. |
| `gemma4:12b-it-qat` | 12 GB | 7 GB | QAT base preferred when Ollama tag exists. |
| `qwen2.5-coder:14b` | 20 GB | 10 GB | Already works well; fine-tuning lower priority. |
| `gemma4:26b` | 32 GB | 18 GB | Out of scope for consumer hardware. |

> **QAT note:** `gemma4:12b-it-qat` is not yet available as an Ollama tag (checked 2026-06-09).
> It exists on Hugging Face via Unsloth (`unsloth/gemma-4-12b-it-qat-GGUF`).
> When the Ollama tag lands, update `theorc-boss-gemma4.Modelfile` FROM line.
> Track: https://ollama.com/library/gemma4

---

## Hardware Requirements by Phase

### Data collection (Phase 2)
- Any machine running TheOrc — examples are captured from live runs
- No GPU required; just disk space for the dataset JSONs

### LoRA training (Phase 3)
- **Minimum:** 16 GB VRAM (RTX 3090/4080, A4000) for gemma4:12b at Q4
- **Recommended:** 24 GB VRAM (RTX 4090, A5000) for full bf16 fine-tune
- **QLoRA (4-bit base):** 12 GB VRAM minimum — enables training on RTX 3080/4070Ti
- RAM: 32 GB system RAM recommended
- Storage: ~30 GB for base weights + adapter checkpoints

### Inference after fine-tune (Phase 4)
- Same as base model: 8 GB VRAM for gemma4:12b Q4
- The LoRA adapter adds ~100–300 MB to the deployed GGUF

---

## File Inventory

```
fine_tuning/
  README.md                     ← this file
  DATASET_SCHEMA.md             ← schema for goal→plan training pairs
  EVAL_RUBRIC.md                ← scoring rubric (also drives autoScore in swarm-metrics.json)
  configs/
    lora_job_template.json      ← placeholder LoRA training config
    qlora_job_template.json     ← placeholder QLoRA (4-bit) config
    base_model_compat.json      ← model compatibility and VRAM matrix
  datasets/
    .gitkeep                    ← empty; datasets are NOT committed to git
    CONTRIBUTING.md             ← how to add training examples
  examples/
    good_plan_001.json          ← high-quality boss plan (Combo A, CleanCSV, score 85)
    good_plan_002.json          ← high-quality boss plan (Combo E, CleanCSV, score 68)
    bad_plan_001.json           ← gemma4 collapse pattern (single empty task — negative example)
```

---

## Do Not

- Do not add `trainer.py`, `train.py`, or any Unsloth/transformers training scripts yet
- Do not add `requirements-train.txt` or heavy ML deps to the main project
- Do not launch training jobs or pull training frameworks until Phase 2 is explicitly started
- Do not commit raw dataset files — they belong in a separate data repo or local path

---

*Last updated: 2026-06-09 — Phase 1 scaffolding complete.*
