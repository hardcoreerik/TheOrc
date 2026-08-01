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

## TheOrc integration boundary

`OrcEngineRuntime` should translate TheOrc messages/tools into a prompt, call the stable native API, stream decoded bytes, parse or constrain tool calls according to supported capabilities, and map telemetry honestly. It should not reimplement `RuntimeOrchestrator`, `ModelDepot`, or product scheduling.

See [TheOrc Integration](THEORC_INTEGRATION.md).
