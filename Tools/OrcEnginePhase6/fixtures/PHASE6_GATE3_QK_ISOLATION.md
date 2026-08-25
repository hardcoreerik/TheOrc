# Phase 6 Stage 1, Gate 3 (round 7): genuine single-variable Q/K isolation

**Upgrades Gate 2's classification from "strongly-supported primary
cause" to "causally isolated" for the specific external Q8/F32-vs-
llama.cpp disagreement this remediation has tracked.** This does NOT
resolve the internal tolerance blocker (Gate 5, separate) and does NOT
authorize any production change.

## Why Gate 2 alone was not sufficient

Gate 2's canonical-vs-existing-custom comparison (OE-ADR-053/054/056)
was confounded: the canonical GGUF omits `output.weight` entirely
(argued numerically inert, not proven irrelevant by direct experiment)
and, as this gate's own metadata audit newly discovered, ALSO differs
in 11 metadata fields the official converter writes that the custom
converter does not (`general.basename`, `general.languages`,
`general.license`, `general.quantization_version`, `general.size_label`,
`general.type`, `llama.attention.key_length`, `llama.attention.value_
length`, `llama.vocab_size`, `tokenizer.ggml.add_space_prefix`,
`tokenizer.ggml.unknown_token_id`), is missing 1 field the custom
converter writes (`tokenizer.ggml.add_eos_token`), and has a
differently-formatted `general.name` (`'SmolLM2-135M'` vs.
`'Smollm2 135m'`). None of these were previously disclosed -- this
audit is the first exhaustive metadata comparison performed. All are
judged non-computational (license/quantization_version/size_label/
basename/languages/type/name are pure descriptive metadata;
key_length/value_length/vocab_size are informational restatements of
values already independently verified matching via `llama.rope.
dimension_count`/tensor shapes; the tokenizer fields are irrelevant
here because every comparison in this project bypasses tokenization
entirely, feeding fixed token IDs and verifying `/tokenize` agreement
live) -- but "judged non-computational" is still not the same as a
genuine single-variable experiment.

## Audit tool: `phase6_gate3_qk_isolation.py audit`

Reads BOTH GGUFs via `gguf.GGUFReader` (existing tooling, not manual
offset parsing). For all 30 layers, applies the official pinned
`permute()` formula (same formula, quoted and verified at 3 layers in
Gate 2) to the existing-custom `attn_q.weight`/`attn_k.weight` and
compares the result byte-for-byte against canonical's corresponding
tensor -- **60/60 verified, 0 mismatches** (generalizes Gate 2's
3-layer spot check to the complete model). Compares every other shared
tensor raw -- **212/212 identical, 0 mismatches**. Enumerates every
metadata field on both sides and classifies each as identical/
differing/only-on-one-side (see table above -- the full result is in
`.orc/gate3-qk-isolation/audit_report.json`, untracked, hash-bound to
both input files). Independently re-verifies `output.weight` ==
`token_embd.weight` (byte-identical, `True`).

## Option A: strict Q/K-only diagnostic artifact

`phase6_gate3_qk_isolation.py build` constructs a NEW GGUF
(`.orc/gate3-qk-isolation/smollm2-135m-qk-only-permuted.gguf`,
untracked) by reading the EXISTING CUSTOM F32 GGUF and re-writing it
via `gguf.GGUFWriter`: every metadata key/value copied verbatim
(generic `add_key_value`, not a hand-picked subset), every tensor
copied byte-for-byte EXCEPT `blk.N.attn_q.weight`/`blk.N.attn_k.weight`
for all 30 layers, which get the official `permute()` applied.

**Programmatic self-verification (not merely asserted):** re-reads the
just-written file and compares it against the existing-custom baseline
-- every one of the 213 non-Q/K tensors byte-identical, all 60 Q/K
tensors equal the expected permuted form, all 21 real metadata fields
identical, tensor-name sets identical. **Result:
`isolation_verified: true`, 0 tensor mismatches, 0 metadata
mismatches** (`.orc/gate3-qk-isolation/build_report.json`). By
construction and by verification, **Q/K permutation is the ONLY
variable** between this artifact and the existing-custom baseline --
`output.weight` is present and unchanged, every metadata field is
unchanged.

