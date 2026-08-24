# Phase 6 Stage 1, Gate 6: internal Q8_0 tolerance-failure localization -- inconclusive, defect not excluded (corrected)

**CORRECTION (Codex/Grok remediation round 5, see OE-ADR-050): this
document's original title and Classification section overclaimed what
the evidence supports.** The original text asserted "no defect found"
and argued a shared dispatch code path "would be expected to manifest
on EVERY layer" if it contained a bug -- that reasoning is not sound: a
data-dependent defect (triggered only by particular block values,
tensor shapes, activation ranges, or numerical regimes) can be entirely
real while still appearing at only some layers, because those
conditions are what varies layer to layer, not the code. The corrected,
narrower title and Classification section below say precisely what the
evidence establishes and no more: the failure reproduces via an
independent code path, large aggregate divergence appears after layers
11 and 28, and **no implementation defect has been identified -- but a
data-dependent layout/dequantization/math defect has NOT been
excluded either.** The original overclaimed wording is preserved in
this file's git history rather than silently disappearing.

This is a SEPARATE investigation from the external llama.cpp comparison
(OE-ADR-046/047). It concerns OrcEngine's OWN F32-vs-Q8_0 behavior only.

## The failure, unchanged, reconfirmed with the corrected harness

- DEV-derived tolerance: `0.973504` (`dev_max_abs_error * 2.0` safety
  margin, per `phase6_q8_0_comparison.cpp`)
- `holdout_quick_fox` observed `max_abs_error`: `1.079983`
- Status: **FAILED**, reconfirmed in the v3 evidence run (generated
  after this remediation's Gate 1 logits-bounds-check fix -- see
  `phase6_q8_0_comparison_v3_run_output.txt`, still ends `1 FAILURES`).

**Not widened, not removed, not relabeled, not weakened.** This
document does not change the tolerance, does not move
`holdout_quick_fox` between DEV/HOLDOUT, and does not touch the
assertion.

## Bounded per-layer localization

New Phase-6-only diagnostic,
`tools/phase6_holdout_quick_fox_layer_localization.cpp` (committed):
calls the EXISTING public per-layer seam,
`execute_cached_transformer_layer()` (documented in `forward_cached.hpp`
as the shared math both the resident and virtualized decode paths
route through), one layer at a time, for both the F32 and Q8_0 models,
reconstructing the same preamble/epilogue `forward_cached_step_unsafe_
explicit_position()` already uses via existing public `ops::` functions
(embedding lookup, RoPE table construction, final RMSNorm, `lm_head`
projection). **No frozen Phase 1-5C file was modified.** Raw output:
`phase6_holdout_quick_fox_layer_localization_output.txt`.

### Per-layer F32-vs-Q8_0 hidden-state divergence

| Stage | max abs diff | RMSE |
|---|---|---|
| embedding lookup | 0.002419 | 0.000575 |
| layer 0 | 0.142609 | 0.018429 |
| layers 1-10 | 0.42-0.97 | 0.029-0.112 (smooth growth) |
| **layer 11** | **11.425154** | **1.413904** |
| layers 12-26 | 11.5-11.7 | 1.42-1.50 (stable plateau) |
| layer 27 | 13.375000 | 1.592289 |
| **layer 28** | **104.654297** | **3.087236** |
| layer 29 | 39.576187 | 2.756404 |
| final RMSNorm | 5.185188 | 0.284574 |
| output projection (last position, full vocab) | **1.079983** | 0.382465 |

The output-projection row's `max_abs_error` (`1.079983`) matches the
already-committed `phase6_q8_0_comparison.exe` value EXACTLY, confirming
this diagnostic reproduces the same real failure via an independent
code path, not a different computation.

### Reading the pattern

- **Layers 0-10**: smooth, gradual growth (RMSE `0.018 -> 0.112`,
  roughly consistent per-layer increments) -- exactly the expected
  shape of independent per-layer Q8_0 quantization noise accumulating
  through the residual stream. Nothing anomalous here.
