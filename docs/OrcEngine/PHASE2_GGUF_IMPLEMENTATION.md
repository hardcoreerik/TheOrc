# OrcEngine Phase 2: GGUF ingestion

Status: freeze hardening complete; **ACCEPT FOR PHASE-2 FREEZE** on
`feat/orcengine-phase2-gguf`. The review challenge, closure experiments,
failed command, explicit validation matrix, and remaining limits are preserved
in [PHASE2_FREEZE_HARDENING.md](PHASE2_FREEZE_HARDENING.md).

Formal closure: **COMPLETE / FROZEN** on 2026-08-16. Trusted commit
`b8e06a0058a56f2ae9fbd1f92ae0bade40b88ec7`; immutable pushed annotated tag
`orcengine-phase2-freeze`. The tag peels exactly to the trusted commit and must
not be moved. Later documentation commits are not part of the frozen
implementation.

Trusted base: `orcengine-phase1-freeze` at
`b27bc9323b89b9151c811c30d41145bb672a2943`. Phase 2 extends the storage
contract and calls the frozen Phase-1 operators; it does not change transformer
math.

## Proven support boundary

| Capability | Phase-2 status |
|---|---|
| GGUF version | Version 3, little-endian only |
| Metadata values | All GGUF v3 scalar types and nested arrays, bounded and UTF-8 validated |
| Architecture | One explicit dense Llama profile |
| Tensor indexing | F32, F16, Q4_0, Q4_1, Q5_0, Q5_1, Q8_0, Q2_K, Q3_K, Q4_K, Q5_K, Q6_K, Q8_K |
| CPU materialization | F32 directly; F16 decoded to F32 |
| CPU execution | F32 Phase-1 resident values only |
| Quantized execution | Not implemented; supported quantized encodings remain index-only |
| Tied output | Supported when the Llama profile permits absent `output.weight` |
| Untied output | Supported when `output.weight` is present and shape-valid |
| Tokenizer | Not implemented; differential tests pass explicit token IDs |
| CUDA, paging, KV cache, batching | Not implemented |
| TheOrc product integration | Not implemented |

This is not a general GGUF framework. Non-Llama artifacts are classified as
`UnsupportedArtifact`. Llama MoE, partial rotary dimensions, unsupported tensor
encodings, incomplete inventories, and contradictory shapes fail closed.

## Data path

`index_gguf()` validates the file envelope, metadata, descriptors, encoding
block sizes, aligned relative offsets, absolute extents, EOF bounds, and overlap
before exposing tensor ranges. It never reads tensor payload bytes.

`map_llama_model()` maps source names such as `blk.0.attn_q.weight` to semantic
tensor identities, validates the complete required inventory and shapes, and
creates file-backed `BackingExtent` values. GGUF names remain source-format
metadata rather than permanent engine identities.

`materialize_gguf_tensor()` reads exactly one validated extent and produces an
F32 `ResidentView`. `materialize_gguf_model()` does that explicitly for all
required tensors before calling the unchanged Phase-1 `forward()` function.

Default parser limits are a 4 GiB file, 100,000 metadata entries, 10,000
tensors, 65,535-byte metadata keys, 64 MiB metadata strings, 10,000,000 array
elements, 512 MiB cumulative metadata allocation, array depth 8, rank 4, and
dimensions no larger than 2^31-1. All
count multiplication, offset addition, alignment, and allocation boundaries are
checked before use. Quantized files must contain uint32
`general.quantization_version`.

The 4 GiB file limit is policy, not an architectural ceiling. A deterministic
sparse-file regression places a valid tensor at absolute offset 4,294,967,424,
proves exact 64-bit retention under an 8 GiB configured limit, and rejects both
one-byte EOF truncation and near-`UINT64_MAX` extent overflow. The 4.29 GB
logical file allocated 131,072 physical bytes and indexing retained an estimated
168-byte manifest; no payload was materialized.

