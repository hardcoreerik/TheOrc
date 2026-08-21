// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B -- extends FL-07's ResidencyBudgetModel ("first N layers resident")
// to an arbitrary, planner-selected StickyLayerPlan. Reuses the SAME public
// seams FL-07 used: Phase 1's forward_with_layer_runner and Phase 3's
// ModelSource/TensorMaterializer/ResidencyLedger contracts. Does not modify
// any frozen Phase 1/2/3/4 file.
//
// Residency invariant under test:
//   Any number of planner-selected sticky layers may remain resident for the
//   execution-plan lifetime, while no more than one additional COLD
//   transformer layer is transiently materialized at once. With K sticky
//   layers, K+1 transformer layers may coexist while a cold layer executes
//   -- this class does NOT claim "only one layer is resident" globally.
//
// EXPERIMENTAL RESEARCH ONLY. Not a stable ABI, not a production model
// class. Single-call forward() only (Phase 1's seam) -- multi-step cached
// decode is a separate, later FL-07B addition consuming Phase 5A's own
// public per-layer seam, not this class.
#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <vector>

#include "fringelab/sticky_layer_plan.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/model_source.hpp"
#include "orcengine/streaming.hpp"

namespace fringelab {

// Optional fault-injection hook for tests only: called immediately before a
// COLD layer's materialize_layer() would run. If it throws, that exception
// propagates exactly as a real materialization failure would, exercising
// the same ledger-cleanup path. Left empty (default) in normal use.
using ColdLayerFaultInjector = std::function<void(int64_t layer)>;

// Optional fault-injection hook for tests only: called immediately before a
// COLD layer's weights are handed to the caller's consumer, simulating an
// execution-time failure (e.g. a NaN/exception during the transformer math)
// after materialization already succeeded.
using ColdLayerExecutionFaultInjector = std::function<void(int64_t layer)>;

class StickyLayerModel {
public:
    // `plan` must already be validate_plan()-clean against a LayerCostInfo
    // description of `source`'s layers -- this constructor re-validates
    // plan.sticky_layer_ids() against source_.config.n_layers itself (using
    // the actual per-layer resident byte sizes discovered from `source`, not
    // trusting the plan's own claimed bytes blindly) before materializing
    // anything, so a plan built against stale layer-size assumptions still
    // fails closed here rather than silently misbehaving.
    StickyLayerModel(orcengine::ModelSource source, orcengine::TensorMaterializer materializer, StickyLayerPlan plan);

    orcengine::ForwardResult forward(const std::vector<int64_t>& token_ids);

    const orcengine::ModelConfig& config() const { return source_.config; }
    const orcengine::StreamingTelemetry& telemetry() const { return ledger_.telemetry(); }
    const StickyLayerPlan& plan() const { return plan_; }
    uint64_t sticky_resident_bytes() const { return sticky_layer_bytes_; }

    // Test-only fault injection (see the two typedefs above). No-op unless set.
    ColdLayerFaultInjector fault_before_cold_materialize;
    ColdLayerExecutionFaultInjector fault_before_cold_execute;

private:
    orcengine::LayerWeights materialize_layer(int64_t layer, uint64_t& resident_bytes, uint64_t& tensor_count);

    orcengine::ModelSource source_;
    orcengine::TensorMaterializer materializer_;
    StickyLayerPlan plan_;
    orcengine::ResidentView token_embedding_;
    orcengine::ResidentView final_norm_weight_;
    std::optional<orcengine::ResidentView> lm_head_;
    // Keyed by position in plan_.sticky_layer_ids() (ascending, immutable) --
    // NOT an unordered container; lookup during forward() is a binary search
    // via StickyLayerPlan::is_sticky() plus an index computed the same way.
    std::vector<orcengine::LayerWeights> sticky_layers_;
    uint64_t sticky_layer_bytes_ = 0;
    orcengine::ResidencyLedger ledger_;
};

}  // namespace fringelab
