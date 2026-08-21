// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Reference Path C (virtualized cached decode, composing persistent KV
// state with Phase 3/4's transient per-layer weight materialization)
// proven against Reference Path B (the fully-resident cached oracle) on
// synthetic Fixture C -- this is the "resident cached vs virtualized
// cached" differential OE-ADR-026 calls for, isolating the residency
// architecture change from cache mathematics. Uses the exact same
// bind_memory_model + in-memory row-region materializer pattern already
// established by Phase 3/4's own synthetic streaming tests (see
// Tools/OrcEnginePhase4/tests/test_bookend_virtualization.cpp) -- not a new
// materialization technique.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>
#include <vector>

#include "orcengine/cache_trace_loader.hpp"
#include "orcengine/context.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/forward_cached_virtualized.hpp"
#include "orcengine/materialization.hpp"

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

// Mirrors Tools/OrcEnginePhase4/tests/test_bookend_virtualization.cpp's
// bind_memory_model -- a resident Model wrapped as a ModelSource so the
// SAME virtualization machinery (materialize/release per layer, row-region
// materializer) used against real GGUF files can be exercised against the
// synthetic fixture too, matching this project's established precedent for
// testing streaming code without a real model file.
ModelSourceBinding bind_memory_model(const Model& model) {
    ModelSource source;
    source.config = model.config();
    source.tied_embeddings = model.manifest.tied_embeddings;
    auto add = [&](TensorRole role, int64_t layer, const std::string& name, const ResidentView& view) {
        const uint64_t bytes = static_cast<uint64_t>(view.raw().size()) * sizeof(float);
        source.tensors.push_back({{role, layer}, LogicalTensor(name, view.shape()),
                                  BackingExtent::FromF32(view.raw()), bytes, std::string("memory:") + name});
    };
    add(TensorRole::TokenEmbedding, -1, "token_embedding", model.token_embedding);
    add(TensorRole::FinalNorm, -1, "final_norm", model.final_norm_weight);
    if (model.lm_head) add(TensorRole::OutputHead, -1, "output_head", *model.lm_head);
    for (int64_t layer = 0; layer < model.config().n_layers; ++layer) {
        const LayerWeights& w = model.layers.at(static_cast<size_t>(layer));
        const std::string prefix = "layer" + std::to_string(layer) + ".";
        add(TensorRole::AttentionNorm, layer, prefix + "attention_norm", w.attn_norm_weight);
        add(TensorRole::AttentionQuery, layer, prefix + "attention_query", w.w_q);
        add(TensorRole::AttentionKey, layer, prefix + "attention_key", w.w_k);
        add(TensorRole::AttentionValue, layer, prefix + "attention_value", w.w_v);
        add(TensorRole::AttentionOutput, layer, prefix + "attention_output", w.w_o);
        add(TensorRole::FfnNorm, layer, prefix + "ffn_norm", w.ffn_norm_weight);
        add(TensorRole::FfnGate, layer, prefix + "ffn_gate", w.w_gate);
        add(TensorRole::FfnUp, layer, prefix + "ffn_up", w.w_up);
        add(TensorRole::FfnDown, layer, prefix + "ffn_down", w.w_down);
    }
    TensorMaterializer full = [](const LogicalTensor& logical, const BackingExtent& backing) {
        return materialize(logical, backing);
    };
    TensorRowRegionMaterializer rows = [](const LogicalTensor& logical, const BackingExtent& backing,
                                          const TensorRowRegion& region) {
        if (logical.shape().ndim() != 2 || region.row_count == 0) throw std::runtime_error("invalid memory row request");
        const uint64_t total_rows = static_cast<uint64_t>(logical.shape().dim(0));
        const uint64_t columns = static_cast<uint64_t>(logical.shape().dim(1));
        if (region.row_begin >= total_rows || region.row_count > total_rows - region.row_begin) {
            throw std::runtime_error("memory row request out of bounds");
        }
        const uint64_t begin = region.row_begin * columns;
        const uint64_t count = region.row_count * columns;
        std::vector<float> values(static_cast<size_t>(count));
        std::memcpy(values.data(), backing.bytes().data() + begin * sizeof(float),
                   static_cast<size_t>(count) * sizeof(float));
        return MaterializedRegion{
            ResidentView(TensorShape({static_cast<int64_t>(region.row_count), static_cast<int64_t>(columns)}),
                        std::move(values)),
            count * sizeof(float)};
    };
    return {std::move(source), std::move(full), std::move(rows)};
}

}  // namespace

