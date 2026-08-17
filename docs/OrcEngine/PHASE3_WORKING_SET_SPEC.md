# OrcEngine Phase 3 proposed specification: real-model streaming working set

Status: **IMPLEMENTED — ready for independent freeze review; see `PHASE3_WORKING_SET_IMPLEMENTATION.md`**

Prepared: 2026-08-16 America/Los_Angeles

Phase-2 authority:

- trusted commit: `b8e06a0058a56f2ae9fbd1f92ae0bade40b88ec7`
- immutable annotated tag: `orcengine-phase2-freeze`
- remote branch: `origin/feat/orcengine-phase2-gguf`
- review verdict: `ACCEPT FOR PHASE-2 FREEZE`

## Decision summary

The proposed next phase is:

> **Phase 3 — Real-model streaming / working-set reference**

Its question is:

> **How little real GGUF-backed weight data must be resident at once for the
> frozen engine to produce the same logits and greedy tokens?**

This is the correct next boundary. Phase 2 unexpectedly completed the old
roadmap's real F32 execution milestone early, but it still materializes every
required tensor before execution. Moving directly to tokenizers, KV caching,
BLAS, quantization, or CUDA would leave that full-residency assumption embedded
in the first practical execution path. A bounded CPU streaming proof can test
the memory model now while the implementation remains small and diagnosable.

Phase 3 does **not** seek the mathematical minimum number of bytes under every
possible schedule. It seeks the smallest useful, reproducible working-set
boundary justified by current evidence. Whole-layer residency is the mandatory
first candidate. A narrower tensor or output-head experiment is run only if the
measured layer result identifies it as the next dominant term.

## 1. Phase-2 closure confirmation

Phase 2 is **COMPLETE / FROZEN**. The branch and annotated tag were pushed on
2026-08-16. Remote verification showed:

```text
b8e06a0058a56f2ae9fbd1f92ae0bade40b88ec7 refs/heads/feat/orcengine-phase2-gguf
7a4d7fd03857084930c37c26211052c65af75ca3 refs/tags/orcengine-phase2-freeze
b8e06a0058a56f2ae9fbd1f92ae0bade40b88ec7 refs/tags/orcengine-phase2-freeze^{}
```

The tag object is annotated and peels exactly to the trusted commit. It must
never be moved. Later planning or documentation commits are not part of the
frozen implementation.

Major Phase-2 evidence:

- strict bounded GGUF v3 indexing and dense-Llama semantic mapping;
- exact independent descriptor agreement with `gguf-py` on a real Q4 artifact;
- F32/F16-to-F32 materialization into Phase-1 `ResidentView` values;
- real F32 explicit and tied-output execution through frozen Phase-1 math;
- direct Hugging Face/PyTorch logits and token agreement;
- safe 64-bit extents beyond 4 GiB;
- Debug, Release, strict MSVC, and ASan validation lanes;
- malformed, mutation, corruption, metadata-key, and non-vacuity regressions.

Intentionally unsupported at freeze:

- quantized materialization or kernels;
- CUDA or other device backends;
- paging, caching, batching, or concurrent contexts;
- tokenizer and prompt-template execution;
- KV-cached decode;
- BLAS/SIMD/threaded optimization;
- stable native API, managed wrapper, or TheOrc integration.

## 2. Current capability boundary

The frozen path is:

```text
real GGUF bytes
  -> index_gguf()                  no payload materialization
  -> map_llama_model()             LogicalTensor + BackingExtent inventory
  -> materialize_gguf_model()      every required weight becomes ResidentView
  -> Phase-1 forward()             full-prefix F32 scalar math
  -> logits + greedy token
```

What is already true:

- A model can be opened, validated, and semantically addressed without being
  resident.
- `LogicalTensor`, `BackingExtent`, and `ResidentView` are separate real types.
- Individual GGUF tensors can be materialized independently.
- Real explicit and tied F32 artifacts produce the independently verified
  answer.

What remains false:

- The executable `Model` can run with absent or temporary layer residents.
- A layer can be released before the next layer is loaded.
- Residency/read counters are measured by the engine.
- Peak RAM is bounded by an execution working set rather than model size.
- Decode avoids full-prefix recomputation or repeated weight reads.