Metadata keys are validated as canonical ASCII hierarchical lower-snake-case.
Tensor names use the GGUF-specific 64-byte maximum rather than the general
metadata-string policy.

## Deterministic fixtures and negative testing

The build generates 41 byte-identical GGUF fixtures: seven valid/indexable
artifacts and 34 malformed artifacts. The valid set covers a structural
baseline, tied and untied inventories, F16, metadata/tensor descriptor
reordering, 64-byte alignment, and unsupported-architecture classification.
The malformed set covers envelope, truncation, count and dimension overflow,
UTF-8, metadata types/arrays/bools, duplicate names, alignment, overlap, EOF,
missing metadata/tensors, bad shapes, unsupported dtype, and missing
quantization metadata. Freeze hardening added noncanonical metadata keys, a
65-byte tensor name, and a missing token embedding.

Two independent generations produced the same aggregate SHA-256 manifest:

`B2A59E94BA8E5DD76FC54B6ABC4CC4069BF49CD495F64719B7C929D978E371B3`

The mutation harness performs 512 deterministic bounded byte mutations. A run
is valid only if the input parses successfully or throws a controlled validation
error; crashes, hangs, accidental large allocations, and undefined behavior are
failures.

All three real-execution checkers independently require a positive requested
step count and an exact returned trace length, preventing zero-step or truncated
traces from passing vacuously.

## Real-artifact evidence

### Quantized indexing target

`SmolLM2-360M-Instruct-Q4_K_M.gguf` was selected because it was already local,
small enough for repeatable validation, Llama-compatible, and exercised mixed
real encodings without pretending quantized execution exists.

- SHA-256: `2FA3F013DCDD7B99F9B237717FA0B12D75BBB89984CC1274BE1471A465BAC9C2`
- Size: 270,590,880 bytes
- GGUF v3; 37 metadata entries; 290 tensors; 32-byte alignment
- Llama: vocab 49,152; hidden 960; FFN 2,560; 32 layers; 15 query heads; 5 KV heads; context 8,192
- Tied output: `output.weight` is absent and the explicit Llama profile aliases the token embedding
- Mapping: 290/290 tensors, zero unused, full-model classification
- Encodings observed: F32, Q4_K, Q5_0, Q6_K, Q8_0
- Materialization: not executable because the quantized tensors are deliberately index-only
- Independent `gguf-py` comparison: exact agreement for every tensor name, dimension, encoding, absolute offset, and encoded length
- Release metadata parse / tensor-index / total time: 26.806 ms / 0.356 ms / 27.189 ms
- Measured inspector peak working set: 22,822,912 bytes (8.43% of file size)
- Estimated metadata allocation: 7,853,897 bytes
- Estimated tensor-index allocation: 46,873 bytes
- Estimated retained metadata plus index allocation: 7,900,770 bytes

The peak working set includes process/runtime overhead; the estimate is not
presented as an OS memory measurement. Both show that opening/indexing does not
materialize the 270 MB model.

### F32 execution target

`HuggingFaceTB/SmolLM2-135M` was downloaded at the already approved revision
`93efa2f097d58c2a74874c7e644dbc9b0cee75a2`, verified against Hub checksums,
and converted by the existing reviewed Phase-0 converter.

- Source `model.safetensors` SHA-256: `80521B40281D6CE74E35C9282C22539E75AA0AC8578892B2A59955EF78D55DA1`
- Converted GGUF SHA-256: `FFFAB10C5298F8B1399088E893C1DDD64E48CD7E5020982A5B2A848E445A4AAC`
- Converted size: 653,091,040 bytes
- GGUF v3; 21 metadata entries; 273 F32 tensors; 32-byte alignment
- Llama: vocab 49,152; hidden 576; FFN 1,536; 30 layers; 9 query heads; 3 KV heads; context 8,192
- Distinct output-head backing: shape-valid `output.weight` is present, so the
  Phase-2 profile maps a separate `lm_head`. The Phase-0 converter intentionally
  duplicated the source model's tied embedding bytes; this is a storage/inventory
  classification, not a claim that the original Hugging Face weights differ.
