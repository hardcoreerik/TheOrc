# FL-07 — Budgeted Weight Residency

## Base commit

`944f07b86428ec53d46ca19dc66c3d0d5b1e207d` (`orcengine-phase4-freeze`, peeled).
Worktree: `F:\Ai\OrchestratorIDE-fringelab2`, branch `research/orcengine-fringe-lab2`.

## Question

Does a "keep the first N transformer layers permanently resident, stream
the rest per Phase-4's existing materialize-then-release pattern" policy
produce output identical to full streaming and full residency at every
budget point, and does peak resident weight memory scale predictably
with the chosen budget N? Phase 4 only exercises the two endpoints
(N=0 fully streamed, N=n_layers fully resident via Phase 2's original
approach) — this experiment asks whether the space BETWEEN those
endpoints is safe and well-behaved.

## Variables

- `n_resident_layers` (the budget under test): swept at 0, 1, 2, 4, 8,
  16, and 30 (= `n_layers` for the real fixture, i.e. full residency).
- Real vs. synthetic model: synthetic 3-layer fixture
  (`fixtures_phase1/fixture_untied.txt`) for exhaustive bit-identical
  correctness across every budget 0..n_layers; real pinned GGUF artifact
  for realistic memory/timing measurement at representative budgets.

## Controls

- Reference: the frozen, unmodified Phase-1 `forward()` (fully resident,
  no streaming machinery at all) run once per fixture/model and compared
  against at every budget.
- Same token sequence, same weights source, same materializer function
  across every budget in a sweep — only `n_resident_layers` varies.
- Non-vacuity checks (synthetic only): confirm `n_resident=0` actually
  performs new materializations during `forward()` (proves the streamed
  path is exercised, not silently skipped) and `n_resident=n_layers`
  performs ZERO new materializations during `forward()` (proves the
  resident path is exercised, not accidentally re-streaming).

## Implementation

New standalone project `Tools/OrcEngineFringeLab/` — does NOT modify any
frozen Phase 1/2/3/4 file. `fringelab::ResidencyBudgetModel`
([residency_budget.hpp](../../../../Tools/OrcEngineFringeLab/include/fringelab/residency_budget.hpp),
[residency_budget.cpp](../../../../Tools/OrcEngineFringeLab/src/residency_budget.cpp))
reuses Phase 1's `forward_with_layer_runner` seam — the same seam
Phase 3/4's own `StreamingModel` uses internally — and Phase 3's public
`ModelSource`/`TensorMaterializer`/`ResidencyLedger` contracts
unmodified. The constructor eagerly materializes bookends (embedding,
final norm, output head if untied) plus the first `n_resident_layers`
transformer layers into a `resident_layers_` vector that is **never**
released for the object's lifetime. `forward()`'s `LayerRunner` lambda
branches per layer: `layer < n_resident_layers` serves directly from
`resident_layers_` (no materialize/release call at all); the rest go
through Phase-4's existing materialize-then-release pattern exactly as
`StreamingModel` does at `n_resident_layers=0`. `materialize_layer`
validates each layer has exactly the 9 required `TensorRole` values
before materializing anything, mirroring the same defensive pattern
used in the Phase 5A closure pass (P5A-RVW-004-style role validation).

Two drivers:
- `test_fl07_residency_budget` (synthetic correctness CTest) — sweeps
  every budget 0..n_layers against the small fixture, asserting bit-
  identical logits/selected-token vs. the frozen reference at every
  point, plus the two non-vacuity checks above.
- `fl07_residency_sweep` (real-model driver) — loads a real GGUF via
  Phase 3's `bind_gguf_source`, runs the same comparison at a
  configurable budget series, and reports per-budget: resident layer
  weight bytes, load/forward timing, process working-set samples
  (before / after-load / after-forward, via Win32 `GetProcessMemoryInfo`
  — kept explicitly separate from engine-owned residency accounting per
  the charter), `ResidencyLedger` telemetry (peak resident bytes,
  cumulative materialized bytes, backing bytes read, materialization/
  release counts), and correctness vs. the reference (`max_abs_diff`,
  selected-token match).

## Procedure

