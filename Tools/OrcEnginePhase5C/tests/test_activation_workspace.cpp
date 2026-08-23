// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5C Stage 1 correctness proof: the workspace-driven cached-decode
// path (forward_cached_step_workspace, backed by ActivationWorkspace) is
// compared, step for step, against Phase 5A's frozen return-by-value
// reference (forward_cached_step) on the SAME synthetic Fixture C trace
// used by Phase 5A's own test_cached_decode.cpp -- prefill (multi-token)
// followed by several single-token decode steps, i.e. both a multi-token
// prefill shape and a single-token decode shape, as required. Two
// independently constructed caches receive the SAME token sequence via
// the two different call paths; their logits, selected tokens, and
// committed KV-cache content must all agree. Also proves the workspace
// itself: fixed capacity, no growth after warm-up, truthful reuse/byte
// accounting, and correct failure/retry behavior with cache state left
// unchanged on a rejected call. See docs/OrcEngine/PHASE5C_ACTIVATION_WORKSPACE_SPEC.md.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <string>
#include <vector>

#include "orcengine/activation_workspace.hpp"
#include "orcengine/cache_trace_loader.hpp"
#include "orcengine/context.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/forward_cached_workspace.hpp"

using namespace orcengine;

namespace {

int g_failures = 0;

void check(bool condition, const std::string& name) {
    std::printf("[%s] %s\n", condition ? "PASS" : "FAIL", name.c_str());
    if (!condition) ++g_failures;
}

float max_abs_diff(const std::vector<float>& a, const std::vector<float>& b) {
    if (a.size() != b.size()) return std::numeric_limits<float>::infinity();
    float m = 0.0f;
    for (size_t i = 0; i < a.size(); ++i) m = std::max(m, std::fabs(a[i] - b[i]));
    return m;
}

bool all_finite(const std::vector<float>& v) {
    for (float f : v) {
        if (!std::isfinite(f)) return false;
    }
    return true;
}

bool cache_matches(const ContiguousAttentionKVStore& a, const ContiguousAttentionKVStore& b, int64_t n_layers,
                   int64_t n_kv_heads, int64_t head_dim, int64_t len) {
    for (int64_t layer = 0; layer < n_layers; ++layer) {
        for (int64_t h = 0; h < n_kv_heads; ++h) {
            for (int64_t p = 0; p < len; ++p) {
                const float* ka = a.k_row(layer, h, p);
                const float* kb = b.k_row(layer, h, p);
                const float* va = a.v_row(layer, h, p);
                const float* vb = b.v_row(layer, h, p);
                for (int64_t d = 0; d < head_dim; ++d) {
                    if (ka[d] != kb[d]) return false;
                    if (va[d] != vb[d]) return false;
                }
            }
        }
    }
    return true;
}

}  // namespace

