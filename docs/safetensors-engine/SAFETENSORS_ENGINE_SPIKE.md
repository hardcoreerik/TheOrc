# TheOrc — Safetensors Engine Spike

> **Status: 🔲 Planned.** Nothing in this folder is implemented. This is the master charter
> for a time-boxed, two-week Tier-B spike. Every claim about the new engine below is a plan,
> not a description of shipped behavior. The only ✅ statements in this folder describe the
> *existing* GGUF/LLamaSharp lane, which this spike must not touch.

---

## 1. Problem statement

TheOrc's mission is user-owned AI: run any model locally, even if slower. Two distinct walls
block that mission, and they need different levers:

| Wall | Question | Lever |
|---|---|---|
| **Coverage** | Can the model run *at all*? | Format + architecture support |
| **Fit** | Does it fit the hardware? | Quantization, offload, distribution |

This spike targets **coverage only**. Models ship as HuggingFace safetensors first; GGUF
conversion lags — sometimes by weeks, sometimes forever (new architectures wait on upstream
llama.cpp support, then on a converted upload, then on a LLamaSharp release). Today TheOrc's
native lane (`OrchestratorIDE/Core/Runtime/LLamaSharpRuntime.cs`) can only load GGUF, and
`ModelAdmissionGate.Evaluate` rejects every asset whose kind is not
`RuntimeAssetKind.BaseModelGguf`. A day-zero HuggingFace release is invisible to the native
runtime until someone else converts it.

**This spike deliberately does not solve fit.** At equal parameter count, fp16 safetensors
weights are roughly 4× larger than Q4 GGUF. An fp16 safetensors engine runs *fewer* large
models on a given GPU than the existing quantized GGUF lane, not more. No reader of these
docs should conclude otherwise; any doc in this folder that implies a fit win is wrong and
should be corrected.

---

## 2. Strategic framing — what is actually being tested

The bet under test: is a **managed .NET inference engine** viable and differentiated enough
to justify Tier-A investment after the spike? Three candidate differentiators, evaluated
honestly (all three are **aspirational** until measured):

1. **Dependency-light managed .NET** — kernels authored in C#, no third-party native
   inference engine (no libtorch, no ONNX Runtime, no llama.cpp) in the supply chain. GPU
   compute still bottoms out in the vendor driver; "pure" means Level 2 on the ladder below.
2. **Offload-first architecture** — weight residency (VRAM / host RAM / disk) as a core
   abstraction rather than a static load-time split. In scope for this spike **only as an
   architectural seam** (see [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md)
   §4), not an implementation.
3. **Deterministic + auditable inference** — bit-identical output for a given seed, plus
   emitted provenance (weights hash, precision, kernel versions, prompt hash, logits).

### The purity ladder

| Level | Description | Target? |
|---|---|---|
| 1 | Fully managed, CPU only | No — too slow for product use (CPU fp32 is still the *correctness* reference inside the spike) |
| 2 | Kernels authored in C#, JIT-compiled to GPU (ILGPU / ComputeSharp) | **Yes — the spike target** |
| 3 | Managed code calling vendor math libs (cuBLAS via P/Invoke) | Acceptable fallback — must be justified in the spike report if used |
| 4 | Managed facade over a large native engine (TorchSharp, ONNX RT) | **Explicit anti-goal.** If the spike ends here, the differentiation claim is dead and the spike should say so. |

---

## 3. Scope

A **two-week Tier-B spike**: fp16, single architecture, single GPU, no quantization, no
production hardening. It exists to produce numbers that make a go/no-go decision defensible.

**Reference model: Llama 3.2 1B Instruct** (`meta-llama/Llama-3.2-1B-Instruct`).
Justification:

- Standard Llama architecture — RMSNorm, RoPE (with Llama-3-style frequency scaling),
  grouped-query attention, SwiGLU MLP, **tied embeddings** — the single most common
  architecture shape in the local-model ecosystem; support here transfers furthest.
- Small enough (~2.5 GB fp16) that iteration is fast and any dev GPU in the fleet fits it
  with full headroom, so fit never contaminates coverage measurements.
- An official F16 GGUF conversion exists, which the head-to-head comparison
  ([SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md)) requires.

**Documented fallback: Llama 3.2 3B Instruct.** Same architecture family; already the
model the 2026-07-19 KV-cache spike validated `OrcScheduler`'s formula against byte-exactly
(224.00/896.00/1792.00 MiB at n_ctx 2048/8192/16384 — see `OrcScheduler.cs`'s own doc
comment), so the project has prior measured experience with it. Fall back if the 1B's
tied-embedding or head-dim configuration surfaces a blocking tooling issue.

### In scope

| Item | Doc |
|---|---|
| Byte-level safetensors parsing + validation, `config.json` mapping, tokenizer handling | [SAFETENSORS_FORMAT_SPEC.md](SAFETENSORS_FORMAT_SPEC.md) |
| Engine component design, GPU backend decision, `IModelRuntime` seating design note | [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) |
| Full mathematical forward-pass spec, implementable without a reference implementation | [NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md) |
| Head-to-head methodology, metrics, statistics, result schema | [SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md) |
| Correctness oracle: reference logits, tolerances, divergence localization | [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) |
| Day-by-day phasing, task IDs, per-phase exit gates | [SPIKE_PHASING_AND_GATES.md](SPIKE_PHASING_AND_GATES.md) |
| Enumerated risks with signals and mitigations | [RISK_REGISTER.md](RISK_REGISTER.md) |

