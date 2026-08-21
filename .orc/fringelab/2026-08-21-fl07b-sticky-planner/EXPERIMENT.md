# FL-07B — Multi-Step Sticky-Layer Planning

Status: **IN PROGRESS — Commit 1A of 3 (Commit 1 corrected/hardened per Codex review; Commit 2 not yet started)**

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
