# Project Truth

> Snapshot date: 2026-08-15 America/Los_Angeles (Phase 0 + post-Phase-0 ablation/streaming update)
>
> Repository: `F:\Ai\OrchestratorIDE-dev`
>
> Product baseline: `origin/master`
>
> Verified product commit: `6ecdd66e5b6bd83de2c5aee2f6c7ed86568d40b7`
>
> Pending integration reviewed separately: PR #96 head `16501dae4568391e8891dc091f8869d43ca6b7b9`
>
> Also pending, separate branch, NOT merged: `fix/native-runtime-grammar-and-admission` @ `198f5db8` (off current master, two verified Native Runtime correctness fixes -- see "Native Runtime findings, 2026-08-15" below)

This document separates repository-observed facts from proposals. Update it only after checking live code, commands, or stored experiment artifacts.

## Phase 0 + post-Phase-0 findings, 2026-08-15

**VERIFIED:** Phase 0 is complete -- all 14 required checks in `PHASE_0_ACCEPTANCE.yaml` read `pass`, maintainer-approved. See `DECISION_LOG.md` OE-ADR-016 through OE-ADR-018.

**VERIFIED:** the ablation-diagnostic tooling built on top of Phase 0 (`Tools/OrcEnginePhase0/oracle/ablation_sweep*.py`, `gguf_streaming_loader.py`) proved, with real measurements against real downloaded GGUF models (not synthetic-only), that a model does not need to fit entirely in VRAM to execute. Meta-Llama-3.1-8B-Instruct (a real model that failed to load under a full-residency CPU approach at ~32GB and a full-residency GPU approach at ~16GB, both tried earlier in the same session) completed a full ablation sweep using a true layer-streaming execution path at **3.17GB peak VRAM**. This is repository-observed evidence, not a claim from the steering document that requested it -- reproducible via `Tools/OrcEnginePhase0/oracle/gguf_streaming_loader.py` against any locally available large GGUF.

**VERIFIED (bug found and fixed):** every OrcEngine oracle forward-pass implementation (CPU, GPU, and streaming) computed final logits using the tied-embedding formula (`token_embedding.T`) unconditionally, even for models with a distinct, untied `output.weight` tensor. This silently corrupted the first Llama-3.1-8B ablation artifact produced in this session -- confirmed via that artifact's own retained `gguf_info.tied_embeddings: false` field. Fixed across all three backends; original artifact preserved (never deleted or overwritten) with a full invalidation record; corrected artifact replaces it as the citable evidence. See `DECISION_LOG.md` OE-ADR-019 for the complete account, including the specific numbers that changed.

**VERIFIED (architecture correction applied):** the first implementation of multi-branch ablation support (running several different ablations that target the same transformer layer) cloned that layer's entire weight set once per branch -- for a full-component sweep (~28 interventions per layer) this meant "baseline weights + ~28 complete cloned copies resident at once," which directly defeats the point of the streaming/oversized-model work. Corrected to `LayerIntervention` (`identity_bypass` / `mask_head` / `disable_ffn`), applied to a single shared, never-cloned, never-mutated layer-weights object per layer. Verified bit-exact against the original spec-major reference implementation (max diff 0.0) and within established fp16-storage tolerance against an independent CPU clone-and-zero reference. A full-component sweep that was unsafe before this fix now runs correctly: 608 components on SmolLM2-360M in 54.3s at 0.32GB peak VRAM.

## Native Runtime findings, 2026-08-15

Found during the same architecture-steering review, in PRODUCTION code (not OrcEngine research code). Both fixed on a separate branch, `fix/native-runtime-grammar-and-admission` (off current master @ `c397f023`), NOT merged into `feat/orcengine-phase0` and NOT yet merged into `master` -- pending maintainer review.

**VERIFIED:** `IRoleRuntime.cs`'s `NativeRoleRuntime` (the persistent per-role execution path, backed by `AdapterManager`'s per-role `BatchedExecutor`) built its own `DefaultSamplingPipeline` with only `Temperature` set -- it never attached `ToolCallGrammarBuilder`'s tool-name grammar, despite `LLamaSharpRuntime.cs`'s own docstring claiming native tool generation was universally grammar-constrained. Only the STATELESS path (`LLamaSharpRuntime.StreamCompletionAsync`) actually attached the grammar. Fixed by extracting one shared `NativeSamplingPolicy` both paths now call. **Caveat, stated honestly (do not overclaim this):** a gated real-model regression test exercising the actual persistent path was added and passes, but the same adversarial prompt also passed on the UNFIXED code with the two small local models available (SmolLM2-360M, Qwen2.5-1.5B) -- confirmed by temporarily reverting the fix and re-running. The test proves the real path executes correctly end-to-end; it does not prove it catches this specific class of regression with these models. The deterministic guarantee comes from the structural fix (one shared construction path) plus the pure-logic grammar-builder tests, not from this adversarial test alone.

