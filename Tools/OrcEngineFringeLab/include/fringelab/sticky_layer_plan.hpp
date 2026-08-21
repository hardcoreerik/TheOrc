// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B -- Multi-Step Sticky-Layer Planning. NEW, standalone Fringe Lab
// code; does not modify any frozen Phase 1/2/3/4/5A file. Extends FL-07's
// "first N layers resident" model to an explicit, immutable set of resident
// transformer-layer IDs, selected once before execution under a resident-
// weight byte budget. This header is pure planning/validation logic -- it
// has no dependency on the model source or the forward/cached-decode seams,
// so it can be unit-tested entirely with synthetic layer descriptors.
//
// EXPERIMENTAL RESEARCH ONLY. Not a stable ABI, not a production
// ExecutionPlanner, not promoted into OrcEngine proper by this file's mere
// existence. See .orc/fringelab/2026-08-21-fl07b-sticky-planner/ for the
// experiment charter and evidence.
#pragma once

#include <cstdint>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace fringelab {

// Where a layer's benefit_estimate came from. The cost-per-byte policy
// must disclose this, never silently treat "unknown" as "zero benefit".
enum class CostSource {
    Measured,   // taken from an actual recorded materialization/read time
    Estimated,  // derived from a source-reported size/heuristic, not timed
    Synthetic,  // deliberately fabricated for a planner-mechanics-only proof
};

// One transformer layer's known planning inputs. `benefit_estimate` is
// std::nullopt when the caller has no cost opinion for this layer --
// MaterializationCostPerByte MUST reject (throw), not default it to 0,
// if it is asked to rank a layer with no cost data.
struct LayerCostInfo {
    int64_t layer_id = 0;
    uint64_t resident_bytes = 0;   // bytes held while this layer stays resident
    uint64_t backing_bytes = 0;    // bytes read from backing storage to materialize it once
    std::optional<double> benefit_estimate;  // higher = more beneficial to keep resident; scale is policy-defined
    CostSource cost_source = CostSource::Measured;
};

// An immutable, already-validated selection of sticky (permanently
// resident) transformer-layer IDs. Construct only via one of the plan_*()
// functions below or via validate_and_build() directly from a caller-
// supplied ID list (ExplicitSet policy) -- there is no public mutator.
class StickyLayerPlan {
public:
    StickyLayerPlan(std::string policy_name, uint64_t byte_budget, std::vector<int64_t> sticky_layer_ids,
                    uint64_t planned_resident_bytes)
        : policy_name_(std::move(policy_name)),
          byte_budget_(byte_budget),
          sticky_layer_ids_(std::move(sticky_layer_ids)),
          planned_resident_bytes_(planned_resident_bytes) {}

    const std::string& policy_name() const { return policy_name_; }
    uint64_t byte_budget() const { return byte_budget_; }
    // Sorted ascending by construction (every plan_*() function below
    // guarantees this; validate_plan() checks it).
    const std::vector<int64_t>& sticky_layer_ids() const { return sticky_layer_ids_; }
    uint64_t planned_resident_bytes() const { return planned_resident_bytes_; }
    bool is_sticky(int64_t layer_id) const;

private:
    std::string policy_name_;
    uint64_t byte_budget_;
    std::vector<int64_t> sticky_layer_ids_;
    uint64_t planned_resident_bytes_;
};

// Thrown by validate_plan() and every plan_*() function for any invariant
// violation (out-of-range ID, duplicate ID, budget exceeded, claimed-total
// mismatch, missing cost data, arithmetic overflow). Always fail-closed --
// never silently clamp, truncate, or drop a layer from the caller's request.
class StickyPlanError : public std::runtime_error {
public:
    explicit StickyPlanError(const std::string& message)
        : std::runtime_error("FL-07B sticky-layer plan error: " + message) {}
};

