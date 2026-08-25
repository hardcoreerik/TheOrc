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

## Disposition

No frozen file, no existing committed tool, and no existing evidence
document was modified for Gate 5 this round. The internal tolerance
gate's status is unchanged: **FAILED** (`0.973504` vs `1.079983`).
Classification per the mega-prompt's own taxonomy: **still
inconclusive** at the sub-projection granularity requested -- the
existing whole-layer evidence supports "ordinary but larger-than-
expected Q8 sensitivity, additive not interactive" as the leading
hypothesis, but does not yet exclude a specific implementation defect
at the sub-projection level, which is exactly what the deferred
extension would test. No replacement tolerance is proposed.