The current `materialize_gguf_tensor()` reopens the backing file, reads one
validated extent into a byte vector, converts it into an F32 vector, and returns
a `ResidentView`. `materialize_gguf_model()` repeats this for the complete
manifest and retains all resulting views. The parser is already nonresident;
the executor is not.

## 3. Old roadmap versus current reality

| Earlier roadmap belief | Current evidence | Consequence |
|---|---|---|
| Phase 2 would inspect GGUF without executing it. | Phase 2 executed real explicit and tied F32 GGUFs and matched Hugging Face/PyTorch. | Real F32 execution is no longer Phase 3's central unknown. |
| Phase 3 would first load a real F32 model and match the oracle. | That correctness gate is frozen at `orcengine-phase2-freeze`. | Phase 3 must not rebuild a second loader or inference implementation. |
| Tokenization, cached decode, and real-model execution belonged in one phase. | Explicit-token full-prefix execution is already proven; tokenizer and KV cache remain absent. | Separate weight residency from text semantics and incremental-state semantics. |
| Explicit residency planning could wait until old Phase 6B/CUDA. | Phase 1/2 already implement the three storage identities, and full materialization is now the immediate CPU limitation. | Validate nonresident execution on CPU before designing device-tier policy. |
| Phase 4 would introduce mapped weights. | Phase 2 already has validated file-backed extents, but materialization uses bounded `ifstream` reads rather than an OS mapping. | Do not call the current implementation memory-mapped; Phase 3 can stream from existing extents without requiring `mmap`. |
| A cache should probably be introduced with streaming. | Fringe Lab showed cache behavior is trace-dependent and LRU is pathological for a cyclic layer walk. | The first C++ streaming reference uses no cache; collect the real trace before choosing policy. |
| Whole-model residency was the obvious baseline model object. | The Python streaming oracle executed an oversized real model one layer at a time, and Phase 2 decoupled model identity from residents. | `loaded model != fully resident model` is now an evidence-backed requirement. |

The old roadmap was not foolish; it was written before the Phase-2 execution
and post-Phase-0 streaming evidence existed. Its history remains visible rather
than being rewritten as if the new phase order were obvious.

## 4. Exact Phase-3 mission

Implement one correctness-first CPU execution strategy that:

1. opens and maps the existing Phase-2 `ModelArtifactManifest`;
2. retains only the immutable bookend weights required across a forward call;
3. materializes one transformer layer from its real GGUF `BackingExtent` values;
4. executes that layer using the same operation order and F32 accumulation as
   the frozen Phase-1 path;
5. releases the layer before materializing the next;
6. produces the same taps, logits, and greedy sequence as full materialization;
7. reports an engine-owned residency/read ledger and process-level peak RAM;
8. quantifies the byte-read and latency cost of the reduced working set.

The phase answers whether whole-layer streaming is a practical reference floor
for the current model. It must also report what still dominates the measured
peak and whether a narrower experiment is justified.

## 5. Explicit non-goals

- No CUDA, cuBLAS, GPU placement, PCIe transfer, or VRAM cache.
- No quantized materialization, dequantization, or quantized kernels.
- No tokenizer, prompt template, text input, or detokenizer.
- No KV cache or incremental decode implementation.
- No BLAS, SIMD, threading, fusion, generalized graph, or performance rewrite.
- No generic `ExecutionPlanner`, memory-tier framework, page table, or cache
  hierarchy.
- No LRU, NextUse, prefetcher, speculative decoding, batching, or multi-context
  scheduler in the engine.
- No tensor tiling unless the mandatory layer result shows that one tensor is
  the remaining working-set blocker and a separate design review approves it.
- No stable ABI, managed wrapper, TheOrc integration, default-runtime change,
  model download, or new architecture profile.
- No claim of interactive CPU usability. This phase measures the correctness
  and cost of a memory strategy; Phase 4 addresses practical decode semantics
  and CPU usability.

## 6. Proposed execution strategy

### 6.1 Frozen baseline

