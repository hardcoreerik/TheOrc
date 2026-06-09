# The Training Pit — Model Compatibility

> **Status:** Phase 1 reference. Updated when new models are tested or availability changes.

---

## Ollama Tags vs HuggingFace Repos

These are two different things and must not be confused:

| Concept | Example | What it is |
|---|---|---|
| **Ollama library tag** | `gemma4:12b` | Ollama's own curated registry; `ollama pull gemma4:12b` |
| **Ollama HF passthrough tag** | `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0` | Ollama fetches directly from HuggingFace |
| **HuggingFace repo** | `google/gemma-4-12b-it` | Source weights; used by training frameworks (Unsloth, PEFT) |

The `hf.co/` Ollama prefix is **inference only** — it pulls a GGUF from HuggingFace for
Ollama to serve. It is not a training source. For training, you use the HuggingFace repo
directly via `from_pretrained()`.

---

## GGUF Inference vs Training Source

| Format | Used for | Notes |
|---|---|---|
| **GGUF** (`.gguf`) | Inference via Ollama / llama.cpp | Quantized weights; cannot be fine-tuned directly |
| **Safetensors / bin** | Training via Unsloth / PEFT | Full-precision or bf16 weights; large (20–50 GB) |
| **QAT GGUF** | Inference only | Quantization-aware trained GGUF; still inference-only |

**QAT clarification:** "QAT" (Quantization-Aware Training) means the quantization was applied
*during* training by Google, preserving more quality at 4-bit than post-hoc quantization. The
resulting file is still a GGUF — it cannot be used as a training source. For LoRA/QLoRA training,
use the bf16 safetensors from the HuggingFace repo, not the GGUF.

---

## Compatibility States

Each model entry in `configs/base_model_compat.json` carries a `compat_state` per training framework:

| State | Meaning |
|---|---|
| `verified` | Tested and confirmed working in TheOrc's training pipeline |
| `inferred` | Unsloth/PEFT document support; expected to work; not tested here |
| `unknown` | No data; do not assume it works |
| `incompatible` | Known to fail or require unsupported configuration |

---

## Current Model States

### `theorc-boss:gemma4` (deployed)

The active boss model on the Ollama server.

- **Base:** `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0` (Google QAT GGUF, 6.7 GB)
- **Customizations:** `theorc-boss-gemma4.Modelfile` — `think=false`, `temperature=0.2`,
  few-shot examples baked in via MESSAGE pairs
- **Inference status:** ✅ Deployed and operational
- **Fine-tune status:** Planned (Phase 3) — LoRA on bf16 safetensors from HF

### `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0`

- **Inference:** ✅ Available via Ollama HF passthrough; installed on server
- **Training:** ❌ GGUF is inference-only. For training, use `google/gemma-4-12b-it` safetensors.
- **Unsloth compat:** `inferred` — Unsloth supports Gemma4 architecture

### `gemma4:12b` (Ollama library)

- **Inference:** ✅ Available via `ollama pull gemma4:12b`
- **Known issues:** `think=false` required for Ollama 0.30+; raw model has planning collapse
  tendencies on complex goals
- **Training:** `inferred` — use Unsloth's `FastLanguageModel.from_pretrained('google/gemma-4-12b-it')`

### `qwen2.5-coder:14b`

- **Inference:** ✅ Available; proven high-quality boss (BossScore 6 in benchmarks)
- **Training:** `inferred` — Unsloth supports `qwen_2_5` template
- **Fine-tune priority:** Secondary (after gemma4 boss adapter)

### `qwen2.5-coder:7b`

- **Inference:** ✅ Available; used as goblin worker
- **Training:** `inferred` — Unsloth supports `qwen_2_5` template; 7B is the primary target for goblin adapters
- **Fine-tune priority:** Secondary (goblin Python / goblin Tests adapters)

---

## The QAT GGUF vs QAT Training Question

One common confusion: "If we're deploying the QAT GGUF, are we training on QAT weights?"

**Answer: No.** The deployed GGUF is the QAT inference model. For LoRA training, you load
the standard `google/gemma-4-12b-it` safetensors from HuggingFace and apply QLoRA quantization
at training time. The resulting adapter is then merged and re-exported as a new GGUF.

If Google publishes trainable QAT safetensors (e.g. `unsloth/gemma-4-12b-it-qat`), you would
use those instead — but as of 2026-06-09, only the inference GGUF is available from Google.

---

## Upgrade Paths

### When a native `gemma4:12b-it-qat` Ollama tag appears

```
ollama pull gemma4:12b-it-qat
```
Then update `theorc-boss-gemma4.Modelfile`:
```
FROM gemma4:12b-it-qat
```
Then rebuild:
```
ollama create theorc-boss:gemma4 -f theorc-boss-gemma4.Modelfile
```

### When Unsloth publishes `unsloth/gemma-4-12b-it-qat` for training

Update `configs/lora_job_template.json` and `configs/qlora_job_template.json`:
```json
"hf_repo": "unsloth/gemma-4-12b-it-qat"
```
This would give QAT-quality weights as the LoRA training base, improving final adapter quality.

---

## Version History

| Date | Change |
|---|---|
| 2026-06-09 | Initial document — QAT GGUF deployed, compatibility states defined |
