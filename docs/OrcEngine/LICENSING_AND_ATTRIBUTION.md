# Licensing and Attribution

## Repository posture

TheOrc is dual-licensed: AGPL-3.0 by default and a separate commercial license where agreed. The root `LICENSE`, `LICENSING.md`, and `CLA.md` are authoritative. This document is planning guidance, not legal advice.

## OrcEngine default assumption

Unless the maintainer decides otherwise, OrcEngine source added to this repository follows the same repository licensing and contributor requirements, including copyright/SPDX conventions for source files.

## From-scratch evidence rule

Studying a specification, paper, documentation, or observable output is different from copying implementation code. Review notes must identify whether information came from:

- public specification;
- academic paper;
- API documentation;
- external source code;
- execution behavior;
- AI-generated suggestion;
- independent derivation.

Do not paste code from llama.cpp, GGML, LLamaSharp, PyTorch, Transformers, or another engine into OrcEngine without explicit provenance and compatibility review.

## External projects used as research references

| Project/resource | Current research role | License/provenance action before code use |
|---|---|---|
| GGUF specification / GGML | Format specification | Record exact revision; review license if copying definitions/code. |
| llama.cpp | Secondary oracle and behavior comparison | Pin commit; do not copy implementation without review. |
| LLamaSharp | Current TheOrc runtime truth | Pin package/source mapping; no OrcEngine execution dependency. |
| PyTorch/Transformers | Candidate semantic oracle | Dev/test only; record package/model licenses and revisions. |
| HuggingFaceTB/SmolLM2-135M | First real-model candidate | Revision `93efa2f097d58c2a74874c7e644dbc9b0cee75a2` reports Apache-2.0; retain the actual license file/notices and decide source/converted artifact distribution before use. |
| SentencePiece | Tokenizer specification/reference | Determine whether implementation, library, or only vectors are used. |
| CUDA runtime/cuBLAS | Future compute dependency | Record toolkit EULA/redistribution requirements and shipped-library policy. |
| BLAS implementation TBD | CPU baseline candidate | Evaluate license, distribution, attribution, and transitive dependencies. |

## Model artifacts

Each model/tokenizer/converted artifact needs:

- source author/project and URL;
- exact revision;
- model license and restrictions;
- tokenizer license if separate;
- original and converted hashes;
- converter and command;
- whether redistribution is allowed;
- whether artifacts may be committed, downloaded by script, or referenced only;
- required notices or attribution.

“Available on a model hub” does not imply redistribution permission.

## Oracle tensor artifacts

Intermediate tensors may derive from model weights. Treat them as model artifacts for licensing and distribution. Prefer tiny synthetic weights under a clearly controlled license for committed oracle bundles.

## Clean-room discipline

If independence becomes strategically or legally important:

1. write behavior/specification notes with citations;
2. separate researchers from implementers if counsel requires it;
3. implement from the approved specification;
4. retain provenance and review records;
5. compare only through test artifacts/interfaces;
6. obtain legal advice before claiming clean-room status.

This starter suite does not claim a formal clean-room process.

## AI-generated content

AI output may reproduce licensed patterns or invent provenance. Every substantial code contribution requires human/reviewer inspection, source search where suspicious, and normal CLA/license policy. “The AI wrote it” is not provenance.

## Attribution ledger template

```text
Component/artifact:
Purpose:
Source/project:
URL and revision:
License:
Copied code? yes/no
Derived behavior/specification:
Required notices:
Distribution allowed:
Reviewer/date:
```

## Attribution ledger — HuggingFaceTB/SmolLM2-135M (Phase 0 real-model candidate)

```text
Component/artifact: HuggingFaceTB/SmolLM2-135M model weights + tokenizer
Purpose: Phase 0 real-model candidate (Profile B), per OE-ADR-014
Source/project: https://huggingface.co/HuggingFaceTB/SmolLM2-135M
URL and revision: 93efa2f097d58c2a74874c7e644dbc9b0cee75a2 (pinned, not "main")
License: Apache-2.0, confirmed 2026-08-15 by direct download and inspection of the
  repository's own README.md frontmatter ("license: apache-2.0") and its "## License"
  section body ("[Apache 2.0](https://www.apache.org/licenses/LICENSE-2.0)"). Full
  README.md retained locally (see download_manifest.json below) as the license notice.
Copied code? No -- weights/config/tokenizer data only, no source code copied.
Derived behavior/specification: config.json's architecture fields (LlamaForCausalLM,
  hidden_size=576, num_hidden_layers=30, num_attention_heads=9, num_key_value_heads=3,
  intermediate_size=1536, vocab_size=49152, rms_norm_eps=1e-5, rope_theta=100000,
  tie_word_embeddings=true, rope_interleaved=false) match the values already recorded
  in PHASE_0_ARCHITECTURE_PROFILE.md's Profile B table on first verification
  (2026-07-18) -- re-confirmed independently on download, not just trusted.
Required notices: Apache 2.0 license text (by reference to the standard URL) and
  attribution to the SmolLM2 project/HuggingFaceTB, per Apache-2.0 §4.
Distribution allowed: Yes for the model itself (Apache-2.0 permits redistribution with
  attribution and license-notice retention, no additional restriction found in the
  repository). This resolves OQ-004's "Yes/no" question for THIS specific candidate;
  it does not blanket-approve every future candidate without its own review. Repo
  hygiene is a separate, stricter constraint: the 269 MB model.safetensors file is
  NOT committed to git (script-downloaded on demand via oracle/download_candidate.py,
  which pins the exact revision and records SHA-256 for every file) -- this is a
  repository-cleanliness choice per this document's own "should not be committed
  casually" guidance, not a license restriction.
Reviewer/date: Claude (Sonnet 5), OrcEngine Phase 0 loop, 2026-08-15
```

Full per-file SHA-256 hashes: `Tools/OrcEnginePhase0/artifacts/smollm2-135m/download_manifest.json`
(generated by `oracle/download_candidate.py`, not hand-recorded).

## Release gate

Before shipping any OrcEngine binary: complete dependency SBOM/manifest, source and binary license notices, CUDA/BLAS redistribution review, model distribution separation, CLA compliance, and maintainer/legal approval where uncertainty remains.
