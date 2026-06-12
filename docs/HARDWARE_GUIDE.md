# TheOrc — Hardware Guide

> This guide maps hardware to what the current TheOrc code can realistically do. For training flow, pair it with [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md). For role-aware model selection, see [MODEL_GUIDE.md](MODEL_GUIDE.md).

---

## What Hardware Changes In TheOrc

Hardware changes three major things:

- which models fit
- whether swarm role splitting is worthwhile
- whether local adapter training is comfortable or constrained

The app itself is hardware-aware:

- `SwarmConfigAdvisor` detects NVIDIA VRAM through `nvidia-smi`
- the Training Pit panel polls VRAM live for ORC ACADEMY
- the shell can point to a stronger remote Ollama host when local hardware is weak

---

## Practical Inference Tiers

### Under 8 GB VRAM

This is the constrained tier.

Expect:

- smaller coding models
- more caution around long writes
- swarm configurations that favor lighter workers

### 8 to 16 GB VRAM

This is the most practical local-developer tier for the current product.

Expect:

- good single-agent behavior from the right 7B to 14B models
- viable swarm experiments
- realistic use of the current QLoRA training path with care

### 24 GB And Up

This is where larger local-model choices and more relaxed role packing become practical.

Expect:

- stronger boss/coder combinations
- fewer compromises on concurrent role assignments
- more comfortable headroom for training experiments

---

## Swarm-Specific Hardware Effects

Swarm hardware pressure is not just "one big model plus one more."

The current code explicitly reasons about:

- boss model fit
- coder model fit
- researcher model reuse or eviction
- tester model cheapness

This is why Swarm Board model slots and metrics history matter more than a single global recommendation.

---

## Training Hardware Reality

The current training path is built around QLoRA-style efficiency, not full-precision luxury training.

Verified from the code:

- `train_lora.py` uses 4-bit NF4 loading
- the Training Pit GUI exposes a VRAM cap
- progress is heartbeat-driven
- resume and checkpointing are built in

The current repository is therefore optimized for "practical local fine-tuning with constraints," not for pretending every workstation is a datacenter box.

---

## Remote Host Strategy

If your local machine is good enough for the shell but not ideal for inference, the current app can still benefit from stronger hardware by pointing at another Ollama host in Settings.

This is the simplest version of hardware separation already present today, and it is the architectural bridge toward the planned HIVE MIND layer.
