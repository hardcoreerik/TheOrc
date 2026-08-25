# Phase 6 Stage 1, Gate 4 (round 7): OrcEngine canonical-GGUF compatibility status

**Diagnostics only. No loader or transformer-math change was made.**
New tool `phase6_gate4_canonical_loader_diagnostic.cpp` (new
executable, `Tools/OrcEnginePhase6/CMakeLists.txt` target added) calls
OrcEngine's EXISTING, UNMODIFIED loading path
(`index_gguf`/`map_llama_model`/`materialize_gguf_model`) and forward
path (`forward_cached_step`) against a single GGUF path with no
hardcoded expected-size/hash gate (that gate exists only in
`phase6_q8_0_comparison.cpp`, protecting the pinned committed
fixtures -- these Gate 2/3 diagnostic artifacts are not pinned
fixtures).

## Does the loader accept each canonical artifact?

**Yes, all four.** Canonical F32, canonical Q8_0 (Gate 2), Q/K-isolated
F32, Q/K-isolated Q8_0 (Gate 3) all load without exception, report the
expected config (`vocab=49152 hidden=576 n_layers=30 n_q_heads=9
n_kv_heads=3 head_dim=64 max_positions=8192`), and complete all 7
forward passes.

## Does OrcEngine produce finite results?

**Yes, all 4 artifacts x 7 prompts = 28/28 finite, no exceptions, no
malformed-logits aborts.**

## Full results (Debug build, run against all 4 legs)

Canonical F32 and Q/K-isolated F32 produced IDENTICAL logits (as
expected -- Gate 3 already proved they differ from the existing-custom
baseline in nothing but Q/K, and OrcEngine's forward path does not
consume `output.weight` differently based on its presence when tied,
nor any of the Gate-3-discovered metadata differences). Same for the
two Q8_0 variants. One table below (F32; Q8_0 is numerically close,
same argmax pattern):

| Prompt | PyTorch (ground truth) | OrcEngine on EXISTING-custom (prior evidence) | **OrcEngine on canonical/Q\|K-isolated F32** |
|---|---|---|---|
| `dev_capital_of_france` (control) | 260 | 260 | **260** |
| `dev_once_upon_a_time` (control) | 28 | 28 | **28** |
| `dev_code_snippet` (control) | 253 | 253 | **253** |
| `holdout_hello_world` (control) | 253 | 253 | **253** |
| `dev_year_weather` | 523 | 523 | **436** |
| `holdout_she_walked` | 3589 | 3589 | **260** |
| `holdout_quick_fox` | 27003 | 27003 | **28** |

## Does it agree with PyTorch? Does it agree with canonical llama.cpp?

**No, on the 3 previously-divergent prompts, in both directions.**
OrcEngine given the CANONICAL (correctly-permuted) GGUF does NOT
recover PyTorch/canonical-llama.cpp's answers (523/3589/27003) -- it
produces 436/260/28. It disagrees with PyTorch on exactly the same 3
prompts it previously agreed on when fed the EXISTING (raw) GGUF.

## Does it disagree in the pattern expected if OrcEngine currently expects raw/unpermuted Q/K?

**Partially confirmed, disclosed honestly rather than oversold.**

- `dev_year_weather`: OrcEngine-on-canonical = 436, which is EXACTLY
  the existing-custom-layout llama.cpp's (wrong) answer. Matches the
  predicted pattern precisely: OrcEngine computing on canonically-
  permuted Q/K reproduces the same wrong answer llama.cpp computes on
  RAW Q/K -- consistent with OrcEngine's RoPE/attention expecting the
  raw layout and misinterpreting a canonically-permuted one.
- `holdout_quick_fox`: OrcEngine-on-canonical = 28, which is EXACTLY
  the existing-custom-layout llama.cpp's (wrong) answer. Same pattern
  confirmed.
- `holdout_she_walked`: OrcEngine-on-canonical = 260, which matches
  NEITHER the existing-custom-layout llama.cpp answer (38734) NOR the
  canonical/PyTorch answer (3589) NOR any other value seen elsewhere in
  this remediation. **This is a third, distinct wrong answer**, not
  predicted by the simple "OrcEngine reproduces the un-permuted
  llama.cpp answer" model. Reported honestly as an open, unexplained
  discrepancy -- likely attributable to `holdout_she_walked` being a
  4-token multi-position prefill (unlike llama.cpp's single completion
  call, OrcEngine's cached-decode path accumulates attention over
  multiple RoPE-rotated positions before the final logit, so a Q/K
  layout mismatch could plausibly interact differently with prefill
  length than with a same-position single-step comparison) -- this is
  a plausible hypothesis, NOT a proven mechanism, and is not
  investigated further in this pass.

**Overall: 2 of 3 previously-divergent prompts exactly match the
predicted "OrcEngine silently misinterprets the canonical layout as if
it were raw" pattern; the third prompt shows a genuinely different,
unexplained wrong answer. In no case does OrcEngine, unmodified,
correctly consume the canonical layout.**

## Verification (Gate 4's own new C++ diagnostic tool)

- **Debug build**: builds clean, ran successfully against all 4
  artifacts (28/28 finite results, 0 exceptions).
- **Strict** (`/W4 /WX /permissive- /EHsc`): builds with **zero
  warnings**.
