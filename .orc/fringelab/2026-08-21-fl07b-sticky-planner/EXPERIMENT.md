# FL-07B — Multi-Step Sticky-Layer Planning

Status: **IN PROGRESS — Commit 2A landed (Commit 2 corrected/hardened per Codex review); Commit 3 (real-GGUF evidence) not yet started; stopped here for Codex review per the authorizing instruction**

Worktree: `F:\Ai\OrchestratorIDE-fringelab-fl07b`
Branch: `research/orcengine-fl07b-sticky-planner`
Base: annotated tag `orcengine-phase5a-freeze`, peeled commit `db3e5f38b37e6b342e28e6d208737ab8288b0c05`
FL-07 provenance: cherry-picked from `a6eb521b` (`research(fringelab): add FL-07 budgeted weight residency experiment`), applied clean, no conflicts.

This is **experimental research only**. It does not modify, promote, or
replace any frozen OrcEngine production path, and this commit does not
constitute an architectural decision.

## Purpose

FL-07 proved that keeping the first N transformer layers resident while
streaming the remainder is bit-exact across tested budgets, for a single
full-prefix forward call. FL-07B asks:

1. Does that correctness hold across a real multi-step cached decode?
2. Does cumulative backing I/O actually diverge across residency budgets
   over repeated decode steps?
3. Can an explicit planner-selected resident-layer set replace "first N"
   without changing results?
4. Can a simple materialization-cost-per-resident-byte policy outperform
   first-N under the same byte budget?
5. If the current homogeneous SmolLM2 model produces a tie, can that be
   reported honestly rather than inventing a benefit?

Questions 1-2 (multi-step cached decode, cumulative I/O) and the real-model
leg of questions 3-4 are **not yet answered by this commit** -- see "Scope
of this commit" below.

## Explicitly out of scope (for the whole experiment)

- MRU/LRU residency policies (every dense layer is used once per token in
  fixed order; recency is not a meaningful discriminator).
- Ablation sensitivity as I/O evidence (residency changes WHERE weights are
  held, not whether/how they execute).
- UI, Avalonia panel, stable telemetry ABI, production ExecutionPlanner,
  generic policy plugin system, Phase 5C functionality.
- Any modification to Phase 1, Phase 2, Phase 3, Phase 4, Phase 5A, or
  Phase 5B source files -- only their existing public seams are consumed.
- Any modification to the main OrcEngine architecture/roadmap/truth
  documents.

## Scope of this commit (Commit 1)

Charter, the planner (`fringelab::StickyLayerPlan` and three selection
policies), a residency model (`fringelab::StickyLayerModel`) extending
FL-07's single-call `forward_with_layer_runner` pattern to an arbitrary
sticky-layer set, and synthetic tests for both. **Not yet included:**
multi-step cached-decode integration (Commit 2) and real-model evidence
(Commit 3).

### Design

`Tools/OrcEngineFringeLab/include/fringelab/sticky_layer_plan.hpp` --
pure planning/validation logic over `LayerCostInfo` descriptors, no model
dependency:

- `LayerCostInfo { layer_id, resident_bytes, backing_bytes,
  std::optional<double> benefit_estimate, CostSource }` -- `CostSource` is
  `Measured` / `Estimated` / `Synthetic`, always disclosed.
- `StickyLayerPlan` -- immutable: policy name, byte budget, sorted-ascending
  sticky layer IDs, claimed resident bytes. No public mutator.
- `validate_plan(plan, layers)` -- checks every ID in `[0, n_layers)`,
  uniqueness, sortedness, that the claimed byte total equals the actual sum
  (overflow-checked), and that the sum does not exceed the budget. Every
  `plan_*()` function below calls this before returning, so an invalid plan
  can never escape as a return value.
- `plan_first_k` -- FL-07's original policy, generalized: greedily add
  layers 0, 1, 2, ... while the running total stays within budget.
- `plan_explicit_set` -- accepts a caller-supplied ID list verbatim (sorted,
  deduplicated-checked, budget-checked); never silently drops or reorders a
  requested layer.
- `plan_cost_per_byte` -- ranks all candidate layers by
  `benefit_estimate / resident_bytes` (descending), tie-broken by ascending
  `layer_id`, via `std::stable_sort` over an explicit `std::vector` --
  **never** decided by `unordered_map`/`unordered_set` iteration order.
  Throws if any layer lacks a `benefit_estimate` (unknown cost is never
  silently treated as zero) or has `resident_bytes == 0`. A single-pass
  greedy selection over the fixed ranking, not a knapsack solver -- never
  claims global optimality.