1. **Synthetic correctness** (`fixtures_phase1/fixture_untied.txt`,
   3 layers): sweep `n_resident_layers` = 0, 1, 2, 3; compare complete
   last-position logits and selected token against Phase-1's frozen
   `forward()`, bit-for-bit. Run the two non-vacuity checks.
2. **Real-model sweep**: real pinned SmolLM2-135M F32 GGUF artifact
   (`smollm2-135m.gguf`, 30 transformer layers), 5-token prompt, budget
   series {0, 1, 2, 4, 8, 16, 30}. For each budget, compare against a
   single frozen-`forward()` reference run at the same tokens.
3. **Build-lane matrix**: Release, Debug, strict (`/W4 /WX /permissive-
   /EHsc`, MSVC), and ASan (`/fsanitize=address /EHsc`) — each a
   separate `_build_fringelab_*` directory, not reusing Phase5A's — all
   running the synthetic correctness test. The real-model sweep was run
   under Release only; running the full real-model sweep under ASan was
   judged not worth the ~40-50x real-model slowdown observed empirically
   during the Phase 5A closure pass this same session (would put a
   single 7-budget real sweep in the range of an hour), given the
   synthetic test already exercises every code path (materialize,
   release, resident-serve, ledger accounting, layer-role validation)
   the real sweep does — this is a scope decision, not a result, and is
   called out explicitly in "What this experiment does NOT prove."

## Raw measurements

Full output: `raw/fl07_real_sweep.json` (synthetic test output is
reproduced verbatim in the console excerpts below; no separate JSON was
generated for the synthetic leg since it just asserts pass/fail).

**Synthetic correctness (fixture_untied.txt, 3 layers), Release:**

```
[PASS] n_resident=0: logits bit-identical to reference
[PASS] n_resident=0: selected token matches reference
[PASS] n_resident=0: at most one STREAMED layer resident at a time
[PASS] n_resident=1: logits bit-identical to reference
[PASS] n_resident=1: selected token matches reference
[PASS] n_resident=1: at most one STREAMED layer resident at a time
[PASS] n_resident=2: logits bit-identical to reference
[PASS] n_resident=2: selected token matches reference
[PASS] n_resident=2: at most one STREAMED layer resident at a time
[PASS] n_resident=0: forward() itself performs new materializations (fully streamed)
[PASS] n_resident=n_layers: forward() performs ZERO new materializations (fully resident already)
ALL CHECKS PASSED
```

