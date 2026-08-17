// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/streaming.hpp"

#include <algorithm>
#include <chrono>
#include <limits>
#include <stdexcept>

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

const SourceTensor& require_tensor(const ModelSource& source,
                                   TensorRole role, int64_t layer = -1) {
    const auto it = std::find_if(source.tensors.begin(), source.tensors.end(),
        [&](const SourceTensor& tensor) {
            return tensor.identity.role == role && tensor.identity.layer == layer;
        });
    if (it == source.tensors.end()) {
        throw StreamingError("model source is missing required semantic tensor");
    }
    return *it;
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
      observer_(std::move(config.observer)), ledger_(config.residency_budget_bytes) {
    validate_model_config(source_.config);
    if (!materializer_) throw StreamingError("tensor materializer is required");
    token_embedding_ = materialize(require_tensor(source_, TensorRole::TokenEmbedding));
    final_norm_weight_ = materialize(require_tensor(source_, TensorRole::FinalNorm));
    if (!source_.tied_embeddings) {
        lm_head_ = materialize(require_tensor(source_, TensorRole::OutputHead));
    }
    validate_bookend_weights(source_.config, source_.tied_embeddings, token_embedding_,
                             lm_head_ ? &*lm_head_ : nullptr, final_norm_weight_);
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
        ForwardResult result = forward_with_layer_runner(
            source_.config, source_.tied_embeddings, token_embedding_,
            lm_head_ ? &*lm_head_ : nullptr, final_norm_weight_, token_ids,
            [&](int64_t layer, const LayerConsumer& consume) {
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
            });
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
