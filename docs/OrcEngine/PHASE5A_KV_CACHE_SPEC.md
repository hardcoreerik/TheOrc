# Phase 5A: KV-Cached Incremental Decode Reference

Status: **IMPLEMENTED, SYNTHETIC GATE PASSING -- REAL-MODEL VALIDATION DEFERRED, AWAITING INDEPENDENT REVIEW**

Branch: `feat/orcengine-phase5a-kv-cache`, worktree
`F:\Ai\OrchestratorIDE-phase5a-kv-cache`, forked from `orcengine-phase4-freeze`.

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

## Results (2026-08-18, first implementation pass)

**Architecture:** `ContiguousAttentionKVStore` (Phase 1's previously-unused
contract type, `Tools/OrcEnginePhase1/include/orcengine/context.hpp`) gained
explicit bounds-checked `write_k`/`write_v`/`k_row`/`v_row` accessors and an
explicit `current_length()` counter -- reads/writes are checked against
capacity (`max_positions`), not against `current_length()`, so a single
`forward_cached_step` call can legitimately read positions it is writing in
that same call; `current_length()` is instead the driver-maintained
accounting boundary a fault-injection test can deliberately violate. A new
`Tools/OrcEnginePhase5A/` project adds `forward_cached_step()`
(`include/orcengine/forward_cached.hpp`, `src/forward_cached.cpp`), mirroring
`oracle/model.py`'s already-proven `forward_cached()` step-for-step: RoPE at
absolute cache position, GQA head mapping unchanged, a rectangular causal
mask (query position `p` attends to keys `[0, p]`) implemented inline. Phase
1's `ops::*` functions and `AccumT` accumulator are reused directly, not
duplicated.

**Correctness (synthetic Fixture C, prefill + 8 incremental decode steps):**
every one of the 9 steps produced **bit-exact** logits versus the
independently-computed Python trace (`max_abs_diff=0.000000` at every step,
not merely within tolerance), exact greedy token selection at every step,
and per-layer KV-cache content verified directly (not inferred from logits)
within a 1e-5 tolerance (text-round-trip noise from the `.9g` fixture
format, ~1e-8 observed -- not an algorithmic difference; see the code
comment at the comparison site). Sequence: `[1,5,5,5,29,29,29,29,29,29,29]`.

**Fault injection -- all six required cases independently shown to diverge**
from the correct reference: wrong cache position, stale/unwritten KV reuse,
swapped K/V at one cache slot, corrupted shared KV head (GQA-mapping
exercise, confirmed `group_size() = 2 > 1` for this fixture), rectangular
causal-mask boundary consistency, and cache non-leakage between two
independent sequences (a fresh, never-prefilled cache produces different
output than a correctly-prefilled one). Two additional failure-semantics
checks (decode past `max_positions`, empty `new_token_ids`) both fail
closed as required.

**One real test-harness bug found and fixed during this pass** (not an
engine defect): the differential harness initially double-counted each
decode step's input token in its running sequence tracker, because a
decode step's `new_tokens` field is, by the Python export's own
construction, already reflected in the *next* step's `seq_before` (it was
appended as the *previous* step's `selected` value) -- appending it again
after processing caused a spurious "sequence diverged" failure from step 2
onward. Caught by the harness's own divergence check working exactly as
designed; fixed by only appending `new_tokens` for the `prefill` step
(where `seq_before` is deliberately empty) and always appending `selected`.

**Validation matrix:** Debug 8/8, Release 8/8 (7 inherited Phase-1 tests +
`cached_decode`), strict `/W4 /WX /permissive-` 8/8 (zero warnings), MSVC
ASan 8/8 (no memory-safety findings across the new raw-pointer cache
accessors and manual offset arithmetic).

