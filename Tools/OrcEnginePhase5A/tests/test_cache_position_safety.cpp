// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// P5A-RVW-002 closure: proves the SAFE forward_cached_step/
// VirtualizedCachedModel::step entry points reject any start_position that
// does not equal cache.current_length() BEFORE any mutation, for both
// Reference Path B and Reference Path C, and that the unsafe explicit-
// position seam remains available for deliberate fault injection.
#include <cmath>
#include <cstdio>
#include <cstring>
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
        const CacheTraceStep& prefill = trace[0];
        const CacheTraceStep& first_decode = trace[1];
        const int64_t correct_position = static_cast<int64_t>(prefill.new_tokens.size());

        std::printf("=== Cache position/commit safety (P5A-RVW-002) -- Path B ===\n");
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, c, prefill.new_tokens, 0);
            check(c.current_length() == correct_position, "B: correct position succeeds and commits");
        }
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, c, prefill.new_tokens, 0);
            const int64_t before = c.current_length();
            bool rejected = false;
            try { forward_cached_step(fx.model, c, first_decode.new_tokens, correct_position + 1); }
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "B: gap +1 rejects before mutation");
            check(c.current_length() == before, "B: gap +1 rejection leaves current_length() unchanged");
        }
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, c, prefill.new_tokens, 0);
            const int64_t before = c.current_length();
            bool rejected = false;
            try { forward_cached_step(fx.model, c, first_decode.new_tokens, correct_position + 100); }
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "B: larger gap rejects");
            check(c.current_length() == before, "B: larger gap rejection leaves current_length() unchanged");
        }
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, c, prefill.new_tokens, 0);
            const int64_t before = c.current_length();
            bool rejected = false;
            try { forward_cached_step(fx.model, c, first_decode.new_tokens, 0); }  // rewind
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "B: rewind rejects");
            check(c.current_length() == before, "B: rewind rejection leaves current_length() unchanged");
        }
        {
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);  // fresh, never prefilled
            bool rejected = false;
            try { forward_cached_step(fx.model, c, first_decode.new_tokens, correct_position); }
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "B: nonzero position on fresh (empty) cache rejects");
            check(c.current_length() == 0, "B: rejection on fresh cache leaves current_length()==0");
        }
        {
            // Physical cache unchanged on rejection, checked where practical: no write
            // ever happens before the position guard fires (guard is the very first
            // check in forward_cached_step, before embedding lookup).
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, c, prefill.new_tokens, 0);
            std::vector<float> k_before(static_cast<size_t>(cfg.head_dim));
            std::memcpy(k_before.data(), c.k_row(0, 0, 0), sizeof(float) * static_cast<size_t>(cfg.head_dim));
            try { forward_cached_step(fx.model, c, first_decode.new_tokens, correct_position + 1); } catch (...) {}
            const float* k_after = c.k_row(0, 0, 0);
            bool unchanged = true;
            for (int64_t d = 0; d < cfg.head_dim; ++d) if (k_before[static_cast<size_t>(d)] != k_after[d]) unchanged = false;
            check(unchanged, "B: physical cache content at an already-committed slot is unchanged after a rejected call");
        }
        {
            // Capacity boundary still fails correctly (via the unsafe seam, isolating
            // the capacity check from the position guard).
            bool rejected = false;
            try {
                ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                forward_cached_step_unsafe_explicit_position(fx.model, c, {1}, cfg.max_positions);
            } catch (const std::exception&) { rejected = true; }
            check(rejected, "B: capacity boundary still fails correctly");
        }
        {
            // Deliberate RoPE-position fault testing remains possible through the
            // explicit attack seam.
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, c, prefill.new_tokens, 0);
            bool succeeded = false;
            try {
                forward_cached_step_unsafe_explicit_position(fx.model, c, first_decode.new_tokens, 0);  // reset attack
                succeeded = true;
            } catch (...) {}
            check(succeeded, "B: deliberate RoPE-position fault (reset-to-0) remains possible through the unsafe seam");
        }

        std::printf("\n=== Cache position/commit safety (P5A-RVW-002) -- Path C ===\n");
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, prefill.new_tokens, 0);
            check(c.current_length() == correct_position, "C: correct position succeeds and commits");
        }
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, prefill.new_tokens, 0);
            const int64_t before = c.current_length();
            bool rejected = false;
            try { vm.step(c, first_decode.new_tokens, correct_position + 1); }
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "C: gap +1 rejects before mutation");
            check(c.current_length() == before, "C: gap +1 rejection leaves current_length() unchanged");
        }
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, prefill.new_tokens, 0);
            const int64_t before = c.current_length();
            bool rejected = false;
            try { vm.step(c, first_decode.new_tokens, 0); }  // rewind
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "C: rewind rejects");
            check(c.current_length() == before, "C: rewind rejection leaves current_length() unchanged");
        }
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);  // fresh
            bool rejected = false;
            try { vm.step(c, first_decode.new_tokens, correct_position); }
            catch (const KVCacheError&) { rejected = true; }
            check(rejected, "C: nonzero position on fresh (empty) cache rejects");
            check(c.current_length() == 0, "C: rejection on fresh cache leaves current_length()==0");
        }
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            bool succeeded = false;
            try {
                ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                vm.step(c, prefill.new_tokens, 0);
                vm.step_unsafe_explicit_position(c, first_decode.new_tokens, 0);  // reset attack
                succeeded = true;
            } catch (...) {}
            check(succeeded, "C: deliberate RoPE-position fault (reset-to-0) remains possible through the unsafe seam");
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("ALL CHECKS PASSED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
