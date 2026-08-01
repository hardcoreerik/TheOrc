# Research Questions

## Research discipline

Each question needs an owner, method, evidence artifact, and decision impact. Reading alone can establish a specification fact; behavioral claims require an experiment.

Priority meanings:

- **P0:** blocks the first executable phase.
- **P1:** blocks a planned near-term phase.
- **P2:** useful after baseline correctness.
- **P3:** speculative; do not spend implementation time yet.

## P0: Reference and model selection

### RQ-001: What is the first model artifact?

- **Status:** partially answered. `OE-L0-SYNTH-1` is required; `HuggingFaceTB/SmolLM2-135M` revision `93efa2f097d58c2a74874c7e644dbc9b0cee75a2` is the selected real candidate.
- Remaining evidence: generated synthetic artifact hashes, converted GGUF hash, converter revision/command, tensor and tokenizer inventories, distribution review, and oracle-load script.

### RQ-002: What is the oracle?

- **Status:** direction decided, exact versions pending.
- Roles: hand-derived microcases, a pinned explicit Python/NumPy/PyTorch semantic oracle, and pinned llama.cpp as deployment comparator.
- Risk: using only llama.cpp may reproduce its assumptions rather than the model definition; using only a framework may hide conversion differences.
- Required outcome: one primary semantic oracle plus one secondary integration oracle, with pinned versions.

### RQ-003: Where are comparison taps inserted?

- Minimum: embeddings, post-attention residual, post-FFN residual, final norm, logits, and cache slices.
- Escalation: Q/K/V before and after RoPE, attention scores/probabilities, gate/up/down projections.
- Evidence: stable artifact naming and tensor metadata schema.

### RQ-004: What tolerances are justified?

- Method: repeat oracle runs, compare independent float32 implementations, characterize accumulation-order sensitivity, and record per-operator ranges.
- Do not choose one universal epsilon by convenience.

### RQ-005: How are model and tokenizer provenance locked?

- Required: SHA-256, source URL, revision, license files, conversion command, converter revision, and generated-artifact manifest.

## P0: GGUF and architecture semantics

### RQ-006: Which GGUF version and metadata keys are mandatory?

- Start from the [GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md).
- Produce a strict supported-key table, optional-key behavior, unknown-key behavior, and limits.

### RQ-007: Which tensor naming/layout contract applies?

- Compare source checkpoint names, converter mapping, GGUF descriptors, llama.cpp architecture mapping, and mathematical dimensions.
- Verify with small known tensors rather than assuming row/column order.

### RQ-008: What tokenizer family does the pinned model use?

- Determine model type, normalization, pre-tokenization, merges/scores, byte fallback, special tokens, and add-prefix-space behavior.
- Build golden byte-level cases including invalid UTF-8 boundaries and non-ASCII text.

### RQ-009: What exact RoPE variant applies?

- Capture dimension, frequency base, scaling type, interleaving, and position origin.
- Validate Q/K tensors before and after rotation.

### RQ-010: What exact attention variant applies?

- Confirm head counts, KV-head counts, grouping, mask convention, scaling, bias, and cache layout.

## P1: Implementation structure

### RQ-011: Eager operators or a graph?

- Default: eager fixed model loop.
- Escalation trigger: backend scheduling, reuse, or workspace planning becomes materially harder without a graph representation.

### RQ-012: How is the decided staged language stack pinned?

- **Status:** language roles decided by OE-ADR-012; package/compiler versions remain open.
- Pin the Python oracle environment first, then C++20 compiler/CMake profiles before Phase 1. The Python reference must not become a production execution backend.

### RQ-013: Which BLAS implementation is acceptable for CPU baseline?

- Assess portability, licensing, tensor layout, deterministic behavior, thread control, and availability in CI.
- Scalar GEMM remains the no-dependency fallback for tiny fixtures.

### RQ-014: What allocator model is sufficient?

- Candidate: immutable mapped weights plus one context-owned monotonic workspace reset per decode.
- Measure allocations and peak bytes before designing a general allocator.

### RQ-015: What is the cancellation granularity?

- Minimum: between layers and generation steps.
- Determine whether long GEMM calls need backend-specific cooperative behavior.

## P2: Optimization and CUDA

### RQ-016: Where does CPU time go after correctness?

- Profile prompt evaluation and one-token decode separately.
- Only optimize measured operators.

### RQ-017: Which SIMD baseline matches target machines?

- Inventory actual CPU features and deployment goals.
- Runtime dispatch is not required until multiple paths exist.

### RQ-018: Explicit device memory or unified memory?

- NVIDIA documents both; benchmark load, prompt, and decode behavior on actual target GPUs.
- Initial proposal: explicit long-lived device weights/cache and bounded transfers.

### RQ-019: cuBLAS or cuBLASLt first?

- Compare API complexity, row/column layout, workspace, deterministic controls, and target shape performance.

### RQ-020: Which elementwise CUDA kernels are necessary?

- Likely RMSNorm, RoPE, softmax/mask, residual, activation/gating, embedding lookup, and cache movement.
- Confirm through the CPU operator inventory.

## P2: Quantization

### RQ-021: First quantization format?

- Proposed order: Q8_0 then Q4_0.
- Verify format stability, block layout, scale precision, reference converter, and expected models.

### RQ-022: Dequantize-first or direct dot product?

- Decision rule: implement dequantize-to-float reference first; add direct kernels only with matching results and measured need.

### RQ-023: What error budget is acceptable?

- Compare against the same model’s float path, not only against another quantized engine.
- Report perplexity or task quality only after operator/logit evidence.

## P3: Agent-native differentiation

### RQ-024: Can role-owned contexts provide unique value?

- Need a real TheOrc workload, current-runtime baseline, measurable limitation, and security/lifecycle model.

### RQ-025: Can Context Fabric prefixes be pre-tokenized or cached safely?

- Separate token reuse from KV reuse.
- Validate document identity, prompt-template identity, model identity, adapter identity, and position semantics.

### RQ-026: Is cross-role cache reuse ever safe?

- Default answer: unknown and prohibited.
- Adapters and system prompts may change activations; proof must be model- and state-specific.

### RQ-027: What HIVE partition has favorable communication cost?

- Layer, expert, prompt-processing, draft-model, and independent-sequence distribution have different transfer profiles.
- Do not roadmap until single-device profiling exists.

## Research output template

```text
Question ID:
Status: open | investigating | answered | rejected
Primary sources:
Repository evidence:
Experiment command:
Artifact paths and hashes:
Result:
Limitations:
Decision impact:
Reviewer:
Date:
```
