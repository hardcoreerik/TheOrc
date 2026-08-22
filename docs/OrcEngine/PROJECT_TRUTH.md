# Project Truth

> Snapshot date: 2026-08-18 America/Los_Angeles (Phase 4 formally frozen; Phase 5A real-model correctness verified, composition with Phase 4 now the active gate per OE-ADR-026)
>
> Active worktree snapshot: `F:\Ai\OrchestratorIDE-phase5a-kv-cache` @ branch `feat/orcengine-phase5a-kv-cache`, HEAD `0a80477d`
>
> Frozen parent (distinct from the active worktree above): `F:\Ai\OrchestratorIDE-phase4-bookend-virtualization` @ branch `feat/orcengine-phase4-bookend-virtualization`, tag `orcengine-phase4-freeze` -> `944f07b86428ec53d46ca19dc66c3d0d5b1e207d`
>
> Product baseline (TheOrc's own integration state, unrelated to OrcEngine's research branches): `origin/master`
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

## Phase 3 closure and Phase 4 bookend virtualization — 2026-08-16

**VERIFIED:** Phase 3 is frozen by immutable annotated tag
`orcengine-phase3-freeze` at
`98dbcf1f370a93574da32dc02ebdcfeff8a60b3d`.
Real explicit and tied F32 GGUFs execute one transformer layer at a time through
the shared Phase-1 math, produce bit-identical full/streamed four-step outputs,
and match the independent Hugging Face/PyTorch gate. Exact engine-owned peaks
are 240,655,104 bytes explicit and 127,408,896 bytes tied. Those exact budgets
reject full residency but admit streamed execution with exact output. The core
streaming algorithm now consumes a format-neutral source/materializer contract,
and optional structured observations are output-invariant. Persistent decode
shows a 1.49x explicit and 1.85x tied streamed/full post-residency slowdown. See
`PHASE3_FREEZE_HARDENING.md` for its frozen evidence.

**REPOSITORY- AND RUNTIME-OBSERVED:** Phase 4 is implemented on
`feat/orcengine-phase4-bookend-virtualization`. A format-neutral logical row
materializer now supplies unique input embedding rows and output vocabulary
chunks while the Phase-3 layer lifecycle remains unchanged. Chunk sizes 1, 16,
64, 256, 1024, and remainder-producing 1000 were bit-identical to both frozen
Phase-3 executables over four generated steps for explicit and tied real
artifacts. Direct tied/explicit execution was exact, and Hugging Face/PyTorch
again produced `[1, 5, 28, 284, 260, 198]` within the unchanged gate.

**MEASURED, WITH A CORRECTION FOUND DURING INDEPENDENT REVIEW (2026-08-18):**
Phase-4 peak resident weights are 14,162,688 bytes for both artifacts: largest
layer 14,160,384 plus the 2,304-byte final norm. This is 2.17% of explicit and
2.63% of tied full resident weights, **conditional on `output_chunk_rows` (a
free, caller-set `StreamingConfig` parameter) staying at or below 6,146 rows**
for this model -- all six tested chunk sizes (1, 16, 64, 256, 1024, 1000) are
well under that threshold, so the measured numbers are real and reproducible,
but the 14,162,688-byte figure is NOT an unconditional property of the
row-region strategy: a caller choosing a larger `output_chunk_rows` (up to the
49,152-row vocabulary) would make the output-projection chunk the dominant
resident term, approaching the old Phase-3 bookend size instead. This is an
adversarial-review finding (independent Grok pass, confirmed against the
actual `streaming.cpp` code and the arithmetic), not a functional defect --
the residency budget check correctly enforces whatever limit is configured
for any chunk size. See `PHASE4_BOOKEND_VIRTUALIZATION.md`'s "Residency and
budget proof" section for the corrected, conditional claim. Frozen Phase 3
rejects the 14,162,688-byte budget, Phase 4 succeeds exactly there (for the
tested chunk-size range), and 14,162,687 rejects. Debug and Release each pass
their full deterministic suite (12/12 without real-artifact CMake options
configured; the real-artifact Release campaign was independently reproduced,
20/20, after applying the same directory-junction workaround Phase 3's own
hardening report documents for the Phase-0 oracle's hardcoded relative
artifact path); the strict `/W4 /WX /permissive-` lane was independently
reproduced clean (12/12). ASan was not independently re-run in this review
pass. See `PHASE4_BOOKEND_VIRTUALIZATION.md` for exact evidence and
limitations.

