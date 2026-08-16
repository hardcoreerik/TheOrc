# Architecture

## Architecture status

Everything in this document is **PROPOSED** unless explicitly marked verified or decided. Component names describe responsibilities, not committed classes or directories.

## System boundary

```text
TheOrc control plane
  IModelRuntime / future OrcEngineRuntime adapter
                |
         stable C ABI boundary
                |
OrcEngine core  |  model + context + decode API
  model loader  |  tokenizer  |  graph  |  cache  |  sampler
                |
       tensor execution contract
          /                 \
  scalar/CPU backend     future CUDA backend
```

The core engine owns inference semantics. Backends own operator execution and memory placement. TheOrc owns product orchestration. [Phase 0 Architecture Profile](PHASE_0_ARCHITECTURE_PROFILE.md) defines model mathematics independently of these software layers.

**DECIDED implementation direction:** Python/NumPy/PyTorch supplies test-only Phase 0 oracles; the standalone engine is C++20; the later NVIDIA backend is CUDA C++; and .NET integration uses a C ABI plus C# `SafeHandle` ownership.

## Proposed layers

### 1. Future public C API — post-correctness

A stable C ABI should expose opaque handles and plain data structures. C++ implementation details must not cross into .NET.

Candidate responsibilities:

- create/destroy engine instance;
- load/unload model;
- create/destroy context;
- tokenize and detokenize;
- evaluate prompt tokens;
- decode one or more new tokens within supported limits;
- read logits or sample through an explicit decoder;
- query capabilities and structured errors;
- cancel work cooperatively;
- retrieve measured allocation and timing telemetry.

The API is a Phase 7 deliverable and should be designed after the standalone core works, not before. Phase 0 and Phase 1 must not construct ABI abstractions speculatively.

### 2. Model ingestion

The loader validates the GGUF envelope, reads typed metadata, validates tensor descriptors, maps tensor names to the supported architecture, checks dimensions, and exposes immutable weight views. It must not silently coerce an unknown architecture into Llama.

See [Model Format and GGUF](MODEL_FORMAT_AND_GGUF.md).

### 3. Tokenizer and prompt pipeline

This layer converts bytes/text to token IDs, handles special tokens and model normalization rules, applies a caller-selected chat template outside the numerical graph, and converts generated IDs back to bytes safely.

See [Tokenizer and Prompt Pipeline](TOKENIZER_AND_PROMPT_PIPELINE.md).

### 4. Model definition

The first model definition is a fixed decoder graph:

```text
token embedding
repeat N layers:
  RMSNorm
  Q/K/V projections
  RoPE
  causal grouped-query attention against cache
  output projection + residual
  RMSNorm
  gate/up projections + SiLU/SwiGLU
  down projection + residual
final RMSNorm
language-model head
logits
```

The implementation may initially execute operators eagerly. A generic graph compiler is a non-goal until measurements show it is needed.

### 5. Tensor core

The tensor core defines dtype, shape, strides, storage ownership, views, bounds checks, operator contracts, workspace allocation, and backend dispatch. The scalar reference path remains available as a correctness oracle.

See [Tensor Engine Design](TENSOR_ENGINE_DESIGN.md).

### 6. CPU backend

The first backend performs float32 operators. It may begin with clear scalar loops and an established BLAS call for dense matrix multiplication. Threading and SIMD arrive only after differential tests exist.

See [CPU Backend Design](CPU_BACKEND_DESIGN.md).

### 7. CUDA backend

The later CUDA backend owns device allocation, transfers, streams, cuBLAS handles, kernel launches, synchronization, errors, and telemetry. The first version should use cuBLAS for dense GEMM and minimal custom kernels for elementwise operations.

See [CUDA Backend Design](CUDA_BACKEND_DESIGN.md).

### 8. Context and KV cache

A context owns sequence position, cache storage, current token history, scratch state, cancellation state, and sampling state. A context must not outlive its model. Cache layout is versioned and backend-specific but governed by a common semantic contract.

See [KV Cache and Context Design](KV_CACHE_AND_CONTEXT_DESIGN.md).

### 9. Decoder and sampler

Greedy selection is the Phase 1 baseline. Stochastic samplers become composable logit transforms only after raw logits match the oracle.

See [Sampling and Decoding](SAMPLING_AND_DECODING.md).

## Data ownership

| Resource | Owner | Lifetime |
|---|---|---|
| Mapped GGUF bytes | Model | Load to final model release. |
| Validated metadata | Model | Immutable after load. |
| Weight tensors/views | Model | Immutable after load. |
| Backend weight copies | Model/backend allocation set | Until model release after all contexts end. |
| KV cache | Context | Context creation to reset/destroy. |
| Scratch/workspace | Context or execution arena | Scoped to documented execution lifetime. |
| Logits | Context output buffer | Valid until next decode unless copied. |
| Token text buffer | Caller-owned result or callback duration | Explicit in API. |

