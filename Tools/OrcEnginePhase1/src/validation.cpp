// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/validation.hpp"

#include <cmath>
#include <cstddef>
#include <unordered_set>

namespace orcengine {

namespace {

void require(bool condition, const std::string& message) {
    if (!condition) throw ValidationError(message);
}

void require_view_shape(const std::string& name, const ResidentView& view,
                        const std::vector<int64_t>& expected_dims) {
    require(view.shape().dims() == expected_dims,
            name + " must have shape " + TensorShape(expected_dims).to_string() +
            ", got " + view.shape().to_string());
    require(static_cast<int64_t>(view.raw().size()) == view.shape().element_count(),
            name + " data size does not match its shape");
    for (float value : view.raw()) {
        require(std::isfinite(value), name + " contains NaN or Inf");
    }
}

void append_layer_requirements(std::vector<ExpectedRequirement>& out,
                               int64_t layer, const ModelConfig& cfg,
                               int64_t seq) {
    const std::string p = "layer" + std::to_string(layer) + ".";
    const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
    const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;
    out.push_back({p + "pre_attention_normalized_state", {seq, cfg.hidden}});
    out.push_back({p + "q_projection", {seq, q_dim}});
    out.push_back({p + "k_projection", {seq, kv_dim}});
    out.push_back({p + "v_projection", {seq, kv_dim}});
    out.push_back({p + "q_after_rope", {cfg.n_q_heads, seq, cfg.head_dim}});
    out.push_back({p + "k_after_rope", {cfg.n_kv_heads, seq, cfg.head_dim}});
    out.push_back({p + "attention_probabilities", {cfg.n_q_heads, seq, seq}});
    out.push_back({p + "attention_output_before_projection", {seq, q_dim}});
    out.push_back({p + "attention_output_after_projection", {seq, cfg.hidden}});
    out.push_back({p + "post_attention_residual", {seq, cfg.hidden}});
    out.push_back({p + "pre_ffn_normalized_state", {seq, cfg.hidden}});
    out.push_back({p + "gate_projection", {seq, cfg.intermediate}});
    out.push_back({p + "up_projection", {seq, cfg.intermediate}});
    out.push_back({p + "activated_gated_product", {seq, cfg.intermediate}});
    out.push_back({p + "down_projection", {seq, cfg.hidden}});
    out.push_back({p + "post_ffn_residual", {seq, cfg.hidden}});
}

}  // namespace

void validate_model_config(const ModelConfig& cfg) {
    require(cfg.vocab > 0, "vocab must be > 0");
    require(cfg.hidden > 0, "hidden must be > 0");
    require(cfg.intermediate > 0, "intermediate must be > 0");
    require(cfg.n_layers > 0, "n_layers must be > 0");
    require(cfg.n_q_heads > 0, "n_q_heads must be > 0");
    require(cfg.n_kv_heads > 0, "n_kv_heads must be > 0");
    require(cfg.head_dim > 0, "head_dim must be > 0");
    require(cfg.max_positions > 0, "max_positions must be > 0");
    require(std::isfinite(cfg.rmsnorm_epsilon) && cfg.rmsnorm_epsilon > 0.0f,
            "rmsnorm_epsilon must be finite and > 0");
    require(std::isfinite(cfg.rope_theta) && cfg.rope_theta > 0.0f,
            "rope_theta must be finite and > 0");
    require(cfg.hidden % cfg.n_q_heads == 0,
            "hidden must be divisible by n_q_heads");
    require(cfg.n_q_heads % cfg.n_kv_heads == 0,
            "n_q_heads must be divisible by n_kv_heads");
    require(cfg.head_dim == cfg.hidden / cfg.n_q_heads,
            "head_dim must equal hidden / n_q_heads");
}

void validate_model(const Model& model, const std::vector<int64_t>& token_ids) {
    const ModelConfig& cfg = model.config();
    validate_model_config(cfg);

    validate_forward_inputs(cfg, token_ids);

    require(static_cast<int64_t>(model.layers.size()) == cfg.n_layers,
            "layer count metadata does not match loaded layer tensor count");
    validate_bookend_weights(cfg, model.manifest.tied_embeddings, model.token_embedding,
                             model.lm_head ? &*model.lm_head : nullptr,
                             model.final_norm_weight);
    for (int64_t i = 0; i < cfg.n_layers; ++i) {
        validate_layer_weights(cfg, model.layers[static_cast<size_t>(i)], i);
    }
}

void validate_forward_inputs(const ModelConfig& cfg,
                             const std::vector<int64_t>& token_ids) {
    validate_model_config(cfg);
    require(!token_ids.empty(), "input sequence must not be empty");
    require(static_cast<int64_t>(token_ids.size()) <= cfg.max_positions,
            "input sequence length exceeds max_positions");
    for (int64_t token : token_ids) {
        require(token >= 0 && token < cfg.vocab,
                "input token is outside [0, vocab)");
    }

}

void validate_bookend_weights(const ModelConfig& cfg,
                              bool tied_embeddings,
                              const ResidentView& token_embedding,
                              const ResidentView* lm_head,
                              const ResidentView& final_norm_weight) {
    validate_model_config(cfg);
    require_view_shape("token_embedding", token_embedding, {cfg.vocab, cfg.hidden});
    validate_final_norm_weight(cfg, final_norm_weight);

    if (tied_embeddings) {
        if (lm_head != nullptr) {
            require_view_shape("lm_head", *lm_head, {cfg.vocab, cfg.hidden});
            require(lm_head->raw() == token_embedding.raw(),
                    "tied lm_head duplicate must be byte-identical to token_embedding");
        }
    } else {
        require(lm_head != nullptr, "untied model requires lm_head");
        require_view_shape("lm_head", *lm_head, {cfg.vocab, cfg.hidden});
    }
}

void validate_final_norm_weight(const ModelConfig& cfg,
                                const ResidentView& final_norm_weight) {
    require_view_shape("final_norm_weight", final_norm_weight, {cfg.hidden});
}

void validate_layer_weights(const ModelConfig& cfg,
                            const LayerWeights& layer,
                            int64_t layer_index) {
    validate_model_config(cfg);
    require(layer_index >= 0 && layer_index < cfg.n_layers,
            "layer index is outside model configuration");
    const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
    const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;
    const std::string p = "layer" + std::to_string(layer_index) + ".";
    require_view_shape(p + "attn_norm_weight", layer.attn_norm_weight, {cfg.hidden});
    require_view_shape(p + "w_q", layer.w_q, {q_dim, cfg.hidden});
    require_view_shape(p + "w_k", layer.w_k, {kv_dim, cfg.hidden});
    require_view_shape(p + "w_v", layer.w_v, {kv_dim, cfg.hidden});
    require_view_shape(p + "w_o", layer.w_o, {cfg.hidden, q_dim});
    require_view_shape(p + "ffn_norm_weight", layer.ffn_norm_weight, {cfg.hidden});
    require_view_shape(p + "w_gate", layer.w_gate, {cfg.intermediate, cfg.hidden});
    require_view_shape(p + "w_up", layer.w_up, {cfg.intermediate, cfg.hidden});
    require_view_shape(p + "w_down", layer.w_down, {cfg.hidden, cfg.intermediate});
}

std::vector<ExpectedRequirement> required_forward_expectations(
    const ModelConfig& cfg, int64_t seq) {
    validate_model_config(cfg);
    if (seq <= 0) throw ValidationError("expectation sequence length must be > 0");

    std::vector<ExpectedRequirement> out;
    out.reserve(static_cast<size_t>(4 + cfg.n_layers * 16));
    out.push_back({"input_embedding", {seq, cfg.hidden}});
    for (int64_t layer = 0; layer < cfg.n_layers; ++layer) {
        append_layer_requirements(out, layer, cfg, seq);
    }
    out.push_back({"final_normalized_state", {seq, cfg.hidden}});
    out.push_back({"logits", {seq, cfg.vocab}});
    out.push_back({"selected_token", {seq}});
    return out;
}

void validate_forward_expectations(
    const ModelConfig& cfg,
    int64_t seq,
    const std::unordered_map<std::string, ActivationBuffer>& expected) {
    const auto required = required_forward_expectations(cfg, seq);
    require(expected.size() == required.size(),
            "expected exactly " + std::to_string(required.size()) +
            " forward expectations, loaded " + std::to_string(expected.size()));

    std::unordered_set<std::string> required_names;
    for (const ExpectedRequirement& req : required) {
        required_names.insert(req.name);
        auto it = expected.find(req.name);
        require(it != expected.end(), "missing required expectation '" + req.name + "'");
        require(it->second.dims == req.dims,
                "expectation '" + req.name + "' has wrong shape");
        require(static_cast<int64_t>(it->second.data.size()) == TensorShape(req.dims).element_count(),
                "expectation '" + req.name + "' data size does not match shape");
        for (float value : it->second.data) {
            require(std::isfinite(value),
                    "expectation '" + req.name + "' contains NaN or Inf");
        }
    }
    for (const auto& [name, _] : expected) {
        require(required_names.contains(name), "unexpected expectation '" + name + "'");
    }

    const ActivationBuffer& selected = expected.at("selected_token");
    for (float value : selected.data) {
        require(value == std::floor(value) && value >= 0.0f && value < static_cast<float>(cfg.vocab),
                "selected_token expectation must contain integral token IDs in range");
    }
}

}  // namespace orcengine
