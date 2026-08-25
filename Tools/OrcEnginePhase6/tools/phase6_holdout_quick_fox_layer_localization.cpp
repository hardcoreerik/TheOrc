// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 6 Stage 1, Gate 6 (Codex/Grok remediation round 5, Gate 3):
// causally useful per-layer localization of the internal DEV-derived
// F32-vs-Q8_0 tolerance failure on `holdout_quick_fox` (tolerance
// 0.973504, observed max_abs_error 1.079983), extended with:
//   A. per-POSITION metrics (not aggregate-only), a config-equality
//      guard, and shape checks before comparing vectors;
//   B. a same-cumulative-input, four-way weight-vs-state decomposition
//      at the two layers implicated by round-4's trace (11 and 28),
//      isolating "quantized WEIGHTS acting on an F32 input" from
//      "F32 weights acting on an already-diverged Q8 input";
//   C. the same bounded analysis run on THREE prompts in one process
//      (one model load) -- the failing holdout, one DEV prompt already
//      below tolerance, and one agreeing HOLDOUT control -- to check
//      whether layers 11/28 are unique to the failing prompt or a
//      model-wide amplification point.
//
// This is a Phase-6-only diagnostic. It does NOT modify any frozen
// Phase 1-5C file -- it calls the EXISTING public per-layer seam,
// execute_cached_transformer_layer() (documented in forward_cached.hpp
// as the shared math both the fully-resident and virtualized decode
// paths route through), reconstructing the same preamble/epilogue
// forward_cached_step_unsafe_explicit_position() already uses via
// existing public ops:: functions -- no new transformer math, no copy
// of frozen logic into a second implementation.
#include <cmath>
#include <cstdio>
#include <stdexcept>
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

int g_selftest_failures = 0;
void check_and_report(const std::string& name, bool ok) {
    std::printf("[%s] %s\n", ok ? "PASS" : "FAIL", name.c_str());
    if (!ok) ++g_selftest_failures;
}

// Codex remediation round 5, Gate 3A: per-position, not aggregate-only.
// `values` is [new_len, hidden]-shaped (row-major); returns one DiffStats
// per position so the specific position driving a layer's jump can be
// identified, plus the aggregate (whole-vector) stats as a secondary view.
std::vector<DiffStats> diff_per_position(const std::vector<float>& a, const std::vector<float>& b,
                                         int64_t new_len, int64_t row_width) {
    if (a.size() != b.size() || a.size() != static_cast<size_t>(new_len * row_width)) {
        throw std::runtime_error("diff_per_position: shape mismatch (a.size=" + std::to_string(a.size()) +
                                 " b.size=" + std::to_string(b.size()) + " expected=" +
                                 std::to_string(new_len * row_width) + ")");
    }
    std::vector<DiffStats> per_pos(static_cast<size_t>(new_len));
    for (int64_t p = 0; p < new_len; ++p) {
        double sq_sum = 0.0;
        float max_abs = 0.0f;
        for (int64_t c = 0; c < row_width; ++c) {
            const size_t idx = static_cast<size_t>(p * row_width + c);
            const float d = std::fabs(a[idx] - b[idx]);
            max_abs = std::max(max_abs, d);
            sq_sum += static_cast<double>(d) * static_cast<double>(d);
        }
        per_pos[static_cast<size_t>(p)] = {max_abs, std::sqrt(sq_sum / static_cast<double>(row_width))};
    }
    return per_pos;
}

