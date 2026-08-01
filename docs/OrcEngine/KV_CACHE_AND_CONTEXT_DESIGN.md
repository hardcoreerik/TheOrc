# KV Cache and Context Design

## Purpose

Autoregressive decoding reuses prior key/value projections rather than recomputing the entire prefix for every token. The cache is numerically load-bearing mutable state, not merely a performance cache.

## First context contract

A context owns:

- one model reference;
- one sequence;
- maximum context length;
- current logical position;
- accepted token history for diagnostics;
- per-layer K and V storage;
- logits/output buffer;
- execution workspace;
- decoder state;
- cancellation and health state.

Batching, multiple sequences, forks, shifts, and eviction are non-goals initially.

## Semantic cache shape

For each layer and position, store keys and values for `n_kv_heads * head_dim`. Physical dimension order is backend-specific but the semantic accessors must make layer, position, KV head, and head element explicit.

Cache bytes can be estimated as:

```text
layers * context_positions * kv_heads * head_dim *
(key_element_bytes + value_element_bytes)
```

Real architectures may add recurrent state or different key/value dimensions. The formula is not universal.

## Lifecycle

```text
model load
  -> context create and bounded allocation
  -> prompt evaluation writes positions 0..N-1
  -> decode reads prefix and writes position N
  -> repeat until stop/context limit
  -> explicit reset or destroy
  -> model release only after all contexts end
```

Reset zeroes logical ownership; physical zeroing is a security/performance policy decision. Reusing stale positions must be impossible through bounds and state transitions.

## Invariants

- position equals the count of committed tokens;
- a failed operation never advances position unless cache/output commit completed;
- each layer commits the same position atomically from the context’s perspective;
- reads never include an uncommitted future position;
- context length is checked before execution;
- model generation/identity cannot change under a context;
- cache dtype/layout matches backend and model configuration;
- reset invalidates sampling history and pending decoded bytes.

## Transactional decode

Proposed safety approach:

1. validate capacity and inputs;
2. compute into workspace and candidate cache location;
3. on success, commit logical position/token;
4. on cancellation/error before commit, leave logical state unchanged or mark context poisoned if backend writes cannot be rolled back safely.

A poisoned context must be reset or destroyed, never silently continued.

Prompt evaluation obeys the same all-or-nothing rule as single-token decode. If a multi-token prompt fails after any layer or position may have written backend cache memory, the context is poisoned unless the implementation can prove complete rollback to the pre-call committed state. Merely restoring the logical position is insufficient. No subsequent read may observe a partially written prompt.

## Prompt versus incremental equivalence

The same token prefix evaluated in one prompt call or token-by-token must yield equivalent last-position states and logits within approved tolerances. This is a permanent regression test.

## Memory planning

Allocate from validated dimensions and maximum context. Report exact cache bytes. Later paged or growable caches require a measured need and new failure/lifetime tests.

## Context overflow

Initial behavior is fail-closed with a structured context-capacity error. Context shifting, sliding-window attention, RoPE scaling beyond the model contract, and automatic truncation are not allowed.

## Role-aware future

TheOrc could eventually associate a context with a semantic role. That metadata must not alter model math implicitly. Cross-role prefix or KV sharing is prohibited until model, prompt, adapter, position, and cache compatibility are proven.

The current LLamaSharp path already uses separate persistent role executors because adapter state is context-scoped. OrcEngine should preserve the safety lesson rather than promise universal sharing.

## Cache quantization

Out of scope until float cache correctness. Any future K/V quantization needs separate dtype per cache, dequantization/operator tests, quality measurements, and explicit compatibility with RoPE/application order.

## Required tests

- create/reset/destroy lifecycle;
- capacity 0/1/boundary/overflow;
- prompt versus incremental equivalence;
- repeated reset with different prompts;
- cancellation at layer boundaries;
- injected failure before and after candidate writes;
- stale-context rejection after model release attempt;
- exact allocation accounting;
- no cross-context state bleed.
