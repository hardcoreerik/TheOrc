# Phase 4 bookend virtualization implementation and evidence

> Status: implemented and self-reviewed; awaiting independent freeze review
>
> Branch: `feat/orcengine-phase4-bookend-virtualization`
>
> Frozen parent: `orcengine-phase3-freeze`, peeled commit
> `98dbcf1f370a93574da32dc02ebdcfeff8a60b3d`
>
> Evidence date: 2026-08-16 America/Los_Angeles

Phase 4 answers one question: **does a large logical tensor have to be fully
resident to execute?** For the pinned real F32 SmolLM2-135M artifacts, the
answer is no. Input embedding now materializes one row per unique input token,
and output projection materializes vocabulary-row chunks while retaining the
complete exact logits. The Phase-3 one-layer-at-a-time transformer lifecycle
and arithmetic remain shared.

This is a reference implementation, not a throughput optimization. It adds no
cache, prefetch, mmap policy, tokenizer, KV cache, CUDA, quantized compute,
threading, product integration, or second source format.

## Discovery record

### Observation

Frozen Phase 3 measured these weight-residency peaks:

| Artifact | Full resident weights | Permanent Phase-3 bookends | Phase-3 peak |
|---|---:|---:|---:|
| explicit output | 651,306,240 | 226,494,720 | 240,655,104 |
| tied output | 538,060,032 | 113,248,512 | 127,408,896 |

The largest transformer layer was only 14,160,384 bytes. Once transformer
layers streamed, the input/output matrices became 94.1% of the explicit peak
and 88.9% of the tied peak. The earlier roadmap emphasis on CPU usability was
therefore reconsidered: the model had exposed a more immediate architectural
question about sub-tensor residency.

### What Fringe had already proved

The Fringe Lab Python experiments partitioned a synthetic NumPy output head and
a real-I/O dequantized F32 output-head scratch artifact. They observed stable
argmax/top-five behavior, including at one row per read. The real-I/O run used a
warm OS page cache. It did not prove C++ execution, exact complete logits,
embedding rows, tied physical backing, neutral source semantics, residency
budgets, or the full real-model path.

### Question and hypothesis

The hypothesis was that only requested embedding rows and one output row chunk
need be resident. If true, the permanent bookends should disappear and peak
weight residency should become the largest layer plus the still-permanent final
norm: `14,160,384 + 2,304 = 14,162,688` bytes.

### Experiment and consequence

The implementation requested logical row regions, ran all six output
partitions through unchanged F32 kernels, retained full logits, and compared
against both frozen Phase-3 executables and Hugging Face/PyTorch. Measured peak
was exactly 14,162,688 bytes for explicit and tied artifacts. The next
architecture decision can therefore treat sub-tensor materialization as a
proven primitive, while cache and transport optimization remain future work.

## Architecture

The new neutral seam is deliberately small:

```text
TensorRegion { row_begin, row_count }
        +
LogicalTensor + BackingExtent
        |
        v
TensorRegionMaterializer
        |
        v
MaterializedRegion { ResidentView, backing_bytes_read }
```

The execution core requests logical rows. It does not calculate GGUF offsets,
name GGUF metadata, or assume a logical region is a raw byte slice. The source
adapter translates logical rows to source reads and decoding. The current GGUF
adapter can directly seek F32/F16 row-major regions; a future blocked or
compressed source may read/decode a larger physical extent while returning the
same logical region.

`forward_with_execution_runners` is a narrow boundary around only input
embedding and output projection. The transformer loop remains one shared body;
there is no copied Phase-4 transformer implementation.

The neutral in-memory adapter copies requested rows directly from an F32Raw
`BackingExtent` and passes the same complete test. A source scan over the
neutral forward, source, and streaming files found no `GGUF`, `.gguf`, or
`GGML` references.

## Execution strategies

Input embedding uses a trivial unique-token set. Each unique token row is
materialized once per forward, copied into every matching sequence position,
and released. There is no persistent cache.

