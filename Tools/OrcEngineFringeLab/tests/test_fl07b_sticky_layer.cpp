// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B commit 1 synthetic tests: (a) pure planner unit tests over
// synthetic LayerCostInfo descriptors, no model required; (b)
// StickyLayerModel residency-invariant and bit-exactness tests against the
// same in-memory synthetic fixture FL-07's own test uses (Phase 1's
// load_fixture), reusing FL-07's ModelSourceBinding pattern.
#include <cstdio>
#include <limits>
#include <string>
#include <vector>

#include "fringelab/sticky_layer_model.hpp"
#include "fringelab/sticky_layer_plan.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/materialization.hpp"

using namespace orcengine;
using namespace fringelab;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

// ---------------------------------------------------------------------
// Synthetic LayerCostInfo helpers (planner tests -- no model needed).
// ---------------------------------------------------------------------
std::vector<LayerCostInfo> uniform_layers(int64_t n, uint64_t bytes_each) {
    std::vector<LayerCostInfo> out;
    out.reserve(static_cast<size_t>(n));
    for (int64_t i = 0; i < n; ++i) {
        LayerCostInfo l;
        l.layer_id = i;
        l.resident_bytes = bytes_each;
        l.backing_bytes = bytes_each;
        l.benefit_estimate = 1.0;  // uniform -- no discriminating ranking
        l.cost_source = CostSource::Synthetic;
        out.push_back(l);
    }
    return out;
}

void run_planner_tests() {
    std::printf("=== Planner unit tests (synthetic descriptors) ===\n");

    // --- FirstKBaseline: empty/full/partial ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(5, 100);
        StickyLayerPlan p = plan_first_k(layers, 0);
        check(p.sticky_layer_ids().empty(), "FirstK budget=0: empty set (equivalent to full streaming)");
        check(p.planned_resident_bytes() == 0, "FirstK budget=0: zero planned bytes");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(5, 100);
        StickyLayerPlan p = plan_first_k(layers, 500);
        check(p.sticky_layer_ids().size() == 5, "FirstK budget=500 (exact total): all 5 layers selected");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(5, 100);
        StickyLayerPlan p = plan_first_k(layers, 250);
        check(p.sticky_layer_ids() == std::vector<int64_t>({0, 1}),
              "FirstK budget=250: exactly layers {0,1} selected (partial prefix)");
    }

    // --- ExplicitSet: non-prefix, invalid, duplicate, insufficient budget ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(6, 100);
        StickyLayerPlan p = plan_explicit_set(layers, {0, 3, 5}, 1000);
        check(p.sticky_layer_ids() == std::vector<int64_t>({0, 3, 5}),
              "ExplicitSet: non-prefix set {0,3,5} accepted and preserved exactly");
        check(p.planned_resident_bytes() == 300, "ExplicitSet: planned bytes == 3*100");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(4, 100);
        bool threw = false;
        try {
            plan_explicit_set(layers, {0, 4}, 1000);  // 4 is out of range for n=4 (valid ids 0..3)
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "ExplicitSet: out-of-range layer id rejected with StickyPlanError");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(4, 100);
        bool threw = false;
        try {
            plan_explicit_set(layers, {1, 1, 2}, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "ExplicitSet: duplicate layer id rejected with StickyPlanError");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(4, 100);
        bool threw = false;
        try {
            plan_explicit_set(layers, {0, 1, 2}, 250);  // needs 300, budget 250
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "ExplicitSet: insufficient budget rejected with StickyPlanError");
    }

    // --- Deterministic selection: repeat calls give identical results ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(8, 100);
        StickyLayerPlan p1 = plan_first_k(layers, 550);
        StickyLayerPlan p2 = plan_first_k(layers, 550);
        check(p1.sticky_layer_ids() == p2.sticky_layer_ids(), "FirstK: deterministic across repeated calls");
    }

    // --- MaterializationCostPerByte: unknown-cost rejection ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        layers[1].benefit_estimate = std::nullopt;
        bool threw = false;
        try {
            plan_cost_per_byte(layers, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "CostPerByte: layer with no benefit_estimate rejected, not silently treated as zero");
    }

    // --- MaterializationCostPerByte: byte budget respected, higher benefit-per-byte preferred ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(4, 100);
        layers[0].benefit_estimate = 1.0;   // 0.01 per byte
        layers[1].benefit_estimate = 5.0;   // 0.05 per byte -- best
        layers[2].benefit_estimate = 3.0;   // 0.03 per byte
        layers[3].benefit_estimate = 0.5;   // 0.005 per byte -- worst
        StickyLayerPlan p = plan_cost_per_byte(layers, 200);  // room for exactly 2 layers
        check(p.sticky_layer_ids() == std::vector<int64_t>({1, 2}),
              "CostPerByte: picks the two highest benefit-per-byte layers (1, 2) within budget");
    }

    // --- MaterializationCostPerByte: deterministic tie-break by ascending layer_id ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(4, 100);
        for (auto& l : layers) l.benefit_estimate = 1.0;  // exact tie on benefit-per-byte
        StickyLayerPlan p = plan_cost_per_byte(layers, 250);  // room for exactly 2 layers
        check(p.sticky_layer_ids() == std::vector<int64_t>({0, 1}),
              "CostPerByte: exact ties broken deterministically by ascending layer_id");
        StickyLayerPlan p2 = plan_cost_per_byte(layers, 250);
        check(p.sticky_layer_ids() == p2.sticky_layer_ids(), "CostPerByte: deterministic across repeated calls");
    }

    // --- Arithmetic overflow rejection ---
    {
        std::vector<LayerCostInfo> layers;
        LayerCostInfo a;
        a.layer_id = 0;
        a.resident_bytes = std::numeric_limits<uint64_t>::max() - 10;
        a.benefit_estimate = 1.0;
        LayerCostInfo b;
        b.layer_id = 1;
        b.resident_bytes = 100;  // a.resident_bytes + b.resident_bytes overflows uint64_t
        b.benefit_estimate = 1.0;
        layers.push_back(a);
        layers.push_back(b);
        bool threw = false;
        try {
            plan_first_k(layers, std::numeric_limits<uint64_t>::max());
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "FirstK: resident-byte accumulation overflow fails closed with StickyPlanError");
    }

    // --- validate_plan catches a hand-corrupted claimed-byte-total plan ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        StickyLayerPlan corrupted("Corrupted", 1000, {0, 1}, 999 /* wrong: should be 200 */);
        bool threw = false;
        try {
            validate_plan(corrupted, layers);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "validate_plan: rejects a plan whose claimed byte total does not match actual sum");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        StickyLayerPlan unsorted("Unsorted", 1000, {1, 0}, 200);
        bool threw = false;
        try {
            validate_plan(unsorted, layers);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "validate_plan: rejects a plan whose sticky_layer_ids() is not sorted ascending");
    }
}

