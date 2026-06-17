<!-- Copyright (C) 2025-present hardcoreerik / TheOrc contributors | SPDX-License-Identifier: AGPL-3.0-or-later -->
# TheOrc — Training Flags & Methods Guide

**Audience:** anyone driving the Training Pit / Forge, and the Pit Boss advisor.
**Hardware baseline:** single RTX 5070 Ti (16 GB), Gemma 4 12B, QLoRA.
**Authoritative trainer:** `training_pit/scripts/train_lora.py` (HF PEFT + TRL).
**Last research pass:** 2026-06-17.

This guide is grounded in TheOrc's own runs, not generic advice. Two real
data points anchor everything below:

| Run | Examples | Config | eval_loss | Real rubric result |
|---|---|---|---|---|
| **v1** | 900 (mostly human-reviewed) | r16/α32, 3 ep, lr 2e-4 | 0.266 | **99.3%** ✅ |
| **v2** | 1,861 (85% unscreened synthetic) | identical | **0.2595** (better!) | **77.8%** ❌ regressed |

> **The first law of the Training Pit:** loss is a *proxy*, not the objective.
> v2 had a lower eval loss and worse behavior. The rubric (`eval_adapter.py`)
> is the real objective. No flag would have saved v2 — it was the data.

So the honest priority order for getting a better adapter:

```
data quality  ≫  data quantity  >  learning rate  >  LoRA target/rank
              >  schedule  >  init method (DoRA/PiSSA/…)  >  everything else
```

Current research backs this ranking. "Learning Rate Matters: Vanilla LoRA May
Suffice" (2026) shows that when every method's LR is tuned, the spread between
vanilla LoRA and DoRA/PiSSA/MiLoRA is **0.15–0.43%** — the fancy methods mostly
just need *different* learning rates, not better math.

---

## Part 1 — The flags that matter, grouped by what they control

### A. LoRA structure — *what* gets trained

| Flag | Ours | Theory | Hardware-proven guidance |
|---|---|---|---|
| `r` (rank) | 16 | Dimensionality of the low-rank update ΔW = B·A. Higher r = more capacity + more overfit risk + more VRAM. | Single narrow skill (boss = one JSON format): **r=16 is plenty, r=8 likely matches.** Multi-language workers: **r=32**. 2025 consensus: intermediate ranks 32–64 best balance capacity/stability for broad tasks. |
| `lora_alpha` | 32 | Update is scaled by `α/r`. Ours = 2.0×. | Keep the 2:1 ratio when changing rank — *but* α interacts with LR (see rsLoRA). Don't tune α and LR independently; they're the same dial twice. |
| `lora_dropout` | 0.05 | Regularizes adapter activations. | Fine. → 0.1 only if train/eval loss visibly diverge. |
| `target_modules` | all 7 proj | Which matrices get adapters. Ours: attn (q/k/v/o) + MLP (gate/up/down). | Correct aggressive default ("all-linear" in HF terms). Escape hatch: add `embed_tokens`/`lm_head` only if a format won't stick — rarely needed for our task. |
| `bias` | none | Train bias terms or not. | Leave `none`. Other modes complicate merge/GGUF export for negligible gain. |

### B. Quantization — *how the frozen base is stored* (QLoRA core)

All four of ours are textbook-correct; this is "why," not "change it":

| Flag | Ours | Why correct |
|---|---|---|
| `load_in_4bit` | true | Only way 12B *trains* on 16 GB. |
| `bnb_4bit_quant_type` | **nf4** | NF4 is information-theoretically optimal for the ~normal-distributed weights of an LLM. Always beats fp4. |
| `bnb_4bit_use_double_quant` | true | Quantizes the quant constants too — ~0.4 bits/param saved, negligible quality loss. Free on a tight card. |
| `bnb_4bit_compute_dtype` | bf16 | 4-bit storage, bf16 compute. Right for Ampere+ (5070 Ti). |

**Forward-looking:** a QAT base (`unsloth/gemma-4-12b-it-qat`) + QLoRA beats
quantizing a non-QAT base — the model was *trained* to tolerate 4-bit. We already
deploy the QAT GGUF at inference; worth one deliberate A/B for v3.

### C. Optimization — *how fast it learns* (the highest-ROI tuning surface)

