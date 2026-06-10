# TheOrc — Sponsor Test Lab

> Transparent hardware compatibility testing for local AI on real consumer GPUs.

---

## What Is TheOrc?

**TheOrc** is a native Windows desktop AI coding assistant that runs 100% on your hardware — no cloud, no subscriptions, no data leaving your machine.

It is built on top of Ollama and llama.cpp, and exposes a full agent loop with plan/execute safety gates, diff approval before any file write, and git auto-checkpoints before every run. The **Goblin Swarm** mode deploys a coordinated squad of specialist agents (Boss, Coder, Researcher, UI Developer, Tester) that work in parallel on a single goal.

**Core features:**

| Feature | Description |
|---|---|
| Single-agent mode | Plan → Review → Execute loop with diff approval and shell approval cards |
| Goblin Swarm | Multi-agent orchestration across 4 specialist roles |
| Model Wiki / Lab | Browseable model catalogue with per-model capability scores and a live test runner |
| Training Pit | Structured tool-call training data generation for LoRA fine-tuning |
| GOBLIN MIND | Runtime tool-call compatibility prober — behavioral format fingerprinting, category boundary mapping, schema reduction middleware |
| Hardware-aware routing | Reads your GPU, VRAM, and run history to recommend the right model per swarm role |

TheOrc is free, open source (MIT), and Windows-first. A cross-platform Docker + Blazor port is on the roadmap.

---

## Why Hardware Testing Matters

Local AI performance is not just about model quality. The same model can behave very differently depending on:

- **VRAM** — determines which quantizations load fully into GPU memory vs. offload to RAM
- **GPU generation** — CUDA 12 (RTX 40/50) vs CUDA 11 (RTX 30) vs Vulkan (AMD, Intel) have real throughput and compatibility differences
- **Model size and quantization** — Q4 vs Q5 vs Q8 affects both speed and reliability; tool-call JSON accuracy degrades measurably at aggressive quants
- **Context window** — large context requests compete with model weights for VRAM; behavior at 4K vs 32K vs 128K context is not the same
- **Tool-call reliability** — structured JSON output failure rates vary significantly across GPU backends, model families, and quant levels
- **Multi-agent load** — running 3–5 parallel Ollama slots simultaneously has different VRAM headroom requirements than single-agent mode

Users of TheOrc need **tested recommendations**, not educated guesses. The Model Wiki, GOBLIN MIND probing, and swarm configuration advisor are all designed to surface this information — but they need real data from real hardware to be useful.

---

## Current Test Hardware

All results reported in TheOrc's model catalogue, Model Wiki, and compatibility notes were collected on the following systems:

| System | GPU | VRAM | Notes |
|---|---|---|---|
| Primary dev machine | NVIDIA RTX 5070 Ti | 16 GB | Main development and test platform |
| Secondary rig | NVIDIA RTX 3080 | 10 GB | Mid-tier GPU baseline |
| Entry-level test | NVIDIA RTX 3050 | 6 GB | Small-model and 4-bit quant coverage |
| CPU baseline | *(placeholder — CPU/RAM specs)* | System RAM | AVX2 baseline; CPU-only inference |

These systems cover a reasonable range of NVIDIA CUDA performance tiers. They do not cover AMD, Intel Arc, AI PC / NPU hardware, 24 GB cards, laptop GPUs, or mainstream 8 GB cards from the current generation.

---

## Hardware Gaps

The following hardware is not currently represented in TheOrc's test matrix. Results for these configurations are estimated or missing entirely:

### High priority

| Gap | Why it matters |
|---|---|
| **24 GB NVIDIA GPU** (RTX 3090, 4090, 5090) | Unlocks Q4 32B models (Qwen 2.5 Coder 32B, DeepSeek R1 32B) — the highest-quality tier available locally. Currently untested. |
| **AMD ROCm-capable GPU** (RX 7900 XTX, RX 7800 XT) | ROCm + Vulkan backend behavior differs from CUDA. Tool-call reliability on Vulkan is largely uncharted for TheOrc's use cases. |
| **Mainstream 8 GB GPU** (RTX 4060, RTX 5060) | The most common consumer GPU tier. Current 8 GB data comes from older RTX 30-series hardware. |

### Secondary priority

| Gap | Why it matters |
|---|---|
| **Intel Arc GPU** (Arc B580, A770) | Vulkan backend, growing market share in the AI PC space. |
| **AI PC / NPU hardware** (Intel Core Ultra, AMD Ryzen AI) | NPU offload paths are not yet tested with TheOrc's inference stack. |
| **Laptop / mini PC systems** | Thermal throttling, shared VRAM, and power limits change inference throughput and stability in ways desktop benchmarks don't capture. |
| **Multi-GPU setup** (2× 10 GB, 2× 12 GB) | Enables 70B models via tensor parallelism. Entirely untested with the Goblin Swarm. |

