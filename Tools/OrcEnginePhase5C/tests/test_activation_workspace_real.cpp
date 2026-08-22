// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5C Stage 1 real-model correctness proof: the workspace-driven
// cached-decode path compared against Phase 5A's frozen return-by-value
// reference on the real SmolLM2-135M F32 GGUF (9 query heads, 3 KV heads,
// head_dim=64, group_size=3 -- a different GQA ratio than the synthetic
// fixture, exercising the real per-layer shapes ActivationWorkspace was
// sized from). Covers a multi-token prefill, several autoregressive
// single-token decode steps (feeding each step's own greedy selection
// back in, mirroring test_real_composed_evidence.cpp's established
// pattern), and one deliberate fault-injection case proving the safe
// entry point still rejects a mismatched position and leaves committed
// cache state untouched on the real model. See
// docs/OrcEngine/PHASE5C_ACTIVATION_WORKSPACE_SPEC.md.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

#include "orcengine/activation_workspace.hpp"
#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/forward_cached_workspace.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
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
}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc != 2) throw std::runtime_error("usage: test_activation_workspace_real MODEL.gguf");
        const std::filesystem::path path = argv[1];
        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);
        const Model model = materialize_gguf_model(manifest);
        const ModelConfig& cfg = model.config();
        const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
        const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;

        std::printf("=== Phase 5C Stage 1 (real SmolLM2-135M): workspace-driven decode vs frozen reference ===\n");
        std::printf("n_q_heads=%lld n_kv_heads=%lld head_dim=%lld group_size=%lld n_layers=%lld\n",
                    (long long)cfg.n_q_heads, (long long)cfg.n_kv_heads, (long long)cfg.head_dim,
                    (long long)cfg.group_size(), (long long)cfg.n_layers);
        check(cfg.n_q_heads == 9 && cfg.n_kv_heads == 3, "real model GQA ratio is 9Q/3KV");

        const int64_t max_tokens_per_step = 2;  // covers the 2-token prefill below; every decode step is 1 token
        ActivationWorkspace workspace(cfg.hidden, cfg.intermediate, q_dim, kv_dim, cfg.vocab, max_tokens_per_step);
        const uint64_t capacity = workspace.capacity_bytes();
        check(capacity > 0, "real-model workspace capacity_bytes() > 0");

        ContiguousAttentionKVStore cache_ref(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        ContiguousAttentionKVStore cache_ws(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        // Multi-token prefill shape.
        const std::vector<int64_t> prompt = {1, 5};
        CachedStepResult ref = forward_cached_step(model, cache_ref, prompt, 0);
        CachedStepResult ws = forward_cached_step_workspace(model, cache_ws, prompt, 0, workspace);
        check(max_abs_diff(ref.logits, ws.logits) == 0.0f, "prefill (2 tokens): logits bit-identical");
        check(ref.selected_token == ws.selected_token, "prefill: selected tokens identical");
        check(all_finite(ws.logits), "prefill: workspace-path logits all finite");
        check(cache_ref.current_length() == cache_ws.current_length(), "prefill: cache length identical");
        // test_real_cache_attacks.cpp establishes 28 as the greedy token immediately
        // following prompt {1, 5} (its "next" constant), and 284 as the SECOND
        // selected token after feeding 28 back in -- 28 is the correct value to
        // check here, at the prefill step itself.
        check(ref.selected_token.back() == 28, "prefill reproduces the established next token (28), same as test_real_cache_attacks");

        int64_t position = static_cast<int64_t>(prompt.size());
        std::vector<int64_t> next_ref = {ref.selected_token.back()};
        std::vector<int64_t> next_ws = {ws.selected_token.back()};
        check(next_ref == next_ws, "prefill: next-step input tokens agree between paths");

        // Several autoregressive single-token decode steps.
        constexpr int kDecodeSteps = 6;
        uint64_t prev_peak = workspace.peak_bytes();
        for (int step = 0; step < kDecodeSteps; ++step) {
            CachedStepResult r = forward_cached_step(model, cache_ref, next_ref, position);
            CachedStepResult w = forward_cached_step_workspace(model, cache_ws, next_ws, position, workspace);
            const std::string label = "decode step " + std::to_string(step) + " (single token, position " +
                                      std::to_string(position) + ")";
            check(max_abs_diff(r.logits, w.logits) == 0.0f, label + ": logits bit-identical");
            check(r.selected_token == w.selected_token, label + ": selected tokens identical");
            check(all_finite(w.logits), label + ": workspace-path logits all finite");
            check(cache_ref.current_length() == cache_ws.current_length(), label + ": cache length identical");
            check(workspace.capacity_bytes() == capacity, label + ": workspace capacity unchanged (no growth)");
            check(workspace.peak_bytes() >= prev_peak, label + ": peak_bytes() non-decreasing");
            prev_peak = workspace.peak_bytes();

            position += 1;
            next_ref = {r.selected_token.back()};
            next_ws = {w.selected_token.back()};
            check(next_ref == next_ws, label + ": next-step input tokens agree between paths");
        }

        std::printf("\n=== Real-model workspace accounting ===\n");
        // 1 prefill + kDecodeSteps decode steps, each calling layer_buffers() n_layers
        // times + final_norm_buffer() once + logits_buffer() once.
        const uint64_t expected_total_calls =
            static_cast<uint64_t>(1 + kDecodeSteps) * static_cast<uint64_t>(cfg.n_layers + 2);
        check(workspace.total_prepare_calls() == expected_total_calls,
              "total_prepare_calls() == (1 + decode_steps) * (n_layers + 2) == " + std::to_string(expected_total_calls));
        check(workspace.reuse_count() == expected_total_calls - 1, "reuse_count() == total_prepare_calls() - 1");
        check(workspace.peak_bytes() <= workspace.capacity_bytes(), "peak_bytes() never exceeds capacity_bytes()");
        check(workspace.capacity_bytes() == capacity, "capacity_bytes() unchanged across the whole real-model run");

        std::printf("\n=== Real-model failure and retry behavior ===\n");
        {
            const int64_t committed_before = cache_ws.current_length();
            bool rejected = false;
            try {
                forward_cached_step_workspace(model, cache_ws, next_ws, position + 1, workspace);  // +1 gap
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "real model: mismatched start_position (+1 gap) rejected by the safe workspace entry point");
            check(cache_ws.current_length() == committed_before, "real model: cache length unchanged after a rejected call");

            CachedStepResult r_retry = forward_cached_step(model, cache_ref, next_ref, position);
            CachedStepResult w_retry = forward_cached_step_workspace(model, cache_ws, next_ws, position, workspace);
            check(max_abs_diff(r_retry.logits, w_retry.logits) == 0.0f,
                  "real model: correct retry after a rejected call still matches reference exactly");
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) {
            std::printf("ALL REAL-MODEL CHECKS PASSED\n");
            return 0;
        }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
