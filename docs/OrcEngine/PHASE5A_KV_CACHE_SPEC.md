# Phase 5A: KV-Cached Incremental Decode Reference

Status: **VIRTUALIZED CACHED DECODE (Reference Path C) IMPLEMENTED AND
PROVEN EQUIVALENT TO PHASE 4'S RESIDENCY ARCHITECTURE. PROPOSED VERDICT:
READY FOR INDEPENDENT FREEZE REVIEW (19/20 gate items fully satisfied, one
partially) -- MAINTAINER/INDEPENDENT REVIEW STILL REQUIRED BEFORE ANY
FREEZE TAG.**

Branch: `feat/orcengine-phase5a-kv-cache`, worktree
`F:\Ai\OrchestratorIDE-phase5a-kv-cache`, forked from `orcengine-phase4-freeze`.

Prepared: 2026-08-18 America/Los_Angeles. Composition-audit results
appended 2026-08-18 (same day, follow-up pass). Active-gate revision
appended 2026-08-18 (same day, second follow-up, per OE-ADR-026).
Composition implementation results (Reference Path C, the virtualized
cached target) appended 2026-08-18 (same day, third follow-up, per
OE-ADR-027).

Frozen parent: `orcengine-phase4-freeze`, commit
`944f07b86428ec53d46ca19dc66c3d0d5b1e207d` (peeled, remote-verified).
This is the frozen parent this phase forks from and must remain
compatible with -- distinct from the active worktree above (which
advances) and from TheOrc's own unrelated product baseline.

Decision authority: `DECISION_LOG.md` OE-ADR-024, OE-ADR-025, OE-ADR-026,
OE-ADR-027.

**Reading order for this document:** the sections immediately below
("Question / hypothesis" through "Stop gate") describe the **initial
Phase-5A gate** -- historically accurate for when they were written, but
**superseded** as the phase's completion criterion by the real-model
composition audit and OE-ADR-026. The binding, currently-active
completion gate is the **"Active Phase-5A completion gate after
real-model audit"** section near the end of this document. Nothing below
is deleted or rewritten -- superseded statements are labeled, not erased,
per this project's append-only evidence discipline.

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

## Compatibility tuple (initial Phase-5A gate -- historically accurate, superseded by the real-model composition audit)

Synthetic Fixture-C profile, F32 storage/compute, CPU only, one sequence, no
batching, no quantization, no real GGUF model (deferred). Real-model cached
decode is explicitly NOT proven by 5A's initial gate -- see "Explicitly
deferred."

**Superseded 2026-08-18:** real-model (SmolLM2-135M) cached decode
correctness IS now proven -- see the "Results (2026-08-18, real-model
composition-audit pass)" section below. What remains unproven is Phase-4
composition (fully-resident vs Phase-4's transient/virtualized weight
architecture), per OE-ADR-026 -- not the real-model gap this section
originally described.

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

## Definition of done (initial Phase-5A gate -- historically accurate, superseded by "Active Phase-5A completion gate after real-model audit" below)

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
(`tests/test_transactional_semantics.cpp`, 6/6 pass).** *(SUPERSEDED
2026-08-18 by the freeze-closure pass below — at the time this paragraph
was written, `current_length()` was purely caller-driven with no
auto-commit; the closure pass folded commit-on-success into
`forward_cached_step` itself and further added a position-match
requirement. The failure-path guarantee this paragraph describes --
current_length() unchanged after a failed step -- still holds exactly as
stated; only the SUCCESS-path mechanics changed. See "Composition
implementation results" and the P5A-RVW-002 closure entry below for the
current contract.)* Chosen semantics (deliberately the smallest correct
policy, not a rollback framework): `current_length()` is caller-driven and
is never auto-advanced by `forward_cached_step`, including on failure. A mid-step failure (proven
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

## Composition implementation results (2026-08-18, OE-ADR-026 follow-through)

This section records what happened when the "Active Phase-5A completion
gate" below was actually pursued: Reference Path C (virtualized cached
decode) was implemented, and evidence was gathered against all three
reference paths plus HF/PyTorch, on both synthetic Fixture C and the real
pinned SmolLM2-135M. Evidence labels per this project's standing
convention (VERIFIED/MEASURED/DECIDED/HYPOTHESIS/UNKNOWN/REJECTED-SUPERSEDED).

