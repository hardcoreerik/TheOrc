// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/streaming.hpp"

#include <algorithm>
#include <chrono>
#include <iterator>
#include <limits>
#include <stdexcept>

#include "orcengine/ops.hpp"
#include "orcengine/validation.hpp"

#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#endif

namespace orcengine {
namespace {

uint64_t checked_add(uint64_t left, uint64_t right, const char* what) {
    if (right > std::numeric_limits<uint64_t>::max() - left) {
        throw std::overflow_error(std::string("streaming telemetry overflow: ") + what);
    }
    return left + right;
}

uint64_t checked_mul(uint64_t left, uint64_t right, const char* what) {
    if (left != 0 && right > std::numeric_limits<uint64_t>::max() / left) {
        throw std::overflow_error(std::string("streaming telemetry overflow: ") + what);
    }
    return left * right;
}

const SourceTensor& require_tensor(const ModelSource& source,
                                   TensorRole role, int64_t layer = -1) {
    const auto matches = [&](const SourceTensor& tensor) {
        return tensor.identity.role == role && tensor.identity.layer == layer;
    };
    const auto it = std::find_if(source.tensors.begin(), source.tensors.end(),
        matches);
    if (it == source.tensors.end()) {
        throw StreamingError("model source is missing required semantic tensor");
    }
    if (std::find_if(std::next(it), source.tensors.end(), matches) !=
        source.tensors.end()) {
        throw StreamingError("model source has duplicate semantic tensor");
    }
    return *it;
}

size_t tensor_count(const ModelSource& source, TensorRole role, int64_t layer = -1) {
    return static_cast<size_t>(std::count_if(
        source.tensors.begin(), source.tensors.end(),
        [&](const SourceTensor& tensor) {
            return tensor.identity.role == role && tensor.identity.layer == layer;
        }));
}

}  // namespace

void ResidencyLedger::require_can_materialize(uint64_t resident) const {
    if (resident > budget_bytes_ ||
        telemetry_.current_resident_weight_bytes > budget_bytes_ - resident) {
        throw ResidencyBudgetError(
            "materialization would require " +
            std::to_string(checked_add(telemetry_.current_resident_weight_bytes,
                                       resident, "budget request")) +
            " bytes with limit " + std::to_string(budget_bytes_));
    }
}

void ResidencyLedger::materialized(const std::string& key, uint64_t resident,
                                   uint64_t backing) {
    require_can_materialize(resident);
    const uint64_t current = checked_add(telemetry_.current_resident_weight_bytes,
                                         resident, "current resident bytes");
    const uint64_t cumulative = checked_add(telemetry_.cumulative_materialized_bytes,
                                            resident, "cumulative materialized bytes");
    const uint64_t backing_total = checked_add(telemetry_.backing_bytes_read,
                                               backing, "backing bytes read");
    const uint64_t count = checked_add(telemetry_.materialization_count, 1,
                                       "materialization count");
    const uint64_t reads = checked_add(telemetry_.read_count, 1, "read count");
    uint64_t repeated = telemetry_.repeated_backing_bytes_read;
    if (seen_extents_.contains(key)) {
        repeated = checked_add(repeated, backing, "repeated backing bytes");
    }
    telemetry_.current_resident_weight_bytes = current;
    telemetry_.peak_resident_weight_bytes = std::max(telemetry_.peak_resident_weight_bytes, current);
    telemetry_.cumulative_materialized_bytes = cumulative;
    telemetry_.backing_bytes_read = backing_total;
    telemetry_.materialization_count = count;
    telemetry_.read_count = reads;
    telemetry_.repeated_backing_bytes_read = repeated;
    seen_extents_.insert(key);
    sample_process_working_set();
}

void ResidencyLedger::released(uint64_t resident, uint64_t tensor_count) {
    if (resident > telemetry_.current_resident_weight_bytes ||
        tensor_count > telemetry_.materialization_count - telemetry_.release_count) {
        throw std::logic_error("streaming telemetry duplicate or oversized release");
    }
    telemetry_.current_resident_weight_bytes -= resident;
    telemetry_.release_count = checked_add(telemetry_.release_count, tensor_count,
                                           "release count");
}

void ResidencyLedger::enter_layer(int64_t layer) {
    if (active_layers_ != 0) throw std::logic_error("more than one transformer layer is resident");
    ++active_layers_;
    telemetry_.peak_active_layers = std::max(telemetry_.peak_active_layers, active_layers_);
    telemetry_.current_layer = layer;
}

void ResidencyLedger::leave_layer() {
    if (active_layers_ != 1) throw std::logic_error("layer release without one active layer");
    --active_layers_;
    telemetry_.current_layer = -1;
}

void ResidencyLedger::sample_process_working_set() {
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS counters{};
    if (GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof(counters))) {
        telemetry_.peak_process_working_set_bytes = std::max(
            telemetry_.peak_process_working_set_bytes,
            static_cast<uint64_t>(counters.WorkingSetSize));
    }
#endif
}