**VERIFIED:** `OrcScheduler.EstimateRequiredBytes` fell back to `binding.BaseModel.SizeBytes ?? 0` when a base model's size was unreadable -- an indeterminate cost silently became "free," always admitted regardless of budget. The pre-existing test's own comment already self-documented this as deliberate-but-fail-open. Fixed with `UnknownBaseModelSizeEstimateBytes` (a large sentinel that fails any realistic GPU-resident admission check while still correctly allowing the existing CPU-only degraded-admission fallback to admit, since that path scales the sentinel to exactly 0 at `gpuLayerOverride: 0`). **This sentinel-constant pattern is a narrow, pragmatic C# production patch -- it is explicitly NOT the design OrcEngine's own eventual cost model should use.** OrcEngine should distinguish `KnownCost(bytes)` / `UnknownCost(reason)` / `UnsupportedCostModel(reason)` as explicit states (see `ARCHITECTURE.md`'s memory-model section), not a numeric placeholder.

**Full C# test suite:** 809 passed, 0 failed, 14 skipped (gated real-model tests, separately run with `THEORC_TEST_GGUF` set and confirmed passing).

## Phase 1 findings, 2026-08-15

**VERIFIED:** Phase 1 (tiny synthetic F32 CPU transformer) is implemented and passing. `Tools/OrcEnginePhase1/` (branch `feat/orcengine-phase1`, worktree `F:/Ai/OrchestratorIDE-phase1`, based on `feat/orcengine-phase0`'s tip `d9045995`) is a ~16-file C++20/CMake project with zero external dependencies. It reuses Phase 0's own Fixture C dimensions (`vocab=32, hidden=16, intermediate=32, n_layers=2, n_q_heads=4, n_kv_heads=2, head_dim=4`), implements every operator in the block (embedding lookup, RMSNorm, linear/matmul, non-interleaved RoPE, GQA causal attention, SwiGLU FFN, tied/untied lm_head resolution, greedy argmax), and a differential test harness (`tests/test_gates.cpp`) comparing all 34 intermediate taps plus final logits plus greedy token selection against fixtures exported directly from the trusted Python oracle (`Tools/OrcEnginePhase0/oracle/export_cpp_phase1_fixture.py`). Both a tied-embeddings and an independently-seeded untied-embeddings fixture pass every tap: measured divergence is `~1e-7` (float32 machine-epsilon scale), roughly four orders of magnitude inside the `1e-3` acceptance threshold. Greedy argmax matches exactly (integer equality) on all 4 test positions in both fixtures. The harness was verified to actually detect faults, not just pass trivially: a deliberately injected one-value weight corruption was caught, with the failure correctly localized to only the taps that algebra predicts should diverge (logits/selected_token), while all upstream taps correctly still passed. NaN/Inf checking runs on every captured tap and fails closed (throws) if triggered; verbose per-tap tracing is opt-in via `ORCENGINE_DEBUG_TAPS=1`. See `PHASE1_IMPLEMENTATION.md` for full detail, build/test commands, and explicitly deferred scope (no cached decode, no GGUF, no CUDA, no quantization -- all Phase 2+).

**Per the steering document's explicit stop-gate instruction, Phase 1 work paused here pending maintainer review. No Phase 2 work has started.**

## Executive truth

**VERIFIED:** as of Phase 1 (above), OrcEngine has one real C++ source project: `Tools/OrcEnginePhase1/`, a CMake target (`orcengine_phase1` static lib + `test_gates` executable), and a passing differential test binary against the Python oracle. There is still no GGUF model loader, no real-model tensor loading, no managed wrapper, no CUDA backend, no quantization, no benchmark, and no product (TheOrc) integration of any kind -- Phase 1 is a synthetic-fixture-only reference core, not a usable inference engine.

**VERIFIED:** TheOrc already has three runtime implementations behind `IModelRuntime`:

- `OllamaRuntime`, a thin adapter over `OllamaClient`;
- `LlamaCppServerRuntime`, an adapter for an out-of-process llama.cpp server;
- `LLamaSharpRuntime`, in-process GGUF inference through LLamaSharp.

**VERIFIED:** Native in-process main chat and HIVE-worker execution are production defaults as of 2026-07-29. The legacy `AppSettings.Backend` field still initializes to `Ollama`, but it is used only when native main chat is explicitly disabled. Production native main chat fails closed through `NoFallbackRuntime`; it does not silently substitute Ollama.

**DECIDED (2026-07-31):** native-only is the product target. Remaining Ollama paths are transitional migration debt, not fallback architecture. OrcEngine research does not block that migration and is not a shortcut around fixing the current LLamaSharp-based native runtime.

**VERIFIED:** The in-process project references LLamaSharp 0.27.0 plus CPU and CUDA 12 backend packages. LLamaSharp’s own project states that it is based on llama.cpp and calls native backends.

**VERIFIED:** The current native runtime already contains meaningful TheOrc-owned control-plane work: model loading, prompt construction, streaming, tool-call parsing, telemetry, stateless generation, persistent role executors, adapter/session coordination, model depot bindings, VRAM admission, and explicit fallback behavior.

**VERIFIED:** Context-aware VRAM cost estimation uses GGUF header metadata. Any statement that the estimate is only file-size-based is stale.

## Current code map

| Concern | Current source | Observed responsibility |
|---|---|---|
| Backend-neutral generation | `OrchestratorIDE/Core/Runtime/IModelRuntime.cs` | Messages, tools, streaming text, tool callbacks, health, and telemetry contracts. |
| In-process inference | `LLamaSharpRuntime.cs` | LLamaSharp model load, stateless executor, templates, sampling, streaming, and raw role-executor seam. |
| Explicit compatibility adapter | `OllamaRuntime.cs` | Delegation to existing `OllamaClient` when the native-default toggle is disabled. |
| Server runtime | `LlamaCppServerRuntime.cs` | llama.cpp server lifecycle plus compatible client transport. |
| Explicit fallback | `NativeWithFallbackRuntime.cs` | Narrow pre-output fallback behavior and admission-denial exclusion. |
| Base model lifecycle | `SessionManager.cs` | Model load and session snapshots. |
| Per-role contexts/adapters | `AdapterManager.cs` | Persistent batched executors, role bindings, and lifecycle controls. |
| Coordination | `RuntimeOrchestrator.cs` | Shared runtime ownership, admission gate, reservations, and component wiring. |
| Admission policy | `OrcScheduler.cs` | Required-byte estimates and VRAM decisions. |
| GGUF metadata subset | `GgufMetadataReader.cs` | Defensive header metadata used for estimates; not a full tensor loader. |
| Runtime assets | `ModelDepot.cs` | Base-model and adapter registration plus role bindings. |
| Native packages | `OrchestratorIDE.NativeRuntime.csproj` | LLamaSharp and CPU/CUDA backend packaging. |

## What TheOrc owns today

TheOrc owns policy and lifecycle above the inference engine. It decides which model and role to use, how prompts and tools enter generation, how output is streamed, how admission is handled, when model generations invalidate contexts, and how native failure may or may not fall back.

## What TheOrc does not own today

For the LLamaSharp path, TheOrc does not implement:

- full GGUF tensor loading;
- model architecture graph construction;
- tensor operators and memory planner;
- CPU vectorized kernels;
- CUDA kernels or backend dispatch;
- attention and feed-forward numerical execution;
- native KV-cache storage implementation;
- tokenizer algorithms embedded in llama.cpp;
- quantized dot-product kernels.

Those are the boundary OrcEngine proposes to explore.

## Existing documentation authorities

| Source | Role | Caution |
|---|---|---|
| `docs/ROADMAP.md` | Public ship-state narrative | Broad and frequently updated; verify details in code. |
| `docs/CURRENT_STATE.yaml` | Machine-readable product state | May lag a just-landed commit. |
| `docs/RUNTIME_SUPPORT_MATRIX.md` | Runtime selection and fallback explanation | Describes current user-facing runtime lanes. |
| `docs/RUNTIME_PHASE0_SPEC.md` | Original native-runtime contract and phasing | Historical design plus still-relevant contracts. |
| `docs/NATIVE_RUNTIME_V2_SPEC.md` | Production-readiness hardening | Concerns current LLamaSharp runtime, not OrcEngine. |
| `.grok/PROJECT_TRUTH.md` | Cross-agent working truth | Contains older entries; live code remains decisive. |

## Verified external facts

- The [GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md) defines a binary, extensible, mmap-compatible model format with typed metadata and tensor descriptors.
- [llama.cpp](https://github.com/ggml-org/llama.cpp) provides model loading, architecture execution, quantization, tokenization, sampling, caches, and multiple hardware backends.
- [LLamaSharp](https://github.com/SciSharp/LLamaSharp) is a .NET library based on llama.cpp and ships native CPU/GPU backend packages.
- NVIDIA documents CUDA as a heterogeneous programming model with separate host/device memory, thread hierarchies, and explicit synchronization and transfer behavior.
- NVIDIA documents cuBLAS as a BLAS implementation on the CUDA runtime; using it for initial dense matrix multiplication is not the same as importing a complete inference engine.
- PyTorch documents that floating-point operations are not associative and bitwise-identical results are not guaranteed across platforms or implementations. OrcEngine therefore needs tolerance-based comparisons plus token-level outcome checks.

## Decisions already made for the starter plan

- **DECIDED:** documentation and evidence precede implementation.
- **DECIDED:** OrcEngine is an authorized experimental research track distinct from the existing native-runtime production-hardening work.
- **DECIDED:** continued investment may be justified by either a capability the existing stack prevents or a material, reproducible improvement over it. Novelty is not the only acceptable value.
- **DECIDED:** the first implementation target is CPU-only float32, one sequence, batch size one, greedy decoding, and the exact `OE-L0-SYNTH-1` profile in [Phase 0 Architecture Profile](PHASE_0_ARCHITECTURE_PROFILE.md).
- **DECIDED:** Phase 0 establishes a reference oracle before the engine is integrated into TheOrc.
- **DECIDED:** Phase 0 uses hand-derived microcases, a Python semantic oracle, and pinned llama.cpp deployment comparison; the later engine core is C++20, with CUDA C++, a C ABI, and C# wrapper deferred to their roadmap phases.
- **DECIDED:** `HuggingFaceTB/SmolLM2-135M` revision `93efa2f097d58c2a74874c7e644dbc9b0cee75a2` is the first real-model candidate, subject to conversion, hashing, tokenizer, license, and reproducibility gates.
- **DECIDED:** GGUF support remains standards-compatible, narrow, strict, resource-bounded, and fail-closed; OrcEngine will not invent a proprietary replacement container for the first path.
- **DECIDED:** the first integration, if reached, is experimental and opt-in.
- **DECIDED:** existing runtimes remain the production/reference lanes; OrcEngine research does not change their selection or fail-closed policy.
- **DECIDED:** unknown metrics are reported as unknown, never inferred from plausible output.

## Hypotheses

- **HYPOTHESIS:** a small standalone reference implementation can match a trusted oracle’s logits within defined float32 tolerances.
- **HYPOTHESIS:** owning model state can eventually expose role- and cache-level telemetry unavailable through the current managed API.
- **HYPOTHESIS:** agent-oriented cache semantics could be strategically useful after correctness.
- **HYPOTHESIS:** cuBLAS-first CUDA execution can establish a correct GPU baseline before custom kernels.
- **HYPOTHESIS:** a narrow supported model surface can be maintained without becoming a general llama.cpp clone.

## Unknowns

- The exact converted GGUF artifact hash and its approved storage/distribution policy.
- Exact numerical tolerances by operator and comparison point.
- Whether source and GGUF-embedded tokenizers agree for all required fixtures.
- Whether a standalone repository will eventually be cleaner than this monorepo.
- Which prevented capability or measured improvement first justifies continued product investment.

## Current blockers

No implementation should start until the Phase 0 oracle contract, first model artifact, provenance record, and acceptance tolerances are approved.

## How to update this document

For every change, include date, commit or artifact identity, exact command where applicable, and whether the evidence is repository-observed, runtime-observed, externally sourced, or inferred. Move superseded beliefs into [Decision Log](DECISION_LOG.md) rather than erasing the history.
