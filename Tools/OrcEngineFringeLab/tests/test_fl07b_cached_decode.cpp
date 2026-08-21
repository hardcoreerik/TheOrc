// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B Commit 2: multi-step cached-decode integration tests.
// StickyLayerCachedModel (sticky_layer_cached_model.hpp) threads a
// persistent ContiguousAttentionKVStore across a prefill step plus several
// decode steps -- including a token id repeated at two different absolute
// positions, to prove correctness depends on POSITION (RoPE), not on
// caching keyed by token identity. Every plan's step sequence is compared,
// step-by-step, against an INDEPENDENTLY-CONSTRUCTED fully-resident
// Reference Path B run (Phase 5A's own forward_cached_step against the
// plain in-memory Model) over: complete logits, selected token, committed
// cache length, and the complete physical KV-cache contents (every
// layer/kv_head/committed position). Reuses Phase 5A's
// execute_cached_transformer_layer seam via StickyLayerCachedModel;
// forward_cached_step itself (the reference driver here) is Phase 5A code,
// called but not modified.
//
// The synthetic fixture (fixtures_phase1/fixture_untied.txt) has only 2
// transformer layers, which caps how many DISTINCT sticky sets exist (four:
// {}, {0}, {1}, {0,1}) -- the plan matrix below constructs 6 plans across
// those 4 sets (including two MaterializationCostPerByte constructions that
// land on different sets depending on budget), documented as a fixture
// limitation in EXPERIMENT.md rather than overclaimed as 7 distinct sets.
#include <algorithm>
#include <cstdio>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "fringelab/sticky_layer_cached_model.hpp"
#include "fringelab/sticky_layer_model.hpp"
#include "fringelab/sticky_layer_plan.hpp"
#include "orcengine/context.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/materialization.hpp"

using namespace orcengine;
using namespace fringelab;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

// Identical to test_fl07b_sticky_layer.cpp's own copy -- kept as this file's
// own local helper per this project's established convention (each test
// file owns its small synthetic-fixture-binding helpers rather than sharing
// a header purely for test code).
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
    return {std::move(source), std::move(full), nullptr};
}

uint64_t bookend_bytes(const Model& model) {
    uint64_t bytes = static_cast<uint64_t>(model.token_embedding.raw().size()) * sizeof(float);
    bytes += static_cast<uint64_t>(model.final_norm_weight.raw().size()) * sizeof(float);
    if (model.lm_head) bytes += static_cast<uint64_t>(model.lm_head->raw().size()) * sizeof(float);
    return bytes;
}

class TrackingMaterializer {
public:
    explicit TrackingMaterializer(TensorMaterializer inner) : inner_(std::move(inner)) {}
    ResidentView operator()(const LogicalTensor& logical, const BackingExtent& backing) {
        ++counts_[logical.name()];
        if (fault_name_.has_value() && logical.name() == *fault_name_) {
            throw std::runtime_error("FL-07B injected materialization fault for '" + logical.name() + "'");
        }
        return inner_(logical, backing);
    }
    void set_fault(std::string name) { fault_name_ = std::move(name); }
    void clear_fault() { fault_name_.reset(); }
    int count(const std::string& name) const {
        const auto it = counts_.find(name);
        return it == counts_.end() ? 0 : it->second;
    }

private:
    TensorMaterializer inner_;
    std::optional<std::string> fault_name_;
    std::map<std::string, int> counts_;
};

// One step of the shared multi-step schedule every plan and the reference
// are driven through: a 3-token prefill, then 4 single-token decode steps.
// Token 5 (first used at position 1) reappears at position 4; token 1
// (first used at position 0) reappears at position 6 -- both are new
// ABSOLUTE POSITIONS for an already-seen token id, so a driver that
// (incorrectly) keyed any state by token id rather than position would
// diverge from the reference here.
struct ScheduleStep {
    std::vector<int64_t> tokens;
    int64_t start_position;
};
std::vector<ScheduleStep> multi_step_schedule() {
    return {
        {{1, 5, 9}, 0},  // prefill
        {{3}, 3},        // decode
        {{5}, 4},        // decode -- token 5 repeated, new position
        {{9}, 5},        // decode
        {{1}, 6},        // decode -- token 1 repeated, new position
    };
}

