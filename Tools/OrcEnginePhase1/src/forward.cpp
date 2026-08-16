// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Mirrors Tools/OrcEnginePhase0/oracle/model.py's forward() step-for-step
// (the 12-step block sequence in docs/OrcEngine/PHASE_0_ARCHITECTURE_PROFILE.md).
// Full-prefix only -- no KV cache reuse, matching Phase 0's own forward()
// (cache equivalence is explicitly out of scope until Phase 3).
#include "orcengine/forward.hpp"

#include <cmath>
#include <stdexcept>

#include "orcengine/diagnostics.hpp"
#include "orcengine/ops.hpp"

namespace orcengine {

namespace {

void put_tap(ForwardResult& result, const std::string& name, std::vector<int64_t> dims,
             std::vector<float> data) {
    if (diagnostics::check_tap(name, data)) {
        throw std::runtime_error("forward: NaN/Inf detected in tap '" + name +
                                  "' -- failing closed rather than propagating poisoned data");
    }
    result.taps[name] = Tap{std::move(dims), std::move(data)};
}

}  // namespace

ForwardResult forward(const Model& model, const std::vector<int64_t>& token_ids) {
    const ModelConfig& cfg = model.config();
    const int64_t seq = static_cast<int64_t>(token_ids.size());
    const int64_t hidden = cfg.hidden;
    const int64_t group_size = cfg.group_size();
    const float scale = 1.0f / std::sqrt(static_cast<float>(cfg.head_dim));

    ForwardResult result;

    std::vector<float> x = ops::embedding_lookup(model.token_embedding.raw(), hidden, token_ids);
    put_tap(result, "input_embedding", {seq, hidden}, x);

    std::vector<std::vector<float>> cos_by_pos(static_cast<size_t>(seq)), sin_by_pos(static_cast<size_t>(seq));
    for (int64_t p = 0; p < seq; ++p) {
        ops::rope_cos_sin(p, cfg.head_dim, static_cast<double>(cfg.rope_theta),
                           cos_by_pos[static_cast<size_t>(p)], sin_by_pos[static_cast<size_t>(p)]);
    }

    for (int64_t li = 0; li < cfg.n_layers; ++li) {
        const LayerWeights& lw = model.layers[static_cast<size_t>(li)];
        const std::string prefix = "layer" + std::to_string(li) + ".";

        std::vector<float> a = ops::rmsnorm(x, seq, hidden, lw.attn_norm_weight.raw(), cfg.rmsnorm_epsilon);
        put_tap(result, prefix + "pre_attention_normalized_state", {seq, hidden}, a);

        const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
        const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;
        std::vector<float> q_flat = ops::linear_no_bias(a, seq, hidden, lw.w_q.raw(), q_dim);
        std::vector<float> k_flat = ops::linear_no_bias(a, seq, hidden, lw.w_k.raw(), kv_dim);
        std::vector<float> v_flat = ops::linear_no_bias(a, seq, hidden, lw.w_v.raw(), kv_dim);
        put_tap(result, prefix + "q_projection", {seq, q_dim}, q_flat);
        put_tap(result, prefix + "k_projection", {seq, kv_dim}, k_flat);
        put_tap(result, prefix + "v_projection", {seq, kv_dim}, v_flat);

        // q_after_rope: [n_q_heads, seq, head_dim]; k_after_rope: [n_kv_heads, seq, head_dim].
        std::vector<float> q_rope(static_cast<size_t>(cfg.n_q_heads * seq * cfg.head_dim));
        std::vector<float> k_rope(static_cast<size_t>(cfg.n_kv_heads * seq * cfg.head_dim));
        for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
            for (int64_t p = 0; p < seq; ++p) {
                std::vector<float> vec(static_cast<size_t>(cfg.head_dim));
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    vec[static_cast<size_t>(d)] = q_flat[static_cast<size_t>(p * q_dim + h * cfg.head_dim + d)];
                }
                std::vector<float> rotated = ops::apply_rope(vec, cos_by_pos[static_cast<size_t>(p)],
                                                               sin_by_pos[static_cast<size_t>(p)]);
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    q_rope[static_cast<size_t>((h * seq + p) * cfg.head_dim + d)] = rotated[static_cast<size_t>(d)];
                }
            }
        }
        for (int64_t h = 0; h < cfg.n_kv_heads; ++h) {
            for (int64_t p = 0; p < seq; ++p) {
                std::vector<float> vec(static_cast<size_t>(cfg.head_dim));
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    vec[static_cast<size_t>(d)] = k_flat[static_cast<size_t>(p * kv_dim + h * cfg.head_dim + d)];
                }
                std::vector<float> rotated = ops::apply_rope(vec, cos_by_pos[static_cast<size_t>(p)],
                                                               sin_by_pos[static_cast<size_t>(p)]);
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    k_rope[static_cast<size_t>((h * seq + p) * cfg.head_dim + d)] = rotated[static_cast<size_t>(d)];
                }
            }
        }
        put_tap(result, prefix + "q_after_rope", {cfg.n_q_heads, seq, cfg.head_dim}, q_rope);
        put_tap(result, prefix + "k_after_rope", {cfg.n_kv_heads, seq, cfg.head_dim}, k_rope);

        // v_heads: [n_kv_heads, seq, head_dim] (no RoPE on V).
        std::vector<float> v_heads(static_cast<size_t>(cfg.n_kv_heads * seq * cfg.head_dim));
        for (int64_t h = 0; h < cfg.n_kv_heads; ++h) {
            for (int64_t p = 0; p < seq; ++p) {
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    v_heads[static_cast<size_t>((h * seq + p) * cfg.head_dim + d)] =
                        v_flat[static_cast<size_t>(p * kv_dim + h * cfg.head_dim + d)];
                }
            }
        }

        std::vector<float> attn_probs(static_cast<size_t>(cfg.n_q_heads * seq * seq));
        // context_heads: [n_q_heads, seq, head_dim].
        std::vector<float> context_heads(static_cast<size_t>(cfg.n_q_heads * seq * cfg.head_dim));
        for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
            const int64_t kv_h = h / group_size;
            std::vector<float> scores(static_cast<size_t>(seq * seq));
            for (int64_t qi = 0; qi < seq; ++qi) {
                for (int64_t ki = 0; ki < seq; ++ki) {
                    double acc = 0.0;
                    for (int64_t d = 0; d < cfg.head_dim; ++d) {
                        acc += static_cast<double>(q_rope[static_cast<size_t>((h * seq + qi) * cfg.head_dim + d)]) *
                               static_cast<double>(k_rope[static_cast<size_t>((kv_h * seq + ki) * cfg.head_dim + d)]);
                    }
                    scores[static_cast<size_t>(qi * seq + ki)] = static_cast<float>(acc) * scale;
                }
            }
            std::vector<float> masked = ops::causal_mask(scores, seq, seq);
            std::vector<float> probs = ops::softmax_last_axis(masked, seq, seq);
            for (int64_t qi = 0; qi < seq; ++qi) {
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    double acc = 0.0;
                    for (int64_t ki = 0; ki < seq; ++ki) {
                        acc += static_cast<double>(probs[static_cast<size_t>(qi * seq + ki)]) *
                               static_cast<double>(v_heads[static_cast<size_t>((kv_h * seq + ki) * cfg.head_dim + d)]);
                    }
                    context_heads[static_cast<size_t>((h * seq + qi) * cfg.head_dim + d)] = static_cast<float>(acc);
                }
            }
            for (size_t i = 0; i < probs.size(); ++i) {
                attn_probs[static_cast<size_t>(h) * probs.size() + i] = probs[i];
            }
        }
        put_tap(result, prefix + "attention_probabilities", {cfg.n_q_heads, seq, seq}, attn_probs);

        // context_flat: [seq, n_q_heads*head_dim] (transpose heads back to the front-facing layout).
        std::vector<float> context_flat(static_cast<size_t>(seq * q_dim));
        for (int64_t p = 0; p < seq; ++p) {
            for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    context_flat[static_cast<size_t>(p * q_dim + h * cfg.head_dim + d)] =
                        context_heads[static_cast<size_t>((h * seq + p) * cfg.head_dim + d)];
                }
            }
        }
        put_tap(result, prefix + "attention_output_before_projection", {seq, q_dim}, context_flat);

        std::vector<float> attn_out = ops::linear_no_bias(context_flat, seq, q_dim, lw.w_o.raw(), hidden);
        put_tap(result, prefix + "attention_output_after_projection", {seq, hidden}, attn_out);

        std::vector<float> r(x.size());
        for (size_t i = 0; i < x.size(); ++i) r[i] = x[i] + attn_out[i];
        put_tap(result, prefix + "post_attention_residual", {seq, hidden}, r);

        std::vector<float> f = ops::rmsnorm(r, seq, hidden, lw.ffn_norm_weight.raw(), cfg.rmsnorm_epsilon);
        put_tap(result, prefix + "pre_ffn_normalized_state", {seq, hidden}, f);

        std::vector<float> gate = ops::linear_no_bias(f, seq, hidden, lw.w_gate.raw(), cfg.intermediate);
        std::vector<float> up = ops::linear_no_bias(f, seq, hidden, lw.w_up.raw(), cfg.intermediate);
        put_tap(result, prefix + "gate_projection", {seq, cfg.intermediate}, gate);
        put_tap(result, prefix + "up_projection", {seq, cfg.intermediate}, up);

        std::vector<float> gate_act = ops::silu(gate);
        std::vector<float> activated(gate.size());
        for (size_t i = 0; i < gate.size(); ++i) activated[i] = gate_act[i] * up[i];
        put_tap(result, prefix + "activated_gated_product", {seq, cfg.intermediate}, activated);

        std::vector<float> ffn = ops::linear_no_bias(activated, seq, cfg.intermediate, lw.w_down.raw(), hidden);
        put_tap(result, prefix + "down_projection", {seq, hidden}, ffn);

        std::vector<float> y(r.size());
        for (size_t i = 0; i < r.size(); ++i) y[i] = r[i] + ffn[i];
        put_tap(result, prefix + "post_ffn_residual", {seq, hidden}, y);

        x = y;
    }

    std::vector<float> final_normed = ops::rmsnorm(x, seq, hidden, model.final_norm_weight.raw(), cfg.rmsnorm_epsilon);
    put_tap(result, "final_normalized_state", {seq, hidden}, final_normed);

    const ResidentView& lm_head = model.effective_lm_head();
    std::vector<float> logits = ops::linear_no_bias(final_normed, seq, hidden, lm_head.raw(), cfg.vocab);
    put_tap(result, "logits", {seq, cfg.vocab}, logits);
    result.logits = logits;

    result.selected_token.resize(static_cast<size_t>(seq));
    for (int64_t p = 0; p < seq; ++p) {
        std::vector<float> row(logits.begin() + p * cfg.vocab, logits.begin() + (p + 1) * cfg.vocab);
        result.selected_token[static_cast<size_t>(p)] = ops::argmax(row);
    }

    return result;
}

}  // namespace orcengine