| Flag | Ours | Theory + guidance |
|---|---|---|
| `learning_rate` | 2e-4 | **The single most important knob** (per current research). Standard LoRA LR. For a strict-format task, **erring low (1e-4) reduces over-memorizing** — including memorizing bad patterns. Note: if you ever switch to PiSSA init, drop LR ~10× (it has a larger max Hessian eigenvalue → needs gentler steps). |
| `lr_scheduler_type` | cosine | Decays LR to ~0 on a cosine. Solid default. `linear` fine; avoid `constant` (late-training thrash). |
| `warmup_steps` | 10 (fixed) | Stabilizes early steps while Adam moments are noisy. **At v2/v3 dataset sizes, switch to `warmup_ratio=0.03`** — 10 fixed steps is too short as step count grows. |
| `weight_decay` | 0.01 | Mild adapter-param regularization. Low impact for LoRA; leave. |
| `optim` | adamw_8bit | 8-bit optimizer states cut optimizer memory ~60–75%. Essential at 16 GB, negligible quality cost. |
| `max_grad_norm` | unset → 1.0 | Gradient clipping (HF default 1.0 = sane). Set explicitly for visibility; → 0.5 only if loss spikes appear. |

### D. Throughput / memory — *fitting 12B on 16 GB*

| Flag | Ours | Reasoning |
|---|---|---|
| `per_device_train_batch_size` | 1 | Template says 2; script uses **1** because 16 GB. |
| `gradient_accumulation_steps` | 8 | **Effective batch = 1×8 = 8.** Accumulation sums grads over micro-steps → same math as bs8 at 1/8 the memory. Raise to 16 for a smoother gradient on noisy worker data (costs time, not VRAM). |
| `per_device_eval_batch_size` | 1 | **Gemma gotcha:** the ~262k-token vocab makes eval logits (`batch×seq×262k`) huge — default eval bs=8 OOMs even when train fits. Keep at 1. |
| `gradient_checkpointing` | true | Recomputes activations in backward instead of storing them — ~20–30% slower, big memory save. Mandatory here. Needs `use_cache=False` (we set it). |
| `bf16` | true | Mixed-precision. Correct for this GPU. |
| `vram_cap`/`max_memory` | optional | Offload-to-RAM escape hatch so training coexists with other GPU work. Offloaded layers are *much* slower — share-the-card only. |

### E. Schedule & checkpointing — *when to stop, which checkpoint to keep*

| Flag | Ours | Notes |
|---|---|---|
| `num_train_epochs` | 3 | More data → fewer epochs needed. **Consider 2 for the 1,800+ sets** (overfit risk rises with repeats × volume). |
| `eval_steps`/`save_steps` | 50/50 | Good cadence. |
| `save_total_limit` | 2 | Bounds disk. Fine. |
| `load_best_model_at_end` | true | Restores lowest-`eval_loss` checkpoint… |
| `metric_for_best_model` | eval_loss | **…which is the trap.** Best eval_loss ≠ best rubric (v2 proved it). See Part 3. |

---

## Part 2 — Current research methods (and whether they're worth it for us)

Honest verdicts for *our* task and hardware. The meta-finding (LR matters more
than method) means these are fine-tuning-the-fine-tuning — pursue only after
data + LR are nailed.