Keep `materialize_gguf_model()` plus `forward()` as the full-resident reference.
Do not weaken or replace it. Its results and telemetry are the A side of every
comparison.

### 6.2 Mandatory strategy: layer-at-a-time, no cache

The smallest useful initial structure is lexical ownership, not a memory
framework:

```text
validated ModelArtifactManifest
  |
  +-- materialize token embedding
  +-- materialize final norm
  +-- materialize distinct output head only when present
  |
  +-- for layer 0..N-1:
        materialize that layer's nine mapped tensors
        execute the frozen layer block
        destroy the layer ResidentViews
  |
  +-- final norm and output projection
```

The Phase-1 tag remains immutable. On the future Phase-3 branch, the existing
layer-loop body may be extracted into one shared internal layer-block function
so full-resident and streamed execution call the exact same arithmetic. The
refactor must be mechanically checked: the frozen Phase-1 suite, full-resident
Phase-2 suite, and bit-identical pre/post outputs must pass before streaming is
considered. Do not copy the transformer equations into a second implementation.

No cache is required for the first strategy. A local `LayerWeights` object owns
the current residents and RAII destruction establishes the release boundary.
If telemetry needs state, use a small execution-scoped ledger with counters;
do not add a general residency manager.

### 6.3 Bookend policy

For the mandatory first run:

- retain `token_embd.weight`, because full-prefix input lookup needs it;
- retain `output_norm.weight`;
- retain a distinct `output.weight` when present;
- preserve tied output as semantic aliasing, never physical duplication.

This is deliberately conservative. It isolates layer streaming first and
leaves output-head/embedding streaming as a measured follow-up.

### 6.4 Conditional narrowing experiment

After the layer-at-a-time gate passes, inspect the measured engine-owned peak:

- If bookend weights are the majority, run one real GGUF-backed chunked
  `lm_head` experiment. Compare complete logits and greedy output, not only
  argmax. Include explicit and tied semantics. This promotes Fringe C/C2 into
  a bounded C++ measurement, not architecture.
- If the current layer or one tensor is the majority, run one tensor-at-a-time
  liveness experiment for that layer. Do not generalize it into tile paging.
- If neither dominates enough to change the practical conclusion, stop at
  whole-layer streaming and record why narrower granularity was rejected.

Only one refinement path is selected from measured evidence. Tile streaming is
not part of Phase 3.

## 7. Test models and artifacts

No new model is required.

### Primary explicit-output artifact

- path: `Tools/OrcEnginePhase0/artifacts/smollm2-135m.gguf`
- size: 653,091,040 bytes
- SHA-256: `FFFAB10C5298F8B1399088E893C1DDD64E48CD7E5020982A5B2A848E445A4AAC`
- tensors: 273 F32
- mapped F32 tensor bytes: 651,306,240
- persistent bookends under the first strategy: 226,494,720 bytes
- largest layer: 14,160,384 bytes

### Tied-output artifact

- path: `Tools/OrcEnginePhase0/artifacts/smollm2-135m-tied.gguf`
- size: 538,076,736 bytes
- SHA-256: `5CA5C86A7421E5A3105BD93806740BD9CF854B22A9AAB578910F109B7BE3681C`
- tensors: 272 F32
- mapped F32 tensor bytes: 538,060,032
- persistent bookends under the first strategy: 113,248,512 bytes
- largest layer: 14,160,384 bytes

### Independent source

- Hugging Face model: `HuggingFaceTB/SmolLM2-135M`
- revision: `93efa2f097d58c2a74874c7e644dbc9b0cee75a2`
- source `model.safetensors` SHA-256:
  `80521B40281D6CE74E35C9282C22539E75AA0AC8578892B2A59955EF78D55DA1`

The existing Q4 artifact remains index-only and is not a Phase-3 execution
target because quantized compute is an explicit non-goal.

## 8. Differential oracle strategy

Three comparisons remain separate:

1. **Frozen full-resident OrcEngine** — exact implementation reference from
   `orcengine-phase2-freeze`.
2. **Streamed OrcEngine** — candidate path using the same operators and order.
3. **Original Hugging Face/PyTorch** — independent source-model reference.

Required checks:

- full-resident versus streamed first-step named taps: bit-identical;
- full-resident versus streamed full last-token logits: bit-identical;
- exact selected token and token history at each step;
- explicit and tied artifacts produce their previously proven equivalent
  answer;
- streamed versus Hugging Face/PyTorch logits satisfy the frozen `(absolute <=
  1e-3) OR (relative <= 1e-3)` rule;
- at least four Release greedy steps from `[1, 5]`, plus one bounded Debug step;
- deliberate layer-order, early-release, short-read, and missing-resident faults
  are detected.

If extraction of the shared layer block changes a frozen output by one bit, the
refactor is rejected until explained. A looser tolerance is not an acceptable
substitute for preserving arithmetic order.

## 9. Required memory and performance measurements

Record for full-resident and streamed runs:

- model file bytes and SHA-256;
- mapped/indexed tensor count and bytes;
- bytes materialized by semantic role and layer;
- current and peak engine-owned resident weight bytes;
- current and peak activation/workspace bytes where measurable;
- sampled process peak working set;
- materialization count;
- backing-file open/seek/read count;
- total backing bytes read;
- repeated bytes read after the first forward/token;
- per-layer materialization and execution time;
- final-head time;
- total materialization, forward, and process wall time;
- token count, logits difference, and greedy agreement;
- cold/warm procedure and whether OS cache state was controlled.

Required scenarios:

- explicit artifact, one forward from `[1, 5]`;
- tied artifact, one forward from `[1, 5]`;
- explicit artifact, four greedy steps;
- tied artifact, four greedy steps;
- at least three measured repetitions after one declared warmup, alternating
  full and streamed order for timing comparisons;
- a separately labeled cold-process run. If filesystem cache cannot be
  controlled, say so and do not call it cold storage.

Correctness, engine-owned residency, process peak, and timing are separate
claims. OS working-set samples are corroborating measurements, not substitutes
for the exact engine-owned ledger.

## 10. Frozen full-materialization planning baseline

These measurements were gathered before Phase-3 implementation from the
rebuilt frozen Release executable. They are planning observations, not a stable
benchmark campaign: one process each, 50 ms working-set polling, warm/unknown OS
file-cache state, input `[1, 5]`, one generated token.

| Artifact | Materialized bytes | Materialize | Forward | Sampled peak working set | Process wall | Selected |
|---|---:|---:|---:|---:|---:|---:|
| explicit | 651,306,240 | 5,339.167 ms | 4,671.577 ms | 668,950,528 | 10,188.547 ms | 28 |
| tied | 538,060,032 | 4,150.711 ms | 4,015.021 ms | 548,528,128 | 8,297.089 ms | 28 |

An earlier explicit run in the same session reported 5,010.200 ms
materialization, 4,862.887 ms forward, and 10,085.777 ms process wall. The
variation is retained rather than selecting the faster run. Peak collection in
that first command failed because Windows returned zero after process exit; the
measurement was rerun with live 50 ms polling. This is a measurement-tooling
failure, not an engine failure.

Static manifest accounting predicts the mandatory layer-at-a-time resident
weight floor before activation/transient overhead:

- explicit: 226,494,720-byte bookends + 14,160,384-byte largest layer =
  240,655,104 bytes, 36.95% of full resident weights;
- tied: 113,248,512-byte bookends + 14,160,384-byte largest layer =
  127,408,896 bytes, 23.68% of full resident weights.

These are hypotheses for the Phase-3 implementation, not measured streamed
results. The difference between them and process peak will expose conversion,
activation, allocator, runtime, and file-I/O overhead.

## 11. Fringe Lab evidence disposition

