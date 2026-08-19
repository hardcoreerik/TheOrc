# Phase 5A: KV-Cached Incremental Decode Reference

Status: **SPECIFIED, IMPLEMENTATION IN PROGRESS**

Prepared: 2026-08-18 America/Los_Angeles

Frozen parent: `orcengine-phase4-freeze`, commit
`944f07b86428ec53d46ca19dc66c3d0d5b1e207d` (peeled, remote-verified).

Decision authority: `DECISION_LOG.md` OE-ADR-024.

## Question / hypothesis

Does one-token-at-a-time cached decode -- reusing already-computed K/V for
every prior position and computing only the new position's Q/K/V -- produce
**exactly** the same next-token logits as full-prefix recompute, at every
step, across enough incremental steps to expose positional, cache-indexing,
and GQA-mapping bugs that a single-shot comparison cannot?

Full-recompute (Phase 1-4's only execution path so far) and cached decode
must not be treated as equivalent merely because they emit the same greedy
token -- a wrong cache offset or a swapped K/V slot can still select the
correct argmax by coincidence on a short sequence. This phase's job is to
make that coincidence detectable.

## Frozen dependencies (inherited unchanged, not re-derived)

- Phase 1's operators (`rmsnorm`, `linear_no_bias`, `apply_rope`,
  `softmax_last_axis`, `causal_mask`, SwiGLU) and `forward_impl`'s block
  order -- unchanged.
- Phase 1's F32 accumulation default (`ops::AccumT`) -- unchanged.
- Phase 3/4's `TensorRowRegion`/format-neutral materialization contract --
  unchanged, not exercised by this phase's synthetic-fixture scope (real-GGUF
  cached decode is explicitly deferred; see "Explicitly deferred" below).
- `ContiguousAttentionKVStore` (`Tools/OrcEnginePhase1/include/orcengine/
  context.hpp`) -- already exists as a contract type from Phase 1, unused by
  any test until now. This phase is its first real exercise.
- The synthetic Fixture-C model (`vocab=32, hidden=16, intermediate=32,
  n_layers=2, n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16`) --
  reused, not reinvented, matching every prior phase's precedent.

## Compatibility tuple (this phase only proves)

Synthetic Fixture-C profile, F32 storage/compute, CPU only, one sequence, no
batching, no quantization, no real GGUF model (deferred). Real-model cached
decode is explicitly NOT proven by 5A's initial gate -- see "Explicitly
deferred."

## Implementation scope

1. A cached-decode variant of the forward pass:
   - `prefill(tokens[0..k))` -> populates the KV cache for positions
     `0..k-1` and returns logits at the last prefilled position, using the
     SAME per-layer weight materialization and the SAME operator sequence
     `forward_impl` already uses (no duplicated transformer math -- extend
     the shared implementation, do not copy it, matching Phase 3's own
     "one shared `forward_impl`, mechanically routed" precedent).
   - `decode_step(new_token)` -> computes Q/K/V for exactly one new
     position, writes K/V into the cache at that position, computes
     attention against the FULL cache (all prior positions + the new one),
     and returns logits for that one new position.
2. Correct RoPE at the cache's actual absolute position (not always
   position 0), correct GQA key/value head mapping unchanged from full
   recompute, and a rectangular causal mask (query position `p` may attend
   to key positions `0..p`, matching Phase 0's already-implemented
   `causal_mask_rectangular` semantics -- see `PHASE_0_REFERENCE_ORACLE.md`'s
   cache equivalence test and `oracle/ops.py`).
3. Cache reset/restart: a fresh `ContiguousAttentionKVStore` per sequence,
   with explicit ownership (no accidental sharing between two decode runs).

## Explicit non-goals (this phase does not do)