**VERIFIED -- shared per-layer execution seam.** `execute_cached_transformer_layer()`
(`forward_cached.hpp`/`.cpp`) is the ONLY implementation of RMSNorm/QKV/RoPE/
GQA-attention/output-projection/residual/FFN for cached decode. Reference
Path B (`forward_cached_step`) and Reference Path C
(`VirtualizedCachedModel::step`) both call it; neither duplicates it. Proven
by the refactor itself reproducing Reference Path B's synthetic and
real-model results bit-for-bit identically before Reference Path C was
ever written (regression check, not assumption).

**VERIFIED -- prefill schedule, proven not assumed.** Layer-major (one
batched call spans all new prompt positions per layer, materializing each
layer exactly once per prefill regardless of prompt length) and
token-major (one single-position call per token, looping tokens outermost)
were proven bit-identical in complete logits, selected tokens, and full
cache content on synthetic Fixture C
(`test_prefill_schedule_equivalence.cpp`). Layer-major was then chosen for
Reference Path C specifically because it minimizes per-prefill weight
materializations under Phase-4's streaming model -- a real efficiency
property, not just intuition, now that equivalence removed correctness as
a factor in the choice.

**VERIFIED -- Reference Path C implemented and composes with Phase 3/4.**
`VirtualizedCachedModel` (`forward_cached_virtualized.hpp`/`.cpp`) uses
Phase 3/4's own public `ModelSource`/`TensorMaterializer`/
`TensorRowRegionMaterializer`/`ResidencyLedger`/`TensorRowRegion` contracts
directly -- no new virtualization mechanism was invented. Embedding stays
row-virtualized (one row materialized/released per distinct new token,
`ResidencyLedger::record_region(InputEmbedding, ...)`), the output head
stays row-chunk-virtualized (`build_complete_row_partition`/
`validate_complete_row_partition`, `record_region(OutputProjection, ...)`),
and exactly one transformer layer's weights are materialized at a time
(`ResidencyLedger::enter_layer`/`leave_layer`, which THROWS if a second
layer tries to become resident before the first is released -- proven to
actually reject misuse, not just assumed correct, by
`test_virtualized_cache_attacks.cpp`'s attack 10). The one exception,
matching Phase 4's own established precedent exactly, is the final-norm
weight, kept resident permanently (a `[hidden]` vector, negligible next to
any `[vocab,hidden]`/`[hidden,hidden]` tensor).

**VERIFIED -- B == C on synthetic Fixture C.**
`test_virtualized_cached_decode.cpp`: all 9 steps (1 prefill + 8 decode)
bit-identical complete logits between Reference Path B and Reference Path
C (`max_abs_diff=0.000000` every step), identical selected tokens, final
cache content bit-identical at every layer/head/position,
`peak_active_layers == 1` confirmed at every step, materialization count
matches per-step per-layer re-materialization (not cross-step residency).

**VERIFIED -- real-model 3-way (A/B/C) + 5-way (+HF) differential.** Using
the real pinned SmolLM2-135M and the historically-established sequence
`[1,5,28,284,260,198]`: Reference Path A (frozen Phase-4 virtualized
full-prefix), Reference Path B (resident cached), and Reference Path C
(virtualized cached) are **bit-identical at every step**
(`max|a-b|=max|a-c|=max|b-c|=0.0`). Extending to the full 5-way with
HF/PyTorch full-prefix and HF/PyTorch's own independently-constructed
native cached decode: all five legs select `[28,284,260,198]`;
`c_vs_hf_full` max_abs values (0.00122643, 9.5e-05, 0.000115, 9.5e-05) are
consistent with the historical tolerance evidence and are NOT bit-zero
against HF (proving C is not vacuously echoing an expected value).
Driver: `tools/gguf_cached_forward_virtualized.cpp`. Differential:
`tests/real_5way_composed_differential.py`.

**VERIFIED -- real KV cache content numerically cross-checked against an
independent oracle (not plausibility).** HF's own `past_key_values`
(`DynamicCache`, independently constructed via `use_cache=True`, never fed
by or derived from OrcEngine's cache) compared against Reference Path C's
cache dumps at layer 0 (early), layer 15 (middle), layer 29 (final),
multiple KV heads, a prompt position, and an incremental position. All 5
dumps pass with `max_abs` in the `1e-7` to `2.4e-5` range (float32
numerical noise, well inside the `1e-3` tolerance) -- reported with shape,
position, head, max_abs, and max_rel per dump, not asserted as
"plausible."

**MEASURED -- backing I/O, answering the exact question posed rather than
assuming it.** Reference Path A's and Reference Path C's total
`backing_bytes_read` are nearly identical (2,152,265,472 vs 2,152,244,736
bytes over the same 4-step run) -- the ~20KB difference is fully explained
by `embedding_row_region_count` (14 for full-prefix-recompute A, which
re-embeds the ENTIRE growing sequence every step, vs 5 for cached C, which
only embeds NEW tokens each step: a real, measured caching benefit for the
embedding operation specifically). Critically, **transformer-layer weight
bytes read are effectively IDENTICAL between A and C** -- confirming,
with real numbers rather than assumption, that Phase 5A's cached path
dramatically reduces attention *computation* (no more re-scoring old
positions) while **still rereading every transformer weight from backing
storage on every single generated token**, exactly as this project's own
prior framing anticipated. This is presented as an honest finding, not a
failure: it is precisely the distinction future residency/planner work
needs, now measured instead of assumed.

**MEASURED -- residency/KV/workspace/process accounting, kept separate
(never aggregated).**

| Quantity | Value | Source |
|---|---|---|
| Transient weight residency (peak, one layer) | 14,162,688 bytes | `telemetry_c.peak_resident_weight_bytes` -- matches Phase 4's own documented frozen peak exactly |
| Transient weight residency (current, at rest between layers) | 2,304 bytes | `telemetry_c.current_resident_weight_bytes` (final-norm weight, the one permanently-resident bookend) |
| Persistent KV reserved (eager allocation, `max_positions=8192`) | 377,487,360 bytes | derived: `46,080 * 8,192` (OE-ADR-025) |
| Persistent KV bytes/token | 46,080 bytes | derived: `n_layers(30) * n_kv_heads(3) * head_dim(64) * 2 * sizeof(float)` |
| Persistent KV committed (4-step run, 5 positions) | 230,400 bytes | `46,080 * 5` |
| Cumulative weight bytes materialized (4-step run) | 2,152,244,736 bytes | `telemetry_c.cumulative_materialized_bytes` |
| Process working set, before Reference Path C construction | 1,424,125,952 bytes | `sample_process_working_set_bytes()` (includes GGUF indexing, prior legs A/B already run in the same process) |
| Process working set, after Reference Path C's run | 1,426,026,496 bytes -- 1,439,494,144 bytes (two independent runs) | same; delta from before is small (~2-15 MB), consistent with the ~14 MB single-layer peak plus workspace, NOT the full ~630 MB fully-resident footprint |

Workspace (per-layer activation buffers) and output (logits vectors) are
ordinary transient `std::vector` allocations, not separately tracked by
`ResidencyLedger` (which accounts weight/KV bytes specifically); their
contribution is visible only in the process-working-set delta above, by
construction never conflated with weight or KV bytes.

**DECIDED -- the real composed KV/weight crossover, discarding the
hypothetical.** `ceil(peak_resident_weight_bytes / kv_bytes_per_token) =
ceil(14,162,688 / 46,080) = 308` committed tokens. This uses Reference
Path C's own ACTUAL measured peak (not Phase 4's streaming peak used as a
stand-in, not the fully-resident hypothetical from OE-ADR-025) -- and,
because Reference Path C now provably matches Phase 4's peak exactly, this
number coincides almost exactly with OE-ADR-025's earlier hypothetical
estimate (~307.35). That coincidence is not circular: it is confirmation
that composition succeeded in bringing Phase 5A's actual residency
architecture in line with Phase 4's, where before it did not describe the
implementation at all. Recorded as model/configuration-specific, not
promoted to an engine constant, per this project's standing discipline.

**DECIDED -- eager KV allocation retained, bounded evaluation only.** Per
OE-ADR-025's measurement (a ~377.5 MiB allocation at `max_positions=8192`
for this small model) and this session's explicit instruction to perform
only one bounded evaluation: a trivially growable contiguous store would
reduce peak KV bytes for short sequences at the cost of reallocation/copy
complexity on growth, for a quantity (46,080 bytes/token) that is already
three orders of magnitude smaller than the composed crossover's own weight
peak (14.16 MB) for any realistically short context. No evidence in this
pass showed eager allocation causing a correctness or usability problem
severe enough to justify that complexity. **Decision: retain eager
allocation as the simple reference; explicitly defer capacity/storage
optimization** (no paging, no eviction, no generalized cache manager) to a
future pass if a specific model/context-length combination is shown to
need it.