void ResidencyLedger::record_layer_timing(LayerTiming timing) {
    telemetry_.layer_timings.push_back(std::move(timing));
}

void ResidencyLedger::observer_succeeded() {
    telemetry_.observer_event_count = checked_add(
        telemetry_.observer_event_count, 1, "observer event count");
}

void ResidencyLedger::observer_failed() {
    telemetry_.observer_failure_count = checked_add(
        telemetry_.observer_failure_count, 1, "observer failure count");
}

void ResidencyLedger::record_region(ExecutionOperation operation, uint64_t backing_bytes) {
    telemetry_.row_region_materialization_count = checked_add(
        telemetry_.row_region_materialization_count, 1,
        "row region materialization count");
    if (operation == ExecutionOperation::InputEmbedding) {
        telemetry_.embedding_row_region_count = checked_add(
            telemetry_.embedding_row_region_count, 1,
            "embedding row region count");
        telemetry_.embedding_backing_bytes_read = checked_add(
            telemetry_.embedding_backing_bytes_read, backing_bytes,
            "embedding backing bytes");
    } else if (operation == ExecutionOperation::OutputProjection) {
        telemetry_.output_row_region_count = checked_add(
            telemetry_.output_row_region_count, 1,
            "output row region count");
        telemetry_.output_backing_bytes_read = checked_add(
            telemetry_.output_backing_bytes_read, backing_bytes,
            "output backing bytes");
    }
}

void ResidencyLedger::record_embedding_time(double milliseconds) {
    telemetry_.embedding_milliseconds += milliseconds;
}

void ResidencyLedger::record_output_time(double milliseconds) {
    telemetry_.output_projection_milliseconds += milliseconds;
}

std::vector<TensorRowRegion> build_complete_row_partition(uint64_t rows,
                                                          uint64_t chunk_rows) {
    if (rows == 0 || chunk_rows == 0) {
        throw StreamingError("row partition requires non-zero rows and chunk size");
    }
    std::vector<TensorRowRegion> regions;
    for (uint64_t begin = 0; begin < rows;) {
        const uint64_t count = std::min(chunk_rows, rows - begin);
        regions.push_back({begin, count});
        begin = checked_add(begin, count, "row partition progress");
    }
    return regions;
}

void validate_complete_row_partition(uint64_t rows,
                                     const std::vector<TensorRowRegion>& regions) {
    if (rows == 0 || regions.empty()) {
        throw StreamingError("vocabulary row partition is empty");
    }
    uint64_t expected_begin = 0;
    for (const TensorRowRegion& region : regions) {
        if (region.row_count == 0 || region.row_begin != expected_begin) {
            throw StreamingError("vocabulary row partition is skipped, duplicated, overlapping, or reordered");
        }
        expected_begin = checked_add(region.row_begin, region.row_count,
                                     "vocabulary row partition end");
        if (expected_begin > rows) {
            throw StreamingError("vocabulary row partition exceeds tensor rows");
        }
    }
    if (expected_begin != rows) {
        throw StreamingError("vocabulary row partition is incomplete");
    }
}

uint64_t full_resident_bytes(const ModelSource& source) {
    uint64_t total = 0;
    for (const SourceTensor& tensor : source.tensors) {
        total = checked_add(total, tensor.resident_bytes, "full resident bytes");
    }
    return total;
}

void require_full_resident_budget(const ModelSource& source, uint64_t budget_bytes) {
    const uint64_t required = full_resident_bytes(source);
    if (required > budget_bytes) {
        throw ResidencyBudgetError("full resident model requires " +
                                   std::to_string(required) + " bytes with limit " +
                                   std::to_string(budget_bytes));
    }
}

