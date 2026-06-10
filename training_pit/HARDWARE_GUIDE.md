# The Training Pit — Hardware Guide

> **Status:** Phase 1 reference. Review before launching any training job in Phase 3.

---

## VRAM Tiers

| Tier | VRAM | Viable operation | Example GPUs |
|---|---|---|---|
| **Minimal** | 8 GB | Inference only (Q4 models up to 7B) | RTX 3070, RTX 4060 |
| **Low** | 12 GB | QLoRA (12B), inference up to 13B | RTX 3080 12GB, RTX 4070, RTX 3060 12GB |
| **Target** | 16 GB | QLoRA (12B fast), LoRA (7B), inference up to 16B | RTX 4080, RTX 4070 Ti, RTX 3090 |
| **High** | 24 GB | LoRA (12B full), QLoRA (32B) | RTX 4090, RTX 3090 24GB, A5000 |
| **Datacenter** | 40–80 GB | LoRA (32B+), unquantized training | A100, H100, A6000 |

**TheOrc's current hardware:** RTX 5070 Ti (16 GB) — **Target tier**.

This means:
- ✅ QLoRA on gemma4:12b (QLoRA NF4 ≈ 12 GB — fits with headroom)
- ✅ QLoRA on qwen2.5-coder:14b (≈ 14 GB — fits at 16 GB)
- ✅ LoRA on 7B models (≈ 10–12 GB — fits)
- ❌ LoRA on gemma4:12b in bf16 (**official BF16 size: 26.7 GB** — confirmed by Google AI docs; does not fit at 16 GB, use QLoRA)
- ❌ LoRA on 32B models (≈ 28+ GB — requires 24 GB+)

---

## Recommended Configuration for RTX 5070 Ti (16 GB)

For gemma4:12b fine-tuning, use **QLoRA** (`qlora_job_template.json`), not LoRA.

Key settings to fit in 16 GB:
```json
{
  "quantization": "4bit",
  "bnb_4bit_quant_type": "nf4",
  "bnb_4bit_use_double_quant": true,
  "per_device_train_batch_size": 1,
  "gradient_accumulation_steps": 8,
  "gradient_checkpointing": true,
  "optimizer": "paged_adamw_8bit",
  "max_seq_length": 2048
}
```

Expected VRAM usage during training: **~13–14 GB** (leaves ~2 GB headroom for Unsloth overhead).

---

## WSL2 Recommendation

Training in Windows native (PowerShell) is **not recommended**. Use WSL2 instead.

**Why:**
- PyTorch + CUDA training is better supported on Linux
- Unsloth's CUDA kernels are Linux-optimized
- Python package management (pip, conda) is simpler on Linux
- Avoids Windows-specific CUDA DLL issues

**WSL2 Setup for Training:**
```bash
# In WSL2 Ubuntu
pip install unsloth[cu124]   # Match your CUDA version
# Verify GPU access:
python -c "import torch; print(torch.cuda.get_device_name(0))"
```

**CUDA version check (PowerShell):**
```powershell
nvidia-smi | Select-String "CUDA Version"
```

---

## System RAM Requirements

| Operation | Min RAM | Recommended |
|---|---|---|
| Inference only | 16 GB | 32 GB |
| QLoRA training (12B) | 32 GB | 64 GB |
| LoRA training (12B) | 32 GB | 64 GB |
| Paged optimizer (12B) | 32 GB | 64 GB |

The paged optimizer (`paged_adamw_8bit`) offloads optimizer states to CPU RAM when VRAM is
full. With 32 GB system RAM, this is effectively unlimited for 12B model training.

---

## Training Time Estimates

### gemma4:12b QLoRA on RTX 5070 Ti (16 GB)

| Dataset size | Epochs | Estimated time |
|---|---|---|
| 100 examples | 3 | ~0.5 hours |
| 300 examples | 3 | ~1.5 hours |
| 500 examples | 3 | ~2.5 hours |

These estimates assume:
- Unsloth with CUDA 12.4 kernels
- Batch size 1 + gradient accumulation 8
- Sequence length 2048
- Mixed precision bf16 + NF4 base

---

## Checking Your Hardware

Run the hardware check script:
```bash
python training_pit/scripts/check_hardware.py
```

This script detects:
- GPU model and VRAM
- System RAM
- CUDA version
- Recommended training tier (LoRA / QLoRA / inference-only)

---

## Before Starting a Training Job