- **Layer 11**: a sharp, isolated discontinuity -- max abs diff jumps
  ~15x and RMSE jumps ~13x in a single layer, far outside the smooth
  trend the prior 10 layers established.
- **Layers 12-26**: the elevated error PERSISTS but does NOT keep
  compounding sharply -- RMSE creeps `1.414 -> 1.498` over 15 layers,
  a similarly gentle per-layer increment to layers 0-10's own growth
  rate. This is consistent with layer 11 injecting one large error
  that then propagates through the residual stream like ordinary noise
  afterward, not with a runaway/unstable divergence.
- **Layer 28**: a second, larger isolated discontinuity (max abs diff
  jumps ~8x from layer 27's 13.4 to 104.7).
- **Layer 29**: the error partially SHRINKS (104.7 -> 39.6). **Reported
  as an observed reduction in aggregate error only** -- an earlier
  draft of this document attributed this to "normalization/nonlinearity
  effects partially damping an extreme per-dimension outlier," which
  was not measured and is corrected out; no specific mechanism is
  claimed for this reduction without dedicated measurement.
- **Final result**: despite this large intermediate divergence, both
  models still select the IDENTICAL top-1 token (`27003`) -- the
  logit-level error is large in an absolute sense but does not flip the
  greedy decision for this specific prompt.

### Weight-magnitude cross-check

A cheap additional scan (same diagnostic, no extra I/O) reports each
layer's F32 weight max-absolute-value across its Q/K/V/O and
gate/up/down projections:

| Layer | max\|weight\| | Discontinuity here? |
|---|---|---|
| 0 | 5.22 | No |
| 1 | 5.00 | No |
| 2 | 5.31 | No |
| 11 | 6.06 | **Yes** |
| 27 | 9.31 (highest of all 30 layers) | Elevated, but continuation of the layer-11-onward plateau, not a fresh jump |
| 28 | 6.16 | **Yes** |
| 29 | 5.00 | No (error SHRINKS here) |

**Weight magnitude alone does not explain the pattern**: layers 0-2
have max-weight values (5.0-5.3) comparable to or only slightly below
layer 11's (6.06), yet show no discontinuity; layer 29's max-weight
(5.0) is unremarkable despite following the largest discontinuity in
the whole trace. A single scalar per-layer max-weight is too coarse a
proxy anyway -- Q8_0 quantizes per 32-element BLOCK, not per whole
tensor, so this check can rule out "this layer's weights are globally
huge" as the sole explanation but cannot rule in or out a specific
poorly-scaled BLOCK interacting with this prompt's specific activation
pattern, which would require much finer-grained (per-block/per-channel)
instrumentation not attempted in this bounded pass.

## Classification (corrected -- see OE-ADR-050)

**No implementation defect has been IDENTIFIED. This is not the same
claim as "no defect EXISTS," and the original version of this document
conflated the two.** The original argument -- that Q8_0 dequantization
and per-layer dispatch run identical code every layer, so a defect
"would be expected to manifest on EVERY layer" -- does not hold up: a
DATA-DEPENDENT defect (one triggered only by particular block scale
values, particular tensor shapes, particular activation magnitude
ranges, or a specific numerical regime -- e.g. an edge case in rounding,
saturation, or accumulation order that only certain inputs reach) can
be entirely real while manifesting at only some layers, precisely
BECAUSE those conditions vary layer to layer even though the code does
not. The observed pattern (isolated, bounded, non-runaway jumps at
layers 11 and 28, with the top-1 selection still landing correctly) is
CONSISTENT WITH genuine per-block quantization sensitivity that this
adversarial prompt happens to trigger -- but consistency is not proof,
and this document does not rule out a genuine data-dependent
implementation defect at or feeding into those two layers. The
weight-magnitude cross-check ruled out ONE specific hypothesis (global
per-layer weight magnitude alone) but, by its own stated limitation
(a scalar per-tensor max cannot see per-32-element-block behavior),
cannot rule in or out a specific poorly-scaled block.

**This is also not simply "a comparison/evidence defect"**: the
independent layer-by-layer diagnostic reproduces the exact same
`1.079983` final value the original Checkpoint 3 comparison tool
computed, via a completely separate code path (manual layer-by-layer
calls vs. the single `forward_cached_step` call the comparison tool
uses) -- ruling out an evidence-generation-side bug as the explanation
for the NUMBER itself (though it does not by itself rule out a subtler
defect inside the shared per-layer math both paths call).

**The current evidence does NOT yet prove that the tolerance-
DERIVATION METHODOLOGY, rather than implementation behavior, is the
cause.** A flat `2.0x` multiplier of a 4-prompt DEV maximum being an
inadequate STATISTICAL methodology (too few DEV samples to bound a
tail) remains a live hypothesis, matching the original Checkpoint 3
finding (`CHECKPOINT3_STATUS.md`) -- but this document's own evidence
(isolated per-layer jumps at specific layers) is equally consistent
with an as-yet-unlocalized implementation issue that a methodology fix
alone would not address. **Classification: still inconclusive.**
Gate 3 below performs a same-input weight-vs-state decomposition and a
per-block inspection specifically to distinguish these two
possibilities, rather than asserting one without that evidence.

## Gate 3 extension (round 5): per-position tracking, same-input decomposition, control prompts

The diagnostic (`phase6_holdout_quick_fox_layer_localization.cpp`) was
extended, without touching any frozen Phase 1-5C file, to: (A) report
PER-POSITION max-abs-diff alongside the aggregate, and verify F32/Q8_0
`ModelConfig` equality before reusing one for both paths; (B) at each
implicated layer, run a same-cumulative-input four-way decomposition
(F32 weights/F32 input, Q8 weights/F32 input, F32 weights/Q8 input, Q8
weights/Q8 input) to separate the isolated WEIGHT-quantization effect
from the isolated INCOMING-STATE effect; (C) run the same analysis on
THREE prompts in one process (one model load): the failing
`holdout_quick_fox`, a DEV prompt already below tolerance
(`dev_code_snippet`, F32-vs-Q8_0 error `0.486752`), and an agreeing
HOLDOUT control (`holdout_hello_world`, error `0.487471`). Raw output:
`phase6_holdout_quick_fox_layer_localization_output.txt`.

### Finding 1 (Gate 3C): layers 11 and 28 are NOT unique to the failing prompt

**The identical discontinuity pattern -- a sharp jump at layer 11,
sustained elevation through layers 12-26, and a second larger jump at
layer 28 -- occurs on ALL THREE prompts**, including the two that
comfortably pass the internal tolerance:

| Prompt | Tolerance status | Layer 11 max_abs | Layer 28 max_abs |
|---|---|---|---|
| `holdout_quick_fox` | FAILS (`1.079983` > `0.973504`) | 11.43 | 104.65 |
| `dev_code_snippet` | passes (`0.486752`) | 10.99 | 102.40 |
| `holdout_hello_world` | passes (`0.487471`) | 12.07 | 91.88 |

This directly answers Gate 3C: **layers 11 and 28 are common, model-
wide amplification points, not something specific to
`holdout_quick_fox`.** The three prompts' FINAL output-projection
errors differ substantially (`1.08` vs `0.49` vs `0.49`) despite
comparable layer-11/28 divergence magnitude -- meaning what determines
PASS vs. FAIL is not simply "does layer 11/28 blow up" (it does, on
every prompt examined) but something in how that intermediate
divergence propagates through the REMAINING layers and interacts with
this specific prompt's own activation pattern at the final position.

### Finding 2 (Gate 3A): the divergence is concentrated at POSITION 0, not spread across positions

For every prompt and every layer where a large jump occurs, the
per-position breakdown shows **position 0 (the first token) accounts
for nearly all of the aggregate max-abs-diff**, with later positions
one to two orders of magnitude smaller. Example, layer 11:

| Prompt | Position 0 | Positions 1+ |
|---|---|---|
| `holdout_quick_fox` | 11.4252 | 0.31-0.58 |
| `dev_code_snippet` | 10.9926 | 0.20-0.33 |
| `holdout_hello_world` | 12.0654 | 0.30-0.64 |

The same position-0 concentration holds at layer 28 (e.g.
`holdout_quick_fox`: position 0 = `104.6543`, positions 1-3 = `1.11-2.42`).
This is a genuinely new, more specific localization than round 4
established: **the phenomenon is not "layer 11/28 broadly," it is
"layer 11/28's handling of POSITION 0 specifically,"** across three
different first-token IDs (`504`, `1604`, `19556` respectively) --
ruling out one specific token value as the trigger, and pointing at
something structural to position 0's computation (e.g. its RoPE angle
being the identity rotation, or an all-positions-attend-to-position-0
attention pattern) rather than that token's specific embedding.

