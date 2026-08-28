# FL-08 Gate 1 — Prior-art and novelty check

**Bounded review, not exhaustive.** Sources: direct inspection of this
repository's own existing tooling (authoritative, read directly);
general working knowledge of llama.cpp/gguf-py, Hugging Face
`transformers`, ONNX, and OpenVINO's public conversion/inspection
tooling as of this session; two targeted web searches for a
dedicated Q/K-layout-mismatch-explaining tool (results below). This is
not a substitute for reading every tool's full source -- it is a
bounded, honest pass sufficient to ground a differentiation claim.

## Internal prior art (read directly from this repository)

- **`Tools/OrcEnginePhase2/tools/gguf_inspect.cpp`
  (`orcengine_gguf_inspect`, frozen Phase 2, unmodified).** Reports
  GGUF container metadata (version, alignment, tensor offsets/
  encodings), maps tensors to OrcEngine's logical model via
  `map_llama_model()`, and reports `tied`/`untied` output semantics and
  a boolean `materializable`. **This is real, working, existing prior
  art for Layer 1 (container) and part of Layer 2 (architecture
  mapping) of FL-08's proposed recognition strategy.** It does NOT:
  classify Q/K layout dialect, explain a semantic mismatch, produce a
  graded confidence/evidence structure, or refuse execution based on
  ambiguity -- it reports facts and a binary `materializable` flag,
  nothing more.
