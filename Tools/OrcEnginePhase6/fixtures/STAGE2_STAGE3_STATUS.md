# Phase 6 Stage 1 Codex remediation, Stages 2-3: status -- STOPPED, genuine correctness blocker found

**This is a mandatory stop, per the remediation instructions' own
condition: "the corrected pinned Q8-vs-Q8 oracle fails."** Stage 2's
oracle-correctness work is complete, validated, and committed. Stage
3's diagnostic run correctly executed and correctly surfaced a genuine
disagreement -- reported here exactly as observed, not hidden, not
tolerance-adjusted, not corpus-expanded to dilute it. Stages 4, 5, and
6 (F32-vs-Q8 tolerance redesign, backing/resident accounting, final
validation matrix) are NOT started, per instruction, because Stage 3
did not pass.

## Stage 2: oracle correctness -- COMPLETE and VALIDATED

The original `phase6_llama_cpp_q8_0_oracle.py` had a mathematically
invalid comparison (renormalizing only OrcEngine's own top-5 logits and
comparing that to llama.cpp's full-vocabulary log-probability) plus
several unverified-identity gaps. All six Codex-identified defects were
corrected:

1. `phase6_q8_0_comparison.cpp` now computes and emits the
   FULL-VOCABULARY logsumexp for OrcEngine's Q8_0 logits at every
   compared position (`q8_full_vocab_logsumexp`, evidence
   `schema_version: 2`), at `max_digits10` round-trip precision. The
   Python driver now computes `orc_logprob = raw_orc_logit -
   full_vocab_logsumexp` exactly, never re-deriving a top-k-only
   softmax. `LogSoftmaxMathTests.test_top5_only_renormalization_
   would_have_been_wrong` constructs a synthetic example proving the
   old approach's answer differs from the correct one by >0.5 nats.
2. `llama-server.exe` identity is now hash-pinned (SHA-256
   `ae159e001d959e7a773af61e24d8e7d5d4de565865ec12a0dfd9974dc8ab7ca1`,
   first authority record for this executable -- see
   `Q8_0_FIXTURE_PROVENANCE.md`) AND its `--version` banner is checked
   (`build 10436`, `6fed9f6ff`) before any request is sent.
   `llama-server-impl.dll` (where the real request logic lives, not the
   thin `.exe` stub) is also hash-pinned.
3. Port selection uses a dynamically-bound free local port
   (`_free_local_port()`), not a fixed constant; the launched process's
   liveness is polled during health-wait so a stale unrelated server on
   the same port cannot be mistaken for this run's server; only the
   specific subprocess handle this script started is ever terminated.
4. Each prompt's tokenization is independently re-verified against
   llama-server's own `/tokenize` endpoint and asserted EXACTLY equal
   to OrcEngine's recorded token IDs before any logit comparison is
   trusted -- proven in this run: **token IDs matched exactly for all 7
   corpus prompts** (`tok_match=True` throughout both the Q8-vs-Q8 run
   and the F32 localization run below).
5. Evidence validation fails closed on: missing/old schema_version,
   missing required fields, empty evidence, F32/Q8 model config
   mismatch, vocab/logits size mismatch, empty logits, non-finite
   logits (both engines, not just Q8_0 as before), evidence
   open/write/close failure.
6. **No arbitrary tolerance was fabricated.** No prior empirically-
   derived Q8-vs-Q8 log-probability floor exists anywhere in this
   project; the only prior cross-engine tolerance
   (`LOGPROB_ATOL=0.1` in `llama_cpp_deployment_oracle.py`) was derived
   for an unrelated synthetic 2-layer toy model, not an applicable
   floor here. Per the explicit instruction not to pick a threshold
   after seeing results, the oracle's PASS/FAIL gate is restricted to
   two claims that need no numeric tolerance: token-ID identity and
   greedy (argmax) agreement. Log-probability diffs are reported as
   diagnostic evidence only.

21 targeted regression tests
(`tests/test_phase6_llama_cpp_q8_0_oracle.py`) cover: wrong server
version/build rejection, wrong server-hash rejection, wrong Q8-file-hash
rejection, evidence-hash-mismatch rejection, port isolation (genuinely
free, non-colliding), server-exits-before-health (both nonzero and
zero exit codes), health-wait timeout, missing/old-schema/incomplete
evidence rejection, the full-vocab-logsumexp correctness proof, and the
constructed top-5-only-would-be-wrong example. All 21 PASS.

## Stage 3: corrected Q8-vs-Q8 oracle run -- RAN, FAILED its own gate

Real run: `llama-server.exe` (pinned, hash-verified, version-verified)
against the real Q8_0 fixture (hash-verified), consuming the
regenerated `phase6_q8_0_f32_comparison_evidence_v2.jsonl` (schema
v2). Raw output: `phase6_llama_cpp_q8_0_oracle_run_output.txt`.
Structured per-prompt report: `phase6_llama_cpp_q8_0_oracle_report.jsonl`.

| Prompt | token IDs match | argmax agree |
|---|---|---|
| dev_capital_of_france | yes | yes |
| dev_once_upon_a_time | yes | yes |
| dev_code_snippet | yes | yes |
| dev_year_weather | yes | **NO** (orc=523, llama=436) |
| holdout_hello_world | yes | yes |
| holdout_she_walked | yes | **NO** (orc=3589, llama=38734) |
| holdout_quick_fox | yes | **NO** (orc=27003, llama=28) |

Token-ID identity: **7/7 exact match** -- both engines definitely
processed identical input. Greedy agreement: **4/7**, failing the
oracle's own pass/fail gate. Per instruction, this was NOT
"fixed" by widening a tolerance (there was no tolerance to widen on
this gate in the first place -- it's exact-match) or by expanding the
corpus to dilute the failure rate.

## Localization: this is NOT a Q8_0/Phase 6 defect

`tools/phase6_localize_f32_divergence.py` (committed): re-requests each
of the 3 disagreeing prompts from the SAME pinned `llama-server.exe`,
but against the pinned **F32** GGUF (no quantization involved at all),
with every optional sampling bias explicitly neutralized
(`repeat_penalty=1.0`, `top_k=0`, `top_p=1.0`, `min_p=0.0`,
`presence_penalty=0.0`, `frequency_penalty=0.0`, `temperature=0`), and
compares against OrcEngine's own F32 selection already recorded in the
same evidence file (`f32_selected`/`f32_top5_*` fields).

**Result: the identical 3 disagreements reproduce byte-for-byte at F32,
with the identical chosen token on both sides** (see
`phase6_f32_divergence_localization_evidence.txt`):

| Prompt | OrcEngine F32 argmax | llama.cpp F32 argmax |
|---|---|---|
| dev_year_weather | 523 | 436 |
| holdout_she_walked | 3589 | 38734 |
| holdout_quick_fox | 27003 | 28 |

Token IDs matched exactly (`tok_match=True`) in this F32 check too.
Since Q8_0 quantization is not involved in this comparison at all, and
the disagreement is EXACTLY the same tokens on both sides as the Q8_0
run, **this is a pre-existing OrcEngine-vs-llama.cpp F32 forward-path
divergence, not a defect introduced by Phase 6's Q8_0 work.** It was
not caught by earlier Phase 5A/5B/5C real-model validation because
those used a different, narrower prompt corpus; Phase 6 Checkpoint 3's
corpus is the first to exercise these specific prompts against an
independent oracle.

One structural observation, offered as a lead for whoever investigates
next, NOT as a diagnosis: in all three disagreeing cases, OrcEngine's
own logit distribution is far more PEAKED (top logit several units
above the runner-up, e.g. 13.79 vs 12.85 for `holdout_she_walked`,
corresponding to extremely high softmax confidence) than llama.cpp's,
which is comparatively FLAT (top log-probability within ~0.2-0.7 nats
of the next few candidates in most of these cases). That asymmetry --
one engine very confident, the other much less so, on the SAME input
tokens -- is consistent with a genuine forward-pass numerical
difference somewhere in the shared F32 computation (attention,
RoPE application, or RMSNorm epsilon handling are the usual suspects
for this class of divergence), not naturally explained by e.g. a
tokenization mismatch (already ruled out) or a sampling-parameter
difference (already ruled out).

## What this means for Phase 6 Stage 1

- **Checkpoints 1-2's evidence remains sound**: Q8_0 layout,
  dequantization arithmetic, and mixed-format loading are unaffected by
  this finding (it reproduces identically with zero Q8_0 tensors
  involved).
- **The F32-vs-Q8_0 internal comparison's 7/7 top-1 agreement is also
  unaffected and remains true**: OrcEngine's OWN F32 and OWN Q8_0 paths
  agree with EACH OTHER on all 7 prompts (both, apparently,
  consistently diverging from llama.cpp's independent F32/Q8_0
  computation on the same 3). That is a real, if narrower, positive
  signal about Q8_0-specific correctness (Q8_0 doesn't introduce
  additional divergence beyond whatever the pre-existing F32 gap is)
  -- it does NOT mean OrcEngine's forward pass overall matches an
  independent implementation on these particular prompts.
- **Per the remediation's stop condition, work stops here.** Stage 4
  (replacing the F32-vs-Q8 tolerance methodology) is explicitly gated
  on Stage 3 passing and is NOT started. Stage 5 (backing/resident
  accounting) and Stage 6 (final validation matrix) are likewise not
  started. `orcengine-phase6-freeze` does not exist and this status
  does not authorize creating it.
- **This finding is broader than Phase 6.** Since it reproduces at F32
  with zero quantization involved, it is arguably an OrcEngine-vs-
  llama.cpp F32 forward-path correctness question that predates and is
  independent of the Q8_0 quantization work this phase is scoped to.
  Recommending it be triaged as its own investigation (likely starting
  with RoPE/attention/RMSNorm-epsilon comparison on these 3 specific
  prompts) rather than solved inside Phase 6 Stage 1's remaining scope.
