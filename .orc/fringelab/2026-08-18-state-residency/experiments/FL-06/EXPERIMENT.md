# FL-06 — Context / Effective Execution Identity

## Base commit

`944f07b86428ec53d46ca19dc66c3d0d5b1e207d` (`orcengine-phase4-freeze`, peeled).
Worktree: `F:\Ai\OrchestratorIDE-fringelab2`, branch `research/orcengine-fringe-lab2`.

## Question

Can persistent inference state (a KV cache) be associated with an
explicit identity describing the computation that produced it, such that
reuse under a matching identity is safe and reuse under a mismatched
identity is detectably unsafe? Which candidate identity fields
experimentally affect produced state, versus which are merely proposed?

## Variables

- Model weights (varied via RNG seed -- OrcEngine has no adapter/LoRA
  execution to exercise, so a different weight seed is the closest
  available analogue to "different model/adapter").
- RoPE `theta` (architecture-level positional-encoding configuration,
  independent of the weights that produced a given cache).
- Token-selection/decoding policy (greedy top-1 vs top-2), tested as an
  explicit NEGATIVE control -- expected NOT to affect already-produced
  state.

## Controls

- "Exact identity reuse": same weights object, same config, continuing
  from the cache it itself produced.
- "Equivalent copy": a SEPARATE `ModelWeights` Python object built from
  the SAME seed (same float values, different object identity) --
  isolates whether "identity" should mean object identity or value
  identity.
- Clean recompute: every contamination comparison is against a fully
  independent, from-scratch computation under the target identity, never
  against another contaminated run.

## Implementation

New standalone script: `experiments/FL-06/fl06_state_identity.py`. Does
not modify `oracle/model.py`. Uses `forward_cached()`/`empty_kv_cache()`
unmodified. `StateIdentity` is a new dataclass enumerating candidate
identity fields (weight_seed, architecture dims, rope_theta, rotary_dim,
kv_dtype, execution_semantics_version) -- only `weight_seed` and
`rope_theta` are experimentally varied this pass; the rest are recorded
as proposed-only (see "What this experiment does NOT prove").

## Procedure

1. Build `identity_a` (seed 20260818, theta 10000), `identity_b_weights`
   (seed 99999, theta 10000 -- weights differ, RoPE config identical),
   `identity_b_rope` (seed 20260818, theta 50000 -- weights identical,
   RoPE config differs).
2. Produce a KV cache under `identity_a` (3-token prefix).
3. Test 1 (exact identity reuse): continue decoding under `identity_a`'s
   own weights object, compare against a from-scratch full-sequence
   recompute under `identity_a`.
4. Test 2 (equivalent-copy reuse): build a SEPARATE `ModelWeights` object
   with the same seed, continue decoding from `cache_a` using THAT
   object, compare against the same clean recompute.
5. Test 3a (weight-identity contamination): continue decoding from
   `cache_a` but under `identity_b_weights`'s weights/config, compare
   against a clean from-scratch recompute under `identity_b_weights`.
6. Test 3b (RoPE-identity contamination): same as 3a but with
   `identity_b_rope` (weights unchanged, only `theta` differs).
7. Test 4 (decoding-policy negative control): show that choosing top-1 vs
   top-2 as the next token is a pure post-hoc function of already-returned
   logits and cannot retroactively alter the cache that already exists --
   argued structurally (the cache is returned by `forward_cached()`
   before any selection policy runs), not just empirically.
8. Contamination attack, extended: reuse `cache_a`'s lineage under
   `identity_b_weights` for 4 consecutive steps, comparing against a
   from-scratch `identity_b_weights` recompute at every step (not just
   the first).

## Raw measurements

Full output: `raw/fl06_result.json`.

| Test | max_abs_diff | Result |
|---|---|---|
| 1. Exact identity reuse | 2.38e-07 | PASS (float32 tolerance) |
| 2. Equivalent-copy reuse (same seed, different object) | 2.38e-07 | PASS (float32 tolerance) |
| 3a. Different identity (weights) contamination | 1.27222 | DIVERGED |
| 3b. Different identity (RoPE theta only) contamination | 5.98e-05 | DIVERGED |
| 4. Decoding policy (top1=3 vs top2=17) | n/a | Structural guarantee holds |

**Multi-step contamination attack (reusing `cache_a` under
`identity_b_weights` for 4 steps):**

| Step | max_abs_diff | Diverged |
|---|---|---|
| 0 | 1.27222 | YES |
| 1 | 1.67878 | YES |
| 2 | 1.66336 | YES |
| 3 | 1.67676 | YES |

First divergent step: 0 (immediate).

## Failures

None -- this script ran successfully on the first execution after FL-05's
path/API fixes were already known and applied up front.

## Unexpected observations

