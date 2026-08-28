# FL-08 Gates 3, 4, 7, 8 — schema, recognition strategy, extension map, operator-facing design

## Gate 3 — compatibility profile schema

**Current schema: version 3** (as of round 7's FL-08 closeout). The
schema evolved through the experiment's remediation rounds (`1` at
this charter's original writing, `2` from round 5's metadata-type
hardening, `3` from round 7's layout/admission/authorization
separation) -- the shape below is the CURRENT, implemented contract;
see `EXPERIMENT.md`'s per-round sections for the chronology of how it
got here.

```jsonc
{
  "schema_version": 3,
  "artifact": {
    "path": "string",
    "sha256": "string (required, always computed)",
    "file_size_bytes": "integer"
  },
  "container": {
    "type": "GGUF",
    "version": "integer",
    "valid": "boolean",
    "evidence": ["container-level facts checked, e.g. magic ok, offsets in-bounds"]
  },
  "declared_architecture": "string | null",
  "provenance": {
    "known": "boolean",
    "producer": "string | null (e.g. 'convert_real_candidate.py' or 'convert_hf_to_gguf.py@<commit>')",
    "source": "DECLARED | STRUCTURALLY_VERIFIED | UNKNOWN"
  },
  "tensor_inventory_fingerprint": "string (sha256 of sorted tensor name+shape+dtype list)",
  "metadata_fingerprint": "string (sha256 of sorted non-internal KV pairs)",
  "tokenizer_fingerprint": "string | null",
  "output_weight_semantics": "TIED_PHYSICALLY_DUPLICATED | UNTIED | ABSENT | UNKNOWN",
  "qk_layout": {
    "classification": "RAW_HF | CANONICAL_LLAMA_CPP | AMBIGUOUS | UNKNOWN",
    "confidence": "DECLARED | STRUCTURALLY_VERIFIED | NUMERICALLY_VERIFIED | AMBIGUOUS",
    "layers_checked": "integer",
    "layers_total": "integer",
    "per_layer_consistent": "boolean"
  },
  "quantization_formats": ["string, one per tensor-type family present, e.g. F32, Q8_0"],
  "known_normalization_requirements": ["string, human-readable"],
  "layout_compatibility": {
    "target": "string, e.g. 'canonical-llama.cpp' or 'orcengine-current'",
    "result": "VERIFIED_LAYOUT_COMPATIBLE | VERIFIED_LAYOUT_NORMALIZATION_REQUIRED | UNSUPPORTED | AMBIGUOUS | INVALID"
  },
  "runtime_admission": {
    "status": "NOT_EVALUATED (fixed -- this prototype has no loader/runtime seam to attest admission; never any other value)",
    "authority": "null (fixed -- no runtime authority has evaluated this artifact)",
    "evidence": "[] (fixed -- no runtime-admission evidence exists)"
  },
  "confidence_level": "DECLARED | STRUCTURALLY_VERIFIED | NUMERICALLY_VERIFIED | AMBIGUOUS",
  "evidence": ["ordered list of specific evidence strings, each tied to a concrete check"],
  "unresolved_ambiguities": ["string, empty if none"],
  "execution_authorization": "boolean -- requires EVERY authorization_conditions entry true, including runtime_admission.status == \"VERIFIED\"; since runtime_admission is always NOT_EVALUATED in this prototype, execution_authorization is unconditionally false for every artifact it profiles"
}
```

**What `layout_compatibility` proves, and what it does not.** FL-08
proves Q/K layout relationships between an artifact and (optionally) a
paired reference of the same underlying model, and may propose a
normalization plan (Gate 6) when the artifact's layout doesn't match a
target's expected convention. It does NOT prove that any engine can
actually load and execute the artifact -- that requires runtime
admission: confirmation from the target loader/runtime itself (or an
engine-owned, versioned capability authority) that every tensor
encoding, metadata value, and structural detail the artifact declares
is one that specific runtime can materialize and run. FL-08 has never
had a seam to obtain that confirmation, so `runtime_admission.status`
is a fixed `NOT_EVALUATED` and `execution_authorization` is always
`false` -- this is a structural limitation of the research prototype,
not a per-artifact judgment.

### Why these fields and not more

