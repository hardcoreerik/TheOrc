# The Training Pit — Model Compatibility

> **Status:** Phase 1 reference. Updated when new models are tested or availability changes.
>
> **Sources for Gemma 4 12B:** Google blog announcement, Developer Guide, Google AI for Developers
> docs, Google DeepMind page, HuggingFace model card (google/gemma-4-12B), HuggingFace Gemma 4
> blog post. Accessed 2026-06-09.
>
> **No Gemma 4 12B technical report / white paper exists at this time.** No arXiv paper for
> Gemma 4 12B has been found from any official source. Do not cite one.
> (Gemma 3 Technical Report arxiv:2503.19786 exists but applies to the prior generation only.)

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
| `confirmed-external` | Listed as supported in official framework or model documentation; not tested here |
| `inferred` | Architecture documented as supported; not explicitly confirmed for this model |
| `unknown` | No data; do not assume it works |
| `incompatible` | Known to fail or require unsupported configuration |

---

## Gemma 4 12B — Verified Architecture Facts

Source: HuggingFace model card (google/gemma-4-12B), Developer Guide, Google AI for Developers docs.

### Core Parameters

| Property | Value | Source |
|---|---|---|
| Total parameters | 11.95B | HF model card |
| Layers | 48 | HF model card |
| Context window | 256K tokens (262,144) | HF model card, Google AI docs |
| Vocabulary size | 262K tokens | HF model card |
| Sliding window size | 1,024 tokens | HF model card |
| Attention type | Hybrid: alternating local sliding-window + full global | HF model card, HF blog |
| Architecture extras | Dual RoPE, Per-Layer Embeddings (PLE), shared KV cache | HF blog |
| License | Apache 2.0 | Google blog |

### Memory Requirements (Official — Google AI for Developers docs)

| Format | Size | Notes |
|---|---|---|
| BF16 (full precision) | 26.7 GB | Inference; training VRAM is lower with Unsloth optimizations |
| SFP8 | 13.4 GB | Requires SFP8-capable hardware or vLLM/SGLang |
| Q4_0 GGUF | **6.7 GB** | ✅ Confirmed — matches our deployed `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0` |

### Encoder-Free Multimodal Design

Gemma 4 12B has **no separate vision encoder or audio encoder**. This is architecturally
significant — all prior multimodal models used a separate vision backbone (e.g. SigLIP).

- **Vision:** A 35M-parameter lightweight embedder replaces a full encoder. Raw image patches
  (48×48 pixels) are processed via a single matrix multiplication, positional embedding, and
  normalization layers. (Source: Developer Guide)
- **Audio:** The audio encoder was removed entirely. 16 kHz audio is sliced into 40ms frames
  (640 floats per frame) and projected directly into the LLM's embedding space. Max audio: 30s.
  (Source: Developer Guide, HF model card)
- **Video:** Frame sequences up to 60 seconds at 1 fps. Audio track can be extracted simultaneously.
  (Source: HF model card)
- **Visual token budget:** Configurable — 70, 140, 280, 560, or 1120 tokens per image.
  (Source: HF model card)

**Impact on training:** Encoder-free means the whole model can be fine-tuned in a single pass
without special multimodal adapter handling. A LoRA adapter trained on text-only boss planning
examples will not disrupt the vision/audio projections because those weights are not in the
LoRA target modules. (Source: HF blog — "enabling whole-model fine-tuning in a single pass")

### Thinking Mode

Thinking mode is enabled by including the `<|think|>` token in the system prompt.
(Source: HF model card)

Our `theorc-boss-gemma4.Modelfile` uses `think false` as a baked-in Ollama parameter.
This correctly suppresses thinking mode for boss planning use — confirmed working (see
`swarm-metrics.json` benchmark results).

### Recommended Sampling (Google's defaults)

| Parameter | Google recommendation | TheOrc boss config | Reason for deviation |
|---|---|---|---|
| temperature | 1.0 | **0.2** | Lower temp for deterministic JSON output |
| top_p | 0.95 | 0.85 | Tighter for structured output |
| top_k | 64 | 40 | Same rationale |
| think | enabled | **disabled** | Boss planning doesn't benefit from thinking budget |

TheOrc's lower temperature (0.2) is a **deliberate deviation** from Google's general-purpose
recommendations. It trades creative diversity for structured JSON consistency, which is the
correct trade for boss planning tasks. Benchmark Combo A (score 85) with qwen2.5-coder:14b
validates the approach; gemma4 at temperature=0.2 is the adaptation of that pattern.

