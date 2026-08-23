# Phase 6 Stage 1 Codex remediation round 2, Gate 4: paired four-way evidence (authoritative)

This is the controlled, request-parity-fixed, full-7-prompt comparison
that supersedes the informal 3-prompt localization in
`STAGE2_STAGE3_STATUS.md`'s "Localization" section as the authoritative
account of the relationship between OrcEngine's F32/Q8_0 internal
consistency and its external agreement with the pinned llama.cpp
oracle.

**This document covers the EXTERNAL blocker only.** Phase 6 Stage 1 has
a SECOND, independent, still-unresolved blocker -- the DEV-derived
internal F32-vs-Q8_0 error tolerance (`0.973504`) failing on holdout
(`holdout_quick_fox` observed `1.079983`; see
`STAGE2_STAGE3_STATUS.md`'s "Two independent Phase 6 blockers" section
and `CHECKPOINT3_STATUS.md`). Neither blocker resolves the other; both
must clear before Phase 6 Stage 1 can proceed.

## How this was produced

`tools/phase6_paired_four_way_evidence.py`, run against:
- The pinned, hash-verified `llama-server.exe` (`ae159e00...`) +
  `llama-server-impl.dll` (`77f8cf12...`), `--version` confirms
  `build 10436`/`6fed9f6ff` (return code 0 checked).
- The pinned F32 GGUF (SHA-256 `fffab10c...`).
- The pinned Q8_0 GGUF (SHA-256 `3aed955d...`).
- The schema-v3 evidence (`phase6_q8_0_f32_comparison_evidence_v3.jsonl`,
  regenerated after Gate 1's logits-bounds-check fix; results are
  numerically identical to the prior v2 run, confirming that fix
  changed no model-execution behavior).

Both the F32 and Q8_0 external legs use the exact same neutral
completion payload
(`oracle.build_completion_payload`: `n_predict=1`, `temperature=0`,
`n_probs=5`, `cache_prompt=False`, `repeat_penalty=1.0`, `top_k=0`,
`top_p=1.0`, `min_p=0.0`, `presence_penalty=0.0`,
`frequency_penalty=0.0`) -- proven identical by construction (both legs
call the one shared function; see
`SharedCompletionPayloadTests.test_request_completion_emits_the_same_
payload_on_every_call` and `SharedCompletionPayloadTests.test_
localizer_and_paired_tool_call_the_shared_request_completion_helper`
-- corrected citation; an earlier draft of this document cited a test
name, `test_q8_oracle_leg_and_f32_localization_leg_send_byte_identical_
payloads`, that was renamed during the prior remediation round and no
longer exists), not by visual inspection of two separate dicts.
Token-ID identity was independently re-verified against
the live server for every prompt on both legs before any logit was
trusted; all 7 x 2 = 14 checks passed (no tokenization mismatch
anywhere in this run). Raw run output:
`phase6_paired_four_way_run_output.txt`. Structured report:
`phase6_paired_four_way_report.jsonl`.

## The table

| Prompt | Orc F32 | Orc Q8_0 | llama F32 | llama Q8_0 | Orc internal F32/Q8 agree? | External F32 agree? | External Q8 agree? |
|---|---|---|---|---|---|---|---|
| dev_capital_of_france | 260 | 260 | 260 | 260 | True | True | True |
| dev_once_upon_a_time | 28 | 28 | 28 | 28 | True | True | True |
| dev_code_snippet | 253 | 253 | 253 | 253 | True | True | True |
| dev_year_weather | 523 | 523 | 436 | 436 | True | **False** | **False** |
| holdout_hello_world | 253 | 253 | 253 | 253 | True | True | True |
| holdout_she_walked | 3589 | 3589 | **38734** | **9612** | True | **False** | **False** |
| holdout_quick_fox | 27003 | 27003 | 28 | 28 | True | **False** | **False** |

## Reading the table

- **Orc internal F32/Q8 agree?**: 7/7. OrcEngine's own F32 and Q8_0
  paths select the identical token on every prompt in this corpus.
- **External F32 agree? / External Q8 agree?**: 4/7 on both legs, and
  **the same 3 prompt IDs fail on both legs** (`dev_year_weather`,
  `holdout_she_walked`, `holdout_quick_fox`) -- there is no prompt that
  disagrees with llama.cpp at ONE precision but agrees at the other.
- **But the llama.cpp-SELECTED TOKEN itself is not always the same
  across precisions.** For `dev_year_weather` and `holdout_quick_fox`,
  llama.cpp picks the identical token at F32 and Q8_0 (436=436,
  28=28) -- consistent with a pure pre-existing F32 divergence that Q8_0
  quantization does not meaningfully perturb further, on THOSE two
  prompts. For `holdout_she_walked`, llama.cpp's own F32 and Q8_0
  selections DIFFER from each other (`38734` vs `9612`) -- llama.cpp's
  Q8_0 output is not simply "the same wrong answer as F32" here.