DiffStats diff_aggregate(const std::vector<float>& a, const std::vector<float>& b) {
    if (a.size() != b.size()) {
        throw std::runtime_error("diff_aggregate: shape mismatch (a.size=" + std::to_string(a.size()) +
                                 " b.size=" + std::to_string(b.size()) + ")");
    }
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

// Codex remediation round 6, Gate 1B: the ORIGINAL Part B "interaction"
// heuristic computed `combined.max_abs - (weight.max_abs + state.max_abs)`
// -- comparing three SEPARATELY-located scalar maxima is not a valid
// interaction measurement, since the isolated weight-effect maximum and
// the isolated state-effect maximum can occur at entirely different
// coordinates from each other and from the combined effect's own
// maximum. The mathematically correct per-element factorial interaction
// residual is `out_qq[i] - out_qf[i] - out_fq[i] + out_ff[i]` -- the
// residual between the actual combined change and the coordinate-wise
// sum of the two isolated changes, evaluated at every coordinate
// independently, not via separately-located scalar maxima.
struct LocatedStats {
    DiffStats stats;      // vs. an implicit zero baseline (this IS the effect vector's own magnitude)
    int64_t max_position = -1;
    int64_t max_channel = -1;
};

// Computes elementwise `effect[i] = a[i] - b[i]` and returns its
// magnitude stats (max_abs, rmse) PLUS the exact (position, channel)
// coordinate where the max occurred, so two different effects' maxima
// can never be silently conflated as if co-located.
LocatedStats effect_vector_stats(const std::vector<float>& a, const std::vector<float>& b,
                                 int64_t new_len, int64_t row_width) {
    if (a.size() != b.size() || a.size() != static_cast<size_t>(new_len * row_width)) {
        throw std::runtime_error("effect_vector_stats: shape mismatch");
    }
    LocatedStats result;
    double sq_sum = 0.0;
    for (int64_t p = 0; p < new_len; ++p) {
        for (int64_t c = 0; c < row_width; ++c) {
            const size_t idx = static_cast<size_t>(p * row_width + c);
            const float d = std::fabs(a[idx] - b[idx]);
            if (d > result.stats.max_abs) {
                result.stats.max_abs = d;
                result.max_position = p;
                result.max_channel = c;
            }
            sq_sum += static_cast<double>(d) * static_cast<double>(d);
        }
    }
    result.stats.rmse = std::sqrt(sq_sum / static_cast<double>(a.size()));
    return result;
}

// The true four-term factorial interaction residual, elementwise:
// interaction[i] = qq[i] - qf[i] - fq[i] + ff[i]. Returns its own
// magnitude stats + location, exactly like effect_vector_stats -- this
// is NOT a difference of two already-computed DiffStats scalars.
LocatedStats factorial_interaction_stats(const std::vector<float>& out_ff, const std::vector<float>& out_qf,
                                         const std::vector<float>& out_fq, const std::vector<float>& out_qq,
                                         int64_t new_len, int64_t row_width) {
    const size_t n = out_ff.size();
    if (out_qf.size() != n || out_fq.size() != n || out_qq.size() != n ||
        n != static_cast<size_t>(new_len * row_width)) {
        throw std::runtime_error("factorial_interaction_stats: shape mismatch");
    }
    LocatedStats result;
    double sq_sum = 0.0;
    for (int64_t p = 0; p < new_len; ++p) {
        for (int64_t c = 0; c < row_width; ++c) {
            const size_t idx = static_cast<size_t>(p * row_width + c);
            const float interaction = out_qq[idx] - out_qf[idx] - out_fq[idx] + out_ff[idx];
            const float d = std::fabs(interaction);
            if (d > result.stats.max_abs) {
                result.stats.max_abs = d;
                result.max_position = p;
                result.max_channel = c;
            }
            sq_sum += static_cast<double>(d) * static_cast<double>(d);
        }
    }
    result.stats.rmse = std::sqrt(sq_sum / static_cast<double>(n));
    return result;
}

// Per-position (not whole-vector) breakdown of the factorial interaction
// residual, at layers 11/28 specifically, per instruction.
std::vector<DiffStats> factorial_interaction_per_position(
    const std::vector<float>& out_ff, const std::vector<float>& out_qf,
    const std::vector<float>& out_fq, const std::vector<float>& out_qq,
    int64_t new_len, int64_t row_width) {
    std::vector<DiffStats> per_pos(static_cast<size_t>(new_len));
    for (int64_t p = 0; p < new_len; ++p) {
        double sq_sum = 0.0;
        float max_abs = 0.0f;
        for (int64_t c = 0; c < row_width; ++c) {
            const size_t idx = static_cast<size_t>(p * row_width + c);
            const float interaction = out_qq[idx] - out_qf[idx] - out_fq[idx] + out_ff[idx];
            const float d = std::fabs(interaction);
            max_abs = std::max(max_abs, d);
            sq_sum += static_cast<double>(d) * static_cast<double>(d);
        }
        per_pos[static_cast<size_t>(p)] = {max_abs, std::sqrt(sq_sum / static_cast<double>(row_width))};
    }
    return per_pos;
}

void require_configs_equal(const ModelConfig& f32_cfg, const ModelConfig& q8_cfg) {
    // Codex remediation round 5, Gate 3A: fail closed BEFORE using
    // `f32_cfg` for both paths (as the original version implicitly did)
    // if the two models' configs actually differ -- silently reusing
    // one model's config for the other would misinterpret shapes.
    const bool equal = f32_cfg.vocab == q8_cfg.vocab && f32_cfg.hidden == q8_cfg.hidden &&
                       f32_cfg.intermediate == q8_cfg.intermediate && f32_cfg.n_layers == q8_cfg.n_layers &&
                       f32_cfg.n_q_heads == q8_cfg.n_q_heads && f32_cfg.n_kv_heads == q8_cfg.n_kv_heads &&
                       f32_cfg.head_dim == q8_cfg.head_dim && f32_cfg.max_positions == q8_cfg.max_positions &&
                       f32_cfg.rmsnorm_epsilon == q8_cfg.rmsnorm_epsilon && f32_cfg.rope_theta == q8_cfg.rope_theta;
    if (!equal) {
        throw std::runtime_error("F32 and Q8_0 model configs differ -- refusing to reuse one config for both paths");
    }
}

struct PromptCase {
    std::string id;
    std::string role;  // "FAILING", "DEV_below_tolerance", "HOLDOUT_agreeing"
    std::vector<int64_t> token_ids;
};

// Same 3 prompts already pinned throughout Phase 6 evidence
// (phase6_q8_0_f32_comparison_evidence_v3.jsonl) -- reused, not
// re-tokenized. holdout_quick_fox is the failing prompt this
// localization is about; dev_code_snippet is a DEV prompt whose F32-
// vs-Q8_0 max_abs_error (0.486752) is well below the derived tolerance
// (0.973504); holdout_hello_world is a HOLDOUT prompt that AGREES and
// is also below tolerance (0.487471) -- together these test whether
// layers 11/28 are unique to the failing prompt or a model-wide
// amplification point common to all three.
const std::vector<PromptCase> kControlAndFailingPrompts = {
    {"holdout_quick_fox", "FAILING", {504, 2365, 6354, 16438}},
    {"dev_code_snippet", "DEV_below_tolerance", {1604, 803, 24, 81, 28, 278, 727, 1003}},
    {"holdout_hello_world", "HOLDOUT_agreeing", {19556, 905, 28, 451, 314}},
};

// Layers implicated by round-4's aggregate trace on holdout_quick_fox.
const std::vector<int64_t> kImplicatedLayers = {11, 28};

// Codex remediation round 6, Gate 1B: hostile self-check proving the
// factorial-interaction formula does NOT silently conflate two
// separately-located effect maxima -- the exact failure mode of the
// OLD scalar `combined.max_abs - (weight.max_abs + state.max_abs)`
// heuristic this replaces. Constructs a tiny 2-position, 4-channel
// synthetic case where the weight effect's own maximum and the state
// effect's own maximum occur at DIFFERENT coordinates, and proves the
// interaction residual is computed per-coordinate (not from the two
// scalar maxima) by checking it against a hand-computed reference.
void run_interaction_formula_hostile_selftest() {
    std::printf("=== Gate 1B self-test: factorial interaction residual formula ===\n");
    const int64_t new_len = 2, row_width = 4;
    // out_ff: baseline, all zeros.
    const std::vector<float> out_ff(static_cast<size_t>(new_len * row_width), 0.0f);
    // out_qf (weight effect = qf-ff): large at position 0, channel 0 only.
    std::vector<float> out_qf(static_cast<size_t>(new_len * row_width), 0.0f);
    out_qf[0] = 10.0f;  // position 0, channel 0
    // out_fq (state effect = fq-ff): large at position 1, channel 3 only --
    // DELIBERATELY a different coordinate from the weight effect's own max.
    std::vector<float> out_fq(static_cast<size_t>(new_len * row_width), 0.0f);
    out_fq[7] = 6.0f;  // position 1, channel 3
    // out_qq (combined = qq-ff): superposition of both effects PLUS a
    // deliberate extra +3.0 interaction term at position 0, channel 1 (a
    // THIRD coordinate, distinct from both isolated maxima) so the
    // interaction residual at that specific coordinate is exactly +3.0,
    // and the residual at the two isolated-effect coordinates is exactly
    // 0 (since qq there equals qf/fq plus ff exactly, no extra term).
    std::vector<float> out_qq = out_ff;
    out_qq[0] = out_qf[0];                 // position 0, channel 0: no interaction here
    out_qq[7] = out_fq[7];                 // position 1, channel 3: no interaction here
    out_qq[1] = 3.0f;                      // position 0, channel 1: pure interaction term

    // Old (WRONG) heuristic would have reported:
    //   weight.max_abs=10.0 (at [0,0]), state.max_abs=6.0 (at [1,3]),
    //   combined.max_abs=10.0 (at [0,0] -- since 10.0 > 6.0 > 3.0),
    //   "interaction_gap" = 10.0 - (10.0+6.0) = -6.0 -- a number with NO
    //   relationship to the actual +3.0 interaction term this test
    //   constructed, because it never looks at [0,1] at all.
    const LocatedStats interaction = factorial_interaction_stats(out_ff, out_qf, out_fq, out_qq, new_len, row_width);
    const bool located_correctly = interaction.max_position == 0 && interaction.max_channel == 1;
    const bool value_correct = std::fabs(interaction.stats.max_abs - 3.0f) < 1e-6f;
    check_and_report("hostile self-test: interaction residual correctly located at [pos=0,ch=1], "
                     "not conflated with the weight-effect maximum at [pos=0,ch=0] or the "
                     "state-effect maximum at [pos=1,ch=3]",
                     located_correctly && value_correct);
    std::printf("  measured: max_abs=%.6f at [pos=%lld,ch=%lld] (expected: 3.0 at [pos=0,ch=1])\n\n",
                interaction.stats.max_abs, (long long)interaction.max_position, (long long)interaction.max_channel);
}

}  // namespace

