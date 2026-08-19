// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07 -- Budgeted Weight Residency. NEW code, not a modification to any
// frozen Phase 1/2/3/4 file. Reuses Phase 1's forward_with_layer_runner
// seam (the same seam Phase 3/4's StreamingModel itself uses) and Phase
// 3's public ModelSource/TensorMaterializer/ResidencyLedger contracts.
//
// Policy under test: "keep the first N transformer layers permanently
// resident (materialized once at construction, never released); stream
// the remaining layers per Phase-4's existing materialize-then-release
// pattern." This is the simplest of the deterministic policies the
// experiment charter allows ("keep first N layers"), chosen over "most
// recently used" or a fixed-selected-layer scheme to keep the first pass
// bounded and unambiguous to interpret.
#pragma once

#include <cstdint>
#include <vector>

#include "orcengine/forward.hpp"
#include "orcengine/model_source.hpp"
#include "orcengine/streaming.hpp"

namespace fringelab {

class ResidencyBudgetModel {
public:
    // n_resident_layers: how many of the FIRST transformer layers are
    // materialized once at construction and never released. Must be in
    // [0, config.n_layers]. 0 == fully streamed (matches Phase 4's own
    // baseline exactly). config.n_layers == fully resident (matches
    // Phase 2's original approach).
    ResidencyBudgetModel(orcengine::ModelSource source, orcengine::TensorMaterializer materializer,
                         int64_t n_resident_layers);

    orcengine::ForwardResult forward(const std::vector<int64_t>& token_ids);
    const orcengine::ModelConfig& config() const { return source_.config; }
    const orcengine::StreamingTelemetry& telemetry() const { return ledger_.telemetry(); }
    uint64_t resident_layer_weight_bytes() const { return resident_layer_bytes_; }

private:
    orcengine::LayerWeights materialize_layer(int64_t layer, uint64_t& resident_bytes, uint64_t& tensor_count);

    orcengine::ModelSource source_;
    orcengine::TensorMaterializer materializer_;
    int64_t n_resident_layers_;
    orcengine::ResidentView token_embedding_;
    orcengine::ResidentView final_norm_weight_;
    std::optional<orcengine::ResidentView> lm_head_;
    std::vector<orcengine::LayerWeights> resident_layers_;  // first n_resident_layers_, materialized once
    uint64_t resident_layer_bytes_ = 0;                     // bytes permanently held by resident_layers_
    orcengine::ResidencyLedger ledger_;
};

}  // namespace fringelab