**FREEZE-HYGIENE CLOSURE, 2026-08-18 (same day, follow-up):** three residual
items from the independent review were resolved -- see `DECISION_LOG.md`
OE-ADR-023 for the full record. (1) The apparent 12/12-vs-13/13 deterministic
test-count discrepancy was root-caused (not a defect): `phase1_frozen_cross_
differential` registers only when `ORCENGINE_FROZEN_PHASE1_SNAPSHOT` is
configured, which Codex's reproduction commands set and the independent
review's first build did not -- both prior reports were correct under
different configurations, verified experimentally by building both ways
(12/12 and 13/13 reproduced directly) and a combined 21/21 with every
optional real-artifact/frozen-snapshot option set together. (2) The recurring
hardcoded-relative-HF-source-path issue (already hit once during Phase-3
hardening) was fixed narrowly: `load_real_weights()` and its call chain now
accept an optional source-directory override, and `gguf_real_f32_forward`'s
CMake registration passes `ORCENGINE_HF_SOURCE_DIR` through -- proven fixed
by a 21/21 real-artifact run using an HF source directory in a genuinely
different worktree, with no directory junction present. (3) The "throwing
row-region observer is isolated" test's coverage gap (it threw on the first
event of any kind, never actually reaching row-region-specific code) was
closed with a new, explicit test that arms the throw only on a
`TensorRowRegionMaterialized` event and verifies every claim (event reached,
threw there specifically, failure counted once, inference completed
bit-identically) -- no engine defect was found. ASan was independently
rebuilt and re-run against the post-closure code (13/13). The chunk-size-
conditional finding from OE-ADR-022 was preserved and generalized into a
verified formula (derived from `forward_impl`'s actual, confirmed-sequential
execution order) rather than walked back, and `ENGINEERING_ROADMAP.md`'s
Phase 6B section now records "region granularity is a policy variable" as
forward-looking `ExecutionPlanner` evidence.

## Phase 4 formal freeze and Phase 5 assignment, 2026-08-18

**VERIFIED (repository- and remote-observed):** Phase 4 is **FORMALLY
FROZEN**, maintainer-authorized (OE-ADR-024), at trusted commit
`944f07b86428ec53d46ca19dc66c3d0d5b1e207d`. Immutable annotated tag
`orcengine-phase4-freeze` created after a clean-worktree check and one
bounded final deterministic verification (12/12) at that exact commit, then
pushed. Remote peeled tag verified to point at
`944f07b86428ec53d46ca19dc66c3d0d5b1e207d`, matching local exactly. Branch
`feat/orcengine-phase4-bookend-virtualization` pushed to `origin`, not
merged into `master`.

