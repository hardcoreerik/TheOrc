# OrcEngine Phase 3 freeze hardening

Status: **HARDENING COMPLETE — ACCEPT FOR PHASE-3 FREEZE**

Date: 2026-08-16 America/Los_Angeles

This pass started from clean commit `11939eef619661018331f0a67c423fec4608a158`.
It did not start Phase 4 and did not change transformer equations, the GGUF
parser, model mapping, quantized compute, CUDA, tokenizer, KV cache, paging,
prefetch, product integration, or visualization UI.

## Outcome

The stronger Phase-3 statement now has reproducible evidence:

```text
full resident cannot be admitted under the selected engine budget
streamed execution is admitted under that same budget
streamed taps, logits, and greedy token remain exact
```

The streaming core is now source-format neutral. GGUF is a thin adapter into a
`ModelSource` plus `TensorMaterializer` callback. A non-GGUF in-memory source
executes the same lifecycle exactly. Optional structured observations expose
measured execution truth without changing output. A separately built
executable from the frozen Phase-1 checkout and an executable built from the
current refactored full-resident source emit identical float bit patterns.

The earlier one-step total-wall result understated the steady-state cost.
Persistent four-token inference measured streamed/full ratios of **1.49x for
the explicit artifact** and **1.85x for the tied artifact**. Phase 3 is a memory
correctness success, not a speed success.

## Discovery history

### Working-set hypothesis

Hypothesis: whole-layer streaming could execute a real model while retaining
only bookends and one transformer layer.

Experiment: Phase 3 routed the layer loop through the shared Phase-1 arithmetic
and materialized nine weights for one layer at a time from Phase-2 extents.

Observation: both real F32 artifacts produced exact full-versus-streamed taps,
logits, tokens, and `[1,5,28,284,260,198]`. Predicted and measured peaks were
240,655,104 bytes explicit and 127,408,896 bytes tied.

Consequence: whole-layer streaming was retained. Tensor-at-a-time streaming was
deferred because the simpler strategy already proved the phase hypothesis.

### Why the budget test was added

Original belief: a measured lower resident peak adequately demonstrated the
working-set claim.

Review challenge: lower usage does not prove streaming is necessary under a
constraint.

Experiment: add a deterministic engine-owned residency admission ceiling. The
ledger checks declared bytes before materialization, checks actual returned
resident bytes after materialization, and never relies on exhausting system
RAM.

Observation: budgets chosen from the already measured exact peaks reject full
residency, admit streaming exactly at the peak, and reject streaming one byte
below it.

Consequence: Phase 3 now proves capability under a controlled constraint, not
only reduced usage.

### Performance assumption corrected

Original belief: streamed execution was approximately 1–2% slower.

Review challenge: the one-step comparison included full-model startup, hiding
the ongoing cost after full weights were already resident.

Experiment: each path now starts one process, materializes once, and generates
four greedy tokens. After one warmup each, three measured repetitions alternate
full/streamed ordering. OS filesystem caches were not flushed, so cache state
is warm or unknown.

Observation: ongoing streamed inference is materially slower. The explicit
full path also had one visible timing outlier; it remains in the raw evidence.

Consequence: future cache, prefetch, amortization, or bookend work has an honest
baseline. No such optimization was added here.

### Format dependency found during review

Original implementation: `StreamingModel` included GGUF declarations, consumed
`ModelArtifactManifest`, knew `SemanticTensorRole`, and directly called
`materialize_gguf_tensor()`.

Review challenge: that made GGUF part of the engine architecture.

Experiment: introduce only a neutral semantic tensor inventory and one
materializer callback:

```text
ModelSource
  SourceTensor(TensorIdentity, LogicalTensor, BackingExtent, declared bytes)
  TensorMaterializer(LogicalTensor, BackingExtent) -> ResidentView
```

Observation: the core builds while linking only Phase 1. `gguf_source.cpp` is a
separate adapter library linking Phase 2. An in-memory `BackingExtent::FromF32`
source runs exact full-versus-streamed output without using the GGUF adapter.

