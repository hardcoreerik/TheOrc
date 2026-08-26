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

> **Correction (round 3, Grok finding #3):** the bullet below originally
> read "Confirmed on real Phase 6 artifacts (Cases A-D below)." That
> overstated the evidence -- committed Cases A-D are directional
> permute successes (`RAW_HF`/`CANONICAL_LLAMA_CPP` via the permutation
> hypotheses), never an unlabeled direct-match rerun on real identical
> artifacts, and no "Cases A-D" table exists in THIS document (the
> tables live in `fixtures_results/`). The direct-match closure is
> proven by the synthetic `TestGate1DirectMatchNeverAbsolute` suite
> plus direct code inspection, not by a real-artifact direct-match run.
> Corrected wording below.

- **The false-authorization defect (for the specific direct-match
  vector Grok's round-1 review found) is closed, proven by 6 dedicated
  synthetic regression tests** (`TestGate1DirectMatchNeverAbsolute`) --
  a direct (non-permuted) Q/K match between artifact and reference now
  classifies as `SAME_LAYOUT_UNKNOWN`, never `RAW_HF`/
  `CANONICAL_LLAMA_CPP`, and never authorizes execution.
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
> **Superseded (round 3):** the bullet immediately below claimed
> tokenizer/general metadata differences alone do not flip
> `pair_identity`. That was true of round 2's implementation (no
> metadata comparison existed at all) but is explicitly REVERSED by
> round 3, Gate 1C -- execution-affecting metadata and the entire
> `tokenizer.*` namespace ARE now required to agree for `VERIFIED` pair
> identity. General provenance/bookkeeping fields (name, license,
> quantization_version, etc.) remain benign and do not block it -- see
> the "Execution-relevant metadata policy" table above for the exact,
> current classification.

- ~~Tokenizer/general metadata differences alone (with tensors
  identical) do not flip `pair_identity`~~ -- preserved as historical
  round-2 text, no longer accurate; see the correction immediately
  above.
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

## Round 3 remediation: results and conclusions

Triggered by a combined Codex authority review and an independent Grok
Double Check (report:
`gdc-orchestratoride-fringelab-fl08-research-orcengine-fl08-model-
compat-profiler-fl08-round2-remediation.md`, project root, untracked,
preserved unmodified). Verdict was **FIX BEFORE MERGE/FREEZE**: Grok's
top finding was that `verify_pair_identity()` could still report
`VERIFIED` and authorize execution when `output.weight` is present on
only one side and never compared -- live in round-2's own committed
Case B evidence (272 vs. 273 tensors). Codex independently reproduced
the SAME `output.weight` defect plus 5 additional live defects. See
the authoritative defect inventory table at the end of this document
(added round 4, correcting round 3's inconsistent "five
additional"-followed-by-six-names / dual Grok+Codex attribution for
`OUTPUT_ONLY_ONE_SIDE`).

### Output-head asymmetry policy (Gate 1B)

`output.weight` present on exactly one side is now handled explicitly,
never silently skipped:

- **Both absent:** shared tied representation, does not block pair
  identity.
- **Both present:** compared for exact type, logical shape, and
  numerical value equality like any other non-Q/K tensor; a mismatch
  is `UNVERIFIED`.
- **Present on exactly one side:** `UNVERIFIED` *unless* ALL of the
  following are proven (`_tied_output_proof()`): (1) same GGML tensor
  type as that side's own `token_embd.weight`; (2) same logical shape;
  (3) numerically identical contents (`np.array_equal`, exact value
  equality -- for the F32 data this project's real and synthetic
  fixtures contain, this is equivalent to byte-identical; no NaN or
  signed-zero content is present in any fixture used, so this
  distinction does not currently matter in practice, but the precise
  claim is "exact array equality," not "byte-identical bytes on disk,"
  and evidence text is worded that way going forward); (4)
  `token_embd.weight` ITSELF already passed the non-Q/K pair-identity
  comparison (a doubly-tampered artifact where both the embedding and
  a "tied" output are altered together cannot exploit the proof); (5)
  evidence explicitly records which side had the tied duplicate and
  which side's tied head is materialized from `token_embd.weight` at
  runtime instead.

> **Correction (round 4, Grok round-3 review finding, corroborated by
> the user):** the paragraph below originally claimed Case B's
> tied-output safety was "confirmed by regenerating real evidence."
> That overstated what was executed. Case B's OWN
> `pair_identity.evidence` (`fixtures_results/case_b.json`) is exactly
> one string: `"tokenizer metadata key sets differ: ..."` -- the
> function returns at the Gate 1C tokenizer check, BEFORE the non-Q/K
> tensor comparison or Gate 1B's tied-output proof ever run. The same
> is true of Cases A, C, and D. **No real A-D run has ever actually
> executed `_tied_output_proof()`.**

**Case B's actual disposition (corrected):** whether the real
custom-vs-canonical artifacts' `output.weight` asymmetry would satisfy
the 5 tied-proof conditions is NOT something any real FL-08 profiler
run has executed or proven -- it is a CROSS-CASE INFERENCE from three
separate facts, none of which is Case B's own executed evidence: (1)
the same artifact hashes were independently investigated in the
separate Phase 6 worktree/branch (`OE-ADR-057`), which established
`output.weight` is byte-identical to `token_embd.weight` in the custom
artifact via its own tooling, not FL-08's; (2) FL-08's own synthetic
`TestRound3OutputWeightTiedProof` tests prove the tied-proof MECHANISM
is correct on constructed fixtures; (3) if the tokenizer-metadata gate
were hypothetically removed or satisfied, the non-Q/K tensor comparison
(which DOES reach as far as needed to reuse the same code path) would
be expected to succeed for these specific artifacts based on (1). This
is a plausible inference, not a proven FL-08 result, and is disclosed
as such. This part of Case B is NOT the reason the current real-artifact
runs are non-authorizing regardless -- the tokenizer metadata policy
below is -- but the claim that FL-08 itself proved the tied-output
equivalence for these real artifacts is retracted.

### Execution-relevant metadata policy (Gate 1C)

Compared directly against the real custom-vs-canonical artifact pair
before writing any code (not assumed):

| Field(s) | Classification | Rule |
|---|---|---|
| `general.name`, `general.basename`, `general.languages`, `general.license`, `general.quantization_version`, `general.size_label`, `general.type`, `general.file_type`, `general.alignment` | **BENIGN** | never blocks pair identity |
| `llama.feed_forward_length`, `llama.attention.layer_norm_rms_epsilon`, `llama.rope.dimension_count`, `llama.rope.freq_base`, `llama.context_length` | **EXECUTION-AFFECTING (required)** | must be present and equal on both sides |
| `llama.rope.scaling.type`/`.factor`, `llama.attention.sliding_window` | **EXECUTION-AFFECTING (optional)** | if present on either side, must be present and equal on both |
| `llama.attention.key_length`, `llama.attention.value_length`, `llama.vocab_size` | **EXECUTION-RELATED, REDUNDANT** | not required symmetric; when present, the VALUE is cross-checked against this profiler's own independently-verified `head_dim`/embedding-row-count facts, not merely carried as inert evidence |
| entire `tokenizer.*` namespace (vocabulary, model/pre, token IDs, special-token flags) | **TOKENIZER-AFFECTING** | the full key set must match exactly, and every value must be equal, on both sides |

**This is the single biggest behavioral change this round.** The real
custom-vs-canonical artifacts differ in `tokenizer.ggml.add_eos_token`
(custom only), `tokenizer.ggml.add_space_prefix` and
`tokenizer.ggml.unknown_token_id` (canonical only) -- a genuine,
real, one-sided tokenizer-metadata asymmetry, not a synthetic
construction. Per the tokenizer-affecting rule above, this now makes
`pair_identity` **`UNVERIFIED`** for every real Cases A-D pairing,
where round 2 reported `VERIFIED`. This is NOT a weakened test or a
regression -- it is the gate working as specified: Q/K layout
compatibility and tokenizer-metadata compatibility are two SEPARATE
questions, and this profiler's job is to not silently ignore the
second one just because the first one resolves cleanly.

### Complete tensor-shape rules (Gate 2)

Every required tensor is now validated for its COMPLETE logical
dimensions (rank, every axis), not rows alone: `attn_q.weight`
`(hidden,hidden)`; `attn_k.weight`/`attn_v.weight`
`(kv_heads*head_dim,hidden)`; `attn_output.weight` `(hidden,hidden)`;
`attn_norm.weight`/`ffn_norm.weight`/`output_norm.weight` exactly
`(hidden,)`; `ffn_gate.weight`/`ffn_up.weight`
`(feed_forward_length,hidden)`; `ffn_down.weight`
`(hidden,feed_forward_length)`; `token_embd.weight` rank-2 with
logical last dim `hidden`; `output.weight` (when present) compatible
with vocabulary/hidden. `head_dim` is additionally required EVEN and
positive before any permutation is attempted.

**`WRONG_QK_INPUT_WIDTH` result:** the exact Codex probe (correct rows,
input width `hidden+2`) now returns a structured `INVALID`, confirmed
by 7 dedicated regression tests covering Q, K, V, attention-output,
FFN gate/up/down.

**`ODD_HEAD_DIM_CRASH` result:** an odd `head_dim` (e.g. `hidden=18,
n_head=2` -> `head_dim=9`) is now caught explicitly BEFORE any tensor
is handed to `official_permute()`, returning structured `INVALID` with
an explanatory ambiguity string, confirmed by a dedicated regression.
`_compare_qk_tensor()` additionally wraps the permutation call in
`try/except ValueError` as defense in depth.

### Declaration-only and contradictory-declaration behavior (Gate 3)

`RAW_HF DECLARED` no longer resolves to `VERIFIED_COMPATIBLE`: a
direct-match label derived from `--reference-layout` (confidence
`DECLARED`) now always resolves `runtime_compatibility.result` to
`AMBIGUOUS`, and the CLI returns nonzero -- confirmed by 2 dedicated
tests (one checking the profile dict directly, one invoking the real
CLI subprocess and checking its exit code). If `--reference-layout` is
supplied on a DIRECTIONAL numerical match (not a direct match) and
contradicts the numerically-proven relationship, the result is an
explicit `CONTRADICTION` ambiguity and `AMBIGUOUS`/non-authorizing --
never a silent preference for either the declaration or the numerical
evidence. A declaration that AGREES with a directional numerical match
does not interfere with it.

### `INCOMPLETE_COUNTS_PLAN` result (Gate 4)

`normalization_plan.py`'s `build_plan()` now validates every nested
field it consumes (type, presence, and cross-field consistency --
`qk_tensors_total == layers_total*2`, `layers_checked == layers_total`,
`qk_tensors_checked == qk_tensors_total`, `per_layer_consistent`,
`pair_identity.status == "VERIFIED"` exactly, `reference.terminal_
result is None`) before trusting it. A malformed/incomplete nested
profile always returns `(None, reason)`, never raises -- confirmed by
9 dedicated tests including the exact "one checked tensor, zero
checked layers" internally-inconsistent case.

### Cases A-E: final, honest disposition

All 4 real artifact hashes re-verified immediately before this run,
unchanged from every prior round:

| Artifact | SHA-256 |
|---|---|
| existing-custom F32 | `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac` |
| existing-custom Q8_0 | `3aed955db7e8e7e73e12a05964ad9efb79cef77a895120a77475d7743609d398` |
| canonical F32 | `aef7f8d471367c711a7e46365619498e0e51a0fa93dca8aa09005dabc19810e7` |
| canonical Q8_0 | `dbf0d1f31d3afd0864bb02a916b7e3762728fd616eebd34bd6021c7497def219` |

With the tokenizer-metadata requirement now enforced, **Cases A-D are
honestly `AMBIGUOUS`, `pair_identity: UNVERIFIED`, execution denied** --
NOT the `VERIFIED_NORMALIZATION_REQUIRED`/`VERIFIED_COMPATIBLE` results
round 2 reported. This is not a loosened test producing a worse
number; it is a NEW, real check (round 2 never implemented it)
correctly finding a NEW, real, disclosed asymmetry in the actual
artifacts. The underlying Q/K permutation math is UNCHANGED and still
verified -- Case A's evidence still shows "Q/K fingerprint matches
RAW_HF convention ... verified on 60/60 tensors (30/30 layers)"; it is
the OVERALL pair-identity/authorization verdict that is now honestly
narrower. Case E (tampered fixture) is unchanged: structural `INVALID`,
execution denied, exit nonzero.

No real-artifact run currently reaches `VERIFIED_NORMALIZATION_
REQUIRED` or `VERIFIED_COMPATIBLE` -- the forward normalization-plan
mechanism itself is still demonstrated working correctly, but only via
`TestGate7NormalizationPlan`'s synthetic fixtures (which have no
tokenizer-metadata asymmetry to trip the new gate). A future round
resolving the tokenizer-metadata question directly (e.g. deciding
whether tokenizer metadata is genuinely part of "the same executable
model" for this profiler's narrow forward-pass scope, or requiring an
explicit operator override) would be the natural next step to recover
a real-artifact authorizing case.

