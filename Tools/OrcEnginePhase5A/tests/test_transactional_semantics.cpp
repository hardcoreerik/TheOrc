// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5A transactional failure semantics. forward_cached_step writes K/V
// per-layer as it goes (see forward_cached.cpp) -- a NaN/Inf check that
// fires on layer L happens AFTER layer L's own K/V have already been
// written into the cache, so a mid-step failure genuinely leaves the cache
// with poisoned (not rolled back) content at the positions that step was
// trying to commit.
//
// Chosen semantics (deliberately the smallest correct policy, not a
// rollback framework): current_length() is caller-driven and is NEVER
// auto-advanced by forward_cached_step itself, including on failure. Any
// cache position >= current_length() is, by contract, not part of the
// committed history and must never be read as context by a correctly
// written caller. A failed step's poisoned writes therefore sit outside
// the trusted region until a subsequent (successful) call at that SAME
// start_position overwrites them -- writes always precede reads for a
// given layer within a single forward_cached_step call, so the retry's
// correct values are in place before anything reads them. This is proven
// below, not just asserted. See docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md.
#include <cmath>
#include <cstdio>
#include <limits>
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
        check(cfg.n_layers >= 2, "fixture has at least 2 layers (needed to isolate a mid-step partial write)");

        const CacheTraceStep& prefill = trace[0];
        const CacheTraceStep& first_decode = trace[1];

        // --- Trusted baseline: correct prefill + correct first decode, never touched by any fault. ---
        ContiguousAttentionKVStore baseline_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        forward_cached_step(fx.model, baseline_cache, prefill.new_tokens, 0);
        baseline_cache.set_current_length(static_cast<int64_t>(prefill.new_tokens.size()));
        CachedStepResult baseline_result = forward_cached_step(fx.model, baseline_cache, first_decode.new_tokens,
                                                                first_decode.start_position);
        std::vector<float> baseline_logits(baseline_result.logits.end() - cfg.vocab, baseline_result.logits.end());

        // --- Fault scenario: corrupt layer 1's w_v with NaN, attempt the SAME decode step. ---
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        forward_cached_step(fx.model, cache, prefill.new_tokens, 0);
        cache.set_current_length(static_cast<int64_t>(prefill.new_tokens.size()));
        const int64_t committed_before_failure = cache.current_length();

        std::vector<float> saved_w_v = fx.model.layers[1].w_v.raw();
        std::vector<float>& mutable_w_v = fx.model.layers[1].w_v.raw();
        for (float& v : mutable_w_v) v = std::numeric_limits<float>::quiet_NaN();

        bool threw = false;
        try {
            forward_cached_step(fx.model, cache, first_decode.new_tokens, first_decode.start_position);
        } catch (const std::exception&) {
            threw = true;
        }
        check(threw, "corrupted layer-1 weights cause forward_cached_step to fail closed (NaN detector fires)");

        // The accounting boundary must NOT have advanced -- the failed step is not committed.
        check(cache.current_length() == committed_before_failure,
              "current_length() unchanged after a mid-step failure (failed step never committed)");

        // Layer 1's cache slot at the attempted position genuinely contains poisoned (NaN) content --
        // this is NOT a rollback, it is poisoned-in-place, protected only by the accounting boundary above.
        const float* poisoned_v = cache.v_row(1, 0, first_decode.start_position);
        bool poisoned = false;
        for (int64_t d = 0; d < cfg.head_dim; ++d) {
            if (std::isnan(poisoned_v[d])) { poisoned = true; break; }
        }
        check(poisoned, "layer 1's cache slot at the attempted position is left genuinely poisoned (not rolled back)");

        // --- Restore weights, retry at the SAME start_position: must succeed and match the untouched baseline. ---
        mutable_w_v = saved_w_v;
        CachedStepResult retry_result = forward_cached_step(fx.model, cache, first_decode.new_tokens,
                                                             first_decode.start_position);
        cache.set_current_length(first_decode.start_position +
                                  static_cast<int64_t>(first_decode.new_tokens.size()));
        std::vector<float> retry_logits(retry_result.logits.end() - cfg.vocab, retry_result.logits.end());
        check(max_abs_diff(retry_logits, baseline_logits) == 0.0f,
              "retry at the same position after a failure overwrites the poison and matches the untouched baseline bit-exactly");

        const float* healed_v = cache.v_row(1, 0, first_decode.start_position);
        bool healed = true;
        for (int64_t d = 0; d < cfg.head_dim; ++d) {
            if (std::isnan(healed_v[d])) { healed = false; break; }
        }
        check(healed, "retry overwrote the poisoned cache slot -- no NaN survives a successful retry");

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("TRANSACTIONAL SEMANTICS HOLD\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