Consequence: GGUF remains an input format. SafeTensors and Orc-native formats
were not implemented or scaffolded.

### Observable inference

Hypothesis: the existing lifecycle telemetry could become a useful observation
seam without a UI or a large tracing framework.

Experiment: an optional callback receives semantic `ExecutionEvent` records for
model, layer, materialization, residency, execution, release, and token
boundaries. Tensor identity is role plus layer; backing identity is opaque. All
current events are explicitly `Measured`. `Derived` and `Interpreted` labels
exist to prevent future visualizations from mislabeling interpretation as
engine truth.

Observation: observer off and on are exactly equal at taps, logits, and token
IDs. If an observer throws, it is counted and disabled after the first failure;
inference continues with exact output.

Consequence: visualization can later consume real structured truth, but no UI,
activation interpretation, or claim about human-like thought was added.

## Budget evidence

| Artifact | Full required | Budget | Stream peak | Full admission | Stream at budget | Stream at budget - 1 |
|---|---:|---:|---:|---|---|---|
| explicit output | 651,306,240 | 240,655,104 | 240,655,104 | rejected cleanly | exact pass | rejected cleanly |
| tied output | 538,060,032 | 127,408,896 | 127,408,896 | rejected cleanly | exact pass | rejected cleanly |

The constrained pass compares selected token, the complete last-row logits,
and captured intermediate taps against the trusted full-resident executable.
Both selected token 28 for input IDs `[1,5]`. The exact one-byte failures were:

```text
explicit: would require 240655104 bytes with limit 240655103
tied:     would require 127408896 bytes with limit 127408895
```

Unit fixtures additionally prove exact peak, one byte above, below permanent
bookends, accounting overflow, declared/actual resident mismatch, duplicate
release, partial-layer cleanup, post-layer failure cleanup, and observer
failure. Admission is an engine-owned weight-residency proof, not an OS process
limit and not a generic memory manager.

## Persistent four-token performance

Protocol: Release build; one warmup per path; three alternating fresh-process
repetitions; one model materialization per process; four full-prefix greedy
steps; warm/unknown filesystem cache. Full raw samples are preserved in
`evidence/PHASE3_PERSISTENT_DECODE_MEASUREMENTS.csv`.

| Artifact/path | Median startup | Median inference after resident | Median total wall | Median/token |
|---|---:|---:|---:|---:|
| explicit full | 5,643.222 ms | 23,306.629 ms | 29,393.386 ms | 5,826.657 ms |
| explicit streamed | 1,850.223 ms | 34,761.889 ms | 36,717.279 ms | 8,690.472 ms |
| tied full | 4,489.629 ms | 17,449.965 ms | 22,035.521 ms | 4,362.491 ms |
| tied streamed | 952.453 ms | 32,258.170 ms | 33,315.516 ms | 8,064.543 ms |

Steady-state slowdown is computed from median post-residency inference:

- explicit: `34,761.8885 / 23,306.6290 = 1.4915x`;
- tied: `32,258.1700 / 17,449.9654 = 1.8486x`.

The explicit full samples were 23.307 s, 32.008 s, and 20.808 s. The middle
run is noisy; it is not discarded. These numbers describe one Windows machine
and warm/unknown cache state, not cold storage or general throughput.

### Backing I/O and materialization

| Artifact/path | Startup bytes | Ongoing bytes/token | Startup reads | Ongoing reads/token | Four-step repeated bytes |
|---|---:|---:|---:|---:|---:|
| explicit full | 651,306,240 | 0 | 273 | 0 | 0 |
| explicit streamed | 226,494,720 | 424,811,520 | 3 | 270 | 1,274,434,560 |
| tied full | 538,060,032 | 0 | 272 | 0 | 0 |
| tied streamed | 113,248,512 | 424,811,520 | 2 | 270 | 1,274,434,560 |

For these F32 artifacts one successful materialization performs one backing
read. Therefore ongoing reads/token and materializations/token are both zero
for persistent full residency and 270 for streamed execution.

## Frozen Phase-1 cross-differential

