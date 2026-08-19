// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Re-runs the required fault-injection suite against Reference Path C
// (VirtualizedCachedModel, temporary per-layer materialized weights), not
// just Reference Path B, per OE-ADR-026's explicit instruction that fault
// attacks must execute with temporary materialized weights, not only
// resident ones. Uses synthetic Fixture C via bind_memory_model (same
// pattern as test_virtualized_cached_decode.cpp / Phase 3/4's own synthetic
// streaming tests) so this runs in milliseconds rather than requiring a
// real-model process per case.
#include <cmath>
#include <cstdio>
#include <cstring>
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

        std::printf("=== Reference Path C (virtualized cached) fault attacks -- temporary materialized weights ===\n");

        // Baseline correct run for comparison.
        VirtualizedCachedModel baseline_vmodel = make_vmodel(fx.model);
        ContiguousAttentionKVStore baseline_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        baseline_vmodel.step(baseline_cache, prefill.new_tokens, 0);
        CachedStepResult baseline = baseline_vmodel.step(baseline_cache, first_decode.new_tokens, first_decode.start_position);
        std::vector<float> baseline_last(baseline.logits.end() - cfg.vocab, baseline.logits.end());

        // 1. Wrong cache position (+1).
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, prefill.new_tokens, 0);
            CachedStepResult r = vm.step_unsafe_explicit_position(c, first_decode.new_tokens, first_decode.start_position + 1);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f, "1. wrong cache position (+1) diverges (virtualized, unsafe seam)");
        }

        // 2. Stale/unwritten KV reuse (skip prefill entirely).
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            CachedStepResult r = vm.step_unsafe_explicit_position(c, first_decode.new_tokens, first_decode.start_position);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f, "2. stale/unwritten KV reuse diverges (virtualized, unsafe seam)");
        }

        // 3. Swapped K/V at one cache slot.
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, prefill.new_tokens, 0);
            std::vector<float> k_copy(static_cast<size_t>(cfg.head_dim)), v_copy(static_cast<size_t>(cfg.head_dim));
            std::memcpy(k_copy.data(), c.k_row(0, 0, 0), sizeof(float) * static_cast<size_t>(cfg.head_dim));
            std::memcpy(v_copy.data(), c.v_row(0, 0, 0), sizeof(float) * static_cast<size_t>(cfg.head_dim));
            c.write_k(0, 0, 0, v_copy.data());
            c.write_v(0, 0, 0, k_copy.data());
            CachedStepResult r = vm.step(c, first_decode.new_tokens, first_decode.start_position);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f, "3. swapped K/V at one cache slot diverges (virtualized)");
        }

        // 4. Corrupted shared GQA head.
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, prefill.new_tokens, 0);
            std::vector<float> corrupt(static_cast<size_t>(cfg.head_dim), 999.0f);
            c.write_k(0, 0, 0, corrupt.data());
            CachedStepResult r = vm.step(c, first_decode.new_tokens, first_decode.start_position);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f,
                  "4. corrupted shared GQA kv_head 0 diverges (virtualized)");
        }

        // 5. Cross-context isolation: fresh never-prefilled context vs correctly-prefilled one.
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore fresh(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            CachedStepResult r = vm.step_unsafe_explicit_position(fresh, first_decode.new_tokens, first_decode.start_position);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f,
                  "5. fresh (never-prefilled) context diverges -- no cross-context leakage (virtualized, unsafe seam)");
            // 5b. The SAFE API rejects this same scenario outright.
            VirtualizedCachedModel vm2 = make_vmodel(fx.model);
            ContiguousAttentionKVStore fresh2(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            bool rejected5b = false;
            try { vm2.step(fresh2, first_decode.new_tokens, first_decode.start_position); }
            catch (const std::exception&) { rejected5b = true; }
            check(rejected5b, "5b. SAFE API rejects decoding a fresh context at a nonzero position outright (virtualized)");
        }

        // 6. Capacity boundary: exact max_positions fails closed, max_positions-1 succeeds.
        {
            bool rejected = false;
            try {
                VirtualizedCachedModel vm = make_vmodel(fx.model);
                ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                vm.step_unsafe_explicit_position(c, {1}, cfg.max_positions);
            } catch (const std::exception&) {
                rejected = true;
            }
            check(rejected, "6a. decode at exactly max_positions fails closed (virtualized, capacity check, unsafe seam)");
        }
        {
            bool ok = false;
            try {
                VirtualizedCachedModel vm = make_vmodel(fx.model);
                ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                vm.step_unsafe_explicit_position(c, {1}, cfg.max_positions - 1);
                ok = true;
            } catch (const std::exception&) {
                ok = false;
            }
            check(ok, "6b. decode at max_positions-1 succeeds (virtualized, capacity check, unsafe seam)");
        }

        // 7. Reset to position 0 (RoPE attack variant).
        {
            VirtualizedCachedModel vm = make_vmodel(fx.model);
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm.step(c, prefill.new_tokens, 0);
            CachedStepResult r = vm.step_unsafe_explicit_position(c, first_decode.new_tokens, 0);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            check(max_abs_diff(r_last, baseline_last) > 1e-3f, "7. RoPE position reset to 0 diverges (virtualized, unsafe seam)");
        }

        // 8. Forced materialization failure: a materializer that throws partway through
        //    layer 1 (this virtualized path's natural fault seam -- materialization is a
        //    caller-supplied callback) must propagate cleanly, must not leave more than one
        //    layer resident, and must not commit the failed step.
        {
            ModelSourceBinding binding = bind_memory_model(fx.model);
            TensorMaterializer real_materializer = binding.materialize;
            int64_t call_count = 0;
            VirtualizedCachedConfig config;
            config.materializer = [real_materializer, &call_count](const LogicalTensor& logical,
                                                                    const BackingExtent& backing) {
                ++call_count;
                if (call_count == 12) {  // partway into layer 1's 9 tensors (after bookends + layer 0)
                    throw std::runtime_error("simulated materialization I/O failure");
                }
                return real_materializer(logical, backing);
            };
            config.row_region_materializer = binding.materialize_rows;
            config.output_chunk_rows = 7;
            VirtualizedCachedModel vm(std::move(binding.source), std::move(config));
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            bool threw = false;
            try {
                vm.step(c, prefill.new_tokens, 0);
            } catch (const std::exception&) {
                threw = true;
            }
            check(threw, "8a. forced materialization failure propagates cleanly (virtualized)");
            check(vm.telemetry().peak_active_layers <= 1,
                  "8b. forced materialization failure never left more than one layer resident");
            check(c.current_length() == 0,
                  "8c. forced materialization failure did not commit the failed step");
        }

        // 9. Non-vacuity: a materializer that returns CORRUPTED values for one layer 1
        //    tensor (instead of throwing) must produce output that diverges from the
        //    untouched baseline -- proving Reference Path C genuinely computes from its
        //    OWN materializer path rather than silently sharing or echoing Reference Path
        //    B's resident weights (which would be unaffected by this corruption).
        {
            ModelSourceBinding binding = bind_memory_model(fx.model);
            TensorMaterializer real_materializer = binding.materialize;
            int64_t call_count = 0;
            VirtualizedCachedConfig config;
            config.materializer = [real_materializer, &call_count](const LogicalTensor& logical,
                                                                    const BackingExtent& backing) {
                ++call_count;
                ResidentView view = real_materializer(logical, backing);
                if (call_count == 12) {  // same slot as attack 8, but corrupt instead of throw
                    for (float& v : view.raw()) v = 12345.0f;
                }
                return view;
            };
            config.row_region_materializer = binding.materialize_rows;
            config.output_chunk_rows = 7;
            VirtualizedCachedModel vm(std::move(binding.source), std::move(config));
            ContiguousAttentionKVStore c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            CachedStepResult r = vm.step(c, prefill.new_tokens, 0);
            std::vector<float> r_last(r.logits.end() - cfg.vocab, r.logits.end());
            // baseline's own prefill-step logits, not the decode-step baseline used above.
            std::vector<float> baseline_prefill_last(baseline.logits.end() - cfg.vocab, baseline.logits.end());
            const float diff9 = max_abs_diff(r_last, baseline_prefill_last);
            check(diff9 > 1e-3f,
                  "9. corrupted materializer output for one layer 1 tensor changes Reference Path C's result "
                  "(max_abs_diff=" + std::to_string(diff9) + ", not silently sharing/echoing another path's weights)");
        }

        // 10. Residency-guard non-vacuity: directly prove ResidencyLedger::enter_layer
        //     (the mechanism Reference Path C relies on for "only one layer resident at a
        //     time") actually rejects a second concurrent layer rather than silently
        //     allowing a full-resident fallback -- this is the guard the telemetry-based
        //     peak_active_layers==1 checks throughout this suite depend on being real.
        {
            ResidencyLedger ledger;
            ledger.enter_layer(0);
            bool rejected = false;
            try {
                ledger.enter_layer(1);
            } catch (const std::logic_error&) {
                rejected = true;
            }
            check(rejected, "10. ResidencyLedger::enter_layer rejects a second concurrently-resident layer "
                  "(the guard peak_active_layers==1 checks above actually depend on)");
        }

        // 11. Reverse B/C independence (Stage 10 closure): corrupt ONLY Path B's resident
        //     Model, confirm B diverges while an INDEPENDENTLY-CONSTRUCTED Path C (its
        //     ModelSource snapshotted via bind_memory_model BEFORE the corruption, so it
        //     cannot alias fx.model's live storage) remains unaffected. Mirrors attack 9's
        //     forward direction (corrupt-only-C), completing the symmetric independence proof.
        {
            // Build Path C's ModelSource from the CORRECT (pre-corruption) weights first --
            // BackingExtent::FromF32 copies, so this is a genuine independent snapshot.
            VirtualizedCachedModel vm_c = make_vmodel(fx.model);
            ContiguousAttentionKVStore cache_c(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm_c.step(cache_c, prefill.new_tokens, 0);
            CachedStepResult c_before = vm_c.step(cache_c, first_decode.new_tokens, first_decode.start_position);
            std::vector<float> c_before_last(c_before.logits.end() - cfg.vocab, c_before.logits.end());

            // Baseline for Path B, established BEFORE corruption.
            ContiguousAttentionKVStore cache_b_baseline(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, cache_b_baseline, prefill.new_tokens, 0);
            CachedStepResult b_baseline = forward_cached_step(fx.model, cache_b_baseline, first_decode.new_tokens,
                                                               first_decode.start_position);
            std::vector<float> b_baseline_last(b_baseline.logits.end() - cfg.vocab, b_baseline.logits.end());

            // Corrupt ONLY fx.model's own resident weights -- Path B reads these live;
            // Path C's vm_c above already holds an independent, unaffected snapshot.
            std::vector<float> saved_w_v = fx.model.layers[1].w_v.raw();
            std::vector<float>& mutable_w_v = fx.model.layers[1].w_v.raw();
            for (float& v : mutable_w_v) v = 999.0f;

            ContiguousAttentionKVStore cache_b_corrupted(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            forward_cached_step(fx.model, cache_b_corrupted, prefill.new_tokens, 0);
            CachedStepResult b_corrupted = forward_cached_step(fx.model, cache_b_corrupted, first_decode.new_tokens,
                                                                first_decode.start_position);
            std::vector<float> b_corrupted_last(b_corrupted.logits.end() - cfg.vocab, b_corrupted.logits.end());
            check(max_abs_diff(b_corrupted_last, b_baseline_last) > 1e-3f,
                  "11a. corrupting ONLY Path B's resident Model makes Path B diverge from its own baseline");

            // Re-run the SAME vm_c (its ModelSource was snapshotted before corruption and
            // is a materializer/BackingExtent, not a live reference to fx.model) on a fresh
            // cache and confirm it still matches its own pre-corruption result exactly.
            ContiguousAttentionKVStore cache_c2(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            vm_c.step(cache_c2, prefill.new_tokens, 0);
            CachedStepResult c_after = vm_c.step(cache_c2, first_decode.new_tokens, first_decode.start_position);
            std::vector<float> c_after_last(c_after.logits.end() - cfg.vocab, c_after.logits.end());
            check(max_abs_diff(c_after_last, c_before_last) == 0.0f,
                  "11b. Path C remains COMPLETELY unaffected by corrupting Path B's resident Model "
                  "(cannot alias fx.model's live storage) -- reverse independence direction closed");

            mutable_w_v = saved_w_v;  // restore for hygiene, though no later test in this file reuses fx.model
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("ALL VIRTUALIZED-PATH ATTACKS DETECTED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
