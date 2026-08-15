# OrcEngine Phase 0 — Reference Oracle (work in progress)

This is the Phase 0 reference-oracle implementation for
[OrcEngine](../../docs/OrcEngine/README.md), following
[PHASE_0_REFERENCE_ORACLE.md](../../docs/OrcEngine/PHASE_0_REFERENCE_ORACLE.md),
[PHASE_0_ARCHITECTURE_PROFILE.md](../../docs/OrcEngine/PHASE_0_ARCHITECTURE_PROFILE.md),
and [PHASE_0_ACCEPTANCE.yaml](../../docs/OrcEngine/PHASE_0_ACCEPTANCE.yaml).

Per the project's own accepted decision (OE-ADR-001: "documentation and
deterministic oracle precede implementation"), this directory contains
**oracle/test code only** — no OrcEngine tensor-execution engine exists here
or anywhere else in the repository yet.

## Status against PHASE_0_ACCEPTANCE.yaml — 12 of 14 passing (2026-08-15)

| Check | Status |
|---|---|
| `synthetic_operator_microcases` | **Done** — `oracle/microcases.py`, 11/11 hand-derived operator microcases pass. |
| `synthetic_layer_taps` | **Done** — `oracle/synthetic_layer_taps_check.py`. All 37 required taps (17 per layer × 2 layers + 3 top-level) agree between the NumPy and independent PyTorch implementations, max diff 4.768e-07. |
| `cache_equivalence` | **Done** — `oracle/fixture_c.py`, full Profile A (n_layers=2). Full-prefix vs. prefill+cached-decode last-position logits agree (max diff 2.4e-07), repeated with context reset, cross-run deterministic. |
| `near_tie_logits` | **Done** — `oracle/fixture_near_tie.py`. Near-tie (margin=0.000145) and exact-tie (bit-exact-equal logits) both constructed via tied-embedding-row perturbation; exact-tie decode rule (lowest token ID wins) verified directly. |
| `deterministic_regeneration` | **Done** — `oracle/deterministic_regeneration.py`, two separate `python3` subprocesses (genuinely fresh interpreters). Non-floating identities exactly equal; floating logits bit-exact (identical SHA-256, max_abs_diff=0.0). |
| `fault_injection` | **Done** — 7/7 fault types. `oracle/fault_injection.py` (6, against the synthetic profile): transposed projection matrix, off-by-one position, incorrect RoPE pairing, missing causal mask, changed RMSNorm epsilon, swapped K/V cache write — each detected at its exact expected checkpoint. `oracle/tokenizer_special_token_fault.py` (7th, against the real SmolLM2-135M candidate): mislabeling a control token as NORMAL shatters its tokenization from `[1, 4093]` into 8 wrong tokens. |
| `artifact_schema_complete` | **Done** — `oracle/manifest.py` + `oracle/artifact_record.py` + `oracle/generate_manifest.py`. Manifest generated from a live Fixture C run, written to disk, reloaded, schema-validated: 8/8 top-level sections, every tensor artifact has all 7 required fields. |
| `three_way_oracle_independence` | **Done** — all three legs real: (1) hand-derived microcases, (2) NumPy vs. independently-written PyTorch oracle (`oracle/cross_oracle_check.py`, agree to 2.4e-07), (3) `oracle/export_gguf.py` writes Profile A's own weights into a real "llama"-architecture GGUF and `oracle/llama_cpp_deployment_oracle.py` runs it through pinned llama.cpp `b10436` — argmax matches exactly, top-5 log_softmax agrees within a documented cross-language tolerance. |
| `provenance_complete` | **Done** — `oracle/download_candidate.py` (pinned revision, per-file SHA-256) + `oracle/convert_real_candidate.py` (converter, GGUF hash) + `docs/OrcEngine/LICENSING_AND_ATTRIBUTION.md` attribution ledger (Apache-2.0). |
| `tokenizer_dual_source_agreement` | **Done** — `oracle/tokenizer_dual_source_check.py`, 5/5 fixtures (incl. non-ASCII) byte-identical between the real HF tokenizer.json and llama.cpp reading our converted GGUF. Required a real fix (missing `tokenizer.ggml.pre`, degraded-quality warning) to actually pass. |
| `raw_prompt_identity` | **Done** — `oracle/raw_prompt_identity.py`, 6 fixture records (synthetic + 5 real) with raw bytes, rendered prompt, token IDs, and SHA-256 for each, retained in `artifacts/raw_prompt_identity_manifest.json`. |
| `real_candidate_conversion` | **Done** — `oracle/real_candidate_conversion_manifest.py`. Converted GGUF read back via gguf-py's independent `GGUFReader` ("strict parser"): 273/273 expected tensors, 24 metadata fields, hash reproducible across reruns. |
| `real_candidate_logits` | **Open** — needs SmolLM2-135M's actual layer-boundary/final logits compared against both pinned oracles (our own oracle loaded with the real weights, and llama.cpp via the converted GGUF). The conversion, tokenization, and provenance are done; the logit comparison itself is not. |
| `independent_reproduction` | **Open, correctly parked** — needs a human or a separate agent to reproduce the synthetic bundle from this README's commands, starting cold. Not something this loop can satisfy for itself. |