### Inference Frameworks with Confirmed Gemma 4 Support

These are listed by Google/Hugging Face as supported — not tested in TheOrc's pipeline:

| Framework | Use case | Source |
|---|---|---|
| **Ollama** | Local inference | Developer Guide |
| **Unsloth** | Fine-tuning | Developer Guide — explicitly listed |
| **llama.cpp** | Local inference | Developer Guide, Google AI docs |
| **LiteRT-LM** | Edge / mobile | Developer Guide |
| **Hugging Face Transformers** | Training + inference | HF model card |
| **vLLM** | Production inference (Compressed Tensors QAT) | Google AI docs |
| **SGLang** | Production inference | Google AI docs |
| **MLX** | Apple Silicon inference | HF blog |

---

## Current Model States

### `theorc-boss:gemma4` (deployed)

The active boss model on the Ollama server.

- **Base:** `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0` (Google official QAT GGUF, 6.7 GB)
- **Serving via:** Ollama HF passthrough — not an Ollama library tag
- **Customizations:** `theorc-boss-gemma4.Modelfile` — `think=false`, `temperature=0.2`, 16K
  context cap (model supports 256K; capped for boss planning speed), few-shot examples baked in
- **Inference status:** ✅ Deployed and operational (as of 2026-06-09)
- **Fine-tune status:** Planned (Phase 3) — QLoRA on `google/gemma-4-12b-it` safetensors from HF

**Context window note:** The model natively supports 256K tokens. The `num_ctx 16384` in the
Modelfile is a deliberate cap — boss planning prompts are 600–1200 tokens; the 16K cap keeps
inference fast and VRAM use low. Raise this cap only if goals consistently require broader
context (e.g., large codebase analysis).

### `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0`

- **What it is:** Google's official Q4_0 QAT GGUF, served via Ollama's HF passthrough
- **Inference:** ✅ Installed on server; 6.7 GB confirmed by Google AI docs
- **Training:** ❌ GGUF is inference-only. Use `google/gemma-4-12b-it` safetensors for training.
- **Unsloth compat:** `confirmed-external` — Unsloth explicitly listed as supported in Developer Guide.
  Not yet tested in TheOrc's pipeline; upgrade to `verified` after first training run.

### `gemma4:12b` (Ollama library tag)

- **Inference:** ✅ Available via `ollama pull gemma4:12b`
- **Known issues:** Thinking mode consumes token budget without `think=false` (Ollama 0.30+);
  raw model has planning collapse tendencies; special token leakage fixed by `StripGemma4Artifacts()`
- **Training source:** `google/gemma-4-12b-it` safetensors via HuggingFace

### `qwen2.5-coder:14b`

- **Inference:** ✅ Available; proven high-quality boss (BossScore 6 in benchmarks)
- **Training:** `inferred` — Unsloth supports `qwen_2_5` template
- **Fine-tune priority:** Secondary (after gemma4 boss adapter)

### `qwen2.5-coder:7b`

- **Inference:** ✅ Available; used as goblin worker
- **Training:** `inferred` — Unsloth supports `qwen_2_5` template
- **Fine-tune priority:** Secondary (goblin Python / goblin Tests adapters)

---

## The QAT GGUF vs QAT Training Question

One common confusion: "If we're deploying the QAT GGUF, are we training on QAT weights?"

**Answer: No.** The deployed GGUF is the QAT inference model. For LoRA training, you load
the standard `google/gemma-4-12b-it` safetensors (BF16, 26.7 GB) from HuggingFace and apply
QLoRA quantization at training time. The resulting adapter is then merged and re-exported as
a new GGUF.

If Google publishes trainable QAT safetensors for fine-tuning, you would use those instead.
As of 2026-06-09, only the inference GGUF is available from Google in QAT format.

---

## Upgrade Paths

### When a native `gemma4:12b-it-qat` Ollama library tag appears

```powershell
ollama pull gemma4:12b-it-qat
```
Then update `theorc-boss-gemma4.Modelfile`:
```
FROM gemma4:12b-it-qat
```
Then rebuild:
```powershell
ollama create theorc-boss:gemma4 -f theorc-boss-gemma4.Modelfile
```

### When Unsloth publishes trainable QAT safetensors

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
| 2026-06-09 | v1.1 — Architecture facts added from official sources; memory table confirmed; Unsloth status upgraded to confirmed-external; no white paper note added; sampling deviation documented |