**Explicitly deferred, not yet done:** real SmolLM2-135M cached-decode
validation (synthetic Fixture C only, per this gate's own scope); Phase 5B
(tokenizer) and 5C (workspace/benchmarking) remain unspecified; memory
accounting is implemented (KV-cache bytes derivable from
`current_length * n_layers * n_kv_heads * head_dim * 4 * 2`) but not yet
reported alongside weight/activation/process numbers in one consolidated
report; no independent (non-self-authored) review has occurred yet, per
this document's own "Independent-review requirement" below.

## Results (2026-08-18, real-model composition-audit pass)

This pass answered the question the synthetic gate could not: does Phase
5A's KV-cached decode remain correct against the real, pinned
`HuggingFaceTB/SmolLM2-135M` model, and does it compose with Phase 3/4's
streaming/row-region virtualization architecture? Evidence labels follow
this project's standing convention (VERIFIED = independently reproduced;
MEASURED = a real number from a real run, not derived; DECIDED = a
recorded choice with rationale; HYPOTHESIS = not yet tested; UNKNOWN =
open; REJECTED-SUPERSEDED = an earlier claim this pass overturned).

**REJECTED-SUPERSEDED — Phase-4 composition.** Direct code inspection
(`grep -rn "ModelSource\|StreamingModel\|TensorRowRegion\|BackingExtent\|materialize" Tools/OrcEnginePhase5A/`
→ zero matches) confirms Phase 5A as implemented does **not** compose with
Phase 3/4's virtualization architecture: it requires a fully-resident
`Model` (full embedding, full output head, all transformer layers resident
simultaneously), bypassing `ModelSource`, `TensorRowRegionMaterializer`,
and the Phase-3 layer-at-a-time lifecycle entirely. It reuses Phase 1's
math correctly, but gives back exactly the bounded weight-residency
property Phase 3/4 spent two phases proving. This is not a defect in
Phase 5A's own correctness claim -- the cached decode math is right -- but
it means Phase 5A cannot yet be described as "Phase-4-compatible."
Composing the two is explicitly out of this phase's scope and is the most
likely candidate for a Phase 5D (or similar) follow-up, not a silent
assumption to carry forward.

**VERIFIED — real-model 4-way differential.** OrcEngine full-prefix vs
OrcEngine Phase-5A cached vs HF/PyTorch full-prefix (`use_cache=False`)
vs HF/PyTorch's own independently-constructed native cached decode
(`use_cache=True`, never fed by OrcEngine's cache) all agree on the
established sequence `[1,5,28,284,260,198]`. `cpp_full_vs_hf_full` max_abs
at step 0 = `0.00104618`, matching the historically-recorded
`0.00104618073` from Phase 2/3/4's own prior evidence almost to the last
digit -- an independent consistency check, not just a fresh pass/fail.
`cpp_full` and `cpp_cached` are bit-identical every step
(`max_abs_diff=0.000000`), and `hf_cached_vs_hf_full` also passes (max_abs
0, 4.2e-5, 4.4e-5, 3.8e-5). Driver: `tools/gguf_cached_forward.cpp`.
Differential: `tests/real_hf_cached_differential.py`.

**VERIFIED — non-vacuity of the bit-exact result.** The
`cpp_full_vs_hf_full` divergence (0.00104618, nonzero) proves the C++ path
is not silently echoing an expected value -- if it were comparing against
itself or a cached expectation, `cpp_full` would also read exactly zero
against HF, not a specific nonzero float that matches historical evidence.
Combined with `test_real_cache_attacks.cpp`'s fault-injection suite (below,
11/11 pass), which independently confirms wrong-position, corrupted-KV,
RoPE-mis-position, and cross-context inputs all produce *detectable*
divergence rather than silently passing, the bit-exact `cpp_full ==
cpp_cached` result is not exact because the harness can't tell wrong from
right -- it's exact because both paths are computing the same real math
correctly.

**VERIFIED — real-model fault attacks (11/11 pass,
`tests/test_real_cache_attacks.cpp`).** Run against the real model's
actual `n_q_heads=9, n_kv_heads=3, group_size=3` ratio (not the synthetic
4Q/2KV fixture): wrong cache position (+1) diverges; corrupted kv_head 0
(shared by 3 real query heads via the 9Q/3KV ratio) diverges; RoPE
position deltas of -1, +1, and a reset-to-0 all diverge (proving the
divergence is driven by the position argument itself, not incidental
input drift); decode at exactly `max_positions` (8,192) fails closed while
`max_positions-1` succeeds (proves the capacity boundary is exact, without
requiring a full 8,192-token inference run); a fresh never-prefilled
context diverges from a correctly-prefilled one (no cross-context
leakage).

