# OrcEngine Phase 3: real-model streaming working-set reference

Status: **HARDENED — ACCEPT FOR PHASE-3 FREEZE; NOT YET TAGGED**

> Freeze-hardening evidence supersedes the original one-step performance
> interpretation and direct-GGUF core design described historically below. See
> [PHASE3_FREEZE_HARDENING.md](PHASE3_FREEZE_HARDENING.md) for the controlled
> budget proof, format-neutral materializer seam, observer contract, persistent
> decode timings, frozen-binary differential, and final matrix. Historical text
> remains here to preserve how the implementation reached review.

Date: 2026-08-16 America/Los_Angeles

## Outcome first

The Phase-3 hypothesis held. Both real F32 SmolLM2 artifacts execute directly
from their Phase-2 `BackingExtent` inventory with at most one transformer
layer resident at a time. Full-resident and streamed execution are
bit-identical at every compared tap, every logit, every greedy selection, and
the four-step sequence:

```text
[1, 5, 28, 284, 260, 198]
```

The exact predicted engine-owned peaks were observed:

| Artifact | Full weights | Predicted streamed peak | Measured peak | Ratio |
|---|---:|---:|---:|---:|
| explicit output | 651,306,240 | 240,655,104 | 240,655,104 | 36.95% |
| tied output | 538,060,032 | 127,408,896 | 127,408,896 | 23.68% |

The implementation is a no-cache lexical lifetime proof, not a memory manager.
No Phase-4 work was started.

## Baseline and isolation

- Frozen Phase-2 implementation: `b8e06a0058a56f2ae9fbd1f92ae0bade40b88ec7`.
- Immutable authority tag: `orcengine-phase2-freeze`.
- Phase-3 base: `4c24d65c5926907c46b7940aee030544c1d49c1e`, the freeze commit plus documentation only.
- Branch: `feat/orcengine-phase3-working-set`.
- Worktree: `F:\Ai\OrchestratorIDE-phase3-working-set`.
- The Phase-2 worktree, tag, parser, semantic mapper, and materializer source were not modified.

The Phase-1 layer loop was mechanically routed through one internal
`forward_impl`. Full execution validates the complete model before entering
that function. Streaming validates immutable bookends once and each temporary
layer before its arithmetic. Both call the same operator sequence; no equation
was copied.

## Artifacts

| Artifact | Bytes | SHA-256 | Semantics |
|---|---:|---|---|
| `smollm2-135m.gguf` | 653,091,040 | `FFFAB10C5298F8B1399088E893C1DDD64E48CD7E5020982A5B2A848E445A4AAC` | distinct output head |
| `smollm2-135m-tied.gguf` | 538,076,736 | `5CA5C86A7421E5A3105BD93806740BD9CF854B22A9AAB578910F109B7BE3681C` | absent output head; embedding reused |

The artifacts remain ignored external evidence under the Phase-2 worktree.
They were not copied or committed.

## Implementation

`StreamingModel` retains only:

- token embedding;
- final norm;
- distinct output head when present.

For each requested layer, it finds exactly nine already-mapped Phase-2
semantic tensors, materializes them with the existing
`materialize_gguf_tensor()`, validates the completed `LayerWeights`, invokes
the shared Phase-1 layer arithmetic synchronously, and releases all nine views
before the next layer. The runner owns no cache and allows no second active
layer.

The ledger records:

- current and peak resident weight bytes;
- cumulative F32 resident bytes materialized;
- materialization and release counts;
- successful backing open/seek/read count (one of each per materialization);
- encoded backing bytes and repeated backing bytes;
- current layer and peak active layers;
- sampled Windows process working set;
- per-layer resident bytes, materialization time, and execution time.

Bookends intentionally remain resident across greedy steps. Layer extents are
read again on every full-prefix step. Release counts cover temporary layer
tensors; current resident bytes at return therefore equal the retained
bookends.

## Correctness evidence

The deterministic test compares every tap map, logits vector, and selected
tokens using exact float equality for:

- full versus normal streamed execution;
- normal versus reverse in-layer materialization order;
- first versus rematerialized use of the same extents;
- explicit versus tied output semantics.

