// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "fringelab/residency_budget.hpp"

#include <algorithm>
#include <stdexcept>

namespace fringelab {

using namespace orcengine;

namespace {

const SourceTensor& require_tensor(const ModelSource& source, TensorRole role, int64_t layer = -1) {
    const auto matches = [&](const SourceTensor& tensor) {
        return tensor.identity.role == role && tensor.identity.layer == layer;
    };
    const auto it = std::find_if(source.tensors.begin(), source.tensors.end(), matches);
    if (it == source.tensors.end()) {
        throw std::runtime_error("ResidencyBudgetModel: model source is missing a required semantic tensor");
    }
    if (std::find_if(std::next(it), source.tensors.end(), matches) != source.tensors.end()) {
        throw std::runtime_error("ResidencyBudgetModel: model source has a duplicate semantic tensor");
    }
    return *it;
}

}  // namespace

ResidencyBudgetModel::ResidencyBudgetModel(ModelSource source, TensorMaterializer materializer,
                                           int64_t n_resident_layers)
    : source_(std::move(source)), materializer_(std::move(materializer)),
      n_resident_layers_(n_resident_layers) {
    if (n_resident_layers_ < 0 || n_resident_layers_ > source_.config.n_layers) {
        throw std::invalid_argument("ResidencyBudgetModel: n_resident_layers out of range");
    }
    if (!materializer_) throw std::invalid_argument("ResidencyBudgetModel: materializer is required");

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

    // Materialize and PERMANENTLY hold the first n_resident_layers_ layers --
    // this is the "budgeted residency" policy under test. These are never
    // released for the lifetime of this object.
    for (int64_t li = 0; li < n_resident_layers_; ++li) {
        uint64_t bytes = 0, count = 0;
        resident_layers_.push_back(materialize_layer(li, bytes, count));
        resident_layer_bytes_ += bytes;
    }
}

LayerWeights ResidencyBudgetModel::materialize_layer(int64_t layer, uint64_t& resident_bytes,
                                                      uint64_t& tensor_count) {
    std::vector<const SourceTensor*> tensors;
    for (const SourceTensor& tensor : source_.tensors) {
        if (tensor.identity.layer == layer) tensors.push_back(&tensor);
    }
    if (tensors.size() != 9) {
        throw std::runtime_error("ResidencyBudgetModel: layer does not contain exactly nine tensors");
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
                default: throw std::runtime_error("ResidencyBudgetModel: non-layer tensor assigned to a transformer layer");
            }
        }
        return out;
    } catch (...) {
        if (tensor_count != 0) ledger_.released(resident_bytes, tensor_count);
        throw;
    }
}

ForwardResult ResidencyBudgetModel::forward(const std::vector<int64_t>& token_ids) {
    const LayerRunner run_layer = [&](int64_t layer, const LayerConsumer& consume) {
        if (layer < n_resident_layers_) {
            // Already resident -- no materialize/release, matches the
            // "kept permanently resident" policy exactly.
            consume(resident_layers_[static_cast<size_t>(layer)]);
            return;
        }
        uint64_t bytes = 0, count = 0;
        ledger_.enter_layer(layer);
        LayerWeights lw;
        try {
            lw = materialize_layer(layer, bytes, count);
        } catch (...) {
            ledger_.leave_layer();
            throw;
        }
        try {
            consume(lw);
        } catch (...) {
            ledger_.leave_layer();
            ledger_.released(bytes, count);
            throw;
        }
        ledger_.leave_layer();
        ledger_.released(bytes, count);
    };
    return forward_with_layer_runner(source_.config, source_.tied_embeddings, token_embedding_,
                                     lm_head_ ? &*lm_head_ : nullptr, final_norm_weight_, token_ids, run_layer);
}

}  // namespace fringelab
