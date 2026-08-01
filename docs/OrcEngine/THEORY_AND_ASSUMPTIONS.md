# Theory and Assumptions

## Purpose

This is the falsifiable-claims ledger. It prevents architectural enthusiasm from becoming undocumented “truth.” Each assumption must acquire evidence, be revised, or be rejected.

## Numerical theory baseline

A decoder-only transformer maps token IDs and prior state to logits through repeated normalization, linear projections, attention, nonlinear feed-forward transforms, and residual connections. The conceptual equations are compact; implementation correctness depends on tensor orientation, strides, grouping, position conventions, masking, precision, and cache indexing.

For layer input `x`:

```text
h  = RMSNorm(x)
q  = Wq h
k  = Wk h
v  = Wv h
q,k = RoPE(q,k,position)
a  = softmax(mask(q k^T / sqrt(head_dim))) v
x' = x + Wo a
n  = RMSNorm(x')
f  = Wdown (SiLU(Wgate n) * Wup n)
y  = x' + f
```

Exact model semantics may differ. GGUF metadata and the pinned architecture definition are authoritative.

## Assumption ledger

### A-001: A narrow reference engine is tractable

- **State:** HYPOTHESIS
- **Claim:** One small classic Llama-style model can be implemented without first building a generic graph compiler.
- **Evidence needed:** matching per-layer outputs and logits for fixed fixtures.
- **Falsified by:** unavoidable model-dependent dynamic behavior or complexity that cannot be represented by the fixed graph.
- **Consequence if false:** narrow or change the first model; do not build speculative infrastructure.

### A-002: Float32 is the correct first precision

- **State:** DECIDED for the first reference path.
- **Claim:** Float32 reduces quantization and mixed-precision confounders enough to make debugging practical.
- **Evidence needed:** oracle configured to expose compatible float32 reference values.
- **Caveat:** floating-point operation ordering still prevents universal bitwise equality.

### A-003: Logits alone are insufficient for diagnosis

- **State:** DECIDED.
- **Claim:** A final-logits mismatch cannot localize tokenizer, layout, RoPE, attention, cache, or FFN errors.
- **Required response:** capture selected intermediate tensors at embeddings and every layer boundary, with deeper capture enabled around the first divergence.

### A-004: CPU scalar/reference paths remain useful

- **State:** HYPOTHESIS.
- **Claim:** Retaining simple kernels provides an internal oracle for optimized CPU and CUDA paths.
- **Falsified by:** maintenance cost materially exceeding debugging value after stable optimized kernels exist.

### A-005: GGUF can be supported narrowly

- **State:** HYPOTHESIS.
- **Claim:** A strict compatibility tuple avoids the need to chase the entire evolving format and architecture ecosystem.
- **Evidence needed:** clean failure on unsupported fixtures and successful load of the pinned artifact.

### A-006: BLAS/cuBLAS do not erase engine ownership

- **State:** DECIDED.
- **Claim:** Calling general matrix libraries is compatible with “from scratch” because OrcEngine still owns model parsing, graph semantics, state, operators, decoding, and lifecycle.
- **Boundary:** a complete transformer/inference runtime is not allowed as the executor.

### A-007: Cache ownership can become agent-native

- **State:** HYPOTHESIS, long-horizon.
- **Claim:** Explicit role/context ownership may enable better telemetry and reuse than anonymous request slots.
- **Evidence needed:** a concrete TheOrc workload and comparison against existing LLamaSharp capabilities.
- **Do not infer:** cross-role cache sharing is safe. Adapters can change activations and invalidate that assumption.

### A-008: Deterministic greedy output is a useful gate

- **State:** DECIDED.
- **Claim:** With fixed input, model, position, cache, and argmax rules, next-token agreement provides a strong end-to-end check.
- **Caveat:** tied or nearly tied logits can produce different tokens from tiny numerical differences; the artifact must record the logit margin.

### A-009: Plausible text is not correctness evidence

- **State:** DECIDED.
- **Claim:** Language redundancy can hide numerical errors. “It chats” is never an acceptance test.

### A-010: CUDA should begin with library GEMM

- **State:** PROPOSED.
- **Claim:** cuBLAS removes one large kernel-development variable while device memory, layout, synchronization, and elementwise semantics are verified.
- **Upgrade condition:** profiling shows GEMM integration or surrounding kernels are the limiting factor.

### A-011: The first engine belongs outside product routing

- **State:** DECIDED.
- **Claim:** Standalone correctness isolates engine failures from prompts, tools, UI streaming, fallback, and scheduler behavior.

### A-012: A C ABI is the durable integration seam

- **State:** PROPOSED.
- **Claim:** Opaque C handles avoid C++ ABI coupling and are straightforward for .NET P/Invoke.
- **Evidence needed:** minimal ownership/cancellation prototype after core correctness.

## Floating-point comparison assumptions

The [PyTorch numerical-accuracy guidance](https://docs.pytorch.org/docs/stable/notes/numerical_accuracy.html) notes that floating-point operations are not associative and results may vary across platforms and implementations. Therefore:

- bitwise equality is required for token IDs, parsed metadata, tensor shapes, and deterministic integer transformations;
- floating tensors use absolute and relative tolerances recorded per operator;
- comparisons record max absolute error, max relative error, mean error, NaN/Inf count, and worst index;
- cosine similarity may supplement but never replace elementwise bounds;
- optimized paths compare against both OrcEngine scalar results and the external oracle;
- token agreement is reported alongside logit distance and top-token margin.

## Assumptions requiring early experiments

1. Tensor orientation from GGUF descriptors to mathematical matrices.
2. RMSNorm epsilon source and precision.
3. RoPE layout, base, scaling, and position indexing.
4. Grouped-query head mapping.
5. Causal-mask representation during prompt versus single-token decode.
6. BOS/EOS insertion and byte fallback.
7. Tied versus separate output weights.
8. Cache write/read ordering.
9. Reference framework operation ordering.

Each maps to [Research Questions](RESEARCH_QUESTIONS.md) and [Phase 0 Reference Oracle](PHASE_0_REFERENCE_ORACLE.md).
