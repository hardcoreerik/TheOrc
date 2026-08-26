# FL-08 — Model Compatibility Profiler (research charter)

- **Worktree:** `F:\Ai\OrchestratorIDE-fringelab-fl08`
- **Branch:** `research/orcengine-fl08-model-compat-profiler`
- **Base:** `orcengine-phase5c-freeze` (`a93e6c6e98a4b9b86f161a4d6695401a7980965e`) -- the same
  frozen Phase 1-5C foundation Phase 6 itself builds on, but this
  branch does NOT depend on, branch from, or modify the
  `feat/orcengine-phase6-quantization` branch or any of its commits.
- **Independent of production paths and frozen Phase 0-5C source**:
  this experiment adds new files only, under `Tools/FringeLab/
  FL08_ModelCompatibilityProfiler/` and this `.orc/fringelab/` evidence
  directory. No frozen file is modified.

## Hypothesis

> OrcEngine can distinguish at least two artifacts with identical
> high-level architecture and tensor names but different tensor-layout
> semantics, explain the mismatch, and fail closed when evidence is
> insufficient.

## Artifacts used (referenced by hash/provenance, NOT committed)

All four are the exact, already-hash-verified artifacts from Phase 6
round 7 (Gates 2/3), referenced here by absolute path (outside this
worktree, on the same machine) and SHA-256 -- never copied into this
worktree, never committed:

| Role | Path (external to this worktree) | SHA-256 |
|---|---|---|
| existing-custom raw-HF F32 | `F:/Ai/OrchestratorIDE-phase2-gguf/Tools/OrcEnginePhase0/artifacts/smollm2-135m.gguf` | `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac` |
| existing-custom raw-HF Q8_0 | `F:/Ai/OrchestratorIDE-phase6-quantization/Tools/OrcEnginePhase6/fixtures/smollm2-135m-q8_0.gguf` | `3aed955db7e8e7e73e12a05964ad9efb79cef77a895120a77475d7743609d398` |
| canonical llama.cpp F32 | `F:/Ai/OrchestratorIDE-phase6-quantization/.orc/gate2-canonical-diagnostic/smollm2-135m-canonical-f32.gguf` | `aef7f8d471367c711a7e46365619498e0e51a0fa93dca8aa09005dabc19810e7` |
| canonical llama.cpp Q8_0 | `F:/Ai/OrchestratorIDE-phase6-quantization/.orc/gate2-canonical-diagnostic/smollm2-135m-canonical-q8_0.gguf` | `dbf0d1f31d3afd0864bb02a916b7e3762728fd616eebd34bd6021c7497def219` |

Re-verified by this charter's own hashing pass immediately before
writing this table (not copied from the Phase 6 doc unverified).

**5th artifact -- deliberately tampered synthetic fixture**:
a small, purpose-built, COMMITTED synthetic GGUF
(`fixtures/tampered_ambiguous.gguf`, ~4.8KB, NOT a real model) that
declares `llama.block_count=2` but only writes layer 0's tensors --
an internal structural CONTRADICTION (see Gate 5 for exact
construction). > **Vocabulary correction (round 2, Grok finding):**
this artifact's proof is a structural layer-count contradiction,
classified `INVALID` -- Layer 3 (Q/K dialect) never runs on it, so it
does NOT exercise the `AMBIGUOUS` classification path despite its
filename. `AMBIGUOUS` is exercised separately (a well-formed artifact
with no paired reference, or two artifacts whose Q/K matches directly
without a declared reference layout -- `SAME_LAYOUT_UNKNOWN`). Small
enough to commit safely (unlike the ~500MB-650MB real fixtures).

## Scope boundary

- Read-only against all 5 artifacts. Never writes, patches, or
  normalizes any input file.
- Does not call into, import, or link against `Tools/OrcEnginePhase6/*`
  or the `feat/orcengine-phase6-quantization` branch in any way --
  the Q/K permutation formula is re-implemented here from the same
  pinned llama.cpp source citation (independently re-derivable, not a
  cross-worktree dependency), consistent with "independent of
  production paths."
- No production OrcEngine loader code is touched.
- No Avalonia/UI work.
- No other model-family adapters (Gemma, Mistral, etc.) -- Llama/Q8_0
  only, per Gate 4's explicit narrow scope.

## Acceptance gate for this charter

Proceeds to Gate 5 (prototype) only because Gates 1-4 (prior art,
schema, recognition strategy) below establish a bounded, achievable,
non-duplicative scope. If Gates 1-4 had found the concept fully
prior-arted with no defensible combination, Gate 5 would not have been
attempted (see `PRIOR_ART.md` Gate 1 question 7 for the specific
defensible combination this charter targets).

## Round 2 remediation: results and conclusions

Triggered by a combined Codex authority review and an independent Grok
Double Check (report:
`gdc-orchestratoride-fringelab-fl08-research-orcengine-fl08-model-
compat-profiler-fl08-profiler-charter.md`, project root, untracked,
preserved unmodified). Verdict was **FIX BEFORE MERGE/FREEZE**: the
round-1 prototype could emit `VERIFIED_COMPATIBLE`/
`execution_authorization=true` from an unlabeled direct tensor match
between two artifacts that happened to share a layout (proving nothing
about which ABSOLUTE layout that was), trusted an unvalidated paired
reference outright, and validated Q/K shapes against the wrong GGUF
axis for non-square tensors.