Output projection creates a contiguous partition of `[0, vocab)`. Every chunk
is materialized, passed to the unchanged `linear_no_bias`, scattered into its
exact full-logit slice, and released. A validator independently requires every
row exactly once, in order, with no zero, skipped, duplicate, overlapping,
reordered, overflowing, or incomplete region.

For tied models, both operations select the token-embedding `SourceTensor`.
The real tied GGUF has no `output.weight`; no second full backing or resident
copy is created. For untied models, output chunks select the distinct output
head. Contradictory tied/output-head inventories fail during construction.

The final norm remains resident (2,304 bytes). Transformer layers retain the
Phase-3 lifecycle: materialize nine tensors for layer N, execute N, release N.

## Correctness evidence

The artifacts are unchanged from the frozen Phase-3 evidence:

| Artifact | File bytes | SHA-256 | Semantics |
|---|---:|---|---|
| `smollm2-135m.gguf` | 653,091,040 | `FFFAB10C5298F8B1399088E893C1DDD64E48CD7E5020982A5B2A848E445A4AAC` | explicit output |
| `smollm2-135m-tied.gguf` | 538,076,736 | `5CA5C86A7421E5A3105BD93806740BD9CF854B22A9AAB578910F109B7BE3681C` | absent output; embedding reused |

Chunk sizes `1`, `16`, `64`, `256`, `1024`, and `1000` were each run for four
generated steps against both artifacts. `1000` exercises a 152-row remainder
for vocabulary 49,152. Every run was bit-identical to the immutable Phase-3
full-resident and streamed executables for input embedding, all retained taps,
final normalized state, complete logits, selected tokens, and sequence:

```text
[1, 5, 28, 284, 260, 198]
```

Later decode steps continued to report embedding-region and output-region
materializations; they did not fall back to full-resident bookends. A direct
explicit-versus-tied Phase-4 comparison was also bit-identical for complete
logits and taps over all four steps.

The independent checker loaded the original local Hugging Face source with
PyTorch 2.11.0 and Transformers 5.11.0. Against the virtualized executable it
produced the same sequence with maximum absolute error `0.00104618073` and
maximum relative error `0.000288560404`, passing the unchanged Phase-2 rule:
absolute `<= 1e-3` **or** relative `<= 1e-3` for every logit.

## Residency and budget proof

| Artifact | Phase-3 peak | Phase-4 peak | Bytes removed | Reduction | Phase-4 / Phase-3 | Phase-4 / full |
|---|---:|---:|---:|---:|---:|---:|
| explicit | 240,655,104 | 14,162,688 | 226,492,416 | 94.11% | 5.89% | 2.17% |
| tied | 127,408,896 | 14,162,688 | 113,246,208 | 88.88% | 11.12% | 2.63% |

The measured result confirms layer dominance, with the precise correction that
the peak is the largest layer plus the 2,304-byte final norm—not the layer in
isolation.

The deterministic strong budget is 14,162,688 bytes. For both artifacts:

- the immutable Phase-3 streamed executable rejects this budget;
- Phase 4 succeeds exactly at this budget with exact outputs;
- Phase 4 at 14,162,687 bytes rejects cleanly.

The same properties are regression-tested on the neutral synthetic model.
That test corrected an initially incomplete assertion: on the tiny fixture,
Phase 3 can construct at the Phase-4 peak and only rejects when the first layer
is added, so the test now executes the model rather than testing construction
alone.

## I/O and timing evidence

The detailed four-step run uses chunk size 1000 and a persistent process. Four
useful generated tokens amortize one model-open/final-norm startup. These are
single warm/unknown-cache measurements, not a benchmark claim.

| Metric | Explicit | Tied |
|---|---:|---:|
| startup resident bytes | 2,304 | 2,304 |
| startup materialization time | 0.2293 ms | 0.2537 ms |
| embedding backing bytes / generated token | 8,064 | 8,064 |
| output backing bytes / generated token | 113,246,208 | 113,246,208 |
| transformer-layer backing bytes / generated token | 424,811,520 | 424,811,520 |
| total backing bytes / generated token, startup amortized | 538,066,368 | 538,066,368 |
| region materializations / generated token | 53.5 | 53.5 |
| all materializations / generated token | 323.75 | 323.75 |
| wall time / generated token | 7,103.931 ms | 6,530.223 ms |
| embedding time, four steps | 1.3894 ms | 1.1578 ms |
| layer materialization time, four steps | 14,294.1205 ms | 13,161.6648 ms |
| layer execution time, four steps | 13,384.0409 ms | 12,204.4894 ms |
| output projection time, four steps | 676.4198 ms | 694.4009 ms |

