# OrcEngine Phase 2 freeze hardening

Date: 2026-08-16 America/Los_Angeles

Branch: `feat/orcengine-phase2-gguf`

Starting HEAD: `17b91f1230d6e1bc6785594568e4351ab916b7c8`

Trusted Phase-1 base: `b27bc9323b89b9151c811c30d41145bb672a2943`
(`orcengine-phase1-freeze`)

Verdict after the closure work and re-attack: **ACCEPT FOR PHASE-2 FREEZE**.
This verdict applies only to the reference path documented here. It does not
approve Phase 3, quantized execution, CUDA, paging, tokenizer work, or product
integration.

## Why the freeze was held

The first Phase-2 implementation established strict GGUF v3 indexing, semantic
mapping for one dense Llama profile, F32/F16 materialization, real Q4 indexing,
and real F32 execution through frozen Phase-1 math. Review correctly held the
freeze for three evidence gaps and one small specification question:

1. A default 4 GiB parser limit existed, but no executable evidence proved that
   it was configurable policy rather than a 32-bit architectural ceiling.
2. Real F32 execution was compared with the Phase-0 Python oracle, which shared
   project history with the converter. A direct comparison with the original
   Hugging Face/PyTorch model was still needed.
3. A real quantized tied artifact indexed correctly, but no real F32 tied GGUF
   had been executed and compared with an explicit output-head artifact.
4. Metadata keys were ASCII-checked but not fully checked against GGUF's
   hierarchical lower-snake-case rule.

The work below preserves that history. It does not rewrite these gaps as if the
final checks had always existed.

## Closure 1: extents beyond 4 GiB

### Hypothesis and reason

Original belief: the 4 GiB default was a defensive policy limit because parser
sizes and offsets were already represented as `uint64_t`.

Review challenge: types alone do not prove the entire path is free from
truncation, unchecked addition, eager allocation, or hidden 32-bit assumptions.

Hypothesis: raising `GgufLimits.max_file_bytes` should allow a valid tensor whose
absolute extent begins beyond `UINT32_MAX`, while the default policy still
rejects it and malformed boundary cases fail closed.

### Setup and artifact

`test_gguf_large_sparse.cpp` creates a deterministic GGUF v3 file whose single
F32 tensor begins at relative offset `4,294,967,296`. The resulting absolute
offset is `4,294,967,424`; encoded length is four bytes; logical file size is
`4,294,967,428` bytes. On Windows the file is marked sparse before its logical
length is set, so this does not write or allocate a 4 GiB payload.

Measured Windows allocation was 131,072 bytes. The parser's retained manifest
estimate was 168 bytes. No tensor payload was materialized.

### Expected result

- Default 4 GiB policy rejects the file.
- An 8 GiB limit accepts it.
- Parsed absolute offset remains exactly `4,294,967,424`.
- The offset fits the signed 64-bit `BackingExtent` contract without truncation.
- A file truncated by one payload byte is rejected by EOF bounds checking.
- A descriptor near `UINT64_MAX` is rejected by checked-addition overflow.

### Actual result

All checks passed:

```text
LARGE SPARSE GGUF PASS: logical_size=4294967428 offset=4294967424
encoded_length=4 estimated_manifest=168 allocated=131072
```

No architectural 4 GiB cap was found, so no parser-limit code was changed. The
new test is the regression proof that the default remains policy.

### What this proves and does not prove

This proves 64-bit file size, alignment, offset, length, EOF, and retained
extent handling for sparse files beyond 4 GiB without payload materialization.
It does not prove performance on multi-gigabyte physical storage, operating
systems without large-file support, or execution of a model larger than memory.

## Closure 2: independent Hugging Face/PyTorch execution

### Hypothesis and reason

Original belief: the Phase-0 Python differential was sufficient because its
math had already been reviewed against independent references.

Review challenge: the Phase-0 converter and oracle were both project-owned.
That left a possible common-history path from conversion assumptions to the
expected outputs.

Hypothesis: the original Hugging Face model, loaded directly by Transformers and
PyTorch, should produce the same greedy tokens and logits as OrcEngine reading
the converted GGUF for the same explicit token IDs.

### Setup and artifacts

- Source: local pinned `HuggingFaceTB/SmolLM2-135M`, revision
  `93efa2f097d58c2a74874c7e644dbc9b0cee75a2`.
- Source `model.safetensors` SHA-256:
  `80521B40281D6CE74E35C9282C22539E75AA0AC8578892B2A59955EF78D55DA1`.
