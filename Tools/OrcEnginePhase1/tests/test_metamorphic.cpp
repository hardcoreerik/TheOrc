// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase-1 storage metamorphisms. These tests prove only the properties they
// execute: F32 backing bytes can create distinct ResidentViews without
// changing inference, and tied inference semantics do not require pointer
// identity. They do not claim GGUF, disk, paging, or CUDA support.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/materialization.hpp"
#include "orcengine/validation.hpp"

using namespace orcengine;

namespace {

int g_failures = 0;

bool logits_bit_identical(const std::vector<float>& a, const std::vector<float>& b,
                          const std::string& label) {
    if (a.size() != b.size()) {
        std::printf("[FAIL] %s: size mismatch\n", label.c_str());
        ++g_failures;
        return false;
    }
    const bool identical = std::memcmp(a.data(), b.data(), a.size() * sizeof(float)) == 0;
    float max_diff = 0.0f;
    for (size_t i = 0; i < a.size(); ++i) {
        max_diff = std::max(max_diff, std::fabs(a[i] - b[i]));
    }
    std::printf("%s %s: max_diff=%.9g (%zu elements)\n",
                identical ? "[PASS]" : "[FAIL]", label.c_str(), max_diff, a.size());
    if (!identical) ++g_failures;
    return identical;
}

struct BackedTensor {
    LogicalTensor logical;
    BackingExtent backing;
};

using ModelBackings = std::vector<BackedTensor>;

void add_backing(ModelBackings& out, const std::string& name, const ResidentView& view) {
    out.push_back({LogicalTensor(name, view.shape()), BackingExtent::FromF32(view.raw())});
}

ModelBackings create_backings(const Model& model) {
    ModelBackings out;
    out.reserve(static_cast<size_t>(2 + model.layers.size() * 9 + (model.lm_head.has_value() ? 1 : 0)));
    add_backing(out, "token_embedding", model.token_embedding);
    if (model.lm_head.has_value()) add_backing(out, "lm_head", *model.lm_head);
    add_backing(out, "final_norm_weight", model.final_norm_weight);
    for (size_t i = 0; i < model.layers.size(); ++i) {
        const std::string p = "layer" + std::to_string(i) + ".";
        const LayerWeights& layer = model.layers[i];
        add_backing(out, p + "attn_norm_weight", layer.attn_norm_weight);
        add_backing(out, p + "w_q", layer.w_q);
        add_backing(out, p + "w_k", layer.w_k);
        add_backing(out, p + "w_v", layer.w_v);
        add_backing(out, p + "w_o", layer.w_o);
        add_backing(out, p + "ffn_norm_weight", layer.ffn_norm_weight);
        add_backing(out, p + "w_gate", layer.w_gate);
        add_backing(out, p + "w_up", layer.w_up);
        add_backing(out, p + "w_down", layer.w_down);
    }
    return out;
}

Model materialize_model(const Model& shape_source, const ModelBackings& backings) {
    Model out = shape_source;
    size_t cursor = 0;
    auto next = [&]() -> ResidentView {
        const BackedTensor& tensor = backings.at(cursor++);
        return materialize(tensor.logical, tensor.backing);
    };

    out.token_embedding = next();
    if (shape_source.lm_head.has_value()) out.lm_head = next();
    out.final_norm_weight = next();
    for (LayerWeights& layer : out.layers) {
        layer.attn_norm_weight = next();
        layer.w_q = next();
        layer.w_k = next();
        layer.w_v = next();
        layer.w_o = next();
        layer.ffn_norm_weight = next();
        layer.w_gate = next();
        layer.w_up = next();
        layer.w_down = next();
    }
    if (cursor != backings.size()) throw ValidationError("unused model backing tensors");
    return out;
}

void verify_view(const std::string& name, const ResidentView& first, const ResidentView& second) {
    const bool distinct = first.raw().data() != second.raw().data();
    const bool same_shape = first.shape() == second.shape();
    const bool same_values = first.raw() == second.raw();
    std::printf("%s %s: distinct_address=%s same_shape=%s same_values=%s\n",
                distinct && same_shape && same_values ? "[PASS]" : "[FAIL]", name.c_str(),
                distinct ? "yes" : "no", same_shape ? "yes" : "no", same_values ? "yes" : "no");
    if (!distinct || !same_shape || !same_values) ++g_failures;
}

void verify_all_views(const Model& first, const Model& second) {
    verify_view("token_embedding", first.token_embedding, second.token_embedding);
    if (first.lm_head.has_value() && second.lm_head.has_value()) {
        verify_view("lm_head", *first.lm_head, *second.lm_head);
    }
    verify_view("final_norm_weight", first.final_norm_weight, second.final_norm_weight);
    for (size_t i = 0; i < first.layers.size(); ++i) {
        const std::string p = "layer" + std::to_string(i) + ".";
        verify_view(p + "attn_norm_weight", first.layers[i].attn_norm_weight, second.layers[i].attn_norm_weight);
        verify_view(p + "w_q", first.layers[i].w_q, second.layers[i].w_q);
        verify_view(p + "w_k", first.layers[i].w_k, second.layers[i].w_k);
        verify_view(p + "w_v", first.layers[i].w_v, second.layers[i].w_v);
        verify_view(p + "w_o", first.layers[i].w_o, second.layers[i].w_o);
        verify_view(p + "ffn_norm_weight", first.layers[i].ffn_norm_weight, second.layers[i].ffn_norm_weight);
        verify_view(p + "w_gate", first.layers[i].w_gate, second.layers[i].w_gate);
        verify_view(p + "w_up", first.layers[i].w_up, second.layers[i].w_up);
        verify_view(p + "w_down", first.layers[i].w_down, second.layers[i].w_down);
    }
}

}  // namespace

