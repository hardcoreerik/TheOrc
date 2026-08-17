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

uint64_t resident_bytes(const MappedGgufTensor& tensor) {
    const int64_t elements = tensor.logical.shape().element_count();
    if (elements < 0 || static_cast<uint64_t>(elements) >
            std::numeric_limits<uint64_t>::max() / sizeof(float)) {
        throw std::overflow_error("streaming resident byte count overflow");
    }
    return static_cast<uint64_t>(elements) * sizeof(float);
}

std::string extent_key(const MappedGgufTensor& tensor) {
    return tensor.backing.source_path() + ":" + std::to_string(tensor.backing.byte_offset()) +
           ":" + std::to_string(tensor.backing.byte_length());
}

const MappedGgufTensor& require_tensor(const ModelArtifactManifest& manifest,
                                      SemanticTensorRole role, int64_t layer = -1) {
    const auto it = std::find_if(manifest.mapped_tensors.begin(), manifest.mapped_tensors.end(),
        [&](const MappedGgufTensor& tensor) {
            return tensor.semantic.role == role && tensor.semantic.layer == layer;
        });
    if (it == manifest.mapped_tensors.end()) {
        throw GgufError("streaming manifest is missing required semantic tensor");
    }
    return *it;
}

}  // namespace

void ResidencyLedger::materialized(const std::string& key, uint64_t resident,
                                   uint64_t backing) {
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

StreamingModel::StreamingModel(ModelArtifactManifest manifest)
    : manifest_(std::move(manifest)) {
    if (manifest_.kind != ModelArtifactKind::FullModel || !manifest_.materializable) {
        throw GgufError("streaming requires a complete materializable model");
    }
    token_embedding_ = materialize(require_tensor(manifest_, SemanticTensorRole::TokenEmbedding));
    final_norm_weight_ = materialize(require_tensor(manifest_, SemanticTensorRole::FinalNorm));
    if (!manifest_.tied_embeddings) {
        lm_head_ = materialize(require_tensor(manifest_, SemanticTensorRole::OutputHead));
    }
    validate_bookend_weights(manifest_.config, manifest_.tied_embeddings, token_embedding_,
                             lm_head_ ? &*lm_head_ : nullptr, final_norm_weight_);
}

ResidentView StreamingModel::materialize(const MappedGgufTensor& tensor) {
    ResidentView view = materialize_gguf_tensor(tensor);
    ledger_.materialized(extent_key(tensor), resident_bytes(tensor),
                         static_cast<uint64_t>(tensor.backing.byte_length()));
    return view;
}

LayerWeights StreamingModel::materialize_layer(int64_t layer, bool reverse_order,
                                               uint64_t& bytes, uint64_t& count) {
    std::vector<const MappedGgufTensor*> tensors;
    for (const auto& tensor : manifest_.mapped_tensors) {
        if (tensor.semantic.layer == layer) tensors.push_back(&tensor);
    }
    if (tensors.size() != 9) throw GgufError("streaming layer does not contain exactly nine tensors");
    if (reverse_order) std::reverse(tensors.begin(), tensors.end());

    LayerWeights out;
    try {
        for (const MappedGgufTensor* tensor : tensors) {
            ResidentView view = materialize(*tensor);
            bytes = checked_add(bytes, resident_bytes(*tensor), "layer resident bytes");
            count = checked_add(count, 1, "layer tensor count");
            switch (tensor->semantic.role) {
                case SemanticTensorRole::AttentionNorm: out.attn_norm_weight = std::move(view); break;
                case SemanticTensorRole::AttentionQuery: out.w_q = std::move(view); break;
                case SemanticTensorRole::AttentionKey: out.w_k = std::move(view); break;
                case SemanticTensorRole::AttentionValue: out.w_v = std::move(view); break;
                case SemanticTensorRole::AttentionOutput: out.w_o = std::move(view); break;
                case SemanticTensorRole::FfnNorm: out.ffn_norm_weight = std::move(view); break;
                case SemanticTensorRole::FfnGate: out.w_gate = std::move(view); break;
                case SemanticTensorRole::FfnUp: out.w_up = std::move(view); break;
                case SemanticTensorRole::FfnDown: out.w_down = std::move(view); break;
                default: throw GgufError("non-layer tensor assigned to transformer layer");
            }
        }
        validate_layer_weights(manifest_.config, out, layer);
        return out;
    } catch (...) {
        if (count != 0) ledger_.released(bytes, count);
        throw;
    }
}

ForwardResult StreamingModel::forward(const std::vector<int64_t>& token_ids,
                                      const StreamingOptions& options) {
    return forward_with_layer_runner(
        manifest_.config, manifest_.tied_embeddings, token_embedding_,
        lm_head_ ? &*lm_head_ : nullptr, final_norm_weight_, token_ids,
        [&](int64_t layer, const LayerConsumer& consume) {
            uint64_t bytes = 0;
            uint64_t count = 0;
            const auto materialize_started = std::chrono::steady_clock::now();
            LayerWeights weights = materialize_layer(
                layer, options.reverse_layer_materialization_order, bytes, count);
            const double materialize_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - materialize_started).count();
            ledger_.enter_layer(layer);
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
            ledger_.leave_layer();
            ledger_.released(bytes, count);
            ledger_.record_layer_timing({layer, bytes, materialize_ms, execute_ms});
            if (options.fail_after_layer_release == layer) {
                throw std::runtime_error("simulated failure after layer release");
            }
        });
}

}  // namespace orcengine
