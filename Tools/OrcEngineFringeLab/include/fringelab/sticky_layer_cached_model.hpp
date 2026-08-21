// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B Commit 2 -- Multi-Step Cached-Decode Integration. Extends
// StickyLayerModel's single-call sticky/cold split (sticky_layer_model.hpp)
// to Phase 5A's KV-cached incremental decode seam
// (Tools/OrcEnginePhase5A/include/orcengine/forward_cached.hpp,
// execute_cached_transformer_layer / CachedStepResult), across an arbitrary
// number of step() calls against one persistent ContiguousAttentionKVStore.
// Reuses that seam UNMODIFIED, per FL-07B's charter -- no frozen Phase
// 1/2/3/4/5A file is touched by this class.
//
// Residency invariant across a multi-step decode sequence: the plan's K
// sticky layers are materialized exactly once (during construction) and
// stay resident for every step() call over this object's lifetime; each
// cold layer is materialized fresh and released at the end of every single
// step() call in which it participates -- so a cold layer visited across N
// steps is materialized N separate times, once per step, never cached
// between steps. At most one additional cold layer's weights are resident
// at once (ResidencyLedger::enter_layer's more-than-one-resident guard).
//
// EXPERIMENTAL RESEARCH ONLY. Not a stable ABI, not a production model or
// ExecutionPlanner. See .orc/fringelab/2026-08-21-fl07b-sticky-planner/.
#pragma once

#include <cstdint>
#include <functional>
#include <optional>
#include <vector>

#include "fringelab/sticky_layer_plan.hpp"
#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/model_source.hpp"
#include "orcengine/streaming.hpp"

namespace fringelab {

// Test-only fault injection, mirroring StickyLayerModel's identical hooks
// (sticky_layer_model.hpp) -- fires for a COLD layer only, once per step()
// call in which that layer participates (never for a sticky layer, which is
// never materialized inside step()).
using CachedColdLayerFaultInjector = std::function<void(int64_t layer, int64_t step_index)>;
using CachedColdLayerExecutionFaultInjector = std::function<void(int64_t layer, int64_t step_index)>;

class StickyLayerCachedModel {
public:
    // Re-derives the ACTUAL layer descriptors from `source` via
    // layer_costs_from_source() (sticky_layer_model.hpp) and validates
    // `plan` against them BEFORE materializing any bookend or sticky-layer
    // tensor -- identical validation discipline to StickyLayerModel's
    // constructor (Commit 1A). `plan` is never trusted blindly.
    StickyLayerCachedModel(orcengine::ModelSource source, orcengine::TensorMaterializer materializer,
                           StickyLayerPlan plan);

    // SAFE, NORMAL PRODUCTION ENTRY POINT -- same contract as Phase 5A's
    // forward_cached_step / VirtualizedCachedModel::step: REQUIRES
    // start_position == cache.current_length(), throws KVCacheError before
    // any mutation if it does not. Commits cache.current_length() to
    // start_position+new_len on the success path ONLY. Callers must NOT
    // call cache.set_current_length() themselves afterward.
    orcengine::CachedStepResult step(orcengine::ContiguousAttentionKVStore& cache,
                                     const std::vector<int64_t>& new_token_ids, int64_t start_position);

    // UNSAFE LOW-LEVEL / TEST-ONLY SEAM -- identical to step() except it
    // does not require start_position == cache.current_length(). Reserved
    // for fault-injection / rollback-verification tests that deliberately
    // violate the position invariant step() enforces.
    orcengine::CachedStepResult step_unsafe_explicit_position(orcengine::ContiguousAttentionKVStore& cache,
                                                               const std::vector<int64_t>& new_token_ids,
                                                               int64_t start_position);

    const orcengine::ModelConfig& config() const { return source_.config; }
    const orcengine::StreamingTelemetry& telemetry() const { return ledger_.telemetry(); }
    const StickyLayerPlan& plan() const { return plan_; }
    uint64_t sticky_resident_bytes() const { return sticky_layer_bytes_; }

    // Test-only fault injection. No-op unless set. `step_index` is 0 for the
    // first step() call this object serves, incrementing thereafter --
    // lets a test target e.g. "fail only on the 3rd step's cold layer".
    CachedColdLayerFaultInjector fault_before_cold_materialize;
    CachedColdLayerExecutionFaultInjector fault_before_cold_execute;

private:
    orcengine::LayerWeights materialize_layer(int64_t layer, uint64_t& resident_bytes, uint64_t& tensor_count);
    orcengine::CachedStepResult run_step(orcengine::ContiguousAttentionKVStore& cache,
                                         const std::vector<int64_t>& new_token_ids, int64_t start_position);

    orcengine::ModelSource source_;
    orcengine::TensorMaterializer materializer_;
    StickyLayerPlan plan_;
    orcengine::ResidentView token_embedding_;
    orcengine::ResidentView final_norm_weight_;
    std::optional<orcengine::ResidentView> lm_head_;
    std::vector<orcengine::LayerWeights> sticky_layers_;
    uint64_t sticky_layer_bytes_ = 0;
    int64_t next_step_index_ = 0;
    orcengine::ResidencyLedger ledger_;
};

}  // namespace fringelab