### Finding 3 (Gate 3B): both weight quantization and state propagation contribute substantially, with a sub-additive interaction

Same-cumulative-input decomposition at layer 11 (`holdout_quick_fox`):
isolated WEIGHT effect (F32 weights vs Q8 weights, same F32 input)
`max_abs=11.42`; isolated STATE effect (F32 input vs Q8 input, same
F32 weights) `max_abs=16.41`; cumulative COMBINED `max_abs=11.43`. At
layer 28: WEIGHT effect `max_abs=59.64`; STATE effect `max_abs=45.04`;
COMBINED `max_abs=104.65`. **Both isolated effects are large on their
own at both layers -- this is not a case where one factor dominates
and the other is negligible.** The combined effect is consistently
LESS than the naive additive bound (weight+state) at layer 11 (e.g.
`11.43` combined vs `27.83` additive bound for `holdout_quick_fox` --
a large negative "interaction gap"), but very close to additive at
layer 28 (`104.65` combined vs `104.68` additive bound). This pattern
(large sub-additive interaction at 11, near-additive at 28) is recorded
as an observation; this diagnostic does not attempt to explain the
mechanism behind the difference between the two layers' interaction
behavior.

### Revised classification

**Still inconclusive, but substantially more localized than round 4.**
The evidence now points specifically at: layers 11 and 28, position 0
specifically (not later positions), a real contribution from BOTH
weight quantization and propagated input state (not purely one or the
other), occurring identically across three prompts regardless of final
pass/fail outcome. This is consistent with either (a) a genuine,
position-0-specific quantization sensitivity in a small number of
Q8_0 blocks feeding layers 11/28's Q/K/V/O or gate/up/down projections,
that this model's architecture or these two layers' weight
distributions happen to make unusually large, or (b) an as-yet-
unlocalized data-dependent implementation issue specific to position-0
handling at those two layers. **Per-block inspection (Gate 3D:
block-scale distribution, worst-block error, saturation counts,
cross-check against the independent reference dequantization) was NOT
performed in this pass** -- disclosed as a real limitation and the
concrete recommended next step, not silently skipped. The internal
tolerance gate remains **FAILED**, unchanged.

## What happens next -- explicitly NOT done in this pass

Per instruction: "If no implementation defect is found and the
tolerance methodology appears inadequate, do not derive a wider gate
from the already-observed holdout... Instead: document the proposed
methodology change; derive it from development evidence only; define a
new, previously unused holdout corpus; stop for Codex approval before
changing the acceptance gate."

**None of that is done here.** This document records the localization
finding only. Specifically NOT performed in this pass:
- No new tolerance value is proposed or derived.
- No new DEV or HOLDOUT corpus is defined.
- No change to `phase6_q8_0_comparison.cpp`'s tolerance-derivation code.
- No claim that `1.079983` should now be considered passing.

The internal gate remains **FAILED** at `0.973504` vs `1.079983`,
exactly as before this investigation. Revising the acceptance
methodology is out of scope for this pass and requires separate
authorization.
