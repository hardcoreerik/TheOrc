// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

#include "orcengine/backing_extent.hpp"
#include "orcengine/logical_tensor.hpp"
#include "orcengine/model.hpp"

namespace orcengine {

enum class TensorRole {
    TokenEmbedding,
    FinalNorm,
    OutputHead,
    AttentionNorm,
    AttentionQuery,
    AttentionKey,
    AttentionValue,
    AttentionOutput,
    FfnNorm,
    FfnGate,
    FfnUp,
    FfnDown,
};

struct TensorIdentity {
    TensorRole role = TensorRole::TokenEmbedding;
    int64_t layer = -1;
};

struct SourceTensor {
    TensorIdentity identity;
    LogicalTensor logical;
    BackingExtent backing;
    uint64_t resident_bytes = 0;
    std::string backing_identity;
};

struct ModelSource {
    ModelConfig config;
    bool tied_embeddings = false;
    std::vector<SourceTensor> tensors;
};

using TensorMaterializer = std::function<ResidentView(
    const LogicalTensor&, const BackingExtent&)>;

struct ModelSourceBinding {
    ModelSource source;
    TensorMaterializer materialize;
};

}  // namespace orcengine