StreamingModel::StreamingModel(ModelSource source, TensorMaterializer materializer,
                               StreamingConfig config)
    : source_(std::move(source)), materializer_(std::move(materializer)),
      row_region_materializer_(std::move(config.row_region_materializer)),
      observer_(std::move(config.observer)),
      virtualize_bookends_(config.virtualize_bookends),
      output_chunk_rows_(config.output_chunk_rows),
      ledger_(config.residency_budget_bytes) {
    validate_model_config(source_.config);
    if (!materializer_) throw StreamingError("tensor materializer is required");
    (void)require_tensor(source_, TensorRole::TokenEmbedding);
    (void)require_tensor(source_, TensorRole::FinalNorm);
    const size_t output_count = tensor_count(source_, TensorRole::OutputHead);
    if ((source_.tied_embeddings && output_count != 0) ||
        (!source_.tied_embeddings && output_count != 1)) {
        throw StreamingError("model source has contradictory tied/output-head semantics");
    }
    if (virtualize_bookends_) {
        if (!row_region_materializer_ || output_chunk_rows_ == 0) {
            throw StreamingError("bookend virtualization requires a row-region materializer and chunk size");
        }
        const SourceTensor& embedding = require_tensor(source_, TensorRole::TokenEmbedding);
        if (embedding.logical.shape().dims() !=
            std::vector<int64_t>{source_.config.vocab, source_.config.hidden}) {
            throw StreamingError("virtualized token embedding has wrong logical shape");
        }
        if (!source_.tied_embeddings) {
            const SourceTensor& output = require_tensor(source_, TensorRole::OutputHead);
            if (output.logical.shape().dims() !=
                std::vector<int64_t>{source_.config.vocab, source_.config.hidden}) {
                throw StreamingError("virtualized output head has wrong logical shape");
            }
        }
        final_norm_weight_ = materialize(require_tensor(source_, TensorRole::FinalNorm));
        validate_final_norm_weight(source_.config, final_norm_weight_);
    } else {
        token_embedding_ = materialize(require_tensor(source_, TensorRole::TokenEmbedding));
        final_norm_weight_ = materialize(require_tensor(source_, TensorRole::FinalNorm));
        if (!source_.tied_embeddings) {
            lm_head_ = materialize(require_tensor(source_, TensorRole::OutputHead));
        }
        validate_bookend_weights(source_.config, source_.tied_embeddings, *token_embedding_,
                                 lm_head_ ? &*lm_head_ : nullptr, final_norm_weight_);
    }
}

void StreamingModel::emit(ExecutionEvent event) {
    if (!observer_) return;
    try {
        observer_(event);
        ledger_.observer_succeeded();
    } catch (...) {
        ledger_.observer_failed();
        observer_ = {};
    }
}

ResidentView StreamingModel::materialize(const SourceTensor& tensor) {
    ledger_.require_can_materialize(tensor.resident_bytes);
    emit({ExecutionEventKind::TensorMaterializationBegin, EvidenceSemantics::Measured,
          tensor.identity, tensor.identity.layer, -1,
          ledger_.telemetry().current_resident_weight_bytes, tensor.resident_bytes,
          tensor.backing_identity});
    ResidentView view = materializer_(tensor.logical, tensor.backing);
    if (view.raw().size() > std::numeric_limits<uint64_t>::max() / sizeof(float)) {
        throw StreamingError("materializer resident byte count overflow");
    }
    const uint64_t actual_bytes = static_cast<uint64_t>(view.raw().size()) * sizeof(float);
    if (actual_bytes != tensor.resident_bytes) {
        throw StreamingError("materializer returned " + std::to_string(actual_bytes) +
                             " bytes for declared " + std::to_string(tensor.resident_bytes));
    }
    ledger_.materialized(tensor.backing_identity, actual_bytes,
                         static_cast<uint64_t>(tensor.backing.byte_length()));
    emit({ExecutionEventKind::TensorMaterialized, EvidenceSemantics::Measured,
          tensor.identity, tensor.identity.layer, -1,
          ledger_.telemetry().current_resident_weight_bytes, actual_bytes,
          tensor.backing_identity});
    emit({ExecutionEventKind::ResidentBytesChanged, EvidenceSemantics::Measured,
          tensor.identity, tensor.identity.layer, -1,
          ledger_.telemetry().current_resident_weight_bytes, actual_bytes,
          tensor.backing_identity});
    return view;
}