The real Release test performs four full-resident steps, four streamed steps,
and one reverse-order streamed step per artifact. It rejects any difference in
tokens, selected IDs, complete last-row logits, or captured taps. Both artifacts
passed and produced the sequence above.

Independent Hugging Face/PyTorch evidence used the original source model and
the explicit token IDs `[1, 5]`. With PyTorch `2.11.0+cu128` and Transformers
`5.11.0`, four streamed steps produced:

```text
max_abs = 0.00104618073
max_rel = 0.000288560404
```

Every element satisfied the frozen `(absolute <= 1e-3) OR (relative <= 1e-3)`
gate, and every greedy token agreed. The absolute maximum alone is slightly
above `1e-3`; the declared OR gate passes through relative error. This is not
reported as a stricter absolute-only pass.

## One-step and four-step accounting

One step reads every model weight exactly once. The output and embedding remain
resident, but temporary layer materializations make cumulative bytes equal the
full F32 model:

| Artifact | Bookends | Peak | Materializations | Releases | Backing bytes |
|---|---:|---:|---:|---:|---:|
| explicit | 226,494,720 | 240,655,104 | 273 | 270 | 651,306,240 |
| tied | 113,248,512 | 127,408,896 | 272 | 270 | 538,060,032 |

At four steps, 270 layer tensors are rematerialized per step while two or three
bookends are loaded once:

| Artifact | Materializations | Releases | Total bytes read | Repeated bytes | Reads |
|---|---:|---:|---:|---:|---:|
| explicit | 1,083 | 1,080 | 1,925,740,800 | 1,274,434,560 | 1,083 |
| tied | 1,082 | 1,080 | 1,812,494,592 | 1,274,434,560 | 1,082 |

Thus no-cache full-prefix decode rereads 424,811,520 layer bytes for every
additional generated token. Phase 3 measures that cost; it does not hide it
with a speculative cache.

## Working-set and timing campaign

Procedure:

1. one separately labeled new-process streamed observation;
2. one full and one streamed warmup;
3. three measured repetitions ordered full/streamed, streamed/full,
   full/streamed;
4. fresh process per run with 50 ms RSS polling.

The OS filesystem cache was not flushed. All runs are **warm or cache-state
unknown**, never claimed as cold-storage measurements. Raw samples are in
`evidence/PHASE3_WORKING_SET_MEASUREMENTS.csv`.

| Artifact/path | Sampled RSS repetitions | Median wall time | Wall range |
|---|---|---:|---:|
| explicit full | 668,950,528; 667,963,392; 667,959,296 | 9,752.261 ms | 9,627.113–9,774.672 ms |
| explicit streamed | 264,622,080; 345,042,944; 297,529,344 | 9,927.695 ms | 9,725.406–10,569.305 ms |
| tied full | 547,487,744; 547,516,416; 547,540,992 | 7,868.626 ms | 7,866.406–9,229.894 ms |
| tied streamed | 136,368,128; 136,368,128; 135,843,840 | 7,967.508 ms | 7,866.049–8,065.604 ms |

Every streamed RSS sample is below its corresponding full sample. Explicit
streamed RSS varied because external 50 ms process sampling captures allocator
and transient read-buffer timing; the exact engine-owned peak remained
240,655,104 in every run. Median streamed wall time was 1.80% slower for
explicit and 1.26% slower for tied. Warm cache and one machine do not support a
general storage-performance claim.

The first campaign was superseded after self-review found that full execution
validated all residents twice. That did not change outputs, but distorted full
timing. The duplicate scan was removed and the complete campaign above was
rerun. Superseded numbers are not used for the verdict. Per-layer timer fields
were added after this campaign; that telemetry-only patch was then rebuilt in
all four deterministic lanes and reran the complete real Release correctness
gates, but the alternating timing campaign was not repeated a third time. A
final one-step explicit sample emitted 30 layer records; layer 0 measured
118.093 ms materialization / 103.603 ms execution and layer 29 measured
124.849 ms / 106.587 ms. These samples are observability checks, not benchmark
claims.

## Failure and metamorphic attack results

The new regression rejects:

- a missing layer semantic tensor;
- an empty/invalid current-layer resident set before arithmetic;
- a backing file truncated after successful indexing and bookend loading;
- the resulting short read after at least one prior layer succeeds;
- a simulated failure immediately after layer release;
- accounting addition overflow;
- duplicate or oversized release;
- retention of more than one active layer.

Existing frozen suites continue to reject malformed metadata, dimensions,
non-finite weights, corrupt payloads, invalid tokens, incomplete expectations,
and zero-step decode. Wrong execution order is structurally unavailable: the
shared forward loop requests layer indices monotonically; the provider cannot
substitute another layer because semantic tensors are filtered and validated
against the requested index. Reverse *materialization* order is independently
tested and bit-identical.

## Failed approaches and corrections

1. MSVC initially failed on Windows `min`/`max` macros. `NOMINMAX` fixed the
   build; no algorithm changed.
2. Real CTest commands initially omitted Python because discovery occurred in
   a child CMake scope. Phase 3 now discovers Python at its own scope.
3. The first real comparator included `forward_milliseconds`, guaranteeing a
   false mismatch. The comparator now isolates tokens, selections, logits, and
   taps.
4. The first benchmark sampler waited for process exit before draining a large
   JSON pipe, causing pipe backpressure deadlock. Output draining and RSS
   sampling now run concurrently.
5. ASan first failed before `main` with `0xc0000135`; adding the exact MSVC ASan
   runtime directory to `PATH` produced 11/11 passes and no sanitizer finding.
6. The inherited Phase-2 Python oracle assumed ignored source artifacts existed
   in the active worktree. A temporary verified directory junction supplied
   them for the test and was removed afterward.
7. Self-review found the duplicate full-model validation scan described above.
   It was removed and all final deterministic lanes plus real Release gates
   were rerun.

No transformer-math, Phase-2 parser, mapping, or materialization defect was
found.

## Explicit validation matrix

| Evidence | Debug | Release | strict MSVC | MSVC ASan |
|---|---|---|---|---|
| Frozen Phase-1 suite | 7/7 | 7/7 | 7/7 | 7/7 |
| Phase-2 conformance/mutations/>4 GiB | 3/3 | 3/3 | 3/3 | 3/3 |
| Phase-3 lifetime/failure/metamorphic suite | 1/1 | 1/1 | 1/1 | 1/1 |
| Full vs streamed real explicit | 1 step pass | 4 steps pass | not configured | not configured |
| Full vs streamed real tied | 1 step pass | 4 steps pass | not configured | not configured |
| Independent HF/PyTorch | 1 step pass | 4 steps pass | not configured | not configured |
| Inherited Phase-2 real F32/oracle | not rerun after final telemetry-only patch | 3/3 pass | not configured | not configured |
| Residency/read telemetry invariants | pass | pass | pass | pass |
| Timing campaign | not run | 3 repetitions after warmup | not run | not run |

Final deterministic totals are Debug 11/11, Release 11/11, strict `/W4 /WX
/permissive- /sdl` 11/11, and ASan 11/11. The strict and ASan lanes intentionally
exclude large external artifacts. ASan requires
`clang_rt.asan_dynamic-x86_64.dll` on `PATH`.

The one-step Debug real checks ran before the final telemetry-only timer fields
and duplicate full-validation scan removal. The shared arithmetic was
unchanged, and the final Debug deterministic suite was rebuilt and rerun, but
the approximately 12-minute external-artifact Debug campaign was not repeated.
This is an explicit evidence boundary for the independent reviewer, not counted
as final-binary Debug timing proof.

## Narrowing decision and limitations

The mandatory whole-layer strategy already meets the below-50% engine-owned
peak gate for both artifacts. Bookends dominate the resulting peak, especially
the 113,246,208-byte embedding/output matrix, but chunking it now would mix
output projection tiling with tied-embedding lifetime and would no longer be a
pure layer-granularity proof. The existing Fringe experiment is useful prior
evidence, not production evidence. Phase 3 therefore stops at whole-layer
granularity and records output/embedding chunking as a separately reviewable
future experiment.

Still not proven:

- cold-storage or throttled-I/O performance;
- exact activation/workspace ownership accounting or isolated final-head time;
- memory mapping, cache policy, prefetch, KV cache, tokenizer, quantized compute,
  CUDA, batching, concurrency, BLAS, SIMD, threading, or product integration;
