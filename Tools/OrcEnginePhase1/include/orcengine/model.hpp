// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// ModelManifest / Model: architecture shape plus the LogicalTensor set for
// one model, and the resident weights bound to it.
//
// Phase 1 has no Model::Open API. The fixture loader eagerly constructs and
// validates this Model. BackingExtent materialization is exercised separately
// by the storage metamorphic tests; a future production loader may connect
// those concepts without changing the transformer operators.
#pragma once

#include <optional>
#include <stdexcept>
#include <string>
#include <unordered_map>
#include <vector>

#include "orcengine/logical_tensor.hpp"
#include "orcengine/resident_view.hpp"

namespace orcengine {

struct ModelConfig {
    int64_t vocab = 0;
    int64_t hidden = 0;
    int64_t intermediate = 0;
    int64_t n_layers = 0;
    int64_t n_q_heads = 0;
    int64_t n_kv_heads = 0;
    int64_t head_dim = 0;
    int64_t max_positions = 0;
    float rmsnorm_epsilon = 1e-5f;
    float rope_theta = 10000.0f;

    int64_t group_size() const {
        if (n_q_heads <= 0 || n_kv_heads <= 0 || n_q_heads % n_kv_heads != 0) {
            throw std::invalid_argument("ModelConfig: invalid GQA head relationship");
        }
        return n_q_heads / n_kv_heads;
    }
};

struct LayerWeights {
    ResidentView attn_norm_weight;  // [hidden]
    ResidentView w_q;               // [n_q_heads*head_dim, hidden]
    ResidentView w_k;               // [n_kv_heads*head_dim, hidden]
    ResidentView w_v;               // [n_kv_heads*head_dim, hidden]
    ResidentView w_o;               // [hidden, n_q_heads*head_dim]
    ResidentView ffn_norm_weight;   // [hidden]
    ResidentView w_gate;            // [intermediate, hidden]
    ResidentView w_up;              // [intermediate, hidden]
    ResidentView w_down;            // [hidden, intermediate]
};

// ModelManifest: the LogicalTensor identity list for a model, independent
// of whether weights are actually loaded. Phase 1 populates it eagerly from
// the same fixture that provides the weights (there is no other source
// yet), but the type stays separate from Model's resident data.
class ModelManifest {
public:
    ModelConfig config;
    bool tied_embeddings = true;
    std::vector<LogicalTensor> tensors;  // identity only -- no bytes.
};

// Model: manifest plus the ResidentViews used by execution.
class Model {
public:
    ModelManifest manifest;
    ResidentView token_embedding;         // [vocab, hidden]
    std::optional<ResidentView> lm_head;  // [vocab, hidden] if untied; empty if tied.
    ResidentView final_norm_weight;       // [hidden]
    std::vector<LayerWeights> layers;

    const ResidentView& effective_lm_head() const {
        return lm_head.has_value() ? *lm_head : token_embedding;
    }

    const ModelConfig& config() const { return manifest.config; }
};

}  // namespace orcengine
