// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 6 Stage 1, Gate 6: bounded per-layer localization of the
// internal DEV-derived F32-vs-Q8_0 tolerance failure on
// `holdout_quick_fox` (tolerance 0.973504, observed max_abs_error
// 1.079983, per phase6_q8_0_comparison.exe's committed run).
//
// This is a Phase-6-only diagnostic. It does NOT modify any frozen
// Phase 1-5C file -- it calls the EXISTING public per-layer seam,
// execute_cached_transformer_layer() (documented in forward_cached.hpp
// as the shared per-layer math both the fully-resident and virtualized
// decode paths route through), one layer at a time, for both the F32
// and Q8_0 models, reconstructing the exact same preamble/epilogue
// sequence forward_cached_step_unsafe_explicit_position() already uses
// (embedding lookup, RoPE table construction, final RMSNorm, lm_head
// projection) purely by calling existing public ops:: functions -- no
// new transformer math, no copy of frozen logic into a second
// implementation.
//
// Reports, per layer: max absolute difference and RMSE between the
// F32-path and Q8_0-path hidden state, so the layer at which Q8_0
// quantization error first becomes material (rather than a smooth,
// expected accumulation) can be identified.
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/ops.hpp"

using namespace orcengine;

namespace {

struct DiffStats {
    float max_abs = 0.0f;
    double rmse = 0.0;
};

DiffStats diff(const std::vector<float>& a, const std::vector<float>& b) {
    DiffStats s;
    double sq_sum = 0.0;
    for (size_t i = 0; i < a.size(); ++i) {
        const float d = std::fabs(a[i] - b[i]);
        s.max_abs = std::max(s.max_abs, d);
        sq_sum += static_cast<double>(d) * static_cast<double>(d);
    }
    s.rmse = std::sqrt(sq_sum / static_cast<double>(a.size()));
    return s;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: phase6_holdout_quick_fox_layer_localization F32.gguf Q8_0.gguf\n");
        return 2;
    }
    try {
        const Model f32_model = materialize_gguf_model(map_llama_model(index_gguf(argv[1])));
        const Model q8_model = materialize_gguf_model(map_llama_model(index_gguf(argv[2])));
        const ModelConfig& cfg = f32_model.config();

        // holdout_quick_fox: "The quick brown fox", tokenized once via the
        // pinned llama-tokenize.exe -- identical token_ids already committed
        // throughout Phase 6's evidence (phase6_q8_0_f32_comparison_evidence_v3.jsonl).
        const std::vector<int64_t> token_ids = {504, 2365, 6354, 16438};
        const int64_t new_len = static_cast<int64_t>(token_ids.size());
        const int64_t hidden = cfg.hidden;

        std::printf("=== Gate 6: holdout_quick_fox per-layer F32-vs-Q8_0 localization ===\n");
        std::printf("model: vocab=%lld hidden=%lld n_layers=%lld\n",
                    (long long)cfg.vocab, (long long)cfg.hidden, (long long)cfg.n_layers);

        // --- Weight-magnitude scan: tests whether any per-layer error
        // discontinuity correlates with that layer's own weights having an
        // unusually large dynamic range (which would legitimately produce a
        // coarser Q8_0 per-block quantization step there -- Q8_0's scale is
        // amax_in_block/127, so a larger amax means a larger absolute
        // quantization step for that block), as opposed to a dispatch/
        // layout defect specific to that layer. Uses only the already-
        // materialized F32 model's own resident tensors -- no new I/O. ---
        std::printf("\n%-6s %14s\n", "layer", "max|weight|(F32)");
        auto max_abs_of = [](const std::vector<float>& v) {
            float m = 0.0f;
            for (float x : v) m = std::max(m, std::fabs(x));
            return m;
        };
        for (int64_t li = 0; li < cfg.n_layers; ++li) {
            const LayerWeights& lw = f32_model.layers[static_cast<size_t>(li)];
            float layer_max = 0.0f;
            layer_max = std::max(layer_max, max_abs_of(lw.w_q.raw()));
            layer_max = std::max(layer_max, max_abs_of(lw.w_k.raw()));
            layer_max = std::max(layer_max, max_abs_of(lw.w_v.raw()));
            layer_max = std::max(layer_max, max_abs_of(lw.w_o.raw()));
            layer_max = std::max(layer_max, max_abs_of(lw.w_gate.raw()));
            layer_max = std::max(layer_max, max_abs_of(lw.w_up.raw()));
            layer_max = std::max(layer_max, max_abs_of(lw.w_down.raw()));
            std::printf("%-6lld %14.6f\n", (long long)li, layer_max);
        }

        // --- Reconstruct the exact preamble forward_cached_step_unsafe_
        // explicit_position() already uses, via the same public ops::
        // functions, for both models. ---
        std::vector<float> x_f32 = ops::embedding_lookup(f32_model.token_embedding.raw(), hidden, token_ids);
        std::vector<float> x_q8 = ops::embedding_lookup(q8_model.token_embedding.raw(), hidden, token_ids);
        {
            const DiffStats s = diff(x_f32, x_q8);
            std::printf("embedding lookup    max_abs=%.6f rmse=%.6f\n", s.max_abs, s.rmse);
        }

        std::vector<std::vector<float>> cos_by_pos(static_cast<size_t>(new_len)), sin_by_pos(static_cast<size_t>(new_len));
        for (int64_t i = 0; i < new_len; ++i) {
            ops::rope_cos_sin(i, cfg.head_dim, static_cast<double>(cfg.rope_theta),
                              cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
        }

        ContiguousAttentionKVStore f32_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        ContiguousAttentionKVStore q8_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        std::printf("\n%-6s %14s %14s\n", "layer", "max_abs_diff", "rmse");
        for (int64_t li = 0; li < cfg.n_layers; ++li) {
            const LayerWeights& f32_lw = f32_model.layers[static_cast<size_t>(li)];
            const LayerWeights& q8_lw = q8_model.layers[static_cast<size_t>(li)];
            x_f32 = execute_cached_transformer_layer(x_f32, new_len, /*start_position=*/0, f32_lw, cfg, li,
                                                     cos_by_pos, sin_by_pos, f32_cache);
            x_q8 = execute_cached_transformer_layer(x_q8, new_len, /*start_position=*/0, q8_lw, cfg, li,
                                                    cos_by_pos, sin_by_pos, q8_cache);
            const DiffStats s = diff(x_f32, x_q8);
            std::printf("%-6lld %14.6f %14.6f\n", (long long)li, s.max_abs, s.rmse);
        }

        const std::vector<float> final_f32 = ops::rmsnorm(x_f32, new_len, hidden, f32_model.final_norm_weight.raw(), cfg.rmsnorm_epsilon);
        const std::vector<float> final_q8 = ops::rmsnorm(x_q8, new_len, hidden, q8_model.final_norm_weight.raw(), cfg.rmsnorm_epsilon);
        {
            const DiffStats s = diff(final_f32, final_q8);
            std::printf("\nfinal RMSNorm       max_abs=%.6f rmse=%.6f\n", s.max_abs, s.rmse);
        }

        const std::vector<float> logits_f32 = ops::linear_no_bias(final_f32, new_len, hidden, f32_model.effective_lm_head().raw(), cfg.vocab);
        const std::vector<float> logits_q8 = ops::linear_no_bias(final_q8, new_len, hidden, q8_model.effective_lm_head().raw(), cfg.vocab);
        // Compare only the LAST position's logits (the row this prompt's
        // Checkpoint 3 comparison already reports on) -- matches the
        // existing phase6_q8_0_comparison.cpp scope exactly.
        const std::vector<float> last_f32(logits_f32.end() - cfg.vocab, logits_f32.end());
        const std::vector<float> last_q8(logits_q8.end() - cfg.vocab, logits_q8.end());
        {
            const DiffStats s = diff(last_f32, last_q8);
            std::printf("output projection   max_abs=%.6f rmse=%.6f (last position, full vocab -- "
                        "should match phase6_q8_0_comparison.exe's committed 1.079983)\n", s.max_abs, s.rmse);
        }

        std::printf("\nselected tokens: F32=%lld Q8_0=%lld\n",
                    (long long)ops::argmax(last_f32), (long long)ops::argmax(last_q8));
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