## Round 4 remediation: results and conclusions

Triggered by a combined Codex authority review and an independent Grok
Double Check (report:
`gdc-orchestratoride-fringelab-fl08-research-orcengine-fl08-model-
compat-profiler-fl08-round3-remediation.md`, project root, untracked,
preserved unmodified -- note this report is itself a META-review of the
round-4 remediation task's own review-prompt wording, not a code audit;
it confirmed the tied-output test naming issue and left the two
Codex-reproduced false-authorization classes as the primary work,
per direct user reconciliation). Verdict: **FIX BEFORE MERGE/FREEZE**
-- two additional false-authorization paths (equality of semantically
invalid metadata; ungated grouped-query-attention geometry), an
incomplete normalization-plan trust boundary, and inaccurate evidence
wording.

### Authoritative defect inventory

Supersedes round 3's inconsistent "five additional" / six-name /
dual-attribution wording for `OUTPUT_ONLY_ONE_SIDE`.

| Defect ID | Discoverer | Round found | Primary symbol | Disposition |
|---|---|---|---|---|
| `OUTPUT_ONLY_ONE_SIDE` | Grok (round-2 review), independently reproduced by Codex (round-3 review) | 2 | `verify_pair_identity()` | Closed round 3 -- `_tied_output_proof()`, 5-condition gate |
| `ROPE_METADATA_DRIFT` | Codex | 3 | `validate_artifact()` | Closed round 3 -- `_EXECUTION_METADATA_*` equality checks |
| `WRONG_QK_INPUT_WIDTH` | Codex | 3 | `validate_artifact()` shape checks | Closed round 3 -- `_check_2d()`/`_check_1d()` complete-dimension validation |
| `ODD_HEAD_DIM_CRASH` | Codex | 3 | `validate_artifact()` / `official_permute()` | Closed round 3 -- even-head_dim precondition + `try/except` |
| `RAW_HF_DECLARED_AUTHORIZATION` | Codex | 3 | `layer5_decision()` | Closed round 3 -- explicit `DECLARED`-confidence branch forces `AMBIGUOUS` |
| `INCOMPLETE_COUNTS_PLAN` (original) | Codex | 3 | `normalization_plan.py build_plan()` | Closed round 3 -- nested-field validation |
| Semantic-metadata false authorization (equal-but-invalid RMS epsilon / RoPE freq_base / RoPE dimension) | Codex | 4 | `validate_artifact()` | Closed round 4 -- see "Semantic metadata validation" below |
| Invalid GQA geometry false authorization (`n_head % n_head_kv != 0` unchecked) | Codex | 4 | `validate_artifact()` | Closed round 4 -- explicit modulus check |
| Remaining malformed normalization-plan cases (bool-as-int, reference `container.valid` bypassed via null `terminal_result`, unvalidated hash format) | Codex | 4 | `normalization_plan.py validate_profile()`/`_get()` | Closed round 4 -- see "Normalization-plan hardening" below |