Every field is either (a) directly read from the container/metadata
(DECLARED), (b) computed by a deterministic structural check
(STRUCTURALLY_VERIFIED), or (c) computed by the Layer 4 numerical
probe (NUMERICALLY_VERIFIED). No field is a free-text guess. The
schema deliberately omits a universal plugin-registry shape (per Gate
3's own instruction to avoid a universal framework) -- `qk_layout` is
the ONE dialect axis implemented; Gate 7 documents how OTHER axes
would extend the schema without speculatively adding fields for them
now.

### Classification enums (exact, no vague states)

- `layout_compatibility.result`: `VERIFIED_LAYOUT_COMPATIBLE`,
  `VERIFIED_LAYOUT_NORMALIZATION_REQUIRED`, `UNSUPPORTED` (a
  structurally valid artifact/value outside this profile's supported
  scope, e.g. a well-formed non-dense MoE model or an alternate RoPE
  scaling mode -- distinct from `INVALID`), `AMBIGUOUS`, `INVALID`
  (malformed, contradictory, wrong-typed, or structurally impossible).
  No `"probably compatible"` or numeric score.
- `runtime_admission.status`: `NOT_EVALUATED` only, in this prototype.
  The schema reserves the name for a future `VERIFIED`/`DENIED` (or
  similar) result an actual engine-owned admission authority would
  produce; FL-08 itself never produces anything but `NOT_EVALUATED`.
- `confidence_level` / `qk_layout.confidence`: `DECLARED`,
  `STRUCTURALLY_VERIFIED`, `NUMERICALLY_VERIFIED`, `AMBIGUOUS`. Ordered
  by evidentiary strength; a profile with ANY unresolved structural
  contradiction is `AMBIGUOUS` regardless of what is declared.

## Gate 4 — recognition strategy (as implemented by the Gate 5 prototype)

### Layer 1: container validation

Checks (all STRUCTURALLY_VERIFIED, computed from the file directly via
the `gguf` Python package -- the same library Phase 6's own tooling
uses, not a new dependency):
- GGUF magic bytes present and version is a known value (currently 2
  or 3).
- Metadata section parses without truncation.
- Every tensor's offset+size stays within the file's actual byte
  length (bounds check).
- Tensor encoding is one of the small set this profiler recognizes
  (F32, F16, Q8_0 -- the set Phase 6 already established provenance
  for; anything else is `UNKNOWN`, not a crash).
- No duplicate tensor names.
- Artifact SHA-256 computed and recorded unconditionally (this is the
  binding key every later field references).

Failure here (malformed container) => `INVALID`, `execution_authorization: false`, stop -- Layers 2-4 do not run.

### Layer 2: architecture validation

Compares the declared `general.architecture` against:
- required tensor name set for that architecture (for `llama`: per-
  layer `attn_q/k/v/output`, `ffn_gate/up/down`, norms, plus
  `token_embd.weight`/`output_norm.weight`)
- tensor SHAPES against `n_head`/`n_head_kv`/`hidden`/`head_dim`
  declared in metadata (contradiction, e.g. a shape that cannot be
  reshaped to the declared head geometry, => reject, do not guess
  which is right)
- layer count consistency (declared `block_count` vs. actual highest
  `blk.N.*` index present)
- RoPE metadata presence (`rope_dimension_count`/`rope_freq_base`)

Contradiction here => `INVALID`. Missing-but-not-contradictory
optional metadata => proceeds with a recorded `unresolved_ambiguities`
entry, not a silent pass.

### Layer 3: dialect/layout identification (Q/K only, this experiment)

Uses the EXACT pinned permutation relationship from Phase 6
(`conversion/llama.py`'s `permute()`, pinned commit
`6fed9f6ff7a603b124cb8c5864fca6ea879f9f99`, tag `b10436`) --
re-implemented in this worktree's own module (see Gate 5), not
imported cross-worktree.

For an artifact with NO paired reference form available (the normal
case -- most artifacts arrive alone): classify by checking whether
`permute(attn_q/k)` applied to the artifact's OWN tensors, when run
through llama.cpp's documented RoPE-application convention, is
self-consistent -- **this experiment does NOT attempt that single-
artifact numerical inference** (it is a materially harder, unproven
technique); instead, per the instruction "do not infer layout from
tensor names alone" and "if provenance is trusted, record it as
declared evidence -- but still verify the tensor relationship when
both reference forms are available," this prototype's Layer 3
implements the PROVEN case: when a second, paired reference-layout
artifact of the SAME underlying model is available (as it is for all 4
real Phase 6 fixtures -- existing-custom paired with canonical), it
verifies the exact permutation relationship, layer by layer, for all
declared layers. When no paired reference is available, layout is
reported `UNKNOWN` with `confidence: AMBIGUOUS` and normalization
guidance is NOT offered (fail closed) rather than inferred from
tensor names or file naming conventions.

This is a real, disclosed scope limitation, not silently omitted:
single-artifact (unpaired) layout inference is explicitly OUT OF SCOPE
for FL-08 -- see EXPERIMENT.md's "Round 2 remediation: results and
conclusions" section, this experiment's durable results/limitations
record (round-2 correction: no separate "final report" file exists;
the original wording here implied one did).

### Layer 4: numerical conformance probe

Smallest useful probe, per instruction: for each of the checked
layers, computes `permute(attn_q)` and `permute(attn_k)` (a
deterministic, cheap, pure-NumPy array operation -- no model forward
pass, no RoPE application, no full-vocabulary computation) and checks
byte-exact equality against the reference artifact's corresponding
tensor. This is the IDENTICAL check Phase 6's Gate 2/3 already proved
correct and reused, generalized to run against an ARBITRARY artifact
pair rather than the one hardcoded pair. States exactly what it
proves: "tensor-level layout equivalence under the pinned permutation
formula, for the specific layers checked" -- explicitly NOT "the whole
model computes correct logits" (that claim would require a full
forward pass, which Phase 6's separate C++ tooling already did
elsewhere and this prototype does not repeat).