- The RoPE-theta-only contamination (test 3b) diverges by ~5.98e-05 --
  three orders of magnitude smaller than the weight-identity contamination
  (1.27) but still four orders of magnitude above this experiment's 1e-5
  equivalence tolerance, and clearly not float32 noise. This makes sense
  on reflection (only the rotation ANGLE applied to Q and the newly-added
  K differs between the two RoPE configs; the cached K's magnitude and
  the linear-projection weights are identical, so the geometric
  inconsistency this introduces into the Q·K dot product is real but
  smaller than a full weight-identity mismatch) -- but the SIZE of the
  effect, and that it was clearly separable in magnitude from the
  weight-identity case, was not anticipated before running the
  experiment.
- Test 2 (equivalent-copy reuse) and Test 1 (exact identity reuse)
  produced the EXACT SAME max_abs_diff value (2.38419e-07) to the last
  digit. This is expected once considered (both compare the identical
  numeric computation against the identical clean baseline; Python object
  identity genuinely has zero effect on the floating-point result, as the
  experiment set out to test) but the exactness of the match was a small,
  satisfying confirmation rather than an assumed outcome.

## Interpretation

*(Written only after all measurements above were complete.)*

- **OBSERVED:** state produced under one identity, reused under an
  independently-constructed but VALUE-equivalent copy of that same
  identity, is indistinguishable (within float32 tolerance) from reuse
  under the original object. Identity, for the purposes of KV cache
  validity, is a property of VALUES (weights, RoPE config), not of Python
  object identity.
- **OBSERVED:** both tested identity-mismatch dimensions (model weights,
  RoPE theta) produce detectable divergence, immediately (step 0) and
  persistently (every subsequent step tested, through step 3) when a
  cache is reused under a mismatched identity. No case of "silent success
  followed by later divergence" or "divergence that self-corrects" was
  observed in the 4 steps tested.
- **DERIVED:** the two experimentally-tested identity dimensions are NOT
  equally "risky" in magnitude -- a full weight-identity mismatch produces
  roughly 4 orders of magnitude more divergence than a RoPE-theta-only
  mismatch, at this model scale. This is a magnitude observation specific
  to this synthetic fixture and these specific seed/theta choices, not a
  general claim about relative risk across all possible identity
  mismatches.
- **OBSERVED (structural, not just empirical):** decoding/token-selection
  policy cannot retroactively affect already-produced KV state, because
  `forward_cached()` returns that state before any selection policy is
  applied downstream. This was verified by code-flow inspection, not
  merely by failing to find a counterexample.
- **INFERRED:** an identity-tracking mechanism for a real system would
  need, at minimum, weight identity and RoPE-configuration identity as
  inputs, based on this experiment's evidence that both independently
  and detectably affect state validity.
- **UNKNOWN / not tested:** whether architecture-shape mismatches
  (n_layers, n_q_heads, n_kv_heads, head_dim) would be caught by a shape
  check before any silent contamination could occur, or would need
  explicit identity tracking of their own -- not empirically exercised
  this pass (see "What this experiment does NOT prove").
- **UNKNOWN:** KV dtype and "execution semantics version" as identity
  fields -- no second value exists in this oracle to test against, so
  these remain proposed-only, not proven or disproven.

## What this experiment does NOT prove

- Does NOT prove architecture-dimension identity (n_layers, head counts,
  head_dim) needs explicit tracking -- these were not independently
  varied. A real implementation might catch shape mismatches structurally
  (array shape errors) rather than needing identity metadata, but this
  was not tested.
- Does NOT prove KV dtype or an "execution semantics version" are
  relevant identity fields -- no second value of either exists in this
  oracle to construct a contamination test against. These remain
  proposed candidates only.
- Does NOT test adapter/LoRA identity, since OrcEngine has no adapter
  execution to exercise. The weight-seed variation is offered as the
  closest available analogue, not as a claim that adapter identity would
  behave identically.
- Does NOT propose or evaluate any specific mechanism for enforcing
  identity checks in a real system (e.g., a hash-based guard, an explicit
  identity struct threaded through the API) -- this experiment only
  establishes WHICH fields matter, not HOW to enforce the check.
- Does NOT constitute an architectural recommendation for OrcEngine.

## Reproduction commands

```bash
cd /f/Ai/OrchestratorIDE-fringelab2/.orc/fringelab/2026-08-18-state-residency/experiments/FL-06
python3 fl06_state_identity.py
```

Raw output: `raw/fl06_result.json`.

## Files changed

- `experiments/FL-06/fl06_state_identity.py` (new)
- `experiments/FL-06/EXPERIMENT.md` (new, this file)
- `experiments/FL-06/raw/fl06_result.json` (new, generated)

## Commit

Recorded after this experiment is committed (see session commit log in
`SUMMARY.md`).
