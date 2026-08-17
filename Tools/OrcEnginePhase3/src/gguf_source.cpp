// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/gguf_source.hpp"

#include <algorithm>
#include <limits>
#include <memory>

namespace orcengine {
namespace {

TensorRole neutral_role(SemanticTensorRole role) {
    switch (role) {
        case SemanticTensorRole::TokenEmbedding: return TensorRole::TokenEmbedding;
        case SemanticTensorRole::FinalNorm: return TensorRole::FinalNorm;
        case SemanticTensorRole::OutputHead: return TensorRole::OutputHead;
        case SemanticTensorRole::AttentionNorm: return TensorRole::AttentionNorm;
        case SemanticTensorRole::AttentionQuery: return TensorRole::AttentionQuery;
        case SemanticTensorRole::AttentionKey: return TensorRole::AttentionKey;
        case SemanticTensorRole::AttentionValue: return TensorRole::AttentionValue;
        case SemanticTensorRole::AttentionOutput: return TensorRole::AttentionOutput;
        case SemanticTensorRole::FfnNorm: return TensorRole::FfnNorm;
        case SemanticTensorRole::FfnGate: return TensorRole::FfnGate;
        case SemanticTensorRole::FfnUp: return TensorRole::FfnUp;
        case SemanticTensorRole::FfnDown: return TensorRole::FfnDown;
    }
    throw GgufError("unknown semantic tensor role");
}

uint64_t f32_bytes(const LogicalTensor& tensor) {
    const int64_t elements = tensor.shape().element_count();
    if (elements < 0 || static_cast<uint64_t>(elements) >
            std::numeric_limits<uint64_t>::max() / sizeof(float)) {
        throw GgufError("resident byte count overflow for '" + tensor.name() + "'");
    }
    return static_cast<uint64_t>(elements) * sizeof(float);
}

bool same_extent(const BackingExtent& left, const BackingExtent& right) {
    return left.source_path() == right.source_path() &&
           left.byte_offset() == right.byte_offset() &&
           left.byte_length() == right.byte_length() &&
           left.encoding() == right.encoding();
}

}  // namespace

ModelSourceBinding bind_gguf_source(ModelArtifactManifest manifest) {
    if (manifest.kind != ModelArtifactKind::FullModel || !manifest.materializable) {
        throw GgufError("GGUF source is not a complete materializable model");
    }
    auto retained = std::make_shared<ModelArtifactManifest>(std::move(manifest));
    ModelSource source;
    source.config = retained->config;
    source.tied_embeddings = retained->tied_embeddings;
    source.tensors.reserve(retained->mapped_tensors.size());
    for (const auto& tensor : retained->mapped_tensors) {
        source.tensors.push_back({
            {neutral_role(tensor.semantic.role), tensor.semantic.layer},
            tensor.logical,
            tensor.backing,
            f32_bytes(tensor.logical),
            tensor.backing.source_path() + ":" +
                std::to_string(tensor.backing.byte_offset()) + ":" +
                std::to_string(tensor.backing.byte_length()),
        });
    }
    TensorMaterializer materializer = [retained](const LogicalTensor& logical,
                                                  const BackingExtent& backing) {
        const auto it = std::find_if(retained->mapped_tensors.begin(), retained->mapped_tensors.end(),
            [&](const MappedGgufTensor& tensor) {
                return tensor.logical.name() == logical.name() && same_extent(tensor.backing, backing);
            });
        if (it == retained->mapped_tensors.end()) {
            throw GgufError("materialization request is not present in the bound GGUF source");
        }
        return materialize_gguf_tensor(*it);
    };
    TensorRowRegionMaterializer row_region_materializer =
        [retained](const LogicalTensor& logical, const BackingExtent& backing,
                   const TensorRowRegion& region) {
            const auto it = std::find_if(
                retained->mapped_tensors.begin(), retained->mapped_tensors.end(),
                [&](const MappedGgufTensor& tensor) {
                    return tensor.logical.name() == logical.name() &&
                           same_extent(tensor.backing, backing);
                });
            if (it == retained->mapped_tensors.end()) {
                throw GgufError("row materialization request is not present in the bound GGUF source");
            }
            uint64_t backing_bytes = 0;
            ResidentView view = materialize_gguf_tensor_rows(
                *it, region.row_begin, region.row_count, backing_bytes);
            return MaterializedRegion{std::move(view), backing_bytes};
        };
    return {std::move(source), std::move(materializer),
            std::move(row_region_materializer)};
}

}  // namespace orcengine
