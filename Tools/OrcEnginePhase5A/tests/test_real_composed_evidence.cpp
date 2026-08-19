// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Closure evidence for P5A-RVW-004/005/006/011 and Stage 11/12 of the
// freeze-closure pass: this is the first REAL-GGUF-backed Path-C evidence
// registered as an actual CTest (previously only exercised manually via
// gguf_cached_forward_virtualized.cpp, never under Debug/strict/ASan).
// Runs against whichever real artifact is passed on argv[1] -- registered
// once for the explicit-output artifact and once for the tied artifact
// (Stage 7), so this single binary closes both gaps.
//
// Unlike real_5way_composed_differential.py's printed-but-unenforced
// bit-identical claim, this test ASSERTS A/B/C bit-identical logits
// directly (fails the process, not just a print statement) -- the
// enforced half of the P5A-RVW-004 fix; the Python script's own gate is
// hardened separately.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/forward_cached_virtualized.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/gguf_source.hpp"
#include "orcengine/streaming.hpp"

using namespace orcengine;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}
bool bit_identical(const std::vector<float>& a, const std::vector<float>& b) {
    return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin());
}
}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc != 2) throw std::runtime_error("usage: test_real_composed_evidence MODEL.gguf");
        const std::filesystem::path path = argv[1];
        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);
        const int64_t steps = 4;
        const std::vector<int64_t> initial = {1, 5};

        std::printf("=== Real composed evidence: A/B/C enforced bit-identity, real fault attacks, real prefill schedule ===\n");

        // --- Build all three paths from the same manifest. ---
        const Model model_b = materialize_gguf_model(manifest);
        ModelSourceBinding binding_a = bind_gguf_source(manifest);
        StreamingConfig config_a;
        config_a.virtualize_bookends = true;
        config_a.row_region_materializer = binding_a.materialize_rows;
        config_a.output_chunk_rows = 4096;
        StreamingModel model_a(binding_a.source, binding_a.materialize, config_a);
        ModelSourceBinding binding_c = bind_gguf_source(manifest);
        VirtualizedCachedConfig config_c;
        config_c.materializer = binding_c.materialize;
        config_c.row_region_materializer = binding_c.materialize_rows;
        config_c.output_chunk_rows = 4096;
        VirtualizedCachedModel model_c(std::move(binding_c.source), std::move(config_c));

        // --- Run all three on the same growing sequence, asserting bit-identity as we go. ---
        ContiguousAttentionKVStore cache_b(model_b.config().n_layers, model_b.config().n_kv_heads,
                                           model_b.config().max_positions, model_b.config().head_dim);
        ContiguousAttentionKVStore cache_c(model_c.config().n_layers, model_c.config().n_kv_heads,
                                           model_c.config().max_positions, model_c.config().head_dim);
        std::vector<int64_t> tokens_a = initial;
        const int64_t vocab = model_b.config().vocab;

        CachedStepResult b_step = forward_cached_step(model_b, cache_b, initial, 0);
        CachedStepResult c_step = model_c.step(cache_c, initial, 0);
        for (int64_t s = 0; s < steps; ++s) {
            ForwardResult a_step = model_a.forward(tokens_a);
            std::vector<float> a_last(a_step.logits.end() - vocab, a_step.logits.end());
            std::vector<float> b_last(b_step.logits.end() - vocab, b_step.logits.end());
            std::vector<float> c_last(c_step.logits.end() - vocab, c_step.logits.end());

            check(bit_identical(a_last, b_last), "step " + std::to_string(s) + ": A vs B bit-identical (ENFORCED, not printed)");
            check(bit_identical(a_last, c_last), "step " + std::to_string(s) + ": A vs C bit-identical (ENFORCED, not printed)");
            check(bit_identical(b_last, c_last), "step " + std::to_string(s) + ": B vs C bit-identical (ENFORCED, not printed)");

            const int64_t selected_a = a_step.selected_token.back();
            const int64_t selected_b = b_step.selected_token.back();
            const int64_t selected_c = c_step.selected_token.back();
            check(selected_a == selected_b && selected_b == selected_c,
                  "step " + std::to_string(s) + ": A/B/C select identical tokens");

            tokens_a.push_back(selected_a);
            if (s + 1 < steps) {
                b_step = forward_cached_step(model_b, cache_b, {selected_b},
                                             static_cast<int64_t>(initial.size()) + s);
                c_step = model_c.step(cache_c, {selected_c}, static_cast<int64_t>(initial.size()) + s);
            }
        }
        check(cache_b.current_length() == static_cast<int64_t>(initial.size()) + steps - 1,
              "Path B current_length() matches expected step count exactly");
        check(cache_c.current_length() == static_cast<int64_t>(initial.size()) + steps - 1,
              "Path C current_length() matches expected step count exactly");

        // --- Stage 11: bounded real Path-C fault coverage. ---
        std::printf("\n=== Real Path-C fault coverage ===\n");
        const ModelConfig& cfg = model_c.config();
        {
            // Wrong position now rejects at the API boundary (P5A-RVW-002).
            ModelSourceBinding binding = bind_gguf_source(manifest);
            VirtualizedCachedConfig cfg2{binding.materialize, binding.materialize_rows, 4096, UINT64_MAX};
            VirtualizedCachedModel vm(std::move(binding.source), std::move(cfg2));
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, initial, 0);
            bool rejected = false;
            try { vm.step(c, {28}, static_cast<int64_t>(initial.size()) + 1); }
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "real Path C: wrong start_position rejects at API boundary");
        }
        {
            // Corrupted real KV head diverges.
            ModelSourceBinding binding = bind_gguf_source(manifest);
            VirtualizedCachedConfig cfg2{binding.materialize, binding.materialize_rows, 4096, UINT64_MAX};
            VirtualizedCachedModel vm(std::move(binding.source), std::move(cfg2));
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, initial, 0);
            CachedStepResult baseline = vm.step_unsafe_explicit_position(c, {28}, static_cast<int64_t>(initial.size()));
            ContiguousAttentionKVStore c2(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            ModelSourceBinding binding2 = bind_gguf_source(manifest);
            VirtualizedCachedConfig cfg3{binding2.materialize, binding2.materialize_rows, 4096, UINT64_MAX};
            VirtualizedCachedModel vm2(std::move(binding2.source), std::move(cfg3));
            vm2.step(c2, initial, 0);
            std::vector<float> corrupt(static_cast<size_t>(cfg.head_dim), 999.0f);
            c2.write_k(0, 0, 0, corrupt.data());
            CachedStepResult corrupted = vm2.step_unsafe_explicit_position(c2, {28}, static_cast<int64_t>(initial.size()));
            std::vector<float> baseline_last(baseline.logits.end() - vocab, baseline.logits.end());
            std::vector<float> corrupted_last(corrupted.logits.end() - vocab, corrupted.logits.end());
            float max_diff = 0.0f;
            for (size_t i = 0; i < baseline_last.size(); ++i) {
                max_diff = std::max(max_diff, std::fabs(baseline_last[i] - corrupted_last[i]));
            }
            check(max_diff > 1e-3f, "real Path C: corrupted real KV head diverges");
        }
        {
            // RoPE attack via the explicit unsafe seam.
            ModelSourceBinding binding = bind_gguf_source(manifest);
            VirtualizedCachedConfig cfg2{binding.materialize, binding.materialize_rows, 4096, UINT64_MAX};
            VirtualizedCachedModel vm(std::move(binding.source), std::move(cfg2));
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, initial, 0);
            CachedStepResult baseline = vm.step_unsafe_explicit_position(c, {28}, static_cast<int64_t>(initial.size()));
            ContiguousAttentionKVStore c2(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            ModelSourceBinding binding2 = bind_gguf_source(manifest);
            VirtualizedCachedConfig cfg3{binding2.materialize, binding2.materialize_rows, 4096, UINT64_MAX};
            VirtualizedCachedModel vm2(std::move(binding2.source), std::move(cfg3));
            vm2.step(c2, initial, 0);
            CachedStepResult reset = vm2.step_unsafe_explicit_position(c2, {28}, 0);
            std::vector<float> baseline_last(baseline.logits.end() - vocab, baseline.logits.end());
            std::vector<float> reset_last(reset.logits.end() - vocab, reset.logits.end());
            float max_diff = 0.0f;
            for (size_t i = 0; i < baseline_last.size(); ++i) {
                max_diff = std::max(max_diff, std::fabs(baseline_last[i] - reset_last[i]));
            }
            check(max_diff > 1e-3f, "real Path C: RoPE position reset-to-0 diverges (unsafe seam)");
        }
        {
            // Forced real materialization failure.
            ModelSourceBinding binding = bind_gguf_source(manifest);
            TensorMaterializer real_materializer = binding.materialize;
            int64_t call_count = 0;
            VirtualizedCachedConfig cfg2;
            cfg2.materializer = [real_materializer, &call_count](const LogicalTensor& logical,
                                                                  const BackingExtent& backing) {
                ++call_count;
                if (call_count == 12) throw std::runtime_error("simulated real materialization I/O failure");
                return real_materializer(logical, backing);
            };
            cfg2.row_region_materializer = binding.materialize_rows;
            cfg2.output_chunk_rows = 4096;
            VirtualizedCachedModel vm(std::move(binding.source), std::move(cfg2));
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            bool threw = false;
            try { vm.step(c, initial, 0); } catch (const std::exception&) { threw = true; }
            check(threw, "real Path C: forced real materialization failure propagates cleanly");
            check(c.current_length() == 0, "real Path C: forced materialization failure did not commit");
        }
        {
            // Capacity boundary on the real model's actual max_positions.
            bool rejected = false;
            try {
                ModelSourceBinding binding = bind_gguf_source(manifest);
                VirtualizedCachedConfig cfg2{binding.materialize, binding.materialize_rows, 4096, UINT64_MAX};
                VirtualizedCachedModel vm(std::move(binding.source), std::move(cfg2));
                ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                vm.step_unsafe_explicit_position(c, {1}, cfg.max_positions);
            } catch (const std::exception&) { rejected = true; }
            check(rejected, "real Path C: capacity boundary at real max_positions fails closed");
        }

        // --- Stage 12: bounded real prefill-schedule comparison (layer-major batched vs
        // token-major single-step loop) on the real model, explicit token IDs, no tokenizer. ---
        std::printf("\n=== Real prefill schedule comparison ===\n");
        {
            ModelSourceBinding binding_lm = bind_gguf_source(manifest);
            VirtualizedCachedConfig cfg_lm{binding_lm.materialize, binding_lm.materialize_rows, 4096, UINT64_MAX};
            VirtualizedCachedModel vm_lm(std::move(binding_lm.source), std::move(cfg_lm));
            ContiguousAttentionKVStore cache_lm(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            CachedStepResult layer_major = vm_lm.step(cache_lm, initial, 0);  // batched, new_len=2

            ModelSourceBinding binding_tm = bind_gguf_source(manifest);
            VirtualizedCachedConfig cfg_tm{binding_tm.materialize, binding_tm.materialize_rows, 4096, UINT64_MAX};
            VirtualizedCachedModel vm_tm(std::move(binding_tm.source), std::move(cfg_tm));
            ContiguousAttentionKVStore cache_tm(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            std::vector<float> token_major_last;
            int64_t token_major_selected = -1;
            for (size_t i = 0; i < initial.size(); ++i) {
                CachedStepResult step = vm_tm.step(cache_tm, {initial[i]}, static_cast<int64_t>(i));
                token_major_last.assign(step.logits.end() - vocab, step.logits.end());
                token_major_selected = step.selected_token.back();
            }
            std::vector<float> layer_major_last(layer_major.logits.end() - vocab, layer_major.logits.end());
            check(bit_identical(layer_major_last, token_major_last),
                  "real model: layer-major and token-major prefill produce bit-identical last-position logits");
            check(layer_major.selected_token.back() == token_major_selected,
                  "real model: layer-major and token-major prefill select the identical token");
            bool cache_match = true;
            for (int64_t li = 0; li < cfg.n_layers && cache_match; ++li) {
                for (int64_t h = 0; h < cfg.n_kv_heads && cache_match; ++h) {
                    for (int64_t p = 0; p < static_cast<int64_t>(initial.size()) && cache_match; ++p) {
                        const float* k1 = cache_lm.k_row(li, h, p);
                        const float* k2 = cache_tm.k_row(li, h, p);
                        for (int64_t d = 0; d < cfg.head_dim; ++d) {
                            if (k1[d] != k2[d]) { cache_match = false; break; }
                        }
                    }
                }
            }
            check(cache_match, "real model: layer-major and token-major prefill produce bit-identical cache content (layer 0/mid/final swept)");
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("ALL REAL COMPOSED EVIDENCE CHECKS PASSED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
