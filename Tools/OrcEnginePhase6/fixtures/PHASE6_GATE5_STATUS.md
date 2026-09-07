# Phase 6 Stage 1, Gate 5 (round 7): deferred per-block internal investigation

**Status: DEFERRED, disclosed explicitly, not silently skipped.**

## What was requested

Bounded per-block inspection at layers 11 and 28, for
`holdout_quick_fox`, `dev_code_snippet`, `holdout_hello_world`, at
positions 0 and the final prompt position, of the Q/K/V/attention-
output/gate/up/down projections individually (not just each layer's
combined output), reporting per projection: Q8_0 scale distribution,
worst block indices, max/RMSE reconstruction error, activation-
weighted output contribution, position-specific contribution, whether
the same component dominates failing and passing prompts, and the
correct factorial interaction contribution.

## What already exists (round 5/6 work, reused, not redone)

`phase6_holdout_quick_fox_layer_localization.cpp` already provides,
for exactly these 3 prompts and layers 11/28: per-position AND
aggregate diff stats for the full LAYER OUTPUT (not sub-projection),
plus a rigorous same-cumulative-input four-way weight/state/combined/
interaction decomposition (using the corrected factorial-interaction
formula this round's Gate 1 refers back to) at whole-layer granularity.
This is real, working, previously-verified evidence -- see
`PHASE6_GATE6_INTERNAL_TOLERANCE_LOCALIZATION.md`.

## Why this round did not extend it to full sub-projection granularity

Gates 1-4 this round (claim correction, fail-closed Gate 2 hardening
with a full real hash-verified 7-prompt run, a new genuine single-
variable Q/K isolation experiment with a self-verifying GGUF-rewrite
tool, and a new C++ loader-compatibility diagnostic with Debug/strict/
ASan verification) consumed the available scope. Extending the
existing tool to per-PROJECTION granularity is materially larger than
the whole-layer decomposition it already does:

- `LayerWeights` exposes `w_q`/`w_k`/`w_v`/`w_o`/`w_gate`/`w_up`/
  `w_down` individually (`Tools/OrcEnginePhase1/include/orcengine/
  model.hpp:44-53`), so per-projection intermediates ARE obtainable
  without touching frozen math -- but doing so means calling
  `ops::linear_no_bias`/`ops::rmsnorm`/the attention sub-steps
  directly, reconstructing (not reusing) `execute_cached_transformer_
  layer`'s internal sequencing outside that frozen function, at 2
  layers x 7 projections x 3 prompts x 2 positions x 4 weight/state
  combinations -- a materially larger surface than this round's other
  4 gates combined.
- The Q8_0 "scale distribution" and "worst block indices" requirement
  additionally needs reading raw per-superblock Q8_0 scale values
  (Phase 2's `gguf.cpp` dequantization internals), which is a
  different code path from anything this round's other gates touched.
- "Activation-weighted output contribution" requires deciding a
  principled weighting (e.g., contribution to the FINAL logit via the
  downstream Jacobian, or a cheaper proxy) that was not specified
  precisely enough to implement without risking a second invalid ad
  hoc heuristic -- exactly the kind of unreviewed metric this whole
  remediation exists to avoid repeating (see OE-ADR-052's correction
  of the round-6 scalar-interaction heuristic for the precedent this
  is deliberately avoiding repeating under time pressure).

## What is NOT deferred

- Gate 5's OWN scope is unaffected by Gates 1-4's findings: the
  internal tolerance blocker (`0.973504` vs `1.079983`, still FAILED)
  is externally-independent of the Q/K layout question Gates 1-4
  addressed (already disclosed in every relevant ADR this round).
- The existing whole-layer decomposition evidence
  (`PHASE6_GATE6_INTERNAL_TOLERANCE_LOCALIZATION.md`) remains valid
  and unmodified -- its "nearly perfectly additive, negligible true
  interaction" finding (round 6) stands: both isolated weight-
  quantization and incoming-state effects contribute substantially at
  layers 11/28, with negligible true (non-additive) interaction
  between them. That is layer-output-level evidence, not
  projection-level.

## Recommended next step (not started)

A dedicated follow-up round, scoped ONLY to Gate 5, extending
`phase6_holdout_quick_fox_layer_localization.cpp` (or a new sibling
tool, to avoid growing one file past reviewable size) with:
1. Per-projection weight/state/combined/interaction decomposition
   (reusing `effect_vector_stats`/`factorial_interaction_stats`,
   already generic over any two vectors -- the extension is calling
   individual `ops::linear_no_bias` calls per projection, not new math
   primitives).
2. A precisely-specified, reviewed activation-weighting metric BEFORE
   implementation (not invented ad hoc mid-tool), to avoid repeating
   the round-6 scalar-interaction mistake this remediation has spent
   two rounds correcting.
3. Direct Q8_0 scale-block inspection via Phase 2's existing
   dequantization internals (read-only).

## Disposition (round of the deferral above)

No frozen file, no existing committed tool, and no existing evidence
document was modified for Gate 5 that round. The internal tolerance
gate's status is unchanged: **FAILED** (`0.973504` vs `1.079983`).
Classification per that mega-prompt's own taxonomy: **still
inconclusive** at the sub-projection granularity requested -- the
existing whole-layer evidence supports "ordinary but larger-than-
expected Q8 sensitivity, additive not interactive" as the leading
hypothesis, but does not yet exclude a specific implementation defect
at the sub-projection level, which is exactly what the deferred
extension would test. No replacement tolerance is proposed.

## Methodology for the dedicated Gate 5 follow-up (written before implementation, per instruction)

This section defines every quantity the new projection-level diagnostic
(`phase6_holdout_quick_fox_projection_localization.cpp`, a new sibling
tool -- the existing `phase6_holdout_quick_fox_layer_localization.cpp`
is not modified) will report, and its limitations, BEFORE any code is
written against these definitions.

### Scope

Layers 11 and 28 (the two layers implicated by the existing whole-layer
trace). Prompts: `holdout_quick_fox` (failing), `dev_code_snippet` and
`holdout_hello_world` (passing controls) -- the same three prompts,
same token IDs, as the existing tool. Positions: position 0 and the
final prompt position. Projections: Q, K, V, attention-output (O), FFN
gate, FFN up, FFN down -- the seven weight matrices in `LayerWeights`
(`Tools/OrcEnginePhase1/include/orcengine/model.hpp:44-53`).

### What "the same projection input vector x" means, per projection

Each projection's real input in the frozen forward pass
(`execute_cached_transformer_layer_impl`,
`Tools/OrcEnginePhase5A/src/forward_cached.cpp:38-201`, read but not
modified) is:

- **Q, K, V**: `rmsnorm(x_before_layer, attn_norm_weight)` -- the
  post-attention-norm hidden state.
- **O**: the flattened multi-head attention context (`context_flat`) --
  the output of RoPE + causal attention using that side's OWN Q/K/V
  projections and its own KV cache content.
- **FFN gate, up**: `rmsnorm(r, ffn_norm_weight)` where `r = x_before_layer
  + attn_out` -- the post-attention-residual, post-FFN-norm hidden
  state.
- **FFN down**: `silu(gate_proj) * up_proj` -- the SwiGLU-activated
  intermediate.

"F32 incoming state" and "Q8-perturbed incoming state" for a given
projection mean: that exact input vector, computed by running the
F32 side's (respectively the Q8 side's) OWN complete upstream
pipeline up to that point -- using that side's own weights for every
upstream step, never a mix mid-pipeline. This mirrors the existing
whole-layer tool's own convention for `x_before_f32`/`x_before_q8`
(each is that side's own cumulative state, not a cross-substitution).
Consequently, "incoming-state effect" at a given projection already
includes that side's own upstream norm-weight quantization and (for O
and down) that side's own upstream projection/attention quantization
effects -- it is not a claim that ONLY the immediately-prior
projection's weight differs. This is the smallest defensible
definition available without a fifth "norm-weight-only" axis the
mega-prompt did not request.

### Four-way factorial table, per projection

For projection weight matrix `W` and its real input `x`:

1. `y_ff = linear_no_bias(x_f32, W_f32)` -- F32 weight, F32 state.
2. `y_qf = linear_no_bias(x_f32, W_q8_dequantized)` -- Q8 weight, F32 state.
3. `y_fq = linear_no_bias(x_q8, W_f32)` -- F32 weight, Q8-perturbed state.
4. `y_qq = linear_no_bias(x_q8, W_q8_dequantized)` -- Q8 weight, Q8-perturbed state.

Reporting, reusing the EXISTING generic `effect_vector_stats()` and
`factorial_interaction_stats()` from the whole-layer tool (copied by
value into the new tool's own translation unit, since C++ has no
shared-diagnostic-library target for these free functions yet, but
NOT reimplemented with different logic -- same formulas, same code):

- isolated weight effect = `effect_vector_stats(y_qf, y_ff)`
- isolated incoming-state effect = `effect_vector_stats(y_fq, y_ff)`
- combined effect = `effect_vector_stats(y_qq, y_ff)`
- exact interaction = `y_qq - y_qf - y_fq + y_ff`, per
  `factorial_interaction_stats(y_ff, y_qf, y_fq, y_qq)`.

### "Activation-weighted projection-output error" (Δy)

Defined exactly as: `Δy = x_f32 · (W_q8_dequantized - W_f32)` for the
SAME projection input vector `x_f32`. This is algebraically identical
to `y_qf - y_ff` above (the isolated weight effect using the F32-side
input) -- so it is not computed as a second, separate quantity; the
isolated-weight-effect row of the four-way table above already IS this
metric. Reported: max absolute error, RMSE, and L2 norm of `Δy`
(`sqrt(sum(Δy_i^2))`, a new small helper, since the existing tool only
reports RMSE/max, not L2 norm).

**Limitation, stated explicitly per instruction**: this is a LOCAL,
activation-weighted, single-projection output error. It is NOT a claim
about which projection causally dominates the final-logit failure --
that would require the downstream Jacobian (how a perturbation at
layer 11's Q output propagates through 17 more layers to the final
logits), which this diagnostic does not compute. Gate 3's "does one
projection uniquely dominate" question is answered by comparing these
LOCAL error magnitudes across projections and across prompts, and by
comparing to the EXISTING whole-layer aggregate evidence and the final
logit margin -- not by asserting a magnitude ranking equals a
causality ranking.

### Unavailable seam encountered (disclosed per instruction, not silently worked around)

Gate 2's raw Q8_0 block inspection needs the STORED F16 scale decoded
per block. Phase 2's actual F16->F32 decoder, `half_to_float()`
(`Tools/OrcEnginePhase2/src/gguf.cpp:328`), is defined inside an
anonymous namespace (spanning lines 19-353) -- internal linkage, not
reachable from another translation unit, and not declared in
`gguf.hpp`. Modifying `gguf.hpp`/`gguf.cpp` to export it is out of
scope (frozen production file). This diagnostic therefore implements
its OWN independent IEEE-754 half-to-float conversion (a fully
specified, standard bit-manipulation algorithm, not "frozen transformer
sequencing" or a business decision) rather than either stopping this
gate entirely or silently duplicating the frozen function's exact
source text. This independent implementation is then CROSS-VALIDATED
against the frozen `dequantize_q8_0_scalar_reference()`
(`Tools/OrcEnginePhase2/include/orcengine/gguf.hpp:197`, itself
unmodified and called, not reimplemented) in the required known-value
test: the same raw block bytes must dequantize to identical F32 values
via both this diagnostic's raw-block reconstruction and the frozen
production function, or the test fails. This is a stronger check than
a single hand-computed known value alone, precisely because the
independent implementation is required to agree with production.

### Disposition of this methodology section

Written before any diagnostic code exists. The actual per-projection
tables, raw-block findings, and outcome classification are recorded in
the section below, appended only after the tool was built and actually
run against the real GGUF pair.

## Results: `phase6_holdout_quick_fox_projection_localization` (dedicated Gate 5 follow-up)

**Canonical evidence artifact**:
`Tools/OrcEnginePhase6/fixtures/PHASE6_GATE5_PROJECTION_EVIDENCE.txt`
(committed). The COMPLETE raw per-projection, per-layer, per-prompt
numerical tables and the full self-test transcript exist ONLY in that
file, along with input-artifact hashes, exact invocation, build
configuration, exit code, and cross-build (Debug/strict/ASan)
comparison hashes. This section is a summary and interpretation of
that data -- it does not reproduce the full tables, and any number
quoted below should be checked against the canonical artifact, not
treated as the primary record.

Built and run against the real, hash-verified F32/Q8_0 GGUF pair
(SmolLM2-135M, vocab=49152 hidden=576 n_layers=30). All 14 known-value
self-test checks (Gate 2's original 8, plus this round's hardening
additions: 3 for the new zero-scale-block case, 3 for the extent-
mismatch regression) passed before any model I/O was attempted,
including the required independent-parser-vs-production-dequantizer
parity check (the diagnostic throws immediately, not silently, if the
two ever disagree -- they did not disagree on any of the tens of
thousands of blocks actually inspected).

### Gate 1: projection-level four-way tables (layers 11, 28; all 3 prompts)

Every non-FFN-down projection (Q, K, V, O, FFN gate, FFN up) shows
**ordinary, small, and mutually comparable** effects across all three
prompts: isolated weight-effect max_abs in the 0.02-0.08 range,
isolated state-effect max_abs in the 0.08-0.5 range, and -- critically
-- **interaction terms 2-3 orders of magnitude smaller than either
isolated effect everywhere** (0.0005-0.05 vs. 0.02-0.5). This is
mathematically expected for a linear projection: the exact interaction
residual for `y = xW` is algebraically `(Δx)(ΔW)`, the product of two
already-small perturbations, so a small-but-nonzero interaction is
the CORRECT behavior of a working linear layer, not evidence against
one. This confirms round 6's whole-layer "nearly additive, negligible
interaction" finding replicates at projection granularity.

**FFN_down is the one outlier, and it shows comparable magnitude at
the same coordinate across passing and failing prompts alike.** At
layer 11, FFN_down's isolated weight-effect max_abs is 11.18
(`dev_code_snippet`, PASSING), 11.43 (`holdout_quick_fox`, FAILING),
and 12.04 (`holdout_hello_world`, PASSING) -- all at the SAME
coordinate, `[pos=0, ch=306]` (an approximately 8% max/min spread
across the three prompts). At layer 28, the isolated state-effect and
combined-effect max_abs are approximately 86.96-91.55 across all three
prompts, all at `[pos=0, ch=507]`. These are bounded, comparable
magnitudes at the same coordinate, not proof of an exact or
prompt-independent value -- "recurs identically" and "within a few
percent" (this section's own prior wording) overstated the precision
the data actually supports; "comparable magnitude at the same
coordinate across passing and failing prompts" is the defensible
statement. In every case, `L2(pos0)` is 40-400x larger than
`L2(final)` for the affected projection's own output -- the disruption
is heavily concentrated at position 0 and does not propagate
proportionally to that projection's own later-position outputs.

### Gate 2: raw Q8_0 block inspection (layers 11, 28, all 7 projections)

No non-finite (malformed) scales in any of the 14 tensors inspected (7
projections x 2 layers); a small number of exact-zero scales occur,
which is a legitimate encoding for an all-zero block, not a defect,
and is now tracked and reported separately from non-finite scales
(see the round-of-hardening correction below). Scale distributions are
all in an ordinary, narrow range (roughly 0.0005-0.05 across every
tensor, consistent medians around 0.003-0.004). Per-block
reconstruction error (against the real F32 ground truth, not merely
the production dequantizer) tops out at 0.007-0.024 max_abs across
every tensor -- consistent with ordinary int8-quantization step-size
noise (max rounding error is approximately half the block's own
scale). **Correction**: the prior statement that "0.003-0.005 scale
implies 0.0015-0.025 expected max rounding error" was arithmetically
wrong -- half of 0.003-0.005 is approximately 0.0015-0.0025, not
0.0015-0.025 (a stray order of magnitude). The observed maximum
reconstruction error near 0.024 corresponds to blocks whose OWN scale
is near the reported per-tensor maximum (approximately 0.048, e.g.
FFN_down layer 11's `scale_max=0.0477295`), not the median-scale
range -- `0.048 / 2 ~= 0.024`, which is exactly what is observed. No
block shows a reconstruction error anywhere near the 7-12 magnitude of
the Gate-1 projection-output outlier -- the outlier is not explained
by one corrupted or mis-scaled Q8_0 block; it is consistent with
ordinary, small, per-element rounding noise multiplying against an
activation that is itself extremely large specifically at position 0
(a "BOS-adjacent outlier channel," a documented phenomenon in
transformer quantization literature, not unique to this
implementation).

**Round-of-hardening correction (Codex review)**: the diagnostic
previously classified a zero scale together with a non-finite scale
under one `invalid_scale_count`. A zero scale is a legitimate encoding
for an all-zero block and is not itself evidence of a defect; it is
now reported separately (`zero_scale_count` vs. `nonfinite_scale_count`
in the tool's output), and a known-value self-test now proves a
zero-scale block with a deliberately NONZERO int8 payload decodes to
all zeros and is not flagged as malformed. Non-finite scales remain
fail-closed-reportable, unweakened.

### Gate 3: failing-versus-control comparison

1. **Does one projection uniquely dominate `holdout_quick_fox`?**
   FFN_down's LOCAL position-0 error dominates every other projection
   by roughly 2 orders of magnitude, but:
2. **Does the same projection show comparable error in the passing
   prompts?** Yes -- comparable magnitude at the same coordinate across
   all three prompts, including the two PASSING ones (layer 11:
   11.18-12.04, approximately an 8% max/min spread; layer 28:
   approximately 86.96-91.55). FFN_down's outlier is therefore NOT what
   differentiates the failing prompt from the passing ones.
3. **Root cause of the holdout failure**: not weight reconstruction
   alone, not incoming-state drift alone, not their interaction
   (negligible everywhere), and not a demonstrated implementation
   defect (Gate 2 found none). The evidence best supports **normal,
   additive quantization-noise accumulation across many small channels
   over 30 layers**, whose net effect happens to differ enough
   prompt-to-prompt to tip `holdout_quick_fox` over the derived
   tolerance while the other two stay under it -- consistent with, and
   now extended from, round 6's whole-layer "additive, not
   interactive" finding.
4. **Do layers 11/28 remain special?** The SAME channel (507) recurs
   as the dominant state/combined-effect channel in the O projection at
   layer 11 (`[pos=0,ch=507]`, visible in the whole-layer tool's own
   evidence) and in FFN_down's state/combined effect at layer 28 --
   suggesting a persistent, model-wide "sink" feature living in the
   residual stream at that channel index, not something unique to
   layers 11 or 28 specifically. This diagnostic did not test other
   layers, so this is stated as a hypothesis this evidence is
   consistent with, not a proof that other layers are equally affected.
5. **Is position 0 still dominant after decomposition?** Yes,
   overwhelmingly -- confirmed at the sub-projection level for every
   one of the 7 projections, not only in the whole-layer aggregate.

**F32 top-1/top-2 final-logit margins**: `holdout_quick_fox`
(FAILING) = 0.427; `dev_code_snippet` (PASSING) = 2.127;
`holdout_hello_world` (PASSING) = 0.162. Margin alone does not explain
pass/fail: `holdout_hello_world` has a NARROWER margin than the
failing prompt yet stays under tolerance (its independently-measured
whole-model max_abs_error, 0.487, is well under `holdout_quick_fox`'s
1.080). The differentiator is the prompt's own accumulated numerical
trajectory, not decision-margin narrowness by itself.

### Gate 4 outcome classification

**Outcome B (PROVISIONAL): implementation appears correct in the
seams examined; current tolerance methodology is inadequate.**

Support for this classification, per the mega-prompt's own required
conditions:
- Known-value dequantization remains exact (10/10 self-test checks
  after this round's hardening, including exact hand-computed block
  values and the new zero-scale-block check).
- Raw-block parsing is correct (independent parser agrees with the
  frozen production `dequantize_q8_0_scalar_reference()` on every
  element of every block inspected across all 14 tensors -- the
  diagnostic is coded to throw immediately on any disagreement, and
  none occurred).
- Projection results follow directly from measured quantization error
  (Gate 2's per-block reconstruction error matches the expected
  int8-step-size magnitude at the observed scales; nothing in the raw
  block data is anomalous).
- No dispatch/layout/math defect was found in the seams this round
  actually exercised: the factorial-interaction formula's
  near-zero-but-nonzero values are exactly what a correctly implemented
  linear layer produces for a bilinear interaction of two small
  perturbations, not a symptom of a bug.
- The holdout failure is consistent with empirically ordinary
  quantization drift (the FFN_down/O-projection position-0 outlier
  shows comparable magnitude at the same coordinate across passing and
  failing prompts alike, not exact or identical recurrence) combined
  with each prompt's own accumulated numerical trajectory over 30
  layers -- not narrow decision margin alone, and no single defect was
  found.

**This is a provisional classification, not a proof.** This pass
examined a specific, bounded set of seams (two layers, two positions,
three prompts, seven projections, direct raw-block inspection) and
found no defect and no anomaly within that scope. It does NOT prove
this is the sole root cause of the holdout failure, and it does NOT
prove every OrcEngine Q8_0-related code path is correct -- only that
the paths this diagnostic actually exercised behaved as expected. A
future round examining different layers, positions, or code paths
could still surface something this one did not.

Per the mega-prompt's Outcome B requirements, this pass does **not**
change the gate (`0.973504` vs `1.079983` remains **FAILED**,
unchanged), and does **not** propose or adopt a replacement tolerance
value. That is a separate, larger decision requiring its own
maintainer-approved methodology (a larger development corpus, a truly
separate holdout, explicit safety margins) -- explicitly out of scope
for this diagnostic-only pass, per Gate 5's own boundary instructions.
**Recommendation for a future round** (not started, not authorized
here): derive a replacement tolerance empirically from a materially
larger, disjoint prompt corpus, characterizing the position-0
outlier-channel phenomenon's typical magnitude range across many
prompts rather than deriving a single-digit-prompt-count tolerance
that this evidence suggests may not have adequately captured that
phenomenon's natural variance.

### Limitations

- This diagnostic examined only layers 11 and 28, positions 0 and
  final, and the 3 already-pinned prompts -- it does not prove the
  position-0 outlier-channel phenomenon is absent or present at other
  layers/positions/prompts.
- The activation-weighted error metric (Δy) is local per the
  methodology's own stated limitation -- it is not a downstream
  Jacobian and does not itself prove causal contribution to the final
  logit failure.
- **Correction (Codex review)**: this limitation previously stated the
  correlation would be "block `floor(306/32)`" -- wrong. Weights are
  stored `[out_features, in_features]` row-major (`ops.hpp`'s own
  documented convention), so an OUTPUT channel (Gate 1's outlier
  coordinate `ch=306` is an output-channel index, since FFN_down's
  output width is `hidden`) occupies its own entire ROW of
  `in_features` elements, i.e. `in_features / 32` consecutive blocks,
  not one block at index `channel/32`. For FFN_down specifically
  (`in_features = cfg.intermediate`, derivable from the model's own
  config -- for the real SmolLM2-135M pair used this round,
  `intermediate=1536`, confirmed from `blocks=27648 = 576 * 1536 / 32`
  in the committed evidence), output channel 306's row spans blocks
  `306 * (1536/32)` through `306 * (1536/32) + 47`, i.e. blocks
  14688-14735 for this specific model. This is a property of the model
  config, not a hardcoded constant, and was not computed as executable
  logic this round. Checked by hand against the committed evidence:
  layer 11 FFN_down's reported worst blocks are `[24382, 24374,
  17838]` -- NONE fall in `[14688, 14735]`. This means the single
  worst-reconstruction-error blocks in the whole tensor are NOT inside
  the specific outlier channel's own row -- consistent with (not
  proof of) the finding that the outlier is not caused by one
  anomalously bad block, but by ordinary per-block quantization noise
  combined with an unusually large activation magnitude at that
  channel/position. A full per-row aggregate (not just top-3 whole-
  tensor blocks) was not computed this round and would be needed to
  make a stronger claim.
- ASan verification of the pre-hardening diagnostic completed after
  this section was originally written (it progressed far slower than
  Debug/strict, ~10-20x, consistent with ASan's known instrumentation
  overhead on I/O-heavy code, not a hang): zero sanitizer violations,
  exit code 0, numerical output byte-identical to Debug and strict.
  This round's hardened diagnostic is re-verified under Debug/strict/
  ASan again -- see this round's own commit for those results and the
  canonical evidence artifact.

### FL-08 and the external Q/K-layout question

Unaffected and out of scope, per the mega-prompt's explicit
instruction. FL-08 remains frozen (`orcengine-fl08-freeze`) and was
not touched.