ResidentView StreamingModel::materialize_row_region(
    const SourceTensor& tensor, const TensorRowRegion& region,
    ExecutionOperation operation) {
    if (tensor.logical.shape().ndim() != 2 || region.row_count == 0) {
        throw StreamingError("tensor row region requires non-zero rows from a rank-2 tensor");
    }
    const uint64_t rows = static_cast<uint64_t>(tensor.logical.shape().dim(0));
    const uint64_t columns = static_cast<uint64_t>(tensor.logical.shape().dim(1));
    const uint64_t row_end = checked_add(region.row_begin, region.row_count,
                                         "tensor row region end");
    if (region.row_begin >= rows || row_end > rows) {
        throw StreamingError("tensor row region is outside logical tensor rows");
    }
    const uint64_t elements = checked_mul(region.row_count, columns,
                                          "tensor row region elements");
    const uint64_t resident_bytes = checked_mul(elements, sizeof(float),
                                                "tensor row region resident bytes");
    if (elements > static_cast<uint64_t>(std::numeric_limits<size_t>::max())) {
        throw StreamingError("tensor row region exceeds addressable memory");
    }
    ledger_.require_can_materialize(resident_bytes);

    ExecutionEvent requested{ExecutionEventKind::TensorRowRegionRequested};
    requested.tensor = tensor.identity;
    requested.resident_bytes = ledger_.telemetry().current_resident_weight_bytes;
    requested.tensor_bytes = resident_bytes;
    requested.backing_identity = tensor.backing_identity;
    requested.row_begin = region.row_begin;
    requested.row_count = region.row_count;
    requested.operation = operation;
    emit(requested);
    requested.kind = ExecutionEventKind::TensorRowRegionMaterializationBegin;
    emit(requested);

    const auto started = std::chrono::steady_clock::now();
    MaterializedRegion materialized = row_region_materializer_(
        tensor.logical, tensor.backing, region);
    const double milliseconds = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count();
    const std::vector<int64_t> expected_shape = {
        static_cast<int64_t>(region.row_count), static_cast<int64_t>(columns)};
    if (materialized.view.shape().dims() != expected_shape ||
        materialized.view.raw().size() != static_cast<size_t>(elements)) {
        throw StreamingError("row-region materializer returned wrong row count, shape, or resident bytes");
    }
    if (materialized.backing_bytes_read == 0 || tensor.backing.byte_length() <= 0 ||
        materialized.backing_bytes_read >
            static_cast<uint64_t>(tensor.backing.byte_length())) {
        throw StreamingError("row-region materializer reported invalid backing bytes read");
    }
    const std::string key = tensor.backing_identity + ":rows:" +
        std::to_string(region.row_begin) + ":" + std::to_string(region.row_count);
    ledger_.materialized(key, resident_bytes, materialized.backing_bytes_read);
    ledger_.record_region(operation, materialized.backing_bytes_read);

    ExecutionEvent event{ExecutionEventKind::TensorRowRegionMaterialized};
    event.tensor = tensor.identity;
    event.resident_bytes = ledger_.telemetry().current_resident_weight_bytes;
    event.tensor_bytes = resident_bytes;
    event.backing_bytes_read = materialized.backing_bytes_read;
    event.backing_identity = tensor.backing_identity;
    event.row_begin = region.row_begin;
    event.row_count = region.row_count;
    event.operation = operation;
    event.milliseconds = milliseconds;
    emit(event);
    emit({ExecutionEventKind::ResidentBytesChanged, EvidenceSemantics::Measured,
          tensor.identity, tensor.identity.layer, -1,
          ledger_.telemetry().current_resident_weight_bytes, resident_bytes,
          tensor.backing_identity});
    return std::move(materialized.view);
}

void StreamingModel::release_row_region(const SourceTensor& tensor,
                                        const TensorRowRegion& region,
                                        ExecutionOperation operation,
                                        uint64_t resident_bytes) {
    ledger_.released(resident_bytes, 1);
    ExecutionEvent event{ExecutionEventKind::TensorRowRegionReleased};
    event.tensor = tensor.identity;
    event.resident_bytes = ledger_.telemetry().current_resident_weight_bytes;
    event.tensor_bytes = resident_bytes;
    event.backing_identity = tensor.backing_identity;
    event.row_begin = region.row_begin;
    event.row_count = region.row_count;
    event.operation = operation;
    emit(event);
    emit({ExecutionEventKind::ResidentBytesChanged, EvidenceSemantics::Measured,
          tensor.identity, tensor.identity.layer, -1,
          ledger_.telemetry().current_resident_weight_bytes, resident_bytes,
          tensor.backing_identity});
}