| Fringe evidence | Phase-3 disposition | Reason |
|---|---|---|
| Python real-model layer streaming | Promote to mandatory C++ real-GGUF correctness test | Directly matches the phase question, but C++ CPU behavior is still unproven. |
| Chunked `lm_head` C/C2 | Conditional bounded experiment | Exact argmax survived synthetic and real reads, but C2 used a derived F32 scratch file and warm OS cache. |
| NextUse versus LRU A/A2 | Do not promote policy; export/replay actual trace only | Results are regime-dependent, and the first single-context strategy needs no cache. |
| Byte amortization | Promote to required telemetry | Repeated backing reads per generated token are the central cost of no-cache streaming. |
| Tile streaming | Defer | Neither real C++ layer nor tensor streaming exists yet. |
| 256 MiB challenge | Defer | An arbitrary cap should not drive the reference before real working-set accounting. |
| Fake-slow/cold storage | Optional measurement follow-up | Useful for read-cost interpretation, not required to prove residency correctness. |
| Multi-context amortization | Defer to later context/batching work | Phase 3 remains one sequence and no KV cache. |

The useful cache result is not “use NextUse.” It is “execution order is known,
so expose and measure the trace before choosing a replacement policy.”

## 12. Risks and controls

| Risk | Control |
|---|---|
| Refactoring the layer loop changes frozen math. | Shared function, bit-identical full-path gate before streaming, retain freeze tag as executable reference. |
| A streamed path accidentally retains old layers. | Exact current/peak resident ledger plus destruction/rematerialization tests. |
| OS page cache is mistaken for engine residency. | Report engine-owned bytes and process working set separately; label cache state. |
| Repeated reads make the result unusably slow. | Measure bytes and time honestly; no throughput pass gate in Phase 3. |
| Tied output is physically duplicated. | Run both artifacts and assert alias semantics/resident accounting. |
| Output-head weights dominate after layer streaming. | Conditional real-GGUF chunk experiment, not speculative framework code. |
| Tensor-at-a-time duplicates layer math or creates lifetime bugs. | Run only if layer measurements justify it; keep it experimental until separately reviewed. |
| General cache abstractions appear before a workload needs them. | Mandatory no-cache strategy and one active layer. |
| Full-prefix recompute obscures read cost. | Report per-forward and per-generated-token totals; defer KV cache explicitly. |
| Timing claims overfit warm cache or one sample. | Predeclare repetitions/order and preserve raw samples. |

## 13. Completion gates

Phase 3 is complete only when all are true:

1. Phase-1 and Phase-2 frozen suites remain green without changed tolerances.
2. Full-resident outputs remain bit-identical before and after the shared
   layer-block extraction.
3. Real explicit and tied F32 GGUFs execute layer-at-a-time from real
   `BackingExtent` reads.
4. Streamed taps, full logits, greedy selections, and four-step sequences are
   bit-identical to full materialization.
5. Independent Hugging Face/PyTorch comparison remains within the frozen gate.
6. At most one transformer layer's `ResidentView` set exists at a time; a
   regression test detects retention of two completed layers.
7. Engine-owned peak resident weight bytes are below 50% of full materialized
   weight bytes for both existing artifacts.
8. Sampled process peak working set is lower than the matching frozen baseline
   in three measured repetitions; exact reduction and variance are reported,
   not selected from the best run.
9. Materialization counts, bytes read, repeated reads, and timing are recorded
   for one and four steps.
10. Short reads, wrong layer order, premature release, missing residents,
    non-finite payloads, and cleanup after failure fail safely.
11. Debug, Release, strict MSVC, and supported ASan lanes have an explicit
    inclusion matrix; no “applicable tests” wording.
12. The conditional narrowing decision is recorded: experiment run and result,
    or evidence-based reason to stop at layer granularity.
13. Documentation retains original beliefs, failed attempts, exact commands,
    raw measurement artifacts/hashes, limitations, and unsupported scope.
14. An independent freeze review accepts the evidence.

No throughput threshold is a Phase-3 completion gate. A slow but correct and
measurably smaller working set is a valid reference result. Its measured cost
becomes Phase-4 input.

## 14. Validation matrix required for the future implementation

