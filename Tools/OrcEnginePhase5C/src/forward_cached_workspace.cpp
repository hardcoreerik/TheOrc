// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/forward_cached_workspace.hpp"

#include <stdexcept>

#include "orcengine/diagnostics.hpp"
#include "orcengine/ops.hpp"

namespace orcengine {

namespace {
void check_finite(const std::string& name, std::span<const float> data) {
    if (diagnostics::check_tap(name, std::vector<float>(data.begin(), data.end()))) {
        throw std::runtime_error("forward_cached_step_workspace: NaN/Inf detected in '" + name +
                                  "' -- failing closed rather than propagating poisoned data");
    }
}
}  // namespace

CachedStepResult forward_cached_step_workspace_unsafe_explicit_position(
    const Model& model, ContiguousAttentionKVStore& cache, const std::vector<int64_t>& new_token_ids,
    int64_t start_position, ActivationWorkspace& workspace) {
    const ModelConfig& cfg = model.config();
    const int64_t new_len = static_cast<int64_t>(new_token_ids.size());
    if (new_len <= 0) {
        throw std::invalid_argument("forward_cached_step_workspace: new_token_ids must be non-empty");
    }
    if (start_position < 0) {
        throw std::invalid_argument("forward_cached_step_workspace: start_position must be >= 0");
    }
    if (start_position + new_len > cfg.max_positions) {
        throw std::runtime_error("forward_cached_step_workspace: would exceed max_positions (" +
                                 std::to_string(cfg.max_positions) + ")");
    }
    if (cache.n_layers() != cfg.n_layers || cache.n_kv_heads() != cfg.n_kv_heads ||
        cache.head_dim() != cfg.head_dim) {
        throw std::runtime_error("forward_cached_step_workspace: cache shape does not match model config");
    }
    if (new_len > workspace.max_tokens_per_step()) {
        throw std::runtime_error("forward_cached_step_workspace: new_len (" + std::to_string(new_len) +
                                 ") exceeds workspace.max_tokens_per_step() (" +
                                 std::to_string(workspace.max_tokens_per_step()) + ")");
    }

    const int64_t hidden = cfg.hidden;

    std::vector<float> x = ops::embedding_lookup(model.token_embedding.raw(), hidden, new_token_ids);
    check_finite("input_embedding", x);

    std::vector<std::vector<float>> cos_by_pos(static_cast<size_t>(new_len)), sin_by_pos(static_cast<size_t>(new_len));
    for (int64_t i = 0; i < new_len; ++i) {
        ops::rope_cos_sin(start_position + i, cfg.head_dim, static_cast<double>(cfg.rope_theta),
                          cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
    }

    for (int64_t li = 0; li < cfg.n_layers; ++li) {
        const LayerWeights& lw = model.layers[static_cast<size_t>(li)];
        // Workspace-aware overload (Phase 5A, Phase 5C addition) -- same
        // arithmetic as the frozen return-by-value overload, called
        // through its own public seam, not copied.
        x = execute_cached_transformer_layer(x, new_len, start_position, lw, cfg, li, cos_by_pos, sin_by_pos, cache,
                                             workspace);
    }

    std::span<float> final_normed = workspace.final_norm_buffer(new_len);
    ops::rmsnorm_into(x, new_len, hidden, model.final_norm_weight.raw(), cfg.rmsnorm_epsilon, final_normed);
    check_finite("final_normalized_state", final_normed);

    const ResidentView& lm_head = model.effective_lm_head();
    std::span<float> logits_buf = workspace.logits_buffer(new_len);
    ops::linear_no_bias_into(final_normed, new_len, hidden, lm_head.raw(), cfg.vocab, logits_buf);
    check_finite("logits", logits_buf);

    CachedStepResult result;
    result.logits.assign(logits_buf.begin(), logits_buf.end());
    result.selected_token.resize(static_cast<size_t>(new_len));
    for (int64_t i = 0; i < new_len; ++i) {
        std::vector<float> row(result.logits.begin() + i * cfg.vocab, result.logits.begin() + (i + 1) * cfg.vocab);
        result.selected_token[static_cast<size_t>(i)] = ops::argmax(row);
    }

    // Commit on success only -- identical discipline to Phase 5A's own
    // forward_cached_step_unsafe_explicit_position.
    cache.set_current_length(start_position + new_len);
    return result;
}

CachedStepResult forward_cached_step_workspace(const Model& model, ContiguousAttentionKVStore& cache,
                                               const std::vector<int64_t>& new_token_ids, int64_t start_position,
                                               ActivationWorkspace& workspace) {
    if (start_position != cache.current_length()) {
        throw KVCacheError(
            "forward_cached_step_workspace: start_position " + std::to_string(start_position) +
            " does not match cache.current_length() " + std::to_string(cache.current_length()) +
            " -- decode must begin exactly at the committed position; use "
            "forward_cached_step_workspace_unsafe_explicit_position for deliberate fault injection");
    }
    return forward_cached_step_workspace_unsafe_explicit_position(model, cache, new_token_ids, start_position,
                                                                   workspace);
}

}  // namespace orcengine
