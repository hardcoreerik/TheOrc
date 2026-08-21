// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B commit 1 + commit 1A synthetic tests: (a) pure planner unit tests
// over synthetic LayerCostInfo descriptors, no model required; (b)
// StickyLayerModel residency-invariant, byte-invariant, and bit-exactness
// tests against the same in-memory synthetic fixture FL-07's own test uses
// (Phase 1's load_fixture); (c) source-derived plan validation negative
// tests; (d) identity-aware materialization counting and partial/execution
// fault-injection tests with exact ledger-state assertions.
//
// Commit 1A note: the original 43/43-check Commit 1 evidence is preserved
// as history in EXPERIMENT.md -- this file supersedes (not merely extends)
// several of those checks with strictly stronger proof of the same claims
// (full-logit comparison instead of last-position-only; identity-aware
// materialization counts instead of aggregate counts alone; exact
// byte-residency-invariant assertions instead of inference from a later
// successful call).
#include <array>
#include <cmath>
#include <cstdio>
#include <limits>
#include <map>
#include <memory>
#include <optional>
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

    // --- MaterializationCostPerByte: benefit domain (Commit 1A) ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        layers[1].benefit_estimate = std::numeric_limits<double>::quiet_NaN();
        bool threw = false;
        try {
            plan_cost_per_byte(layers, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "CostPerByte: NaN benefit_estimate rejected");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        layers[1].benefit_estimate = std::numeric_limits<double>::infinity();
        bool threw = false;
        try {
            plan_cost_per_byte(layers, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "CostPerByte: +infinity benefit_estimate rejected");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        layers[1].benefit_estimate = -std::numeric_limits<double>::infinity();
        bool threw = false;
        try {
            plan_cost_per_byte(layers, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "CostPerByte: -infinity benefit_estimate rejected");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        layers[1].benefit_estimate = -0.5;
        bool threw = false;
        try {
            plan_cost_per_byte(layers, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "CostPerByte: negative benefit_estimate rejected");
    }
    {
        // Zero is explicitly ALLOWED -- ranked last among positive-benefit
        // layers, but still selectable if budget allows.
        std::vector<LayerCostInfo> layers = uniform_layers(3, 100);
        layers[0].benefit_estimate = 1.0;
        layers[1].benefit_estimate = 0.0;
        layers[2].benefit_estimate = 0.5;
        StickyLayerPlan p = plan_cost_per_byte(layers, 1000);  // budget for all 3
        check(p.sticky_layer_ids() == std::vector<int64_t>({0, 1, 2}),
              "CostPerByte: zero benefit_estimate is accepted (not rejected) when budget allows all layers");
        StickyLayerPlan p2 = plan_cost_per_byte(layers, 250);  // budget for exactly 2
        check(p2.sticky_layer_ids() == std::vector<int64_t>({0, 2}),
              "CostPerByte: zero-benefit layer ranked last and excluded first under a tight budget");
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

    // --- require_contiguous_layer_descriptors: gap and duplicate rejection (Commit 1A) ---
    {
        std::vector<LayerCostInfo> layers = uniform_layers(4, 100);
        layers[3].layer_id = 5;  // gap: ids are now {0,1,2,5}, not [0,4)
        bool threw = false;
        try {
            plan_first_k(layers, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "FirstK: layer descriptor id outside [0,n) (gap) rejected");
    }
    {
        std::vector<LayerCostInfo> layers = uniform_layers(4, 100);
        layers[3].layer_id = 1;  // duplicate id 1
        bool threw = false;
        try {
            plan_first_k(layers, 1000);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "FirstK: duplicate layer descriptor id rejected");
    }
}

// ---------------------------------------------------------------------
// Residency-invariant, byte-invariant, and bit-exactness tests
// (synthetic in-memory model, Commit 1A: source-derived validation,
// identity-aware materialization tracking, exact fault-injection cleanup
// proof).
// ---------------------------------------------------------------------

// Test-only materializer wrapper: counts materializations per logical
// tensor name (layer/tensor IDENTITY, not a fragile global call number),
// and can be configured to throw when a specific logical-tensor name is
// about to be materialized -- letting earlier tensors of the SAME cold
// layer succeed and be recorded before the fault fires, which the plain
// "throw before entering the layer" hook cannot exercise.
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
    int total_calls() const {
        int total = 0;
        for (const auto& [k, v] : counts_) total += v;
        return total;
    }

private:
    TensorMaterializer inner_;
    std::optional<std::string> fault_name_;
    std::map<std::string, int> counts_;
};

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

uint64_t largest_layer_bytes(const std::vector<LayerCostInfo>& layers) {
    uint64_t largest = 0;
    for (const auto& l : layers) largest = std::max(largest, l.resident_bytes);
    return largest;
}

void run_source_validation_negative_tests(const Model& model) {
    std::printf("\n=== Source-derived plan validation negative tests (Commit 1A) ===\n");
    const ModelConfig& cfg = model.config();
    if (cfg.n_layers < 2) {
        std::printf("[INFO] skipping source-validation negative tests: fixture has < 2 layers\n");
        return;
    }

    // Forged claimed byte total: hand-build a plan with a wrong byte total,
    // pass it to the model constructor directly (not through plan_first_k,
    // which would itself reject it) -- proves the MODEL constructor itself
    // re-validates, not merely the planner functions.
    {
        ModelSourceBinding binding = bind_memory_model(model);
        StickyLayerPlan forged("Forged", 1u << 30, {0}, 999999999ull);  // wildly wrong claimed total
        bool threw = false;
        try {
            StickyLayerModel bad(std::move(binding.source), std::move(binding.materialize), forged);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "StickyLayerModel: forged planned_resident_bytes rejected at construction");
    }

    // Insufficient budget: plan claims a byte total that is internally
    // self-consistent with actual sizes but exceeds its OWN declared budget.
    {
        ModelSourceBinding binding = bind_memory_model(model);
        std::vector<LayerCostInfo> actual = layer_costs_from_source(binding.source);
        StickyLayerPlan tight("TooTight", actual[0].resident_bytes / 2, {0}, actual[0].resident_bytes);
        bool threw = false;
        try {
            StickyLayerModel bad(std::move(binding.source), std::move(binding.materialize), tight);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "StickyLayerModel: sticky bytes exceeding the plan's own budget rejected at construction");
    }

    // Stale layer-size assumption: plan believes layer 0 is smaller than it
    // actually is, so its claimed total is wrong relative to the REAL
    // source -- must be rejected even though the plan is self-consistent
    // with the (incorrect) assumption it was built from.
    {
        ModelSourceBinding binding = bind_memory_model(model);
        std::vector<LayerCostInfo> actual = layer_costs_from_source(binding.source);
        const uint64_t stale_bytes = actual[0].resident_bytes > 100 ? actual[0].resident_bytes - 100
                                                                    : actual[0].resident_bytes + 100;
        StickyLayerPlan stale("Stale", stale_bytes, {0}, stale_bytes);
        bool threw = false;
        try {
            StickyLayerModel bad(std::move(binding.source), std::move(binding.materialize), stale);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "StickyLayerModel: plan built from a stale/incorrect layer-size assumption rejected");
    }

    // Duplicate semantic tensor role within a layer, in the ModelSource
    // itself (not the plan) -- layer_costs_from_source() must reject this
    // before any plan is even considered.
    {
        ModelSourceBinding binding = bind_memory_model(model);
        // Duplicate layer 0's AttentionQuery tensor a second time (as an
        // extra entry) so the layer now has 10 tensors with a repeated role.
        for (const SourceTensor& t : binding.source.tensors) {
            if (t.identity.layer == 0 && t.identity.role == TensorRole::AttentionQuery) {
                binding.source.tensors.push_back(t);
                break;
            }
        }
        bool threw = false;
        try {
            std::vector<LayerCostInfo> bad = layer_costs_from_source(binding.source);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "layer_costs_from_source: duplicate semantic tensor role within a layer rejected");
    }

    // Missing required tensor role within a layer.
    {
        ModelSourceBinding binding = bind_memory_model(model);
        auto& tensors = binding.source.tensors;
        for (auto it = tensors.begin(); it != tensors.end(); ++it) {
            if (it->identity.layer == 0 && it->identity.role == TensorRole::FfnDown) {
                tensors.erase(it);
                break;
            }
        }
        bool threw = false;
        try {
            std::vector<LayerCostInfo> bad = layer_costs_from_source(binding.source);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "layer_costs_from_source: missing required tensor role within a layer rejected");
    }

    // Missing contiguous layer id: delete every tensor of layer 1 entirely,
    // leaving a gap in [0, n_layers) even though tensors for layer 0 and
    // layer 2+ are all still present.
    if (cfg.n_layers >= 3) {
        ModelSourceBinding binding = bind_memory_model(model);
        auto& tensors = binding.source.tensors;
        tensors.erase(std::remove_if(tensors.begin(), tensors.end(),
                                     [](const SourceTensor& t) { return t.identity.layer == 1; }),
                     tensors.end());
        bool threw = false;
        try {
            std::vector<LayerCostInfo> bad = layer_costs_from_source(binding.source);
        } catch (const StickyPlanError&) {
            threw = true;
        }
        check(threw, "layer_costs_from_source: missing contiguous layer id (gap) rejected");
    }
}

void run_residency_tests(const std::string& fixtures_dir) {
    std::printf("\n=== StickyLayerModel residency-invariant, byte-invariant, and bit-exactness tests (Commit 1A) ===\n");
    LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_untied.txt");
    const ModelConfig& cfg = fx.model.config();

    ModelSourceBinding probe_binding = bind_memory_model(fx.model);
    const std::vector<LayerCostInfo> layer_costs = layer_costs_from_source(probe_binding.source);
    const uint64_t bookends = bookend_bytes(fx.model);

    run_source_validation_negative_tests(fx.model);

    ForwardResult reference = forward(fx.model, fx.token_ids);

    auto bit_identical_to_reference = [&](const ForwardResult& result, const std::string& label) {
        // Commit 1A: COMPLETE logits vector equality, not last-position-only.
        check(result.logits == reference.logits, label + ": COMPLETE logits vector bit-identical to Phase 1 reference");
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
        const uint64_t expected_baseline = bookends + np.plan.planned_resident_bytes();

        StickyLayerModel model(std::move(binding.source), std::move(binding.materialize), np.plan);
        check(model.sticky_resident_bytes() == np.plan.planned_resident_bytes(),
              np.label + ": sticky_resident_bytes() equals plan.planned_resident_bytes()");
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              np.label + ": current resident bytes immediately after construction equal bookends+sticky baseline");

        ForwardResult result = model.forward(fx.token_ids);
        bit_identical_to_reference(result, np.label);

        const bool any_cold_visited = np.plan.sticky_layer_ids().size() < static_cast<size_t>(cfg.n_layers);
        if (any_cold_visited) {
            check(model.telemetry().peak_active_layers == 1,
                  np.label + ": peak_active_layers == 1 (at least one cold layer actually visited)");
        } else {
            check(model.telemetry().peak_active_layers == 0,
                  np.label + ": peak_active_layers == 0 (all-sticky execution, no cold layer ever visited)");
        }
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              np.label + ": current resident bytes after a successful forward() return to the baseline");
        check(model.telemetry().peak_resident_weight_bytes <= expected_baseline + largest_layer_bytes(layer_costs),
              np.label + ": peak resident bytes never exceed baseline + the largest cold layer that can be visited");
    }

    // Numerically-identical-selection proof: FirstK and ExplicitSet with the
    // SAME total sticky bytes but a DIFFERENT selected set still produce
    // bit-identical results (both are still bit-exact against the Phase 1
    // reference, proven above for both "prefix-same-budget" and
    // "non-prefix-explicit" independently -- this just cross-checks them
    // against EACH OTHER directly for the same input).
    if (cfg.n_layers >= 3) {  // need n_layers-1 != 1 so {0,n_layers-1} != {0,1}
        ModelSourceBinding binding_a = bind_memory_model(fx.model);
        ModelSourceBinding binding_b = bind_memory_model(fx.model);
        StickyLayerPlan prefix_plan = plan_first_k(layer_costs, layer_costs[0].resident_bytes * 2);
        StickyLayerPlan explicit_plan = plan_explicit_set(layer_costs, {cfg.n_layers - 1, 0}, layer_costs[0].resident_bytes * 2);
        check(prefix_plan.sticky_layer_ids() != explicit_plan.sticky_layer_ids(),
              "FirstK and non-prefix ExplicitSet at equal sticky bytes select DIFFERENT layer sets (sanity check)");
        StickyLayerModel model_a(std::move(binding_a.source), std::move(binding_a.materialize), prefix_plan);
        StickyLayerModel model_b(std::move(binding_b.source), std::move(binding_b.materialize), explicit_plan);
        ForwardResult result_a = model_a.forward(fx.token_ids);
        ForwardResult result_b = model_b.forward(fx.token_ids);
        check(result_a.logits == result_b.logits,
              "FirstK vs non-prefix ExplicitSet at equal sticky bytes: numerically identical complete logits");
    }

    // Identity-aware materialization proof (Commit 1A): using
    // TrackingMaterializer, prove per-tensor-identity counts, not just
    // aggregate totals.
    if (cfg.n_layers >= 1) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerPlan p = plan_first_k(layer_costs, one_layer_bytes);
        StickyLayerModel model(std::move(binding.source), wrapped, p);
        model.forward(fx.token_ids);
        model.forward(fx.token_ids);
        model.forward(fx.token_ids);

        check(tracker->count("layer0.attention_query") == 1,
              "identity-aware: sticky layer 0's tensor materialized exactly ONCE across 3 forward() calls "
              "(during construction only)");
        if (cfg.n_layers >= 2) {
            check(tracker->count("layer1.attention_query") == 3,
                  "identity-aware: cold layer 1's tensor materialized exactly once PER forward() call (3 calls)");
        }
    }

    // Zero-sticky: every layer's every tensor materializes on every call.
    {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerModel model(std::move(binding.source), wrapped, plan_first_k(layer_costs, 0));
        model.forward(fx.token_ids);
        model.forward(fx.token_ids);
        check(tracker->count("layer0.attention_query") == 2,
              "zero-sticky: layer 0 (cold) materialized exactly once per call across 2 calls");
    }

    // All-sticky: zero NEW transformer-layer materializations occur during
    // forward() itself (everything was already materialized at construction).
    {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerModel model(std::move(binding.source), wrapped, plan_first_k(layer_costs, all_bytes));
        const int before = tracker->count("layer0.attention_query");
        model.forward(fx.token_ids);
        const int after = tracker->count("layer0.attention_query");
        check(before == 1 && after == 1,
              "all-sticky: forward() performs ZERO new transformer-layer materializations (already resident)");
    }

    // Fault injection 1: PARTIAL cold-layer materialization failure. Uses
    // TrackingMaterializer to let SOME tensors of the cold layer succeed
    // before the fault fires on a later tensor of the SAME layer.
    if (cfg.n_layers >= 2) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerPlan p = plan_first_k(layer_costs, one_layer_bytes);  // layer 0 sticky, layer 1+ cold
        const int64_t cold_layer = 1;
        const std::string fault_tensor = "layer" + std::to_string(cold_layer) + ".ffn_down";  // 8th of 9 tensors
        const uint64_t expected_baseline = bookends + p.planned_resident_bytes();

        StickyLayerModel model(std::move(binding.source), wrapped, p);
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              "partial-materialize-fault: baseline correct immediately after construction");

        tracker->set_fault(fault_tensor);
        bool threw = false;
        try {
            model.forward(fx.token_ids);
        } catch (const std::exception&) {
            threw = true;
        }
        tracker->clear_fault();

        check(threw, "partial-materialize-fault: forward() propagates the exception");
        check(model.telemetry().current_layer == -1, "partial-materialize-fault: telemetry.current_layer == -1 immediately after");
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              "partial-materialize-fault: current resident bytes return EXACTLY to bookends+sticky baseline "
              "(no partial cold-layer bytes remain resident)");

        ForwardResult recovered = model.forward(fx.token_ids);
        bit_identical_to_reference(recovered, "partial-materialize-fault recovery");
        check(tracker->count("layer0.attention_query") == 1,
              "partial-materialize-fault: sticky layer 0 was NOT rematerialized by the failed or recovery call");
    }

    // Fault injection 2: cold-layer EXECUTION failure, occurring AFTER the
    // cold layer's weights are fully resident (all 9 tensors materialized).
    if (cfg.n_layers >= 2) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerPlan p = plan_first_k(layer_costs, one_layer_bytes);
        const uint64_t expected_baseline = bookends + p.planned_resident_bytes();
        int64_t fault_layer = p.sticky_layer_ids().empty() ? 1 : p.sticky_layer_ids().back() + 1;
        if (fault_layer >= cfg.n_layers) fault_layer = cfg.n_layers - 1;

        StickyLayerModel model(std::move(binding.source), std::move(binding.materialize), p);
        model.fault_before_cold_execute = [&](int64_t layer) {
            if (layer == fault_layer) throw std::runtime_error("FL-07B injected cold-execution fault");
        };
        bool threw = false;
        try {
            model.forward(fx.token_ids);
        } catch (const std::exception&) {
            threw = true;
        }
        model.fault_before_cold_execute = nullptr;

        check(threw, "cold-execution-fault: forward() propagates the exception");
        check(model.telemetry().current_layer == -1, "cold-execution-fault: telemetry.current_layer == -1 immediately after");
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              "cold-execution-fault: current resident bytes return EXACTLY to bookends+sticky baseline immediately "
              "after the failure (not merely inferred from a later successful call)");

        ForwardResult recovered = model.forward(fx.token_ids);
        bit_identical_to_reference(recovered, "cold-execution-fault recovery");
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
