# Project Truth

> Snapshot date: 2026-07-31 America/Los_Angeles
>
> Repository: `F:\Ai\OrchestratorIDE-dev`
>
> Product baseline: `origin/master`
>
> Verified product commit: `6ecdd66e5b6bd83de2c5aee2f6c7ed86568d40b7`
>
> Pending integration reviewed separately: PR #96 head `16501dae4568391e8891dc091f8869d43ca6b7b9`

This document separates repository-observed facts from proposals. Update it only after checking live code, commands, or stored experiment artifacts.

## Executive truth

**VERIFIED:** OrcEngine currently consists only of this documentation suite. There is no OrcEngine source project, CMake target, native library, managed wrapper, model loader, tensor implementation, test binary, benchmark, or product integration.

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