`Tools/OrcEngineFringeLab/include/fringelab/sticky_layer_model.hpp` --
`StickyLayerModel`, structurally identical to FL-07's `ResidencyBudgetModel`
except it accepts a `StickyLayerPlan` instead of an integer `n_resident_layers`,
so an arbitrary (non-prefix) set of layers can be held sticky. Reuses the
exact same seams FL-07 used (Phase 1's `forward_with_layer_runner`, Phase
3's `ModelSource`/`TensorMaterializer`/`ResidencyLedger`). Two optional
fault-injection hooks (`fault_before_cold_materialize`,
`fault_before_cold_execute`) exist for tests only, to prove ledger cleanup
on injected failures without needing a real, naturally-occurring fault.

### Residency invariant under test

> Any number of planner-selected sticky layers may remain resident for the
> execution-plan lifetime, while no more than one additional cold
> transformer layer is transiently materialized at once.

This is deliberately **not** "only one layer is resident globally" -- with
K sticky layers, K+1 transformer layers may coexist while a cold layer
executes. `StickyLayerModel` proves this the same way FL-07's model did:
sticky layers are materialized once at construction and handed to the
per-layer callback directly (bypassing `ResidencyLedger::enter_layer`
entirely, since they are never released); cold layers go through the exact
`enter_layer`/`leave_layer` pair FL-07 already used, which is what enforces
"at most one entered layer at a time" -- so `telemetry().peak_active_layers
<= 1` measures the cold-path invariant specifically, and is checked for
every tested plan.

## Commit 1 synthetic evidence (original, historical -- see Commit 1A below for corrections)

**This section is preserved as history and is NOT rewritten.** A Codex
review of Commit 1 found that several of these 43 checks did not fully
prove the claims this section originally made (last-position-only logit
comparison presented as complete-logit equivalence; aggregate
`materialization_count` deltas presented as proof of which specific layers
were read; `validate_plan()` never actually exercised against a
source-derived descriptor set, only against caller-supplied `LayerCostInfo`
vectors; the fault-injection hook fired before any tensor materialized,
never exercising PARTIAL cold-layer cleanup; a wording bug labeling a
hand-picked synthetic `benefit_estimate` as `CostSource::Measured`). None of
these were found to be a PRODUCTION correctness defect in the checked-in
Commit 1 code -- see "Commit 1A: corrections and hardened evidence" below
for the precise scope of what was strengthened and why the underlying
43/43 pass count was not simply wrong, only insufficiently proving.

`Tools/OrcEngineFringeLab/tests/test_fl07b_sticky_layer.cpp` (as it existed
at commit `70cb3946`), run against Phase 1's own synthetic fixture
(`Tools/OrcEnginePhase0/fixtures_phase1/fixture_untied.txt`, the same
fixture FL-07's own test uses) plus pure in-memory `LayerCostInfo`
descriptors for the planner-only cases.

**43/43 checks pass, 0 failures** (historical, commit `70cb3946`), across
all four required lanes:

| Lane | Command | Result |
|---|---|---|
| Debug | `cmake --build ... --config Debug --target test_fl07b_sticky_layer` | 43/43, exit 0 |
| Release | `cmake --build ... --config Release --target test_fl07b_sticky_layer` | 43/43, exit 0 |
| Strict | `/W4 /WX /permissive- /EHsc` (confirmed present in actual `cl.exe` commands) | 43/43, exit 0, zero real warnings (only benign `MSB8029`) |
| ASan | `/fsanitize=address /EHsc` (confirmed present in actual `cl.exe` commands, `C4530` absent) | 43/43, exit 0, zero AddressSanitizer diagnostics |

Coverage:

- **Planner unit tests** (16 checks): FirstK empty/full/partial-prefix
  selection; ExplicitSet non-prefix acceptance, out-of-range rejection,
  duplicate rejection, insufficient-budget rejection; deterministic
  repeated selection (FirstK and CostPerByte); CostPerByte unknown-cost
  rejection, budget-respecting higher-benefit-per-byte selection,
  deterministic ascending-layer-id tie-break; arithmetic-overflow rejection;
  `validate_plan` rejecting a hand-corrupted claimed-byte-total plan and an
  unsorted-ID plan.
- **Residency/bit-exactness tests** (27 checks): six named plans (zero
  sticky, one-sticky-first, non-prefix explicit set, prefix set at the same
  budget, ~half sticky, all sticky) each checked for exact logit and
  selected-token equality against Phase 1's own `forward()` reference, plus
  `peak_active_layers <= 1` for every plan; sticky-materialized-once (a
  second `forward()` call re-materializes only the cold layers); cold layers
  re-materialize on every call when zero are sticky; two independent
  fault-injection tests (cold-materialize failure, cold-execution failure)
  each proving the thrown exception propagates AND that a subsequent
  `forward()` call still succeeds and remains bit-exact, demonstrating
  ledger cleanup on the failure path rather than merely "the exception
  propagated".

## Commit 1A: corrections and hardened evidence

Addresses every Codex finding against Commit 1, listed in the same order
Codex raised them.

1. **Source-derived plan validation.** Added
   `layer_costs_from_source(const ModelSource&)` (`sticky_layer_model.hpp/
   cpp`): discovers every transformer layer in a `ModelSource`, requires
   exactly one complete 9-role descriptor per contiguous layer id in
   `[0, n_layers)` (delegating the contiguity check to the planner's own
   `require_contiguous_layer_descriptors()` -- one shared helper, not
   duplicated), computes actual resident/backing bytes with overflow-checked
   arithmetic, and rejects missing layers, duplicate layer ids, duplicate
   semantic tensor roles, missing required roles, and wrong tensor counts.
   `StickyLayerModel`'s constructor now calls this and then
   `validate_plan(plan_, actual_layers)` BEFORE materializing any bookend or
   sticky-layer tensor -- a plan is never trusted blindly, and a plan built
   from stale or incorrect size assumptions is rejected closed, before any
   side effect. `sticky_layer_bytes_` and the new byte totals inside
   `layer_costs_from_source` all use overflow-checked addition. Six new
   targeted negative tests (forged claimed byte total, insufficient budget
   against the plan's own declared budget, stale layer-size assumption,
   duplicate semantic tensor role, missing required tensor role, missing
   contiguous layer id) plus two new planner-level negative tests for
   `require_contiguous_layer_descriptors()` itself (gap, duplicate).
2. **Real partial-materialization failure.** Added `TrackingMaterializer`
   (test-only): wraps the real materializer, counts calls keyed by the
   LOGICAL TENSOR NAME (e.g. `"layer1.ffn_down"` -- layer/tensor identity,
   not a global call counter), and can be configured to throw when a
   specific named tensor is about to materialize. The partial-failure test
   now targets `layer1.ffn_down` (the 8th of 9 tensors in that layer),
   letting the first 7 succeed and be recorded before the fault fires.
   Immediately after the exception (before any recovery call), the test now
   asserts: the exception propagated; `telemetry().current_layer == -1`;
   `telemetry().current_resident_weight_bytes` equals EXACTLY the
   bookends+sticky baseline (no partial cold-layer bytes remain resident);
   a subsequent `forward()` call succeeds and is bit-exact; the sticky layer
   was not rematerialized by either the failed or the recovery call. The
   separate cold-EXECUTION fault test (weights fully resident, failure
   during `consume()`) now asserts the identical exact-baseline check
   immediately after the failure, not merely inferred from later success.
3. **Complete logits.** Every bit-exactness comparison now asserts
   `result.logits == reference.logits` (the full vector), not a
   last-`vocab`-elements slice. `selected_token` is still checked separately.
4. **Layer-identity-aware materialization proof.** `TrackingMaterializer`
   (see #2) is also used to prove, across 3 repeated `forward()` calls on a
   one-sticky-layer plan: the sticky layer's tensor materializes exactly
   once total (during construction only, never during any of the 3 calls);
   the cold layer's tensor materializes exactly once PER call (3 times
   total). Separate checks confirm zero-sticky rematerializes every call and
   all-sticky performs zero new transformer-layer materializations during
   `forward()`. Aggregate `materialization_count` deltas are retained as
   secondary corroboration in the byte-invariant checks below, not as the
   sole proof.
5. **Complete byte-residency invariant.** For every one of the six named
   plans: `sticky_resident_bytes() == plan.planned_resident_bytes()`;
   `telemetry().current_resident_weight_bytes` equals the directly-computed
   `bookend_bytes(model) + plan.planned_resident_bytes()` baseline
   immediately after construction AND after a successful `forward()`
   returns; `telemetry().peak_resident_weight_bytes <= baseline +
   largest_layer_bytes(layer_costs)`; `peak_active_layers` is asserted `== 0`
   for the all-sticky plan specifically and `== 1` for every plan where at
   least one cold layer is actually visited (never conflated with the total
   count of resident transformer layers). Both fault-injection tests (#2)
   assert the same exact baseline-bytes equality immediately after failure.
6. **Planner descriptor and benefit validation hardened.** Added
   `require_contiguous_layer_descriptors()` (declared once in
   `sticky_layer_plan.hpp`, called by all three policies -- `plan_first_k`,
   `plan_explicit_set`, `plan_cost_per_byte` -- rather than duplicated).
   `plan_cost_per_byte`'s `benefit_estimate` domain is now strictly and
   separately enforced and tested: `std::nullopt` rejected (unchanged);
   NaN rejected; +/-infinity rejected; NEGATIVE values rejected (decided
   explicitly: `benefit_estimate` represents a saved cost/benefit with no
   established meaning for a negative value in this experiment, so it is
   treated as caller error rather than silently accepted); exactly `0.0` IS
   accepted -- a well-defined, finite "no benefit" value, ranked last among
   positive-benefit layers but still selectable if budget allows (tested:
   included when budget covers all layers, excluded first under a tight
   budget). `resident_bytes == 0` remains rejected. The sort comparator only
   ever receives already-validated finite values; ascending-`layer_id`
   tie-break is unchanged and still tested for determinism.
7. **Provenance and wording corrected.**
   - The test file's own `layer_costs_from_model` synthetic-benefit helper
     (which assigned `benefit_estimate = 1.0`) has been REMOVED entirely
     (it was unused after the rewrite); every remaining test-side
     `benefit_estimate` assignment goes through `uniform_layers()`, which
     has always used `CostSource::Synthetic` -- the `CostSource::Measured`
     mislabeling Codex found no longer exists anywhere in this file.
   - `plan_explicit_set`'s doc comment corrected: it accepts requested IDs
     in ANY order and returns them in the plan's canonical ascending-sorted
     order (`StickyLayerPlan::sticky_layer_ids()` is always sorted, for
     every policy) -- this is a representation normalization, not a
     semantic reordering; the SET of selected layers is exactly the
     requested set. The prior wording's simultaneous claim of "order
     irrelevant" and "never reorders" is replaced with this precise
     distinction.
   - `layer_costs_from_source()`'s doc comment states explicitly that its
     `resident_bytes`/`backing_bytes` are real, source-declared sizes,
     while `benefit_estimate` is left `std::nullopt` (a `ModelSource` alone
     carries no cost/benefit opinion) -- measured byte size and a
     synthetic/estimated benefit value are never conflated.
   - `plan_cost_per_byte`'s "never claims global optimality: a single-pass
     greedy heuristic, not a knapsack solver" statement is preserved
     unchanged in the header comment.

### Commit 1A verification

**88/88 checks pass, 0 failures**, across all four required lanes (this
is the CURRENT, complete check count -- the historical 43/43 above is not
restated as current):

| Lane | Command | Result |
|---|---|---|
| Debug | `cmake --build ... --config Debug --target test_fl07b_sticky_layer` | 88/88, exit 0 |
| Release | `cmake --build ... --config Release --target test_fl07b_sticky_layer` | 88/88, exit 0 |
| Strict | `/W4 /WX /permissive- /EHsc` (confirmed present in actual `cl.exe` commands for `sticky_layer_plan.cpp`, `sticky_layer_model.cpp`, `test_fl07b_sticky_layer.cpp`) | 88/88, exit 0, zero real compiler warnings (only benign `MSB8029` project-system messages, not code warnings) |
| ASan | `/fsanitize=address /EHsc` (confirmed present in actual `cl.exe` commands, `C4530` absent) | 88/88, exit 0, zero AddressSanitizer diagnostics (only the informational `LNK4300` linker notice, not a code warning or sanitizer finding) |

No inherited Phase 1-5A suite was run (not required -- no shared-
infrastructure regression indicated).

## Disposition (interim, Commit 1A only)

Still not yet assessable -- multi-step correctness (question 1), cumulative
I/O divergence (question 2), and the real-model policy comparison
(questions 3-4) all require Commit 2 (cached-decode integration) and
Commit 3 (real-model evidence), neither of which exists yet. Commit 1A
strengthens (does not merely restate) the claim that the planner and
single-call residency mechanics are correct and oracle-independent,
including now under a genuinely-exercised source-derived validation path
and identity-aware materialization proof -- still the necessary foundation
for later commits, not the answer to FL-07B's actual research questions.

## Limitations (Commit 1A)

- Single-call `forward()` only (Phase 1's seam) -- no cached decode yet.
- Synthetic fixture only -- no real GGUF model exercised in this commit.
- `MaterializationCostPerByte`'s tests use uniform or hand-picked synthetic
  `benefit_estimate` values chosen to be unambiguous for assertion purposes,
  not derived from any real measurement; `CostSource::Synthetic` is used
  throughout this commit's tests for exactly that reason.
- The identity-aware `TrackingMaterializer` keys on the `LogicalTensor`
  name string constructed by the test's own `bind_memory_model()` helper
  (e.g. `"layer1.ffn_down"`); this is a test-harness convention, not a
  guarantee from `ModelSource` itself, so a real GGUF-backed `ModelSource`
  (Commit 2/3) would need its own equivalent naming to reuse this exact
  technique -- noted here so Commit 2 does not assume it transfers for free.

## Scope of this commit (Commit 2, original -- see Commit 2A below for corrections)

**This section is preserved as history and is NOT rewritten.** A Codex
review of Commit 2 found four gaps: `plan_first_k()` could select the
wrong layer for an unordered input vector; the cumulative
materialization/I-O evidence was a bare "nonzero" check rather than exact
formulas; the "cold-layer execution fault" test fired its hook BEFORE
`execute_cached_transformer_layer` ran, never actually exercising a
failure from inside cached-layer execution despite this section's own
"during the execute call itself" wording below; and the phrase "physical
KV-cache contents" overstated what the comparison helper actually checked
(committed rows only, not unused capacity). None were found to be a
PRODUCTION correctness defect in the checked-in Commit 2 code -- see
"Commit 2A: corrections and hardened evidence" below for what changed and
why. Wherever the text below says "physical", the accurate word is
"committed" (see Commit 2A finding 4) -- left as originally written here
rather than silently edited.

Multi-step cached-decode integration: threads Phase 5A's KV-cache seam
(`execute_cached_transformer_layer`, called via `forward_cached_step` for
the reference and directly for the new class) through the sticky/cold
layer split across an arbitrary number of `step()` calls against one
persistent `ContiguousAttentionKVStore`. Reuses Phase 5A's public seam
UNMODIFIED; does not touch any frozen Phase 1-5A file. **Not yet
included:** real-GGUF evidence, timing measurements, any policy
conclusion, UI, or production integration (Commit 3).

### Design

`Tools/OrcEngineFringeLab/include/fringelab/sticky_layer_cached_model.hpp` /
`src/sticky_layer_cached_model.cpp` -- `StickyLayerCachedModel`, structured
as StickyLayerModel's constructor-time sticky-materialization pattern
(itself re-validated per Commit 1A's `layer_costs_from_source()` /
`validate_plan()` discipline before any materialization) plus a `step()` /
`step_unsafe_explicit_position()` pair mirroring Phase 5A's own
`VirtualizedCachedModel::step()` contract exactly:

- `step()` REQUIRES `start_position == cache.current_length()`, throwing
  `KVCacheError` before any mutation if it does not (the same P5A-RVW-002
  invariant Phase 5A's own safe entry points enforce).
- Every call: sticky layers run `execute_cached_transformer_layer` directly
  against their permanently-resident weights (no `enter_layer`/`leave_layer`
  -- they are never released); each cold layer is materialized fresh via
  `enter_layer`/`materialize_layer`/`execute_cached_transformer_layer`/
  `leave_layer`/`released`, identical to `StickyLayerModel::forward()`'s
  cold-layer path, just invoked once per `step()` call instead of once per
  object lifetime -- so a cold layer visited across N steps materializes N
  separate times, never cached between steps.
- `cache.set_current_length(start_position+new_len)` is called on the
  success path ONLY, exactly mirroring `forward_cached_step` /
  `VirtualizedCachedModel::step`'s own commit-on-success-only discipline
  (P5A-RVW-002/P5A-RVW-003 lineage) -- a failed step leaves the cache's
  COMMITTED length exactly where it was before the call. This is a logical
  rollback via the committed-length gate, not a physical zeroing of any
  bytes an earlier-executing layer of the same failed step may already have
  written into the cache's K/V storage -- the same distinction
  `context.hpp`'s own `write_k`/`write_v` documentation draws, and the test
  evidence below states it exactly this way rather than overclaiming a
  physical wipe.
- `fault_before_cold_materialize` / `fault_before_cold_execute` hooks now
  carry a `step_index` parameter (0-based, advancing only on a successful
  call) alongside the layer id, so a test can target e.g. "fail only on
  step 3's cold layer" -- not possible with Commit 1's single-call hooks,
  which had no notion of "which step".

`Tools/OrcEngineFringeLab/tests/test_fl07b_cached_decode.cpp` -- a shared
5-step schedule (a 3-token prefill, then four 1-token decode steps) run
against every tested plan and an independently-constructed Reference Path
B run (`forward_cached_step` against the plain, fully-resident `Model` --
Phase 5A code, called but not modified). Token `5` (first used at position
1) reappears at position 4, and token `1` (first used at position 0)
reappears at position 6 -- both are NEW absolute positions for an
already-seen token id, deliberately exercising RoPE-position-dependent
correctness rather than any (incorrect) token-identity-keyed caching.

The synthetic fixture (`fixtures_phase1/fixture_untied.txt`) has only 2
transformer layers, which caps the number of DISTINCT sticky sets at four:
`{}`, `{0}`, `{1}`, `{0,1}`. The plan matrix therefore builds 6 plans
(FirstK zero/one-sticky, ExplicitSet non-prefix `{1}`, FirstK all-sticky,
and two `MaterializationCostPerByte` constructions with synthetic benefit
data -- a tight budget landing on the non-prefix `{1}` and a generous
budget landing on `{0,1}`) across those 4 sets, rather than 6-7 pairwise
distinct sets -- stated here plainly as a fixture ceiling, not claimed as
broader coverage than the 2-layer fixture allows. A 2-layer-only fixture
also means the fault-injection tests below can only target layer 1 as the
cold layer (the only cold layer that can ever exist when layer 0 is
sticky).

Per plan, per step, the test asserts: complete `logits` vector bit-identical
to the reference; `selected_token` identical; the committed
`cache.current_length()` correct for that step; and, after the full
5-step sequence, `caches_physically_identical()` -- every layer, every
kv_head, every committed position's K and V vectors, plus cache metadata
(`n_layers`/`n_kv_heads`/`max_positions`/`head_dim`/`current_length`) --
between that plan's cache and the reference cache. A separate cross-plan
check confirms two DIFFERENT policies that land on the same final sticky
set (FirstK all-sticky and CostPerByte generous-budget, both `{0,1}`) agree
with each other step-by-step, not merely each independently with the
reference.

### Cumulative materialization/backing-byte evidence

Using the same `TrackingMaterializer` pattern as Commit 1A (keyed on
`LogicalTensor` name, e.g. `"layer1.ffn_down"`), run across the full
5-step sequence with layer 0 sticky / layer 1 cold: layer 0's tensor
materializes exactly ONCE total (construction only, across all 5 steps);
layer 1's tensor materializes exactly ONCE PER STEP (5 times total, never
cached between steps). `telemetry().backing_bytes_read` is asserted
nonzero and reported as descriptive evidence only -- this test does NOT
draw any conclusion about which residency budget is cheaper in cumulative
I/O terms (FL-07B's question 2); that comparison requires real-model
timing/byte data and is explicitly left to Commit 3.

### Fault injection with cache-rollback verification

Two independent mid-sequence fault tests, both asserting the SAME shape of
evidence: (a) the exception propagates; (b) `cache.current_length()` is
exactly its PRE-step value immediately after the failure (logical
rollback); (c) `telemetry().current_layer == -1` and
`telemetry().current_resident_weight_bytes` returns exactly to the
bookends+sticky baseline immediately after, not merely inferred from a
later successful call; (d) a RETRIED call with the same tokens/position
succeeds and is bit-identical to the reference for that step; (e) every
subsequent step in the schedule remains bit-identical to the reference;
(f) the complete physical KV-cache contents match the reference once the
full sequence finishes despite the mid-sequence fault and retry.

1. **Partial cold-layer materialization failure** (step index 2, the
   repeated-token-5 decode step): `TrackingMaterializer` targets
   `"layer1.ffn_down"` (the 8th of 9 tensors), letting the first 7 of that
   step's cold-layer tensors materialize and be recorded before the fault
   fires.
2. **Cold-layer execution failure** (step index 3): `fault_before_cold_execute`
   fires after the cold layer's weights are fully resident, during the
   `execute_cached_transformer_layer` call itself.

A separate, non-fault test independently confirms `step()`'s
position-invariant guard: a position-mismatched call is rejected with
`KVCacheError` before any mutation, and the cache's committed length is
unchanged by the rejected call.

### Commit 2 verification

**136/136 checks pass, 0 failures** in `test_fl07b_cached_decode`, across
all four required lanes. `test_fl07b_sticky_layer` was rebuilt and rerun
unchanged alongside it to confirm no regression from the CMakeLists.txt
changes (adding the Phase 5A dependency, the new library/executable
targets): still **88/88, 0 failures**.

| Lane | Command | cached_decode result | sticky_layer regression check |
|---|---|---|---|
| Debug | `cmake --build ... --config Debug --target test_fl07b_cached_decode test_fl07b_sticky_layer` | 136/136, exit 0 | 88/88, exit 0 |
| Release | `cmake --build ... --config Release --target test_fl07b_cached_decode test_fl07b_sticky_layer` | 136/136, exit 0 | 88/88, exit 0 |
| Strict | `-DCMAKE_CXX_FLAGS="/permissive- /WX /EHsc"` (base `/W4` already applied per-target; confirmed present in actual `cl.exe` invocations for every new/changed source file) | 136/136, exit 0, zero real compiler warnings | 88/88, exit 0 |
| ASan | `-DCMAKE_CXX_FLAGS="/fsanitize=address /EHsc"` (confirmed present in actual `cl.exe` invocations, `C4530` absent; `clang_rt.asan_dynamic-x86_64.dll` copied next to the Debug-config executables, which the ASan-instrumented binaries require at load time) | 136/136, exit 0, zero AddressSanitizer diagnostics | 88/88, exit 0 |

CMakeLists.txt change: `add_subdirectory(../OrcEnginePhase3 phase3)` was
replaced with `add_subdirectory(../OrcEnginePhase5A phase5a)` (Phase 5A
already pulls in Phase 3, which pulls in Phase 2/1, transitively -- adding
Phase 3 a second time directly would double-define its targets). Two new
targets: `fringelab_sticky_layer_cached_model` (library) and
`test_fl07b_cached_decode` (executable + registered CTest
`fringelab_fl07b_cached_decode`).

## Disposition (Commit 2)

Question 1 (does correctness hold across a real multi-step cached decode)
now has affirmative synthetic evidence: every tested plan is bit-identical
to an independently-constructed fully-resident reference across a 5-step
prefill+decode sequence including two repeated-token/new-position cases,
with full physical KV-cache equivalence, not just logits equivalence.
Question 2 (cumulative I/O divergence across budgets) has only descriptive
telemetry numbers here (nonzero `backing_bytes_read`), no comparative
conclusion -- that requires real-model measurement (Commit 3). Questions
3-4 (explicit planner-selected sets, cost-per-byte policy) have further
synthetic confirmation extended to the multi-step setting, but the
real-model comparison Commit 3 is meant to provide is still absent.
Question 5 (honest tie/null reporting) has not yet been exercised against
a real model with actual measured costs -- the synthetic `benefit_estimate`
values in this commit are deliberately fabricated to produce an
unambiguous non-prefix selection, not derived from any real measurement.

## Limitations (Commit 2)

- Synthetic fixture only, 2 transformer layers -- no real GGUF model
  exercised; see the plan-matrix limitation above (4 distinct sticky sets,
  not more) and the single-cold-layer-target limitation on the
  fault-injection tests.
- `MaterializationCostPerByte`'s synthetic `benefit_estimate` values in
  this commit are hand-picked to produce an unambiguous non-prefix
  selection for test purposes, not derived from any real measurement --
  `CostSource::Synthetic` throughout.
- `telemetry().backing_bytes_read` is reported descriptively only; no
  cumulative-I/O-divergence conclusion across budgets is drawn (Commit 3).
- No timing data (materialize/execute milliseconds) is collected or
  compared in this commit.
- The 5-step schedule is fixed and hand-authored for this commit; it is
  not a real decode trace and does not exercise `max_positions` boundary
  behavior, cache eviction, or sequences longer than a handful of steps.

## Commit 2A: corrections and hardened evidence

Addresses every finding from a Codex review of Commit 2, in the order
raised.

1. **`plan_first_k()` could select the wrong layer for an unordered input
   vector.** `require_contiguous_layer_descriptors()` only proved the SET
   of layer ids in `layers` was complete and contiguous -- it never
   required the VECTOR's element order to match ascending `layer_id`.
   `plan_first_k()` iterated `layers` directly, so a caller-supplied
   vector ordered e.g. `{id 1, id 0}` could make FirstK select layer 1
   before layer 0 for a one-layer budget, silently breaking its documented
   "starts at layer 0" guarantee (`validate_plan()` could not catch this:
   a single selected id is trivially "sorted"). **Fixed** in
   `sticky_layer_plan.cpp`: `plan_first_k()` now iterates layer id `0, 1,
   2, ...` explicitly and looks each descriptor up by id (`find_layer`),
   so its result depends only on which ids are present, never on the input
   vector's iteration order. `validate_plan()` now also calls
   `require_contiguous_layer_descriptors(layers)` itself at the top of its
   own body, making its public contract self-contained rather than merely
   assuming every caller already checked that (defense in depth; every
   `plan_*()` policy and `layer_costs_from_source()` already called it
   before this change, so this is redundant for those callers but closes
   the gap for a caller that builds a `StickyLayerPlan` directly and hands
   it to `validate_plan()` without going through a policy function first).
   Two new regression tests in `test_fl07b_sticky_layer.cpp` construct
   deliberately scrambled `layers` vectors (`{id 1, id 0}` and `{id 2, id
   0, id 1}`) and confirm `plan_first_k` still selects the true ascending
   prefix (`{0}` and `{0,1}` respectively, never a vector-order-derived
   set); a third new test confirms `validate_plan()` now rejects a gap in
   `layers` itself, not only a gap in the plan's own selected ids.
2. **Cumulative materialization/I-O evidence was a bare "nonzero" check.**
   `test_fl07b_cached_decode.cpp`'s cumulative-proof block asserted only
   `telemetry().backing_bytes_read > 0`, which does not establish the
   exact deterministic schedule the charter and Commit 2's own report
   claimed. **Fixed:** every named plan in the main per-plan loop now
   asserts EXACT formulas, computed independently from `layer_costs` and
   the plan's own sticky/cold split, against every relevant
   `StreamingTelemetry` field:
   `materialization_count == bookend_tensor_count + sticky_count*9 +
   steps*cold_count*9`; `release_count == steps*cold_count*9` (bookends
   and sticky layers never release); `backing_bytes_read == bookend_backing
   + sticky_backing + steps*cold_backing_per_step`;
   `repeated_backing_bytes_read == (steps-1)*cold_backing_per_step`
   (`ResidencyLedger::materialized()` keys repetition by
   `backing_identity` via its own `seen_extents_` set -- only cold-layer
   reads, which recur every step, ever count as repeated; bookends and
   sticky layers materialize exactly once, ever); `peak_active_layers == 1`
   iff the plan has at least one cold layer, else `0`;
   `peak_resident_weight_bytes == baseline + the largest single cold
   layer's resident bytes among the plan's cold set` (exactly one cold
   layer is ever resident at an instant, proven by
   `ResidencyLedger::enter_layer`'s own more-than-one-resident guard, so
   the peak is never the SUM of multiple cold layers even when a plan's
   cold set has more than one member). `sticky_resident_bytes() ==
   plan.planned_resident_bytes()` is also now asserted for the cached
   model (it was already asserted for `StickyLayerModel` in Commit 1A but
   missing here). The permanent baseline
   (`current_resident_weight_bytes == expected_baseline`) is now asserted
   after EVERY individual step, not only once after the full sequence.
   The bare `> 0` check that used to end the identity-aware
   `TrackingMaterializer` block was removed as redundant once the exact
   formulas above cover the same plan construction under a different
   check name.
3. **The "cold-layer execution fault" test never exercised a genuine
   in-execution failure.** Its `fault_before_cold_execute` hook fires
   BEFORE `execute_cached_transformer_layer` is called at all -- the test
   proved that fully-materialized cold-layer weights are released
   correctly when a PRE-execution hook throws, but Commit 2's own "Design"
   section above claims the failure occurs "during the execute call
   itself", which that test did not actually exercise. **Fixed:** replaced
   with a genuine in-execution failure using a new
   `CorruptingMaterializer` (test-only): it lets the real materializer run
   normally, then on a specifically-targeted occurrence of one named
   tensor, overwrites every element of the already-materialized
   `ResidentView` with NaN. Targeting `layer1.ffn_down`'s 4th
   materialization call (armed from before any step runs, so the
   occurrence counter advances correctly across steps 0, 1, 2 before the
   4th call -- an initial version of this fix armed the target only right
   before the intended step and silently missed the fault entirely,
   caught by the retried step's own position-invariant guard throwing an
   unrelated, uncaught `KVCacheError` later in the run; fixed by arming
   before the warm-up steps) makes Phase 5A's own `check_finite()`
   (`forward_cached.cpp`'s local helper, called on
   `"layer1.post_ffn_residual"` inside `execute_cached_transformer_layer`,
   unmodified) throw from strictly INSIDE that function -- after layer 1's
   K/V has already been written into the cache for this step (K/V writes
   happen near the top of `execute_cached_transformer_layer`, before the
   FFN), but strictly before `step()`'s commit-on-success-only
   `cache.set_current_length()` call. The test asserts: the exception
   message contains `"NaN/Inf detected"`, confirming it genuinely
   originated from Phase 5A's own finite-check, not a test-injected
   stand-in; `cache.current_length()` unchanged; the COMMITTED prefix
   (every layer/kv_head/position below the pre-fault length, snapshotted
   before the fault and compared byte-for-byte after) is untouched, across
   BOTH layers, not only the one that failed; resident bytes return to
   baseline; a retried call succeeds and is bit-identical to the
   reference; and -- the direct "retry overwrites uncommitted data" proof
   the review specifically asked for -- the retried step's own new
   position's K/V, for every layer and kv_head, is compared directly
   against the reference cache's K/V at that same position immediately
   after the retry, confirming the previously-NaN-poisoned-but-uncommitted
   row was genuinely overwritten with the correct values, not merely that
   `current_length()` advanced past it.
4. **"Physical" cache-comparison wording was overstated.** The comparison
   helper only ever compared committed rows (`[0, current_length())`), not
   unused capacity out to `max_positions()` -- correct and sufficient for
   Phase 5A's own committed-cache contract (`context.hpp`), but "physical"
   implies comparing every byte of underlying storage including
   never-written capacity, which this helper never did. Per the review's
   explicit recommendation, NO physical zeroing or rollback machinery was
   added (unnecessary complexity for what Phase 5A's contract actually
   guarantees) -- only the wording was corrected. **Fixed:** the helper is
   renamed `caches_committed_contents_identical()` (was
   `caches_physically_identical()`), its doc comment states explicitly
   what it does and does not compare, and every call site's check label
   now says "complete committed KV-cache contents", not "complete physical
   KV-cache contents".

### Commit 2A verification

**213/213 checks pass, 0 failures** in `test_fl07b_cached_decode`
(up from Commit 2's 136 -- the increase is the new per-plan exact-formula
block times six plans, the rewritten fault-injection-2 test's added
committed-prefix and retry-overwrite proofs, and per-step baseline
assertions). **91/91 checks pass, 0 failures** in `test_fl07b_sticky_layer`
(up from Commit 1A's 88 -- the three new FirstK-order-independence and
validate_plan-descriptor-check regression tests). Both across all four
required lanes:

| Lane | Command | cached_decode | sticky_layer |
|---|---|---|---|
| Debug | `cmake --build ... --config Debug --target test_fl07b_cached_decode test_fl07b_sticky_layer` | 213/213, exit 0 | 91/91, exit 0 |
| Release | `cmake --build ... --config Release --target test_fl07b_cached_decode test_fl07b_sticky_layer` | 213/213, exit 0 | 91/91, exit 0 |
| Strict | `-DCMAKE_CXX_FLAGS="/permissive- /WX /EHsc"` (confirmed present in actual `cl.exe` invocations for every changed source file, zero real warnings) | 213/213, exit 0 | 91/91, exit 0 |
| ASan | `-DCMAKE_CXX_FLAGS="/fsanitize=address /EHsc"` (confirmed present in actual `cl.exe` invocations, `C4530` absent; `clang_rt.asan_dynamic-x86_64.dll` copied next to the Debug-config executables) | 213/213, exit 0, zero AddressSanitizer diagnostics | 91/91, exit 0, zero AddressSanitizer diagnostics |

No source file outside `Tools/OrcEngineFringeLab/` was touched; no frozen
Phase 1-5A file was touched; `CMakeLists.txt` was not modified in this
commit (Commit 2's targets already build both test files unchanged).

## Disposition (Commit 2A)

The correctness/evidence gaps a Codex review found in Commit 2 are closed:
FirstK is now genuinely order-independent of its input vector; the
cumulative materialization/I-O claims are now exact formulas, not a
"nonzero" placeholder; the second fault-injection test now exercises a
genuine in-execution failure with committed-prefix and retry-overwrite
proof, not a pre-execution stand-in; and "physical" cache-content claims
are now accurately worded as "committed". This does not change FL-07B's
research disposition from Commit 2: questions 1, 3, and 4 have stronger
synthetic evidence than before, question 2 (cumulative I/O divergence
across budgets) still has only descriptive numbers with no comparative
conclusion, and question 5 (honest tie/null reporting against a real
model) remains unexercised. Commit 3 (real-GGUF evidence) is still the
next and only remaining step before FL-07B's actual research questions can
be answered.

## Limitations (Commit 2A)

All limitations listed under "Limitations (Commit 2)" above still apply
unchanged (synthetic fixture only, 2 layers, fabricated
`MaterializationCostPerByte` benefit data, no timing data, fixed
hand-authored schedule). Additionally:

- `CorruptingMaterializer`'s occurrence-counting is a test-harness
  convention (1-indexed calls to one named tensor), not a `ModelSource`
  guarantee -- a real GGUF-backed materializer (Commit 3) would need its
  own equivalent mechanism to reuse this exact fault-injection technique.
- The exact telemetry formulas in this commit are specific to this test's
  own 5-step schedule and 2-layer fixture (e.g. `steps=5`); they are
  correct DERIVATIONS from `ResidencyLedger`'s documented counting
  semantics, not a general formula proven for arbitrary schedules or layer
  counts -- a longer or differently-shaped schedule would need its own
  (differently-parameterized) formula, not a verbatim reuse of these
  numbers.
