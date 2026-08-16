// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// ModelManifest / Model: architecture shape plus the LogicalTensor set for
// one model, and the resident weights bound to it.
//
// Model::Open in Phase 1 does not mean "copy every tensor into memory before
// returning" as a hidden coupling -- it means "the fixture is parsed, the
// manifest is validated, tensors are addressable." ExecutionPlan::Create is
// the separate, explicit step that actually materializes ResidentViews (see
// execution_plan.hpp). Phase 1's ExecutionPlan always resolves to
// ResidentCPU/immediate, so in practice the distinction has no visible
// latency yet -- but the two calls are still separate on purpose, per
// docs/OrcEngine/ARCHITECTURE.md's "Model loaded does not mean fully
// resident" section, so Phase 6B can make ExecutionPlan::Create actually lazy
// without changing Model::Open's contract.
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

    int64_t group_size() const { return n_q_heads / n_kv_heads; }
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

// Model: manifest plus resident weights. Phase 1's "Open" always fully
// materializes (see model.hpp doc comment above for why that's still
// behind a named, separate call rather than baked into the type).
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
