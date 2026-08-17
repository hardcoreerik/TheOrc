// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include <cstdint>
#include <functional>
#include <limits>
#include <optional>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <vector>

#include "orcengine/forward.hpp"
#include "orcengine/model_source.hpp"

namespace orcengine {

class StreamingError : public std::runtime_error {
public:
    explicit StreamingError(const std::string& message)
        : std::runtime_error("OrcEngine streaming error: " + message) {}
};

class ResidencyBudgetError : public StreamingError {
public:
    explicit ResidencyBudgetError(const std::string& message)
        : StreamingError("residency budget: " + message) {}
};

enum class EvidenceSemantics { Measured, Derived, Interpreted };
enum class ExecutionOperation { None, InputEmbedding, OutputProjection };

enum class ExecutionEventKind {
    ModelExecutionBegin,
    LayerBegin,
    TensorMaterializationBegin,
    TensorMaterialized,
    ResidentBytesChanged,
    LayerExecutionBegin,
    LayerExecutionEnd,
    TensorReleased,
    TensorRowRegionRequested,
    TensorRowRegionMaterializationBegin,
    TensorRowRegionMaterialized,
    TensorRowRegionReleased,
    LayerEnd,
    TokenScored,
    TokenSelected,
    ModelExecutionEnd,
};

struct ExecutionEvent {
    ExecutionEventKind kind = ExecutionEventKind::ModelExecutionBegin;
    EvidenceSemantics semantics = EvidenceSemantics::Measured;
    std::optional<TensorIdentity> tensor;
    int64_t layer = -1;
    int64_t token = -1;
    uint64_t resident_bytes = 0;
    uint64_t tensor_bytes = 0;
    std::string backing_identity;
    uint64_t row_begin = 0;
    uint64_t row_count = 0;
    ExecutionOperation operation = ExecutionOperation::None;
    double milliseconds = 0.0;
    uint64_t backing_bytes_read = 0;
};

using ExecutionObserver = std::function<void(const ExecutionEvent&)>;

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
    uint64_t observer_event_count = 0;
    uint64_t observer_failure_count = 0;
    uint64_t row_region_materialization_count = 0;
    uint64_t embedding_row_region_count = 0;
    uint64_t output_row_region_count = 0;
    uint64_t embedding_backing_bytes_read = 0;
    uint64_t output_backing_bytes_read = 0;
    double embedding_milliseconds = 0.0;
    double output_projection_milliseconds = 0.0;
    int64_t current_layer = -1;
    std::vector<LayerTiming> layer_timings;
};

class ResidencyLedger {
public:
    explicit ResidencyLedger(uint64_t budget_bytes = std::numeric_limits<uint64_t>::max())
        : budget_bytes_(budget_bytes) {}
    const StreamingTelemetry& telemetry() const { return telemetry_; }
    uint64_t budget_bytes() const { return budget_bytes_; }
    void require_can_materialize(uint64_t resident_bytes) const;
    void materialized(const std::string& extent_key, uint64_t resident_bytes,
                      uint64_t backing_bytes);
    void released(uint64_t resident_bytes, uint64_t tensor_count);
    void enter_layer(int64_t layer);
    void leave_layer();
    void sample_process_working_set();
    void record_layer_timing(LayerTiming timing);
    void observer_succeeded();
    void observer_failed();
    void record_region(ExecutionOperation operation, uint64_t backing_bytes);
    void record_embedding_time(double milliseconds);
    void record_output_time(double milliseconds);

private:
    StreamingTelemetry telemetry_;
    std::unordered_set<std::string> seen_extents_;
    uint64_t active_layers_ = 0;
    uint64_t budget_bytes_ = std::numeric_limits<uint64_t>::max();
};

struct StreamingOptions {
    bool reverse_layer_materialization_order = false;
    int64_t fail_after_layer_release = -1;
};

struct StreamingConfig {
    uint64_t residency_budget_bytes = std::numeric_limits<uint64_t>::max();
    ExecutionObserver observer;
    bool virtualize_bookends = false;
    TensorRowRegionMaterializer row_region_materializer;
    uint64_t output_chunk_rows = 1024;
};

uint64_t full_resident_bytes(const ModelSource& source);
void require_full_resident_budget(const ModelSource& source, uint64_t budget_bytes);
std::vector<TensorRowRegion> build_complete_row_partition(uint64_t rows,
                                                          uint64_t chunk_rows);
void validate_complete_row_partition(uint64_t rows,
                                     const std::vector<TensorRowRegion>& regions);

class StreamingModel {
public:
    StreamingModel(ModelSource source, TensorMaterializer materializer,
                   StreamingConfig config = {});

    ForwardResult forward(const std::vector<int64_t>& token_ids,
                          const StreamingOptions& options = {});
    const ModelConfig& config() const { return source_.config; }
    const StreamingTelemetry& telemetry() const { return ledger_.telemetry(); }

private:
    ResidentView materialize(const SourceTensor& tensor);
    ResidentView materialize_row_region(const SourceTensor& tensor,
                                        const TensorRowRegion& region,
                                        ExecutionOperation operation);
    void release_row_region(const SourceTensor& tensor,
                            const TensorRowRegion& region,
                            ExecutionOperation operation, uint64_t resident_bytes);
    std::vector<float> virtualized_embedding(const std::vector<int64_t>& token_ids);
    std::vector<float> virtualized_output(const std::vector<float>& final_normed,
                                          int64_t sequence_length);
    LayerWeights materialize_layer(int64_t layer, bool reverse_order,
                                   uint64_t& resident_bytes,
                                   uint64_t& tensor_count);
    void emit(ExecutionEvent event);

    ModelSource source_;
    TensorMaterializer materializer_;
    TensorRowRegionMaterializer row_region_materializer_;
    ExecutionObserver observer_;
    bool virtualize_bookends_ = false;
    uint64_t output_chunk_rows_ = 0;
    std::optional<ResidentView> token_embedding_;
    std::optional<ResidentView> lm_head_;
    ResidentView final_norm_weight_;
    ResidencyLedger ledger_;
};

}  // namespace orcengine