Reference counting is not assumed. The first version may require explicit parent-before-child destruction and reject model destruction while contexts exist.

## Error model

Errors require stable categories:

- invalid argument;
- malformed or unsupported GGUF;
- missing or mismatched tensor;
- unsupported architecture/tokenizer/dtype;
- out of memory;
- backend initialization or device failure;
- numerical invalidity;
- cancelled;
- internal invariant violation.

Every error includes a stable code and diagnostic message. Native exceptions must not cross the C ABI.

## Concurrency model

The first engine is single-threaded at the API level, even if BLAS uses worker threads. A model may have one active context. Concurrent context execution, scheduler queues, and shared weights become later explicit capabilities.

This deliberate ceiling prevents undefined cache mutation and lifetime races while correctness is being established.

## Telemetry

Telemetry is measurement, not estimate, where the engine owns the resource:

- mapped file bytes;
- host weight bytes;
- device weight bytes;
- cache bytes;
- workspace bytes;
- load duration;
- prompt-evaluation duration;
- decode duration;
- tokens evaluated/generated;
- backend and device identity.

Estimates must carry an `estimated` label and the formula inputs.

## Memory model: a model is a logical address space, not a VRAM resident

**Added 2026-08-15** (`Infinite_Model_Runtime_Claude_Handoff.md` steering review; see [Decision Log](DECISION_LOG.md) OE-ADR-019). This section is a PROPOSED contract for Phase 6B onward, motivated by real evidence gathered in Phase 0's ablation-diagnostic tooling (`Tools/OrcEnginePhase0/oracle/gguf_streaming_loader.py`): Meta-Llama-3.1-8B, which failed to load under every full-residency approach tried in the same research session, completed a full forward-pass sweep using **3.17GB peak VRAM** by loading one transformer layer from disk, using it, and discarding it before loading the next. That is proof, not speculation, that "the model lives in VRAM" cannot be a foundational assumption OrcEngine's types bake in.

**Thesis:** VRAM is a cache/execution tier, not synonymous with "the loaded model." RAM and NVMe are additional storage tiers. A logical tensor is not the same thing as its backing bytes, and is not the same thing as a currently-resident allocation.

Three distinct concepts, deliberately kept separate:

- **`LogicalTensor`** — semantic identity: shape, strides/layout, architecture role (e.g. "layer 12's attention output projection"), logical dtype. This identity must survive eviction and reload unchanged — a tensor that gets paged out to NVMe and back is still the same `LogicalTensor`.
- **`BackingExtent`** — where the tensor's bytes actually come from: source artifact, file/region, byte offset, encoded length, source codec/quantization, checksum/provenance. One `LogicalTensor` has exactly one canonical `BackingExtent` (the GGUF file), but may have zero or more derived/cached extents (see the derived execution cache, below).
- **`ResidentView`** — a CURRENTLY MATERIALIZED copy: which memory tier/backend, resident address/handle, resident dtype, compute dtype, tile/page bounds, and a residency lease/lifetime. A `LogicalTensor` may have zero, one, or (transiently, during a page-in/page-out) more than one `ResidentView` at a time.

**Invariant:** `LogicalTensor != BackingExtent != ResidentView`. Views do not imply ownership of the backing data. Residency is potentially temporary. Code that holds a `LogicalTensor` reference must not assume a `ResidentView` currently exists for it.

**Phase 1 stays deliberately trivial regardless of this contract's eventual sophistication** — see the Permanent verification rule at the bottom of `ENGINEERING_ROADMAP.md`. A correct-but-boring Phase 1 implementation: `ResidentView` = a plain CPU pointer, always. `BackingExtent` = a `mmap`'d GGUF file section. There is exactly one `ResidentView` per `LogicalTensor`, created once at load and never evicted. The contract exists so a LATER implementation (Phase 6B onward) can change *how* these are satisfied without changing the engine's semantics or its callers' code.

### "Model loaded" does not mean "fully resident"

For OrcEngine, **"model loaded" means:** source opened, GGUF validated, tensor index built, architecture manifest built, the model is addressable and executable through an execution plan. It does **NOT** require every tensor to be copied into RAM, and does not require every tensor to be copied into VRAM. Separate `Model::Open` (source validation, tensor index) from `ExecutionPlan::Create` (residency/placement decisions) from `Context::Create` (per-sequence state) — or equivalent concepts — rather than one monolithic "load" call that also decides placement. This split is essential for models larger than RAM or VRAM; collapsing it back into one step is exactly the assumption Phase 6B exists to prevent from being baked into Phase 1's types.

### ExecutionPlanner is not a second OrcScheduler

TheOrc already has `OrcScheduler` (`OrchestratorIDE/Core/Runtime/OrcScheduler.cs`), which answers: *should this workload run, on which role/node, interactive or background, against what resource policy?* OrcEngine's eventual `ExecutionPlanner` answers a different question entirely: *where should tensors live, what's resident right now, what streams, what's paged, what's the working-set size, what transport/resident precision is in use, what's the fallback plan if a device allocation fails?* These must stay two separate concepts with two separate names — do not build a second, ambiguous "Scheduler." Candidate future plan types (not implemented, names only): `ResidentCPU`, `ResidentCUDA`, `StaticHybrid`, `PagedCUDA`, `CompressedPagedCUDA`.