## What's implemented

- `oracle/ops.py` — Profile A (`OE-L0-SYNTH-1`) block-level math (RMSNorm,
  non-interleaved Llama RoPE, causal masking incl. the rectangular variant
  for incremental decode, softmax, SiLU, linear/matmul, embedding lookup),
  float32, plain unoptimized NumPy so it reads directly against the spec.
- `oracle/microcases.py` — Fixture A: 11 hand-derived operator microcases,
  expected values from an independent scalar reference path (`math` stdlib,
  never calling `ops.py`). The "independent ground truth" oracle class.
- `oracle/comparison.py` / `oracle/artifact_record.py` / `oracle/manifest.py` —
  comparison-record and artifact-manifest schemas from
  `PHASE_0_REFERENCE_ORACLE.md`.
- `oracle/weights.py` / `oracle/model.py` — deterministic weight generation
  and the full-prefix + incremental-decode (`forward_cached`) forward pass,
  generic over layer count. The "primary semantic oracle," NumPy leg.
- `oracle/torch_oracle.py` — the same math independently re-derived in
  PyTorch (not translated from the NumPy code), with matching tap capture
  for full intermediate-state comparison.
- `oracle/fixture_b.py` / `oracle/fixture_c.py` / `oracle/fixture_near_tie.py` —
  one-layer, full-2-layer + cache equivalence, and near/exact-tie fixtures.
- `oracle/fault_injection.py` / `oracle/tokenizer_special_token_fault.py` —
  all 7 required fault-injection cases.
- `oracle/export_gguf.py` / `oracle/llama_cpp_deployment_oracle.py` — writes
  Profile A's own weights into a real "llama"-architecture GGUF and runs it
  through pinned llama.cpp for the secondary deployment oracle leg.
- `oracle/download_candidate.py` / `oracle/convert_real_candidate.py` /
  `oracle/real_candidate_conversion_manifest.py` — real-model (SmolLM2-135M)
  download, HF-safetensors-to-GGUF conversion, and independent parser
  verification.
- `oracle/tokenizer_dual_source_check.py` / `oracle/raw_prompt_identity.py` —
  real-candidate tokenizer fidelity and retained prompt/token provenance.

## What's deliberately NOT here

- The real-candidate logit comparison itself (`real_candidate_logits`) —
  everything needed to build it (conversion, tokenizer, llama.cpp
  integration) exists; the comparison isn't written yet.
- Any C++/CUDA engine code — that's explicitly Phase 1+, gated on Phase 0
  passing in full AND the maintainer approving the product-value thesis
  (`docs/OrcEngine/DECISION_LOG.md` OE-ADR-016, currently proposed, not
  yet accepted).

## Known decisions worth knowing about

- **OE-ADR-015**: Fixture B/C's weight-init scale was raised from 0.02 to
  0.1 after measuring that 0.02 made a real fault (transposed `w_o`)
  invisible to fault-injection comparison (0.07 max diff, argmax unchanged).
- **OE-ADR-016** (proposed, awaiting maintainer approval): the Phase 0
  bounded product-value thesis, using this project's own dated evidence
  (LLamaSharp can't load Qwen3.8-27B; llama.cpp can) as the prevented-
  capability claim.

## Reproducing

```bash
cd Tools/OrcEnginePhase0
python3 -m pip install -r requirements.txt
python3 -m oracle.microcases
python3 -m oracle.fixture_b
python3 -m oracle.fixture_c
python3 -m oracle.fault_injection
python3 -m oracle.fixture_near_tie
python3 -m oracle.deterministic_regeneration
python3 -m oracle.generate_manifest
python3 -m oracle.cross_oracle_check
python3 -m oracle.synthetic_layer_taps_check
python3 -m oracle.llama_cpp_deployment_oracle   # needs a pinned llama.cpp build; see that file's docstring
python3 -m oracle.download_candidate            # downloads ~270MB from HuggingFace
python3 -m oracle.convert_real_candidate
python3 -m oracle.tokenizer_dual_source_check
python3 -m oracle.real_candidate_conversion_manifest
python3 -m oracle.raw_prompt_identity
python3 -m oracle.tokenizer_special_token_fault
```

Pinned environment: CPython 3.14.3, numpy 2.5.2, torch 2.13.0+cpu,
gguf 0.19.0, huggingface_hub 1.27.0, safetensors 0.8.0, tokenizers 0.23.1
(all in `requirements.txt`). `oracle.llama_cpp_deployment_oracle` and
`oracle.tokenizer_special_token_fault` additionally need a pinned llama.cpp
build (b10436, 2026-08-14) — path configurable via `ORC_LLAMA_SERVER_PATH`
/ `ORC_LLAMA_TOKENIZE_PATH` env vars, get it from
https://github.com/ggml-org/llama.cpp/releases/tag/b10436.
