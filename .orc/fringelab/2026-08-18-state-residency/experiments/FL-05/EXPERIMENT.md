# FL-05 — Shared Prefix State

## Base commit

`944f07b86428ec53d46ca19dc66c3d0d5b1e207d` (`orcengine-phase4-freeze`, peeled).
Worktree: `F:\Ai\OrchestratorIDE-fringelab2`, branch `research/orcengine-fringe-lab2`.

## Question

Can multiple independent inference contexts (branches) reference a
physically shared, immutable prefix KV state without changing inference
results, compared against fully independent per-branch KV? What does that
cost or save in physical memory, and is isolation between branches
actually enforced or merely assumed?

## Variables

- Branch count (1, 2, 4, 8, 16 in the scaling sweep; 3 named branches A/B/C
  in the main equivalence/isolation/fault-attack pass).
- Shared prefix length (fixed at 3 tokens for the main pass).
- Per-branch max suffix buffer size (fixed at 4 tokens).
- Model: synthetic Fixture-C-shaped config (`ModelConfig()` defaults:
  n_layers=2, n_q_heads=4, n_kv_heads=2, head_dim=4, vocab=32, hidden=16),
  weights seeded deterministically (`seed=20260818`).

## Controls

- CONTROL path: `oracle.model.forward_cached()`, UNMODIFIED, called
  independently per branch (each branch recomputes its own prefix from
  scratch via a fresh call with `kv_cache=None`) -- no object-identity
  sharing at all. This is the "Reference A/B/C = full independent KV"
  baseline the session charter specifies.
- Same seed, same weights, same token sequence used for both control and
  experimental paths.

## Implementation

New standalone script:
`experiments/FL-05/fl05_shared_prefix.py`. Does NOT modify
`Tools/OrcEnginePhase0/oracle/model.py`. Reuses `oracle.ops` primitives
(rmsnorm, linear_no_bias, apply_rope, rope_cos_sin, softmax_last_axis,
causal_mask_rectangular, embedding_lookup, silu) directly -- all already
independently proven/trusted by Phase 0's own microcases and by every
later phase's differential harness.

