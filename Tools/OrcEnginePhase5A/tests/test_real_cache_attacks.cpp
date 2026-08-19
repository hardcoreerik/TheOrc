// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Real-model (SmolLM2-135M: 9 query heads, 3 KV heads, head_dim=64) fault
// attacks -- the synthetic Fixture-C fixture only exercises a 4Q/2KV ratio;
// this proves the same fault classes are detected at the real model's
// different GQA ratio, RoPE dimensionality, and layer count. See
// docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md.
//
// Cases that deliberately violate the position-must-equal-current_length()
// invariant (wrong position, RoPE deltas, reset-to-zero, isolation, one
// capacity-boundary leg) use forward_cached_step_unsafe_explicit_position
// -- the normal forward_cached_step now rejects those before any mutation
// (P5A-RVW-002 fix), so testing that the RESULTING divergence is still
// detectable requires the explicit low-level seam.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"

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
        if (argc != 2) throw std::runtime_error("usage: test_real_cache_attacks MODEL.gguf");
        const std::filesystem::path path = argv[1];
        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);
        const Model model = materialize_gguf_model(manifest);
        const ModelConfig& cfg = model.config();

        std::printf("=== Real-model (n_q_heads=%lld n_kv_heads=%lld head_dim=%lld group_size=%lld) fault attacks ===\n",
                    (long long)cfg.n_q_heads, (long long)cfg.n_kv_heads, (long long)cfg.head_dim, (long long)cfg.group_size());
        check(cfg.n_q_heads == 9 && cfg.n_kv_heads == 3, "real model GQA ratio is 9Q/3KV as expected (not the synthetic 4Q/2KV)");

        const std::vector<int64_t> initial = {1, 5};
        const std::vector<int64_t> next = {28};  // established next token from the 4-way differential

        // Baseline: correct prefill + one correct decode step -- both via the SAFE API.
        ContiguousAttentionKVStore baseline_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        forward_cached_step(model, baseline_cache, initial, 0);
        // forward_cached_step auto-commits current_length() on success.
        CachedStepResult baseline = forward_cached_step(model, baseline_cache, next,
                                                         static_cast<int64_t>(initial.size()));
        std::vector<float> baseline_last(baseline.logits.end() - cfg.vocab, baseline.logits.end());
        check(baseline.selected_token.back() == 284, "baseline real cached step reproduces established next token (284)");

        // 0. The SAFE API now rejects a position that doesn't match current_length()
        //    before any mutation happens -- this is the P5A-RVW-002 closure itself.
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c, initial, 0);
            const int64_t committed_before = c.current_length();
            bool rejected = false;
            try {
                forward_cached_step(model, c, next, static_cast<int64_t>(initial.size()) + 1);  // gap +1
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "0a. SAFE API rejects a +1 position gap before any mutation (real model)");
            check(c.current_length() == committed_before, "0b. rejected gap leaves current_length() unchanged (real model)");
        }
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c, initial, 0);
            const int64_t committed_before = c.current_length();
            bool rejected = false;
            try {
                forward_cached_step(model, c, next, static_cast<int64_t>(initial.size()) + 100);  // large gap
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "0c. SAFE API rejects a large position gap (real model)");
            check(c.current_length() == committed_before, "0d. rejected large gap leaves current_length() unchanged (real model)");
        }
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c, initial, 0);
            const int64_t committed_before = c.current_length();
            bool rejected = false;
            try {
                forward_cached_step(model, c, next, 0);  // rewind to 0 on a nonempty cache
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "0e. SAFE API rejects rewind to position 0 on a nonempty cache (real model)");
            check(c.current_length() == committed_before, "0f. rejected rewind leaves current_length() unchanged (real model)");
        }

        // 1. Wrong cache position (off-by-one) on the real model -- via the explicit
        //    low-level seam, proving the resulting divergence is still detectable
        //    for callers that deliberately bypass the SAFE API's guard.
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c, initial, 0);
            CachedStepResult wrong = forward_cached_step_unsafe_explicit_position(
                model, c, next, static_cast<int64_t>(initial.size()) + 1);
            std::vector<float> wrong_last(wrong.logits.end() - cfg.vocab, wrong.logits.end());
            check(max_abs_diff(wrong_last, baseline_last) > 1e-3f,
                  "1. wrong cache position (+1) diverges from correct reference (unsafe seam, real model)");
        }

        // 2. Real GQA head corruption: corrupt kv_head 0 (shared by query heads 0,1,2 -- group_size=3),
        //    confirming the attack surfaces at the model's ACTUAL 3:1 ratio, not the synthetic 2:1.
        //    Position itself stays correct here -- only cache CONTENT is corrupted -- so the SAFE API applies.
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c, initial, 0);
            std::vector<float> corrupt(static_cast<size_t>(cfg.head_dim), 999.0f);
            c.write_k(0, 0, 0, corrupt.data());
            CachedStepResult corrupted = forward_cached_step(model, c, next, static_cast<int64_t>(initial.size()));
            std::vector<float> corrupted_last(corrupted.logits.end() - cfg.vocab, corrupted.logits.end());
            check(max_abs_diff(corrupted_last, baseline_last) > 1e-3f,
                  "2. corrupted kv_head 0 (shared by 3 query heads via 9Q/3KV GQA) diverges");
        }

        // 3. RoPE absolute-position attacks: p-1, p+1, and reset-to-zero, via the unsafe seam.
        for (int64_t delta : {-1, 1}) {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c, initial, 0);
            const int64_t bad_position = static_cast<int64_t>(initial.size()) + delta;
            if (bad_position < 0) continue;
            CachedStepResult r = forward_cached_step_unsafe_explicit_position(model, c, next, bad_position);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f,
                  "3. real model: RoPE position delta=" + std::to_string(delta) +
                  " diverges from correct reference (unsafe seam)");
        }
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c, initial, 0);
            CachedStepResult r = forward_cached_step_unsafe_explicit_position(model, c, next, 0);  // reset to position 0
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f,
                  "3b. real model: RoPE position reset to 0 diverges from correct reference (unsafe seam)");
        }

        // 4. Capacity boundary at the real model's validated max_positions (8192), tested
        //    deterministically without requiring full-context inference. Isolated via the
        //    unsafe seam so this tests ONLY the capacity check, not the position-match guard.
        check(cfg.max_positions == 8192, "real model max_positions is 8192 as expected");
        {
            bool rejected = false;
            try {
                ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                forward_cached_step_unsafe_explicit_position(model, c, {1}, cfg.max_positions);  // exactly at capacity
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "4a. real model: decode at exactly max_positions fails closed (capacity check, unsafe seam)");
        }
        {
            bool ok = false;
            try {
                ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                forward_cached_step_unsafe_explicit_position(model, c, {1}, cfg.max_positions - 1);  // last valid position
                ok = true;
            } catch (const std::exception&) {
                ok = false;
            }
            check(ok, "4b. real model: decode at max_positions-1 (last valid position) succeeds (capacity check, unsafe seam)");
        }

        // 5. Cross-context isolation with enough real steps that stale state would matter.
        //    c2 is fresh/never-prefilled, so decoding at initial.size() is a deliberate
        //    position violation -- uses the unsafe seam, matching its original intent.
        {
            ContiguousAttentionKVStore c1(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(model, c1, initial, 0);
            CachedStepResult r1 = forward_cached_step(model, c1, next, static_cast<int64_t>(initial.size()));

            ContiguousAttentionKVStore c2(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);  // fresh, never prefilled
            CachedStepResult r2 = forward_cached_step_unsafe_explicit_position(
                model, c2, next, static_cast<int64_t>(initial.size()));
            std::vector<float> r1_last(r1.logits.end() - cfg.vocab, r1.logits.end());
            std::vector<float> r2_last(r2.logits.end() - cfg.vocab, r2.logits.end());
            check(max_abs_diff(r1_last, r2_last) > 1e-3f,
                  "5. real model: fresh (never-prefilled) context diverges from correctly-prefilled one -- "
                  "no cross-context leakage (unsafe seam)");
        }

        // 6. The SAFE API rejects decoding a fresh, never-prefilled context at a nonzero
        //    position outright -- a stronger guarantee than divergence-only detection.
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            bool rejected = false;
            try {
                forward_cached_step(model, c, next, static_cast<int64_t>(initial.size()));
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "6. SAFE API rejects decoding a fresh context at a nonzero position outright (real model)");
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("ALL REAL-MODEL ATTACKS DETECTED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