**MEASURED — real-model timing (`gguf_cached_forward` JSON output,
single run, not yet a repeated campaign).** Model load: 5212.994 ms (a
later re-run measured 5664.335 ms -- both single samples, not averaged).
Full-prefix recompute: ~8s/step (32,069.783 ms / 4 steps). Phase-5A
prefill: 152.012 ms (148.518 ms on the memory-instrumented re-run).
Phase-5A incremental decode steps: 77.855, 75.380, 75.391 ms each (74.881,
74.824, 75.056 ms on the re-run) -- roughly 100x faster per generated
token via caching. **Prefill and decode are reported separately per the
spec's own requirement**, not averaged into one number. This measures
*wall-clock*, not backing-weight-bytes-read-per-token -- the cached path
may be reducing attention computation while still rereading every
transformer weight each step (Phase 5A's fully-resident architecture makes
every weight byte available every step regardless of cache use), and that
distinction remains UNKNOWN/unmeasured this pass, deferred to whatever
follow-up composes Phase 5A with Phase 3/4's virtualization.

**MEASURED — KV memory accounting, derived independently, not trusted
from any prompt.** `bytes/token = n_layers * n_kv_heads * head_dim * 2 *
sizeof(float) = 30 * 3 * 64 * 2 * 4 = 46,080 bytes` (confirmed against the
real GGUF's own `llama.block_count=30`,
`llama.attention.head_count_kv=3`, `head_dim=576/9=64`). At
`max_positions=8192`: `kv_reserved_bytes = 377,487,360` (≈360 MiB).
Process working-set samples from `gguf_cached_forward` (Windows
`GetProcessMemoryInfo`, one sample point each, not a full profiling
campaign): before load 4,800,512 bytes; after load 665,370,624 bytes
(weight-load delta ≈630.0 MiB, a process-level proxy, not a
residency-ledger number -- Phase 5A doesn't use Phase 3's `ResidencyLedger`
architecture, per the composition-audit finding above); before KV-cache
allocation 668,241,920 bytes (already includes the 4-step full-prefix
loop's activation memory, since that ran first); **after KV-cache
allocation 1,045,741,568 bytes -- a jump of ≈377.5 MiB, matching the
derived `kv_reserved_bytes` almost exactly.** This is empirical, measured
confirmation (not just code inspection) that `ContiguousAttentionKVStore`
eagerly allocates its full `max_positions` capacity at construction,
regardless of how many positions are ever actually committed (in this
run, only 5: `kv_committed_bytes = 230,400`). After the full decode loop:
1,046,253,568 bytes -- negligible further growth (≈500 KiB), consistent
with activation/logits workspace being small relative to weight+KV.
Instrumentation: `Psapi`-linked `sample_process_working_set_bytes()` in
`tools/gguf_cached_forward.cpp`.

**DECIDED — residency crossover is architecture-dependent, not a single
number.** Two honest answers exist depending which residency mode is
being asked about, and conflating them was a real risk this pass avoided:
(1) against Phase 4's *streaming/virtualized* single-layer weight peak
(≈14.16 MB for this tiny model) -- the earlier hypothetical estimate of
≈308 committed tokens still holds, but describes an architecture Phase 5A
does not currently use; (2) against Phase 5A's *own actual, measured*
fully-resident weight footprint (≈630.0 MiB) -- the crossover would
require ≈14,335 committed tokens, which **exceeds `max_positions=8192`
entirely**, meaning under Phase 5A's current architecture, KV memory never
dominates total memory within any valid context length. This is recorded
as a planning observation specific to this model/configuration, per the
spec's own instruction, not promoted to an architectural constant.

**VERIFIED — weight-residency-regression check.** No permanent
embedding/output-head-only residency, no multiple simultaneous resident
layers beyond what full materialization always implies, and no
NEW full-model-residency behavior was introduced by this pass's KV-cache
work -- the fully-resident load path is exactly Phase 2's pre-existing
`materialize_gguf_model`, unchanged. (This is a statement that Phase 5A's
*KV-cache* additions didn't make residency worse, not a claim that Phase
5A matches Phase 4's virtualized residency -- it doesn't, per the
composition-audit finding above.)

**VERIFIED — transactional failure semantics
(`tests/test_transactional_semantics.cpp`, 6/6 pass).** Chosen semantics
(deliberately the smallest correct policy, not a rollback framework):
`current_length()` is caller-driven and is never auto-advanced by
`forward_cached_step`, including on failure. A mid-step failure (proven
here by corrupting layer 1's `w_v` weights with NaN mid-decode, on the
synthetic Fixture-C model) leaves the cache genuinely **poisoned in
place** at the positions that step was attempting to write (layer 1's
cache slot is directly read back and confirmed to contain NaN after the
throw) -- this is not a rollback. What makes it safe: `current_length()`
is confirmed unchanged after the failure (the failed step was never
committed), and a retry at the *same* `start_position` with correct
weights restored overwrites the poisoned slot before anything reads it
(writes precede reads for a given layer within one `forward_cached_step`
call) and reproduces the untouched baseline's logits bit-exactly. The
contract: any cache position `>= current_length()` is, by convention, not
part of committed history and must never be read as context by a
correctly written caller; only a subsequent write at that same position
(a retry) makes it trustworthy again.

**VERIFIED — full validation matrix on the current HEAD (all 13 tests:
8 inherited from `cached_decode`, `real_cache_attacks`,
`transactional_semantics`, plus 4 inherited Phase-1/2 conformance
tests).** Debug 13/13. Release 13/13. Strict `/W4 /WX /permissive- /EHsc`
13/13, **zero warnings** (the one MSVC C4530 warning encountered
mid-session was in frozen Phase-1 code Phase 5A links against unmodified,
and was resolved by restoring the default `/EHsc` flag CMake's raw
`CMAKE_CXX_FLAGS` override had inadvertently dropped -- not by weakening
any check). MSVC ASan 13/13, **zero memory-safety findings** -- notable
specifically because the new code (`ContiguousAttentionKVStore`'s
`write_k`/`write_v`/`k_row`/`v_row`, and `gguf_cached_forward.cpp`'s
`--dump-cache` raw-pointer reads) does manual offset arithmetic Phase 1
never needed.

**Phase-5A freeze-candidate gate checklist (real-model pass):**

| # | Item | Status |
|---|---|---|
| 1 | Synthetic bit-exact differential (Fixture C) | VERIFIED (prior pass) |
| 2 | Real-model 4-way differential (HF full/cached, OrcEngine full/cached) | VERIFIED |
| 3 | Bit-exact result proven non-vacuous | VERIFIED |
| 4 | Real GQA attack (actual 9Q/3KV ratio) | VERIFIED |
| 5 | Real RoPE position attacks (p-1, p+1, reset-to-0) | VERIFIED |
| 6 | Real capacity boundary (exact 8,192 fail-closed) | VERIFIED |
| 7 | Real cross-context isolation | VERIFIED |
| 8 | KV bytes/token derived independently | VERIFIED (matches suggested value) |
| 9 | Eager KV allocation confirmed empirically (not just by code read) | VERIFIED |
| 10 | Residency crossover computed, correctly scoped to architecture | DECIDED |
| 11 | Weight-residency non-regression | VERIFIED |
| 12 | Transactional (poisoned-in-place) failure semantics | VERIFIED |
| 13 | Prefill vs decode timing reported separately | MEASURED |
| 14 | Weight-read-bytes-per-token (transformer/embedding/head, separated) | UNKNOWN -- deferred |
| 15 | Phase-4/5A composition | REJECTED-SUPERSEDED -- does not currently compose |
| 16 | Debug/Release/strict/ASan validation matrix | VERIFIED, 13/13 all four lanes |
| 17 | Documentation reconciled (this section) | VERIFIED |
| 18 | Independent (non-self-authored) review | NOT DONE -- required before freeze |
| 19 | `orcengine-phase5a-freeze` tag / branch push | NOT DONE -- not authorized this pass |

**Proposed verdict: `NOT READY — BLOCKERS REMAIN`.** The KV-cache
correctness claim itself (items 1-13) is now real-model verified and
strong. The blocker is item 15: Phase 5A does not currently compose with
Phase 3/4's virtualization architecture, so it cannot yet be described as
preserving OrcEngine's bounded-residency property -- only its correctness
property. Item 14 (weight-read-bytes-per-token) is the specific
measurement that would make item 15's practical impact legible rather
than just structurally true. Item 18 (independent review) has not
happened. None of these are reasons to distrust the KV-cache math itself;
they are reasons the *composed system* is not yet ready for a freeze
decision.

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