### Layer 5: layout compatibility decision

(`layout_compatibility` -- schema v3, round 7; this layer was named
"compatibility decision" against the pre-round-7 `runtime_
compatibility` field and is renamed here to match. Its decision logic
is unchanged by that rename -- only the field/result names it writes
are new.)

- All layers verified consistent, paired reference confirms permutation
  match => `VERIFIED_LAYOUT_COMPATIBLE` (vs. the reference target) or
  `VERIFIED_LAYOUT_NORMALIZATION_REQUIRED` (vs. a DIFFERENT stated
  target, e.g. "this is raw-HF, canonical-llama.cpp target requires
  normalization").
- Contradiction in Layer 1/2 => `INVALID`.
- No paired reference, no other resolving evidence => `AMBIGUOUS`.
- Layer 3 not applicable (non-`llama` architecture, out of this
  experiment's scope), or a well-typed value describing a real,
  meaningful configuration outside this profile's supported scope
  (round 7, Gate 4 -- e.g. a non-dense MoE model or an alternate RoPE
  scaling mode) => `UNSUPPORTED` for the affected axis specifically
  (container/architecture layers may still pass).

Never silently picks an interpretation when both remain possible.
This layer decides LAYOUT compatibility only -- it never decides, and
this design never claims it decides, whether the artifact can actually
be loaded and executed by the target runtime (see the Gate 3 schema
section's `runtime_admission` axis).

## Gate 7 — universal-extension map (documentation only, no code)

| Future dialect axis | Detection evidence required | Possible normalization | Main ambiguity risk | Numerical confirmation needed? |
|---|---|---|---|---|
| Fused vs. separate Q/K/V | Tensor name/shape inventory (`attn_qkv.weight` vs. 3 separate tensors) + declared head geometry | Split-and-reshape at materialization time | A fused tensor's internal Q/K/V ordering convention varies by producer -- shape alone doesn't prove ordering | Yes -- same permutation-style equality check against a paired reference, generalized to a split-then-compare |
| Transposed matrices | Shape comparison against declared `(out,in)` vs. `(in,out)` convention; GGUF stores row-major but producers disagree on which logical axis is which | Transpose at materialization | A square weight matrix cannot be structurally distinguished from its transpose without a semantic anchor (e.g. a paired reference or a known input/output dimension asymmetry) | Yes, and for square matrices MAY be unresolvable without one |
| Tied vs. physically duplicated output head | Tensor presence (`output.weight` absent/present) + byte-equality vs. `token_embd.weight` when present | None needed (both forms are numerically equivalent once loaded) -- classification only | A "duplicated but NOT byte-identical" case is meaningfully different from either clean case and must not be conflated with either | Yes -- byte-equality check, already proven in Phase 6 |
| RoPE convention/scaling variants (linear/NTK/YaRN) | `rope_scaling_type`/`rope_scaling_factor` metadata + declared vs. actual context length | Runtime-side scaling formula selection, not a tensor transform | Multiple scaling schemes can produce similar metadata footprints; a wrong choice is silent, not crashing | Yes -- a bounded synthetic-position numerical probe, not full-model |
| Quantization block variants (Q4_K, Q5_K, Q6_K vs. Q8_0) | Tensor `ggml_type` enum per tensor | Dequantize-then-requantize, or accept multiple block schemes natively | Different block-size/scale-packing conventions can silently misread as a different, structurally-similar scheme | Yes -- reconstruction-error check against a known-good reference |
| Tokenizer normalization/special-token policy | `tokenizer.ggml.*` metadata completeness + BOS/EOS insertion flags | Runtime-side tokenization policy selection | A missing/default-assumed special-token id silently shifts every downstream position | Yes -- exact-token-ID-match probe (already this project's own established convention) |
| MoE expert ordering/packing | Expert-count/expert-tensor-naming metadata + per-expert tensor shapes | Reordering/repacking expert tensors at materialization | Expert index assigned by different converters may not correspond 1:1 without an explicit mapping | Yes -- per-expert output comparison against a reference |
| Sliding-window attention metadata | `attention.sliding_window`/pattern metadata presence and consistency with layer count | Runtime-side windowing policy selection | A model needing SWA silently run as full attention produces plausible-looking but wrong long-context output | Yes -- a bounded long-context synthetic probe |
| Model-family-specific tensor aliases | Cross-reference against a maintained alias table per architecture | Alias resolution at the mapping layer (already how `map_llama_model` works) | A NEW, unseen alias is indistinguishable from an unsupported tensor without a maintained table | No -- purely a lookup-table completeness problem |
| Producer/version-specific conversion quirks | Provenance metadata (`general.quantization_version`, tool banners) cross-referenced against a maintained quirks table | Quirk-specific normalization, looked up by producer/version | An UNDOCUMENTED quirk from a new producer version is invisible until discovered and added to the table | Sometimes -- depends on the specific quirk |

No code was written for any of the above this round.

## Gate 8 — operator-facing differentiation (design only, no UI code)

Presentation states TheOrc/Avalonia would need, distinct from each
other (matching the schema's own enum, not inventing new UI-only
states). Reconciled with schema v3 (round 7): layout compatibility,
runtime admission, and execution authorization are now three SEPARATE
axes, and the operator-facing states below distinguish all three
rather than collapsing them into one "Compatible" indicator as the
original (schema-v1-era) design did:

- **Recognized** -- container + architecture validated (Layers 1-2
  passed), dialect not yet classified.
- **Layout compatible** -- `layout_compatibility.result ==
  VERIFIED_LAYOUT_COMPATIBLE` against the currently selected target.
  This is a claim about Q/K layout ONLY -- it does not by itself mean
  the artifact can execute (see "Runtime admission not evaluated"
  below, which always applies alongside it in this prototype).
- **Layout normalization available** --
  `VERIFIED_LAYOUT_NORMALIZATION_REQUIRED`, with the normalization
  plan (Gate 6) available to review/select, but NOT auto-applied. Also
  never implies execution is authorized on its own.
- **Unsupported** -- `layout_compatibility.result == UNSUPPORTED` (a
  structurally valid artifact/value outside this profile's supported
  scope, distinct from "broken").
- **Ambiguous** -- insufficient layout evidence; execution denied
  until more evidence (e.g. a paired reference, or explicit operator
  override with informed consent) is available.
- **Invalid** -- structural contradiction; execution denied
  unconditionally.
- **Runtime admission not evaluated** -- `runtime_admission.status ==
  NOT_EVALUATED`. In this prototype this state is ALWAYS present,
  regardless of how favorable the layout state above is -- FL-08 has
  no loader/runtime seam to attest that the selected engine can
  actually load and execute the artifact. A production UI would only
  retire this state once a real engine-owned admission result exists
  to replace it (see `EXPERIMENT.md`'s round-7 architectural
  conclusion); FL-08 itself never produces anything else.
- **Execution denied** -- `execution_authorization == false`. In this
  prototype this is the state for EVERY artifact, unconditionally,
  because it requires runtime admission to be verified and that never
  happens here. This is not a per-artifact judgment call the UI would
  ever show as "maybe" -- it is a fixed property of what this research
  prototype can and cannot attest.

**Hard rule carried into the UI design, updated for schema v3: no
green "Compatible" indicator may be derived from architecture name
alone, AND no such indicator may be derived from
`layout_compatibility` alone either** -- the indicator must read
`layout_compatibility.result` for the layout claim (which by
construction requires at least `STRUCTURALLY_VERIFIED` evidence, never
just a `declared_architecture` string match) AND separately gate any
"can run" claim on `execution_authorization`/`runtime_admission`,
never inferring the latter from the former. This directly prevents
TWO failure modes: the original Phase 6 discovery (a loader that
accepts and silently mis-runs an architecturally-plausible but
layout-incompatible artifact) AND round 7's own finding in this same
experiment (a profiler that understands an encoding structurally but
conflates that with the runtime being able to execute it). FL-08 does
NOT perform production runtime admission or normalization itself --
it only classifies and proposes; a production integration would need
the real engine-owned admission authority this design document
recommends but does not implement.

No Avalonia code was written this round.