The frozen probe was configured against the separate checkout
`F:\Ai\OrchestratorIDE-phase1`, whose tag `orcengine-phase1-freeze` peels to
`b27bc9323b89b9151c811c30d41145bb672a2943`. The current probe links the
refactored full-resident Phase-1 library in this Phase-3 tree. This is not the
old suite recompiled twice.

Both executables emit sorted intermediate tap data, complete logits, selected
IDs for tied and untied fixtures, and eight greedy decode steps as exact F32
bit patterns. Their 328,928 output bytes were identical with SHA-256:

```text
EFFCA261F3F661C27683C614757FC6B81DBCE63C5E546A54FFA19E3D996B5614
```

This confirms the shared-forward refactor did not change the frozen ruler for
the exercised deterministic fixtures.

## Format-independence attack

The following scan returned no matches:

```powershell
rg -n -i 'gguf|materialize_gguf|general\.architecture|llama\.' `
  Tools/OrcEnginePhase3/include/orcengine/model_source.hpp `
  Tools/OrcEnginePhase3/include/orcengine/streaming.hpp `
  Tools/OrcEnginePhase3/src/streaming.cpp
```

`orcengine_phase3` links only `orcengine_phase1`. GGUF references remain in the
separate `orcengine_phase3_gguf` adapter, CLI, and adapter tests. The inherited
`BackingExtent` encoding enum includes names for GGUF quantized encodings, but
the Phase-3 core does not inspect or branch on encoding; it passes the extent to
the supplied materializer. That vocabulary is not a required GGUF execution
dependency and was not broadened in this freeze pass.

## Adversarial results and regressions

The Phase-3 lifecycle executable reports 22 explicit passes:

- exact full/streamed, reverse-order, rematerialization, and tied-output checks;
- non-GGUF in-memory materialization;
- full admission rejection and stream admission at exact/above/below bounds;
- rejection below permanent bookends;
- structured observer output equivalence and measured semantics;
- thrown-observer isolation;
- materializer output larger than its declaration;
- missing layer tensor and invalid resident set;
- failure after release and truncation after prior layers succeed;
- accounting overflow and duplicate release.

One new test-quality problem was found during final re-attack: the first real
budget script checked admission and selected token but did not independently
compare all constrained outputs. It was corrected before the final run to
compare taps and full last-row logits against the trusted full executable.

One Release matrix attempt failed because an inherited Phase-2 Python oracle
hardcodes ignored source artifacts under the active worktree. A temporary,
verified directory junction to the Phase-2 source artifact was created, the
single affected test passed, and the junction was removed and verified absent.
This environmental failure is retained in the history.

No transformer-math, parser, GGUF semantic mapping, or materialization defect
was found.

## Explicit validation matrix

`D` means Debug, `R` Release, `S` strict MSVC Release with `/W4 /WX
/permissive- /sdl`, and `A` MSVC ASan RelWithDebInfo.

| Evidence | D | R | S | A |
|---|---|---|---|---|
| Frozen Phase-1 seven-test suite | 7/7 | 7/7 | 7/7 | 7/7 |
| Phase-2 conformance, mutations, >4 GiB sparse | 3/3 | 3/3 | 3/3 | 3/3 |
| Phase-3 lifecycle/budget/observer/non-GGUF/failure suite | 1/1 | 1/1 | 1/1 | 1/1 |
| Frozen-binary cross-differential | 1/1 | 1/1 | 1/1 | 1/1 |
| Real explicit full vs streamed, four steps | not run | pass | not run | not run |
| Real tied full vs streamed, four steps | not run | pass | not run | not run |
| Real explicit controlled budget | unit boundaries only | pass | unit boundaries only | unit boundaries only |
| Real tied controlled budget | unit boundaries only | pass | unit boundaries only | unit boundaries only |
| Independent HF/PyTorch comparison | not run | pass | not run | not run |
| Inherited Phase-2 real F32/Python oracle | not run | pass after scoped artifact junction | not run | not run |
| Inherited Phase-2 real HF and tied execution | not run | 2/2 | not run | not run |
| Persistent full and streamed four-token timing | not run | 3 repetitions/path/artifact | not run | not run |

Totals were 12/12 deterministic tests in Debug, strict, and ASan. Release was
20/20 after the one environmental rerun described above. ASan required
`clang_rt.asan_dynamic-x86_64.dll` from MSVC 14.44 on `PATH`; no sanitizer
finding occurred. Real artifacts were intentionally not run under strict or
ASan because those lanes test source warnings and memory safety with bounded
deterministic fixtures, while the real Release lane tests external artifacts.

## Reproduction

```powershell
$explicit = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m.gguf'
$tied = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m-tied.gguf'
$hf = 'F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m'
$frozen = 'F:\Ai\_build_orcengine_p1_frozen_probe\Release\orcengine_phase1_snapshot_frozen.exe'

