# Phase 6 Stage 1 Codex remediation round 2, Gate 4: paired four-way evidence (authoritative)

This is the controlled, request-parity-fixed, full-7-prompt comparison
that supersedes the informal 3-prompt localization in
`STAGE2_STAGE3_STATUS.md`'s "Localization" section as the authoritative
account of the relationship between OrcEngine's F32/Q8_0 internal
consistency and its external agreement with the pinned llama.cpp
oracle.

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
`SharedCompletionPayloadTests.test_q8_oracle_leg_and_f32_localization_
leg_send_byte_identical_payloads`), not by visual inspection of two
separate dicts. Token-ID identity was independently re-verified against
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

## Recommended next smallest localization experiment

Per Outcome B's instruction to recommend the next smallest step without
changing transformer math in this pass: isolate `holdout_she_walked`
specifically (prompt "She walked into the", token IDs
`[8113, 13197, 618, 260]`) and compare llama.cpp's own reported
per-position log-probabilities/logits at F32 vs Q8_0 for the SAME small
set of candidate tokens (e.g. `38734`, `9612`, and OrcEngine's own
`3589`) to see whether the F32-vs-Q8_0 shift on llama.cpp's side is a
large, qualitative change (suggesting a real Q8_0-side numerical
sensitivity at this position) or a small, close-call perturbation
(consistent with ordinary quantization noise nudging an already-close
decision across a near-tie). This is a read-only, no-transformer-math-
change diagnostic using already-verified tooling (the paired script's
`llama_f32_top5`/`llama_q8_top5` fields, already captured in
`phase6_paired_four_way_report.jsonl`, are sufficient to start this
without a new run).

## What this does NOT claim

- Does not describe the top-5 evidence as full-distribution
  equivalence -- log-probability comparisons are reported (where
  computable) as diagnostic-only data with the correct full-vocabulary
  logsumexp for the matching precision (`f32_full_vocab_logsumexp`/
  `q8_full_vocab_logsumexp`, both present in the v3 evidence and
  referenced in the structured report), never approximated for a token
  outside the relevant top-5 slice.
- Does not invent or apply an external log-probability tolerance.
- Does not conclude Q8_0 is defect-free, and does not conclude Q8_0 is
  the sole cause either -- both would be overclaims this evidence does
  not support.
- Does not authorize Stage 4, Stage 5, Stage 6, transformer-math
  changes, a freeze tag, or FL-08.
