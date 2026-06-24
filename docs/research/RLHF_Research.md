# RLHF, Refusal Mechanisms, and De-Alignment Techniques — Research for TheOrc

> Theory and feasibility research only. No operational harm instructions. This document
> covers published, widely-discussed ML interpretability and fine-tuning research — the
> same territory covered by Anthropic's own interpretability work, EleutherAI, and the
> model cards of hundreds of public Hugging Face "uncensored" variants.

## 1. What RLHF Is, and How It Produces Refusals

**Reinforcement Learning from Human Feedback** is the standard alignment pipeline for
instruction-tuned LLMs, run in three stages:

1. **Supervised Fine-Tuning (SFT)** — the base model is fine-tuned on curated
   instruction/response pairs, including examples of refusing harmful requests.
2. **Reward Modeling (RM)** — human raters rank multiple model outputs for the same
   prompt; a separate reward model is trained to predict these preference scores,
   learning to score "helpful, harmless" responses higher and unsafe-but-compliant
   responses lower.
3. **Policy Optimization (PPO, or more recently DPO)** — the SFT model ("policy") is
   further trained to maximize the reward model's score, using PPO (Proximal Policy
   Optimization, the original RLHF method) or DPO (Direct Preference Optimization, which
   skips the separate reward model and trains directly on preference pairs — now more
   common because it's simpler and more stable to train).

**The mechanistic finding that matters for de-alignment**: Arditi et al., *"Refusal in
Language Models Is Mediated by a Single Direction"* (2024,
[arxiv.org/pdf/2406.11717](https://arxiv.org/pdf/2406.11717)), showed that across many
open-weight chat models, refusal behavior is mediated by a single direction (a vector) in
the residual stream's activation space — not diffusely encoded across the whole network.
Projecting a harmful prompt's activations onto this direction strongly predicts whether
the model will refuse; ablating (zeroing) that direction causes the model to comply with
requests it would otherwise refuse, while largely preserving general capability. A 2026
follow-up, *"There Is More to Refusal in Large Language Models than a Single Direction"*
([arxiv.org/html/2602.02132](https://arxiv.org/html/2602.02132)), complicates this finding
— in some models refusal is mediated by a small *set* of directions rather than exactly
one, and the single-direction model is an approximation that works well in practice but
isn't universally exact.

In short: RLHF doesn't refuse by "understanding" harm in any deep semantic sense — it
learns a fairly low-dimensional, often near-linear feature in activation space that gates
a "decline to answer" behavior, trained in via the RM/PPO-or-DPO loop. This low
dimensionality is *why* refusal turns out to be comparatively easy to remove post-hoc,
compared to, say, removing a capability that's diffusely distributed across the model.

## 2. Published De-Alignment Techniques

### 2.1 Abliteration (Refusal-Direction Ablation)

**What it is**: Directly exploiting the Arditi et al. finding. Run a set of harmful and
harmless prompts through the model, compute the activation difference at each layer to
identify the refusal direction, then either (a) zero out that direction's contribution at
every layer via a modified forward pass, or (b) "ortho-projection" the weight matrices
themselves so the direction is permanently removed from the model's weights, producing a
new, fully de-aligned checkpoint with no inference-time overhead.

**Tooling**: Maxime Labonne's widely-cited guide, *"Uncensor any LLM with abliteration"*
([medium.com/@mlabonne/uncensor-any-llm-with-abliteration-d30148b7d43e](https://medium.com/@mlabonne/uncensor-any-llm-with-abliteration-d30148b7d43e)),
popularized a practical, reproducible abliteration pipeline (originally built on
`TransformerLens` for interpretability-style activation access, later ported to plain
PyTorch/`transformers` hooks for production use). **Heretic** is a more recent,
"fully automatic" abliteration tool that optimizes which layers/directions to ablate via a
search procedure rather than manual tuning
([darkwebinformer.com/heretic-...](https://darkwebinformer.com/heretic-fully-automatic-censorship-removal-for-language-models-via-optimized-abliteration/)).

**Key constraint**: abliteration operates on **raw model weights** (safetensors/PyTorch),
not on a quantized inference format. It requires the same kind of forward-pass
instrumentation used in interpretability research (hooking residual-stream activations at
each layer), which is a first-class feature of PyTorch model code but not something
`llama.cpp`/GGUF inference engines expose. Practically, this means abliteration is a
**pre-processing step that happens before GGUF conversion**, not something you can do to
an already-quantized model file in-place.

### 2.2 Refusal-Removal Fine-Tuning

**What it is**: Skip activation surgery entirely — just continue training (full
fine-tune, or more practically QLoRA/LoRA) on a dataset of compliant responses to prompts
the base model would normally refuse, directly counter-training the RLHF-learned refusal
behavior via ordinary supervised fine-tuning or DPO with flipped preference labels. This
is **exactly what the "Dolphin" model series from cognitivecomputations does**, and what
most "uncensored" Hugging Face variants are: a continued fine-tune on a curated
permissive/compliant dataset, not activation surgery
([huggingface.co/cognitivecomputations/dolphin-3.0-qwen2.5-3b-GGUF](https://huggingface.co/cognitivecomputations/dolphin-3.0-qwen2.5-3b-GGUF)
documents this directly in its model card). A reference open-source implementation of this
pattern: [github.com/SahilChachra/Refusal-Finetuning](https://github.com/SahilChachra/Refusal-Finetuning).

There's also published evidence this works on closed models accessed via fine-tuning
APIs, not just open weights — Zhan et al., *"Removing RLHF Protections in GPT-4 via
Fine-Tuning"* (2023, [arxiv.org/pdf/2311.05553](https://arxiv.org/pdf/2311.05553)), showed
that even GPT-4's safety training degrades substantially after a modest amount of
adversarial fine-tuning through OpenAI's own fine-tuning API — refusal training is not
robust to continued training in general, regardless of whether you have weight access.

**Key advantage for TheOrc specifically**: this is **ordinary supervised fine-tuning**,
the exact thing ORC ACADEMY already does. No activation hooking, no interpretability
tooling, no format constraints — just a different training dataset and (for DPO-style
approaches) a different loss function.

### 2.3 Activation Steering / Representation Engineering

**What it is**: A more general technique family than abliteration specifically — instead
of finding and removing one "refusal direction," find a steering vector for *any* desired
behavioral axis (honesty, sycophancy, formality, refusal, etc.) via paired
contrastive prompts, then **add** a scaled version of that vector to the residual stream
at inference time to push behavior in the desired direction, without modifying weights at
all. Rimsky et al., *"Steering Llama 2 via Contrastive Activation Addition"*
([arxiv.org/html/2312.06681v4](https://arxiv.org/html/2312.06681v4)) is the most-cited
reference implementation of this for refusal/safety-adjacent behaviors specifically.
"Representation Engineering" is the broader academic framing for this family of
techniques (treating a model's internal representations as a steerable, inspectable
object rather than a black box).

**Key tradeoff vs. abliteration**: steering is reversible and tunable at inference time
(dial the steering strength up/down, or turn it off entirely) but requires runtime access
to intermediate activations on every forward pass — a heavier integration requirement than
a static, pre-baked abliterated checkpoint.

## 3. Feasibility for TheOrc

TheOrc has two plausible integration points: **ORC ACADEMY** (existing local LoRA
fine-tuning) and the **Native Runtime** (LLamaSharp/GGUF in-process inference).

### 3.1 ORC ACADEMY extension (refusal-removal fine-tuning) — **MEDIUM difficulty**

This is the natural fit. ORC ACADEMY already runs QLoRA fine-tuning jobs locally against
a base model; refusal-removal fine-tuning is the *same kind of job* with a different
training dataset (compliant responses to commonly-refused prompt categories) and
optionally a DPO-style loss instead of plain SFT if ORC ACADEMY's training pipeline
supports preference-pair training. Concretely this would mean: (1) a new dataset
template/category in the Training Pit's dataset tooling for "uncensoring" data, (2) no
change at all to the actual QLoRA training loop, since the mechanism is identical to
every other fine-tune ORC ACADEMY already runs. **Estimated effort: low-to-medium** —
mostly dataset curation/tooling work, not new training infrastructure. This is the
technique already used by every Dolphin/Hermes-style "uncensored" model on Hugging Face,
so it's also the most battle-tested approach to imitate.

### 3.2 Native Runtime (LLamaSharp/GGUF) integration — **HIGH difficulty**

Both abliteration and activation steering require hooking into a model's intermediate
layer activations during the forward pass. `llama.cpp` (and therefore LLamaSharp, which
wraps it) is built around a fixed, optimized inference graph for quantized GGUF tensors —
it does not expose a stable, public API for reading or modifying residual-stream
activations mid-forward-pass the way PyTorch's hook system does. This means:

- **True in-process abliteration or steering against an already-loaded GGUF model is not
  practical** with LLamaSharp as it exists today. It would require either patching
  llama.cpp itself (a C++ change upstream, not a LLamaSharp-side change) or abandoning
  GGUF for a PyTorch-based local inference path for this one feature, which conflicts with
  the whole reason Native Runtime exists (fast, quantized, low-memory local inference).
- **The practical alternative**: do abliteration as a **pre-processing step on the
  original safetensors/PyTorch checkpoint**, before GGUF conversion — i.e., a one-time
  "abliterate, then quantize to GGUF" pipeline, producing a new static GGUF file that
  LLamaSharp loads completely normally afterward (no runtime changes needed at all, since
  the de-alignment already happened in the weights). This is exactly how every existing
  "abliterated" model on Hugging Face is distributed already (e.g. search
  "abliterated" GGUF on Hugging Face — they're static files, not models with runtime
  steering hooks).
- Inference-time activation steering specifically (section 2.3) is the technique most
  blocked by this constraint, since by definition it needs a live hook on every forward
  pass. A weaker approximation — prompt-level "steering" via a strong system prompt or
  few-shot priming — is achievable today with zero runtime changes, but is not the same
  technique and is considerably less robust (a system prompt is just more text the model
  can still be steered away from; an activation-space intervention is not).

### 3.3 Recommendation

**Phase 1**: extend ORC ACADEMY with a refusal-removal fine-tuning dataset/recipe. Lowest
effort, reuses 100% of existing training infrastructure, and is the same technique that
already produces every real-world "uncensored" model referenced in the tier guide.

**Phase 2+ (if pursued)**: a separate, offline "abliteration pipeline" tool — takes a
HF safetensors checkpoint, runs activation-difference abliteration (Labonne's
method/Heretic), outputs a de-aligned safetensors checkpoint, then hands off to the
existing GGUF conversion/quantization tooling. This is a genuinely separate feature from
the Native Runtime itself (it operates on PyTorch checkpoints, before GGUF exists at all)
and would need a Python dependency (`transformers`/`torch`) that TheOrc's C# runtime
doesn't currently carry — a real architectural cost worth weighing against how much
differentiator value it actually adds over "ship the three pre-existing Dolphin models
that already do this via fine-tuning," which needs none of that.

## Sources

- [Refusal in Language Models Is Mediated by a Single Direction — Arditi et al., 2024](https://arxiv.org/pdf/2406.11717)
- [There Is More to Refusal in Large Language Models than a Single Direction, 2026](https://arxiv.org/html/2602.02132)
- [Uncensor any LLM with abliteration — Maxime Labonne](https://medium.com/@mlabonne/uncensor-any-llm-with-abliteration-d30148b7d43e)
- [Heretic: Fully Automatic Censorship Removal via Optimized Abliteration](https://darkwebinformer.com/heretic-fully-automatic-censorship-removal-for-language-models-via-optimized-abliteration/)
- [Dolphin 3.0 Qwen2.5-3B model card — Cognitive Computations](https://huggingface.co/cognitivecomputations/dolphin-3.0-qwen2.5-3b-GGUF)
- [Refusal-Finetuning reference implementation — Sahil Chachra](https://github.com/SahilChachra/Refusal-Finetuning)
- [Removing RLHF Protections in GPT-4 via Fine-Tuning — Zhan et al., 2023](https://arxiv.org/pdf/2311.05553)
- [Steering Llama 2 via Contrastive Activation Addition — Rimsky et al., 2023](https://arxiv.org/html/2312.06681v4)
- [LLamaSharp (LLamaSharp/llama.cpp wrapper used by TheOrc's Native Runtime)](https://github.com/SciSharp/LLamaSharp)