Mechanism: `SharedPrefixBuffer` is a preallocated `[n_layers][n_kv_heads]
[prefix_len][head_dim]` numpy array, written ONCE (by copying the result
of one `forward_cached()` prefix call), then read by reference (numpy
view, never `.copy()`'d) from every branch's attention computation.
`BranchSuffixBuffer` is a preallocated private buffer per branch. Attention
for a branch computes scores against the shared prefix and against its own
committed suffix via two SEPARATE matmuls, concatenating only the
resulting `[new_len, key_count]` SCORE matrices before softmax -- the K/V
arrays themselves are never copied or merged.

## Procedure

1. Build weights once (seed 20260818).
2. CONTROL: for each of 3 branches (A/B/C), independently call
   `forward_cached()` for the 3-token prefix, then again for the branch's
   own next token, using a freshly-computed prefix cache each time.
3. EXPERIMENTAL: build ONE `SharedPrefixBuffer` (one `forward_cached()`
   call, one copy into the shared buffer). For each of the 3 branches,
   allocate a private `BranchSuffixBuffer` and run
   `cached_step_shared_prefix()` for the branch's own next token.
4. Compare CONTROL vs EXPERIMENTAL: complete logits (not argmax-only) and
   selected token, per branch.
5. Mutation/isolation: extend branch A one more step; verify branch B's
   and C's own committed suffix bytes are byte-identical before/after.
   Wrap `shared` + each branch's suffix in an explicit `BranchContext`
   object (so reference counting is actually meaningful -- see "Failures"
   below for why the raw buffers alone don't exercise this), delete A's
   context, and record `sys.getrefcount(shared)` before/after. Reset
   branch C's suffix to empty and confirm its next step matches an
   entirely fresh branch computing the same token at the same position.
6. Fault attacks: (a) corrupt one row of the shared prefix, run the SAME
   token at the SAME position on a snapshotted-and-rewound branch state
   both with and without the corruption, compare; (b) lie about a
   branch's committed suffix length by +1 (reading an unwritten,
   zero-initialized row) and compare against the correct-length result.
7. Scaling sweep: repeat the shared-buffer construction for branch counts
   1, 2, 4, 8, 16, recording physical bytes for both designs.

## Raw measurements

Full machine-readable output: `raw/fl05_result.json`. Console log:
`fl05_run.log` (not written this pass -- output captured directly in this
report and the JSON; see "Reproduction commands").

**Equivalence (complete logits, tolerance 1e-5 -- see "Unexpected
observations" for why not exact equality):**

| Branch | Control selected | Experimental selected | max_abs_diff | Pass (tol 1e-5) |
|---|---|---|---|---|
| A | 7 | 7 | 8.94e-08 | YES |
| B | 3 | 3 | 1.49e-07 | YES |
| C | 3 | 3 | 1.19e-07 | YES |

**Isolation:**

- Extend A, then B's committed suffix bytes unchanged: YES (byte-identical)
- Extend A, then C's committed suffix bytes unchanged: YES (byte-identical)
- `sys.getrefcount(shared)` before deleting A's `BranchContext`: 4; after: 3
  (decreased by exactly 1, consistent with real reference-counted sharing)
- B computes successfully against the shared prefix after A's destruction
  (no exception; shared buffer remains valid)
- C, reset to empty suffix, matches a freshly-constructed branch computing
  the identical token at the identical position: max_abs_diff = 0.0 (exact)

**Fault attacks:**

| Attack | max_abs_diff | Diverges (> 1e-5)? |
|---|---|---|
| Corrupt one shared-prefix K row (same token/position, controlled) | 0.2525 | YES |
| Lie about committed suffix length by +1 (read unwritten zero row) | 0.0736 | YES |

**Memory, main pass (3 branches, prefix_len=3, max_suffix=4):**

- Shared-design physical bytes: 1920
- Independent-design physical bytes (actual, from control's real allocations): 1536

**Memory, scaling sweep:**

| n_branches | Shared-design physical bytes | Independent-design physical bytes (extrapolated from control) |
|---|---|---|
| 1 | 896 | 512 |
| 2 | 1408 | 1024 |
| 4 | 2432 | 2048 |
| 8 | 4480 | 4096 |
| 16 | 8576 | 8192 |

## Failures

- First run crashed: `sys.path` insertion used the wrong number of
  `.parents[]` levels (fixed: needed `parents[5]`, not `[4]`, to reach the
  repo root from the experiment file's nested path).
- First run crashed: `weights.effective_lm_head` is a method, not a
  property (fixed: added `()`).
- First working run: equivalence check used `tol=0.0` (exact equality)
  and reported `pass=False` for all three branches at ~1e-7 max_abs_diff.
  This was a test-harness defect (tolerance too strict for a genuine
  floating-point non-associativity difference between two mathematically
  equivalent but differently-ordered matmul reductions), not evidence of
  an actual bug in the shared-prefix mechanism -- fixed by adopting a
  1e-5 tolerance matching this project's established `kCacheTol`
  precedent (Phase 5A's own cache-content comparisons use the same
  reasoning for the same reason).
- First working run's refcount check was measuring the wrong thing:
  `cached_step_shared_prefix()` takes `shared` as a bare per-call function
  argument, so a `BranchSuffixBuffer` alone never holds a Python reference
  to `shared` -- `sys.getrefcount(shared)` was constant (1->1) regardless
  of branch destruction, which is not evidence of anything. Fixed by
  introducing an explicit `BranchContext` wrapper (holding both `shared`
  and the branch's own suffix) specifically to make the refcount
  observable, modeling what a real caller holding contexts (not raw
  buffers) would see.

## Unexpected observations

- The shared-prefix design's complete logits are NOT bit-identical to the
  independent-KV control, despite being mathematically equivalent
  formulations of the same attention computation. The difference
  (~1e-7, i.e. a few ULPs of float32) comes specifically from computing
  attention scores via two separate matmuls (`q @ shared_k.T` and
  `q @ branch_k.T`) and concatenating the resulting score matrices, versus
  the control's single matmul over an already-concatenated K array.
  Float32 matrix multiplication is not associative across different
  reduction groupings, so this is expected once identified, but was not
  anticipated before running the experiment.
- At this experiment's scale (prefix_len=3, max_suffix=4), the
  shared-design consistently used MORE physical memory than the
  independent-design at every branch count tested (1 through 16) --
  the opposite of the naive assumption that sharing a prefix must save
  memory. This is because the shared design's per-branch suffix buffers
  are eagerly preallocated to `max_suffix=4` regardless of how many
  positions are actually committed (only 1, in every test here), while
  the independent design's `np.concatenate`-based growth allocates
  exactly what is used, no more. See "What this experiment does NOT
  prove" below.

## Interpretation

*(Written only after all measurements above were complete, per this
session's methodology.)*

- **OBSERVED:** a shared, reference-held, never-copied prefix buffer
  produces logits equivalent (within float32 non-associativity tolerance)
  to fully independent per-branch computation, across 3 branches and a
  1-16 branch scaling sweep.
- **OBSERVED:** branch isolation holds under direct measurement -- one
  branch's suffix writes never touch another branch's suffix bytes or the
  shared buffer; reference counting behaves as expected when a branch
  context is destroyed; a reset branch reproduces a fresh branch's
  computation exactly.
- **OBSERVED:** both tested fault classes (corrupting the shared prefix;
  lying about a branch's committed length) produce large, easily
  detectable divergence (0.07-0.25) -- there is no evidence of a "silent
  corruption" failure mode in this design at this scale.
- **OBSERVED, and contrary to intuition:** at this experiment's specific
  buffer-sizing choices (tiny prefix, small preallocated per-branch
  suffix), the shared-prefix design used MORE physical memory than
  growing independent buffers, at every branch count tested.
- **DERIVED:** the memory crossover point (where sharing a prefix
  actually saves bytes) depends on `prefix_len` being large relative to
  each branch's suffix over-allocation, and/or on the number of sharing
  branches being large enough that the shared bytes amortize across them.
  Neither condition was true in this experiment's parameter choices.
- **HYPOTHESIS, not tested here:** a real model's prefix (hundreds to
  thousands of tokens, tens of megabytes of KV per Phase 5A's own
  measurements) combined with many concurrent branches would likely cross
  into genuine memory savings -- this experiment's tiny synthetic fixture
  was not sized to test that regime, and no claim is made about it beyond
  a hypothesis worth a follow-up experiment.
- **UNKNOWN:** performance/timing implications of the two-matmul-then-
  concatenate-scores attention pattern versus a single matmul over
  concatenated K -- not measured in this pass (this experiment's charter
  treats timing as secondary unless the experiment specifically studies
  it, which FL-05 does not).

## What this experiment does NOT prove

- Does NOT prove that shared-prefix state would save memory on a real
  model or realistic branch counts -- the measured result at this
  experiment's scale was the opposite (more memory, not less), due to
  buffer-sizing choices specific to this synthetic setup, not to the
  sharing mechanism itself.
- Does NOT prove bit-exact equivalence to independent computation --
  only equivalence within float32 tolerance, with a specific, understood
  cause for the (small) numeric difference.
- Does NOT test copy-on-write in the literal sense (no implementation in
  this experiment ever copies the shared buffer on write -- writes are
  structurally impossible against it, since branches only ever write to
  their own private suffix buffers). A COW-specific experiment, if
  desired, would need a design where shared segments CAN be written and
  the copy boundary is deliberately triggered.
- Does NOT test real-model scale, real GGUF-backed weights, or the actual
  Phase 5A `ContiguousAttentionKVStore` type (this is a new, standalone
  mechanism built for this experiment specifically, not integrated with
  or a replacement for anything in the Phase-5A candidate).
- Does NOT constitute an architectural recommendation for OrcEngine.

## Reproduction commands

```bash
cd /f/Ai/OrchestratorIDE-fringelab2/.orc/fringelab/2026-08-18-state-residency/experiments/FL-05
python3 fl05_shared_prefix.py
```

Raw output: `raw/fl05_result.json`.

## Files changed

- `experiments/FL-05/fl05_shared_prefix.py` (new)
- `experiments/FL-05/EXPERIMENT.md` (new, this file)
- `experiments/FL-05/raw/fl05_result.json` (new, generated)

## Commit

Recorded after this experiment is committed (see session commit log in
`SUMMARY.md`).
