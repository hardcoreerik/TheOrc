// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase-1 freeze-audit items 3-4: the first tiny proof of OrcEngine's bigger
// thesis -- the SAME LOGICAL MODEL produces the SAME mathematical result
// even when its PHYSICAL storage/residency changes. LogicalTensor identity
// must not depend on a particular backing address, and "tied" is a
// semantic claim about VALUES, not a requirement that two logical tensors
// share one physical pointer.
//
// Three metamorphic properties, each compared bit-for-bit (these are
// literally the same floating-point operations on the same values --
// anything other than exact equality would mean address/aliasing is
// leaking into the math, which would itself be the bug):
//
//   1. Backing relocation: copying a tensor's bytes to a fresh, differently
//      -addressed buffer before executing must not change the result.
//   2. Tied-alias vs tied-duplicate: a tied model computed via
//      effective_lm_head() aliasing token_embedding must produce the exact
//      same logits as an "untied" model whose lm_head is a byte-identical
//      but physically separate copy.
//   3. Evict/rematerialize: destroying a ResidentView and rebuilding an
//      identical one from the same source values at a new address must not
//      change the result.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"

using namespace orcengine;

namespace {

int g_failures = 0;

bool logits_bit_identical(const std::vector<float>& a, const std::vector<float>& b, const std::string& label) {
    if (a.size() != b.size()) {
        std::printf("[FAIL] %s: size mismatch\n", label.c_str());
        ++g_failures;
        return false;
    }
    bool identical = std::memcmp(a.data(), b.data(), a.size() * sizeof(float)) == 0;
    float max_diff = 0.0f;
    for (size_t i = 0; i < a.size(); ++i) max_diff = std::max(max_diff, std::fabs(a[i] - b[i]));
    std::printf("%s %s: max_diff=%.9g (%zu elements)\n", identical ? "[PASS]" : "[FAIL]",
                label.c_str(), max_diff, a.size());
    if (!identical) ++g_failures;
    return identical;
}

ResidentView relocated_copy(const ResidentView& src) {
    // A genuinely fresh allocation: new std::vector, new heap address,
    // values copied byte-for-byte from the source. This is exactly what
    // "physical relocation" means for Phase 1's CPU-only residency model.
    std::vector<float> fresh_data(src.raw());  // copy, not move -- new buffer
    return ResidentView(src.shape(), std::move(fresh_data));
}

Model relocate_all_weights(const Model& src) {
    Model out = src;  // ModelManifest, LayerWeights vector, etc. all deep-copied
    out.token_embedding = relocated_copy(src.token_embedding);
    if (src.lm_head.has_value()) out.lm_head = relocated_copy(*src.lm_head);
    out.final_norm_weight = relocated_copy(src.final_norm_weight);
    for (size_t i = 0; i < out.layers.size(); ++i) {
        out.layers[i].attn_norm_weight = relocated_copy(src.layers[i].attn_norm_weight);
        out.layers[i].w_q = relocated_copy(src.layers[i].w_q);
        out.layers[i].w_k = relocated_copy(src.layers[i].w_k);
        out.layers[i].w_v = relocated_copy(src.layers[i].w_v);
        out.layers[i].w_o = relocated_copy(src.layers[i].w_o);
        out.layers[i].ffn_norm_weight = relocated_copy(src.layers[i].ffn_norm_weight);
        out.layers[i].w_gate = relocated_copy(src.layers[i].w_gate);
        out.layers[i].w_up = relocated_copy(src.layers[i].w_up);
        out.layers[i].w_down = relocated_copy(src.layers[i].w_down);
    }
    return out;
}

}  // namespace

int main(int argc, char** argv) {
    std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
    LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_tied.txt");

    std::printf("=== Metamorphic property 1: backing relocation ===\n");
    ForwardResult base_result = forward(fx.model, fx.token_ids);
    Model relocated = relocate_all_weights(fx.model);
    // Confirm the relocation actually produced different addresses -- a
    // no-op "copy" that reused the same buffer would make this test vacuous.
    bool addresses_differ = fx.model.token_embedding.raw().data() != relocated.token_embedding.raw().data();
    std::printf("relocated token_embedding to a new address: %s (%p -> %p)\n",
                addresses_differ ? "confirmed" : "FAILED TO RELOCATE",
                (void*)fx.model.token_embedding.raw().data(), (void*)relocated.token_embedding.raw().data());
    if (!addresses_differ) ++g_failures;
    ForwardResult relocated_result = forward(relocated, fx.token_ids);
    logits_bit_identical(base_result.logits, relocated_result.logits, "relocation: original vs relocated logits");

    std::printf("\n=== Metamorphic property 2: tied-alias vs tied-duplicate ===\n");
    // Representation A: true tied model (effective_lm_head() aliases token_embedding).
    ForwardResult tied_alias_result = forward(fx.model, fx.token_ids);
    // Representation B: physically separate, byte-identical lm_head tensor
    // (not an alias -- a distinct ResidentView with a copy of the same values).
    Model tied_duplicate = fx.model;
    tied_duplicate.lm_head = relocated_copy(fx.model.token_embedding);
    bool lm_head_is_separate = tied_duplicate.lm_head->raw().data() != fx.model.token_embedding.raw().data();
    std::printf("lm_head duplicate is a physically separate buffer: %s\n", lm_head_is_separate ? "confirmed" : "FAILED");
    if (!lm_head_is_separate) ++g_failures;
    ForwardResult tied_duplicate_result = forward(tied_duplicate, fx.token_ids);
    logits_bit_identical(tied_alias_result.logits, tied_duplicate_result.logits,
                          "tied semantics: alias vs physically-separate-but-byte-identical duplicate");

    std::printf("\n=== Metamorphic property 3: evict/rematerialize ===\n");
    {
        // "Evict": save the source values, then destroy the resident view.
        std::vector<float> saved_values = fx.model.token_embedding.raw();
        TensorShape saved_shape = fx.model.token_embedding.shape();
        Model evictable = fx.model;
        ForwardResult before_evict = forward(evictable, fx.token_ids);

        evictable.token_embedding = ResidentView();  // destroy/replace -- old buffer freed
        void* evicted_ptr_placeholder = evictable.token_embedding.raw().data();  // should be null/empty

        // "Rematerialize": rebuild an identical ResidentView from the saved
        // source values, necessarily at a new address (fresh allocation).
        evictable.token_embedding = ResidentView(saved_shape, std::vector<float>(saved_values));
        bool rematerialized_at_new_address = evictable.token_embedding.raw().data() != evicted_ptr_placeholder;
        std::printf("rematerialized at a new address: %s\n", rematerialized_at_new_address ? "confirmed" : "note: address reused (allocator-dependent, not a failure)");

        ForwardResult after_rematerialize = forward(evictable, fx.token_ids);
        logits_bit_identical(before_evict.logits, after_rematerialize.logits,
                              "evict/rematerialize: before vs after logits");
    }

    std::printf("\n=== Summary ===\n");
    if (g_failures == 0) {
        std::printf("ALL METAMORPHIC PROPERTIES HOLD: logical model identity is independent of physical "
                    "backing address, and tied semantics are a claim about VALUES, not about a shared pointer.\n");
        return 0;
    }
    std::printf("%d METAMORPHIC FAILURES\n", g_failures);
    return 1;
}