Each closure's required regression evidence is named in its own
section below and is executed as part of the full suite (final count
at the end of this section).

### Semantic metadata validation (Gate 1)

Round 3 required execution-metadata EQUALITY between artifact and
reference. Codex reproduced that two artifacts sharing the SAME
INVALID value (`layer_norm_rms_epsilon=-1.0`, `rope.freq_base=
-10000.0`, `rope.dimension_count=999`) previously still reached
`pair_identity.status=VERIFIED` / `execution_authorization=true`.
Round 4 adds semantic (not merely relative) validation in
`validate_artifact()`, applied to EITHER artifact standalone (so it
also protects the no-reference path), derived from the frozen
runtime's own actual, inspected (not modified) contract:

- **RMSNorm epsilon**: finite and `> 0`.
- **RoPE freq_base**: finite and `> 0`.
- **RoPE dimension_count**: must equal `head_dim` exactly. Source of
  truth: `Tools/OrcEnginePhase1/include/orcengine/ops.hpp`'s own
  comment, inspected directly -- `"Full-rotation (rotary_dim ==
  head_dim) non-interleaved Llama RoPE ... Phase 1 has no partial
  rotary factor."` The frozen runtime has no partial-rotary code path
  at all, so any other value cannot be executed by it.
- **RoPE scaling type** (if present): OrcEngine's frozen runtime
  implements NO scaling variant (grep-confirmed: zero occurrences of
  `rope_scaling`/`yarn`/`ntk`/`sliding_window` anywhere under
  `Tools/OrcEngine*`) -- only the no-op values `"none"`/`"linear"`
  (with factor `1.0`) are tolerated; anything else (`yarn`, `dynamic`,
  ...) is rejected as an unsupported execution-affecting mode.