Quantized to Q8_0 via the same pinned `llama-quantize.exe`
(SHA-256 `0e252724...`, already established in
`Q8_0_FIXTURE_PROVENANCE.md`) -- confirmed the identical binary ran
(hash-checked before use).

Artifact hashes:

| Artifact | SHA-256 |
|---|---|
| existing-custom F32 | `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac` |
| existing-custom Q8_0 | `3aed955db7e8e7e73e12a05964ad9efb79cef77a895120a77475d7743609d398` |
| **Q/K-isolated F32** (new) | `9de557a9e3705bbd737e07a7f3afc2ed3bf8942462ccdd9bc9f40815290bb90f` |
| **Q/K-isolated Q8_0** (new) | `ad7df9a13d6c37d916e5c5a592a7c1951e7608df6b144de8511d29f70c3ca50c` |
| canonical F32 (Gate 2, for reference) | `aef7f8d471367c711a7e46365619498e0e51a0fa93dca8aa09005dabc19810e7` |

## Controlled comparison: existing-custom vs. Q/K-isolated, full 7-prompt corpus

Same pinned, hash-verified `llama-server.exe` (build 10436/commit
6fed9f6ff), same controlled settings (`--cache-type-k f32
--cache-type-v f32 --flash-attn off`), all 4 legs, all 7 prompts.
Raw JSON: `phase6_gate3_qk_isolation_llama_cpp_results.json`.

| Prompt | PyTorch/OrcEngine | existing-custom F32 | existing-custom Q8_0 | **Q/K-isolated F32** | **Q/K-isolated Q8_0** |
|---|---|---|---|---|---|
| `dev_capital_of_france` (control) | 260 | 260 | 260 | **260** | **260** |
| `dev_once_upon_a_time` (control) | 28 | 28 | 28 | **28** | **28** |
| `dev_code_snippet` (control) | 253 | 253 | 253 | **253** | **253** |
| `holdout_hello_world` (control) | 253 | 253 | 253 | **253** | **253** |
| `dev_year_weather` | 523 | 436 | 436 | **523** | **523** |
| `holdout_she_walked` | 3589 | 38734 | 9612 | **3589** | **3589** |
| `holdout_quick_fox` | 27003 | 28 | 28 | **27003** | **27003** |

**Every one of these numbers is identical to the canonical-artifact
result from Gate 2** (which had the output.weight/metadata confounds)
-- but this time the ONLY thing that changed, verified by construction
and by re-read, was the Q/K permutation.

## Classification: **causally isolated** (upgraded from "strongly-supported primary cause")

> "If a Q/K-only change resolves every disagreement while all other
> data remain fixed, the missing Q/K permutation is causally isolated."

That is exactly what was measured. All 3 previously-divergent prompts
flip to agreement with PyTorch/OrcEngine; all 4 controls remain
agreeing; the artifact that produced this flip differs from the
existing-custom baseline in NOTHING but the Q/K permutation, verified
tensor-by-tensor and field-by-field, not assumed.

**What remains NOT tested by this experiment:**
- A full per-prefix-position sweep (still only final-position, same
  limitation carried from Gate 2).
- Whether OrcEngine's own loader accepts either the canonical or the
  Q/K-isolated GGUF -- Gate 4, this round.
- The internal Q8_0 tolerance blocker (`0.973504` vs `1.079983`) is
  completely unaffected -- Gate 5, this round.

## What this does and does NOT authorize

- Does **NOT** authorize modifying `convert_real_candidate.py` or any
  other frozen file. Both new artifacts are untracked, under
  `.orc/gate3-qk-isolation/`, never committed, never overwriting any
  existing Phase 6 fixture.
- Does **NOT** clear the external acceptance gate on the EXISTING
  pinned custom artifacts -- those are unmodified and still disagree
  with llama.cpp, unchanged. Root-cause isolation is not production
  remediation.
- Does **NOT** authorize creating `orcengine-phase6-freeze` or starting
  FL-08.
