// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "fringelab/sticky_layer_cached_model.hpp"

#include <algorithm>
#include <limits>
#include <stdexcept>

#include "fringelab/sticky_layer_model.hpp"
#include "orcengine/diagnostics.hpp"
#include "orcengine/ops.hpp"

namespace fringelab {

using namespace orcengine;

namespace {

void check_finite(const std::string& name, const std::vector<float>& data) {
    if (diagnostics::check_tap(name, data)) {
        throw std::runtime_error("StickyLayerCachedModel::step: NaN/Inf detected in '" + name +
                                  "' -- failing closed rather than propagating poisoned data");
    }
}

const SourceTensor& require_tensor(const ModelSource& source, TensorRole role, int64_t layer = -1) {
    const auto matches = [&](const SourceTensor& tensor) {
        return tensor.identity.role == role && tensor.identity.layer == layer;
    };
    const auto it = std::find_if(source.tensors.begin(), source.tensors.end(), matches);
    if (it == source.tensors.end()) {
        throw std::runtime_error("StickyLayerCachedModel: model source is missing a required semantic tensor");
    }
    if (std::find_if(std::next(it), source.tensors.end(), matches) != source.tensors.end()) {
        throw std::runtime_error("StickyLayerCachedModel: model source has a duplicate semantic tensor");
    }
    return *it;
}

}  // namespace

StickyLayerCachedModel::StickyLayerCachedModel(ModelSource source, TensorMaterializer materializer,
                                               StickyLayerPlan plan)
    : source_(std::move(source)), materializer_(std::move(materializer)), plan_(std::move(plan)) {
    if (!materializer_) throw std::invalid_argument("StickyLayerCachedModel: materializer is required");

    // Source-derived validation, identical discipline to StickyLayerModel's
    // Commit 1A constructor -- re-derive the ACTUAL layer descriptors and
    // validate plan_ against them before materializing anything.
    const std::vector<LayerCostInfo> actual_layers = layer_costs_from_source(source_);
    if (static_cast<int64_t>(actual_layers.size()) != source_.config.n_layers) {
        throw std::invalid_argument("StickyLayerCachedModel: model source declares " +
                                    std::to_string(actual_layers.size()) +
                                    " transformer layers, config.n_layers is " +
                                    std::to_string(source_.config.n_layers));
    }
    validate_plan(plan_, actual_layers);

    const SourceTensor& embedding_tensor = require_tensor(source_, TensorRole::TokenEmbedding);
    token_embedding_ = materializer_(embedding_tensor.logical, embedding_tensor.backing);
    ledger_.materialized(embedding_tensor.backing_identity,
                        static_cast<uint64_t>(token_embedding_.raw().size()) * sizeof(float),
                        static_cast<uint64_t>(embedding_tensor.backing.byte_length()));

    const SourceTensor& final_norm_tensor = require_tensor(source_, TensorRole::FinalNorm);
    final_norm_weight_ = materializer_(final_norm_tensor.logical, final_norm_tensor.backing);
    ledger_.materialized(final_norm_tensor.backing_identity,
                        static_cast<uint64_t>(final_norm_weight_.raw().size()) * sizeof(float),
                        static_cast<uint64_t>(final_norm_tensor.backing.byte_length()));

    if (!source_.tied_embeddings) {
        const SourceTensor& output_tensor = require_tensor(source_, TensorRole::OutputHead);
        lm_head_ = materializer_(output_tensor.logical, output_tensor.backing);
        ledger_.materialized(output_tensor.backing_identity,
                            static_cast<uint64_t>(lm_head_->raw().size()) * sizeof(float),
                            static_cast<uint64_t>(output_tensor.backing.byte_length()));
    }

    // Materialize and PERMANENTLY hold every sticky layer, ascending order,
    // exactly once for this object's entire multi-step lifetime.
    sticky_layers_.reserve(plan_.sticky_layer_ids().size());
    for (int64_t layer : plan_.sticky_layer_ids()) {
        uint64_t bytes = 0, count = 0;
        sticky_layers_.push_back(materialize_layer(layer, bytes, count));
        if (sticky_layer_bytes_ > std::numeric_limits<uint64_t>::max() - bytes) {
            throw StickyPlanError("StickyLayerCachedModel: sticky layer resident bytes overflowed uint64_t");
        }
        sticky_layer_bytes_ += bytes;
    }
}

LayerWeights StickyLayerCachedModel::materialize_layer(int64_t layer, uint64_t& resident_bytes,
                                                        uint64_t& tensor_count) {
    std::vector<const SourceTensor*> tensors;
    for (const SourceTensor& tensor : source_.tensors) {
        if (tensor.identity.layer == layer) tensors.push_back(&tensor);
    }
    if (tensors.size() != 9) {
        throw std::runtime_error("StickyLayerCachedModel: layer does not contain exactly nine tensors");
    }
    LayerWeights out;
    try {
        for (const SourceTensor* tensor : tensors) {
            ledger_.require_can_materialize(tensor->resident_bytes);
            ResidentView view = materializer_(tensor->logical, tensor->backing);
            const uint64_t actual_bytes = static_cast<uint64_t>(view.raw().size()) * sizeof(float);
            ledger_.materialized(tensor->backing_identity, actual_bytes,
                                static_cast<uint64_t>(tensor->backing.byte_length()));
            resident_bytes += actual_bytes;
            ++tensor_count;
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
                default: throw std::runtime_error("StickyLayerCachedModel: non-layer tensor assigned to a transformer layer");
            }
        }
        return out;
    } catch (...) {
        if (tensor_count != 0) ledger_.released(resident_bytes, tensor_count);
        throw;
    }
}

