# Phase 6 Stage 1 Codex remediation, Stages 2-3: status -- STOPPED, genuine correctness blocker found

**CORRECTION (Codex remediation round 2, see OE-ADR-043): the
"Localization" section below originally overclaimed its result.** The
original text said the 3 disagreements "reproduced byte-for-byte" and
that "both sides selected identical tokens under F32 and Q8," and
concluded this "proves" Q8_0 is not involved. That is NOT what the raw
evidence shows: the llama.cpp-selected token for `holdout_she_walked`
is `9612` at Q8_0 and `38734` at F32 -- NOT the same token. The
llama.cpp side disagrees with OrcEngine on the same 3 prompt IDs at
both precisions, but the llama.cpp-selected token itself differs
between precisions on at least this one prompt. The corrected,
defensible statement is: **a pre-existing OrcEngine-versus-llama.cpp
F32 forward-path divergence exists and blocks clean attribution of the
external Q8 failure to Q8_0 specifically; OrcEngine's own internal
F32-versus-Q8_0 behavior is consistent on this seven-prompt corpus, but
external Q8_0 correctness remains unproven.** The section below is
corrected in place to say this; the original overclaimed wording is
preserved in git history (this file's prior committed version) rather
than silently disappearing. See Gate 4's paired four-way evidence for
the corrected, full comparison across all 7 prompts.

**This remains a mandatory stop, per the remediation instructions' own
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
5. Fail-closed checks, split by which tool actually owns/performs each
   one (corrected here -- an earlier draft of this document implied a
   single undifferentiated list, which could be misread as the Python
   unit tests exercising C++-side behavior they never touch):
   - **Owned and enforced by the C++ evidence generator**
     (`phase6_q8_0_comparison.cpp`, `Tools/OrcEnginePhase2/src/gguf.cpp`):
     F32/Q8 model config mismatch, vocab/logits size mismatch, empty
     logits, non-finite logits (both engines), evidence file
     open/write/close failure, and (added in the round-2 remediation
     below) the logits-bounds-check ordering fix. These are verified by
     C++ self-tests compiled into the tool itself and by running the
     real tool against real models -- there is no separate C++ test
     BINARY for this specific tool; see the round-2 remediation section
     for how the bounds-check fix's own hostile self-test is exercised.
   - **Owned and enforced by the Python oracle/localizer**
     (`phase6_llama_cpp_q8_0_oracle.py`,
     `phase6_localize_f32_divergence.py`): server executable/impl-DLL
     hash and version verification, Q8_0/F32 GGUF hash verification,
     evidence schema_version/required-field validation, port isolation,
     process-liveness-before-health checking, token-ID identity
     verification against the live server, and (round-2) the shared
     neutral completion payload and expected-prompt-set validation.
     These, and ONLY these, are what the 35 Python regression tests
     below actually exercise.
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

35 targeted Python regression tests
(`tests/test_phase6_llama_cpp_q8_0_oracle.py`; 21 from the first
remediation pass, 14 added in round 2) cover exactly the Python-owned
checks listed above -- **not** the C++ evidence generator's own
config/logits-shape/finite-value/stream-close behavior, which has no
separate Python or C++ test binary and is instead verified by the C++
tool's own compiled-in self-test (round 2) plus running the real tool
against real models. All 35 PASS.

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

## Localization: a pre-existing F32 divergence exists, but it does not clear Q8_0 (corrected)

`tools/phase6_localize_f32_divergence.py` (committed, since further
hardened -- see the round-2 remediation section below): re-requests
each of the 3 disagreeing prompts from the SAME pinned
`llama-server.exe`, but against the pinned **F32** GGUF (no
quantization involved at all), with every optional sampling bias
explicitly neutralized, and compares against OrcEngine's own F32
selection already recorded in the same evidence file.

**Raw result** (see `phase6_f32_divergence_localization_evidence.txt`):

| Prompt | OrcEngine F32/Q8_0 selected | llama.cpp F32 argmax | llama.cpp Q8_0 argmax |
|---|---|---|---|
| dev_year_weather | 523 | 436 | 436 |
| holdout_she_walked | 3589 | **38734** | **9612** |
| holdout_quick_fox | 27003 | 28 | 28 |

Token IDs matched exactly in both the F32 and Q8_0 external legs for
all 3 prompts.

**Corrected conclusion.** OrcEngine (both its F32 and Q8_0 paths, which
agree with each other) disagrees with llama.cpp's selection on all 3
prompts at BOTH precisions -- that much is a genuine, reproducible
pre-existing OrcEngine-vs-llama.cpp F32 forward-path divergence, not
something introduced by Q8_0 quantization. **However**, the llama.cpp
side itself does not always select the SAME token across precisions:
for `holdout_she_walked`, llama.cpp's own F32 argmax (`38734`) differs
from its own Q8_0 argmax (`9612`) -- meaning llama.cpp's Q8_0 output is
not simply "the same wrong answer OrcEngine also gives, at F32
precision too." This means the evidence does **not** prove Q8_0
contributes nothing: it is consistent with (a) a pure pre-existing F32
divergence that Q8_0 quantization noise then perturbs further on
llama.cpp's side, (b) an independent Q8_0-specific issue on OrcEngine's
side that happens to still track its own F32 output, or some mixture of
both. **A pre-existing OrcEngine-versus-llama.cpp F32 forward-path
divergence exists and blocks clean attribution of the external Q8
failure. OrcEngine's internal F32-versus-Q8_0 behavior is consistent on
this seven-prompt corpus, but external Q8_0 correctness remains
unproven.** It was not caught by earlier Phase 5A/5B/5C real-model
validation because those used a different, narrower prompt corpus;
Phase 6 Checkpoint 3's corpus is the first to exercise these specific
prompts against an independent oracle.

See the round-2 remediation section below and
`PHASE6_GATE4_PAIRED_EVIDENCE.md` for the corrected, controlled,
full-corpus four-way comparison that supersedes the informal 3-prompt
localization above as the authoritative account.

## What this means for Phase 6 Stage 1

- **Checkpoints 1-2's evidence remains sound**: Q8_0 layout,
  dequantization arithmetic, and mixed-format loading are unaffected by
  this finding.
- **The F32-vs-Q8_0 internal comparison's 7/7 top-1 agreement remains
  true**: OrcEngine's OWN F32 and OWN Q8_0 paths agree with EACH OTHER
  on all 7 prompts. That is a real, narrow positive signal (Q8_0 does
  not visibly perturb OrcEngine's OWN greedy choice relative to its own
  F32) -- it does NOT mean OrcEngine's forward pass overall matches an
  independent implementation, and it does NOT mean Q8_0 is proven
  correct against that independent implementation.
- **Per the remediation's stop condition, work stops here.** Stage 4
  (replacing the F32-vs-Q8 tolerance methodology) is explicitly gated
  on Stage 3 passing and is NOT started. Stage 5 (backing/resident
  accounting) and Stage 6 (final validation matrix) are likewise not
  started. `orcengine-phase6-freeze` does not exist and this status
  does not authorize creating it.
- **The pre-existing F32 divergence is broader than Phase 6** and is
  recommended for triage as its own investigation (RoPE/attention/
  RMSNorm-epsilon comparison on these 3 specific prompts is a reasonable
  starting point) -- but per the corrected conclusion above, that
  investigation should NOT be assumed to also fully explain the Q8_0
  leg's behavior without further evidence.
