# FL-08 Gates 3, 4, 7, 8 — schema, recognition strategy, extension map, operator-facing design

## Gate 3 — compatibility profile schema

```jsonc
{
  "schema_version": 1,
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
  "runtime_compatibility": {
    "target": "string, e.g. 'canonical-llama.cpp' or 'orcengine-current'",
    "result": "VERIFIED_COMPATIBLE | VERIFIED_NORMALIZATION_REQUIRED | VERIFIED_UNSUPPORTED | AMBIGUOUS | INVALID"
  },
  "confidence_level": "DECLARED | STRUCTURALLY_VERIFIED | NUMERICALLY_VERIFIED | AMBIGUOUS",
  "evidence": ["ordered list of specific evidence strings, each tied to a concrete check"],
  "unresolved_ambiguities": ["string, empty if none"],
  "execution_authorization": "boolean (false unless result is VERIFIED_COMPATIBLE or a normalization plan was explicitly selected -- this prototype never sets it true for NORMALIZATION_REQUIRED)"
}
```

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

- `runtime_compatibility.result`: `VERIFIED_COMPATIBLE`,
  `VERIFIED_NORMALIZATION_REQUIRED`, `VERIFIED_UNSUPPORTED`,
  `AMBIGUOUS`, `INVALID`. No `"probably compatible"` or numeric score.
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
for FL-08 and named as a limitation in the final report.

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

### Layer 5: compatibility decision

- All layers verified consistent, paired reference confirms permutation
  match => `VERIFIED_COMPATIBLE` (vs. the reference target) or
  `VERIFIED_NORMALIZATION_REQUIRED` (vs. a DIFFERENT stated target,
  e.g. "this is raw-HF, canonical-llama.cpp target requires
  normalization").
- Contradiction in Layer 1/2 => `INVALID`.
- No paired reference, no other resolving evidence => `AMBIGUOUS`.
- Layer 3 not applicable (non-`llama` architecture, out of this
  experiment's scope) => `VERIFIED_UNSUPPORTED` for the dialect axis
  specifically (container/architecture layers may still pass).

Never silently picks an interpretation when both remain possible.

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
states):

- **Recognized** -- container + architecture validated (Layers 1-2
  passed), dialect not yet classified.
- **Compatible** -- `VERIFIED_COMPATIBLE` against the currently
  selected runtime target.
- **Normalization available** -- `VERIFIED_NORMALIZATION_REQUIRED`,
  with the normalization plan (Gate 6) available to review/select, but
  NOT auto-applied.
- **Unsupported** -- `VERIFIED_UNSUPPORTED` (a known dialect this
  version doesn't handle, distinct from "broken").
- **Ambiguous** -- insufficient evidence; execution denied until more
  evidence (e.g. a paired reference, or explicit operator override with
  informed consent) is available.
- **Invalid** -- structural contradiction; execution denied
  unconditionally.

**Hard rule carried into the UI design: no green "Compatible"
indicator may be derived from architecture name alone** -- the
indicator must read the profile's `runtime_compatibility.result`
field, which by construction requires at least
`STRUCTURALLY_VERIFIED` evidence, never just a `declared_architecture`
string match. This directly prevents the exact failure mode Phase 6
discovered (a loader that accepts and silently mis-runs an
architecturally-plausible but layout-incompatible artifact).

No Avalonia code was written this round.