---

## Testing Methodology

For every piece of hardware added to the test matrix, the following protocol is run:

### Model load tests
- Verify each tier-appropriate model loads fully into VRAM (no CPU offload unless expected)
- Record load time and VRAM consumption at idle after load

### Prompt response tests
- Short prompt (< 100 tokens output): tokens/second, time-to-first-token
- Long prompt (1K+ tokens output): sustained throughput, thermal throttle check
- 32K context request: VRAM headroom check, does it load or error?

### Structured JSON tests
- Ask the model to output a specific JSON schema
- Measure parse success rate across 10 runs
- Note any format drift at high quant compression levels

### Tool-call reliability tests (GOBLIN MIND)
- Run the full `tool-probe full` suite: dispatch mode detection, format fingerprinting (5 formats), category boundary mapping (7 categories)
- Record results in `tool-call-profiles.json` and import into Model Wiki
- FileWriteSmall / FileWriteMedium / FileWriteLarge payload tests via ModelCapabilityTestDialog

### Swarm boss/worker tests
- Launch a 3-role Goblin Swarm (Boss + Coder + Researcher) with a representative goal
- Verify Ollama parallel slot handling under concurrent load
- Note any instability, OOM events, or degraded output quality

### Reported outputs
- Tokens/second at representative load
- VRAM at idle / active inference / swarm load
- Tool-call pass rates from GOBLIN MIND probes
- Recommended model per swarm role for this GPU
- Any known issues, workarounds, or configuration notes
- TheOrc Model Wiki entry with scores populated

All results are published unedited. If a model performs poorly on a given GPU, that is reported. If tool-call reliability is low on a Vulkan backend, that is reported. The goal is accurate compatibility data, not marketing copy.

---

## Sponsorship Options

If you make hardware and want it properly represented in TheOrc's compatibility data, here are the available arrangements:

### Hardware loaner
You ship a GPU or system, I run the full test protocol, publish the results, and return the hardware. Suitable for GPU vendors wanting compatibility coverage without a permanent donation.

### Review sample / eval unit
You send hardware with no return expectation. I run the full test protocol and publish results. Suitable for vendors wanting ongoing community representation in the test matrix.

### Discount code or affiliate link
If you sell hardware relevant to local AI users, I can add your product to the relevant section of the README and Model Wiki with a discount code or affiliate link alongside the test data. Disclosure is always shown.

### GitHub Sponsors — targeted funding
Contribute directly toward a specific hardware purchase via [GitHub Sponsors](https://github.com/sponsors/hardcoreerik). When a target is reached, the hardware is purchased, tested, and results published — with all sponsors credited.

### One-time donation
Ko-fi, PayPal, or GitHub Sponsors one-time contributions. Any amount helps offset hardware costs and development time.

---

## What Sponsors Receive

Every sponsor who contributes hardware or funding toward a specific hardware tier receives:

- **Credit in `README.md`** under Supported Hardware — name, product, and link
- **Listed in the test matrix** as the source of that hardware configuration's results
- **Public benchmark report** — full test protocol output published in this repo under `docs/benchmarks/`
- **Model Wiki compatibility data** — GOBLIN MIND probe results and swarm recommendations for your hardware, permanently visible in the app
- **Release note mention** — credited in the changelog entry when results are published
- **Screenshots and reproducible notes** — test conditions, Ollama version, driver version, full reproduction steps

**What sponsors do not receive:** editorial control over results, positive framing of negative data, or removal of unfavorable findings. Honest data is the product. A GPU that underperforms at tool-call reliability will be reported accurately — that is what makes the data worth anything.

---

## Contact

**GitHub:** [@hardcoreerik](https://github.com/hardcoreerik)  
**Repository:** [github.com/hardcoreerik/TheOrc](https://github.com/hardcoreerik/The-Orchestrator)  
**GitHub Sponsors:** [github.com/sponsors/hardcoreerik](https://github.com/sponsors/hardcoreerik)

For hardware loaner or review sample arrangements, open a [GitHub Discussion](https://github.com/hardcoreerik/The-Orchestrator/discussions) or reach out via the Sponsors page.

---

*TheOrc is MIT-licensed, free, and not affiliated with any GPU vendor. All benchmark data is collected independently on unmodified retail hardware.*