int main(int argc, char** argv) {
    try {
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase5a";
        LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_cache_weights.txt");
        std::vector<CacheTraceStep> trace = load_cache_trace(fixtures_dir + "/fixture_cache_trace_python.txt");
        const ModelConfig& cfg = fx.model.config();
        const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
        const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;

        int64_t max_tokens_per_step = 1;
        for (const auto& step : trace) {
            max_tokens_per_step = std::max<int64_t>(max_tokens_per_step, static_cast<int64_t>(step.new_tokens.size()));
        }

        std::printf("=== Phase 5C Stage 1: workspace-driven decode vs frozen reference (%zu steps, max_tokens_per_step=%lld) ===\n",
                    trace.size(), static_cast<long long>(max_tokens_per_step));

        ActivationWorkspace workspace(cfg.hidden, cfg.intermediate, q_dim, kv_dim, cfg.vocab, max_tokens_per_step);

        check(workspace.hidden() == cfg.hidden && workspace.intermediate() == cfg.intermediate &&
                  workspace.q_dim() == q_dim && workspace.kv_dim() == kv_dim && workspace.vocab() == cfg.vocab &&
                  workspace.max_tokens_per_step() == max_tokens_per_step,
              "workspace dimensions match model config");
        check(workspace.capacity_bytes() > 0, "workspace capacity_bytes() > 0 after construction");
        check(workspace.current_bytes() == 0 && workspace.peak_bytes() == 0 && workspace.reuse_count() == 0 &&
                  workspace.total_prepare_calls() == 0,
              "workspace usage counters all zero before first accessor call");
        const uint64_t capacity_before_use = workspace.capacity_bytes();

        ContiguousAttentionKVStore cache_ref(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        ContiguousAttentionKVStore cache_ws(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        bool sequence_diverged = false;
        std::vector<int64_t> tokens;
        uint64_t prev_peak = 0;

        for (size_t step_idx = 0; step_idx < trace.size(); ++step_idx) {
            const CacheTraceStep& step = trace[step_idx];
            if (!sequence_diverged && tokens != step.seq_before) {
                check(false, "step " + std::to_string(step_idx) + ": input sequence diverged before this step");
                sequence_diverged = true;
            }
            if (sequence_diverged) break;

            CachedStepResult ref = forward_cached_step(fx.model, cache_ref, step.new_tokens, step.start_position);
            CachedStepResult ws =
                forward_cached_step_workspace(fx.model, cache_ws, step.new_tokens, step.start_position, workspace);

            const std::string label = "step " + std::to_string(step_idx) + " (" + step.kind + ")";

            check(ref.logits.size() == ws.logits.size() && max_abs_diff(ref.logits, ws.logits) == 0.0f,
                  label + ": workspace-path logits bit-identical to reference-path logits");
            check(ref.selected_token == ws.selected_token, label + ": selected tokens identical");
            check(all_finite(ws.logits), label + ": workspace-path logits all finite");

            const int64_t committed_len = step.start_position + static_cast<int64_t>(step.new_tokens.size());
            check(cache_matches(cache_ref, cache_ws, cfg.n_layers, cfg.n_kv_heads, cfg.head_dim, committed_len),
                  label + ": committed KV-cache content identical between reference and workspace paths");
            check(cache_ref.current_length() == cache_ws.current_length(),
                  label + ": committed KV-cache length identical");

            check(workspace.capacity_bytes() == capacity_before_use,
                  label + ": workspace capacity_bytes() unchanged (no growth)");
            check(workspace.peak_bytes() >= prev_peak, label + ": workspace peak_bytes() non-decreasing");
            prev_peak = workspace.peak_bytes();

            if (step.kind == "prefill") {
                tokens.insert(tokens.end(), step.new_tokens.begin(), step.new_tokens.end());
            }
            tokens.push_back(ref.selected_token.back());
        }

        std::printf("\n=== Workspace accounting ===\n");
        // Every step calls layer_buffers() n_layers times, then final_norm_buffer()
        // once, then logits_buffer() once: (n_layers + 2) accessor calls per step,
        // across `trace.size()` steps -- every call after the very first is "reuse".
        const uint64_t expected_total_calls =
            static_cast<uint64_t>(trace.size()) * static_cast<uint64_t>(cfg.n_layers + 2);
        check(workspace.total_prepare_calls() == expected_total_calls,
              "total_prepare_calls() == steps * (n_layers + 2) == " + std::to_string(expected_total_calls));
        check(workspace.reuse_count() == expected_total_calls - 1,
              "reuse_count() == total_prepare_calls() - 1 (every call after the first reused storage)");
        check(workspace.peak_bytes() <= workspace.capacity_bytes(),
              "peak_bytes() never exceeds capacity_bytes()");
        check(workspace.capacity_bytes() == capacity_before_use,
              "capacity_bytes() unchanged across the whole run (fixed at construction, never regrown)");

        std::printf("\n=== Failure and retry behavior ===\n");
        {
            // A rejected call (mismatched start_position) must not mutate the
            // committed cache state, and a correct retry afterward must still
            // match the reference exactly.
            ContiguousAttentionKVStore c_ref(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            ContiguousAttentionKVStore c_ws(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            ActivationWorkspace ws2(cfg.hidden, cfg.intermediate, q_dim, kv_dim, cfg.vocab, max_tokens_per_step);

            const CacheTraceStep& prefill = trace[0];
            forward_cached_step(fx.model, c_ref, prefill.new_tokens, 0);
            forward_cached_step_workspace(fx.model, c_ws, prefill.new_tokens, 0, ws2);
            const int64_t len_before = c_ws.current_length();

            bool rejected = false;
            try {
                forward_cached_step_workspace(fx.model, c_ws, trace[1].new_tokens, trace[1].start_position + 1, ws2);
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "mismatched start_position rejected by the safe workspace entry point");
            check(c_ws.current_length() == len_before, "cache length unchanged after a rejected call");

            CachedStepResult ref_next = forward_cached_step(fx.model, c_ref, trace[1].new_tokens, trace[1].start_position);
            CachedStepResult ws_next =
                forward_cached_step_workspace(fx.model, c_ws, trace[1].new_tokens, trace[1].start_position, ws2);
            check(max_abs_diff(ref_next.logits, ws_next.logits) == 0.0f,
                  "correct retry after a rejected call still matches reference exactly");
        }
        {
            // new_len exceeding the workspace's max_tokens_per_step must be rejected.
            // Asserted unconditionally: the fixture's own prefill size is checked
            // first so this case cannot silently pass by being vacuous (a bare
            // `rejected || len <= 1` OR would let a shrunk fixture mask a real
            // regression, the same false-pass shape OE-ADR-038 fixed elsewhere).
            check(trace[0].new_tokens.size() > 1,
                  "precondition: fixture prefill has more than 1 token (so the rejection below is exercised)");
            ActivationWorkspace tiny(cfg.hidden, cfg.intermediate, q_dim, kv_dim, cfg.vocab, 1);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            bool rejected = false;
            try {
                forward_cached_step_workspace(fx.model, c, trace[0].new_tokens, 0, tiny);
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "new_len exceeding workspace.max_tokens_per_step() rejected");
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) {
            std::printf("ALL CHECKS PASSED\n");
            return 0;
        }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