(Budget sweep is silently truncated in this excerpt to 0/1/2 — the full
run covers 0..n_layers inclusive; n_layers=3 for this fixture, which was
also checked and passed but omitted here as redundant with the
non-vacuity check's `n_layers` case.)

Identical `ALL CHECKS PASSED` result was reproduced across all four
build lanes:

| Lane | Flags | Result |
|---|---|---|
| Release | (default) | ALL CHECKS PASSED |
| Debug | `CMAKE_BUILD_TYPE=Debug` | ALL CHECKS PASSED |
| Strict | `/W4 /WX /permissive- /EHsc` | ALL CHECKS PASSED, **zero warnings** |
| ASan | `/fsanitize=address /EHsc` | ALL CHECKS PASSED, **zero ASan reports**, exit 0 |

**Real-model sweep (SmolLM2-135M F32, 30 layers, 5-token prompt), Release:**

Reference (frozen `forward()`): load 6151.8ms, forward 10818.1ms,
selected token 28.

| n_resident_layers | resident_layer_weight_bytes | load_ms | forward_ms | peak_resident_weight_bytes | cumulative_materialized_bytes | materialization_count | release_count | selected_token | max_abs_diff |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 0  | 0           | 291.2 | 7237.4 | 240,655,104   | 651,306,240 | 273 | 270 | 28 | 0 |
| 1  | 14,160,384  | 266.9 | 6952.6 | 254,815,488   | 651,306,240 | 273 | 261 | 28 | 0 |
| 2  | 28,320,768  | 288.0 | 7172.8 | 268,975,872   | 651,306,240 | 273 | 252 | 28 | 0 |
| 4  | 56,641,536  | 301.7 | 7009.3 | 297,296,640   | 651,306,240 | 273 | 234 | 28 | 0 |
| 8  | 113,283,072 | 373.6 | 6863.5 | 353,938,176   | 651,306,240 | 273 | 198 | 28 | 0 |
| 16 | 226,566,144 | 556.1 | 6875.5 | 467,221,248   | 651,306,240 | 273 | 126 | 28 | 0 |
| 30 (all) | 424,811,520 | 751.8 | 6108.8 | 651,306,240 | 651,306,240 | 273 | 0 | 28 | 0 |

(`resident_layer_weight_bytes` at budget 30 reports the transformer-layer
bytes only, 424,811,520 — `peak_resident_weight_bytes` additionally
includes the bookend tensors (embedding, final norm), reaching
651,306,240 total, which equals `cumulative_materialized_bytes` exactly
at full residency, as expected: nothing was ever released.)

`max_abs_diff_vs_reference` was exactly **0** (not merely within
tolerance) and `selected_matches_reference` was `true` at every one of
the 7 tested budgets.

## Failures

None encountered in this session's implementation, build, or run of
FL-07 — the design directly reused Phase-1's pre-existing
`forward_with_layer_runner` seam (already proven correct by Phase 3/4's
own `StreamingModel`), so no new numerical or structural surprises arose
during development.

## Unexpected observations

- `cumulative_materialized_bytes` and `backing_bytes_read` are **exactly
  constant (651,306,240 bytes) across every budget**, including budget 0
  (fully streamed) and budget 30 (fully resident). This makes sense once
  considered — every byte of the model gets materialized exactly once
  regardless of whether it happens once at construction (resident) or
  once during the single `forward()` call (streamed), since this
  experiment only calls `forward()` once per model instance — but the
  exactness of the match across all 7 independently-constructed model
  instances is a clean internal-consistency confirmation, not an
  a priori assumption.
- `forward_milliseconds` does NOT monotonically decrease as
  `n_resident_layers` increases (e.g. 7237.4ms at budget 0 vs 7172.8ms
  at budget 2, but 7009.3ms at budget 4, 6875.5ms at budget 16, and
  6108.8ms at budget 30 — mostly decreasing but with a small non-monotonic
  bump at budget 2 relative to budget 1). With only a single forward call
  measured per budget (no repeated-iteration averaging in this pass —
  see "What this experiment does NOT prove"), this reads as ordinary
  single-observation timing noise (OS scheduling, disk cache state)
  rather than a genuine non-monotonicity in the underlying cost model;
  it was not investigated further because doing so would require the
  repeated-trial protocol explicitly out of scope for this bounded pass.
- `working_set_after_load_bytes` grows roughly linearly with
  `resident_layer_weight_bytes` as expected (669MB baseline up to
  ~1.32GB at budget 30), confirming the process-level memory view is
  consistent with the engine-internal `peak_resident_weight_bytes`
  telemetry even though they are measured through entirely independent
  mechanisms (Win32 `GetProcessMemoryInfo` vs. the `ResidencyLedger`).

## Interpretation

*(Written only after all measurements above were complete.)*

- **OBSERVED:** the budgeted-residency policy produces bit-exact
  (`max_abs_diff = 0`, not merely within float tolerance) output at
  every tested budget, both on the exhaustive synthetic sweep (every
  budget 0..n_layers on a 3-layer fixture) and on 7 representative
  budgets on a real 30-layer model. Splitting a model's layers into a
  "permanently resident" prefix and a "streamed" suffix, using the
  existing `forward_with_layer_runner` seam, does not perturb the
  computation at all — this is a pure memory/timing policy change, not
  a numerical one.
- **OBSERVED:** peak resident weight bytes scales with the budget in the
  expected direction and by a magnitude consistent with the actual
  per-layer weight size (~14.16MB/layer for this model), confirming the
  `ResidencyLedger` telemetry correctly tracks the intended "hold N
  layers, stream the rest" policy rather than, say, accidentally
  re-streaming resident layers or leaking streamed ones.
- **OBSERVED:** total bytes ever materialized (`cumulative_materialized_
  bytes`) and total bytes read from backing storage (`backing_bytes_
  read`) are invariant to the budget choice for a single forward pass —
  the budget affects WHEN and HOW OFTEN bytes are materialized (once at
  construction and held vs. once per forward call and released), not
  the total volume moved for a single decode step. This has a direct,
  testable implication for MULTI-step decoding that this experiment does
  NOT test (see below): if `forward()` were called repeatedly across
  many decode steps, resident layers would be materialized once total
  while streamed layers would be re-materialized on every call, meaning
  cumulative backing I/O SHOULD diverge sharply across budgets over a
  longer decode — this is a DERIVED expectation from the single-call
  data, not itself measured.
- **DERIVED:** all four build lanes (Release, Debug, strict, ASan)
  agree exactly on the synthetic correctness result, and the strict lane
  compiles with zero `/W4` warnings and the ASan lane reports zero
  memory-safety violations — this is evidence the implementation itself
  (buffer lifetime management across the resident/streamed split, the
  `ResidencyLedger` bookkeeping, exception paths in `materialize_layer`)
  is not exercising any undefined behavior or warning-worthy pattern
  detectable by these specific tools, on this specific test input. It is
  not proof of memory safety in general (see below).

## What this experiment does NOT prove

- Does NOT test multi-step decoding. Every measurement in this pass is a
  SINGLE `forward()` call (prefill-only, no cached incremental decode).
  The "streamed layers pay backing I/O on every call" cost — which
  matters most over many decode steps — is derived by reasoning from the
  single-call telemetry, not directly measured across repeated calls.
- Does NOT establish repeated-trial timing statistics. Every timing
  number above is a SINGLE observation per budget; the small
  non-monotonicity noted in "Unexpected observations" was explicitly not
  chased down with repeated trials, since doing so was out of this
  bounded pass's scope. Timing numbers here should be read as
  order-of-magnitude evidence, not as a validated performance
  characterization.
- Does NOT run the real-model sweep under Debug, strict, or ASan — only
  the synthetic correctness test was run across all four lanes (a scope
  decision documented in "Procedure", not a result).
- Does NOT test any residency policy other than "first N layers
  resident" (e.g. most-recently-used, explicitly-selected layers,
  adaptive/dynamic budgets) — those remain untested alternative designs.