int main(int argc, char** argv) {
    try {
        const std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
        LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_tied.txt");
        const ForwardResult fixture_result = forward(fx.model, fx.token_ids);

        std::printf("=== Metamorphic property 1: all weights materialize from BackingExtent ===\n");
        const ModelBackings backings = create_backings(fx.model);
        Model first_resident = materialize_model(fx.model, backings);
        verify_all_views(fx.model, first_resident);
        const ForwardResult first_result = forward(first_resident, fx.token_ids);
        logits_bit_identical(fixture_result.logits, first_result.logits,
                              "fixture residents vs backing-materialized residents");

        std::printf("\n=== Metamorphic property 2: tied alias vs tied duplicate ===\n");
        Model tied_duplicate = fx.model;
        const BackedTensor duplicate_head{
            LogicalTensor("lm_head", fx.model.token_embedding.shape()),
            BackingExtent::FromF32(fx.model.token_embedding.raw())};
        tied_duplicate.lm_head = materialize(duplicate_head.logical, duplicate_head.backing);
        verify_view("tied lm_head duplicate", fx.model.token_embedding, *tied_duplicate.lm_head);
        const ForwardResult tied_duplicate_result = forward(tied_duplicate, fx.token_ids);
        logits_bit_identical(fixture_result.logits, tied_duplicate_result.logits,
                              "tied semantics: alias vs byte-identical duplicate");

        std::printf("\n=== Metamorphic property 3: rematerialize from retained BackingExtents ===\n");
        Model second_resident = materialize_model(fx.model, backings);
        // Both generations coexist here, forcing every resident allocation to
        // have a distinct address before the first generation is destroyed.
        verify_all_views(first_resident, second_resident);
        first_resident = Model{};
        const ForwardResult second_result = forward(second_resident, fx.token_ids);
        logits_bit_identical(first_result.logits, second_result.logits,
                              "first vs second materialization from same backings");

        if (g_failures == 0) {
            std::printf("ALL METAMORPHIC PROPERTIES HOLD FOR PHASE-1 F32 BACKINGS\n");
            return 0;
        }
        std::printf("%d METAMORPHIC FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