### What is now proven

- **The false-authorization defect is closed.** A direct (non-
  permuted) Q/K match between artifact and reference now classifies as
  `SAME_LAYOUT_UNKNOWN`, never `RAW_HF`/`CANONICAL_LLAMA_CPP`, and
  never authorizes execution. Confirmed on real Phase 6 artifacts
  (Cases A-D below) and on 6 dedicated regression tests
  (`TestGate1DirectMatchNeverAbsolute`).
- **The two permutation-direction hypotheses remain valid and are
  UNCHANGED** -- `permute(X) == Y` still proves X is raw-form/Y is
  canonical-form, because `official_permute()` is the externally fixed,
  independently-verified transform (not something inferred from
  reference identity). This is the part of the round-1 design the
  review explicitly confirmed correct and asked to be preserved.
- **The reference artifact is now validated through the identical
  Layer 1+2 path as the primary artifact** (`validate_artifact()`, one
  shared function, no duplicated logic), hash-bound into the profile's
  new `reference` section, and a missing/malformed reference produces a
  structured `INVALID` result -- never an uncaught exception.
- **Pair identity is now a separate, explicit check** from the Q/K
  relationship: all non-Q/K tensors (V, attention-output, norms, FFN,
  embedding, and the output head when present on both sides) must be
  byte-identical between artifact and reference for `pair_identity` to
  reach `VERIFIED`. A tampered V/FFN/norm tensor is caught even when
  Q/K still matches -- confirmed by 3 dedicated adversarial tests.
- **The Layer 2 shape-contradiction check now validates the LOGICAL
  axis** (`tensor.data.shape`, via the new `logical_shape()`/
  `logical_last_dim()` helpers), not GGUF's native on-disk dimension
  order. Confirmed against a non-square GQA fixture
  (`n_head=4 != n_head_kv=2`, so Q rows != K/V rows) that the round-1
  square synthetic fixtures could not have caught this bug with.
  Regenerating real evidence against the actual SmolLM2-135M artifacts
  surfaced a SECOND instance of the same class of bug this fix
  resolves: `token_embd.weight` is itself Q8_0-quantized in the real
  Q8_0 artifacts (confirmed: 612 packed bytes = 576/32*34), and the
  original last-dim check compared that packed byte count directly
  against `hidden` -- `logical_last_dim()` now decodes packed Q8_0
  width back to logical elements before comparing.
- **`execution_authorization` is now a single centrally-computed
  invariant** (`layer5_decision`'s `authorization_conditions` dict) --
  true only when container/architecture/reference/pair-identity/Q/K-
  numerical-verification/tensor-completeness/per-layer-consistency/
  target-match/no-normalization-needed/no-unresolved-ambiguity ALL
  hold. Confirmed false under all 9 previously-risky conditions the
  review named, by 9 dedicated tests plus the specific 3 regressions
  the remediation instruction required (identical raw artifacts,
  reference with a changed non-Q/K tensor, zero-layer/vacuous
  artifact) -- all three now end non-authorizing.
- **Packed Q8_0 comparison is validated before being trusted**: same
  quantization type on both sides, packed row width equal to
  `(hidden/32)*34`, matching packed shapes -- confirmed on a non-square
  GQA Q8_0 fixture with distinguishable per-block scales, and rejected
  on malformed row width, mixed F32/Q8_0 encodings, and a one-byte
  packed-row mutation (5 dedicated tests).
- **The normalization plan generator now refuses the reverse
  (canonical -> raw-HF) direction explicitly** rather than emitting the
  same operation names in both directions -- a genuine inverse formula
  was not derived or round-trip-tested in this experiment, so no
  reverse plan is emitted (`build_plan` returns `(None, reason)` with a
  human-readable refusal). The forward (raw-HF -> canonical) plan is
  unaffected and still emitted with corrected evidence text.

### What remains ambiguous / out of scope

- **Single-artifact (unpaired) layout inference** remains explicitly
  unimplemented -- a well-formed artifact with no reference still
  reports `UNKNOWN`/`AMBIGUOUS`, never a guess from tensor names.
- **Tokenizer/general metadata differences alone** (with tensors
  identical) do not flip `pair_identity` -- pair identity in this
  experiment is proven from tensor CONTENT, not metadata bookkeeping;
  see `test_reference_with_different_tokenizer_metadata_documented_
  behavior`. This is a disclosed scope boundary: a future round could
  additionally require metadata-fingerprint agreement, but that was
  not implemented here.
- **The reverse (canonical -> raw-HF) normalization transform** has no
  proven formula in this experiment. `build_plan` fails closed rather
  than guess.
- **Cross-encoding pair identity** (e.g. an F32 artifact vs. a Q8_0
  reference) is not attempted -- byte comparison cannot prove identity
  across encodings without a real provenance/hash chain, which this
  prototype does not implement; such pairs report `pair_identity:
  UNVERIFIED`.

### This remains a research prototype

No production loader, frozen file, or Avalonia UI was touched.
Production integration has not been authorized and was not attempted.
The corrected Cases A-E results, artifact hashes, unit-test counts, and
lane results are in the round-2 commit's own report (see the session's
final report to the user) and in `fixtures_results/`.