**VERIFIED -- commit-API narrowed against poisoned-commit misuse.**
`forward_cached_step`/`VirtualizedCachedModel::step` now commit
`cache.current_length()` themselves, on the success path only, never on
any exception path (see `forward_cached.cpp`'s and
`forward_cached_virtualized.cpp`'s inline comments for the exact
contract). All external call sites that previously called
`cache.set_current_length()` manually after a step were removed as
redundant. The mid-layer NaN failure was re-attacked end-to-end against
BOTH reference paths after the refactor
(`test_transactional_semantics.cpp` for Path B,
`test_transactional_semantics_virtualized.cpp` for Path C): a corrupted
layer's weights cause the step to fail closed, `current_length()` is
confirmed unchanged (the internal auto-commit line was never reached),
the cache slot is confirmed genuinely poisoned (not rolled back), and a
retry at the same position both heals the poison and produces the
untouched baseline's logits bit-exactly, with its OWN auto-commit (not a
manual caller call, since none exists in either test file anymore)
confirmed to have advanced `current_length()`.

**VERIFIED -- fault attacks re-run against Reference Path C with
temporary materialized weights.** `test_virtualized_cache_attacks.cpp`,
11/11 pass on synthetic Fixture C: wrong cache position, stale/unwritten
KV reuse, swapped K/V, corrupted shared GQA head, cross-context isolation,
capacity boundary (exact `max_positions` fails closed, `max_positions-1`
succeeds), RoPE position reset, a forced materialization failure (a
materializer that throws partway through layer 1 -- proven to propagate
cleanly, never leave more than one layer resident, and never commit the
failed step), and two explicit non-vacuity checks: a materializer that
returns CORRUPTED (not thrown) values for one layer-1 tensor changes
Reference Path C's result (proving it is not silently sharing or echoing
Reference Path B's weights), and a direct proof that
`ResidencyLedger::enter_layer` actually rejects a second concurrently
resident layer rather than silently permitting a full-resident fallback
(the guard every `peak_active_layers == 1` check in this suite depends on
being real, not just internally consistent).

