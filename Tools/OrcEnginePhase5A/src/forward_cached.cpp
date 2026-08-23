// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/forward_cached.hpp"

#include <cmath>
#include <optional>
#include <stdexcept>

#include "orcengine/diagnostics.hpp"
#include "orcengine/ops.hpp"

namespace orcengine {

namespace {

void check_finite(const std::string& name, std::span<const float> data) {
    // diagnostics::check_tap (frozen Phase 1 API) takes a std::vector<float>
    // specifically; this local helper accepts a span (so it works for both
    // owned-vector and workspace-span buffers) and copies only for this
    // check, not on any hot numerical path.
    if (diagnostics::check_tap(name, std::vector<float>(data.begin(), data.end()))) {
        throw std::runtime_error("forward_cached_step: NaN/Inf detected in '" + name +
                                  "' -- failing closed rather than propagating poisoned data");
    }
}

// Shared internal implementation both execute_cached_transformer_layer
// overloads route through (Phase 5C, OE-ADR: narrow backward-compatible
// workspace seam). `workspace` is null for the original frozen-compatible
// entry point (every buffer below is freshly allocated locally, exactly
// as before this refactor -- observably byte-identical) and non-null for
// the workspace-aware overload (the nine named buffers below alias
// `workspace`'s own permanently-owned storage instead). RoPE's per-
// head-dim temporaries, the manual attention-context accumulation, and
// the residual/final-output additions are local std::vector in BOTH
// modes -- not covered by `workspace` in this Stage 1, disclosed as such
// in the public header's own doc comment.
std::vector<float> execute_cached_transformer_layer_impl(
    const std::vector<float>& x, int64_t new_len, int64_t start_position,
    const LayerWeights& lw, const ModelConfig& cfg, int64_t layer,
    const std::vector<std::vector<float>>& cos_by_pos,
    const std::vector<std::vector<float>>& sin_by_pos,
    ContiguousAttentionKVStore& cache, ActivationWorkspace* workspace) {
    const int64_t hidden = cfg.hidden;
    const int64_t group_size = cfg.group_size();
    const float scale = 1.0f / std::sqrt(static_cast<float>(cfg.head_dim));
    const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
    const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;

    // Select the nine named buffers: workspace-owned spans (reused across
    // calls) or freshly-allocated local storage (owned by this call only,
    // matching the pre-refactor behavior exactly). Either way, the rest of
    // this function only ever sees `std::span<float>` -- the arithmetic
    // below does not know or care which mode is active.
    std::optional<LayerActivationBuffers> from_workspace;
    std::vector<float> local_norm_out, local_q_proj, local_k_proj, local_v_proj, local_attn_out, local_gate_proj,
        local_up_proj, local_gate_activated, local_ffn_out;
    std::span<float> norm_out, q_proj, k_proj, v_proj, attn_out_buf, gate_proj, up_proj, gate_activated, ffn_out_buf;
    if (workspace != nullptr) {
        from_workspace = workspace->layer_buffers(new_len);
        norm_out = from_workspace->norm_out;
        q_proj = from_workspace->q_proj;
        k_proj = from_workspace->k_proj;
        v_proj = from_workspace->v_proj;
        attn_out_buf = from_workspace->attn_out;
        gate_proj = from_workspace->gate_proj;
        up_proj = from_workspace->up_proj;
        gate_activated = from_workspace->gate_activated;
        ffn_out_buf = from_workspace->ffn_out;
    } else {
        local_norm_out.resize(static_cast<size_t>(new_len * hidden));
        local_q_proj.resize(static_cast<size_t>(new_len * q_dim));
        local_k_proj.resize(static_cast<size_t>(new_len * kv_dim));
        local_v_proj.resize(static_cast<size_t>(new_len * kv_dim));
        local_attn_out.resize(static_cast<size_t>(new_len * hidden));
        local_gate_proj.resize(static_cast<size_t>(new_len * cfg.intermediate));
        local_up_proj.resize(static_cast<size_t>(new_len * cfg.intermediate));
        local_gate_activated.resize(static_cast<size_t>(new_len * cfg.intermediate));
        local_ffn_out.resize(static_cast<size_t>(new_len * hidden));
        norm_out = local_norm_out;
        q_proj = local_q_proj;
        k_proj = local_k_proj;
        v_proj = local_v_proj;
        attn_out_buf = local_attn_out;
        gate_proj = local_gate_proj;
        up_proj = local_up_proj;
        gate_activated = local_gate_activated;
        ffn_out_buf = local_ffn_out;
    }

    ops::rmsnorm_into(x, new_len, hidden, lw.attn_norm_weight.raw(), cfg.rmsnorm_epsilon, norm_out);
    check_finite("layer" + std::to_string(layer) + ".a", norm_out);

    ops::linear_no_bias_into(norm_out, new_len, hidden, lw.w_q.raw(), q_dim, q_proj);
    ops::linear_no_bias_into(norm_out, new_len, hidden, lw.w_k.raw(), kv_dim, k_proj);
    ops::linear_no_bias_into(norm_out, new_len, hidden, lw.w_v.raw(), kv_dim, v_proj);

    // q_rope: [n_q_heads][new_len][head_dim] (only this step's new query
    // positions). Not workspace-covered in this Stage 1 -- always local.
    std::vector<float> q_rope(static_cast<size_t>(cfg.n_q_heads * new_len * cfg.head_dim));
    for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
        for (int64_t i = 0; i < new_len; ++i) {
            std::vector<float> vec(static_cast<size_t>(cfg.head_dim));
            for (int64_t d = 0; d < cfg.head_dim; ++d) {
                vec[static_cast<size_t>(d)] = q_proj[static_cast<size_t>(i * q_dim + h * cfg.head_dim + d)];
            }
            std::vector<float> rotated = ops::apply_rope(vec, cos_by_pos[static_cast<size_t>(i)],
                                                           sin_by_pos[static_cast<size_t>(i)]);
            for (int64_t d = 0; d < cfg.head_dim; ++d) {
                q_rope[static_cast<size_t>((h * new_len + i) * cfg.head_dim + d)] = rotated[static_cast<size_t>(d)];
            }
        }
    }