CachedStepResult StickyLayerCachedModel::run_step(ContiguousAttentionKVStore& cache,
                                                   const std::vector<int64_t>& new_token_ids,
                                                   int64_t start_position) {
    const ModelConfig& cfg = source_.config;
    const int64_t new_len = static_cast<int64_t>(new_token_ids.size());
    const int64_t step_index = next_step_index_;
    if (new_len <= 0) {
        throw std::invalid_argument("StickyLayerCachedModel::step: new_token_ids must be non-empty");
    }
    if (start_position < 0) {
        throw std::invalid_argument("StickyLayerCachedModel::step: start_position must be >= 0");
    }
    if (start_position + new_len > cfg.max_positions) {
        throw std::runtime_error("StickyLayerCachedModel::step: would exceed max_positions (" +
                                 std::to_string(cfg.max_positions) + ")");
    }
    if (cache.n_layers() != cfg.n_layers || cache.n_kv_heads() != cfg.n_kv_heads ||
        cache.head_dim() != cfg.head_dim) {
        throw std::runtime_error("StickyLayerCachedModel::step: cache shape does not match model config");
    }

    std::vector<float> x = ops::embedding_lookup(token_embedding_.raw(), cfg.hidden, new_token_ids);
    check_finite("input_embedding", x);

    std::vector<std::vector<float>> cos_by_pos(static_cast<size_t>(new_len)), sin_by_pos(static_cast<size_t>(new_len));
    for (int64_t i = 0; i < new_len; ++i) {
        ops::rope_cos_sin(start_position + i, cfg.head_dim, static_cast<double>(cfg.rope_theta),
                          cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
    }

    for (int64_t li = 0; li < cfg.n_layers; ++li) {
        if (plan_.is_sticky(li)) {
            const auto& ids = plan_.sticky_layer_ids();
            const auto it = std::lower_bound(ids.begin(), ids.end(), li);
            const size_t idx = static_cast<size_t>(it - ids.begin());
            x = execute_cached_transformer_layer(x, new_len, start_position, sticky_layers_[idx], cfg, li,
                                                 cos_by_pos, sin_by_pos, cache);
            continue;
        }
        if (fault_before_cold_materialize) fault_before_cold_materialize(li, step_index);
        uint64_t bytes = 0, count = 0;
        ledger_.enter_layer(li);
        LayerWeights lw;
        try {
            lw = materialize_layer(li, bytes, count);
        } catch (...) {
            ledger_.leave_layer();
            throw;
        }
        try {
            if (fault_before_cold_execute) fault_before_cold_execute(li, step_index);
            x = execute_cached_transformer_layer(x, new_len, start_position, lw, cfg, li,
                                                 cos_by_pos, sin_by_pos, cache);
        } catch (...) {
            ledger_.leave_layer();
            ledger_.released(bytes, count);
            throw;
        }
        ledger_.leave_layer();
        ledger_.released(bytes, count);
    }

    std::vector<float> final_normed = ops::rmsnorm(x, new_len, cfg.hidden, final_norm_weight_.raw(),
                                                    cfg.rmsnorm_epsilon);
    check_finite("final_normalized_state", final_normed);
    const ResidentView& lm_head = source_.tied_embeddings ? token_embedding_ : *lm_head_;
    std::vector<float> logits = ops::linear_no_bias(final_normed, new_len, cfg.hidden, lm_head.raw(), cfg.vocab);
    check_finite("logits", logits);

    CachedStepResult result;
    result.logits = logits;
    result.selected_token.resize(static_cast<size_t>(new_len));
    for (int64_t i = 0; i < new_len; ++i) {
        std::vector<float> row(logits.begin() + i * cfg.vocab, logits.begin() + (i + 1) * cfg.vocab);
        result.selected_token[static_cast<size_t>(i)] = ops::argmax(row);
    }

    // Auto-commit on success only -- identical discipline to Phase 5A's own
    // forward_cached_step / VirtualizedCachedModel::step. next_step_index_
    // also only advances on success, so a failed step's index can be
    // retried by a caller without skipping a step number.
    cache.set_current_length(start_position + new_len);
    ++next_step_index_;
    return result;
}

CachedStepResult StickyLayerCachedModel::step_unsafe_explicit_position(ContiguousAttentionKVStore& cache,
                                                                        const std::vector<int64_t>& new_token_ids,
                                                                        int64_t start_position) {
    return run_step(cache, new_token_ids, start_position);
}

CachedStepResult StickyLayerCachedModel::step(ContiguousAttentionKVStore& cache,
                                              const std::vector<int64_t>& new_token_ids,
                                              int64_t start_position) {
    if (start_position != cache.current_length()) {
        throw KVCacheError(
            "StickyLayerCachedModel::step: start_position " + std::to_string(start_position) +
            " does not match cache.current_length() " + std::to_string(cache.current_length()) +
            " -- decode must begin exactly at the committed position; use "
            "step_unsafe_explicit_position for deliberate fault injection");
    }
    return run_step(cache, new_token_ids, start_position);
}

}  // namespace fringelab