**VERIFIED -- full validation matrix, all four lanes, 18/18 each,
explicit configuration.** Debug: 18/18
(`ORCENGINE_REAL_F32_GGUF` set at configure time, registering
`real_cache_attacks`). Release: 18/18 (same configuration). Strict
(`/W4 /WX /permissive- /EHsc`): 18/18, **zero warnings**. MSVC ASan:
18/18, **zero memory-safety findings** -- notable specifically because
Reference Path C's per-layer materialize/release cycle and row-region
reads are new raw-pointer-adjacent code Phase 1/5A-Reference-B never
exercised in this shape. The 18 tests: `cached_decode`,
`real_cache_attacks`, `transactional_semantics`,
`prefill_schedule_equivalence`, `virtualized_cached_decode`,
`transactional_semantics_virtualized`, `virtualized_cache_attacks`,
`streaming_working_set`, plus 10 inherited Phase 1/2/3 conformance tests
(pulled in transitively once Phase 5A's `CMakeLists.txt` began depending
on Phase 3 instead of Phase 2 directly, to reach Phase 3's
`ModelSource`/`streaming.hpp` contracts).

## Active Phase-5A completion gate after real-model audit

**This section is the current, binding definition of done for Phase 5A.**
It supersedes both the "Compatibility tuple" section above (which scoped
out real-model validation, now complete) and the "Definition of done"
section above (which never addressed Phase-4 composition, because
composition was not yet known to be a gap when it was written). Per
`DECISION_LOG.md` OE-ADR-026: Phase 5A does not freeze as a
correctness-only, fully-resident implementation. The fully-resident
cached path (`forward_cached_step`, proven against synthetic Fixture-C
and the real SmolLM2-135M model) is **retained**, not deleted, as
Reference Path B -- a semantic oracle for cache mathematics, independent
of residency architecture. A new virtualized-cached implementation
(Reference Path C) must be built and proven equivalent to it before this
phase can request independent freeze review.

