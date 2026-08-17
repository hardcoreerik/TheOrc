# Project Truth

> Snapshot date: 2026-08-16 America/Los_Angeles (Phase 2 frozen; Phase 3 implemented pending independent freeze review)
>
> Repository snapshot: `F:\Ai\OrchestratorIDE-phase3-working-set`
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

## Phase 1 freeze audit, 2026-08-15 (same day, follow-up)

Four additional questions were raised before allowing Phase 1 to freeze, all answered experimentally rather than asserted:

**VERIFIED:** true autoregressive greedy decoding, not just single-forward-pass argmax. `tests/test_decode.cpp` runs full-recompute greedy decode (no KV cache in either implementation) for 8 steps from seed tokens `[1, 5]`, with the C++ side and an independently-computed Python trace (`oracle/export_cpp_phase1_decode_fixture.py`) each choosing their own next token with no knowledge of the other's choice. All 8 steps matched token-for-token: `[1, 5, 5, 5, 29, 29, 29, 29, 29, 29]` on both sides. Per-step max logit error stayed at float32 machine-epsilon scale (1.19e-7 to 1.79e-7).

**VERIFIED (deliberate decision, not an accident):** the original implementation used `double` accumulation without having run the F32-vs-F64 comparison the Phase-1 spec requires before deviating from F32-throughout. Made the accumulator type configurable and built both variants (`orcengine_phase1` = F32, `orcengine_phase1_f64accum` = F64, comparison-only). Real numbers: F64 accumulation is marginally more precise (e.g. tied logits max_abs_error 2.38e-7 vs F32's 3.50e-7), but the difference is itself ~5 orders of magnitude smaller than the 1e-3 acceptance threshold in both directions -- F64 is not materially necessary. **F32 accumulation is the selected default**, matching the stated preference that later CUDA/quantized work needs a clean F32 reference to compare against.

**VERIFIED:** activation values (`ActivationBuffer`, `forward.hpp`) were already a distinct type from `ResidentView` in the original implementation -- `ResidentView` is used exclusively for durable model weights. The freeze audit's concern was a real documentation/naming gap, not an actual type-collapse bug: `ActivationBuffer` now carries an explicit doc comment contrasting it with `ResidentView` (no `LogicalTensor` identity, no `BackingExtent`, no lifetime past one `forward()` call).

**SUPERSEDED BY THE HARDENING REVIEW BELOW:** the first metamorphic harness established bit-identical results after resident-vector copies, but it did not exercise `BackingExtent`, verified only one relocated address, and compared the rematerialized pointer with an empty view rather than the original allocation. Its mathematical outputs were valid; its storage-independence wording was broader than its evidence.

The original CTest registration covered only the three F32 executables. The F64 executables were run directly, but were not registered until the hardening review below.

**HISTORICAL CHECKPOINT:** per the steering document's explicit stop-gate
instruction, Phase 1 work paused here pending maintainer review; at that point
no Phase-2 work had started. The hardening and frozen Phase-2 sections below
record what happened afterward.

## Phase 1 freeze hardening, 2026-08-16

**VERIFIED:** the evidence harness now requires an exact structural expectation set: 36 records for each of tied and untied fixtures, 72 total. Missing logits, missing selected token, unexpected records, wrong shapes, and NaN/Inf golden values fail before execution. Decode requires a positive exact step count, self-consistent sequence growth, finite vocabulary-sized logits, exact selected-token agreement, and enforced `1e-3` absolute and relative logit tolerances.

**VERIFIED:** `validate_model()` is called at the start of `forward()` and rejects invalid dimensions, head relationships, sequence lengths, token IDs, layer counts, missing output heads/tensors, tied contradictions, and every required tensor-shape mismatch before unchecked math. The confirmed `n_kv_heads=0` divide-by-zero is now a clean validation error.

**VERIFIED, NARROW CLAIM:** the metamorphic harness now creates owned F32Raw `BackingExtent` bytes for every model weight and calls `materialize(LogicalTensor, BackingExtent)` twice while both resident generations coexist. Every resident has distinct storage with identical shape/values, the first generation is destroyed, and both generations produce bit-identical logits. The tied alias-vs-byte-identical-duplicate test remains exact. This proves the in-memory F32 backing path only; GGUF, mapped-file, disk, paging, and CUDA backing remain unimplemented.

**VERIFIED:** CTest now registers seven cases: F32 and F64 gate/decode/metamorphic variants plus the accumulation-independent hardening regression suite.

## Phase 2 GGUF ingestion, 2026-08-16

**VERIFIED:** Phase 1 is frozen at `b27bc9323b89b9151c811c30d41145bb672a2943`, pushed on `feat/orcengine-phase1`, and annotated by the immutable tag `orcengine-phase1-freeze`. Phase-2 work is isolated on `feat/orcengine-phase2-gguf` in `F:/Ai/OrchestratorIDE-phase2-gguf`.

**VERIFIED:** `Tools/OrcEnginePhase2/` implements a strict, bounded little-endian GGUF v3 indexer, one dense Llama semantic mapping, file-backed `BackingExtent` creation, F32 and F16-to-F32 materialization, a human/JSON inspector, and an explicit-token forward CLI. It indexes selected quantized encodings but deliberately rejects their materialization. It does not implement CUDA, quantized compute, tokenizer algorithms, paging, batching, or product integration.

**VERIFIED:** a 270,590,880-byte real mixed-quantized SmolLM2-360M GGUF maps 290/290 tensors and agrees exactly with `gguf-py` on names, dimensions, encodings, offsets, and lengths while remaining nonresident and non-executable. Inspector peak working set was 22,822,912 bytes (8.43% of file size).

**VERIFIED:** the pinned SmolLM2-135M source was converted to a 653,091,040-byte F32 GGUF, mapped 273/273 tensors, and executed through unchanged Phase-1 math. C++ and the Phase-0 Python oracle generated `[1, 5, 28, 284, 260, 198]` token-for-token; all compared intermediate values and logits satisfy the declared `1e-3` absolute-or-relative rule.

## Phase 2 freeze hardening, 2026-08-16

**VERIFIED:** the default 4 GiB parser cap is policy rather than an architectural ceiling. A deterministic sparse GGUF with a valid tensor at absolute offset 4,294,967,424 is rejected under the default cap, accepted under an 8 GiB configured cap with the exact 64-bit offset retained, and indexed without payload materialization. One-byte sparse EOF truncation and near-`UINT64_MAX` extent overflow fail closed.

**VERIFIED:** direct Hugging Face Transformers/PyTorch execution of the original pinned source model, using the same explicit `[1, 5]` token IDs and importing no Phase-0 converter/oracle code, produced the same four-step greedy sequence as OrcEngine. Every last-token logit passed the frozen `1e-3` absolute-or-relative rule (maximum absolute 0.00104618073; maximum relative 0.000288560404).

**VERIFIED:** a deterministic real F32 tied GGUF was derived only after checking byte identity between the explicit output head and token embedding. It removes `output.weight`, maps 272/272 tensors as tied, independently agrees with `gguf-py`, and produces bit-identical logits, first-step taps, and `[1, 5, 28, 284, 260, 198]` greedy sequence versus the 273-tensor explicit artifact.

**VERIFIED (bugs found and fixed):** GGUF metadata-key validation previously accepted noncanonical underscore placement, and tensor names incorrectly inherited the 64 MiB metadata-string cap instead of GGUF's 64-byte tensor-name limit. Strict hierarchical lower-snake-case metadata keys and the 64-byte tensor-name boundary are now enforced by malformed fixtures.

**VERIFIED (test non-vacuity):** all three real-execution checkers now require a positive requested step count and an exact returned trace length. Direct zero-step re-attacks were rejected 3/3.

**VERIFIED:** post-fix validation is Debug 14/14, Release 14/14, strict `/W4 /WX` 10/10, and MSVC ASan 11/11. Exact inclusions and exclusions are explicit in `PHASE2_FREEZE_HARDENING.md`; the strict and ASan lanes do not claim unconfigured real F32 comparisons. The Phase-2 verdict is **ACCEPT FOR PHASE-2 FREEZE**. No Phase-3 work was started.

**UNKNOWN:** no direct Phase-2 llama.cpp differential was run because no local llama.cpp executable/module was available. Direct Hugging Face/PyTorch now supplies the required independent end-to-end reference; llama.cpp remains an additional unperformed comparison rather than a freeze blocker.

## Phase 2 formal closure and Phase 3 planning baseline, 2026-08-16

**VERIFIED:** Phase 2 is COMPLETE / FROZEN at trusted commit
`b8e06a0058a56f2ae9fbd1f92ae0bade40b88ec7`. The branch
`feat/orcengine-phase2-gguf` and immutable annotated tag
`orcengine-phase2-freeze` were pushed. Remote verification showed the branch
and peeled tag both point exactly to the trusted commit. Later planning/docs
commits are outside the frozen implementation.

**VERIFIED:** a planning measurement rebuilt the frozen Release executable and
ran one full-resident forward from token IDs `[1, 5]`. The 653,091,040-byte
explicit artifact materialized 651,306,240 weight bytes, selected token 28, and
reached a 668,950,528-byte sampled process working set (50 ms polling);
materialization was 5,339.167 ms, forward 4,671.577 ms, and process wall
10,188.547 ms. The 538,076,736-byte tied artifact materialized 538,060,032
bytes, selected token 28, and reached 548,528,128 bytes; materialization was
4,150.711 ms, forward 4,015.021 ms, and process wall 8,297.089 ms. These are
single warm/unknown-cache planning observations, not a benchmark campaign.

**VERIFIED:** manifest accounting shows the largest transformer layer is only
14,160,384 bytes. Conservative layer-at-a-time residency would retain
226,494,720 bytes of explicit-model bookends (predicted weight high-water
240,655,104 bytes, 36.95% of full) or 113,248,512 tied-model bookends
(predicted 127,408,896 bytes, 23.68% of full), before activation/conversion and
runtime overhead. These are static predictions, not streamed measurements.

**HISTORICAL PROPOSAL — IMPLEMENTED BELOW:** Phase 3 becomes a real-model streaming/working-set reference,
starting with one real GGUF-backed layer at a time and no cache. It must preserve
frozen math and GGUF semantics, compare bit-identically with full
materialization, measure RAM/read amplification honestly, and stop before
tokenizer, KV cache, optimization, quantization, or CUDA. See
`PHASE3_WORKING_SET_SPEC.md`. At this planning checkpoint implementation had not started.

## Executive truth

**VERIFIED:** OrcEngine now has the frozen Phase-1 C++ reference core and a separate Phase-2 GGUF ingestion project. Phase 2 can index and semantically map real dense-Llama GGUF v3 files, materialize F32/F16 source tensors into F32 residents, and execute the verified real F32 candidate from explicit token IDs. Quantized execution, tokenizer support, managed wrappers, CUDA, paging, benchmarks, and product integration remain absent; this is a reference/review checkpoint, not a usable production inference engine.

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

- The approved storage/distribution policy for the converted GGUF artifact (its exact hash is recorded in `PHASE2_GGUF_IMPLEMENTATION.md`).
- Exact numerical tolerances by operator and comparison point.
- Whether source and GGUF-embedded tokenizers agree for all required fixtures.
- Whether a standalone repository will eventually be cleaner than this monorepo.
- Which prevented capability or measured improvement first justifies continued product investment.

## Phase 3 working-set status — 2026-08-16

**REPOSITORY- AND RUNTIME-OBSERVED:** Phase 3 is implemented and hardened on
`feat/orcengine-phase3-working-set` with a recommendation to accept the freeze.
Real explicit and tied F32 GGUFs execute one transformer layer at a time through
the shared Phase-1 math, produce bit-identical full/streamed four-step outputs,
and match the independent Hugging Face/PyTorch gate. Exact engine-owned peaks
are 240,655,104 bytes explicit and 127,408,896 bytes tied. Those exact budgets
reject full residency but admit streamed execution with exact output. The core
streaming algorithm now consumes a format-neutral source/materializer contract,
and optional structured observations are output-invariant. Persistent decode
shows a 1.49x explicit and 1.85x tied streamed/full post-residency slowdown. See
`PHASE3_FREEZE_HARDENING.md` for commands, discovery history, raw evidence,
matrix, limitations, and verdict. Phase 3 is not tagged and Phase 4 has not
started.

## Current blockers

An independent reviewer must accept and tag the hardened Phase 3 result before
Phase 4 is planned or implemented.

## How to update this document

For every change, include date, commit or artifact identity, exact command where applicable, and whether the evidence is repository-observed, runtime-observed, externally sourced, or inferred. Move superseded beliefs into [Decision Log](DECISION_LOG.md) rather than erasing the history.