- **RoPE scaling factor** (if present): finite, `> 0`, and must equal
  `1.0` exactly (the only value consistent with "no scaling
  implemented").
- **Sliding window** (if present): must be `0` (the conventional
  GGUF "no window" value) -- any nonzero value requests windowed
  attention this runtime cannot execute.

All of these fail closed to a structured `INVALID` result with
evidence naming the specific field and value -- never an uncaught
Python exception. Confirmed by `TestRound4SemanticMetadataValidation`
(19 tests): all 3 exact Codex-reproduced attacks (paired matching
invalid values), NaN/infinite/zero/negative variants where
representable, the odd/zero/`>head_dim` RoPE-dimension variants, the
unsupported-scaling-type and non-identity-scaling-factor and
nonzero-sliding-window cases, and 4 valid-boundary control cases
(including a full valid pair still reaching `RAW_HF`/non-`INVALID`)
proving the checks are not over-strict on legitimate metadata.

### Grouped-query-attention geometry (Gate 2)

`hidden % n_head == 0` was already checked; `n_head % n_head_kv == 0`
(every Q head must map onto a whole number of shared KV heads -- the
defining GQA invariant) was not. Codex reproduced authorization with
`hidden=24, n_head=3, n_head_kv=2` (3 does not divide evenly by 2).
Round 4 adds the modulus check in `validate_artifact()`, returning
`INVALID` with explicit evidence before any Q/K permutation or
compatibility decision. Confirmed by `TestRound4GQAGeometry` (4 tests):
the small invalid case, the exact nondegenerate Codex probe (`24/3/2`),
valid MHA (`n_head == n_head_kv`), and valid GQA
(`n_head > n_head_kv`, evenly divisible) -- both valid cases still
reach `RAW_HF`, confirming the new check does not reject legitimate
geometries.

### Normalization-plan hardening (Gate 3)

Three gaps closed in `normalization_plan.py`:

1. **Bool-as-int**: Python's `bool` is an `int` subtype, so
   `isinstance(True, int)` is `True` -- `layers_total=True`/
   `layers_checked=True` previously passed integer-type validation.
   `_get()` now explicitly rejects `bool` wherever `int` is the
   expected type.
2. **Reference validity bypass**: a reference with `container.valid=
   False` but a `null` `terminal_result` previously still produced a
   plan (validity was inferred from `terminal_result` alone). Both the
   primary artifact's and the reference's `container.valid` AND
   `declared_architecture` are now checked directly via a shared
   `_validate_artifact_record()` helper (one function, used for both
   records, per this round's "smallest shared fix" instruction).
3. **Hash format**: `artifact.sha256`/`reference.sha256` were only
   checked for "non-empty string" -- `"x"`/`"y"` passed. Both are now
   required to match `^[0-9a-fA-F]{64}$` exactly; case is NOT
   normalized (uppercase hex is accepted as-is since it is still a
   well-formed digest shape; no other malformed value is silently
   repaired).

Confirmed by 18 new tests in `TestGate7NormalizationPlan` (30 total,
up from round 3's 12): boolean
counts (both `layers_total` and `layers_checked`), negative counts,
invalid reference container, invalid primary container, unsupported
reference architecture, unsupported primary architecture, empty/short/
long/nonhex/arbitrary-string hashes on both sides, present-null typed
fields (`layers_total=None`, `pair_identity.status=None`), uppercase
hashes accepted, the original `INCOMPLETE_COUNTS_PLAN` case
reconfirmed still closed, and a valid profile still producing the same
bounded proposal. All malformed cases return `(None, reason)`; none
raises.

### Tied-output guard: what actually happened (Gate 4)

Grok's round-3 review correctly found that
`test_output_weight_tied_proof_fails_if_token_embd_itself_unverified`
did not exercise the `"token_embd.weight" not in checked_names` guard
it claimed to -- a mismatched `token_embd.weight` is caught by the
EARLIER, separate `mismatches` check in `verify_pair_identity()` (the
same path that catches a tampered V/FFN/norm tensor), which returns
before Gate 1B or `_tied_output_proof()` is ever reached. Investigation
this round confirmed the guard is **unreachable as false via any
structurally-valid profile pair**: `token_embd.weight` is a REQUIRED
tensor (an artifact lacking it is already `INVALID` before
`verify_pair_identity()` is ever called), so for any pair that both
pass Layer 1/2, it is unconditionally present on both sides by the
time execution reaches Gate 1B.

Per this round's explicit instruction not to construct an artificial
production path merely to make an unreachable guard testable, the
guard is kept in `profiler.py` as a documented internal invariant /
defense-in-depth against a future refactor (see the comment at its
call site), and the misleading test is renamed to
`test_embedding_mismatch_denied_before_tied_output_proof_is_ever_
reached`, now asserting the SPECIFIC evidence string the actual path
produces (`"N non-Q/K tensor(s) differ..."`) and explicitly asserting
the Gate-1B-specific wording (`"did not pass pair identity"`) does NOT
appear -- so a future code change that accidentally routes this attack
through Gate 1B instead would fail the test rather than pass silently.

Retained, unrenamed, and still passing: one-sided untied output head
denied (`test_artifact_only_untied_output_weight_denied`,
`test_reference_only_untied_output_weight_denied`); a genuine one-sided
tied duplicate passing when every prerequisite is independently
satisfied (`test_one_sided_output_genuinely_tied_is_verified_and_can_
authorize`); a mismatched embedding denied before tied-output proof
(the renamed test above, which also covers "altering both one side's
embedding and its matching tied output cannot bypass pair identity" --
the fixture used already tampers the embedding on the side that ALSO
gets a tied output).

### Pair-identity evidence is fail-fast, not exhaustive

Stated explicitly, since it was previously implicit: `pair_identity.
evidence` in this schema records the FIRST failure `verify_pair_
identity()` encountered (architecture, then geometry, then
execution-metadata, then tokenizer, then non-Q/K tensor inventory,
then output-weight handling, in that fixed order) -- it is NOT an
exhaustive list of every way two artifacts differ. A profile showing
one tokenizer-metadata mismatch does not mean that is the ONLY
difference; later-stage checks simply never ran. This is why Case B's
tied-output equivalence (see the correction above) cannot be read
directly from its own evidence field.

### Tokenizer policy: still fail-closed, no override added

Unchanged from round 3, restated for this round's audit: weight/Q-K
layout compatibility and tokenizer semantic compatibility are two
SEPARATE axes this profiler checks. Current `execution_authorization`
requires BOTH to hold. The real custom-vs-canonical A-D artifact pairs
remain non-authorizing because tokenizer semantic equivalence has not
been proven for them (a real, disclosed `tokenizer.ggml.*` key-set
asymmetry exists). No operator override was added this round, per
explicit instruction. Any future relaxation requires either
evidence-backed tokenizer equivalence (a new check proving the
asymmetric fields are semantically inert for this profiler's
forward-pass scope) or a defined normalization contract for
reconciling them -- never a manual bypass of the check itself.

### Final test count (this round)

`python -m unittest test_profiler` (executed, not estimated): **134
tests, 134 passing.** Independently cross-checked via
`grep -c "    def test_" test_profiler.py` = 134 (matches exactly). Up
from round 3's 93. Derived directly from `git diff 47fab059 --
test_profiler.py`: **42 `def test_` lines added, 1 removed** (the
round-3 `test_output_weight_tied_proof_fails_if_token_embd_itself_
unverified` renamed to `test_embedding_mismatch_denied_before_tied_
output_proof_is_ever_reached`, counted as one removal + one addition)
-- net **+41**, `93 + 41 = 134`, matching the executed count exactly.
