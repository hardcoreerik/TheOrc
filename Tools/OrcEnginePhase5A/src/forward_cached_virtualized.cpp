// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/forward_cached_virtualized.hpp"

#include <algorithm>
#include <stdexcept>
#include <unordered_set>

#include "orcengine/ops.hpp"

namespace orcengine {

namespace {

const SourceTensor& require_tensor(const ModelSource& source, TensorRole role, int64_t layer = -1) {
    const auto matches = [&](const SourceTensor& tensor) {
        return tensor.identity.role == role && tensor.identity.layer == layer;
    };
    const auto it = std::find_if(source.tensors.begin(), source.tensors.end(), matches);
    if (it == source.tensors.end()) {
        throw std::runtime_error("VirtualizedCachedModel: model source is missing a required semantic tensor");
    }
    if (std::find_if(std::next(it), source.tensors.end(), matches) != source.tensors.end()) {
        throw std::runtime_error("VirtualizedCachedModel: model source has a duplicate semantic tensor");
    }
    return *it;
}

}  // namespace

VirtualizedCachedModel::VirtualizedCachedModel(ModelSource source, VirtualizedCachedConfig config)
    : source_(std::move(source)), config_(std::move(config)), ledger_(config_.residency_budget_bytes) {
    if (!config_.materializer) throw std::invalid_argument("VirtualizedCachedModel: materializer is required");
    if (!config_.row_region_materializer) {
        throw std::invalid_argument("VirtualizedCachedModel: row-region materializer is required");
    }
    if (config_.output_chunk_rows == 0) {
        throw std::invalid_argument("VirtualizedCachedModel: output_chunk_rows must be non-zero");
    }
    (void)require_tensor(source_, TensorRole::TokenEmbedding);
    (void)require_tensor(source_, TensorRole::FinalNorm);

    // Final norm stays resident permanently -- a [hidden] vector, negligible
    // next to any [vocab,hidden]/[hidden,hidden] tensor, matching Phase 4's
    // own virtualize_bookends=true precedent exactly (streaming.cpp
    // materializes final_norm_weight_ unconditionally regardless of bookend
    // virtualization).
    const SourceTensor& final_norm_tensor = require_tensor(source_, TensorRole::FinalNorm);
    ledger_.require_can_materialize(final_norm_tensor.resident_bytes);
    final_norm_weight_ = config_.materializer(final_norm_tensor.logical, final_norm_tensor.backing);
    ledger_.materialized(final_norm_tensor.backing_identity,
                         static_cast<uint64_t>(final_norm_weight_.raw().size()) * sizeof(float),
                         static_cast<uint64_t>(final_norm_tensor.backing.byte_length()));
}

std::vector<float> VirtualizedCachedModel::virtualized_embedding(const std::vector<int64_t>& token_ids) {
    const SourceTensor& tensor = require_tensor(source_, TensorRole::TokenEmbedding);
    const int64_t hidden = source_.config.hidden;
    const uint64_t row_bytes = static_cast<uint64_t>(hidden) * sizeof(float);
    std::vector<float> output(token_ids.size() * static_cast<size_t>(hidden));
    std::unordered_set<int64_t> processed;
    for (int64_t token : token_ids) {
        if (!processed.insert(token).second) continue;
        const TensorRowRegion region{static_cast<uint64_t>(token), 1};
        ledger_.require_can_materialize(row_bytes);
        MaterializedRegion materialized = config_.row_region_materializer(tensor.logical, tensor.backing, region);
        if (materialized.view.raw().size() != static_cast<size_t>(hidden)) {
            throw std::runtime_error("VirtualizedCachedModel: embedding row region returned wrong element count");
        }
        const std::string key = tensor.backing_identity + ":rows:" + std::to_string(token) + ":1";
        ledger_.materialized(key, row_bytes, materialized.backing_bytes_read);
        ledger_.record_region(ExecutionOperation::InputEmbedding, materialized.backing_bytes_read);
        try {
            for (size_t position = 0; position < token_ids.size(); ++position) {
                if (token_ids[position] != token) continue;
                std::copy(materialized.view.raw().begin(), materialized.view.raw().end(),
                          output.begin() + position * static_cast<size_t>(hidden));
            }
        } catch (...) {
            ledger_.released(row_bytes, 1);
            throw;
        }
        ledger_.released(row_bytes, 1);
    }
    return output;
}

std::vector<float> VirtualizedCachedModel::virtualized_output(const std::vector<float>& final_normed,
                                                               int64_t new_len) {
    const SourceTensor& tensor = source_.tied_embeddings
        ? require_tensor(source_, TensorRole::TokenEmbedding)
        : require_tensor(source_, TensorRole::OutputHead);
    const int64_t hidden = source_.config.hidden;
    const int64_t vocab = source_.config.vocab;
    const std::vector<TensorRowRegion> regions = build_complete_row_partition(
        static_cast<uint64_t>(vocab), config_.output_chunk_rows);
    validate_complete_row_partition(static_cast<uint64_t>(vocab), regions);
    std::vector<float> logits(static_cast<size_t>(new_len * vocab));
    for (const TensorRowRegion& region : regions) {
        const uint64_t resident_bytes = region.row_count * static_cast<uint64_t>(hidden) * sizeof(float);
        ledger_.require_can_materialize(resident_bytes);
        MaterializedRegion materialized = config_.row_region_materializer(tensor.logical, tensor.backing, region);
        const std::string key = tensor.backing_identity + ":rows:" + std::to_string(region.row_begin) +
            ":" + std::to_string(region.row_count);
        ledger_.materialized(key, resident_bytes, materialized.backing_bytes_read);
        ledger_.record_region(ExecutionOperation::OutputProjection, materialized.backing_bytes_read);
        try {
            std::vector<float> chunk = ops::linear_no_bias(final_normed, new_len, hidden,
                                                            materialized.view.raw(),
                                                            static_cast<int64_t>(region.row_count));
            for (int64_t position = 0; position < new_len; ++position) {
                std::copy(chunk.begin() + static_cast<size_t>(position) * region.row_count,
                         chunk.begin() + static_cast<size_t>(position + 1) * region.row_count,
                         logits.begin() + static_cast<size_t>(position * vocab) + region.row_begin);
            }
        } catch (...) {
            ledger_.released(resident_bytes, 1);
            throw;
        }
        ledger_.released(resident_bytes, 1);
    }
    return logits;
}

LayerWeights VirtualizedCachedModel::materialize_layer(int64_t layer, uint64_t& resident_bytes,
                                                        uint64_t& tensor_count) {
    std::vector<const SourceTensor*> tensors;
    for (const SourceTensor& tensor : source_.tensors) {
        if (tensor.identity.layer == layer) tensors.push_back(&tensor);
    }
    if (tensors.size() != 9) {
        throw std::runtime_error("VirtualizedCachedModel: layer does not contain exactly nine tensors");
    }
    LayerWeights out;
    try {
        for (const SourceTensor* tensor : tensors) {
            ledger_.require_can_materialize(tensor->resident_bytes);
            ResidentView view = config_.materializer(tensor->logical, tensor->backing);
            const uint64_t actual_bytes = static_cast<uint64_t>(view.raw().size()) * sizeof(float);
            if (actual_bytes != tensor->resident_bytes) {
                throw std::runtime_error("VirtualizedCachedModel: materializer returned unexpected byte count");
            }
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
                default: throw std::runtime_error("VirtualizedCachedModel: non-layer tensor assigned to a transformer layer");
            }
        }
        return out;
    } catch (...) {
        if (tensor_count != 0) ledger_.released(resident_bytes, tensor_count);
        throw;
    }
}