    // Compute this step's new K (with RoPE) and V (no RoPE), then write into the cache
    // at absolute positions [start_position, start_position+new_len) for this layer.
    for (int64_t h = 0; h < cfg.n_kv_heads; ++h) {
        for (int64_t i = 0; i < new_len; ++i) {
            std::vector<float> k_vec(static_cast<size_t>(cfg.head_dim));
            for (int64_t d = 0; d < cfg.head_dim; ++d) {
                k_vec[static_cast<size_t>(d)] = k_proj[static_cast<size_t>(i * kv_dim + h * cfg.head_dim + d)];
            }
            std::vector<float> k_rotated = ops::apply_rope(k_vec, cos_by_pos[static_cast<size_t>(i)],
                                                            sin_by_pos[static_cast<size_t>(i)]);
            cache.write_k(layer, h, start_position + i, k_rotated.data());

            std::vector<float> v_vec(static_cast<size_t>(cfg.head_dim));
            for (int64_t d = 0; d < cfg.head_dim; ++d) {
                v_vec[static_cast<size_t>(d)] = v_proj[static_cast<size_t>(i * kv_dim + h * cfg.head_dim + d)];
            }
            cache.write_v(layer, h, start_position + i, v_vec.data());
        }
    }

    // Attention: query position (start_position + qi) attends to key positions
    // [0, start_position + qi] inclusive -- prior cache content plus this step's
    // own new keys up to and including its own position (rectangular causal mask).
    // Not workspace-covered in this Stage 1 -- always local.
    std::vector<float> context_heads(static_cast<size_t>(cfg.n_q_heads * new_len * cfg.head_dim));
    for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
        const int64_t kv_h = h / group_size;
        for (int64_t qi = 0; qi < new_len; ++qi) {
            const int64_t query_abs = start_position + qi;
            const int64_t key_count = query_abs + 1;  // keys [0, query_abs] inclusive
            std::vector<float> scores(static_cast<size_t>(key_count));
            for (int64_t ki = 0; ki < key_count; ++ki) {
                const float* k_row = cache.k_row(layer, kv_h, ki);
                ops::AccumT acc = ops::AccumT(0);
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    acc += static_cast<ops::AccumT>(q_rope[static_cast<size_t>((h * new_len + qi) * cfg.head_dim + d)]) *
                           static_cast<ops::AccumT>(k_row[d]);
                }
                scores[static_cast<size_t>(ki)] = static_cast<float>(acc) * scale;
            }
            std::vector<float> probs = ops::softmax_last_axis(scores, 1, key_count);
            for (int64_t d = 0; d < cfg.head_dim; ++d) {
                ops::AccumT acc = ops::AccumT(0);
                for (int64_t ki = 0; ki < key_count; ++ki) {
                    const float* v_row = cache.v_row(layer, kv_h, ki);
                    acc += static_cast<ops::AccumT>(probs[static_cast<size_t>(ki)]) *
                           static_cast<ops::AccumT>(v_row[d]);
                }
                context_heads[static_cast<size_t>((h * new_len + qi) * cfg.head_dim + d)] = static_cast<float>(acc);
            }
        }
    }

    std::vector<float> context_flat(static_cast<size_t>(new_len * q_dim));
    for (int64_t i = 0; i < new_len; ++i) {
        for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
            for (int64_t d = 0; d < cfg.head_dim; ++d) {
                context_flat[static_cast<size_t>(i * q_dim + h * cfg.head_dim + d)] =
                    context_heads[static_cast<size_t>((h * new_len + i) * cfg.head_dim + d)];
            }
        }
    }

    ops::linear_no_bias_into(context_flat, new_len, q_dim, lw.w_o.raw(), hidden, attn_out_buf);

    // Residual add -- not workspace-covered in this Stage 1 -- always local.
    std::vector<float> r(x.size());
    for (size_t i = 0; i < x.size(); ++i) r[i] = x[i] + attn_out_buf[i];
    check_finite("layer" + std::to_string(layer) + ".post_attention_residual", r);

    ops::rmsnorm_into(r, new_len, hidden, lw.ffn_norm_weight.raw(), cfg.rmsnorm_epsilon, norm_out);
    ops::linear_no_bias_into(norm_out, new_len, hidden, lw.w_gate.raw(), cfg.intermediate, gate_proj);
    ops::linear_no_bias_into(norm_out, new_len, hidden, lw.w_up.raw(), cfg.intermediate, up_proj);
    ops::silu_into(gate_proj, gate_activated);
    // In-place: gate_activated := silu(gate_proj) * up_proj -- avoids a
    // tenth named buffer for the elementwise product (gate_activated's own
    // pre-multiply value is no longer needed once this line completes).
    for (size_t i = 0; i < gate_activated.size(); ++i) gate_activated[i] *= up_proj[i];
    ops::linear_no_bias_into(gate_activated, new_len, cfg.intermediate, lw.w_down.raw(), hidden, ffn_out_buf);

    // Residual add -- not workspace-covered in this Stage 1 -- always local.
    std::vector<float> y(r.size());
    for (size_t i = 0; i < r.size(); ++i) y[i] = r[i] + ffn_out_buf[i];
    check_finite("layer" + std::to_string(layer) + ".post_ffn_residual", y);

    return y;
}

}  // namespace