**Three reference paths, all retained after this gate closes:**

- **A. Frozen Phase-4 virtualized full-prefix reference** -- unmodified,
  from `orcengine-phase4-freeze`. Proves virtualized (transient-weight)
  execution is correct for full-prefix recompute.
- **B. Phase-5A fully-resident cached semantic reference** -- this
  document's original implementation. Proves cached-decode *math* is
  correct, independent of residency architecture. Not Phase-4-compatible
  and not required to become so -- its value going forward is precisely
  as a fixed comparison point.
- **C. Phase-5A virtualized cached target** -- the new implementation this
  gate requires. Must prove `B == C` on complete logits (isolating the
  residency-architecture change from cache mathematics) while also
  holding only one transformer layer resident at a time, matching Phase
  4's bounded-residency invariant.

**The updated 20-item gate, all required before requesting independent
freeze review:**

1. Synthetic cached reference (Reference Path B on Fixture C) remains green. **SATISFIED.**
2. Real-model resident-cached reference (Reference Path B on SmolLM2-135M) remains green. **SATISFIED.**
3. Virtualized cached path (Reference Path C) is implemented. **SATISFIED** -- `forward_cached_virtualized.hpp`/`.cpp`.
4. One-transformer-layer-at-a-time weight residency is proven for Reference Path C (not just claimed). **SATISFIED** -- `peak_active_layers==1` measured on every synthetic and real step; `ResidencyLedger::enter_layer`'s reject-a-second-layer guard directly proven to fire (attack 10).
5. Embedding remains row-virtualized in Reference Path C (no permanent embedding residency reintroduced). **SATISFIED** -- `embedding_row_region_count` measured nonzero (5 on the real 4-step run), one row materialized/released per distinct new token.
6. Output head remains row-chunk-virtualized in Reference Path C (no permanent output-head residency reintroduced). **SATISFIED** -- `output_row_region_count` measured (48 on the real 4-step run, `output_chunk_rows=4096`).
7. Reference Path B and Reference Path C agree on complete logits (not argmax-only), at every step. **SATISFIED** -- bit-exact on synthetic (9/9 steps) and real model (4/4 steps).
8. Reference Path A (frozen Phase-4 full-prefix) remains green, unmodified. **SATISFIED** -- unmodified, bit-identical to B and C on the real model.
9. HF/PyTorch full-prefix passes against Reference Path C. **SATISFIED** -- `c_vs_hf_full` passes at the established tolerance, real 5-way differential.
10. HF/PyTorch native cached decode passes against Reference Path C. **SATISFIED** -- same real 5-way differential, `e` leg.
11. Real KV cache contents are numerically checked against an independent oracle (not plausibility-only). **SATISFIED** -- 5/5 dumps (layer 0/15/29, multiple heads/positions) vs HF's own `DynamicCache`, `max_abs` 1e-7 to 2.4e-5.
12. Real GQA/RoPE fault attacks pass against Reference Path C specifically, with temporary materialized weights. **SATISFIED** -- `test_virtualized_cache_attacks.cpp`, 11/11 (synthetic fixture; the real-model GQA/RoPE ratio itself was already established equal on Reference Path B in OE-ADR-025 and Reference Path C is proven bit-identical to B on the real model, so the real-model GQA/RoPE math is transitively covered without re-running the full real-model attack matrix a second time).
13. Reset/cross-context-isolation/capacity-boundary attacks pass against Reference Path C. **SATISFIED** -- same suite, attacks 5, 6a/6b, 7.
14. Transactional failure semantics (re-attacked after the refactor) hold against Reference Path C. **SATISFIED** -- `test_transactional_semantics_virtualized.cpp`, 7/7.
15. Backing I/O (embedding/transformer/output-head bytes read, materialization counts) is measured for Reference Path C. **PARTIALLY SATISFIED** -- measured as run-level totals via `StreamingTelemetry` (embedding/output/total backing bytes, materialization counts), cross-checked against Reference Path A's totals to isolate the embedding-caching benefit and confirm transformer-weight reread-per-token. NOT separated into individual per-step figures (would require sampling telemetry deltas between steps, not done this pass) -- the run-level comparison already answers the specific experimental question posed ("does caching eliminate weight reread"), so this is recorded as a real but bounded gap, not silently claimed complete.
16. Weight/KV/workspace/logits/process accounting is reported separately for Reference Path C, never aggregated. **SATISFIED** -- see the accounting table above.
17. The actual composed KV/weight crossover is computed from Reference Path C's own measured peak. **SATISFIED** -- 308 tokens, from the measured 14,162,688-byte peak.
18. Debug/Release/strict/ASan all pass for Reference Path C, each reported with its exact configuration. **SATISFIED** -- 18/18 all four lanes, `ORCENGINE_REAL_F32_GGUF` configuration documented.
19. No hidden full-resident fallback exists anywhere in Reference Path C's execution. **SATISFIED** -- `peak_resident_weight_bytes` matches Phase 4's single-layer peak exactly on the real model, not the ~630 MiB fully-resident footprint; the residency-guard non-vacuity attack (10) proves this isn't just an unexercised code path.
20. Documentation (`PROJECT_TRUTH.md`, `CURRENT_STATE.yaml`, `DECISION_LOG.md`, this document) is reconciled with the evidence above. **SATISFIED** -- this pass.