- **ASan** (`/fsanitize=address /EHsc` present in the actual compile
  command): built clean; ran against the canonical F32 artifact.
  **Durable evidence record:**
  - ASan executable: `Tools/OrcEnginePhase6/build_asan/Debug/
    phase6_gate4_canonical_loader_diagnostic.exe`,
    SHA-256 `dad3938512b425c16075c2e3cfe25122da0877cf35e8b03f902f58
    630bba7086`
  - Input artifact: `.orc/gate2-canonical-diagnostic/smollm2-135m-
    canonical-f32.gguf`, SHA-256 `aef7f8d471367c711a7e46365619498e0e
    51a0fa93dca8aa09005dabc19810e7` (matches the pinned canonical F32
    authority established in Gate 2)
  - Exit code: `0`
  - Raw output: `phase6_gate4_asan_run_output.txt` -- results
    byte-for-byte identical to the Debug-build run above (same
    argmax/top5 for all 7 prompts)
  - `grep -iE "error|leak|overflow|SUMMARY|AddressSanitizer|sanitizer"`
    against the raw output: **no matches** -- zero AddressSanitizer
    runtime diagnostics.

## Proposed production layout policy (NOT implemented -- requires separate reviewed specification)

This is a proposal only. No production code was changed. Any
implementation requires its own reviewed specification and Codex/Grok
review, per this round's explicit instruction to stop before
implementing.

1. **Target format**: canonical llama.cpp-compatible GGUF (official
   `convert_hf_to_gguf.py` Q/K permutation) should be the INTENDED
   external interchange format going forward, so OrcEngine-produced
   and OrcEngine-consumed GGUFs interoperate with the broader
   llama.cpp ecosystem without a silent layout mismatch. This is a
   direction proposal, not a decision -- an explicitly documented
   alternative (continuing to standardize on OrcEngine's raw/
   un-permuted convention, with `convert_real_candidate.py` as the
   canonical converter for OrcEngine-only use) remains available and
   has NOT been ruled out; it avoids ever needing a permutation seam at
   all, at the cost of external incompatibility.
2. **Smallest normalization seam**: a NEW, explicit adapter/
   materialization step -- e.g. a `qk_layout` field read from GGUF
   metadata (or inferred from a to-be-defined convention, since
   tensor names alone do not distinguish raw from canonical layout,
   see item 5) that triggers an UN-permute (canonical -> raw) transform
   on `attn_q.weight`/`attn_k.weight` during materialization, BEFORE
   weights enter the frozen `execute_cached_transformer_layer` math.
   This keeps the frozen RoPE/attention implementation completely
   unchanged -- the seam is purely a materialization-time weight
   transform, symmetric to (and reusing the same formula as) the
   `_official_permute`/`permute()` transform already proven correct in
   Gates 2/3.
3. **Frozen historical raw-layout fixtures remain reproducible**: the
   seam must be OPT-IN per artifact (see item 5's fail-closed
   requirement) -- an artifact with no layout marker, or explicitly
   marked raw, must produce byte-identical materialized weights to
   today's frozen path. This is testable directly: re-run every
   existing Phase 0-5C/6 fixture through the adapter path with the
   marker absent/raw and diff against current frozen output.
4. **Where the normalization belongs**: a NEW Phase 6 (or later)
   adapter/materialization path, NOT a change to any frozen Phase
   0-5C file or to `execute_cached_transformer_layer` itself. The
   transform is a pure input-weight relayout, analogous to Q8_0
   dequantization already being handled as a materialization-time
   concern (Phase 2's `gguf.cpp`) rather than a change to the frozen
   transformer math.
5. **Fail-closed layout selection**: tensor names alone do NOT
   distinguish raw from canonical Q/K layout (both use identical
   tensor names, `blk.N.attn_q.weight`/`attn_k.weight` -- this
   remediation's own artifacts prove that). Layout MUST be determined
   from an explicit, verified source -- e.g., a dedicated GGUF metadata
   key this project controls and writes at conversion time (NOT
   inferred from `general.quantization_version` or any field the
   official converter happens to also write, since Gate 3's audit found
   the custom and official converters diverge unpredictably on which
   metadata fields are present at all). Absence of the marker MUST
   fail closed (refuse to load, or default to the historically-proven
   raw-layout interpretation with an explicit warning) -- never guess.
6. **Required tests before any implementation**: (a) equivalence test
   proving materialized weights for a raw-marked artifact are
   byte-identical pre/post this change; (b) round-trip test proving
   permute-then-unpermute recovers the original raw weights exactly
   (already partially covered by this round's
   `test_permute_is_self_inverse_for_this_shape`); (c) a hostile test
   feeding a canonically-permuted artifact with NO layout marker,
   proving the fail-closed default activates rather than silently
   misinterpreting it (this is precisely the failure mode Gate 4 just
   measured); (d) full Phase 0-5C/6 regression re-run proving zero
   behavior change for every existing raw-layout fixture; (e) a new
   external-oracle comparison against canonical llama.cpp confirming
   the previously-divergent 3 prompts now agree end-to-end through the
   real (not diagnostic) loader.

## What this does and does NOT authorize

- Does **NOT** modify the loader, `execute_cached_transformer_layer`,
  or any frozen file.
- Does **NOT** implement the proposed policy above -- proposal only,
  stopping here per instruction, pending separate maintainer/Codex
  review.
- Does **NOT** change the external acceptance gate's status (still
  RED/FAILED on the existing pinned artifacts) or the internal
  tolerance gate's status (Gate 5, separate).
- Does **NOT** authorize `orcengine-phase6-freeze` or starting FL-08.
