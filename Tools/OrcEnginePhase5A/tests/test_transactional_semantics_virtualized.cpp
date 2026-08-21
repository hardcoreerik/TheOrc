// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Re-attacks the mid-layer NaN failure (test_transactional_semantics.cpp)
// against Reference Path C (VirtualizedCachedModel) specifically, per the
// commit-API audit's explicit instruction not to assume the resident
// oracle's transactional proof automatically carries over to the
// virtualized path after the architecture refactor. Same fault (corrupt
// layer 1's w_v with NaN mid-decode), same three claims: the step fails
// closed, current_length() is unchanged (auto-commit never reached), and a
// retry at the same position heals the poison and matches an untouched
// baseline bit-exactly.
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>
#include <vector>

#include "orcengine/cache_trace_loader.hpp"
#include "orcengine/context.hpp"
#include "orcengine/fixture_loader.hpp"
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

// Same pattern as test_virtualized_cached_decode.cpp's bind_memory_model --
// duplicated locally rather than factored out, matching this project's own
// established precedent (Phase 3 and Phase 4 each keep their own local
// copy rather than sharing one across phase boundaries).
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

VirtualizedCachedModel make_vmodel(const Model& model) {
    ModelSourceBinding binding = bind_memory_model(model);
    VirtualizedCachedConfig config;
    config.materializer = std::move(binding.materialize);
    config.row_region_materializer = std::move(binding.materialize_rows);
    config.output_chunk_rows = 7;
    return VirtualizedCachedModel(std::move(binding.source), std::move(config));
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

        // --- Trusted baseline: correct prefill + correct first decode via Reference Path C, untouched by any fault. ---
        VirtualizedCachedModel baseline_vmodel = make_vmodel(fx.model);
        ContiguousAttentionKVStore baseline_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        baseline_vmodel.step(baseline_cache, prefill.new_tokens, 0);
        CachedStepResult baseline_result = baseline_vmodel.step(baseline_cache, first_decode.new_tokens,
                                                                 first_decode.start_position);
        std::vector<float> baseline_logits(baseline_result.logits.end() - cfg.vocab, baseline_result.logits.end());

        // --- Fault scenario. bind_memory_model snapshots weights into an owned
        // BackingExtent AT CONSTRUCTION TIME (BackingExtent::FromF32 copies, it does
        // not alias fx.model's live storage) -- unlike Reference Path B, which reads
        // model.layers[li] live on every call. So corrupting fx.model AFTER a
        // VirtualizedCachedModel is built has no effect on that instance; the fault
        // must be baked in by building a SEPARATE instance from the corrupted model,
        // sharing the SAME cache object across instances (the cache is independent of
        // which model instance touches it -- exactly like a real driver swapping which
        // GGUF-backed materializer it hands a persistent KV cache to). ---
        VirtualizedCachedModel correct_vmodel = make_vmodel(fx.model);
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        correct_vmodel.step(cache, prefill.new_tokens, 0);
        const int64_t committed_before_failure = cache.current_length();

        std::vector<float> saved_w_v = fx.model.layers[1].w_v.raw();
        std::vector<float>& mutable_w_v = fx.model.layers[1].w_v.raw();
        for (float& v : mutable_w_v) v = std::numeric_limits<float>::quiet_NaN();

        VirtualizedCachedModel corrupted_vmodel = make_vmodel(fx.model);  // snapshots the NOW-corrupted weights
        bool threw = false;
        try {
            corrupted_vmodel.step(cache, first_decode.new_tokens, first_decode.start_position);
        } catch (const std::exception&) {
            threw = true;
        }
        check(threw, "[virtualized] corrupted layer-1 weights cause step() to fail closed (NaN detector fires)");
        check(cache.current_length() == committed_before_failure,
              "[virtualized] current_length() unchanged after a mid-step failure (failed step never committed)");

        const float* poisoned_v = cache.v_row(1, 0, first_decode.start_position);
        bool poisoned = false;
        for (int64_t d = 0; d < cfg.head_dim; ++d) {
            if (std::isnan(poisoned_v[d])) { poisoned = true; break; }
        }
        check(poisoned, "[virtualized] layer 1's cache slot at the attempted position is left genuinely poisoned");

        // --- Restore weights, retry at the SAME start_position via a FRESH VirtualizedCachedModel
        // (the materializer reads fx.model's weights fresh each call -- no stale resident state to worry about). ---
        mutable_w_v = saved_w_v;
        VirtualizedCachedModel retry_vmodel = make_vmodel(fx.model);
        CachedStepResult retry_result = retry_vmodel.step(cache, first_decode.new_tokens, first_decode.start_position);
        std::vector<float> retry_logits(retry_result.logits.end() - cfg.vocab, retry_result.logits.end());
        check(max_abs_diff(retry_logits, baseline_logits) == 0.0f,
              "[virtualized] retry at the same position overwrites the poison and matches the untouched baseline bit-exactly");
        check(cache.current_length() == first_decode.start_position +
                  static_cast<int64_t>(first_decode.new_tokens.size()),
              "[virtualized] successful retry's own auto-commit advanced current_length()");

        const float* healed_v = cache.v_row(1, 0, first_decode.start_position);
        bool healed = true;
        for (int64_t d = 0; d < cfg.head_dim; ++d) {
            if (std::isnan(healed_v[d])) { healed = false; break; }
        }
        check(healed, "[virtualized] retry overwrote the poisoned cache slot -- no NaN survives a successful retry");

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("VIRTUALIZED TRANSACTIONAL SEMANTICS HOLD\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