The 14 input histories contain 14 unique-token occurrences, so 32,256 total
embedding bytes become 8,064 bytes per generated token. Output projection must
still read the complete 113,246,208-byte matrix per generated token; Phase 4
reduces residency, not I/O volume. Chunk size 1 produced 49,155.5 region
materializations per generated token and was slower. This is evidence for a
future cache/I/O phase, not authorization to add one here.

The six-partition campaign wall times for four steps were:

| Chunk rows | Explicit seconds | Tied seconds |
|---:|---:|---:|
| 1 | 34.387 | 37.255 |
| 16 | 25.900 | 29.655 |
| 64 | 25.284 | 29.019 |
| 256 | 26.136 | 27.837 |
| 1024 | 24.973 | 26.160 |
| 1000 | 25.276 | 27.825 |

## Observable inference

Phase 4 adds measured events:

- `TensorRegionRequested`;
- `TensorRegionMaterializationBegin`;
- `TensorRegionMaterialized`;
- `TensorRegionReleased`.

Region events carry semantic tensor role, logical row begin/count, resident and
region bytes, opaque backing identity, operation (`InputEmbedding` or
`OutputProjection`), and measured materialization duration. Existing
`Measured`, `Derived`, and `Interpreted` categories remain distinct; current
region events are `Measured`. Observer off/on output is bit-identical. A
throwing observer remains isolated by the inherited Phase-3 behavior.

## Safety re-attack and regressions

The focused Phase-4 test covers:

- row begin at/past end, row end past end, zero rows, and 64-bit extent overflow;
- malformed rank/dimensions and short source data;
- wrong returned region shape/count and impossible reported backing bytes;
- partial final chunk plus missing, duplicate, overlapping, skipped, reordered,
  oversized, incomplete, and overflowed partitions;
- tied model with an output head, untied model without one, and duplicate
  semantic embedding;
- unique-token row deduplication and complete exact logits for all chunk sizes;
- exact peak, peak-minus-one, and Phase-3-under-Phase-4-budget behavior;
- neutral in-memory region materialization and observer event truth.

A copied real explicit F32 GGUF was re-attacked by changing the first float of
the baseline-selected output row to 1000.0. Parsing still succeeded, but exact
differential execution detected it and selection changed from 28 to 30. The
temporary 653 MB copy was deleted automatically.

Failures discovered while building the evidence are retained here:

- the first focused test used direct map equality, but `ActivationBuffer` has
  no equality operator; it was replaced with explicit dimensions/data checks;
- the tiny strong-budget test initially stopped at construction and exposed
  the lifecycle distinction described above;
- the first Release matrix pointed at a nonexistent frozen snapshot path; the
  corrected immutable Phase-3 executable was used;
- the first strict command replaced CMake's default exception flag and failed
  with C4530; `/EHsc` was added explicitly and the strict build then passed;
- no correctness defect was found by the real chunk, tied, HF, corruption, or
  sanitizer campaigns.

## Validation matrix

| Lane | Configuration | Deterministic result | Real campaigns |
|---|---|---:|---|
| Debug | MSVC Debug | 13/13 | not repeated; Release evidence is authoritative |
| Release | MSVC Release | 13/13 | explicit/tied six-chunk four-step, tied equivalence, HF, corruption |
| strict MSVC | Release, `/EHsc /W4 /WX /permissive-` | 13/13 | bounded deterministic suite only |
| MSVC ASan | RelWithDebInfo, `/EHsc /fsanitize=address /W4` | 13/13 | bounded region/lifecycle suite only |