bool caches_physically_identical(const ContiguousAttentionKVStore& a, const ContiguousAttentionKVStore& b) {
    if (a.n_layers() != b.n_layers() || a.n_kv_heads() != b.n_kv_heads() ||
        a.max_positions() != b.max_positions() || a.head_dim() != b.head_dim() ||
        a.current_length() != b.current_length()) {
        return false;
    }
    for (int64_t li = 0; li < a.n_layers(); ++li) {
        for (int64_t h = 0; h < a.n_kv_heads(); ++h) {
            for (int64_t p = 0; p < a.current_length(); ++p) {
                const float* ka = a.k_row(li, h, p);
                const float* kb = b.k_row(li, h, p);
                const float* va = a.v_row(li, h, p);
                const float* vb = b.v_row(li, h, p);
                for (int64_t d = 0; d < a.head_dim(); ++d) {
                    if (ka[d] != kb[d] || va[d] != vb[d]) return false;
                }
            }
        }
    }
    return true;
}

struct NamedCachedPlan {
    std::string label;
    StickyLayerPlan plan;
};

std::vector<NamedCachedPlan> build_plan_matrix(const std::vector<LayerCostInfo>& layer_costs) {
    std::vector<NamedCachedPlan> plans;
    plans.push_back({"zero-sticky", plan_first_k(layer_costs, 0)});
    plans.push_back({"one-sticky-first", plan_first_k(layer_costs, layer_costs[0].resident_bytes)});
    if (layer_costs.size() >= 2) {
        plans.push_back({"one-sticky-second-nonprefix",
                         plan_explicit_set(layer_costs, {1}, layer_costs[1].resident_bytes)});
        uint64_t all_bytes = 0;
        for (const auto& l : layer_costs) all_bytes += l.resident_bytes;
        plans.push_back({"all-sticky", plan_first_k(layer_costs, all_bytes)});

        // MaterializationCostPerByte, synthetic benefit data: layer 1 ranked
        // higher per byte than layer 0 -- a tight budget selects {1} only
        // (a non-prefix, cost-driven choice a byte-order-only policy like
        // FirstK could never produce), a generous budget selects both.
        std::vector<LayerCostInfo> ranked = layer_costs;
        ranked[0].benefit_estimate = 1.0;
        ranked[0].cost_source = CostSource::Synthetic;
        ranked[1].benefit_estimate = 4.0;
        ranked[1].cost_source = CostSource::Synthetic;
        plans.push_back({"cost-per-byte-tight-budget", plan_cost_per_byte(ranked, layer_costs[1].resident_bytes)});
        plans.push_back({"cost-per-byte-generous-budget", plan_cost_per_byte(ranked, all_bytes)});
    }
    return plans;
}