| Evidence | Debug | Release | strict MSVC | ASan |
|---|---:|---:|---:|---:|
| Frozen Phase-1 suite | Required | Required | Required | Required |
| Frozen Phase-2 parser/conformance suite | Required | Required | Required | Required |
| Full-resident real F32 reference | 1 step | 4 steps | Explicit status | Explicit status |
| Layer-streamed real F32 explicit | 1 step | 4 steps | Explicit status | Explicit status |
| Layer-streamed real F32 tied | 1 step | 4 steps | Explicit status | Explicit status |
| Full versus streamed bit identity | Required | Required | Explicit status | Explicit status |
| Hugging Face/PyTorch comparison | 1 step | 4 steps | Explicit status | Explicit status |
| Residency/read telemetry invariants | Required | Required | Required | Required |
| Failure/lifetime regressions | Required | Required | Required | Required |
| Working-set/timing campaign | No | Required | No | No |

“Explicit status” means configured and run, or specifically marked not
configured with rationale. It must never be silently omitted and counted as a
pass.

## 15. What Phase 4 becomes

If Phase 3 succeeds, Phase 4 should become:

> **Practical CPU inference semantics and usability baseline**

Candidate Phase-4 order:

1. exact tokenizer and text/token boundary;
2. incremental KV-cached decode proven equivalent to full-prefix recompute;
3. reusable bounded activation workspace and lifecycle/cancellation checks;
4. prompt versus decode benchmark separation;
5. established BLAS for dense operations only after scalar correctness;
6. bounded threading and profiling-led optimization.

This absorbs the still-unfinished parts of the old Phase 3 and the useful parts
of the old Phase 4. Quantization remains later. CUDA remains later. Device-tier
planning remains a distinct future problem, informed by the CPU streaming
contract rather than invented before it.

Old Phase 6B should therefore narrow to multi-tier/device execution planning:
CPU layer streaming will already have validated temporary residency and the
three storage identities. Phase 6B need not repeat that proof; it must decide
placement, transfer, fallback, and cost across CPU/RAM/CUDA tiers before paged
CUDA.

## 16. Exact planning-baseline commands

Build the frozen Release executable:

```powershell
$build = Join-Path $env:TEMP 'orcengine-phase2-hardening-build'
cmake --build $build --config Release --parallel
```

Run the full-resident executable directly:

```powershell
$exe = Join-Path $build 'Release\orcengine_gguf_forward.exe'
$explicit = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m.gguf'
$tied = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m-tied.gguf'
& $exe $explicit 1 5 --steps 1
& $exe $tied 1 5 --steps 1
```

The process peak above was sampled while the child was alive:

```powershell
$stdout = Join-Path $env:TEMP 'orcengine-phase3-baseline.stdout.json'
$stderr = Join-Path $env:TEMP 'orcengine-phase3-baseline.stderr.txt'
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $exe `
  -ArgumentList @($explicit, '1', '5', '--steps', '1') `
  -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
  -PassThru -WindowStyle Hidden
[uint64]$peakWorking = 0
while (-not $process.HasExited) {
  $process.Refresh()
  if ([uint64]$process.WorkingSet64 -gt $peakWorking) {
    $peakWorking = [uint64]$process.WorkingSet64
  }
  Start-Sleep -Milliseconds 50
}
$process.WaitForExit()
$timer.Stop()
$result = Get-Content -Raw -LiteralPath $stdout | ConvertFrom-Json
[pscustomobject]@{
  materialized_bytes = [uint64]$result.materialized_bytes
  materialize_ms = [double]$result.materialize_milliseconds
  forward_ms = [double]$result.steps[0].forward_milliseconds
  sampled_peak_working_set_bytes = $peakWorking
  process_wall_ms = [math]::Round($timer.Elapsed.TotalMilliseconds, 3)
  selected = [int64]$result.steps[0].selected
}
```

The tied measurement used the same command with `$explicit` replaced by
`$tied`. The first failed peak attempt queried `PeakWorkingSet64` only after
process exit and returned zero; live polling above is the corrected procedure.

Manifest-derived bookend/layer totals were computed from:

```powershell
$inspector = Join-Path $build 'Release\orcengine_gguf_inspect.exe'
& $inspector --json $explicit
& $inspector --json $tied
```

The temporary captured stdout/stderr files were removed after each measurement.
No tracked file or frozen artifact was changed.

## Stop gate

Stop here for design review. Do not create a Phase-3 branch, refactor the layer
loop, add residency code, or run speculative optimization experiments until the
maintainer accepts or revises this specification.
