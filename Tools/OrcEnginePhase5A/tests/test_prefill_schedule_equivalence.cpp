// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Proves layer-major and token-major prefill scheduling are semantically
// equivalent BEFORE picking one for the virtualized composed implementation
// (see docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md's "Active Phase-5A
// completion gate"). Both orders are expressed here using the SAME shared
// execute_cached_transformer_layer function (via forward_cached_step, which
// is layer-major -- one batched call spans all layers for the whole
// new_len batch, materializing/attending once per layer for the whole
// prompt) vs a token-major emulation (one forward_cached_step call PER
// PROMPT TOKEN, new_len=1 each, looping tokens outermost) -- which is
// exactly what "load every layer for every token" means when a layer's
// materialization is tied to one forward_cached_step call.
//
// If a real virtualized materializer were layer-major, each layer would be
// materialized exactly once for the whole prompt; if token-major, each
// layer would be (re-)materialized once per prompt token. This test proves
// the two orders produce IDENTICAL logits and cache content -- the choice
// between them is then purely a weight-I/O efficiency question, not a
// correctness one, and is recorded as such.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include "orcengine/cache_trace_loader.hpp"
#include "orcengine/context.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward_cached.hpp"

using namespace orcengine;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}
float max_abs_diff(const std::vector<float>& a, const std::vector<float>& b) {
    float m = 0.0f;
    for (size_t i = 0; i < a.size(); ++i) m = std::max(m, std::fabs(a[i] - b[i]));
    return m;
}
}  // namespace

int main(int argc, char** argv) {
    try {
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase5a";
        LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_cache_weights.txt");
        std::vector<CacheTraceStep> trace = load_cache_trace(fixtures_dir + "/fixture_cache_trace_python.txt");
        const ModelConfig& cfg = fx.model.config();
        const std::vector<int64_t>& prompt = trace[0].new_tokens;  // the prefill step's prompt
        check(prompt.size() >= 2, "fixture prefill prompt has at least 2 tokens (needed to distinguish schedules)");

        // --- Layer-major: one batched forward_cached_step call over the whole prompt. ---
        ContiguousAttentionKVStore layer_major_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        CachedStepResult layer_major = forward_cached_step(fx.model, layer_major_cache, prompt, 0);  // auto-commits

        // --- Token-major emulation: one forward_cached_step call PER TOKEN, new_len=1 each. ---
        ContiguousAttentionKVStore token_major_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        std::vector<float> token_major_logits(static_cast<size_t>(prompt.size()) * static_cast<size_t>(cfg.vocab));
        std::vector<int64_t> token_major_selected(prompt.size());
        for (size_t i = 0; i < prompt.size(); ++i) {
            CachedStepResult step = forward_cached_step(fx.model, token_major_cache, {prompt[i]},
                                                         static_cast<int64_t>(i));  // auto-commits
            std::copy(step.logits.begin(), step.logits.end(),
                      token_major_logits.begin() + static_cast<size_t>(i) * static_cast<size_t>(cfg.vocab));
            token_major_selected[i] = step.selected_token[0];
        }

        // --- Compare complete logits, selected tokens, and full cache content. ---
        const float diff = max_abs_diff(layer_major.logits, token_major_logits);
        check(diff == 0.0f, "layer-major and token-major prefill produce bit-identical complete logits (max_abs_diff=" +
              std::to_string(diff) + ")");

        bool selected_match = layer_major.selected_token == token_major_selected;
        check(selected_match, "layer-major and token-major prefill select identical tokens at every position");

        bool cache_match = true;
        for (int64_t li = 0; li < cfg.n_layers && cache_match; ++li) {
            for (int64_t h = 0; h < cfg.n_kv_heads && cache_match; ++h) {
                for (int64_t p = 0; p < static_cast<int64_t>(prompt.size()) && cache_match; ++p) {
                    const float* k1 = layer_major_cache.k_row(li, h, p);
                    const float* k2 = token_major_cache.k_row(li, h, p);
                    const float* v1 = layer_major_cache.v_row(li, h, p);
                    const float* v2 = token_major_cache.v_row(li, h, p);
                    for (int64_t d = 0; d < cfg.head_dim; ++d) {
                        if (k1[d] != k2[d] || v1[d] != v2[d]) { cache_match = false; break; }
                    }
                }
            }
        }
        check(cache_match, "layer-major and token-major prefill produce bit-identical cache content at every layer/head/position");

        std::printf("\n=== Decision ===\n");
        std::printf(
            "Semantic equivalence proven (%s). Layer-major is chosen for the virtualized\n"
            "composed implementation: it materializes each transformer layer exactly ONCE\n"
            "per prefill call regardless of prompt length, vs token-major's one\n"
            "materialization PER LAYER PER TOKEN. Under Phase-4's streaming/virtualized\n"
            "weight model this is a real, measurable weight-I/O reduction, not just an\n"
            "intuition -- see docs/OrcEngine/DECISION_LOG.md's composition ADR for the\n"
            "recorded decision and this test as its evidence.\n",
            (g_failures == 0) ? "yes" : "NO -- schedules diverge, do not treat as equivalent");

        if (g_failures == 0) { std::printf("ALL CHECKS PASSED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
