# OrcEngine Fringe Lab — session 2026-08-18 — state/residency wave

## Repository identity

- Worktree: `F:\Ai\OrchestratorIDE-fringelab2`
- Branch: `research/orcengine-fringe-lab2`
- Base: `orcengine-phase4-freeze` @ `944f07b86428ec53d46ca19dc66c3d0d5b1e207d`
- Final HEAD this session: `a6eb521b` (`research(fringelab): add FL-07
  budgeted weight residency experiment`)

## Base authority

The pre-existing worktree `F:\Ai\OrchestratorIDE-fringelab` (branch
`research/orcengine-fringe-lab`) was found to predate Phase 1-4 entirely
(only `Tools/OrcEnginePhase0` present) and was rejected as a base per
this session's own instructions ("verify its current state... if
necessary, create a new clean worktree instead"). A new worktree was
created from `orcengine-phase4-freeze` instead, giving FL-05/06/07
access to Phase 1/3/4's C++ residency/KV/streaming infrastructure that
FL-06 and FL-07 both directly reuse.

## Experiments completed

### FL-05 — Shared Prefix State

- **Status:** COMPLETE, committed at `582637c7`.
- **Result:** a physically shared, reference-held, never-copied prefix
  buffer produces logits equivalent (within float32 non-associativity
  tolerance, ~1e-7) to fully independent per-branch KV, across 3 named
  branches and a 1-16 branch scaling sweep. Isolation holds under direct
  measurement (byte-identical suffix buffers across branches after
  mutation; reference counting behaves correctly on branch destruction).
  Both tested fault classes (corrupt shared prefix; lie about committed
  suffix length) produce large, easily detectable divergence — no silent
  corruption observed.
- **Strongest evidence:** the isolation and fault-attack results are the
  strongest part of this experiment — they are structural/mechanical
  checks (byte comparison, refcount inspection, corrupted-input
  divergence) with no room for a tolerance-driven false pass.
- **Important limitation:** at this experiment's specific scale (3-token
  shared prefix, 4-token max private suffix), the shared design used
  MORE physical memory than independent buffers at every branch count
  tested (1 through 16) — the opposite of the naive "sharing saves
  memory" assumption. This is a real, measured NEGATIVE result at this
  scale, driven by this experiment's specific eager-preallocation
  choice for private suffix buffers, not a property of sharing itself.
- **Unexpected observation:** the ~1e-7 numeric difference between the
  shared and independent designs comes specifically from computing
  attention scores via two separate matmuls and concatenating SCORES
  (not K/V), which has a different floating-point reduction order than
  one matmul over concatenated K — expected once identified, not
  anticipated beforehand.

### FL-06 — Context / Effective Execution Identity

- **Status:** COMPLETE, committed at `163e86ef`.
- **Result:** state reuse under a value-equivalent but object-distinct
  "identity" (weights, RoPE config) is indistinguishable from reuse
  under the original object (both diff ~2.38e-07 from a clean baseline,
  to the same digit). Both tested identity-mismatch dimensions (model
  weights, RoPE theta) produce IMMEDIATE (first-step) and PERSISTENT
  (every subsequent step through step 3) divergence when a cache is
  reused under a mismatched identity — no silent-then-diverging or
  self-correcting failure mode observed. Decoding-policy is structurally
  proven NOT to affect already-produced KV state (the cache is returned
  before any selection policy runs).
- **Strongest evidence:** the multi-step contamination attack (4
  consecutive steps, not just step 0) ruling out delayed or
  self-correcting divergence, combined with the exact digit-for-digit
  match between "exact identity reuse" and "equivalent-copy reuse."
- **Important limitation:** only 2 of 6 candidate identity fields
  (weight_seed, rope_theta) were experimentally varied. Architecture
  dimensions, KV dtype, and "execution semantics version" remain
  proposed-only — no second value exists in this oracle to construct a
  contamination test against for the latter two.
- **Unexpected observation:** RoPE-theta-only contamination diverges by
  ~5.98e-05 — three orders of magnitude smaller than full weight-identity
  contamination (1.27) but four orders of magnitude above this
  experiment's own 1e-5 tolerance, i.e. real and detectable but clearly
  smaller in magnitude. The two identity-mismatch dimensions tested are
  NOT equally "risky" at this model scale.

### FL-07 — Budgeted Weight Residency

- **Status:** COMPLETE, committed at `a6eb521b`.
- **Result:** a "keep the first N transformer layers permanently
  resident, stream the rest" policy — implemented as new C++ reusing
  Phase 1's `forward_with_layer_runner` seam — produces output
  BIT-IDENTICAL (not merely within tolerance) to full streaming and full
  residency at every tested budget, both exhaustively on a synthetic
  fixture (every budget 0..n_layers) and at 7 representative budgets
  (0,1,2,4,8,16,30) on a real 30-layer SmolLM2-135M model
  (`max_abs_diff = 0` at every point). Peak resident weight memory scales
  with the budget as expected (~14.16MB/layer for this model). Confirmed
  identical across Release, Debug, strict (`/W4 /WX`, zero warnings), and
  ASan (zero reports) build lanes on the synthetic test.
- **Strongest evidence:** bit-exact (not tolerance-bounded) equivalence
  at every tested point, on both synthetic and real-model data, across
  four independent build/sanitizer configurations.
- **Important limitation:** every real-model measurement is a SINGLE
  `forward()` call (prefill-only) — no multi-step decode was tested, and
  the "streamed layers pay backing I/O on every call" cost that matters
  most over many decode steps is a DERIVED expectation from single-call
  telemetry, not directly measured. Timing numbers are single
  observations, not repeated-trial statistics. The real-model sweep was
  run under Release only (a scope decision, not a result).
