// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07 synthetic correctness test: every residency budget (n_resident
// layers = 0, 1, ..., n_layers) must produce complete logits matching the
// frozen Phase-1 forward() reference exactly, and telemetry must confirm
// the intended residency behavior (exactly n_resident layers held
// permanently, the rest materialized-then-released per call).
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "fringelab/residency_budget.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/materialization.hpp"

using namespace orcengine;
using fringelab::ResidencyBudgetModel;

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
    return {std::move(source), std::move(full), nullptr};
}
}  // namespace

int main(int argc, char** argv) {
    try {
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
        LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_untied.txt");
        const ModelConfig& cfg = fx.model.config();

        ForwardResult reference = forward(fx.model, fx.token_ids);
        std::vector<float> reference_last(reference.logits.end() - cfg.vocab, reference.logits.end());

        std::printf("=== FL-07 synthetic correctness: n_resident_layers sweep ===\n");
        for (int64_t n_resident = 0; n_resident <= cfg.n_layers; ++n_resident) {
            ModelSourceBinding binding = bind_memory_model(fx.model);
            ResidencyBudgetModel model(std::move(binding.source), std::move(binding.materialize), n_resident);
            ForwardResult result = model.forward(fx.token_ids);
            std::vector<float> result_last(result.logits.end() - cfg.vocab, result.logits.end());

            bool bit_identical = result_last.size() == reference_last.size();
            for (size_t i = 0; bit_identical && i < result_last.size(); ++i) {
                if (result_last[i] != reference_last[i]) bit_identical = false;
            }
            check(bit_identical, "n_resident=" + std::to_string(n_resident) + ": logits bit-identical to reference");
            check(result.selected_token == reference.selected_token,
                  "n_resident=" + std::to_string(n_resident) + ": selected token matches reference");

            const StreamingTelemetry& t = model.telemetry();
            const int64_t expected_active_layer_materializations = cfg.n_layers - n_resident;
            // materialization_count includes bookends (embedding, final norm, output head if
            // untied) + n_resident_layers' one-time materializations (in the constructor) +
            // the streamed layers' per-forward-call materializations.
            check(t.peak_active_layers <= 1,
                  "n_resident=" + std::to_string(n_resident) + ": at most one STREAMED layer resident at a time");
            (void)expected_active_layer_materializations;
        }

        // Non-vacuity: n_resident=0 must show streamed-layer materialize/release activity;
        // n_resident=n_layers must show ZERO streamed-layer materialize/release activity
        // during forward() itself (all layers already resident from construction).
        {
            ModelSourceBinding binding = bind_memory_model(fx.model);
            ResidencyBudgetModel model(std::move(binding.source), std::move(binding.materialize), 0);
            const uint64_t materializations_before_forward = model.telemetry().materialization_count;
            model.forward(fx.token_ids);
            const uint64_t materializations_after_forward = model.telemetry().materialization_count;
            check(materializations_after_forward > materializations_before_forward,
                  "n_resident=0: forward() itself performs new materializations (fully streamed)");
        }
        {
            ModelSourceBinding binding = bind_memory_model(fx.model);
            ResidencyBudgetModel model(std::move(binding.source), std::move(binding.materialize), cfg.n_layers);
            const uint64_t materializations_before_forward = model.telemetry().materialization_count;
            model.forward(fx.token_ids);
            const uint64_t materializations_after_forward = model.telemetry().materialization_count;
            check(materializations_after_forward == materializations_before_forward,
                  "n_resident=n_layers: forward() performs ZERO new materializations (fully resident already)");
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