std::vector<float> execute_cached_transformer_layer(
    const std::vector<float>& x, int64_t new_len, int64_t start_position,
    const LayerWeights& lw, const ModelConfig& cfg, int64_t layer,
    const std::vector<std::vector<float>>& cos_by_pos,
    const std::vector<std::vector<float>>& sin_by_pos,
    ContiguousAttentionKVStore& cache) {
    return execute_cached_transformer_layer_impl(x, new_len, start_position, lw, cfg, layer, cos_by_pos, sin_by_pos,
                                                  cache, nullptr);
}

std::vector<float> execute_cached_transformer_layer(
    const std::vector<float>& x, int64_t new_len, int64_t start_position,
    const LayerWeights& lw, const ModelConfig& cfg, int64_t layer,
    const std::vector<std::vector<float>>& cos_by_pos,
    const std::vector<std::vector<float>>& sin_by_pos,
    ContiguousAttentionKVStore& cache, ActivationWorkspace& workspace) {
    return execute_cached_transformer_layer_impl(x, new_len, start_position, lw, cfg, layer, cos_by_pos, sin_by_pos,
                                                  cache, &workspace);
}

CachedStepResult forward_cached_step_unsafe_explicit_position(
    const Model& model, ContiguousAttentionKVStore& cache,
    const std::vector<int64_t>& new_token_ids, int64_t start_position) {
    const ModelConfig& cfg = model.config();
    const int64_t new_len = static_cast<int64_t>(new_token_ids.size());
    if (new_len <= 0) {
        throw std::invalid_argument("forward_cached_step_unsafe_explicit_position: new_token_ids must be non-empty");
    }
    if (start_position < 0) {
        throw std::invalid_argument("forward_cached_step_unsafe_explicit_position: start_position must be >= 0");
    }
    if (start_position + new_len > cfg.max_positions) {
        throw std::runtime_error("forward_cached_step_unsafe_explicit_position: would exceed max_positions (" +
                                  std::to_string(cfg.max_positions) + ")");
    }
    if (cache.n_layers() != cfg.n_layers || cache.n_kv_heads() != cfg.n_kv_heads ||
        cache.head_dim() != cfg.head_dim) {
        throw std::runtime_error("forward_cached_step_unsafe_explicit_position: cache shape does not match model config");
    }
    // Explicit, clearly-attributed vocab bounds check before any work happens
    // (independent-review follow-up, Gemini/PR#103 finding 1.1). The frozen
    // non-cached forward() path validates this via validate_forward_inputs();
    // this cached-decode path never called that or an equivalent, so an
    // out-of-vocab token ID would previously reach ops::embedding_lookup's
    // raw table[token*hidden+h] indexing unchecked -- an out-of-bounds heap
    // read. ops::embedding_lookup itself was ALSO hardened with the same
    // check as the last line of defense for every OTHER caller; this check
    // is kept here too, both for a clearer "forward_cached_step"-attributed
    // error message and because checking before any per-layer work begins
    // is this project's established fail-closed-before-mutation discipline.
    for (int64_t token : new_token_ids) {
        if (token < 0 || token >= cfg.vocab) {
            throw std::invalid_argument("forward_cached_step_unsafe_explicit_position: token ID " +
                                        std::to_string(token) + " is outside vocabulary [0, " +
                                        std::to_string(cfg.vocab) + ")");
        }
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
        x = execute_cached_transformer_layer(x, new_len, start_position, lw, cfg, li,
                                              cos_by_pos, sin_by_pos, cache);
    }

    std::vector<float> final_normed = ops::rmsnorm(x, new_len, hidden, model.final_norm_weight.raw(), cfg.rmsnorm_epsilon);
    check_finite("final_normalized_state", final_normed);

    const ResidentView& lm_head = model.effective_lm_head();
    std::vector<float> logits = ops::linear_no_bias(final_normed, new_len, hidden, lm_head.raw(), cfg.vocab);
    check_finite("logits", logits);

    CachedStepResult result;
    result.logits = logits;
    result.selected_token.resize(static_cast<size_t>(new_len));
    for (int64_t i = 0; i < new_len; ++i) {
        std::vector<float> row(logits.begin() + i * cfg.vocab, logits.begin() + (i + 1) * cfg.vocab);
        result.selected_token[static_cast<size_t>(i)] = ops::argmax(row);
    }

    // Auto-commit on success, here and ONLY here (never on an exception
    // path above -- every throw site above returns before this line runs).
    // This is the API-narrowing fix from the commit-API audit: a caller no
    // longer needs (and should not) call cache.set_current_length()
    // manually after this function returns -- doing so was the exact
    // "failed step + set_current_length()" misuse pattern that could make
    // poisoned KV appear committed if a caller mistakenly called it after
    // catching an exception. Folding the commit into the success path
    // itself removes the caller's opportunity to get this wrong for the
    // normal call pattern, without building rollback machinery.
    cache.set_current_length(start_position + new_len);
    return result;
}

CachedStepResult forward_cached_step(const Model& model, ContiguousAttentionKVStore& cache,
                                      const std::vector<int64_t>& new_token_ids,
                                      int64_t start_position) {
    // P5A-RVW-002 fix: the normal/safe entry point requires the caller's
    // claimed position to match the cache's own committed history BEFORE
    // any mutation happens (embedding lookup, RoPE, cache writes). No
    // gap-skip, rewind, or reset-to-zero can silently succeed and
    // auto-commit through this function anymore. Deliberately violating
    // this invariant for fault-injection purposes must use
    // forward_cached_step_unsafe_explicit_position instead.
    if (start_position != cache.current_length()) {
        throw KVCacheError(
            "forward_cached_step: start_position " + std::to_string(start_position) +
            " does not match cache.current_length() " + std::to_string(cache.current_length()) +
            " -- decode must begin exactly at the committed position; use "
            "forward_cached_step_unsafe_explicit_position for deliberate fault injection");
    }
    return forward_cached_step_unsafe_explicit_position(model, cache, new_token_ids, start_position);
}

}  // namespace orcengine