- **`training_pit/scripts/check_model_compatibility.py` +
  `MODEL_COMPATIBILITY.md` + `configs/base_model_compat.json`.**
  A DIFFERENT compatibility axis entirely (which HF architectures are
  supported by which TRAINING framework -- Unsloth/PEFT -- for
  TheOrc's fine-tuning pipeline, not GGUF tensor-layout compatibility
  for OrcEngine's inference loader). Notable precedent: this project
  already uses an explicit, non-guessing compatibility-state taxonomy
  (`verified`/`confirmed-external`/`inferred`/`unknown`/`incompatible`)
  for a different problem -- establishes that "explicit states over
  guessing" is already a house convention, not a new idea being
  introduced here for the first time.
- **Phase 6 round 7's `phase6_gate2_canonical_layout_check.py` and
  `phase6_gate3_qk_isolation.py`.** > **CORRECTED (round 2, independent
  Grok Double Check finding):** these files live on the SEPARATE
  `feat/orcengine-phase6-quantization` branch, in the separate
  `OrchestratorIDE-phase6-quantization` worktree -- they are NOT
  present in this FL-08 tree (`research/orcengine-fl08-model-compat-
  profiler`, based on `orcengine-phase5c-freeze`, which predates Phase
  6 entirely). The original wording below implied in-tree evidence;
  this is cross-worktree/session knowledge from the same overall
  working session, not something this repository state can itself
  reproduce or freeze as evidence. Read on the other worktree's disk
  directly, not cited from memory. They ARE, in effect, narrow,
  single-purpose, throwaway compatibility probes for exactly the Q/K
  question -- they hash-verify artifacts, apply the pinned permutation
  formula, and report agree/disagree. They are NOT reusable,
  general-purpose, or artifact-agnostic; they hardcode one specific
  pair of fixture paths and one specific 7-prompt corpus. FL-08's job
  is to generalize the REUSABLE pieces (permutation check, hash
  binding, fail-closed identity discipline) into an artifact-agnostic
  profiler, not to reinvent them.

## External prior art (bounded review)

1. **Which tools merely identify container metadata?**
   `gguf-dump` (part of `gguf-py`, llama.cpp's own Python package) --
   validates GGUF structure (magic, version, KV pairs, tensor index)
   without loading tensor data. Reports what is DECLARED in the
   container. Does not evaluate semantic correctness of tensor
   CONTENT. Hugging Face `transformers`' `AutoConfig`/`from_pretrained`
   reads `config.json` and validates architecture-class existence and
   required config keys -- again, declared-metadata validation, not
   tensor-content semantics.
2. **Which tools normalize tensors during conversion?**
   llama.cpp's own `convert_hf_to_gguf.py` (the exact tool this
   project's Phase 6 remediation used as the canonical-layout
   authority) DOES normalize -- it applies the Q/K permute() transform
   as part of ONE-DIRECTION, ONE-TIME conversion from HF safetensors to
   GGUF. It has no facility to INSPECT an already-produced GGUF and
   determine after the fact which convention it used -- normalization
   there is a write-time transform, not a read-time diagnostic. ONNX's
   `onnx.checker`/`onnxsim`/OpenVINO's Model Optimizer (`ovc`) also
   normalize during conversion (constant folding, layout transforms
   between NCHW/NHWC for vision models) but, like llama.cpp's
   converter, do not retroactively explain a layout mismatch in an
   artifact that already exists.
3. **Which tools can explain a semantic layout mismatch AFTER
   conversion, on an artifact already on disk?** None found, internal
   or external, that explain a TENSOR-LAYOUT semantic mismatch (as
   opposed to a declared-architecture-name mismatch or a gross
   structural corruption) with evidence. The two targeted web searches
   run for this gate found abundant "GGUF won't load" / "unknown
   architecture" troubleshooting content, but no dedicated tool that
   inspects two structurally-similar-but-semantically-different GGUFs
   and explains WHY they diverge in RoPE/Q-K layout terms.
4. **Which tools fail closed rather than guessing?** llama.cpp's own
   runtime loader fails closed on missing/malformed REQUIRED metadata
   (throws/aborts on load) -- but this is architecture-declaration-
   level failure, not layout-dialect-level: an artifact with the WRONG
   Q/K layout for its declared architecture loads and runs SILENTLY,
   producing wrong numbers with no error at all (this is exactly
   Phase 6's own finding: OrcEngine's existing loader accepts a
   canonically-permuted GGUF and silently produces wrong logits). No
   tool found, internal or external, that fails closed specifically on
   layout-dialect AMBIGUITY (as opposed to missing/malformed
   metadata).
5. **Which tools produce a durable, machine-readable compatibility
   profile?** `gguf-dump --json` and `orcengine_gguf_inspect --json`
   both produce durable JSON, but of DECLARED container/architecture
   facts only -- neither carries a confidence gradient
   (declared/structurally-verified/numerically-verified/ambiguous/
   unknown), an explicit compatibility verdict enum, or a normalization
   plan.
6. **Which parts of the OrcEngine proposal appear established prior
   art?** Container validation (Layer 1) and architecture-vs-tensor-
   inventory validation (Layer 2) are NOT novel -- `gguf-dump`,
   `orcengine_gguf_inspect`, and HF's `AutoConfig` all do materially
   similar checks today. The GENERAL IDEA of "compatibility states
   instead of a boolean" is not novel either -- this repo's own
   `training_pit` already does it for a different axis.
7. **Which combination could still be meaningfully differentiating?**
   The combination of (a) a specific, evidence-verified tensor-LAYOUT
   dialect classification (not just architecture-name matching), (b)
   an explicit 4-tier confidence gradient that preserves the
   declared-vs-structurally-proven-vs-numerically-proven distinction,
   (c) fail-closed behavior specifically on layout ambiguity (not just
   missing metadata), and (d) a reproducible, evidence-bound
   normalization PLAN (not an auto-applied transform) output alongside
   the verdict. No internal or external tool found in this bounded
   review combines all four. This is the defensible differentiation
   claim -- see below.

## Novelty verdict

**Not unprecedented. A defensible, narrower differentiation claim
is supported by the evidence above; a broad "nobody does this"
claim is NOT supported and must not be made.**

## Sources consulted this session

- [gguf-dump / gguf-py structural validation](https://deepwiki.com/qualcomm/llama.cpp/5.3-gguf-py-python-library)
- [GGUF format guide, layout/schema drift across llama.cpp versions](https://www.datacamp.com/tutorial/gguf-format-a-complete-guide)
- [ggml/gguf.md spec](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md)
- [GGUF architecture-mismatch failure reports (various)](https://runaihome.com/blog/unknown-model-architecture-gguf-ollama-llama-cpp-fix-2026/)
- Direct repository inspection, IN THIS TREE: `Tools/OrcEnginePhase2/tools/gguf_inspect.cpp`, `training_pit/MODEL_COMPATIBILITY.md`, `training_pit/scripts/check_model_compatibility.py`
- Cross-worktree session knowledge, NOT in this tree (see the round-2 correction above): Phase 6's `phase6_gate2_canonical_layout_check.py`/`phase6_gate3_qk_isolation.py`, read directly on the separate `OrchestratorIDE-phase6-quantization` worktree/`feat/orcengine-phase6-quantization` branch during the same overall working session