- Converted explicit-output GGUF: 653,091,040 bytes, SHA-256
  `FFFAB10C5298F8B1399088E893C1DDD64E48CD7E5020982A5B2A848E445A4AAC`.
- Input token IDs: `[1, 5]`.
- Four greedy generation steps in Release; one bounded step in Debug.
- Reference load: `AutoModelForCausalLM.from_pretrained(...,
  dtype=torch.float32, local_files_only=True, attn_implementation="eager")`.
- Comparison: every last-token logit must satisfy `(absolute <= 1e-3) OR
  (relative <= 1e-3)`; selected tokens must be exact.

`hf_pytorch_forward_check.py` invokes the C++ forward executable and imports no
Phase-0 converter or oracle code. Conversion is still the artifact-production
step under test; expected execution values come directly from Hugging
Face/PyTorch.

### Expected result

The source model and OrcEngine should independently generate the same four
tokens, with all last-token logits inside the declared tolerance.

### Actual result

```text
HF/PYTORCH END-TO-END PASS: torch=2.11.0+cu128 transformers=5.11.0
steps=4 sequence=[1, 5, 28, 284, 260, 198]
max_abs=0.00104618073 max_rel=0.000288560404
hf_load_s=0.594 hf_forward_s=0.184
```

The largest absolute difference slightly exceeded `1e-3`, but its relative
difference was well inside the frozen absolute-or-relative rule. Selected
tokens matched exactly at all four steps.

### What this proves and does not prove

This independently checks:

```text
source Hugging Face model -> conversion -> GGUF interpretation
-> OrcEngine materialization -> frozen Phase-1 math -> same answer as PyTorch
```

It does not prove tokenizer equivalence because explicit token IDs are used. It
does not prove every model architecture, dtype, prompt, or generation policy.
It is a dense F32 SmolLM2/Llama-profile closure test.

## Closure 3: real tied-output execution

### Hypothesis and reason

Original belief: the real Q4 model's absent `output.weight` plus synthetic tied
fixtures adequately proved the semantic mapping.

Review challenge: that evidence stopped at indexing for the real artifact. It
did not execute a real GGUF through the tied mapping.

Hypothesis: removing a byte-identical explicit `output.weight` from the real F32
artifact should select the loader's tied-output path and produce bit-identical
logits, taps, and greedy tokens.

### Setup and artifacts

`derive_real_tied_gguf.py` uses the installed `gguf-py` writer. It first proves
that `output.weight` and `token_embd.weight` contain identical bytes, then writes
all metadata and tensors except `output.weight`.

- Explicit artifact: 273 F32 tensors, 653,091,040 bytes, SHA-256
  `FFFAB10C5298F8B1399088E893C1DDD64E48CD7E5020982A5B2A848E445A4AAC`.
- Tied artifact: 272 F32 tensors, 538,076,736 bytes, SHA-256
  `5CA5C86A7421E5A3105BD93806740BD9CF854B22A9AAB578910F109B7BE3681C`.
- Independent `gguf-py`/OrcEngine descriptor comparison: exact for all 272
  tensors; tied classification true.
- A second derivation in `%TEMP%` produced the same byte count and SHA-256.
- Input token IDs and generation steps match the independent-reference test.

The script intentionally uses existing `gguf-py`; no second GGUF writer was
added. An initial evidence command incorrectly passed `--source` and `--target`
to the positional-only script and produced no file. The corrected positional
invocation succeeded. This was a command error, not an engine or artifact
failure, and is retained here rather than erased from the record.

### Expected result

- The explicit artifact maps 273 tensors with `tied_embeddings=false`.
- The tied artifact maps 272 tensors with `tied_embeddings=true`.
- Full logits, selected tokens, and first-step taps are exactly equal.

### Actual result

```text
REAL TIED OUTPUT PASS: steps=4 sequence=[1, 5, 28, 284, 260, 198]
logits=bit-identical taps=bit-identical explicit_mapped=273 tied_mapped=272
```

This proves the real Phase-2 loader's tied semantic mapping participates in
execution and is equivalent to an explicit byte-identical output head.

### What this proves and does not prove

GGUF has no standardized metadata boolean that separately declares tied versus
untied embeddings; the supported Llama profile infers tied output from absent
`output.weight`. Therefore a metadata-level “tied/untied contradiction” cannot
be encoded. Shape-invalid present output heads remain rejected by the existing
`wrong_output_shape` fixture, and a model missing both output and token
embedding now has an explicit regression fixture.

