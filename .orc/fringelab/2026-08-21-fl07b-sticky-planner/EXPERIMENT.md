# FL-07B — Multi-Step Sticky-Layer Planning

Status: **IN PROGRESS — Commit 1 of 3 (charter + planner/residency implementation + synthetic tests)**

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

## Synthetic evidence (this commit)

`Tools/OrcEngineFringeLab/tests/test_fl07b_sticky_layer.cpp`, run against
Phase 1's own synthetic fixture (`Tools/OrcEnginePhase0/fixtures_phase1/fixture_untied.txt`,
the same fixture FL-07's own test uses) plus pure in-memory `LayerCostInfo`
descriptors for the planner-only cases.

**43/43 checks pass, 0 failures**, across all four required lanes:

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

## Disposition (interim, Commit 1 only)

Not yet assessable -- multi-step correctness (question 1), cumulative I/O
divergence (question 2), and the real-model policy comparison (questions
3-4) all require Commit 2 (cached-decode integration) and Commit 3
(real-model evidence), neither of which exists yet. This commit establishes
that the planner and single-call residency mechanics are correct and
oracle-independent (i.e. self-consistent against Phase 1's own reference),
which is the necessary foundation for those later commits, not the answer
to FL-07B's actual research questions.

## Limitations (Commit 1)

- Single-call `forward()` only (Phase 1's seam) -- no cached decode yet.
- Synthetic fixture only -- no real GGUF model exercised in this commit.
- `MaterializationCostPerByte`'s tests use uniform or hand-picked synthetic
  `benefit_estimate` values chosen to be unambiguous for assertion purposes,
  not derived from any real measurement; `CostSource::Synthetic` is used
  throughout this commit's tests for exactly that reason.
