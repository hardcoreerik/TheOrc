# Phase 6 Stage 1 Checkpoint 3: status -- PARTIAL, tolerance-derivation gate failed honestly

**This checkpoint did NOT pass its own predeclared gate.** Reported here
exactly as observed, per this task's explicit instruction: "Do not hide
failures behind relaxed tolerances... If holdout fails, report the
failure; do not widen the tolerance after seeing it."

## What ran

`Tools/OrcEnginePhase6/tools/phase6_q8_0_comparison.cpp` (the OrcEngine
F32-vs-Q8_0 leg): loads both the pinned F32 GGUF and the locally-
quantized Q8_0 fixture (`Q8_0_FIXTURE_PROVENANCE.md`), runs the frozen
cached-decode path on a predeclared 4-prompt DEV corpus and a
predeclared 3-prompt HOLDOUT corpus (prompts tokenized once via the
pinned `llama-tokenize.exe`, hardcoded in the source), and compares
full-vocabulary logits between the two. Raw evidence:
`phase6_q8_0_f32_comparison_evidence.jsonl` (7 lines, one per prompt).

## What the evidence actually shows

| Prompt | max_abs_error | top-1 agree | top-5 overlap |
|---|---|---|---|
| dev_capital_of_france | 0.4746 | yes | 4/5 |
| dev_once_upon_a_time | 0.4512 | yes | 5/5 |
| dev_code_snippet | 0.4868 | yes | 5/5 |
| dev_year_weather | 0.4048 | yes | 5/5 |
| holdout_hello_world | 0.4875 | yes | 5/5 |
| holdout_she_walked | 0.4857 | yes | 5/5 |
| **holdout_quick_fox** | **1.0800** | yes | 5/5 |

**Top-1 (greedy) agreement was 5/5 -- perfect -- across every single dev
and holdout prompt**, and top-5 overlap was 4-5/5 throughout. This is a
genuinely strong correctness signal for the actual dequantization and
forward-pass implementation (Checkpoints 1-2's evidence, plus this run,
give no reason to suspect a bug in `dequantize_q8_0_scalar_reference()`
or the materialization dispatch).

**What failed is the specific tolerance-DERIVATION methodology tried**:
`derived_tolerance = max(dev_max_abs_error) * 2.0` = `0.4868 * 2.0` =
`0.9735`. The holdout set's `holdout_quick_fox` prompt observed
`max_abs_error = 1.0800`, exceeding that frozen tolerance. Per this
task's explicit rule, the tolerance was NOT adjusted after seeing this.

## Honest assessment: why, and what this means

A single hand-picked multiplier (`2.0x` of the observed dev maximum)
applied to only 4 dev prompts is not a statistically grounded
tolerance-derivation method -- it has no percentile/distribution basis,
and 4 prompts is too small a sample to characterize the true tail of
the per-token quantization-error distribution. `holdout_quick_fox`
apparently exercises a harder case (a rarer/more ambiguous continuation
of "The quick brown fox") than any of the 4 dev prompts happened to
cover. This is exactly the kind of corpus-representativeness failure a
genuine dev/holdout split exists to catch -- and it caught it. That is
a correctly-functioning discipline surfacing a real gap, not a flaw in
the discipline itself.

This is **not** classified as a "negative" Checkpoint-3 result in the
strong sense (the underlying computation is not shown to be wrong --
top-1 agreement is perfect) but it **is** a genuine methodology
shortfall: the specific tolerance-derivation approach implemented here
is not yet adequate to make an honest end-to-end acceptance claim.

## What was NOT run because of this

The companion Python driver for the pinned llama.cpp Q8_0 oracle leg
(`Tools/OrcEnginePhase6/tools/phase6_llama_cpp_q8_0_oracle.py`) was
written but **not executed** -- running it against an already-failed
OrcEngine-vs-F32 tolerance gate would be building further evidence on
top of an acknowledged-insufficient foundation, not a meaningful next
step until the tolerance methodology itself is fixed.

## What a corrected approach would need (not implemented here -- a
scope decision for the next pass, not silently patched into this one)

- A meaningfully larger DEV corpus (this run's 4 prompts is too small
  to characterize a 49,152-way per-token error distribution's tail).
- A statistically grounded derivation (e.g. a high percentile of the
  observed per-token or per-prompt error distribution, not a flat
  multiplier of the single observed maximum).
- Possibly separating the tolerance by claim: this task's own framing
  distinguishes "does the SELECTED (greedy) token match" from "are the
  raw logit VALUES close" -- the former held perfectly here (5/5); a
  tolerance gate scoped to selected-token identity (rather than full
  raw-logit max-abs-error) may be the more defensible acceptance
  criterion for Phase 6's actual claim ("deterministic greedy-token
  identity," matching this project's own FL-08 framing precedent),
  with raw-logit distributional statistics reported as supporting
  evidence rather than a pass/fail gate in their own right.

None of the above was implemented in this pass -- recording it
explicitly as the reasoned next step, not applying it unilaterally
after seeing the holdout failure it would have prevented.