std::vector<float> StreamingModel::virtualized_embedding(
    const std::vector<int64_t>& token_ids) {
    const auto started = std::chrono::steady_clock::now();
    const SourceTensor& tensor = require_tensor(source_, TensorRole::TokenEmbedding);
    const uint64_t row_bytes = checked_mul(static_cast<uint64_t>(source_.config.hidden),
                                           sizeof(float), "embedding row bytes");
    std::vector<float> output(token_ids.size() * static_cast<size_t>(source_.config.hidden));
    std::unordered_set<int64_t> processed;
    for (int64_t token : token_ids) {
        if (!processed.insert(token).second) continue;
        const TensorRowRegion region{static_cast<uint64_t>(token), 1};
        ResidentView row = materialize_row_region(
            tensor, region, ExecutionOperation::InputEmbedding);
        try {
            for (size_t position = 0; position < token_ids.size(); ++position) {
                if (token_ids[position] != token) continue;
                std::copy(row.raw().begin(), row.raw().end(),
                          output.begin() + position * static_cast<size_t>(source_.config.hidden));
            }
        } catch (...) {
            release_row_region(tensor, region, ExecutionOperation::InputEmbedding,
                               row_bytes);
            throw;
        }
        release_row_region(tensor, region, ExecutionOperation::InputEmbedding,
                           row_bytes);
    }
    ledger_.record_embedding_time(std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count());
    return output;
}

std::vector<float> StreamingModel::virtualized_output(
    const std::vector<float>& final_normed, int64_t sequence_length) {
    const auto started = std::chrono::steady_clock::now();
    const SourceTensor& tensor = source_.tied_embeddings
        ? require_tensor(source_, TensorRole::TokenEmbedding)
        : require_tensor(source_, TensorRole::OutputHead);
    const std::vector<TensorRowRegion> regions = build_complete_row_partition(
        static_cast<uint64_t>(source_.config.vocab), output_chunk_rows_);
    validate_complete_row_partition(static_cast<uint64_t>(source_.config.vocab), regions);
    std::vector<float> logits(static_cast<size_t>(sequence_length * source_.config.vocab));
    for (const TensorRowRegion& region : regions) {
        ResidentView rows = materialize_row_region(
            tensor, region, ExecutionOperation::OutputProjection);
        const uint64_t resident_bytes = checked_mul(
            checked_mul(region.row_count, static_cast<uint64_t>(source_.config.hidden),
                        "output row-region elements"),
            sizeof(float), "output region resident bytes");
        try {
            std::vector<float> chunk = ops::linear_no_bias(
                final_normed, sequence_length, source_.config.hidden, rows.raw(),
                static_cast<int64_t>(region.row_count));
            for (int64_t position = 0; position < sequence_length; ++position) {
                std::copy(
                    chunk.begin() + static_cast<size_t>(position) * region.row_count,
                    chunk.begin() + static_cast<size_t>(position + 1) * region.row_count,
                    logits.begin() + static_cast<size_t>(position * source_.config.vocab) +
                        region.row_begin);
            }
        } catch (...) {
            release_row_region(tensor, region,
                               ExecutionOperation::OutputProjection, resident_bytes);
            throw;
        }
        release_row_region(tensor, region, ExecutionOperation::OutputProjection,
                           resident_bytes);
    }
    ledger_.record_output_time(std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - started).count());
    return logits;
}

LayerWeights StreamingModel::materialize_layer(int64_t layer, bool reverse_order,
                                               uint64_t& bytes, uint64_t& count) {
    std::vector<const SourceTensor*> tensors;
    for (const auto& tensor : source_.tensors) {
        if (tensor.identity.layer == layer) tensors.push_back(&tensor);
    }
    if (tensors.size() != 9) throw StreamingError("layer does not contain exactly nine tensors");
    if (reverse_order) std::reverse(tensors.begin(), tensors.end());

    LayerWeights out;
    try {
        for (const SourceTensor* tensor : tensors) {
            ResidentView view = materialize(*tensor);
            bytes = checked_add(bytes, tensor->resident_bytes, "layer resident bytes");
            count = checked_add(count, 1, "layer tensor count");
            switch (tensor->identity.role) {
                case TensorRole::AttentionNorm: out.attn_norm_weight = std::move(view); break;
                case TensorRole::AttentionQuery: out.w_q = std::move(view); break;
                case TensorRole::AttentionKey: out.w_k = std::move(view); break;
                case TensorRole::AttentionValue: out.w_v = std::move(view); break;
                case TensorRole::AttentionOutput: out.w_o = std::move(view); break;
                case TensorRole::FfnNorm: out.ffn_norm_weight = std::move(view); break;
                case TensorRole::FfnGate: out.w_gate = std::move(view); break;
                case TensorRole::FfnUp: out.w_up = std::move(view); break;
                case TensorRole::FfnDown: out.w_down = std::move(view); break;
                default: throw StreamingError("non-layer tensor assigned to transformer layer");
            }
        }
        validate_layer_weights(source_.config, out, layer);
        return out;
    } catch (...) {
        if (count != 0) ledger_.released(bytes, count);
        throw;
    }
}

