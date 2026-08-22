// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "fringelab/sticky_layer_plan.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <set>

namespace fringelab {

namespace {

// Overflow-checked accumulation, matching the checked_add convention
// already established in OrcEngine's frozen phases (Phase 2/3) -- FL-07B
// reuses the PATTERN, not their code (no frozen file is included or
// modified).
uint64_t checked_add(uint64_t a, uint64_t b) {
    if (a > std::numeric_limits<uint64_t>::max() - b) {
        throw StickyPlanError("resident-byte accumulation overflowed uint64_t");
    }
    return a + b;
}

const LayerCostInfo& find_layer(const std::vector<LayerCostInfo>& layers, int64_t layer_id) {
    for (const LayerCostInfo& l : layers) {
        if (l.layer_id == layer_id) return l;
    }
    throw StickyPlanError("layer id " + std::to_string(layer_id) + " not present in the supplied layer descriptors");
}

}  // namespace

void require_contiguous_layer_descriptors(const std::vector<LayerCostInfo>& layers) {
    std::set<int64_t> seen;
    for (const LayerCostInfo& l : layers) {
        if (l.layer_id < 0 || l.layer_id >= static_cast<int64_t>(layers.size())) {
            throw StickyPlanError("layer descriptor id " + std::to_string(l.layer_id) + " outside [0, " +
                                  std::to_string(layers.size()) + ") -- descriptors must be exactly one per "
                                  "contiguous layer id");
        }
        if (!seen.insert(l.layer_id).second) {
            throw StickyPlanError("duplicate layer descriptor for id " + std::to_string(l.layer_id));
        }
    }
    // seen.size() == layers.size() is implied by: no duplicates (checked
    // above) and every id in-range over a set the same size as layers --
    // together these force every id in [0, layers.size()) to be present
    // exactly once (a range of N distinct values in [0,N) with no gaps).
}

bool StickyLayerPlan::is_sticky(int64_t layer_id) const {
    // sticky_layer_ids_ is sorted ascending by construction -- binary search,
    // not a linear scan or an unordered-container lookup.
    return std::binary_search(sticky_layer_ids_.begin(), sticky_layer_ids_.end(), layer_id);
}

void validate_plan(const StickyLayerPlan& plan, const std::vector<LayerCostInfo>& layers) {
    // Commit 2A: validate_plan's own public contract is now self-contained
    // -- it no longer merely ASSUMES `layers` describes exactly one
    // descriptor per contiguous id in [0, layers.size()); it verifies that
    // itself, via the same shared helper every plan_*() policy already
    // calls. Redundant when the caller already called it (which every
    // plan_*() function and layer_costs_from_source() do), but closes the
    // gap for any caller that constructs a StickyLayerPlan directly and
    // hands it to validate_plan() without going through a plan_*() policy
    // first (e.g. StickyLayerModel/StickyLayerCachedModel's constructors).
    require_contiguous_layer_descriptors(layers);
    const int64_t n_layers = static_cast<int64_t>(layers.size());

    if (!std::is_sorted(plan.sticky_layer_ids().begin(), plan.sticky_layer_ids().end())) {
        throw StickyPlanError("plan.sticky_layer_ids() is not sorted ascending");
    }

    std::set<int64_t> seen;
    uint64_t actual_bytes = 0;
    for (int64_t id : plan.sticky_layer_ids()) {
        if (id < 0 || id >= n_layers) {
            throw StickyPlanError("plan selects layer id " + std::to_string(id) + " outside [0, " +
                                  std::to_string(n_layers) + ")");
        }
        if (!seen.insert(id).second) {
            throw StickyPlanError("plan selects duplicate layer id " + std::to_string(id));
        }
        actual_bytes = checked_add(actual_bytes, find_layer(layers, id).resident_bytes);
    }

    if (actual_bytes != plan.planned_resident_bytes()) {
        throw StickyPlanError("plan's claimed planned_resident_bytes() (" +
                              std::to_string(plan.planned_resident_bytes()) +
                              ") does not equal the actual sum of selected layers' resident_bytes (" +
                              std::to_string(actual_bytes) + ")");
    }
    if (actual_bytes > plan.byte_budget()) {
        throw StickyPlanError("plan's resident bytes (" + std::to_string(actual_bytes) +
                              ") exceed byte_budget (" + std::to_string(plan.byte_budget()) + ")");
    }
}

StickyLayerPlan plan_first_k(const std::vector<LayerCostInfo>& layers, uint64_t byte_budget) {
    require_contiguous_layer_descriptors(layers);
    // Commit 2A fix: iterate layer id 0, 1, 2, ... explicitly and look each
    // descriptor up by id (find_layer), rather than iterating `layers` in
    // whatever order its caller happened to store it. The prior version
    // iterated the vector directly -- require_contiguous_layer_descriptors
    // only proves the SET of ids is complete and contiguous, not that the
    // vector's ELEMENT ORDER matches ascending id, so a caller-supplied
    // `layers` ordered e.g. {id 1, id 0} could silently make FirstK select
    // layer 1 before layer 0, breaking its documented "starts at layer 0"
    // guarantee while still passing validate_plan (a single selected id is
    // trivially "sorted"). This makes FirstK's result independent of the
    // input vector's iteration order, by construction.
    std::vector<int64_t> selected;
    uint64_t total = 0;
    for (int64_t id = 0; id < static_cast<int64_t>(layers.size()); ++id) {
        const uint64_t candidate_total = checked_add(total, find_layer(layers, id).resident_bytes);
        if (candidate_total > byte_budget) break;
        selected.push_back(id);
        total = candidate_total;
    }
    StickyLayerPlan plan("FirstKBaseline", byte_budget, std::move(selected), total);
    validate_plan(plan, layers);
    return plan;
}

StickyLayerPlan plan_explicit_set(const std::vector<LayerCostInfo>& layers, std::vector<int64_t> requested_ids,
                                  uint64_t byte_budget) {
    require_contiguous_layer_descriptors(layers);
    std::sort(requested_ids.begin(), requested_ids.end());
    uint64_t total = 0;
    for (int64_t id : requested_ids) {
        // find_layer throws StickyPlanError for an out-of-range id, giving a
        // clearer message than validate_plan's own range check would for a
        // request that names a nonexistent layer entirely; validate_plan
        // below still re-checks everything as the single source of truth.
        total = checked_add(total, find_layer(layers, id).resident_bytes);
    }
    StickyLayerPlan plan("ExplicitSet", byte_budget, std::move(requested_ids), total);
    validate_plan(plan, layers);
    return plan;
}

StickyLayerPlan plan_cost_per_byte(const std::vector<LayerCostInfo>& layers, uint64_t byte_budget) {
    require_contiguous_layer_descriptors(layers);
    struct Ranked {
        int64_t layer_id;
        double benefit_per_byte;
    };
    std::vector<Ranked> ranked;
    ranked.reserve(layers.size());
    for (const LayerCostInfo& l : layers) {
        if (!l.benefit_estimate.has_value()) {
            throw StickyPlanError("layer id " + std::to_string(l.layer_id) +
                                  " has no benefit_estimate -- MaterializationCostPerByte requires an explicit "
                                  "cost for every candidate layer and never treats unknown cost as zero");
        }
        const double benefit = *l.benefit_estimate;
        if (std::isnan(benefit)) {
            throw StickyPlanError("layer id " + std::to_string(l.layer_id) + " has a NaN benefit_estimate");
        }
        if (std::isinf(benefit)) {
            throw StickyPlanError("layer id " + std::to_string(l.layer_id) +
                                  " has an infinite benefit_estimate");
        }
        if (benefit < 0.0) {
            throw StickyPlanError("layer id " + std::to_string(l.layer_id) +
                                  " has a negative benefit_estimate (" + std::to_string(benefit) +
                                  ") -- negative benefit is not a supported value for this experiment");
        }
        if (l.resident_bytes == 0) {
            throw StickyPlanError("layer id " + std::to_string(l.layer_id) +
                                  " has resident_bytes == 0 -- benefit-per-byte is undefined");
        }
        ranked.push_back({l.layer_id, benefit / static_cast<double>(l.resident_bytes)});
    }

    // Deterministic ranking: std::stable_sort over an explicit std::vector,
    // comparator breaks ties by ascending layer_id. Never decided by
    // unordered_map/unordered_set iteration order.
    std::stable_sort(ranked.begin(), ranked.end(), [](const Ranked& a, const Ranked& b) {
        if (a.benefit_per_byte != b.benefit_per_byte) return a.benefit_per_byte > b.benefit_per_byte;
        return a.layer_id < b.layer_id;
    });

    std::vector<int64_t> selected;
    uint64_t total = 0;
    for (const Ranked& r : ranked) {
        const uint64_t bytes = find_layer(layers, r.layer_id).resident_bytes;
        const uint64_t candidate_total = checked_add(total, bytes);
        if (candidate_total > byte_budget) continue;  // skip -- greedy, not a knapsack solver; see header comment
        selected.push_back(r.layer_id);
        total = candidate_total;
    }
    std::sort(selected.begin(), selected.end());  // StickyLayerPlan's ids are stored in ascending order
    StickyLayerPlan plan("MaterializationCostPerByte", byte_budget, std::move(selected), total);
    validate_plan(plan, layers);
    return plan;
}

}  // namespace fringelab