### `gpu_layers` is a placement mechanism, not an ABI concept

`gpu_layers` (the llama.cpp partial-offload knob TheOrc's `OrcScheduler`/`RuntimeOrchestrator` already use today for real VRAM admission estimates — see `PROJECT_TRUTH.md`) is a useful CURRENT signal and should keep being used where it already is. It must **not** become the primary placement contract in OrcEngine's eventual stable C ABI. A single integer cannot describe placement once OrcEngine supports (even eventually) embeddings in RAM, selectively resident tensors, streamed tensors, tiled MLPs, KV split across VRAM/RAM, a vocabulary-paged output head, or independently-resident MoE experts. Placement belongs in an execution-plan/capabilities structure designed for that, not a legacy layer-count integer promoted past its original scope.

### Four independent precision concepts

Do not assume **source format == transport format == resident format == compute format.** Keep them as four independent choices from the start of Phase 6B's design, even though Phase 1-6A may set all four to the same value in practice:

- **Source format** — what the canonical GGUF actually stores (e.g. Q5_K).
- **Transport format** — what moves over PCIe/disk I/O (may equal source format, avoiding an early decompress-then-recompress round trip).
- **Resident format** — what sits in VRAM/RAM between uses (e.g. fp16, to halve footprint vs fp32).
- **Compute/accumulator format** — what the actual arithmetic runs in.

**This is not a hypothetical concern — it caused a real, confirmed bug this session that motivates keeping resident and compute format independent by design, not by accident.** The GPU/streaming ablation oracle (`oracle/model_gpu.py`) initially stored AND computed in fp16 for VRAM savings. Verification against the CPU oracle (`oracle/verify_gpu_against_cpu.py`) caught real silent overflow: RMSNorm's `x^2` reduction overflowed fp16's max (65504) once the residual stream reached ~20000+ magnitude (`rsqrt(inf) = 0`, silently zeroing all downstream output); a second, independent overflow was later found in raw attention-score accumulation (an overflowed `+inf` at an unmasked position colliding with the causal mask's `-inf` produces `NaN`); a third, narrower overflow was found in an FFN down-projection during a full 608-component sweep. Patching each site individually was unbounded whack-a-mole against every new model/prompt/ablation combination. The actual fix: keep RESIDENT format fp16 (the VRAM saving is real and worth keeping) but make COMPUTE format float32 unconditionally (cast each weight up transiently at its point of use) — this closed the entire bug class at once, at zero measured speed cost (these matmuls are memory-bandwidth-bound, not compute-bound, on the tested GPU). The general lesson generalizes directly to the eventual CUDA backend: resident-format compression and compute-format precision are separable engineering decisions, and conflating them is a real correctness risk, not a theoretical one.

### Context state is abstract, and pageable KV is not speculative

TheOrc's Native Runtime has already demonstrated, in production, that: fixed KV slot counts hurt; recurrent architectures need different state than plain attention KV; cancellation can poison executor state if not handled carefully; a `NoKvSlot` failure is not merely "context too large"; and unified context assumptions break on hybrid/recurrent models (see `PROJECT_TRUTH.md` and the Native Runtime v2 spec for the concrete incidents). OrcEngine should not re-derive these limitations from scratch. Keep Phase 1's context implementation simple (`ContiguousKVStore`), but define the abstract contract now: `ContextStateStore` with `AttentionKV` / `RecurrentState` / `SequenceState` / `SamplerState` as named sub-concerns, so `PagedKVStore` / `TieredKVStore` / `QuantizedKVStore` can be added later without redefining what "context state" means. See [KV Cache and Context Design](KV_CACHE_AND_CONTEXT_DESIGN.md).

### Derived execution cache (disposable, GGUF stays canonical)

GGUF remains the canonical, required model format — OrcEngine does not invent a competing container. A future, entirely optional, content-addressed derived cache (conceptually `.orc/cache/models/<model-hash>/<planner-version>/`) may hold rebuild-on-demand artifacts: aligned tensor pages, tensor indexes, transcode caches, alternate execution-quantization layouts, ablation/sensitivity metadata, checksums. Think shader cache / compiled-execution cache, not a replacement model format — it must be disposable, rebuildable, and tied to both source-model identity and engine/planner version.

## TheOrc integration boundary

`OrcEngineRuntime` should translate TheOrc messages/tools into a prompt, call the stable native API, stream decoded bytes, parse or constrain tool calls according to supported capabilities, and map telemetry honestly. It should not reimplement `RuntimeOrchestrator`, `ModelDepot`, or product scheduling.

See [TheOrc Integration](THEORC_INTEGRATION.md).