int main(int argc, char** argv) {
    run_interaction_formula_hostile_selftest();
    if (g_selftest_failures != 0) {
        std::fprintf(stderr, "Gate 1B self-test FAILED -- aborting before any model I/O\n");
        return 1;
    }
    if (argc != 3) {
        std::fprintf(stderr, "usage: phase6_holdout_quick_fox_layer_localization F32.gguf Q8_0.gguf\n");
        return 2;
    }
    try {
        const Model f32_model = materialize_gguf_model(map_llama_model(index_gguf(argv[1])));
        const Model q8_model = materialize_gguf_model(map_llama_model(index_gguf(argv[2])));
        require_configs_equal(f32_model.config(), q8_model.config());
        const ModelConfig& cfg = f32_model.config();
        const int64_t hidden = cfg.hidden;

        std::printf("=== Gate 6 (round 5, Gate 3): per-position + weight/state-decomposed localization ===\n");
        std::printf("model: vocab=%lld hidden=%lld n_layers=%lld (F32/Q8_0 configs verified equal)\n",
                    (long long)cfg.vocab, (long long)cfg.hidden, (long long)cfg.n_layers);

        for (const PromptCase& pc : kControlAndFailingPrompts) {
            const int64_t new_len = static_cast<int64_t>(pc.token_ids.size());
            std::printf("\n\n########## prompt: %s (role=%s) ##########\n", pc.id.c_str(), pc.role.c_str());

            // --- Part A: full 30-layer trace, per-position + aggregate. ---
            std::vector<float> x_f32 = ops::embedding_lookup(f32_model.token_embedding.raw(), hidden, pc.token_ids);
            std::vector<float> x_q8 = ops::embedding_lookup(q8_model.token_embedding.raw(), hidden, pc.token_ids);

            std::vector<std::vector<float>> cos_by_pos(static_cast<size_t>(new_len)), sin_by_pos(static_cast<size_t>(new_len));
            for (int64_t i = 0; i < new_len; ++i) {
                ops::rope_cos_sin(i, cfg.head_dim, static_cast<double>(cfg.rope_theta),
                                  cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
            }

            ContiguousAttentionKVStore f32_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            ContiguousAttentionKVStore q8_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

            // Saved cumulative inputs at the START of each implicated
            // layer, for Part B's same-input decomposition below.
            std::vector<float> x_f32_before_implicated, x_q8_before_implicated;
            int64_t decomposition_layer = kImplicatedLayers.empty() ? -1 : kImplicatedLayers.front();

            std::printf("%-6s %16s %16s   %s\n", "layer", "max_abs(agg)", "rmse(agg)", "per-position max_abs");
            for (int64_t li = 0; li < cfg.n_layers; ++li) {
                if (li == decomposition_layer) {
                    x_f32_before_implicated = x_f32;
                    x_q8_before_implicated = x_q8;
                }
                const LayerWeights& f32_lw = f32_model.layers[static_cast<size_t>(li)];
                const LayerWeights& q8_lw = q8_model.layers[static_cast<size_t>(li)];
                x_f32 = execute_cached_transformer_layer(x_f32, new_len, 0, f32_lw, cfg, li, cos_by_pos, sin_by_pos, f32_cache);
                x_q8 = execute_cached_transformer_layer(x_q8, new_len, 0, q8_lw, cfg, li, cos_by_pos, sin_by_pos, q8_cache);

                const DiffStats agg = diff_aggregate(x_f32, x_q8);
                const auto per_pos = diff_per_position(x_f32, x_q8, new_len, hidden);
                std::printf("%-6lld %16.6f %16.6f   [", (long long)li, agg.max_abs, agg.rmse);
                for (int64_t p = 0; p < new_len; ++p) {
                    std::printf("%.4f%s", per_pos[static_cast<size_t>(p)].max_abs, p + 1 < new_len ? "," : "");
                }
                std::printf("]\n");
            }

            const std::vector<float> final_f32 = ops::rmsnorm(x_f32, new_len, hidden, f32_model.final_norm_weight.raw(), cfg.rmsnorm_epsilon);
            const std::vector<float> final_q8 = ops::rmsnorm(x_q8, new_len, hidden, q8_model.final_norm_weight.raw(), cfg.rmsnorm_epsilon);
            const std::vector<float> logits_f32 = ops::linear_no_bias(final_f32, new_len, hidden, f32_model.effective_lm_head().raw(), cfg.vocab);
            const std::vector<float> logits_q8 = ops::linear_no_bias(final_q8, new_len, hidden, q8_model.effective_lm_head().raw(), cfg.vocab);
            const std::vector<float> last_f32(logits_f32.end() - cfg.vocab, logits_f32.end());
            const std::vector<float> last_q8(logits_q8.end() - cfg.vocab, logits_q8.end());
            const DiffStats logit_stats = diff_aggregate(last_f32, last_q8);
            std::printf("final-position output-projection: max_abs=%.6f rmse=%.6f "
                        "(full vocab, last position only)\n", logit_stats.max_abs, logit_stats.rmse);
            std::printf("selected tokens: F32=%lld Q8_0=%lld\n",
                        (long long)ops::argmax(last_f32), (long long)ops::argmax(last_q8));

            // --- Part B: same-cumulative-input, four-way weight-vs-state
            // decomposition at each implicated layer. Uses the cumulative
            // states captured just before that layer ran in Part A above.
            // Each of the 4 combinations gets its OWN fresh KV cache (a
            // single-layer, single-call forward through JUST that layer,
            // not a re-run of the whole stack) so the 4 calls do not
            // interfere with each other or with Part A's own caches. ---
            for (int64_t layer : kImplicatedLayers) {
                std::printf("\n--- Part B: same-input weight/state decomposition at layer %lld (prompt %s) ---\n",
                            (long long)layer, pc.id.c_str());
                std::vector<float> x_before_f32, x_before_q8;
                if (layer == kImplicatedLayers.front()) {
                    x_before_f32 = x_f32_before_implicated;
                    x_before_q8 = x_q8_before_implicated;
                } else {
                    // Re-derive the cumulative input for a later implicated
                    // layer by re-running Part A's trace up to that layer
                    // (cheap: this model/prompt combination is tiny).
                    std::vector<float> rx_f32 = ops::embedding_lookup(f32_model.token_embedding.raw(), hidden, pc.token_ids);
                    std::vector<float> rx_q8 = ops::embedding_lookup(q8_model.token_embedding.raw(), hidden, pc.token_ids);
                    ContiguousAttentionKVStore rf32_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                    ContiguousAttentionKVStore rq8_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                    for (int64_t li = 0; li < layer; ++li) {
                        rx_f32 = execute_cached_transformer_layer(rx_f32, new_len, 0, f32_model.layers[static_cast<size_t>(li)],
                                                                  cfg, li, cos_by_pos, sin_by_pos, rf32_cache);
                        rx_q8 = execute_cached_transformer_layer(rx_q8, new_len, 0, q8_model.layers[static_cast<size_t>(li)],
                                                                 cfg, li, cos_by_pos, sin_by_pos, rq8_cache);
                    }
                    x_before_f32 = std::move(rx_f32);
                    x_before_q8 = std::move(rx_q8);
                }

                const LayerWeights& f32_lw = f32_model.layers[static_cast<size_t>(layer)];
                const LayerWeights& q8_lw = q8_model.layers[static_cast<size_t>(layer)];

                ContiguousAttentionKVStore cache_ff(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                ContiguousAttentionKVStore cache_qf(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                ContiguousAttentionKVStore cache_fq(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                ContiguousAttentionKVStore cache_qq(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

                // 1: F32 weights, F32 input (baseline)
                const std::vector<float> out_ff = execute_cached_transformer_layer(
                    x_before_f32, new_len, 0, f32_lw, cfg, layer, cos_by_pos, sin_by_pos, cache_ff);
                // 2: Q8 weights, F32 input (isolates WEIGHT quantization effect)
                const std::vector<float> out_qf = execute_cached_transformer_layer(
                    x_before_f32, new_len, 0, q8_lw, cfg, layer, cos_by_pos, sin_by_pos, cache_qf);
                // 3: F32 weights, Q8 input (isolates INCOMING-STATE effect)
                const std::vector<float> out_fq = execute_cached_transformer_layer(
                    x_before_q8, new_len, 0, f32_lw, cfg, layer, cos_by_pos, sin_by_pos, cache_fq);
                // 4: Q8 weights, Q8 input (cumulative combined -- matches Part A)
                const std::vector<float> out_qq = execute_cached_transformer_layer(
                    x_before_q8, new_len, 0, q8_lw, cfg, layer, cos_by_pos, sin_by_pos, cache_qq);

                // Codex remediation round 6, Gate 1B: the four elementwise
                // effect vectors, each with its OWN independently-located
                // maximum (never confused with another effect's maximum).
                const LocatedStats weight_effect = effect_vector_stats(out_qf, out_ff, new_len, hidden);       // qf - ff
                const LocatedStats state_effect = effect_vector_stats(out_fq, out_ff, new_len, hidden);        // fq - ff
                const LocatedStats combined_effect = effect_vector_stats(out_qq, out_ff, new_len, hidden);     // qq - ff
                const LocatedStats interaction = factorial_interaction_stats(out_ff, out_qf, out_fq, out_qq, new_len, hidden);
                const auto interaction_per_pos = factorial_interaction_per_position(out_ff, out_qf, out_fq, out_qq, new_len, hidden);
                const auto weight_per_pos = diff_per_position(out_qf, out_ff, new_len, hidden);
                const auto state_per_pos = diff_per_position(out_fq, out_ff, new_len, hidden);
                const auto combined_per_pos = diff_per_position(out_qq, out_ff, new_len, hidden);

                std::printf("  isolated WEIGHT effect  (out_qf-out_ff, same F32 input):  max_abs=%.6f rmse=%.6f "
                            "at [pos=%lld,ch=%lld]\n",
                            weight_effect.stats.max_abs, weight_effect.stats.rmse,
                            (long long)weight_effect.max_position, (long long)weight_effect.max_channel);
                std::printf("  isolated STATE effect   (out_fq-out_ff, same F32 weights): max_abs=%.6f rmse=%.6f "
                            "at [pos=%lld,ch=%lld]\n",
                            state_effect.stats.max_abs, state_effect.stats.rmse,
                            (long long)state_effect.max_position, (long long)state_effect.max_channel);
                std::printf("  combined effect         (out_qq-out_ff, matches Part A):  max_abs=%.6f rmse=%.6f "
                            "at [pos=%lld,ch=%lld]\n",
                            combined_effect.stats.max_abs, combined_effect.stats.rmse,
                            (long long)combined_effect.max_position, (long long)combined_effect.max_channel);
                std::printf("  FACTORIAL INTERACTION RESIDUAL (out_qq-out_qf-out_fq+out_ff): max_abs=%.6f rmse=%.6f "
                            "at [pos=%lld,ch=%lld] (this is the mathematically valid interaction measure -- "
                            "NOT a difference of separately-located scalar maxima)\n",
                            interaction.stats.max_abs, interaction.stats.rmse,
                            (long long)interaction.max_position, (long long)interaction.max_channel);
                std::printf("  per-position max_abs: weight=[");
                for (int64_t p = 0; p < new_len; ++p) std::printf("%.4f%s", weight_per_pos[static_cast<size_t>(p)].max_abs, p + 1 < new_len ? "," : "");
                std::printf("] state=[");
                for (int64_t p = 0; p < new_len; ++p) std::printf("%.4f%s", state_per_pos[static_cast<size_t>(p)].max_abs, p + 1 < new_len ? "," : "");
                std::printf("] combined=[");
                for (int64_t p = 0; p < new_len; ++p) std::printf("%.4f%s", combined_per_pos[static_cast<size_t>(p)].max_abs, p + 1 < new_len ? "," : "");
                std::printf("] interaction=[");
                for (int64_t p = 0; p < new_len; ++p) std::printf("%.4f%s", interaction_per_pos[static_cast<size_t>(p)].max_abs, p + 1 < new_len ? "," : "");
                std::printf("]\n");
            }
        }
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