## Metadata-key specification decision

The parser now chooses strict canonical behavior. Metadata keys must be ASCII
hierarchical lower-snake-case: non-empty dot-separated segments containing
lowercase alphanumeric words separated by single underscores. Uppercase,
hyphens, non-ASCII, empty segments, and leading, trailing, or repeated
underscores are rejected. This follows the canonical key and tensor-name rules
in the [GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md).

Reviewing the same GGUF rule exposed a separate bug: tensor names were being
read with the general 64 MiB string cap, while GGUF tensor names are limited to
64 bytes. The parser now enforces 64 bytes at the tensor-name read boundary.

New malformed fixtures cover seven metadata-key failures and a 65-byte tensor
name. The real Q4 and F32 artifacts still pass, so the stricter policy did not
break the supported evidence set.

## Re-attack after closure

| Attack | Regression/evidence | Result |
|---|---|---|
| 32-bit offset truncation | Sparse tensor absolute offset `4,294,967,424` | Exact value retained |
| Extent overflow near 4 GiB | Sparse valid extent plus `UINT64_MAX-31` descriptor | Valid accepted with raised policy; overflow rejected |
| Malformed sparse bounds | Sparse file truncated by one byte | Rejected by EOF bound |
| Tied/untied contradiction | Present wrong-shaped output head; missing token embedding | Both rejected; no standard GGUF tie flag exists |
| Missing output head | Real absent-output tied artifact | Valid and bit-identical in execution |
| Bogus metadata key | Seven new malformed key fixtures | All rejected |
| Corrupted F32 artifact | Finite `2^31` output-weight mutation | Runs but logits differ by more than 1.0, proving detection |
| Non-finite F32 artifact | Quiet-NaN output-weight mutation | Fails closed before successful execution |
| Oversized tensor name | 65-byte tensor name | Rejected at parse boundary |
| Vacuous/incomplete real decode trace | Zero-step direct calls and exact returned-length checks | All three execution harnesses reject |

No Phase-1 math weakness was found. The re-attack confirmed two Phase-2
validation weaknesses—the permissive underscore handling and incorrect tensor
name cap—and both are now executable regressions. It also found that the three
real-execution Python checkers relied on CMake to provide a positive step count
and did not independently require the returned trace length to equal that
count. All three now reject nonpositive counts and incomplete traces; direct
zero-step calls failed 3/3 as required.

## Deterministic fixture evidence

The generator now produces 41 GGUF files: seven valid/indexable and 34
malformed. Two independent generations were byte-identical. The aggregate
sorted manifest SHA-256 was:

`B2A59E94BA8E5DD76FC54B6ABC4CC4069BF49CD495F64719B7C929D978E371B3`

The conformance executable reports 34 malformed rejections, seven valid/index
cases, four forward-equivalence checks, and two real-payload corruption
regressions. The deterministic mutation harness still performs 512 bounded
mutations; 72 were cleanly rejected and 440 remained valid.

## Explicit validation matrix

“Yes” means the row actually ran in that lane. “No” means the target was not
configured in that lane; it does not mean a skipped test was counted as pass.

| Evidence | Debug | Release | strict MSVC | MSVC ASan |
|---|---:|---:|---:|---:|
| Frozen Phase-1 suite (7 tests) | Yes | Yes | Yes | Yes |
| GGUF valid fixtures | Yes | Yes | Yes | Yes |
| GGUF malformed fixtures | Yes | Yes | Yes | Yes |
| Mutation harness (512) | Yes | Yes | Yes | Yes |
| Real Q4 indexing/cross-reader | Yes | Yes | No | Yes |
| Real F32 Phase-0 differential execution | Yes, 1 step | Yes, 4 steps | No | No |
| >4 GiB sparse test | Yes | Yes | Yes | Yes |
| Real tied-output execution | Yes, 1 step | Yes, 4 steps | No | No |
| Independent HF/PyTorch comparison | Yes, 1 step | Yes, 4 steps | No | No |

Lane totals and measured CTest times:

| Lane | Compiler flags/configuration | Result | Time |
|---|---|---:|---:|
| Debug | MSVC multi-config Debug | 14/14 | 547.07 s |
| Release | MSVC multi-config Release | 14/14 | 125.54 s |
| strict MSVC | `/EHsc /W4 /WX /permissive- /sdl`, Release | 10/10 | 6.33 s |
| MSVC ASan | `/EHsc /fsanitize=address /W4`, RelWithDebInfo | 11/11 | 12.60 s |