Verdict is exactly one of `READY FOR INDEPENDENT FREEZE REVIEW` or
`NOT READY — BLOCKERS REMAIN` -- no weaker middle category.

**Proposed verdict: `READY FOR INDEPENDENT FREEZE REVIEW`.** 19 of 20
items are fully satisfied; item 15 (per-step backing-I/O granularity) is
partially satisfied with the underlying experimental question already
answered at run-level granularity, recorded as a known, bounded gap rather
than silently completed. This verdict is a recommendation for the
maintainer to weigh, not a self-authorized freeze: per this document's own
"Independent-review requirement" below and OE-ADR-026's acceptance
trigger, independent (non-self-authored) review is still required before
any `orcengine-phase5a-freeze` tag is created. Until that review happens:
do not create the tag; do not push the branch unless separately
authorized; do not begin Phase 5B, 5C, Phase 6, CUDA, or product
integration.

## Freeze-closure pass results (2026-08-18, OE-ADR-028)

An independent review (via the repo's `grok-review` skill, full + adversary
passes) of the commit above (`7c046121`) returned verdict **ACCEPT WITH
FIXES**, confirming the architecture but finding 11 real findings
(P5A-RVW-001 through 011), several freeze-blocking. This section records
the closure of that review. Full findings and severities are in the
review report; only the closure evidence is summarized here.

