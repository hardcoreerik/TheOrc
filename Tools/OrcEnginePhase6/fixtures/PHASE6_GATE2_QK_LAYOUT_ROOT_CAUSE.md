# Phase 6 Stage 1, Gate 2: Q/K GGUF layout incompatibility -- strongly-supported external root cause (Outcome A)

> **CORRECTED (round 7, combined Codex/Grok remediation, see
> OE-ADR-055):** the original banner below read "This resolves the
> external same-Q8/F32 disagreement blocker." That overstated the
> finding. **Root-cause identification is not the same as production
> remediation or acceptance clearance.** The external acceptance gate
> on the EXISTING pinned custom artifacts is still **RED/FAILED** --
> those artifacts still disagree with llama.cpp, unchanged, because
> nothing about the pinned custom F32/Q8_0 fixtures or the frozen
> converter has been modified. No canonical-GGUF support policy has
> been accepted or implemented for OrcEngine. What this investigation
> established is a strongly-supported explanation for WHY the existing
> artifacts disagree -- see Gate 3 (this round) for the genuine
> single-variable isolation experiment that upgrades this from
> "strongly supported" toward "causally isolated."

The custom project GGUF converter (`Tools/OrcEnginePhase0/oracle/
convert_real_candidate.py`, NOT modified by this investigation) writes
HF `q_proj.weight`/`k_proj.weight` directly into GGUF
`attn_q.weight`/`attn_k.weight` without applying the RoPE-layout
permutation the pinned llama.cpp's own official converter applies for
the `llama` architecture. A canonical GGUF pair, generated with the
EXACT pinned llama.cpp source's own official converter against the
same pinned HF source, resolves ALL THREE previously-divergent prompts
when loaded into the SAME pinned `llama-server.exe` under the SAME
controlled numerical settings used throughout this remediation. This
canonical comparison is confounded by one other known structural
difference (the canonical file omits `output.weight`; see below) --
Gate 3 (this round) isolates Q/K as the sole variable to close that
gap.

## 2A: exact layout contracts

- **Pinned llama.cpp source commit**: `6fed9f6ff7a603b124cb8c5864fca6ea879f9f99`
  (tag `b10436`; resolved via `https://api.github.com/repos/ggml-org/
  llama.cpp/git/refs/tags/b10436`, matching the abbreviated `6fed9f6ff`
  this project has cited throughout via the pinned binaries'
  `--version` banners). Fetched into the pre-existing local
  `F:\Ai\llama.cpp` checkout via `git fetch https://github.com/ggml-org/
  llama.cpp 6fed9f6ff7a603b124cb8c5864fca6ea879f9f99 --depth=1` (network
  access required and used; this checkout's own pre-existing working-
  tree state, including an unrelated uncommitted modification to
  `convert_lora_to_gguf.py`, was left untouched -- extraction was done
  via `git archive`/`git show`, never `git checkout`).
- **Converter source**: `conversion/llama.py` at that exact commit,
  class `LlamaModel(TextModel)`, `undo_permute = True` (line 33).
  Extracted verbatim via `git archive
  6fed9f6ff7a603b124cb8c5864fca6ea879f9f99` into
  `.orc/gate2-canonical-diagnostic/` (gitignored, untracked, not
  committed).
- **HF architecture class**: `LlamaForCausalLM` (per `config.json`
  `architectures`), matching `LlamaModel`'s applicability in the
  official converter (`convert_hf_to_gguf.py` dispatches by
  `architectures[0]`).
- **Q-head count / KV-head count / head dim**: 9 / 3 / 64 (identical
  values already recorded in `PHASE6_GATE4_CONFIG_PARITY.md`; unchanged
  by this investigation).
- **Exact official permutation formula** (`conversion/llama.py:173-179`):
  ```python
  @staticmethod
  def permute(weights: Tensor, n_head: int, n_head_kv: int | None):
      if n_head_kv is not None and n_head != n_head_kv:
          n_head = n_head_kv
      return (weights.reshape(n_head, 2, weights.shape[0] // n_head // 2, *weights.shape[1:])
              .swapaxes(1, 2)
              .reshape(weights.shape))
  ```
  Applied as `permute(q_proj.weight, n_head, n_head)` for Q and
  `permute(k_proj.weight, n_head, n_head_kv)` for K
  (`conversion/llama.py:258-262`). This is the standard "undo the
  GPT-NeoX-style interleaved RoPE pairing" permutation llama.cpp's
  GGUF-consuming `llama` architecture kernel expects for Q/K
  projections, converting from HF's native rotate-half pairing
  ([0:d/2] paired with [d/2:d]) to the interleaved layout llama.cpp's
  own RoPE kernel indexes into for this architecture.