CachedStepResult VirtualizedCachedModel::step(ContiguousAttentionKVStore& cache,
                                              const std::vector<int64_t>& new_token_ids,
                                              int64_t start_position) {
    const ModelConfig& cfg = source_.config;
    const int64_t new_len = static_cast<int64_t>(new_token_ids.size());
    if (new_len <= 0) throw std::invalid_argument("VirtualizedCachedModel::step: new_token_ids must be non-empty");
    if (start_position < 0) throw std::invalid_argument("VirtualizedCachedModel::step: start_position must be >= 0");
    if (start_position + new_len > cfg.max_positions) {
        throw std::runtime_error("VirtualizedCachedModel::step: would exceed max_positions (" +
                                 std::to_string(cfg.max_positions) + ")");
    }
    if (cache.n_layers() != cfg.n_layers || cache.n_kv_heads() != cfg.n_kv_heads ||
        cache.head_dim() != cfg.head_dim) {
        throw std::runtime_error("VirtualizedCachedModel::step: cache shape does not match model config");
    }

    std::vector<float> x = virtualized_embedding(new_token_ids);

    std::vector<std::vector<float>> cos_by_pos(static_cast<size_t>(new_len)), sin_by_pos(static_cast<size_t>(new_len));
    for (int64_t i = 0; i < new_len; ++i) {
        ops::rope_cos_sin(start_position + i, cfg.head_dim, static_cast<double>(cfg.rope_theta),
                          cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
    }

    // Layer-major: each layer is materialized exactly ONCE for the whole
    // new_len batch (proven equivalent to token-major by
    // test_prefill_schedule_equivalence.cpp), and exactly one layer is
    // resident at any instant -- ledger_.enter_layer throws if a second
    // layer tries to become resident before the first is released.
    for (int64_t li = 0; li < cfg.n_layers; ++li) {
        uint64_t bytes = 0;
        uint64_t count = 0;
        ledger_.enter_layer(li);
        LayerWeights lw;
        try {
            lw = materialize_layer(li, bytes, count);
        } catch (...) {
            ledger_.leave_layer();
            throw;
        }
        try {
            x = execute_cached_transformer_layer(x, new_len, start_position, lw, cfg, li,
                                                 cos_by_pos, sin_by_pos, cache);
        } catch (...) {
            ledger_.leave_layer();
            ledger_.released(bytes, count);
            throw;
        }
        ledger_.leave_layer();
        ledger_.released(bytes, count);
        ledger_.record_layer_timing({li, bytes, 0.0, 0.0});
    }

    std::vector<float> final_normed = ops::rmsnorm(x, new_len, cfg.hidden, final_norm_weight_.raw(),
                                                    cfg.rmsnorm_epsilon);
    std::vector<float> logits = virtualized_output(final_normed, new_len);

    CachedStepResult result;
    result.logits = logits;
    result.selected_token.resize(static_cast<size_t>(new_len));
    for (int64_t i = 0; i < new_len; ++i) {
        std::vector<float> row(logits.begin() + i * cfg.vocab, logits.begin() + (i + 1) * cfg.vocab);
        result.selected_token[static_cast<size_t>(i)] = ops::argmax(row);
    }

    // Auto-commit on success only, matching Reference Path B's identical
    // fix from the commit-API audit -- see forward_cached.cpp's comment.
    cache.set_current_length(start_position + new_len);
    return result;
}

}  // namespace orcengine