// Validates `plan` against `layers` (which must describe exactly layer IDs
// [0, layers.size())). Checks: every sticky ID is in range and unique; the
// claimed planned_resident_bytes() equals the actual sum of the selected
// layers' resident_bytes (overflow-checked); that sum does not exceed
// byte_budget() (which governs sticky TRANSFORMER-LAYER bytes only --
// bookend tensors and the single transient cold layer are accounted for
// separately by the caller's own residency invariant, not by this budget);
// sticky_layer_ids() is sorted ascending. ALSO verifies `layers` itself is
// a complete, contiguous, non-duplicate descriptor set for [0,
// layers.size()) (delegates to require_contiguous_layer_descriptors() --
// Commit 2A: this makes validate_plan's own public contract self-contained
// rather than assuming its caller already checked that). Throws
// StickyPlanError on any violation. Does not mutate `plan`.
void validate_plan(const StickyLayerPlan& plan, const std::vector<LayerCostInfo>& layers);

// Shared precondition every plan_*() function below requires: `layers` must
// contain EXACTLY one descriptor per contiguous layer id in
// [0, layers.size()) -- no gaps, no duplicates, no id outside that range.
// This is the same "exactly one descriptor per id" contract
// StickyLayerModel's source-derived validation (sticky_layer_model.hpp)
// separately enforces when the descriptors come from a real ModelSource;
// this function is the pure, model-independent half of that same
// requirement, reused by all three policies instead of being duplicated
// three times. Throws StickyPlanError on violation.
void require_contiguous_layer_descriptors(const std::vector<LayerCostInfo>& layers);

// FirstKBaseline: preserves FL-07's original policy exactly -- materialize
// layers 0, 1, 2, ... in order while the running byte total (checked for
// overflow) stays within `byte_budget`; stop at the first layer that would
// exceed it. An empty result (byte_budget too small for even layer 0) is
// valid and equivalent to full streaming. Selecting all layers is valid
// only when the budget is sufficient for their full sum. Resolves layer id
// 0, 1, 2, ... by explicit lookup (Commit 2A) -- the result never depends
// on what ORDER `layers` happens to store its elements in, only on the ids
// present (which require_contiguous_layer_descriptors() already requires
// to be exactly [0, layers.size())).
StickyLayerPlan plan_first_k(const std::vector<LayerCostInfo>& layers, uint64_t byte_budget);

// ExplicitSet: accepts a caller-supplied list of exact layer IDs in any
// order (duplicates rejected) and returns them in the plan's canonical
// ascending-sorted order (StickyLayerPlan::sticky_layer_ids() is ALWAYS
// sorted ascending, for every policy -- this is a normalization of
// representation, not a semantic reordering: the SET of selected layers is
// exactly the requested set, never a subset, superset, or substitution).
// Validates the request against `byte_budget` via the same checks
// validate_plan() performs -- this function IS validate_and_build() for
// this policy: a request that violates any invariant throws
// StickyPlanError rather than returning a partial or adjusted plan.
StickyLayerPlan plan_explicit_set(const std::vector<LayerCostInfo>& layers, std::vector<int64_t> requested_ids,
                                  uint64_t byte_budget);

// MaterializationCostPerByte: an EXPERIMENTAL, deliberately simple
// deterministic heuristic. Ranks every layer in `layers` by
// benefit_estimate / resident_bytes (higher first), breaking ties by
// ascending layer_id (never by container iteration order -- ranking is
// computed via std::stable_sort over an explicit std::vector, never an
// unordered_map/unordered_set), then greedily adds layers in that order
// while the running byte total stays within `byte_budget`. Never claims
// global optimality: this is a single-pass greedy heuristic over a fixed
// ranking, not a knapsack solver.
//
// benefit_estimate domain, strictly enforced -- throws StickyPlanError if
// ANY layer in `layers` violates any of these:
//   - std::nullopt is rejected (unknown cost is never silently treated as
//     zero)
//   - NaN is rejected
//   - +/-infinity is rejected
//   - negative values are rejected (benefit_estimate represents a saved
//     materialization cost/benefit; a negative "benefit" has no
//     established real-world meaning in this experiment and is treated as
//     a caller error rather than silently accepted)
//   - exactly 0.0 IS accepted -- a well-defined, finite "no measured
//     benefit" value, ranked last among layers with any positive benefit
//     but still eligible for selection if budget allows
// resident_bytes == 0 is also rejected (benefit-per-byte is undefined).
StickyLayerPlan plan_cost_per_byte(const std::vector<LayerCostInfo>& layers, uint64_t byte_budget);

}  // namespace fringelab