### Explicit non-goals

| Non-goal | Why it stays out |
|---|---|
| Quantization (any form) | Fit lever, not coverage; the single most likely scope-creep vector ([RISK_REGISTER.md](RISK_REGISTER.md) R-04) |
| Batching / concurrent sequences | Production concern; single-sequence decode is enough for go/no-go numbers |
| Any non-Llama architecture | One architecture proves the pipeline; breadth is Tier-A work |
| CPU/GPU offload implementation | Only the residency *seam* is designed (differentiator 2); implementing it is Tier-A work |
| Touching the shipped native lane | Isolation requirement below |
| Production hardening (retry, telemetry wiring, Settings UI) | Spike code is disposable by charter |
| Multi-GPU, non-CUDA GPUs | Reference hardware is one CUDA GPU; backend portability is assessed on paper only |

---

## 4. Isolation requirement

The spike must not touch or risk the shipped native lane. Concretely:

- Nothing in the spike may alter the behavior of `LLamaSharpRuntime`, `NativeRoleRuntime`,
  `OrcScheduler`, or `ModelAdmissionGate`. Zero diffs to files under
  `OrchestratorIDE/Core/Runtime/` — not even "harmless" refactors to expose a seam.
- The spike lives in **`Tools/SafetensorsSpike/`** as a self-contained console project.
  Rationale and the rejected alternative (`OrchestratorIDE.SafetensorsEngine/`) are in
  [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) §2.
- How the engine would *eventually* sit behind `IModelRuntime`/`IRoleRuntime` is documented
  as a **design note only** ([NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md)
  §8) — no production construction path changes during the spike.
- Unit tests for spike code live in the spike project itself, not
  `OrchestratorIDE.UnitTests/`, so the shipped test suite's green/red signal stays about the
  shipped product. (They follow the same xUnit `Method_Scenario_Expectation` naming so they
  can migrate later if the spike graduates.)

---

## 5. Go / no-go decision criteria

The spike ends with a written verdict against these criteria. "We lost on throughput but
learned X" is a valid outcome; an unmeasured claim is not. All thresholds are evaluated on
the reference hardware recorded in the benchmark report.

### GO (recommend Tier-A investment) requires ALL of:

| # | Criterion | Measured by |
|---|---|---|
| G-1 | Parser loads real multi-shard HF safetensors repos with byte-exact size prediction (same rigor as `GgufMetadataReader`) | Format gate, [SPIKE_PHASING_AND_GATES.md](SPIKE_PHASING_AND_GATES.md) P1 |
| G-2 | Greedy fp16 output achieves logit parity with the HF transformers reference within the tolerances of [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) — specifically, no worse than Path A (F16 GGUF on llama.cpp) diverges from the same reference | Parity harness |
| G-3 | Bit-identical output across N=10 same-seed runs on one machine (cross-machine determinism is measured and reported but **not** a GO gate — see [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) §6) | Determinism suite |
| G-4 | GPU decode throughput ≥ **25%** of Path A's F16 decode tok/s on identical hardware, **or** below that with a written, specific, credible optimization path (e.g. "kernel X is 80% of time and unfused") | Benchmark report |
| G-5 | The result was achieved at purity Level 2, or Level 3 with a written justification | Spike report |

The 25% floor in G-4 is a deliberate, pre-registered judgment call: llama.cpp has years of
kernel tuning, and the spike's value is proving the *pipeline*, not winning the race. It is
recorded here **before** any measurement so the bar cannot quietly move after the numbers
arrive.

### NO-GO (kill) — any of:

| # | Kill criterion |
|---|---|
| K-1 | Parity cannot be reached within tolerance by the end of Phase 3, and the divergence cannot be localized to a specific layer/op ([LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) §7) |
| K-2 | GPU decode < 5% of Path A F16 with no identified, specific cause |
| K-3 | Level 2 proves infeasible **and** Level 3 also misses G-4 — the differentiation story collapses to Level 4, which is the anti-goal |
| K-4 | The two-week box expires with Phase 2 (CPU parity) incomplete — the time box is the scope control; extending it silently is scope creep |

A NO-GO still produces the full benchmark report and a written findings section — the
project's standard for honest failure reporting (see `docs/CONTEXT_FABRIC_BUG_HISTORY.md`
for the tone: what was tried, what was measured, what was disproven).

---

## 6. Document index

| # | Doc | One-line contents |
|---|---|---|
| 1 | SAFETENSORS_ENGINE_SPIKE.md | This charter |
| 2 | [SAFETENSORS_FORMAT_SPEC.md](SAFETENSORS_FORMAT_SPEC.md) | Byte-level format, validation, `config.json`/tokenizer mapping, size prediction |
| 3 | [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) | Components, interfaces, GPU backend decision matrix, residency seam, `IModelRuntime` design note |
| 4 | [NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md) | The math: every op with formula, shapes, dtypes, stability notes |
| 5 | [SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md) | Head-to-head methodology, metrics, statistics, result schema, threats to validity |
| 6 | [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) | Reference logits, storage, tolerances, pass/fail, divergence localization |
| 7 | [SPIKE_PHASING_AND_GATES.md](SPIKE_PHASING_AND_GATES.md) | Day-by-day plan, task IDs ST-xxx, acceptance criteria, exit gates |
| 8 | [RISK_REGISTER.md](RISK_REGISTER.md) | Risks with likelihood, impact, early-warning signal, mitigation |

Folder index for `docs/README.md`: [README.md](README.md).
