# Phase 6 Stage 1 Checkpoint 2: Q8_0 fixture provenance

The canonical Q8_0 build of SmolLM2-135M is produced by locally
quantizing the existing pinned F32 GGUF with the pinned `llama.cpp`
`b10436` build's `llama-quantize` binary, per
`docs/OrcEngine/PHASE6_QUANTIZATION_SPEC.md` Section 6 item 1. This file
records the exact provenance chain so the fixture is reproducible and
independently verifiable, not merely asserted.

The generated `smollm2-135m-q8_0.gguf` itself is NOT committed (this
project's `.gitignore` excludes `*.gguf` globally; the file is fully
regenerable from this record). Regenerate it with the exact command in
"Invocation" below.

## Input

- Path: `F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m.gguf`
- SHA-256: `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac`
  (matches the authority already pinned throughout Phase 2-5C and this
  spec's Section 1/2)

## Quantizer

- Executable: `C:\Users\hardc\AppData\Local\Temp\llamacpp_test\llama-quantize.exe`
  (extracted from `llama-cpu.zip` in the same directory -- the archive
  also contains `llama-tokenize.exe`, whose `--version` output is the
  pinned build's own confirmation banner)
- Executable SHA-256: `0e252724487acb66550f2b4ac97036e384e047ab8d8bc60cea529bbe5da54a8e`
- Version confirmation: `llama-quantize.exe` has no `--version` flag of
  its own, but prints the identical build banner at runtime that
  `llama-tokenize.exe` prints under `--version`:
  ```
  version: 0.1.0-dev (build 10436, commit 6fed9f6ff)
  built with Clang 20.1.8 for Windows x86_64
  ```
  Independently corroborated: `llama.dll` (the shared library both
  executables link against, extracted from the SAME `llama-cpu.zip`
  archive) has SHA-256 `0089c0b354d0da3d262466c04ee58c52e23add486d3a0338194b1fc00cee413b`
  in both the already-version-verified live directory and a fresh
  extraction from the zip -- byte-identical, confirming `llama-
  quantize.exe` shares the exact same pinned build's core library, not
  merely a same-named binary from an unpinned source.
- Working directory: `C:\Users\hardc\AppData\Local\Temp\llamacpp_test`

## Invocation

```
llama-quantize.exe ^
  "F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m.gguf" ^
  "F:\Ai\OrchestratorIDE-phase6-quantization\Tools\OrcEnginePhase6\fixtures\smollm2-135m-q8_0.gguf" ^
  Q8_0
```

Ran successfully: 273/273 tensors processed, quantize time 626.81ms.
`llama_model_quantize_impl` reported: model size 621.13 MiB (32.00 BPW)
-> quant size 165.09 MiB (8.51 BPW).

## Output

- Path: `Tools/OrcEnginePhase6/fixtures/smollm2-135m-q8_0.gguf` (not committed -- `*.gguf` is gitignored)
- SHA-256: `3aed955db7e8e7e73e12a05964ad9efb79cef77a895120a77475d7743609d398`
- File size: 174,891,296 bytes

## Tensor-by-tensor encoding inventory

**MIXED-FORMAT, confirmed directly** (not assumed): 273 total tensors --
**212 Q8_0**, **61 F32**. Generated via this project's own
`orcengine_gguf_inspect` tool (`Tools/OrcEnginePhase2/tools/`), full
per-tensor listing archived alongside this file as
`smollm2-135m-q8_0-inventory.txt` (also not committed, regenerable via
`orcengine_gguf_inspect.exe <path-to-fixture>`).

Grouped by role:

| Tensor group | Count | Encoding |
|---|---|---|
| `token_embd.weight` | 1 | Q8_0 |
| `output.weight` | 1 | Q8_0 |
| `output_norm.weight` | 1 | F32 |
| `blk.N.attn_norm.weight` (30 layers) | 30 | F32 |
| `blk.N.ffn_norm.weight` (30 layers) | 30 | F32 |
| `blk.N.attn_q/k/v/output.weight`, `blk.N.ffn_gate/up/down.weight` (30 layers x 7) | 210 | Q8_0 |

212 + 61 = 273, matches `tensor_count` in the GGUF header exactly.
**No assumption was made that every tensor is Q8_0** -- norm layers
(RMSNorm scale vectors) were confirmed to remain F32, matching
`llama-quantize`'s well-known convention of leaving 1-D normalization
tensors unquantized regardless of the requested target type.

## Tied/untied output-head identity

**Confirmed from actual metadata, not assumed:** this fixture (both the
F32 input and the quantized output) is **UNTIED** --
`orcengine_gguf_inspect` reports `output_semantics: untied` for the F32
input, and `output.weight` is present as its own distinct tensor
(separate file offset, separate quantized bytes) in the Q8_0 output,
confirmed by `map_llama_model()`'s existing
`output_present = tensors.contains("output.weight")` /
`manifest.tied_embeddings = !output_present` logic. This corrects an
earlier, unverified assumption in this session's PR #102 fix commit
message that this specific model was tied -- a real model can appear
"tied" in casual description while its actual GGUF materializes a
physically separate `output.weight` tensor; only the metadata check
is authoritative. (Not corrected via history-rewriting -- that earlier
commit's substantive fix, using `effective_lm_head()` instead of a
hardcoded `token_embedding`, is correct regardless of which specific
model is tied or untied; this note simply records the accurate fact
discovered here.)