**DECIDED (OE-ADR-024):** the post-Phase-4 roadmap question -- old
`ENGINEERING_ROADMAP.md` said "Phase 5 = Initial quantization," while
OE-ADR-021 said the deferred practical-CPU work "remains the logical next
phase" -- was resolved by chronology, not prose position: `git blame`/`git
log -S` showed the quantization heading was last touched **2026-07-31**
(`0c361e26`), sixteen days before OE-ADR-021 (2026-08-16) and before Phase 3
or Phase 4 existed. Stale prose, not a competing decision. Phase 5 is now
assigned to "practical CPU inference semantics," split into three
separately-gated sub-phases (5A KV-cached decode, 5B tokenizer, 5C
workspace/benchmarking) rather than one bundled step. Old quantization
content is unchanged but renumbered to Phase 6; CUDA phases 6A-6D become
7A-7D; stable API/experimental backend/agent-native phases 7/8/9 become
8/9/10. Full renumbering table and reasoning in `DECISION_LOG.md`
OE-ADR-024.

Phase 5A (KV-cached incremental decode reference) implementation is now in
progress on a separate worktree/branch, using the frozen Phase-4 tag as its
parent authority. See `PHASE5A_KV_CACHE_SPEC.md` for its bounded hypothesis,
scope, oracle, memory model, and definition of done.

## Phase 5A real-model composition audit, 2026-08-18

**VERIFIED (runtime-observed, real pinned SmolLM2-135M model):** Phase 5A's
KV-cached decode correctness now holds against the real model, not just
synthetic Fixture C -- a 4-way differential (OrcEngine full-prefix,
OrcEngine cached, HF/PyTorch full-prefix, HF/PyTorch's own independently
constructed native cached decode) all agree on the established
`[1,5,28,284,260,198]` sequence, with `cpp_full_vs_hf_full` divergence
matching the historically-recorded tolerance almost exactly. Real GQA
(actual 9Q/3KV ratio), real RoPE-position, real capacity-boundary
(exact 8,192), and cross-context-isolation fault attacks all pass
(11/11, `test_real_cache_attacks.cpp`). Transactional failure semantics
(poisoned-in-place, protected by the `current_length()` accounting
boundary, proven safe to retry) hold (6/6,
`test_transactional_semantics.cpp`). Full validation matrix (Debug,
Release, strict `/W4 /WX /permissive-`, ASan) is 13/13 clean across all
four lanes.

**REJECTED-SUPERSEDED:** Phase 5A does **not** currently compose with
Phase 3/4's streaming/row-region virtualization -- direct code inspection
confirms it requires a fully-resident `Model`, bypassing `ModelSource`,
`TensorRowRegionMaterializer`, and the Phase-3 layer lifecycle entirely.
KV-memory accounting is independently derived and empirically confirmed
(46,080 bytes/token; `ContiguousAttentionKVStore` eagerly allocates its
full `max_positions` capacity at construction -- measured via a
≈377.5 MiB process working-set jump at cache allocation, matching the
derived value almost exactly). Full detail, evidence labels, and the
19-item freeze-candidate checklist are in `PHASE5A_KV_CACHE_SPEC.md`'s
"Results (2026-08-18, real-model composition-audit pass)" section.

**Proposed verdict: `NOT READY — BLOCKERS REMAIN`** -- specifically, the
Phase-4 composition gap (correctness is proven; bounded-residency
composition is not) and the absence of any independent (non-self-authored)
review. No `orcengine-phase5a-freeze` tag exists; the branch remains
unpushed pending that review.

## OE-ADR-026: composition is now the active gate, 2026-08-18

**DECIDED:** per the maintainer's explicit direction after reviewing the
above, Phase 5A will **not** freeze as a correctness-only, fully-resident
implementation -- OE-ADR-024 already made bounded weight residency an
inherited Phase-5A invariant, and freezing on top of a known violation of
it would let Phase 5B/5C/6 build on an architecture already known to be
wrong. The existing fully-resident cached implementation is **retained**
(not deleted) as a semantic oracle: it isolates cache-math correctness
from residency architecture, which is exactly what a `resident cached vs
virtualized cached` differential needs going forward. The active
completion gate is now: compose persistent KV/context state with Phase-4's
transient, per-layer-materialized weight architecture, and prove the two
implementations agree on complete logits, before independent freeze
review is requested. Full context, the rejected "defer to Phase 5D"
alternative, and the acceptance trigger are in `DECISION_LOG.md`
OE-ADR-026. `PHASE5A_KV_CACHE_SPEC.md`'s "Active Phase-5A completion gate
after real-model audit" section is the current, binding definition of
done -- its earlier synthetic-only gate is retained for historical
accuracy but is no longer the active gate.

## OE-ADR-027: composition implemented and verified equivalent, 2026-08-18

**VERIFIED:** `VirtualizedCachedModel` (Reference Path C) is implemented
using Phase 3/4's own public `ModelSource`/`TensorMaterializer`/
`TensorRowRegionMaterializer`/`ResidencyLedger` contracts -- no frozen
Phase 1/3/4 file was modified. It is proven **bit-identical** to Reference
Path B (the fully-resident cached oracle, retained per OE-ADR-026) on
synthetic Fixture C (9/9 steps) and the real pinned SmolLM2-135M (4/4
steps), and bit-identical to frozen Phase-4's own virtualized full-prefix
reference on the real model as well. `peak_resident_weight_bytes` measures
at 14,162,688 bytes -- matching Phase 4's documented frozen peak exactly.
`peak_active_layers==1` holds at every step, and the underlying
`ResidencyLedger::enter_layer` guard was directly proven to reject a
second concurrently-resident layer, not merely assumed correct. Real KV
cache content is numerically cross-checked against HF's own independently
constructed cache at 5 layer/head/position combinations (max_abs 1e-7 to
2.4e-5). Backing I/O is measured, not assumed: transformer weight bytes
read are effectively identical between cached and full-prefix execution
(caching does not reduce weight rereads per token under this
architecture), while embedding backing bytes ARE measurably reduced. The
real composed KV/weight crossover, from Reference Path C's own measured
peak, is 308 tokens. Full validation matrix (Debug/Release/strict/ASan)
is 18/18 across all four lanes, zero warnings, zero memory-safety
findings. Full detail, the updated 20-item gate (19 satisfied, 1
partial), and the proposed verdict are in `PHASE5A_KV_CACHE_SPEC.md`'s
"Composition implementation results" section and `DECISION_LOG.md`
OE-ADR-027.

**Proposed verdict: `READY FOR INDEPENDENT FREEZE REVIEW`** -- a
recommendation, not a self-authorization. No `orcengine-phase5a-freeze`
tag has been created; the branch remains unpushed pending that review.
*(Historical, as of this entry's date -- superseded by OE-ADR-028's
review and OE-ADR-029's formal freeze below; not the current status.)*

## OE-ADR-028: freeze-closure pass, review findings closed, 2026-08-18

**VERIFIED:** an independent review (via the repo's `grok-review` skill,
full + adversary passes) of the composition candidate returned verdict
`ACCEPT WITH FIXES` and 11 findings (P5A-RVW-001 through 011: 3 CRITICAL,
4 MAJOR, 4 MINOR). This entry records their closure. All three CRITICAL
findings closed with new/hardened evidence: (002) `forward_cached_step`/
`VirtualizedCachedModel::step` now REQUIRE `start_position ==
cache.current_length()`, rejecting gaps/rewinds/resets before any
mutation, with a new explicit low-level seam preserving deliberate
fault-injection capability; (004) the real 5-way differential now
enforces (not just prints) A≡B≡C bit-identical logits, plus a NEW
compiled, CTest-registered, ASan-covered `test_real_composed_evidence.cpp`
that asserts the same bit-identity directly in C++; (006) real-GGUF-backed
Path C is now actually registered as a CTest and runs under
Debug/Release/strict/ASan for the first time, on both the explicit and
tied real artifacts. All four MAJOR findings closed similarly (fail-closed
parity between Path B/C, KV-oracle-completeness enforcement, an exact
materialization-count assertion replacing a one-sided bound, and
clarified ADR wording). Two MINOR findings closed with new tests (tied
real-artifact coverage; the reverse B/C independence attack -- corrupt
only B's resident weights, confirm C, snapshotted independently
beforehand, is completely unaffected). One MINOR finding closed via
documentation superseded-annotations. One MINOR finding (a
materialization-failure residency assertion) is closed with a direct
code assertion (`c89e7801`'s attack 8d, confirming a forced
materialization failure returns the resident-weight ledger to exactly
the permanent FinalNorm bookend) -- the earlier manual code trace is
retained only as supporting evidence, not as the closing evidence
itself. Full detail in `DECISION_LOG.md` OE-ADR-028 and
`PHASE5A_KV_CACHE_SPEC.md`'s "Freeze-closure pass results" section.

**Validation matrix grew from 18 to 30 registered tests.** See the
closure commit history for exact per-lane (Debug/Release/strict/ASan)
pass counts.

**Proposed verdict pending a NEW independent review:
`READY FOR FINAL INDEPENDENT FREEZE REVIEW`** -- a recommendation, not a
self-authorization. No tag created, branch still unpushed.
*(Historical, as of this entry's date -- that NEW independent review
subsequently ran and completed; see OE-ADR-029 below for the current
status, not this paragraph.)*

## OE-ADR-029: Phase 5A formal maintainer freeze, 2026-08-20

**ACCEPTED AND FROZEN.** The full + adversarial independent review
required by OE-ADR-026's acceptance trigger and referenced above
subsequently ran (2026-08-19) against the closure candidate at
`af2dc59b`. Its confirmed BLOCKER and FIX-BEFORE-FREEZE findings (a
CMake environment-fallback ordering bug; stale P5A-RVW-003/010
disposition wording; a `CURRENT_STATE.yaml` internal contradiction;
gate item 18's overly-literal "all pass" criterion) were resolved in
subsequent bounded commits, and focused diff reviews of those
corrections completed cleanly. The independent-review requirement is
therefore complete. On 2026-08-20 the maintainer explicitly approved
Phase 5A for formal local freeze. Phase 5A is now frozen under the
annotated tag `orcengine-phase5a-freeze`. Gate item 15 (per-step
backing-I/O granularity) remains an accepted, explicitly bounded
partial -- the underlying experimental question was already answered
at run-level granularity, and this was never silently upgraded to
fully satisfied. The optional real-GGUF resident-weight-ledger
duplicate assertion (a real-model equivalent of synthetic attack 8d's
check) remains deferred future strengthening, not a freeze blocker --
the cleanup path it would duplicate-check is shared code, already
directly asserted synthetically. The tag and branch remain local and
unpushed. Phase 5B specification drafting started 2026-08-20 on
`feat/orcengine-phase5b-tokenizer` (forked from this frozen authority
via a separate worktree, per this document's own requirement); the
maintainer subsequently approved that specification for implementation
the same day, including all seven previously-unresolved policy
decisions (`DECISION_LOG.md` OE-ADR-030). Stage 1 (native GGUF
tokenizer-metadata construction and fail-closed validation,
`Tools/OrcEnginePhase5B/`) was implemented, closure-corrected, and
green-lane classified the same day (`DECISION_LOG.md` OE-ADR-031):
`smollm2-135m.gguf` is the canonical, tokenizer-bearing Phase 5B
artifact; `smollm2-135m-tied.gguf` is a frozen legacy tensor/output-
head-equivalence fixture with no `tokenizer.ggml.*` metadata, and its
rejection is a required, passing test outcome, not a gap. Three
independent test contracts (synthetic, explicit real-artifact positive,
legacy tied-artifact expected-rejection) are all clean across
Debug/Release/strict/ASan (synthetic 142/142; the other two contracts
pass in full) -- no registered Phase 5B test intentionally fails. The
same day, Stage 2A (`DECISION_LOG.md` OE-ADR-032) implemented exact
native pretokenization (`pretokenize.cpp`), reproducing the pinned
`Digits->ByteLevel` sequence and producing byte-range pretoken
boundaries only. Its `\p{L}`/`\p{N}`/`\s` classification tables were
generated from Python's `unicodedata` and validated against the live
`tokenizers==0.22.2` oracle (2,831 boundary probes + 4,974 random
codepoints, 0 mismatches after correcting a discovered `str.isspace()`
gap). `test_pretokenize` matches a 63-entry oracle-derived fixture
corpus plus 8 invalid-UTF-8 cases byte-for-byte: 209/209 checks, 0
failures, across Debug/Release/strict/ASan. Matches the pinned oracle
across the 63-entry corpus, all generated category boundaries, and the
recorded seeded sample; exhaustive equivalence over every possible
Unicode string is not claimed. The generator was subsequently hardened
(`DECISION_LOG.md` OE-ADR-033): fail-closed on the installed
`tokenizers` version and the supplied `tokenizer.json`'s SHA-256/
declared contract, no hard-coded machine-specific path, a read-only
`--check` mode, and the complete provenance hash record. Same day,
Stage 2B (`DECISION_LOG.md` OE-ADR-034) implemented native byte
mapping, ranked BPE merge execution, and text-to-token-ID encoding,
extending `TokenizerProfile` with `encode(std::string_view,
SpecialTokenMode)`. `SpecialTokenMode::LiteralText` (default) treats
all input as ordinary text; `SpecialTokenMode::RecognizeControlTokens`
(explicit opt-in) recognizes exact CONTROL substrings derived from
validated metadata, never a hard-coded list. The GPT-2 byte alphabet
and the oracle's counterintuitive `encode_special_tokens` polarity
(=False, its own default, RECOGNIZES control strings; =True treats
them as ordinary text) were both established empirically before being
relied on. `test_encode` matches a 386-entry oracle fixture corpus
(including exhaustive 17x17 CONTROL-adjacency coverage) plus a
256-entry check comparing an independently reconstructed reference
byte-to-codepoint table against the oracle-generated one (not a direct
comparison against the production table): 1,198/1,198 checks, 0 failures
(1,182/1,182 at initial delivery, `DECISION_LOG.md` OE-ADR-034; +16 from
the OE-ADR-035 reconciliation pass's tightened invalid-UTF-8
exception-type assertions), across Debug/Release/strict/ASan. Matches
the pinned oracle across that corpus and the described cases; exhaustive
equivalence is not claimed.
Same day (2026-08-22), native decode (`decode()`/`decode_token_bytes()`,
`DecodeControlPolicy::PreserveControlTokens`/`SkipControlTokens`,
fail-closed on invalid token IDs, inverse GPT-2 byte-alphabet mapping
validated at construction), streaming UTF-8 decode (`Utf8StreamDecoder`
-- explicit error on malformed or incomplete-at-end-of-stream UTF-8,
poisoned-state-after-error semantics), the Section 11 frozen-engine
integration proof (native `encode("Hello, world!")` -> frozen Phase 5A
`forward_cached_step` -> native `decode()`, two independent executions
bit-identical across complete logits/selected tokens/generated
continuation/committed KV-cache contents, run against the real
SmolLM2-135M F32 GGUF), and the pinned llama.cpp `b10436` (2026-08-14,
commit `6fed9f6ff`) three-way oracle comparison (Hugging Face
`tokenizers==0.22.2` / llama.cpp reading the GGUF's own metadata /
native Phase 5B -- exact agreement on all 5 canonical dual-source
fixtures plus a 15-item representative subset; one apparent
disagreement fully root-caused as llama-tokenize.exe's CLI having no
literal-text mode, not a tokenizer defect) were all implemented and
oracle-validated (`DECISION_LOG.md` OE-ADR-036): decode 836/836 checks,
streaming decode 36/36, integration 8/8, all across
Debug/Release/strict/ASan (12/12 including inherited Phase 5A tests
newly reachable through this phase's build-graph change). Independently
reviewed (Grok 4.5, full mode, over the complete branch diff since the
Phase 5A freeze base: zero BLOCKER findings, three MINOR doc-staleness
findings, all reconciled into this freeze pass). **Phase 5B is complete
and formally frozen as of 2026-08-22** under the annotated tag
`orcengine-phase5b-freeze` (`DECISION_LOG.md` OE-ADR-037). Full detail in
`DECISION_LOG.md`
OE-ADR-029 through OE-ADR-037,
`PHASE5A_KV_CACHE_SPEC.md`'s current status sections, and
`PHASE5B_TOKENIZER_SPEC.md` (status: complete and frozen).

## Current blockers

None remaining for Phase 4, which is formally frozen. None remaining
for Phase 5A, which is formally frozen as of 2026-08-20 under
`orcengine-phase5a-freeze` (OE-ADR-029) -- the independent review that
was the sole remaining blocker has completed, its findings were
resolved, and the maintainer has approved the freeze. Gate item 15's
bounded partial and the optional real-GGUF ledger assertion remain
recorded, accepted gaps, not blockers. Phase 5B is the next separately
gated sub-phase; its specification was accepted for implementation
2026-08-20 (`PHASE5B_TOKENIZER_SPEC.md`; `DECISION_LOG.md` OE-ADR-030)
and Stage 1 (metadata construction and validation) was implemented,
closure-corrected, and green-lane classified the same day
(`DECISION_LOG.md` OE-ADR-031 resolved the tied-GGUF artifact-
classification question: `smollm2-135m-tied.gguf` is a frozen legacy
fixture, not a tokenizer-bearing artifact, and its rejection is a
required, passing test outcome). Same day, Stage 2A (exact native
pretokenization, `DECISION_LOG.md` OE-ADR-032) was implemented and
oracle-validated. Also same day, Stage 2B (native byte mapping, BPE,
and text-to-token-ID encoding, `DECISION_LOG.md` OE-ADR-034) was
implemented and oracle-validated. On 2026-08-22, native decode,
streaming UTF-8 decode, the Section 11 frozen-engine integration proof,
and the pinned llama.cpp `b10436` three-way oracle comparison were
implemented, oracle-validated, and independently reviewed
(`DECISION_LOG.md` OE-ADR-036), and Phase 5B was formally frozen
(`DECISION_LOG.md` OE-ADR-037, tag `orcengine-phase5b-freeze`). **No
blockers remain for Phase 5B.** Phase 5C may now begin -- its only
dependency (Phase 5B closing) is satisfied. **Post-freeze (same day, a
separate commit on `feat/orcengine-phase5b-tokenizer`, NOT moving the
freeze tag):** a narrow evidence-hardening pass fixed a false-pass path
in the frozen-engine integration test, corrected the native comparison
CLI's protocol to be byte-safe (it previously could not represent a
prompt containing embedded LF/CRLF), and replaced the three-way oracle
comparison's previously narrative-only "15-item representative subset"
claim with a committed, independently-reproducible driver
(`three_way_tokenizer_comparison.py`, 19-fixture durably-defined corpus,
exact three-way agreement on all 18 ordinary fixtures plus a correctly
policy-limited comparison for the one CONTROL-lookalike fixture) --
see `DECISION_LOG.md` OE-ADR-038. Production tokenizer behavior did not
change. See `DECISION_LOG.md` OE-ADR-022 through OE-ADR-038 for the
full record.

## How to update this document

For every change, include date, commit or artifact identity, exact command where applicable, and whether the evidence is repository-observed, runtime-observed, externally sourced, or inferred. Move superseded beliefs into [Decision Log](DECISION_LOG.md) rather than erasing the history.