- **Custom converter's exact behavior** (`Tools/OrcEnginePhase0/
  oracle/convert_real_candidate.py:170-171`, read-only, NOT modified):
  ```python
  writer.add_tensor(f"blk.{i}.attn_q.weight", get(p + "self_attn.q_proj.weight"))
  writer.add_tensor(f"blk.{i}.attn_k.weight", get(p + "self_attn.k_proj.weight"))
  ```
  Direct write, no permutation call anywhere in the file.
- **Existing custom F32 GGUF Q/K layout, verified directly (not
  inferred)**: applying the EXACT official `permute()` formula above to
  the existing custom GGUF's `blk.N.attn_q.weight`/`blk.N.attn_k.weight`
  tensors (checked at layers 0, 11, 28) produces arrays BYTE-FOR-BYTE
  IDENTICAL (`np.array_equal` = `True`) to the canonical converter's own
  output at those same tensors. **This proves, directly, that the
  existing custom GGUF's Q/K tensors are in the RAW (un-permuted) HF
  layout** -- not inferred from source reading alone.
- **Existing Q8_0 artifact provenance**: quantized from that same raw-
  layout custom F32 GGUF via the pinned `llama-quantize.exe`
  (`Q8_0_FIXTURE_PROVENANCE.md`) -- descends from the un-permuted
  layout; quantization does not alter tensor axis ordering, so the
  existing Q8_0 artifact inherits the same layout classification.

## 2B: canonical diagnostic artifacts generated

Using the exact pinned HF source (already-pinned local directory
`Tools/OrcEnginePhase0/artifacts/smollm2-135m/`, revision
`93efa2f097d58c2a74874c7e644dbc9b0cee75a2`, unchanged) and the exact
pinned llama.cpp converter extracted above:

```
python convert_hf_to_gguf.py \
  "Tools/OrcEnginePhase0/artifacts/smollm2-135m" \
  --outfile ".orc/gate2-canonical-diagnostic/smollm2-135m-canonical-f32.gguf" \
  --outtype f32
```
Result: 272 tensors (one FEWER than the existing custom GGUF's 273 --
see "Additional structural difference" below), `total_size = 538.1M`.
SHA-256: `aef7f8d471367c711a7e46365619498e0e51a0fa93dca8aa09005dabc19810e7`.

```
llama-quantize.exe \
  ".orc\gate2-canonical-diagnostic\smollm2-135m-canonical-f32.gguf" \
  ".orc\gate2-canonical-diagnostic\smollm2-135m-canonical-q8_0.gguf" \
  Q8_0