void run_cached_decode_tests(const std::string& fixtures_dir) {
    std::printf("=== StickyLayerCachedModel multi-step cached-decode tests (Commit 2) ===\n");
    LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_untied.txt");
    const ModelConfig& cfg = fx.model.config();
    const std::vector<ScheduleStep> schedule = multi_step_schedule();

    ModelSourceBinding probe = bind_memory_model(fx.model);
    const std::vector<LayerCostInfo> layer_costs = layer_costs_from_source(probe.source);
    const uint64_t bookends = bookend_bytes(fx.model);

    // Independently-constructed reference: Phase 5A's own fully-resident
    // Reference Path B (forward_cached_step against the plain Model),
    // called through its unmodified public seam.
    ContiguousAttentionKVStore reference_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
    std::vector<CachedStepResult> reference_results;
    reference_results.reserve(schedule.size());
    for (const ScheduleStep& s : schedule) {
        reference_results.push_back(forward_cached_step(fx.model, reference_cache, s.tokens, s.start_position));
    }

    const std::vector<NamedCachedPlan> plans = build_plan_matrix(layer_costs);
    check(!plans.empty(), "plan matrix is non-empty");

    for (const NamedCachedPlan& np : plans) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        const uint64_t expected_baseline = bookends + np.plan.planned_resident_bytes();
        StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize), np.plan);
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              np.label + ": resident bytes immediately after construction equal bookends+sticky baseline");

        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        for (size_t i = 0; i < schedule.size(); ++i) {
            const ScheduleStep& s = schedule[i];
            CachedStepResult result = model.step(cache, s.tokens, s.start_position);
            check(result.logits == reference_results[i].logits,
                  np.label + ": step " + std::to_string(i) + " COMPLETE logits bit-identical to reference");
            check(result.selected_token == reference_results[i].selected_token,
                  np.label + ": step " + std::to_string(i) + " selected token matches reference");
            check(cache.current_length() == s.start_position + static_cast<int64_t>(s.tokens.size()),
                  np.label + ": step " + std::to_string(i) + " committed cache length correct after this step");
        }
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              np.label + ": resident bytes return to baseline after the full multi-step sequence");
        check(caches_physically_identical(cache, reference_cache),
              np.label + ": complete physical KV-cache contents (every layer/kv_head/position) and metadata "
              "identical to the reference cache after the full sequence");
    }

    // Cross-plan consistency: two plans that select the SAME final set of
    // sticky layers via different policies (all-sticky FirstK vs the
    // cost-per-byte generous-budget plan, both {0,1}) must themselves be
    // pairwise bit-identical across the whole sequence, not merely each
    // individually identical to the reference.
    if (layer_costs.size() >= 2) {
        ModelSourceBinding binding_a = bind_memory_model(fx.model);
        ModelSourceBinding binding_b = bind_memory_model(fx.model);
        uint64_t all_bytes = 0;
        for (const auto& l : layer_costs) all_bytes += l.resident_bytes;
        std::vector<LayerCostInfo> ranked = layer_costs;
        ranked[0].benefit_estimate = 1.0;
        ranked[1].benefit_estimate = 4.0;
        StickyLayerCachedModel model_a(std::move(binding_a.source), std::move(binding_a.materialize),
                                       plan_first_k(layer_costs, all_bytes));
        StickyLayerCachedModel model_b(std::move(binding_b.source), std::move(binding_b.materialize),
                                       plan_cost_per_byte(ranked, all_bytes));
        ContiguousAttentionKVStore cache_a(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        ContiguousAttentionKVStore cache_b(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        for (const ScheduleStep& s : schedule) {
            CachedStepResult ra = model_a.step(cache_a, s.tokens, s.start_position);
            CachedStepResult rb = model_b.step(cache_b, s.tokens, s.start_position);
            check(ra.logits == rb.logits,
                  "cross-plan: FirstK all-sticky and CostPerByte generous-budget (same final set) agree step-by-step");
        }
    }

    // Identity-aware cumulative materialization proof across the WHOLE
    // multi-step sequence, not one call: sticky layer 0's tensor
    // materializes exactly once (construction only); cold layer 1's tensor
    // materializes exactly once PER step it participates in.
    if (layer_costs.size() >= 2) {
        std::printf("\n--- Identity-aware cumulative materialization proof ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerCachedModel model(std::move(binding.source), wrapped,
                                     plan_first_k(layer_costs, layer_costs[0].resident_bytes));
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        for (const ScheduleStep& s : schedule) model.step(cache, s.tokens, s.start_position);

        check(tracker->count("layer0.attention_query") == 1,
              "cumulative: sticky layer 0's tensor materialized exactly ONCE across all 5 steps (construction only)");
        check(tracker->count("layer1.attention_query") == static_cast<int>(schedule.size()),
              "cumulative: cold layer 1's tensor materialized exactly ONCE PER STEP (5 times total, never cached "
              "between steps)");
        check(model.telemetry().backing_bytes_read > 0,
              "cumulative: telemetry reports nonzero cumulative backing-bytes-read across the sequence "
              "(descriptive evidence only -- no I/O-divergence conclusion drawn here; that is Commit 3's job)");
    }

    // Fault injection 1: PARTIAL cold-layer materialization failure at a
    // MIDDLE step (step index 2, the repeated-token-5 decode step). Proves
    // logical cache rollback: current_length() stays at its PRE-step value
    // (the committed-length gate, not a physical wipe of any bytes this
    // step's earlier-executing layers may have already written -- matching
    // Phase 5A's own documented "commit on success only" semantics, see
    // context.hpp's write_row comment), and a retried step recovers bit-exact.
    if (layer_costs.size() >= 2) {
        std::printf("\n--- Fault injection 1: partial cold-layer materialization failure mid-sequence ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerPlan p = plan_first_k(layer_costs, layer_costs[0].resident_bytes);  // layer 0 sticky, 1 cold
        const uint64_t expected_baseline = bookends + p.planned_resident_bytes();
        StickyLayerCachedModel model(std::move(binding.source), wrapped, p);
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        for (size_t i = 0; i < 2; ++i) model.step(cache, schedule[i].tokens, schedule[i].start_position);
        const int64_t pre_fault_length = cache.current_length();

        tracker->set_fault("layer1.ffn_down");
        bool threw = false;
        try {
            model.step(cache, schedule[2].tokens, schedule[2].start_position);
        } catch (const std::exception&) {
            threw = true;
        }
        tracker->clear_fault();

        check(threw, "partial-fault: step() propagates the exception");
        check(cache.current_length() == pre_fault_length,
              "partial-fault: cache.current_length() stays at its pre-step value (logical rollback via the "
              "commit-on-success-only gate)");
        check(model.telemetry().current_layer == -1, "partial-fault: telemetry.current_layer == -1 immediately after");
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              "partial-fault: resident bytes return exactly to the bookends+sticky baseline");

        CachedStepResult recovered = model.step(cache, schedule[2].tokens, schedule[2].start_position);
        check(recovered.logits == reference_results[2].logits,
              "partial-fault recovery: retried step 2 bit-identical to reference");
        check(cache.current_length() ==
                  schedule[2].start_position + static_cast<int64_t>(schedule[2].tokens.size()),
              "partial-fault recovery: cache length correct after the retried step");
        check(tracker->count("layer0.attention_query") == 1,
              "partial-fault: sticky layer 0 was not rematerialized by the failed or the recovery step");

        for (size_t i = 3; i < schedule.size(); ++i) {
            CachedStepResult r = model.step(cache, schedule[i].tokens, schedule[i].start_position);
            check(r.logits == reference_results[i].logits,
                  "partial-fault recovery: subsequent step " + std::to_string(i) + " remains bit-identical to reference");
        }
        check(caches_physically_identical(cache, reference_cache),
              "partial-fault recovery: complete physical KV-cache contents identical to reference after the "
              "full sequence completes despite the mid-sequence fault and retry");
    }

    // Fault injection 2: cold-layer EXECUTION failure (after the cold
    // layer's weights are fully resident), again mid-sequence.
    if (layer_costs.size() >= 2) {
        std::printf("\n--- Fault injection 2: cold-layer execution failure mid-sequence ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerPlan p = plan_first_k(layer_costs, layer_costs[0].resident_bytes);
        const uint64_t expected_baseline = bookends + p.planned_resident_bytes();
        StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize), p);
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        for (size_t i = 0; i < 3; ++i) model.step(cache, schedule[i].tokens, schedule[i].start_position);
        const int64_t pre_fault_length = cache.current_length();

        model.fault_before_cold_execute = [&](int64_t layer, int64_t step_index) {
            if (layer == 1 && step_index == 3) throw std::runtime_error("FL-07B injected cold-execution fault");
        };
        bool threw = false;
        try {
            model.step(cache, schedule[3].tokens, schedule[3].start_position);
        } catch (const std::exception&) {
            threw = true;
        }
        model.fault_before_cold_execute = nullptr;

        check(threw, "execution-fault: step() propagates the exception");
        check(cache.current_length() == pre_fault_length,
              "execution-fault: cache.current_length() stays at its pre-step value (logical rollback)");
        check(model.telemetry().current_layer == -1, "execution-fault: telemetry.current_layer == -1 immediately after");
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              "execution-fault: resident bytes return exactly to the bookends+sticky baseline");

        CachedStepResult recovered = model.step(cache, schedule[3].tokens, schedule[3].start_position);
        check(recovered.logits == reference_results[3].logits,
              "execution-fault recovery: retried step 3 bit-identical to reference");

        CachedStepResult last = model.step(cache, schedule[4].tokens, schedule[4].start_position);
        check(last.logits == reference_results[4].logits,
              "execution-fault recovery: final step 4 bit-identical to reference");
        check(caches_physically_identical(cache, reference_cache),
              "execution-fault recovery: complete physical KV-cache contents identical to reference after the "
              "full sequence completes despite the mid-sequence fault and retry");
    }

    // Position-invariant proof: the safe step() entry point rejects a
    // position mismatch before any mutation, mirroring Phase 5A's own
    // P5A-RVW-002 guard (forward_cached_step / VirtualizedCachedModel::step).
    {
        std::printf("\n--- Position-invariant enforcement ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize),
                                     plan_first_k(layer_costs, layer_costs[0].resident_bytes));
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        model.step(cache, schedule[0].tokens, schedule[0].start_position);
        bool threw = false;
        try {
            model.step(cache, schedule[1].tokens, schedule[1].start_position + 1);  // wrong position
        } catch (const KVCacheError&) {
            threw = true;
        }
        check(threw, "step(): position mismatch rejected with KVCacheError before any mutation");
        check(cache.current_length() == schedule[0].start_position + static_cast<int64_t>(schedule[0].tokens.size()),
              "step(): rejected position-mismatch call left cache length unchanged");
    }
}

}  // namespace

int main(int argc, char** argv) {
    try {
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
        run_cached_decode_tests(fixtures_dir);

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) {
            std::printf("ALL CHECKS PASSED\n");
            return 0;
        }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] unexpected exception: %s\n", ex.what());
        return 1;
    }
}