Checklist:
- [ ] Run `check_hardware.py` — confirm VRAM and CUDA
- [ ] Run `check_model_compatibility.py` — confirm base model is available
- [ ] Run `validate_dataset.py` on your JSONL — confirm no format errors
- [ ] Run `sanitize_dataset.py` — confirm no secrets or PII
- [ ] Confirm `eval_v1.jsonl` has at least 15% of examples held out
- [ ] WSL2 environment set up with Unsloth installed
- [ ] At least 50 GB disk space free (model weights + adapter + GGUF export)

---

## Official Gemma 4 12B Memory Sizes

Source: Google AI for Developers docs (ai.google.dev/gemma/docs/core), accessed 2026-06-09.

| Format | Size | Use case |
|---|---|---|
| BF16 (full precision) | 26.7 GB | Inference reference / LoRA training base |
| SFP8 | 13.4 GB | Production inference (vLLM / SGLang) |
| **Q4_0 GGUF** | **6.7 GB** | **Our deployed model** — confirmed match |

The Q4_0 GGUF (6.7 GB) is what we deploy via Ollama. The BF16 figure (26.7 GB) is the
relevant number for training — loading the BF16 safetensors for LoRA requires ~26.7 GB
*before* Unsloth's CUDA kernel optimizations, which reduce effective training VRAM.

---

## Disk Space Requirements

| Item | Size |
|---|---|
| `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0` (already pulled) | 6.7 GB |
| `google/gemma-4-12b-it` safetensors (for training) | **~26.7 GB** (confirmed) |
| LoRA adapter weights | ~200 MB |
| Merged GGUF export (Q4_K_M) | ~8 GB |
| Training checkpoints (×3 saves) | ~600 MB |
| **Total needed for training pass** | **~37 GB** |

Add another 10 GB buffer for Unsloth cache and tokenizer files.

---

---

## LoRA/QLoRA Guidance for Small Models (≤4B)

### What LoRA/QLoRA Can Improve

LoRA and QLoRA are efficient fine-tuning methods that add trainable adapter weights on top
of a frozen base model. For small models (≤4B), they can improve:

- **Format adherence** — teaching the model to consistently use a specific JSON schema
  or tool-call format without drifting to prose or alternate structures
- **Narrow task specialization** — a tester model that reliably outputs pass/fail verdicts;
  a summarizer that follows a consistent summary template
- **Short structured outputs** — outputs up to ~500 tokens that match a learned pattern

### What LoRA/QLoRA Cannot Fix

**A LoRA adapter cannot turn a 4B model into a reliable long-file autonomous coder.**

The reason is architectural: a model with 4B active parameters has a hard ceiling on how
much context state it can maintain while generating a long, nested JSON payload. When
writing `write_file app.py` with 200 lines of Python encoded as a JSON string, the model
must track `{` / `}` balance, escape state, and code coherence simultaneously across
hundreds of tokens. This is a parameter-count capacity issue — fine-tuning changes the
adapter weights, not the base model's capacity.

T06 confirmed (2026-06-09): even at Q8 precision (highest quality for the 4B weights),
`nemotron-3-nano:4b-q8_0` truncates write_file JSON on every pass. Q8 vs Q4 does not
change the active parameter count (3.6B in both cases).

### Best Adapter Target for Nemotron Nano 4B

If a Nano 4B adapter is ever built, the correct target is:

```
NARROW TESTER / LOG SUMMARIZER adapter
  Goal:        Improve reliability of short structured outputs (< 100 tokens)
  Training:    ~100–200 examples of: [log content] → [pass/fail verdict + reason]
  NOT a goal:  Multi-file code generation, long write_file payloads
  Dataset:     Short prompt → short structured response pairs (tester verdicts, summaries)
  Expected improvement: format adherence and verdict consistency for lightweight roles
```

### Primary LoRA Target (Phase 3 — boss planning)

The first Training Pit LoRA target remains the **boss-planning adapter for Gemma 4 12B QAT**.

```
Priority 1:  gemma4:12b — boss-planning QLoRA adapter
  Goal:      Improve task decomposition quality and JSON plan consistency
  Evidence:  swarm benchmarks show planning collapse on plain gemma4:12b; theorc-boss:gemma4
             (few-shot tuned) partially addresses this — a real adapter will do more
  Hardware:  RTX 5070 Ti (16 GB) — QLoRA NF4 ≈ 12–14 GB, fits with headroom
  Dataset:   boss_plan_examples.jsonl (≥150 examples required before Phase 3 start)

Priority 2:  qwen2.5-coder:7b or qwen2.5-coder:14b — goblin coder adapter (future)
Priority 3:  nemotron-3-nano:4b — narrow tester adapter (low priority, narrow scope)
```

---

*Last updated: 2026-06-09 — v1.2: LoRA/QLoRA guidance for small models added; Nemotron Nano 4B adapter scope documented; T06 evidence referenced.*