## Outcome classification: **Outcome B**

Per the governing instructions' outcome taxonomy: Outcome A requires
"both external legs disagree on the same prompts" with no caveat about
selected tokens differing; Outcome B applies "if the mismatch sets OR
selected tokens differ after request parity is fixed." The mismatch
SETS are identical (same 3 prompt IDs) but the SELECTED TOKENS differ
on `holdout_she_walked` -- **this is Outcome B, not Outcome A.**

**Phase 6 remains blocked: a Q8_0-specific contribution to the observed
disagreement is possible and not ruled out by this evidence.** The
7/7 internal-consistency signal and the 2-of-3 same-selected-token
prompts are both real, but `holdout_she_walked`'s differing llama.cpp
selections across precision is a genuine differential that a pure
"pre-existing F32 bug, Q8_0 innocent" story does not fully explain.

## Recommended next smallest localization experiment (corrected)

**Corrected scope.** An earlier draft of this recommendation proposed
comparing llama.cpp's F32-vs-Q8_0 probabilities for `38734`, `9612`,
AND OrcEngine's own selected token `3589`. That third comparison is not
actually available from the existing evidence: `3589` does not appear
in llama.cpp's own top-5 at EITHER precision (see
`llama_f32_top5`/`llama_q8_top5` in `phase6_paired_four_way_report.jsonl`
for `holdout_she_walked`), so llama.cpp's log-probability for `3589` was
never captured and cannot be read off from what was already run.
Obtaining it would require a larger `n_probs` (to widen the reported
top-k until `3589` is included), a targeted single-token-logit request
mechanism, or another server run -- not something this pass's existing
data supports.

**What the existing evidence DOES support**: comparing llama.cpp's own
`38734` vs `9612` margin across precisions, since both tokens appear in
llama.cpp's own top-5 at BOTH F32 and Q8_0. Read directly from
`phase6_paired_four_way_report.jsonl` (`holdout_she_walked`):

- **llama.cpp F32**: `38734` at `-3.8839` nats, `9612` at `-4.0915`
  nats -- `38734` leads by **~0.208 nats**.
- **llama.cpp Q8_0**: `9612` at `-3.5354` nats, `38734` at `-4.3757`
  nats -- the ranking FLIPS, with `9612` now leading by **~0.840
  nats**.

**How to describe this, precisely**: Q8_0 quantization changes a
near-tied llama.cpp ranking between two candidate tokens (a ~0.208-nat
F32 margin, well within plausible quantization-noise range) into a
larger, ~0.840-nat Q8_0 margin favoring the OTHER candidate. This is
**not** described as proof of an OrcEngine Q8_0 defect (llama.cpp's own
Q8_0 leg is what shifted here, not OrcEngine's), and it is **not**
described as ordinary/harmless quantization either (a rank flip on a
near-tie is exactly the kind of behavior that would need further
localization before being dismissed as harmless). It is reported as an
open, unresolved observation.

**Recommended next smallest step**: if `3589` needs to be included in
future localization, rerun the paired script's F32/Q8_0 completion
requests for `holdout_she_walked` specifically with a larger `n_probs`
(e.g. 20-50) so `3589`'s log-probability is actually captured on the
llama.cpp side, rather than assuming it would follow the same pattern
as `38734`/`9612`. This is a read-only, no-transformer-math-change
diagnostic; it is explicitly NOT performed in this pass.

## What this does NOT claim

- Does not describe the top-5 evidence as full-distribution
  equivalence. **Corrected wording** (an earlier draft overstated this):
  the report records the required INGREDIENTS for a diagnostic log-
  probability comparison -- llama.cpp's own top-5 log-probabilities
  (`llama_f32_top5`/`llama_q8_top5`), OrcEngine's own top-5 raw logits
  (`orc_f32_top5_ids`/`orc_f32_top5_logits`/`orc_q8_top5_ids`/
  `orc_q8_top5_logits`), and the correct full-vocabulary logsumexp for
  the matching precision (`f32_full_vocab_logsumexp`/
  `q8_full_vocab_logsumexp`) -- it does NOT itself emit a precomputed
  cross-engine comparison record. A diagnostic comparison CAN be
  computed from these ingredients (exactly as `oracle._log_softmax_at`
  already does elsewhere in this codebase), and only where the
  candidate token appears in OrcEngine's own top-5 slice for that
  precision -- never approximated for a token outside it.
- Does not invent or apply an external log-probability tolerance.
- Does not conclude Q8_0 is defect-free, and does not conclude Q8_0 is
  the sole cause either -- both would be overclaims this evidence does
  not support.
- Does not authorize Stage 4, Stage 5, Stage 6, transformer-math
  changes, a freeze tag, or FL-08.