cmake -S .\Tools\OrcEnginePhase3 -B F:\Ai\_build_orcengine_p3_hardening `
  -G 'Visual Studio 17 2022' -A x64 `
  -DORCENGINE_REAL_F32_GGUF=$explicit `
  -DORCENGINE_REAL_TIED_F32_GGUF=$tied `
  -DORCENGINE_HF_SOURCE_DIR=$hf `
  -DORCENGINE_FROZEN_PHASE1_SNAPSHOT=$frozen
cmake --build F:\Ai\_build_orcengine_p3_hardening --config Debug --parallel
cmake --build F:\Ai\_build_orcengine_p3_hardening --config Release --parallel
ctest --test-dir F:\Ai\_build_orcengine_p3_hardening -C Debug `
  -E 'streaming_real|gguf_real' --output-on-failure
ctest --test-dir F:\Ai\_build_orcengine_p3_hardening -C Release --output-on-failure
```

Frozen executable:

```powershell
cmake -S .\Tools\OrcEnginePhase3\tests\frozen_phase1_probe `
  -B F:\Ai\_build_orcengine_p1_frozen_probe -G 'Visual Studio 17 2022' -A x64 `
  -DORCENGINE_PHASE1_SOURCE_DIR=F:\Ai\OrchestratorIDE-phase1\Tools\OrcEnginePhase1
cmake --build F:\Ai\_build_orcengine_p1_frozen_probe --config Release `
  --target orcengine_phase1_snapshot_frozen --parallel
```

Strict and ASan use the commands above with, respectively,
`-DCMAKE_CXX_FLAGS='/EHsc /W4 /WX /permissive- /sdl'` and
`-DCMAKE_CXX_FLAGS='/EHsc /fsanitize=address /W4'`. ASan CTest must prepend
the matching MSVC 14.44 `Hostx64\x64` runtime directory to `PATH`.

Persistent benchmark:

```powershell
python .\Tools\OrcEnginePhase3\tests\benchmark_persistent_decode.py `
  F:\Ai\_build_orcengine_p3_hardening\phase2\Release\orcengine_gguf_forward.exe `
  F:\Ai\_build_orcengine_p3_hardening\Release\orcengine_gguf_streaming_forward.exe `
  explicit $explicit
```

## Evidence boundary and next bottleneck

The dominant retained working set is now the embedding/output bookend:

- explicit: 226,494,720 permanent bytes of a 240,655,104-byte peak;
- tied: 113,248,512 permanent bytes of a 127,408,896-byte peak.

Potential later experiments are embedding-row fetch, output-head chunking,
running argmax/top-k, and output projection virtualization. They were not
implemented. The roadmap must compare that direction against tokenizer, KV
cache, and CPU usability work after the freeze; this pass does not choose by
intuition.

Still not proven: cold or throttled storage, activation/workspace budget,
multiple architectures, concurrent contexts, tokenizer, KV cache, quantized
compute, CUDA, memory mapping, prefetch, generic caching, BLAS/SIMD/threading,
product integration, or useful interactive throughput.

## Verdict

**ACCEPT FOR PHASE-3 FREEZE**

The result is correct, reproducible, memory-constrained, format-independent,
observable, and grounded in the separately built frozen Phase-1 ruler. Stop
here for independent freeze review; do not tag or begin the next phase yet.