- Mapping: 273/273 tensors, zero unused, materializable full-model classification
- Independent `gguf-py` descriptor comparison: exact

With initial token IDs `[1, 5]`, both OrcEngine C++ and the independent Phase-0
Python oracle greedily produced:

`[1, 5, 28, 284, 260, 198]`

The four generated tokens matched independently at every step. Every compared
element passed the frozen Phase-1 rule `(absolute <= 1e-3) OR (relative <=
1e-3)`. First-step taps covered input embedding, layer-0 norm, Q/K/V,
attention output, FFN down projection, final normalized state, and logits. The
largest reported absolute error was 0.00159764 in the final normalized state;
its relative error was 0.000694406 and therefore passed the declared gate. The
largest step-0 logit error was 0.0012331 absolute / 0.000284181 relative; later
steps were at most 0.000134468 absolute.

A direct Phase-2 llama.cpp run was not performed because no local `llama-cli`,
`llama-server`, or `llama_cpp` module was available. This is reported as an
uncovered llama.cpp comparison, not agreement. The required independent
end-to-end closure instead loads the original source directly through Hugging
Face Transformers 5.11.0 and PyTorch 2.11.0, imports no Phase-0 oracle or
converter code, and compares every last-token logit. It produced the same
`[1, 5, 28, 284, 260, 198]` sequence over four Release steps with maximum
absolute/relative differences 0.00104618073/0.000288560404, satisfying the
declared absolute-or-relative rule.

### Real tied-output execution target

A second real F32 artifact was deterministically derived only after proving
that the explicit `output.weight` and `token_embd.weight` bytes were identical.
The tied artifact omits `output.weight`, contains 272 F32 tensors, is
538,076,736 bytes, and has SHA-256
`5CA5C86A7421E5A3105BD93806740BD9CF854B22A9AAB578910F109B7BE3681C`.
A second derivation was byte-identical, and `gguf-py` agreed with OrcEngine on
every descriptor. Executing the explicit and tied artifacts produced exact
full logits, exact first-step taps, and the exact same four-token greedy
sequence. This closes real execution of the GGUF tied mapping rather than only
its indexing.

## Verification commands and outcomes

The normal multi-config build included the Q4 indexing target, explicit F32
artifact, original Hugging Face source, and tied F32 artifact. The complete
row-by-row matrix and reproduction commands are in the freeze-hardening report.

- Release: 14/14 passed in 125.54 seconds; all three real F32 comparisons used four generation steps.
- Debug: 14/14 passed in 547.07 seconds; all three real F32 comparisons used one bounded generation step.
- Strict MSVC: global `/EHsc /W4 /WX /permissive- /sdl`, Release — 10/10 configured tests passed in 6.33 seconds.
- MSVC ASan: global `/EHsc /fsanitize=address /W4`, RelWithDebInfo — 11/11 configured tests passed in 12.60 seconds, including real Q4 cross-reader indexing.

The strict lane did not configure real artifacts. The ASan lane configured the
real Q4 artifact but not the expensive F32 execution comparisons. These are
explicit exclusions rather than implicit “applicable test” claims.

The only build messages outside the source warning policy were MSBuild's
`MSB8029` notices that the requested out-of-tree build directories were under
the Windows temporary directory. They are build-location warnings, not C++
diagnostics.

## Review boundary

Phase 2 disproved the idea that source encoding and resident type should be the
same: the F16 fixture correctly becomes an F32 resident view, while quantized
tensors can be fully indexed without being executable. It also showed that an
explicit `output.weight` in the converted real artifact must be treated as a
distinct output head even when another artifact in the same architecture uses
the tied profile rule.

Nothing found requires a change to frozen Phase-1 transformer math. Phase 3,
quantized kernels, CUDA, paging, performance work, tokenizer implementation,
and product integration remain intentionally stopped at the Phase-2 freeze.