- interactive CPU usability;
- architectures other than the frozen dense-Llama profile.

## Reproduction commands

Configure and build the real lane:

```powershell
cmake -S .\Tools\OrcEnginePhase3 -B F:\Ai\_build_orcengine_p3_real `
  -G 'Visual Studio 17 2022' -A x64 `
  -DORCENGINE_REAL_F32_GGUF=$explicit `
  -DORCENGINE_REAL_TIED_F32_GGUF=$tied `
  -DORCENGINE_HF_SOURCE_DIR=$hf
cmake --build F:\Ai\_build_orcengine_p3_real --config Debug --parallel
cmake --build F:\Ai\_build_orcengine_p3_real --config Release --parallel
ctest --test-dir F:\Ai\_build_orcengine_p3_real -C Release --output-on-failure
```

Strict and ASan:

```powershell
cmake -S .\Tools\OrcEnginePhase3 -B F:\Ai\_build_orcengine_p3_strict `
  '-DCMAKE_CXX_FLAGS=/EHsc /W4 /WX /permissive- /sdl'
cmake --build F:\Ai\_build_orcengine_p3_strict --config Release --parallel
ctest --test-dir F:\Ai\_build_orcengine_p3_strict -C Release --output-on-failure

cmake -S .\Tools\OrcEnginePhase3 -B F:\Ai\_build_orcengine_p3_asan `
  '-DCMAKE_CXX_FLAGS=/EHsc /fsanitize=address /W4'
cmake --build F:\Ai\_build_orcengine_p3_asan --config RelWithDebInfo --parallel
$env:PATH = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717\bin\Hostx64\x64;' + $env:PATH
ctest --test-dir F:\Ai\_build_orcengine_p3_asan -C RelWithDebInfo --output-on-failure
```

Benchmark campaign:

```powershell
python .\Tools\OrcEnginePhase3\tests\benchmark_working_set.py `
  F:\Ai\_build_orcengine_p3_real\phase2\Release\orcengine_gguf_forward.exe `
  F:\Ai\_build_orcengine_p3_real\Release\orcengine_gguf_streaming_forward.exe `
  explicit $explicit
```

## Required 22-item stop report

1. New HEAD: the Phase-3 implementation commit containing this document.
2. Branch/worktree: `feat/orcengine-phase3-working-set` at the isolated path above.
3. Base: documentation head `4c24d65c`, descended from frozen `b8e06a00`.
4. Frozen boundaries: Phase-2 GGUF source and both freeze tags unchanged.
5. Shared math: one Phase-1 `forward_impl`; no duplicate equations.
6. Streaming policy: immutable bookends plus exactly one temporary layer, no cache.
7. Explicit artifact: real F32 distinct-output path passed.
8. Tied artifact: absent-output path passed without physical duplication.
9. Full/streamed differential: bit-identical taps, logits, and greedy IDs.
10. Four-step result: `[1,5,28,284,260,198]` for both artifacts.
11. Independent reference: PyTorch/Transformers pass under the frozen OR gate.
12. Explicit peak: predicted and measured 240,655,104 bytes, 36.95%.
13. Tied peak: predicted and measured 127,408,896 bytes, 23.68%.
14. One-layer invariant: peak active layer count 1; current layer -1 at return.
15. Four-step read cost: 1,274,434,560 repeated layer bytes for either artifact.
16. Process RSS: lower in all six paired measured repetitions.
17. Timing: median streamed wall approximately 1–2% slower; warm/unknown cache only.
18. New regressions: missing/invalid/truncated/fault/release/overflow/order/rematerialization.
19. Toolchain matrix: deterministic 11/11 in Debug, Release, strict, and ASan.
20. Assumptions disproved: timing comparator, pipe sampler, Python scope, ASan PATH, and duplicate validation were all corrected.
21. Deferred scope: no chunked head, tensor streaming, cache, Phase 4, or product integration.
22. Verdict: **READY FOR INDEPENDENT FREEZE REVIEW**.

## Verdict

**READY FOR INDEPENDENT FREEZE REVIEW**

This is not a freeze acceptance. Phase 3 stops here for an adversarial reviewer.