| Method | What it changes | VRAM/Speed cost | Verdict for TheOrc |
|---|---|---|---|
| **QLoRA** (have) | 4-bit NF4 frozen base + LoRA | baseline | **Keep.** It's what makes 12B trainable at all on 16 GB. |
| **DoRA** (`use_dora=True`) | Splits weights into magnitude + direction, adapts both | +20–30% train time, modest VRAM | **Worth a test on workers.** Best-documented low-rank quality bump; lets us stay at r=8–16 cheaply. Watch the 16 GB ceiling. |
| **rsLoRA** (`use_rslora=True`) | Scale becomes `α/√r` instead of `α/r` | ~free | **Enable when we raise rank to 32+.** Prevents the larger update from over-scaling/destabilizing. Free insurance. |
| **PiSSA** (init) | Inits adapter from principal singular vectors → faster convergence, less quant error | ~free (one-time SVD) | **Promising but needs ~10× lower LR** — don't drop it in without re-tuning LR or it'll diverge. Candidate for a careful v3 A/B. |
| **LoftQ** (init) | Inits LoRA to minimize quantization error | ~free | **Use if 4-bit quality loss is visible** (it isn't obviously, for us). A QLoRA-specific quality patch. |
| **OLoRA / EVA / MiLoRA** | Other principled inits | ~free | Marginal (≤0.4% in head-to-heads). Skip unless chasing the last fraction. |
| **GaLore** | Not LoRA — full-param training via low-rank *gradient* projection | Fits 7B full-FT in 24 GB; **12B won't fit our 16 GB** | **Out of reach** on this card for 12B. Note for a future 24 GB+ box; can outperform LoRA but needs the headroom. |

**The through-line:** start vanilla (what we have), tune LR first, then test
**DoRA on workers** and **rsLoRA when rank goes up**. PiSSA/LoftQ are init-time
experiments worth one controlled run each — never stacked blindly.

---

## Part 3 — The one upgrade that directly fixes our failure mode

v2's regression went undetected for 3.5 hours because we select checkpoints on
`eval_loss`, and the best-loss checkpoint was behaviorally worse.

**Rubric-in-the-loop checkpoint selection.** We already have the scorer
(`eval_adapter.score_plan`). Wire a lightweight rubric pass into the training
loop on a `TrainerCallback` (e.g. every `save_steps`, on a 25–40 example slice),
log `rubric_pass_pct`, and set `metric_for_best_model="rubric_pass_pct"`,
`greater_is_better=True`. Then `load_best_model_at_end` keeps the best *behaving*
checkpoint, not the best *loss* one — and the Forge GUI can plot rubric vs loss
diverging in real time. This is higher leverage than any init method:

- catches regressions mid-run instead of post-hoc,
- directly optimizes the thing we ship on,
- needs no new dependency (the scorer exists).

Cost: a short eval every N steps (minutes), well worth it. This is the
recommended companion build to `ContentAwareDatasets_v1.md`.

---

## Part 4 — Proven recipes for the next runs

Defaults Pit Boss should propose, by target. "Proven" = matches v1's winning
config except where research/our-data argues otherwise.

| Knob | v3 Boss (precision, clean data) | v4 Tester Worker | Coder Worker (multi-lang) |
|---|---|---|---|
| **data** | filtered clean only (the real fix) | 794 rerouted tester examples | language-sliced coder examples |
| `r` / `alpha` | 16 / 32 | 16 / 32 | **32 / 64 + rsLoRA** |
| init | vanilla (or PiSSA A/B) | vanilla | vanilla, then DoRA test |
| `learning_rate` | **1e-4** | 2e-4 | 2e-4 (1e-4 if PiSSA) |
| `num_train_epochs` | **2** | 3 | 2–3 |
| `warmup` | **ratio 0.03** | ratio 0.03 | ratio 0.03 |
| `grad_accum` | 8 | 8–16 | 16 (smoother) |
| checkpoint metric | **rubric_pass_pct** | rubric_pass_pct | eval_loss ok |
| NEFTune | **off** (strict JSON) | try `alpha=5` | try `alpha=5` |
| everything else | = `train_lora.py` defaults | " | " |

**NEFTune note:** embedding-noise (`neftune_noise_alpha`) reliably helps
instruction-following but can hurt *exact-format* output — keep it **off for the
boss** (strict JSON), **test it on workers** where output is freer.

**Estimated wall-clock on the 5070 Ti:** v1 was 900 ex × 3 ep ≈ 149 min. Scale
roughly linearly with `examples × epochs`; rubric-in-loop adds a few min per eval
checkpoint; DoRA adds ~25%.

---

## Part 5 — Anti-patterns (learned the hard way)

- **Chasing eval_loss.** It is not what we ship. Always confirm with the rubric.
- **Letting unscreened synthetic dominate a single-target run.** v2's root cause.
  Filter through the rubric *before* `finalize` (see `ContentAwareDatasets_v1.md`).
- **Stacking init methods + new LR blindly.** PiSSA/DoRA/rsLoRA each shift the
  optimal LR; change one variable per run.
- **Default eval batch size on Gemma.** The 262k vocab OOMs it — keep eval bs=1.
- **Bumping rank to "fix quality" first.** Per current research, tune LR before
  reaching for capacity; rank is a later lever.

---

## Sources

- [Learning Rate Matters: Vanilla LoRA May Suffice for LLM Fine-tuning (2026)](https://arxiv.org/html/2602.04998v1)
- [PiSSA: Principal Singular Values and Singular Vectors Adaptation](https://arxiv.org/pdf/2404.02948)
- [HuggingFace PEFT — LoRA developer guide (DoRA, rsLoRA, LoftQ, PiSSA flags)](https://huggingface.co/docs/peft/developer_guides/lora)
- [Advanced LoRA Fine-Tuning: How to Pick LoRA/QLoRA/DoRA/PiSSA/OLoRA/EVA/LoftQ (The Kaitchup)](https://kaitchup.substack.com/p/advanced-lora-fine-tuning-how-to)
- [Memory-efficient LLM training with GaLore](https://medium.com/@geronimo7/llm-training-on-consumer-gpus-with-galore-d25075143cfb)
- [Profiling LoRA/QLoRA Fine-Tuning Efficiency on Consumer GPUs: an RTX 4060 case study (2025)](https://arxiv.org/html/2509.12229v1)
- Internal: `training_pit/scripts/train_lora.py`, `training_pit/configs/{lora,qlora}_job_template.json`, ORC ACADEMY v1/v2 run logs.