All lanes include Phase-1 frozen tests, Phase-2 GGUF conformance/mutations and
large-sparse test, Phase-3 streaming/cross-freeze tests, and the Phase-4
bookend test. Large real F32 campaigns are Release-only to keep warning and
sanitizer lanes bounded.

## Reproduction

```powershell
$root = 'F:\Ai\OrchestratorIDE-phase4-bookend-virtualization'
$build = 'F:\Ai\_build_orcengine_p4'
$frozen = 'F:\Ai\_build_orcengine_p3_hardening\Release\orcengine_phase1_snapshot_current.exe'
$frozenFull = 'F:\Ai\_build_orcengine_p3_hardening\phase2\Release\orcengine_gguf_forward.exe'
$frozenStream = 'F:\Ai\_build_orcengine_p3_hardening\Release\orcengine_gguf_streaming_forward.exe'
$explicit = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m.gguf'
$tied = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m-tied.gguf'
$hf = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m'

cmake -S "$root\Tools\OrcEnginePhase4" -B $build `
  -DORCENGINE_FROZEN_PHASE1_SNAPSHOT=$frozen
cmake --build $build --config Debug --parallel
ctest --test-dir $build -C Debug --output-on-failure
cmake --build $build --config Release --parallel
ctest --test-dir $build -C Release --output-on-failure

$current = "$build\phase3\Release\orcengine_gguf_streaming_forward.exe"
python "$root\Tools\OrcEnginePhase4\tests\real_bookend_check.py" `
  $frozenFull $frozenStream $current $explicit 4 14162688 1 16 64 256 1024 1000
python "$root\Tools\OrcEnginePhase4\tests\real_bookend_check.py" `
  $frozenFull $frozenStream $current $tied 4 14162688 1 16 64 256 1024 1000
python "$root\Tools\OrcEnginePhase4\tests\real_tied_equivalence.py" `
  $current $explicit $tied 4 1000
python "$root\Tools\OrcEnginePhase2\tests\hf_pytorch_forward_check.py" `
  $current $explicit $hf 4 --virtualize-bookends --output-chunk-rows 1000
python "$root\Tools\OrcEnginePhase4\tests\corrupt_f32_bookend_check.py" `
  "$build\phase3\phase2\Release\orcengine_gguf_inspect.exe" $current $explicit
```

Strict uses `-DCMAKE_CXX_FLAGS='/EHsc /W4 /WX /permissive-'`. ASan uses
`-DCMAKE_CXX_FLAGS='/EHsc /fsanitize=address /W4'`, builds RelWithDebInfo, and
prepends the matching MSVC `Hostx64\x64` runtime directory to `PATH` before
CTest. The neutral leakage scan is:

```powershell
rg -n -i 'gguf|\.gguf|ggml' `
  Tools/OrcEnginePhase1/include/orcengine/forward.hpp `
  Tools/OrcEnginePhase1/src/forward.cpp `
  Tools/OrcEnginePhase3/include/orcengine/model_source.hpp `
  Tools/OrcEnginePhase3/include/orcengine/streaming.hpp `
  Tools/OrcEnginePhase3/src/streaming.cpp
```

No output is the passing result.

## Evidence boundary and unsupported capabilities

This proves logical row virtualization for dense materializable F32/F16 GGUF
and a neutral in-memory F32 source. It does not prove efficient blocked or
quantized region decoding, cold-storage behavior, async I/O, cache policy,
prefetch, mmap, GPU residency, tokenizer input, KV-cached decode, stochastic
sampling, multi-context scheduling, or production integration. Complete logits
remain activation memory and are intentionally outside weight-residency
accounting. Process working set includes allocator, executable, activation, and
OS effects and is not claimed to equal the weight ledger.

The raw compact campaign record is
`Tools/OrcEnginePhase4/evidence/phase4_campaign.json`. The checkers compare full
logit arrays in memory and emit summaries; multi-megabyte duplicated logit JSON
was deliberately not committed.

## Proposed verdict

**READY FOR INDEPENDENT FREEZE REVIEW.** Do not tag or begin Phase 5 until an
independent reviewer attacks the implementation and evidence.