ForwardResult StreamingModel::forward(const std::vector<int64_t>& token_ids,
                                      const StreamingOptions& options) {
    emit({ExecutionEventKind::ModelExecutionBegin});
    try {
        const LayerRunner run_layer = [&](int64_t layer, const LayerConsumer& consume) {
            emit({ExecutionEventKind::LayerBegin, EvidenceSemantics::Measured,
                  std::nullopt, layer});
            uint64_t bytes = 0;
            uint64_t count = 0;
            const auto materialize_started = std::chrono::steady_clock::now();
            LayerWeights weights = materialize_layer(
                layer, options.reverse_layer_materialization_order, bytes, count);
            const double materialize_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - materialize_started).count();
            ledger_.enter_layer(layer);
            emit({ExecutionEventKind::LayerExecutionBegin, EvidenceSemantics::Measured,
                  std::nullopt, layer, -1,
                  ledger_.telemetry().current_resident_weight_bytes});
            const auto execute_started = std::chrono::steady_clock::now();
            try {
                consume(weights);
            } catch (...) {
                ledger_.leave_layer();
                ledger_.released(bytes, count);
                throw;
            }
            const double execute_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - execute_started).count();
            emit({ExecutionEventKind::LayerExecutionEnd, EvidenceSemantics::Measured,
                  std::nullopt, layer, -1,
                  ledger_.telemetry().current_resident_weight_bytes});
            ledger_.leave_layer();
            ledger_.released(bytes, count);
            for (const SourceTensor& tensor : source_.tensors) {
                if (tensor.identity.layer != layer) continue;
                emit({ExecutionEventKind::TensorReleased, EvidenceSemantics::Measured,
                      tensor.identity, layer, -1,
                      ledger_.telemetry().current_resident_weight_bytes,
                      tensor.resident_bytes, tensor.backing_identity});
            }
            emit({ExecutionEventKind::ResidentBytesChanged, EvidenceSemantics::Measured,
                  std::nullopt, layer, -1,
                  ledger_.telemetry().current_resident_weight_bytes, bytes});
            ledger_.record_layer_timing({layer, bytes, materialize_ms, execute_ms});
            emit({ExecutionEventKind::LayerEnd, EvidenceSemantics::Measured,
                  std::nullopt, layer, -1,
                  ledger_.telemetry().current_resident_weight_bytes});
            if (options.fail_after_layer_release == layer) {
                throw std::runtime_error("simulated failure after layer release");
            }
        };
        ForwardResult result;
        if (virtualize_bookends_) {
            result = forward_with_execution_runners(
                source_.config, final_norm_weight_, token_ids,
                [&](const std::vector<int64_t>& ids) {
                    return virtualized_embedding(ids);
                },
                run_layer,
                [&](const std::vector<float>& final_normed, int64_t sequence_length) {
                    return virtualized_output(final_normed, sequence_length);
                });
        } else {
            result = forward_with_layer_runner(
                source_.config, source_.tied_embeddings, *token_embedding_,
                lm_head_ ? &*lm_head_ : nullptr, final_norm_weight_, token_ids,
                run_layer);
        }
        for (int64_t token : result.selected_token) {
            emit({ExecutionEventKind::TokenScored, EvidenceSemantics::Measured,
                  std::nullopt, -1, token, ledger_.telemetry().current_resident_weight_bytes});
            emit({ExecutionEventKind::TokenSelected, EvidenceSemantics::Measured,
                  std::nullopt, -1, token, ledger_.telemetry().current_resident_weight_bytes});
        }
        emit({ExecutionEventKind::ModelExecutionEnd, EvidenceSemantics::Measured,
              std::nullopt, -1, -1, ledger_.telemetry().current_resident_weight_bytes});
        return result;
    } catch (...) {
        emit({ExecutionEventKind::ModelExecutionEnd, EvidenceSemantics::Measured,
              std::nullopt, -1, -1, ledger_.telemetry().current_resident_weight_bytes});
        throw;
    }
}

}  // namespace orcengine
