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
"Invocation" below. **What IS committed**: this provenance file, the
tensor inventory dump (`smollm2-135m-q8_0-inventory.txt`), and the
comparison evidence JSONL files -- corrected here after an earlier
draft of this document incorrectly described the inventory file as
also not committed.

## Quantizer archive (source of `llama-quantize.exe`)

- Archive: `llama-cpu.zip`, downloaded from the pinned
  `b10436` release: https://github.com/ggml-org/llama.cpp/releases/tag/b10436
  (`llama-b10436-bin-win-cpu-x64.zip` on that release page; renamed
  locally to `llama-cpu.zip`), same pinning this project already uses
  for `llama_cpp_deployment_oracle.py`.
- Local path: `C:\Users\hardc\AppData\Local\Temp\llamacpp_test\llama-cpu.zip`
- Archive SHA-256: `eebe233f29bd89a6c3c03a1e92c8b97a216a67977f4742aef28005c784b1f02c`
  (recorded here as the archive-level authority; if this project later
  needs to re-download the release, this hash is what to verify against
  the GitHub release asset, not merely trust the filename/tag match).

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
  This shared-`llama.dll`-identity argument is CORROBORATION, not sole
  proof, of `llama-quantize.exe`'s build identity -- a shared DLL
  hash alone cannot rule out a mismatched or tampered `.exe` stub built
  against that same DLL. See the independent, stronger proof below.
- Independent proof (added during Codex remediation): `llama-server.exe`,
  extracted from the SAME `llama-cpu.zip` archive, DOES support
  `--version` directly and prints the identical banner:
  ```
  version: 0.1.0-dev (build 10436, commit 6fed9f6ff)
  built with Clang 20.1.8 for Windows x86_64
  ```
  `llama-server.exe` SHA-256: `ae159e001d959e7a773af61e24d8e7d5d4de565865ec12a0dfd9974dc8ab7ca1`
  (first pinned here -- no prior authority record existed for this
  executable before the Q8-vs-Q8 external-oracle remediation work).
  `llama-server-impl.dll` (where llama-server's actual request-handling
  logic lives; the `.exe` itself is a thin stub) SHA-256:
  `77f8cf124d0222993f7e98c16cecc855bc7b0d31f8005155d75f141944846793`,
  also first pinned here. Both were extracted from the same
  archive-verified `llama-cpu.zip` (see "Quantizer archive" above) as
  `llama-quantize.exe`, `llama-tokenize.exe`, and `llama.dll` -- so all
  four executables and their shared/impl DLLs are proven, by common
  archive provenance plus this direct `--version` confirmation on TWO
  of the four executables (`llama-tokenize.exe` and `llama-server.exe`),
  to be the same pinned `b10436`/`6fed9f6ff` build, not merely
  same-named binaries from an unpinned source.
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
`smollm2-135m-q8_0-inventory.txt` -- **this listing file IS committed**
(it is plain text, not gitignored; only the `.gguf` binary itself is
gitignored -- corrected here, an earlier draft of this document
incorrectly said "also not committed"). Regenerable via
`orcengine_gguf_inspect.exe <path-to-fixture>` if it ever needs
refreshing against a re-quantized fixture.

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

> **CORRECTED (round 6, see `PHASE6_GATE4_CONFIG_PARITY.md` and
> OE-ADR-052):** "UNTIED" below describes tensor-presence structure
> only. `output.weight` was subsequently verified byte-for-byte
> identical to `token_embd.weight` (`np.array_equal` = `True`). The
> accurate current characterization is **logically tied (HF's declared
> `tie_word_embeddings=true` intent), physically duplicated** (two
> byte-identical tensor records), not two independently-varying weight
> matrices. The paragraph below is preserved as originally written for
> its structural/metadata findings, which remain correct as far as they
> go.

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