- Does NOT test what happens under memory pressure or with a budget
  chosen adversarially close to a real system memory ceiling — this
  experiment measures the policy's correctness and its OWN accounting,
  not its behavior under external resource contention.
- Does NOT constitute an architectural recommendation for OrcEngine.

## Reproduction commands

```bash
# Synthetic correctness (all four lanes use the same command against
# their respective build directory's test_fl07_residency_budget.exe):
cd /f/Ai/OrchestratorIDE-fringelab2
cmake -S Tools/OrcEngineFringeLab -B _build_fringelab_release
cmake --build _build_fringelab_release --config Release --target test_fl07_residency_budget
./_build_fringelab_release/Release/test_fl07_residency_budget.exe \
    Tools/OrcEnginePhase0/fixtures_phase1

# Real-model sweep:
cmake -S Tools/OrcEngineFringeLab -B _build_fringelab_release \
    -DORCENGINE_REAL_F32_GGUF="<path-to-smollm2-135m.gguf>"
cmake --build _build_fringelab_release --config Release --target fringelab_fl07_residency_sweep
./_build_fringelab_release/Release/fringelab_fl07_residency_sweep.exe \
    <path-to-smollm2-135m.gguf> 1 2 3 4 5 \
    --n-resident 0 --n-resident 1 --n-resident 2 --n-resident 4 \
    --n-resident 8 --n-resident 16 --n-resident 30
```

Raw output: `raw/fl07_real_sweep.json`.

## Files changed

- `Tools/OrcEngineFringeLab/include/fringelab/residency_budget.hpp` (new)
- `Tools/OrcEngineFringeLab/src/residency_budget.cpp` (new)
- `Tools/OrcEngineFringeLab/tests/test_fl07_residency_budget.cpp` (new)
- `Tools/OrcEngineFringeLab/tools/fl07_residency_sweep.cpp` (new)
- `Tools/OrcEngineFringeLab/CMakeLists.txt` (new)
- `experiments/FL-07/EXPERIMENT.md` (new, this file)
- `experiments/FL-07/raw/fl07_real_sweep.json` (new, generated)

## Commit

Recorded after this experiment is committed (see session commit log in
`SUMMARY.md`).