```
(same pinned `llama-quantize.exe`, hash `0e252724...`, already
established in `Q8_0_FIXTURE_PROVENANCE.md`). Result: 272/272 tensors,
`model size 513.13 MiB -> quant size 136.40 MiB`. SHA-256:
`dbf0d1f31d3afd0864bb02a916b7e3762728fd616eebd34bd6021c7497def219`.

Both `.gguf` files are UNTRACKED (`.orc/` is gitignored) and were never
used to overwrite the existing Phase 6 F32/Q8 fixtures.

**Direct verification the canonical artifact's Q/K tensors equal the
official permuted form and differ from raw HF**: at layers 0, 11, and
28, `permute(existing_custom_Q_or_K, n_head, n_head_or_kv)` exactly
equals the canonical GGUF's corresponding tensor (`np.array_equal` =
`True` in every case checked); the canonical tensor is NOT equal to the
existing custom (un-permuted) tensor directly (`np.array_equal` =
`False`). Both facts independently confirmed, not assumed from the
non-identity of the permutation formula alone.

**Follow-up (Codex review finding): "only Q/K changed" was asserted
without an exhaustive check.** The original pass here checked Q/K
specifically at 3 layers and separately confirmed the tied-embedding
byte-identity, but never systematically compared every OTHER shared
tensor between the two files -- so the causal claim that Q/K is the
SOLE variable between the "existing-custom" and "canonical" comparison
legs was not fully verified, only plausible. Closed via
`tools/phase6_gate2_full_tensor_diff.py`
(`phase6_gate2_full_tensor_diff_output.txt`): compares ALL 272 shared
tensors between the two GGUFs (Q/K compared post-permutation using the
already-proven transform, everything else compared raw,
byte-for-byte). **Result: zero mismatches beyond Q/K.** Every one of
the other 272 shared tensors (V/O projections, FFN gate/up/down,
attention/FFN norms, `token_embd.weight`, `output_norm.weight`) is
byte-for-byte identical between the two files. The only structural
difference is the already-disclosed `output.weight` presence/absence,
which is itself accounted for (byte-identical to `token_embd.weight`
in the file that has it; the file that lacks it relies on llama.cpp
reusing `token_embd.weight` for the tied head at runtime instead). **The
"only Q/K changed" claim is now comprehensively verified, not assumed.**

**Additional structural difference found (not the focus of this
investigation, recorded for completeness)**: the canonical converter
does NOT write a separate `output.weight` tensor at all when
`tie_word_embeddings=true` (272 tensors, vs. the custom converter's 273
-- which writes both `token_embd.weight` and a byte-identical duplicate
`output.weight`, per the tied-weight correction in
`PHASE6_GATE4_CONFIG_PARITY.md`). This means llama.cpp, when loading
the canonical GGUF, materializes the output head by reusing
`token_embd.weight` at runtime rather than reading a stored duplicate.
Since the existing custom GGUF's duplicate is byte-identical to
`token_embd.weight`, this structural difference does not itself
introduce a numerical discrepancy -- but it is a second, independent
divergence from the official converter's output, disclosed for
completeness.

## 2C: controlled comparison -- ROUND 7 UPDATE: now the FULL fail-closed 7-prompt corpus (supersedes the round-6 4-prompt/final-position-only pass below)

**Round 7** (`phase6_gate2_canonical_layout_check.py`, rewritten to
fail closed on identity -- see the module docstring for the exact
verification list): ran the SAME pinned, hash-verified
`llama-server.exe` against all FOUR legs (existing-custom F32,
existing-custom Q8_0, canonical F32, canonical Q8_0) across the FULL
fixed 7-prompt corpus (not just 3 divergent + 1 control), under the
same controlled settings (`--cache-type-k f32 --cache-type-v f32
--flash-attn off`). Every GGUF's SHA-256, the server's SHA-256 +
`--version` banner, and the impl DLL's SHA-256 were verified BEFORE any
server launched. Token-ID identity re-verified live via `/tokenize` for
every prompt on every leg -- exact match throughout. Raw console output
and full structured JSON (all identities, settings, and results):
`phase6_gate2_canonical_layout_check_report.json`.

| Prompt | PyTorch/OrcEngine (ground truth) | llama.cpp existing-custom F32 | llama.cpp existing-custom Q8_0 | **llama.cpp canonical F32** | **llama.cpp canonical Q8_0** |
|---|---|---|---|---|---|
| `dev_capital_of_france` (control) | 260 | 260 | 260 | **260** | **260** |
| `dev_once_upon_a_time` (control) | 28 | 28 | 28 | **28** | **28** |
| `dev_code_snippet` (control) | 253 | 253 | 253 | **253** | **253** |
| `holdout_hello_world` (control) | 253 | 253 | 253 | **253** | **253** |
| `dev_year_weather` | 523 | 436 | 436 | **523** | **523** |
| `holdout_she_walked` | 3589 | 38734 | 9612 | **3589** | **3589** |
| `holdout_quick_fox` | 27003 | 28 | 28 | **27003** | **27003** |

**Every single previously-divergent prompt now agrees with PyTorch and
OrcEngine once llama.cpp is given the canonically-permuted GGUF, and
every previously-agreeing control prompt continues to agree, across the
COMPLETE 7-prompt corpus (not a 4-prompt subset).** This is a stronger,
fully reproducible, hash-bound version of the round-6 result below --
the round-6 result is retained for its historical record but is
superseded by this table as the current authority.

### Round 6 original pass (4 prompts, final position only -- historical, superseded above)

Ran llama.cpp (pinned server, controlled settings
`--cache-type-k f32 --cache-type-v f32 --flash-attn off`, matching
every other run in this remediation) against BOTH the canonical F32 and
canonical Q8_0 GGUFs, for the 3 previously-divergent prompts plus 1
agreeing control (`dev_capital_of_france`), using the exact same token
IDs as every other evidence file in this project. Token-ID identity
re-verified live via `/tokenize` for every prompt on both legs -- exact
match throughout (`tok_match=True`).

| Prompt | PyTorch | OrcEngine (existing) | llama.cpp existing-custom F32 | llama.cpp existing-custom Q8_0 | **llama.cpp canonical F32** | **llama.cpp canonical Q8_0** |
|---|---|---|---|---|---|---|
| `dev_capital_of_france` (control) | 260 | 260 | 260 | 260 | **260** | **260** |
| `dev_year_weather` | 523 | 523 | 436 | 436 | **523** | **523** |
| `holdout_she_walked` | 3589 | 3589 | 38734 | 9612 | **3589** | **3589** |
| `holdout_quick_fox` | 27003 | 27003 | 28 | 28 | **27003** | **27003** |

Raw run output and structured JSON (historical):
`phase6_gate2_canonical_llama_cpp_results.json`.

### First divergence position

Per instruction, this pass measured the FINAL prompt position only
(the position every other Phase 6 comparison in this project reports
on, and the position the internal tolerance gate itself measures) --
**a full per-prefix-position sweep (finding the exact first token
position at which the existing-custom-layout llama.cpp run first
diverges from PyTorch/OrcEngine, position by position through the
prompt) was NOT performed in this pass.** This is disclosed as a
limitation, not silently omitted: the final-position result above is
sufficient to establish Outcome A (the canonical artifact resolves the
disagreement), but does not by itself pinpoint whether the existing-
custom-layout divergence first appears at position 0 or only develops
after several tokens of attention history -- a natural, low-cost next
step given the machinery already built here.

## Outcome classification: **Outcome A**

> Canonical llama.cpp aligns with PyTorch while existing-custom
> llama.cpp does not. This supports a custom-GGUF tensor-layout
> incompatibility as the external root cause.

This is exactly what was measured: canonical llama.cpp (both F32 and
Q8_0) agrees with PyTorch and OrcEngine on all 4 prompts tested,
including all 3 that disagreed under the existing custom layout.
**This directly implicates the missing Q/K RoPE-layout permutation in
`convert_real_candidate.py` as the strongly-supported external root
cause** -- a controlled before/after comparison (same server, same
settings, same prompts, same tokens) that flips the result. This is
**not yet a genuinely isolated single-variable proof**: the compared
files also differ in `output.weight` presence (canonical omits it;
see "Additional structural difference" above), so "only Q/K changed"
describes the two files' TENSOR VALUES (verified exhaustively, see the
Codex follow-up above) but not their complete structural identity.
Because the differing `output.weight` is byte-identical to
`token_embd.weight` wherever present, and llama.cpp materializes the
tied head from `token_embd.weight` when `output.weight` is absent, this
confound is judged numerically inert -- but "judged inert" is a
narrower claim than "proven irrelevant by direct single-variable
experiment." See Gate 3 (this round) for that experiment.

## What this does and does NOT authorize

- **Does NOT authorize modifying `convert_real_candidate.py`** (an
  explicit instruction for this pass) or any other frozen Phase 0-5C
  file. `convert_real_candidate.py` remains untouched; it is evidence
  under investigation, and any production decision about supporting the
  canonical/permuted Q/K layout requires a separate, reviewed
  specification.
- **Does NOT resolve the internal Q8_0 tolerance blocker.** That
  investigation (Gate 3, `PHASE6_GATE6_INTERNAL_TOLERANCE_LOCALIZATION.md`)
  is unaffected -- it concerns OrcEngine's own F32-vs-Q8_0 behavior on
  the EXISTING (un-permuted) artifacts, a completely separate question
  from llama.cpp's external interpretation of the GGUF layout. The
  internal tolerance gate remains **FAILED** (`0.973504` vs `1.079983`),
  unchanged.
- **Does NOT prove OrcEngine's own RoPE application is "more correct"
  in any general sense** -- it proves OrcEngine's existing, frozen RoPE
  implementation is SELF-CONSISTENT with the existing (un-permuted)
  custom GGUF layout it was built to consume (both use the rotate-half/
  non-interleaved convention throughout), which is exactly why
  OrcEngine already agreed with PyTorch on these prompts without any
  change. The finding here is about llama.cpp's GGUF-layout
  EXPECTATION for the `llama` architecture, not about which convention
  is "correct" in the abstract.
- **Does not itself change whether OrcEngine can load the canonical
  GGUF.** Whether OrcEngine's existing loader accepts the canonical
  artifact without production changes was not tested in this pass (Gate
  2C item 5, "if the existing loader accepts them without production
  changes") -- given the scope already covered, this is deferred and
  disclosed as a limitation, not silently skipped. **Addressed in round
  7, Gate 4** -- see `PHASE6_GATE4_ORCENGINE_CANONICAL_COMPATIBILITY.md`.

## Status as of round 7 (combined Codex/Grok remediation)

- **External acceptance gate on the EXISTING pinned custom artifacts:
  still RED/FAILED.** Unchanged, because no frozen file or pinned
  fixture was modified.
- **Root cause: strongly-supported hypothesis, upgraded toward isolated
  in Gate 3 (round 7)** -- see
  `PHASE6_GATE3_QK_ISOLATION.md` for the genuine single-variable
  experiment and its classification.
- **Internal tolerance gate: still FAILED** (`0.973504` vs `1.079983`),
  unaffected by any of this investigation -- see
  `PHASE6_GATE6_INTERNAL_TOLERANCE_LOCALIZATION.md` (round 7 per-block
  addendum).
- **No canonical-GGUF support policy has been accepted or implemented
  for OrcEngine.** Any such change requires a separate, reviewed
  specification -- see the round-7 Gate 4 proposal (not implemented).
- **Phase 6: not freeze-ready.** `orcengine-phase6-freeze` does not
  exist.