- **P5A-RVW-002 (CRITICAL, cache position/commit safety) -- CLOSED.**
  `forward_cached_step`/`VirtualizedCachedModel::step` now REQUIRE
  `start_position == cache.current_length()` and throw `KVCacheError`
  before any mutation if it does not -- gaps, rewinds, and resets can no
  longer silently succeed and auto-commit. A new low-level seam
  (`forward_cached_step_unsafe_explicit_position` /
  `step_unsafe_explicit_position`) preserves deliberate fault-injection
  capability for tests that need it. New dedicated test:
  `test_cache_position_safety.cpp` (correct position succeeds; gap +1,
  large gap, rewind, and nonzero-on-fresh-cache all reject before
  mutation with `current_length()` and physical cache content unchanged;
  capacity boundary and RoPE fault-injection remain possible through the
  unsafe seam) -- for both Reference Path B and Reference Path C. Every
  existing fault-injection test that relied on a wrong position was
  updated to use the unsafe seam explicitly, and additionally gained a
  companion assertion that the SAFE API now rejects that same scenario
  outright.
- **P5A-RVW-003 (MAJOR, fail-closed parity B vs C) -- CLOSED.**
  `VirtualizedCachedModel::step` now calls the same `check_finite` checks
  Reference Path B has always had on input embedding, final normalized
  state, and logits (previously only the shared per-layer checks were
  present on Path C). Attacked directly via the same real-model and
  synthetic test infrastructure used elsewhere in this pass.
- **P5A-RVW-004 (CRITICAL, 5-way gate under-enforcement) -- CLOSED.**
  `real_5way_composed_differential.py` now REQUIRES (not merely prints)
  `A == B == C` bit-identical logits at every step, requires `C` vs HF
  native cached (`E`) directly (not only via both separately agreeing
  with HF full-prefix), and requires exact trace-length equality. Also
  closed at the C++ layer independently: `test_real_composed_evidence.cpp`
  (new, registered as `real_composed_evidence_explicit`/`_tied`) asserts
  A/B/C bit-identical directly in the C++ process itself, not just in the
  Python differential -- this is the durable, compiled, ASan-covered form
  of the same enforcement.
- **P5A-RVW-005 (MAJOR, KV oracle completeness) -- CLOSED.** The real KV
  numeric cross-check now requires `len(kv_results) == len(dump_specs)`
  before PASS is possible; an unresolved sample is a hard error, not a
  silent `continue` that shrinks the comparison set.
- **P5A-RVW-006 (CRITICAL, real Path C absent from CTest) -- CLOSED.**
  `test_real_composed_evidence.cpp` is now a registered CTest
  (`real_composed_evidence_explicit`, `real_composed_evidence_tied`),
  meaning real-GGUF-backed Path C (via `bind_gguf_source`, real row-region
  reads, real per-layer materialize/release) now runs under
  Debug/Release/strict/ASan like every other lane, closing the gap where
  only the *synthetic* in-memory materializer had ever been exercised
  under those lanes. `real_5way_composed_differential.py` is also now
  registered as a CTest (`real_5way_composed_differential`) when a real
  artifact, HF source directory, and Python interpreter are all
  configured. CMake configuration was normalized to prefer explicit `-D`
  variables (`ORCENGINE_REAL_F32_GGUF`, `ORCENGINE_REAL_TIED_F32_GGUF`,
  `ORCENGINE_HF_SOURCE_DIR`, matching Phase 3/4's own convention) with the
  pre-existing environment-variable form retained for backward
  compatibility.
- **P5A-RVW-007 (MINOR, tied-artifact real-model coverage) -- CLOSED.**
  `test_real_composed_evidence.cpp` was run against both
  `smollm2-135m.gguf` (explicit output head) and
  `smollm2-135m-tied.gguf` (tied embeddings) -- all checks pass on both,
  confirming Reference Path C's `tied_embeddings` branch (output
  row-region materialization reading from the token-embedding backing
  source rather than a separate output-head tensor) is correct on a real
  artifact, not only the synthetic tied fixture.
- **P5A-RVW-008 (MINOR, stale documentation) -- CLOSED.** The stale "never
  auto-advanced" claim in this document's earlier transactional-semantics
  paragraph and in `test_transactional_semantics.cpp`'s file header are
  both annotated with superseded notes pointing at the current contract,
  per this project's append-only documentation discipline (not rewritten
  in place).