- No real GGUF model cached-decode validation yet -- synthetic Fixture-C
  only for the initial freeze gate. A real-model follow-up (mirroring Phase
  3/4's "synthetic proof first, then real artifact") is recorded as a
  deferred next step, not silently promised as already covered.
- No tokenizer (Phase 5B), no bounded activation workspace design (Phase
  5C, though this phase's cache allocation is itself explicit and
  accounted -- see "Memory model" below), no benchmarking beyond basic
  correctness-adjacent timing.
- No multi-sequence/batched cache, no paging, no eviction policy.
- No BLAS/SIMD/threading.
- No CUDA, no quantized execution, no SafeTensors, no arbitrary tensor
  regions, no MoE, no speculative decoding, no multi-context scheduler, no
  TheOrc/HIVE/C-ABI integration.

## Correctness oracle

Three-way, matching the project's established independence-class discipline:

1. **Primary:** Python oracle's own `forward_cached`/prefill+decode path
   (`Tools/OrcEnginePhase0/oracle/model.py`), which Phase 0 already proved
   equivalent to full-prefix recompute (`PHASE_0_ACCEPTANCE.yaml`'s
   `cache_equivalence` check, `max_abs_diff=2.384e-07`). This is the
   trusted reference this phase's C++ path is compared against -- new
   fixtures exported from it, same discipline as Phase 1's
   `export_cpp_phase1_fixture.py`.
2. **Secondary:** the frozen Phase-1 full-prefix `forward()` on the same
   growing token sequence -- prefill(k) + decode_step(k+1) must match
   full-prefix `forward(tokens[0..k+1))`'s last-position logits exactly
   (bit-identical or within the established float32-machine-epsilon
   tolerance, matching Phase 1's own `1e-3` acceptance gate).
3. **Structural:** cache-content assertions (K/V at position `p` after
   `decode_step` matches what a from-scratch full computation at position
   `p` would produce), not just logit agreement -- a logit match alone
   cannot distinguish "correct cache" from "wrong cache that happens to
   still select the right argmax."

## Memory model (accounted separately, not collapsed into one number)

- **Durable model-weight residency:** unchanged from Phase 1/3/4 --
  `ResidentView` for weights, released per Phase 3's layer lifecycle when
  streaming is in play (not exercised by the synthetic-only initial gate,
  since Fixture-C's weights are trivially small and loaded once).
- **KV-cache residency:** `ContiguousAttentionKVStore`'s own accounting --
  `n_layers * n_kv_heads * max_positions * head_dim * 2 (K and V) * 4 bytes`,
  reported explicitly, grown/tracked separately from weight residency.
  Never conflated with weight bytes in any report this phase produces.
- **Activation/workspace residency:** the existing per-call temporaries in
  `forward_impl` (unchanged, not yet a bounded arena -- that's Phase 5C).
- **Logits/output buffers:** `[seq, vocab]` per call, reported separately.
- **Process working set:** sampled the same way Phase 3/4 sampled it, kept
  as a corroborating measurement, never substituted for engine-owned
  accounting.

## Prompt vs. decode semantics (kept distinct, not conflated)

- **Prefill:** processes the initial prompt tokens in one call, populates
  the cache for all of them, returns the last position's logits.
- **First-token generation:** the argmax selection immediately after
  prefill -- uses prefill's own last-position logits, no separate call.
- **Incremental decode:** each subsequent `decode_step` call, one new
  token at a time, cache-extended.
- **Steady-state decode:** repeated incremental steps -- this phase's
  fixtures explicitly run enough steps (8, matching Phase 1's own
  autoregressive-decode precedent) to exercise this regime, not just one
  step.

## Failure semantics (fail closed)

- Decoding past `max_positions` (cache capacity) rejects explicitly, does
  not silently wrap or overwrite.
- A `decode_step` call before any `prefill` (empty cache) rejects.
- Mismatched config between cache and model (e.g. a cache sized for a
  different `n_layers`/`n_kv_heads`/`head_dim`) rejects at construction,
  matching `ContiguousAttentionKVStore`'s existing `checked_size` pattern.
- Non-finite values in any cached-decode tap fail closed, matching Phase
  1's existing `diagnostics.hpp` NaN/Inf policy (extended to the cached
  path, not bypassed).

## Fault-injection plan (the harness must prove it can catch each of these)

1. Wrong cache position (off-by-one write/read).
2. Stale KV reuse (reading a position that was never actually written this
   sequence).
3. Swapped K/V (writing V's values into K's slot or vice versa).
4. Incorrect GQA head indexing (query head mapped to the wrong KV head
   during cached attention).
5. Missing causal boundary (a decode step allowed to attend to a position
   that should be in its future -- not applicable to strictly-append-only
   decode, but tested via a deliberately malformed cache write to prove the
   rectangular mask logic itself is exercised, not just structurally
   assumed correct).
6. Incorrect reset (a second sequence's `decode_step` silently reusing the
   first sequence's cache contents instead of a fresh store).

Each fault is injected via a deliberately corrupted intermediate value or
cache write, and the differential harness must show a divergence at or
before the expected checkpoint -- not merely "the final token happened to
differ." Matching Phase 0's own fault-injection discipline
(`PHASE_0_REFERENCE_ORACLE.md`'s "Fault-injection proof" section): a
harness that cannot detect these is not accepted regardless of how clean
its happy-path result looks.

## Test matrix

| Evidence | Scope |
|---|---|
| Prefill vs full-recompute, single call | synthetic, exact/tolerance-matched |
| 8-step incremental decode vs full-recompute at each step | synthetic, exact/tolerance-matched, matches Phase 1's `test_decode.cpp` step count precedent |
| Cache content assertions (not just logits) | synthetic |
| Cache reset / fresh-store-per-sequence | synthetic |
| Capacity/bounds rejection | synthetic |
| Six fault-injection cases above | synthetic |
| Debug / Release / strict `/W4 /WX /permissive-` / ASan | all of the above |
| Real-model (SmolLM2-135M) cached decode | **deferred**, recorded as the next step after this gate closes, not claimed here |

## Definition of done (5A's own freeze gate)

1. Frozen Phase-1/3/4 suites remain green, unmodified tolerances.
2. Prefill + 8-step incremental decode on synthetic Fixture-C match
   full-prefix recompute at every step (logits, not argmax-only).
3. Cache content itself (not just downstream logits) is asserted correct.
4. All six fault-injection cases are independently shown to be detected,
   each localized to a plausible checkpoint.
5. Capacity/reset/empty-cache failure modes fail closed with clear errors.
6. Memory accounting is reported with weight/KV-cache/activation/logits/
   process-working-set kept separate, never collapsed into one number.
7. Debug, Release, strict, and ASan lanes each have an explicit,
   individually-reported result -- no single blended pass/fail count.
8. Documentation (`PROJECT_TRUTH.md`, `CURRENT_STATE.yaml`,
   `DECISION_LOG.md`) updated with the evidence, in the project's
   established VERIFIED/MEASURED/HYPOTHESIS labeling style.
9. Self-review completed; real-model cached-decode validation explicitly
   scoped as the next step, not silently implied as already done.

## Independent-review requirement

Per this project's established precedent (Phase 2/3/4 all required
independent review before freeze), Phase 5A requires the same before any
`orcengine-phase5a-freeze`-style tag is created. This document does not
authorize self-tagging on completion.

## Stop gate

Implementation proceeds autonomously through the scope above per the
maintainer's standing authorization for this phase. Stop for maintainer
input only if: a fault-injection case cannot be made to fail as designed
without touching frozen Phase-1 math (a genuine architecture contradiction,
not an implementation bug); or the synthetic-to-real-model gap turns out to
require a decision beyond this spec's scope. Do not begin Phase 5B, 5C, or
Phase 6 (quantization) work from this branch.
