# Tensor Engine Design

## Design objective

Provide the smallest tensor substrate that executes the supported inference graph correctly and can be checked independently. This is not a general machine-learning framework.

## Non-goals

- autograd;
- training;
- arbitrary dynamic graphs;
- user-defined operators;
- broadcasting beyond explicitly required cases;
- sparse tensors;
- distributed tensor semantics;
- a public NumPy-like API.

## Tensor descriptor

A tensor needs:

- dtype;
- rank and dimensions;
- byte strides or a documented contiguous layout;
- immutable or mutable access mode;
- storage handle plus byte offset;
- checked logical and physical byte extent;
- backend/device identity;
- optional debug name.

Views do not own storage. The owning model, context, or arena must outlive every view.

## Initial dtypes

- F32 for computation and synthetic weights.
- Potential F16 storage converted to F32 only if required by the pinned model.
- Integer token IDs and indices.
- Boolean/implicit causal mask representation; avoid materializing a full mask if a loop bound suffices.

Quantized block types arrive under [Quantization Plan](QUANTIZATION_PLAN.md).

## Required operators

1. embedding row lookup;
2. matrix-vector and small matrix-matrix multiplication;
3. elementwise add and multiply;
4. RMSNorm;
5. RoPE;
6. scaled dot-product attention with causal bounds;
7. stable softmax;
8. SiLU and SwiGLU composition;
9. reshape/view operations that do not copy;
10. copy into and read from KV cache;
11. argmax over logits.

Each operator defines accepted ranks, layout, aliasing rules, accumulation type, NaN/Inf policy, and error behavior.

## Eager first

The proposed first model loop calls operators directly. A graph object, optimizer, fusion planner, and generalized scheduler are skipped until an actual backend or memory-planning problem requires them.

This keeps control flow visible during differential debugging.

## Shape safety

All public construction and operator boundaries use checked multiplication and validate:

- nonzero supported rank;
- dimensions within configured limits;
- shape compatibility;
- byte extent within storage;
- dtype alignment;
- no forbidden output/input overlap;
- index range.

Release builds may remove redundant inner-loop checks only after boundary validation makes the invariants provable.

## Storage classes

### Model storage

Read-only mapped or allocated weights. Lifetime spans all contexts.

### Context storage

KV cache, token history, logits, and durable sequence state.

### Execution workspace

Temporary normalized vectors, projections, attention scores, and FFN intermediates. Proposed implementation: a context-owned bounded arena reset at safe execution boundaries.

### Debug capture

Explicit opt-in copies of named tensors. Never retain views into a workspace after reset.

## Allocation plan

Phase 1 may allocate clear vectors per operation for diagnosis. Before real-model execution, calculate upper bounds from validated model dimensions and allocate reusable workspace. An allocation ledger records owner, purpose, bytes, backend, and lifetime.

No general garbage collection or reference-counted tensor graph is proposed.

## Numerical contracts

### RMSNorm

Confirm exact formula, epsilon placement, and accumulation precision from the pinned model. Sum squares in float32 or better-defined accumulator; compare against oracle microcases.

### Softmax

Subtract maximum before exponentiation. Handle an empty legal range as an invariant error. Define behavior for non-finite inputs and fully masked rows.

### RoPE

Treat pairing/interleaving, rotary dimension, frequency base, scaling, and position as explicit model parameters. Never assume every head dimension rotates.

### Attention

Separate semantic grouped-query mapping from storage layout. Prompt and incremental decode must share the same semantic function.

### Matmul

Name logical dimensions (`out_features`, `in_features`, `tokens`) and isolate physical GGUF layout in tensor views or loader mapping. Avoid transpose flags scattered through model code.

## Backend contract

A backend reports supported dtype/operator/layout combinations, creates storage, performs copies, executes operators, synchronizes, and returns structured errors. Unsupported combinations fail before partial execution.

The scalar CPU path is the semantic reference. Optimized CPU and CUDA paths are differential implementations, not new semantics.

## Debuggability

Build-time or runtime diagnostic mode should provide:

- tensor metadata and bounded value samples;
- finite-value checks after named taps;
- allocation ledger;
- operator timing;
- first-divergence capture;
- deterministic tensor serialization with schema/version/hash.

## Upgrade triggers

Add a graph representation only when required for at least one measured need: backend partitioning, workspace liveness planning, fusion, repeated graph capture, or execution scheduling. Add a custom allocator only when allocation profiles show the arena is inadequate.