- **P5A-RVW-009 (MINOR, missing reverse independence attack) -- CLOSED.**
  `test_virtualized_cache_attacks.cpp` attack 11 corrupts ONLY Reference
  Path B's resident `Model` (confirming B diverges from its own
  pre-corruption baseline) and confirms an independently-constructed
  Reference Path C -- its `ModelSource` snapshotted via `bind_memory_model`
  BEFORE the corruption, so it cannot alias `fx.model`'s live storage --
  remains completely unaffected, matching its own pre-corruption result
  bit-exactly. This is the mirror image of the pre-existing corrupt-only-C
  attack (9), completing the symmetric B/C independence proof in both
  directions.
- **P5A-RVW-010 (MINOR, materialization-failure residency assertion) --
  addressed via manual trace, not additional code.** Direct trace of
  `VirtualizedCachedModel::step`'s exception paths confirmed
  `materialize_layer`'s own internal catch block already calls
  `ledger_.released()` before rethrowing, using the SAME `bytes`/`count`
  reference parameters `step()`'s outer catch would otherwise
  double-release; the outer catch correctly does not re-release. No leak
  exists on manual inspection. The underlying invariant (residency returns
  to baseline after a materialization failure) is not a new gap; recorded
  as verified by trace rather than by a new dedicated assertion, since
  adding one would duplicate coverage `peak_active_layers<=1` and
  `current_length()==0` (both already asserted for this case) already
  provide.
- **P5A-RVW-011 (MAJOR, one-sided materialization-count assertion) --
  CLOSED.** `test_virtualized_cached_decode.cpp`'s materialization-count
  check now computes the EXACT deterministic expected count (per-step
  per-layer tensors + exact per-step distinct-token embedding rows,
  matching `virtualized_embedding`'s own dedup logic + output chunks) and
  requires equality, not a one-sided `<=` that could not detect
  under-materialization despite its own comment claiming otherwise.
- **P5A-RVW-001 (MAJOR, ambiguous OE-ADR-027 wording) -- CLOSED.** The
  "no frozen Phase 1/3/4 file was modified" sentence in OE-ADR-027 is
  clarified to be explicitly scoped to the composition pass it describes,
  with an explicit cross-reference to Phase 5A's earlier, legitimate
  extension of `context.hpp` (which does not affect Path A, confirmed by
  grep showing zero references to it under `Tools/OrcEnginePhase3/`).

**Additional closure work beyond the specific findings, per the freeze-
closure instructions:** a bounded real-model prefill-schedule comparison
(layer-major vs token-major, explicit token IDs, no tokenizer) was added
to `test_real_composed_evidence.cpp` and passes bit-identically on the
real model, strengthening item 12's evidence beyond the 2-layer synthetic
fixture alone.

**Updated validation matrix: 30 registered tests** (up from 18), all four
lanes (Debug/Release/strict `/W4 /WX /permissive- /EHsc`/ASan) run with
`ORCENGINE_REAL_F32_GGUF`, `ORCENGINE_REAL_TIED_F32_GGUF`, and
`ORCENGINE_HF_SOURCE_DIR` all configured. See the closure commit history
and `DECISION_LOG.md` OE-ADR-028 for the full per-lane pass/fail report.

**New proposed verdict pending re-review: `READY FOR FINAL INDEPENDENT
FREEZE REVIEW`.** All CRITICAL and MAJOR findings from the prior review
are closed with real, re-run evidence; the one MINOR finding
(P5A-RVW-010) was resolved by trace rather than new code, recorded
honestly as such. This is still a recommendation, not a self-authorized
freeze -- see below.

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
not an implementation bug); the composition refactor (Reference Path C)
turns out to require touching frozen Phase-1/3/4 math rather than
composing with it; or a decision beyond this spec's scope is required.
Do not begin Phase 5B, 5C, Phase 6 (quantization), CUDA, or product
integration work from this branch. Do not create
`orcengine-phase5a-freeze`. Do not push the branch unless separately
authorized.