// ---------------------------------------------------------------------
// Residency-invariant and bit-exactness tests (synthetic in-memory model).
// ---------------------------------------------------------------------
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

std::vector<LayerCostInfo> layer_costs_from_model(const Model& model) {
    std::vector<LayerCostInfo> out;
    for (int64_t layer = 0; layer < model.config().n_layers; ++layer) {
        const LayerWeights& w = model.layers.at(static_cast<size_t>(layer));
        uint64_t bytes = 0;
        for (const ResidentView* v : {&w.attn_norm_weight, &w.w_q, &w.w_k, &w.w_v, &w.w_o, &w.ffn_norm_weight,
                                      &w.w_gate, &w.w_up, &w.w_down}) {
            bytes += static_cast<uint64_t>(v->raw().size()) * sizeof(float);
        }
        LayerCostInfo l;
        l.layer_id = layer;
        l.resident_bytes = bytes;
        l.backing_bytes = bytes;
        l.benefit_estimate = 1.0;
        l.cost_source = CostSource::Measured;
        out.push_back(l);
    }
    return out;
}

void run_residency_tests(const std::string& fixtures_dir) {
    std::printf("\n=== StickyLayerModel residency-invariant and bit-exactness tests ===\n");
    LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_untied.txt");
    const ModelConfig& cfg = fx.model.config();
    std::vector<LayerCostInfo> layer_costs = layer_costs_from_model(fx.model);

    ForwardResult reference = forward(fx.model, fx.token_ids);
    std::vector<float> reference_last(reference.logits.end() - cfg.vocab, reference.logits.end());

    auto bit_identical_to_reference = [&](const ForwardResult& result, const std::string& label) {
        std::vector<float> result_last(result.logits.end() - cfg.vocab, result.logits.end());
        bool ok = result_last.size() == reference_last.size();
        for (size_t i = 0; ok && i < result_last.size(); ++i) {
            if (result_last[i] != reference_last[i]) ok = false;
        }
        check(ok, label + ": logits bit-identical to Phase 1 reference");
        check(result.selected_token == reference.selected_token, label + ": selected token matches reference");
    };

    // Plans under test: zero sticky, one sticky (first), a non-prefix
    // explicit set, a prefix set at the same byte budget, ~half sticky, all
    // sticky.
    const uint64_t one_layer_bytes = layer_costs.empty() ? 0 : layer_costs[0].resident_bytes;
    struct NamedPlan {
        std::string label;
        StickyLayerPlan plan;
    };
    std::vector<NamedPlan> plans;
    plans.push_back({"zero-sticky", plan_first_k(layer_costs, 0)});
    plans.push_back({"one-sticky-first", plan_first_k(layer_costs, one_layer_bytes)});
    if (cfg.n_layers >= 2) {
        plans.push_back({"non-prefix-explicit",
                         plan_explicit_set(layer_costs, {cfg.n_layers - 1, 0}, layer_costs[0].resident_bytes * 2)});
        plans.push_back({"prefix-same-budget", plan_first_k(layer_costs, layer_costs[0].resident_bytes * 2)});
    }
    const uint64_t half_budget =
        one_layer_bytes * static_cast<uint64_t>(cfg.n_layers / 2 == 0 ? 1 : cfg.n_layers / 2);
    plans.push_back({"about-half-sticky", plan_first_k(layer_costs, half_budget)});
    uint64_t all_bytes = 0;
    for (const auto& l : layer_costs) all_bytes += l.resident_bytes;
    plans.push_back({"all-sticky", plan_first_k(layer_costs, all_bytes)});

    for (const NamedPlan& np : plans) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerModel model(std::move(binding.source), std::move(binding.materialize), np.plan);
        ForwardResult result = model.forward(fx.token_ids);
        bit_identical_to_reference(result, np.label);
        check(model.telemetry().peak_active_layers <= 1, np.label + ": peak_active_layers <= 1 (cold-path invariant)");
    }

    // Sticky materialized once: materialization_count must not increase
    // across repeated forward() calls for a plan with >=1 sticky layer.
    if (cfg.n_layers >= 1) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerPlan p = plan_first_k(layer_costs, one_layer_bytes);
        StickyLayerModel model(std::move(binding.source), std::move(binding.materialize), p);
        model.forward(fx.token_ids);
        const uint64_t after_call_1 = model.telemetry().materialization_count;
        model.forward(fx.token_ids);
        const uint64_t after_call_2 = model.telemetry().materialization_count;
        // materialization_count increments once PER TENSOR (9 per layer),
        // not once per layer -- see ResidencyLedger::materialized().
        const uint64_t expected_cold_per_call =
            (static_cast<uint64_t>(cfg.n_layers) - p.sticky_layer_ids().size()) * 9;
        check(after_call_2 - after_call_1 == expected_cold_per_call,
              "one sticky layer: second forward() call re-materializes only the COLD layers, never the sticky one");
    }

    // Cold materialized per visit + released: with zero sticky layers, every
    // call must show the same number of new materializations.
    {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerModel model(std::move(binding.source), std::move(binding.materialize), plan_first_k(layer_costs, 0));
        model.forward(fx.token_ids);
        const uint64_t after_call_1 = model.telemetry().materialization_count;
        model.forward(fx.token_ids);
        const uint64_t after_call_2 = model.telemetry().materialization_count;
        check(after_call_2 - after_call_1 == static_cast<uint64_t>(cfg.n_layers) * 9,
              "zero sticky: every forward() call re-materializes all n_layers cold layers");
    }

    // Fault injection: cold materialization failure must leave the model in
    // a state where a SUBSEQUENT successful call still works and is still
    // bit-exact -- proving ledger cleanup on the failure path, not just
    // "the exception propagated".
    if (cfg.n_layers >= 2) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerPlan p = plan_first_k(layer_costs, one_layer_bytes);
        StickyLayerModel model(std::move(binding.source), std::move(binding.materialize), p);
        int64_t fault_layer = p.sticky_layer_ids().empty() ? 1 : p.sticky_layer_ids().back() + 1;
        if (fault_layer >= cfg.n_layers) fault_layer = cfg.n_layers - 1;
        model.fault_before_cold_execute = [&](int64_t layer) {
            if (layer == fault_layer) throw std::runtime_error("FL-07B injected cold-execution fault");
        };
        bool threw = false;
        try {
            model.forward(fx.token_ids);
        } catch (const std::exception&) {
            threw = true;
        }
        check(threw, "injected cold-execution fault: forward() propagates the exception");
        model.fault_before_cold_execute = nullptr;
        ForwardResult recovered = model.forward(fx.token_ids);
        bit_identical_to_reference(recovered, "post-fault recovery");
    }
    if (cfg.n_layers >= 2) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerPlan p = plan_first_k(layer_costs, one_layer_bytes);
        StickyLayerModel model(std::move(binding.source), std::move(binding.materialize), p);
        int64_t fault_layer = p.sticky_layer_ids().empty() ? 1 : p.sticky_layer_ids().back() + 1;
        if (fault_layer >= cfg.n_layers) fault_layer = cfg.n_layers - 1;
        model.fault_before_cold_materialize = [&](int64_t layer) {
            if (layer == fault_layer) throw std::runtime_error("FL-07B injected cold-materialize fault");
        };
        bool threw = false;
        try {
            model.forward(fx.token_ids);
        } catch (const std::exception&) {
            threw = true;
        }
        check(threw, "injected cold-materialize fault: forward() propagates the exception");
        model.fault_before_cold_materialize = nullptr;
        ForwardResult recovered = model.forward(fx.token_ids);
        bit_identical_to_reference(recovered, "post-materialize-fault recovery");
    }
}

}  // namespace

int main(int argc, char** argv) {
    try {
        run_planner_tests();
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
        run_residency_tests(fixtures_dir);

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