The strict lane intentionally contains no real-artifact options. The ASan lane
contains the real Q4 cross-reader but not the three expensive F32 execution
comparisons. Those are explicit matrix exclusions, not “applicable test”
wording. Debug uses one generation step because unoptimized scalar
full-recompute execution is slow; Release retains all four required steps.

MSBuild emitted `MSB8029` because out-of-tree builds were under `%TEMP%`; this is
a build-location warning, not a source warning. The ASan linker also reported
the normal incremental-linking incompatibility warning. No C++ warning escaped
the strict `/WX` gate.

## Exact reproduction commands

Run from `F:\Ai\OrchestratorIDE-phase2-gguf` in a Developer PowerShell with
Python dependencies already installed. The local artifact paths are part of
the evidence identity.

```powershell
$build = Join-Path $env:TEMP 'orcengine-phase2-hardening-build'
$q4 = 'C:\Users\hardc\AppData\Roaming\OrchestratorIDE\Models\SmolLM2-360M-Instruct-Q4_K_M.gguf'
$f32 = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m.gguf'
$hf = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m'
$tied = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m-tied.gguf'

cmake -S .\Tools\OrcEnginePhase2 -B $build `
  -DORCENGINE_REAL_GGUF=$q4 `
  -DORCENGINE_REAL_F32_GGUF=$f32 `
  -DORCENGINE_HF_SOURCE_DIR=$hf `
  -DORCENGINE_REAL_TIED_F32_GGUF=$tied
cmake --build $build --config Debug --parallel
cmake --build $build --config Release --parallel
ctest --test-dir $build -C Debug --output-on-failure
ctest --test-dir $build -C Release --output-on-failure
```

Strict warning lane:

```powershell
$strict = Join-Path $env:TEMP 'orcengine-phase2-hardening-strict'
cmake -S .\Tools\OrcEnginePhase2 -B $strict `
  '-DCMAKE_CXX_FLAGS=/EHsc /W4 /WX /permissive- /sdl'
cmake --build $strict --config Release --parallel
ctest --test-dir $strict -C Release --output-on-failure
```

AddressSanitizer lane:

```powershell
$asan = Join-Path $env:TEMP 'orcengine-phase2-hardening-asan'
cmake -S .\Tools\OrcEnginePhase2 -B $asan `
  '-DCMAKE_CXX_FLAGS=/EHsc /fsanitize=address /W4' `
  -DORCENGINE_REAL_GGUF=$q4
cmake --build $asan --config RelWithDebInfo --parallel
ctest --test-dir $asan -C RelWithDebInfo --output-on-failure
```

Tied artifact derivation and independent descriptor check:

```powershell
python .\Tools\OrcEnginePhase2\tests\derive_real_tied_gguf.py $f32 $tied
python .\Tools\OrcEnginePhase2\tests\cross_reader_check.py `
  (Join-Path $build 'Release\orcengine_gguf_inspect.exe') $tied
```

Direct closure checks:

```powershell
& (Join-Path $build 'Release\test_gguf_large_sparse.exe')
python .\Tools\OrcEnginePhase2\tests\hf_pytorch_forward_check.py `
  (Join-Path $build 'Release\orcengine_gguf_forward.exe') $f32 $hf 4
python .\Tools\OrcEnginePhase2\tests\real_tied_forward_check.py `
  (Join-Path $build 'Release\orcengine_gguf_inspect.exe') `
  (Join-Path $build 'Release\orcengine_gguf_forward.exe') $f32 $tied 4
```

## Remaining limits

- Only GGUF v3 little-endian files and one dense Llama semantic profile are in
  the supported execution boundary.
- Q4 and other selected quantized encodings are index-only; no quantized tensor
  is materialized or executed.
- The >4 GiB artifact is sparse and synthetic. It proves indexing arithmetic,
  not real-storage throughput or multi-gigabyte execution.
- Hugging Face comparison uses explicit token IDs, greedy decoding, and one
  pinned F32 model. Tokenizer behavior remains unproven.
- ASan and strict `/WX` lanes do not execute the large F32 comparisons; their
  exclusions are explicit in the matrix.
- No llama.cpp executable/module was locally available for a second direct
  Phase-2 execution comparison. Direct Hugging Face/PyTorch now supplies the
  required independent end-to-end reference.
- No Phase-3 work has begun as part of this hardening.