- **Unexpected observation:** `cumulative_materialized_bytes` and
  `backing_bytes_read` are exactly constant across every budget for a
  single forward call — the budget affects WHEN bytes are materialized,
  not the total volume moved for one decode step.

## Cross-experiment observations

- All three experiments independently confirm that a policy change
  affecting WHEN/HOW state is held (shared vs. private buffers in FL-05;
  reused vs. freshly-built identity in FL-06; resident vs. streamed
  layers in FL-07) can be made numerically transparent to the model's
  output — none of the three mechanisms tested this session perturbed
  correctness beyond ordinary floating-point non-associativity (and in
  FL-07's case, not even that — bit-exact).
- Both experiments that tested fault injection (FL-05's corrupted-prefix/
  lied-length attacks; FL-06's identity-mismatch contamination) found
  LARGE, immediately detectable divergence with no silent-corruption
  case — consistent with the underlying computation (attention,
  layer-normalized transformer blocks) being numerically sensitive
  enough that state corruption doesn't hide.
- FL-05 and FL-07 reached opposite-flavored memory conclusions at their
  respective scales: FL-05 (tiny synthetic buffers) found sharing costs
  MORE memory than independent allocation at every tested size; FL-07
  (real 30-layer model) found residency budgeting scales resident memory
  in the expected direction. These are not directly comparable results
  (different mechanisms, different scales) but both underline that
  "obviously helps" memory-policy intuitions need direct measurement at
  the relevant scale before being trusted.

## Negative / null results (preserved, not discarded)

- FL-05: shared-prefix design uses MORE physical memory than independent
  buffers at every tested branch count (1-16) at this experiment's
  buffer-sizing choices. This is a genuine negative result at this
  scale — recorded, not rescued by widening a tolerance or reframing.
- FL-05: complete logits are NOT bit-identical between shared and
  independent designs (only equivalent within 1e-5) — a real, understood
  but non-zero numeric cost of the two-matmul-then-concatenate-scores
  formulation.

## Measurement limitations (session-wide)

- FL-05 and FL-06 both run on a tiny synthetic fixture (2-3 layers,
  vocab 32, hidden 16) — neither has been reproduced at real-model
  scale. FL-07 is the only one of the three with real-model evidence.
- No experiment in this wave used repeated-trial timing statistics —
  every timing number reported (FL-07's load/forward milliseconds) is a
  single observation per configuration.
- FL-06's contamination attack was extended to 4 steps, not a longer
  decode — later, deeper divergence patterns beyond step 3 are untested.
- FL-07's real-model sweep did not test Debug/strict/ASan lanes (a
  documented scope decision, given the ~40-50x real-model ASan slowdown
  observed independently during this session's Phase 5A closure work).

## Candidate follow-up experiments (proposed only — none started)

- FL-05B: repeat FL-05 at real-model prefix lengths (hundreds to
  thousands of tokens) to test the HYPOTHESIS that the memory crossover
  favoring shared prefixes exists at realistic scale.
- FL-06 extension: vary architecture-dimension identity fields
  (n_layers, head counts, head_dim) to determine whether shape mismatches
  are structurally caught (array shape errors) or need explicit identity
  tracking of their own.
- FL-07 extension: multi-step decode measurement (repeated `forward()`/
  `forward_cached()` calls) to directly measure the cumulative backing-I/O
  divergence across budgets that this pass only derived.
- FL-05+FL-07 combination: shared-prefix branches under a residency
  budget, to see whether the two mechanisms compose without interaction
  effects.

None of these were started this session, per the explicit instruction to
stop after FL-05/06/07 and evaluate before proceeding.

## Architectural implications

None. Per this session's charter, no Fringe Lab observation from this
wave has been converted into an accepted OrcEngine architectural
decision, and none of the three EXPERIMENT.md reports contain an
architectural recommendation. All three explicitly state "does NOT
constitute an architectural recommendation for OrcEngine" in their "What
this experiment does NOT prove" sections.

## What should NOT be inferred

- That shared-prefix state, execution identity tracking, or budgeted
  residency are validated for integration into OrcEngine proper — all
  three are new, standalone mechanisms built for these experiments, not
  integrated with or a replacement for anything in the Phase 3/4/5A
  codebase.
- That FL-05's memory result generalizes to real-model scale (explicitly
  marked HYPOTHESIS, not tested).
- That FL-06's 2 tested identity fields are the complete set needed for
  a real identity-tracking mechanism (4 of 6 candidate fields remain
  untested).
- That FL-07's single-call timing numbers characterize real decode-loop
  performance (no multi-step measurement was taken).

## Git commits (this session, `research/orcengine-fringe-lab2`)

```
a6eb521b research(fringelab): add FL-07 budgeted weight residency experiment
163e86ef research(fringelab): add FL-06 context/execution-identity experiment and evidence
582637c7 research(fringelab): add FL-05 shared-prefix-state experiment and evidence
```

All three are bounded, per-experiment commits as required by this
session's charter. None have been merged into `feat/orcengine-phase5a-kv-cache`,
`orcengine-phase4-freeze`, or `master`, and none have been pushed.

## Raw artifacts

- `experiments/FL-05/fl05_shared_prefix.py`,
  `experiments/FL-05/raw/{fl05_result.json,
  fl05_run_attempt1_failed_20260818.log, fl05_run_final_20260818.log}`
- `experiments/FL-06/fl06_state_identity.py`,
  `experiments/FL-06/raw/fl06_result.json`,
  `experiments/FL-06/fl06_run_20260818.log`
- `experiments/FL-07/EXPERIMENT.md`,
  `experiments/FL-07/raw/fl07_real_sweep.json`,
  `Tools/OrcEngineFringeLab/` (new C++ project: headers, implementation,
  synthetic CTest, real-model sweep driver, CMakeLists)