int main(int argc, char** argv) {
    try {
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase5a";
        LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_cache_weights.txt");
        std::vector<CacheTraceStep> trace = load_cache_trace(fixtures_dir + "/fixture_cache_trace_python.txt");
        const ModelConfig& cfg = fx.model.config();

        ModelSourceBinding binding = bind_memory_model(fx.model);
        VirtualizedCachedConfig vconfig;
        vconfig.materializer = std::move(binding.materialize);
        vconfig.row_region_materializer = std::move(binding.materialize_rows);
        vconfig.output_chunk_rows = 7;  // deliberately not a divisor of vocab=32, matching Phase 4's own chunk-boundary practice
        VirtualizedCachedModel vmodel(std::move(binding.source), std::move(vconfig));

        ContiguousAttentionKVStore resident_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        ContiguousAttentionKVStore virtualized_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        std::printf("=== Reference Path B (resident cached) vs Reference Path C (virtualized cached) ===\n");
        for (size_t step_idx = 0; step_idx < trace.size(); ++step_idx) {
            const CacheTraceStep& step = trace[step_idx];
            CachedStepResult resident = forward_cached_step(fx.model, resident_cache, step.new_tokens, step.start_position);
            // Both auto-commit current_length() on success (commit-API audit fix) -- no manual calls.
            CachedStepResult virt = vmodel.step(virtualized_cache, step.new_tokens, step.start_position);

            const float diff = max_abs_diff(resident.logits, virt.logits);
            check(diff == 0.0f, "step " + std::to_string(step_idx) + " (" + step.kind +
                  "): resident cached and virtualized cached produce bit-identical complete logits (max_abs_diff=" +
                  std::to_string(diff) + ")");
            check(resident.selected_token == virt.selected_token,
                  "step " + std::to_string(step_idx) + ": resident and virtualized select identical tokens");
            check(vmodel.telemetry().peak_active_layers == 1,
                  "step " + std::to_string(step_idx) + ": at most one transformer layer resident at any instant (virtualized)");
        }

        bool cache_match = true;
        for (int64_t li = 0; li < cfg.n_layers && cache_match; ++li) {
            for (int64_t h = 0; h < cfg.n_kv_heads && cache_match; ++h) {
                for (int64_t p = 0; p < resident_cache.current_length() && cache_match; ++p) {
                    const float* k1 = resident_cache.k_row(li, h, p);
                    const float* k2 = virtualized_cache.k_row(li, h, p);
                    const float* v1 = resident_cache.v_row(li, h, p);
                    const float* v2 = virtualized_cache.v_row(li, h, p);
                    for (int64_t d = 0; d < cfg.head_dim; ++d) {
                        if (k1[d] != k2[d] || v1[d] != v2[d]) { cache_match = false; break; }
                    }
                }
            }
        }
        check(cache_match, "resident and virtualized final cache content bit-identical at every layer/head/position");

        // Weight residency never regresses to full-model: materialization
        // count must match EXACTLY the deterministic sum of (per-layer
        // tensors + distinct embedding rows + output chunks) across all
        // steps -- an exact equality, not a one-sided bound. A one-sided
        // `<=` (this test's original form) cannot detect under-materialization
        // (e.g. a layer silently kept resident across steps instead of
        // re-materialized) since any smaller actual count would still
        // satisfy `<=`; equality catches both directions
        // (P5A-RVW-011 fix).
        const uint64_t output_chunks_per_step = (static_cast<uint64_t>(cfg.vocab) + 6) / 7;  // ceil(vocab/7)
        const uint64_t per_step_layer_tensors = static_cast<uint64_t>(cfg.n_layers) * 9;
        uint64_t expected_materializations = 1;  // final norm, once at construction
        for (const CacheTraceStep& step : trace) {
            expected_materializations += per_step_layer_tensors + output_chunks_per_step;
            // Exact distinct-token count for this step, matching
            // virtualized_embedding's own per-call std::unordered_set dedup.
            std::vector<int64_t> distinct;
            for (int64_t token : step.new_tokens) {
                if (std::find(distinct.begin(), distinct.end(), token) == distinct.end()) {
                    distinct.push_back(token);
                }
            }
            expected_materializations += distinct.size();
        }
        check(vmodel.telemetry().materialization_count == expected_materializations,
              "materialization count EXACTLY matches the deterministic per-step per-layer re-materialization schedule "
              "(actual=" + std::to_string(vmodel.telemetry().materialization_count) +
              " expected=" + std::to_string(expected_materializations) + ")");
        check(vmodel.telemetry().peak_resident_weight_bytes < vmodel.telemetry().cumulative_materialized_bytes,
              "peak resident weight bytes are below cumulative materialized bytes (proves release actually happens)");

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("ALL CHECKS PASSED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
