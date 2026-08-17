// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_set>
#include <vector>

#include "orcengine/forward.hpp"
#include "orcengine/gguf.hpp"

namespace orcengine {

struct LayerTiming {
    int64_t layer = -1;
    uint64_t resident_bytes = 0;
    double materialize_milliseconds = 0.0;
    double execute_milliseconds = 0.0;
};

struct StreamingTelemetry {
    uint64_t current_resident_weight_bytes = 0;
    uint64_t peak_resident_weight_bytes = 0;
    uint64_t cumulative_materialized_bytes = 0;
    uint64_t materialization_count = 0;
    uint64_t release_count = 0;
    uint64_t backing_bytes_read = 0;
    uint64_t repeated_backing_bytes_read = 0;
    uint64_t read_count = 0;
    uint64_t peak_process_working_set_bytes = 0;
    uint64_t peak_active_layers = 0;
    int64_t current_layer = -1;
    std::vector<LayerTiming> layer_timings;
};

class ResidencyLedger {
public:
    const StreamingTelemetry& telemetry() const { return telemetry_; }
    void materialized(const std::string& extent_key, uint64_t resident_bytes,
                      uint64_t backing_bytes);
    void released(uint64_t resident_bytes, uint64_t tensor_count);
    void enter_layer(int64_t layer);
    void leave_layer();
    void sample_process_working_set();
    void record_layer_timing(LayerTiming timing);

private:
    StreamingTelemetry telemetry_;
    std::unordered_set<std::string> seen_extents_;
    uint64_t active_layers_ = 0;
};

struct StreamingOptions {
    bool reverse_layer_materialization_order = false;
    int64_t fail_after_layer_release = -1;
};

class StreamingModel {
public:
    explicit StreamingModel(ModelArtifactManifest manifest);

    ForwardResult forward(const std::vector<int64_t>& token_ids,
                          const StreamingOptions& options = {});
    const ModelConfig& config() const { return manifest_.config; }
    const StreamingTelemetry& telemetry() const { return ledger_.telemetry(); }

private:
    ResidentView materialize(const MappedGgufTensor& tensor);
    LayerWeights materialize_layer(int64_t layer, bool reverse_order,
                                   uint64_t& resident_bytes,
                                   uint64_t& tensor_count);

    ModelArtifactManifest manifest_;
    ResidentView token_embedding_;
    std::optional<ResidentView> lm_head_;
    ResidentView final_norm_weight_;
    ResidencyLedger ledger_;
};

}  // namespace orcengine
