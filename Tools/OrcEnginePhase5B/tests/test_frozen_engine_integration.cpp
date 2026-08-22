// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B Section 11 frozen-engine integration proof (A3):
//   TokenizerProfile::encode() -- native Phase 5B, PROMPT ids only
//   -> frozen Phase 5A forward_cached_step() -- unmodified, CONTINUATION ids
//   -> TokenizerProfile::decode() / Utf8StreamDecoder -- native Phase 5B
//
// Calls Phase 1/2/5A's existing public seams (index_gguf, map_llama_model,
// materialize_gguf_model, forward_cached_step) exactly as
// Tools/OrcEnginePhase5A/tests/test_real_cache_attacks.cpp already does --
// no frozen Phase 1-5A source file is modified or reimplemented here.
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/tokenizer.hpp"

using namespace orcengine;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

bool caches_equal(const ContiguousAttentionKVStore& a, const ContiguousAttentionKVStore& b) {
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
}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) {
        std::fprintf(stderr, "usage: %s <canonical-explicit-gguf-path>\n", argv[0]);
        return 2;
    }
    try {
        const std::filesystem::path path = argv[1];

        // Same GGUF loaded twice, independently, for two distinct purposes
        // (tokenizer metadata vs. full model weights) -- both through
        // existing frozen public seams, neither modified.
        const GgufArtifact tokenizer_artifact = index_gguf(path);
        const TokenizerProfile profile = TokenizerProfile::from_gguf_metadata(tokenizer_artifact);

        const GgufArtifact model_artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(model_artifact);
        const Model model = materialize_gguf_model(manifest);
        const ModelConfig& cfg = model.config();

        std::printf("=== Section 11: native Phase 5B tokenizer -> frozen Phase 5A engine -> native Phase 5B decode ===\n");

        // --- Step 1: native Phase 5B encoding produces the exact
        // established PROMPT ids. The tokenizer produces PROMPT ids here --
        // it never sees or produces the model's own CONTINUATION ids below. ---
        const std::vector<int64_t> native_prompt_ids = profile.encode("Hello, world!");
        const std::vector<int64_t> expected_prompt_ids = {19556, 28, 905, 17};
        check(native_prompt_ids == expected_prompt_ids,
              "Step 1: native encode(\"Hello, world!\") produces exactly [19556, 28, 905, 17]");

        const int decode_steps = 4;  // a short but genuine multi-step continuation

        // --- Step 2: the frozen Phase 5A engine runs using the
        // NATIVE-PRODUCED ids (Execution A). The MODEL produces
        // CONTINUATION ids here -- distinct from the tokenizer-produced
        // PROMPT ids above; encode() is not involved in this step at all. ---
        std::printf("\n--- Execution A: driven by native encode() output ---\n");
        ContiguousAttentionKVStore cache_a(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        CachedStepResult prefill_a = forward_cached_step(model, cache_a, native_prompt_ids, 0);
        std::vector<int64_t> continuation_a;
        std::vector<std::vector<float>> logits_a = {prefill_a.logits};
        std::vector<int64_t> selected_a = {prefill_a.selected_token.back()};
        continuation_a.push_back(prefill_a.selected_token.back());
        for (int step = 1; step < decode_steps; ++step) {
            CachedStepResult r = forward_cached_step(model, cache_a, {continuation_a.back()}, cache_a.current_length());
            logits_a.push_back(r.logits);
            selected_a.push_back(r.selected_token.back());
            continuation_a.push_back(r.selected_token.back());
        }
        std::printf("[INFO] Execution A generated continuation ids:");
        for (int64_t id : continuation_a) std::printf(" %lld", static_cast<long long>(id));
        std::printf("\n");

        // --- Step 3: a SEPARATE execution uses the IDENTICAL ids supplied
        // EXPLICITLY (a literal array, not encode()'s return value) for the
        // prefill -- proving the frozen engine's result depends only on the
        // integer values, with no hidden dependency on encode()'s own
        // internal representation of them. ---------------------------------
        std::printf("\n--- Execution B: driven by an explicit literal array of the identical prompt IDs ---\n");
        const std::vector<int64_t> explicit_prompt_ids = {19556, 28, 905, 17};  // literal, not encode()'s output
        ContiguousAttentionKVStore cache_b(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        CachedStepResult prefill_b = forward_cached_step(model, cache_b, explicit_prompt_ids, 0);
        std::vector<int64_t> continuation_b;
        std::vector<std::vector<float>> logits_b = {prefill_b.logits};
        std::vector<int64_t> selected_b = {prefill_b.selected_token.back()};
        continuation_b.push_back(prefill_b.selected_token.back());
        for (int step = 1; step < decode_steps; ++step) {
            CachedStepResult r = forward_cached_step(model, cache_b, {continuation_b.back()}, cache_b.current_length());
            logits_b.push_back(r.logits);
            selected_b.push_back(r.selected_token.back());
            continuation_b.push_back(r.selected_token.back());
        }

        // --- Step 4: both executions produce identical evidence. ----------
        std::printf("\n--- Comparing Execution A vs. Execution B ---\n");
        check(logits_a.size() == logits_b.size(), "Step 4: same number of steps recorded for A and B");
        bool all_logits_equal = logits_a.size() == logits_b.size();
        for (size_t i = 0; all_logits_equal && i < logits_a.size(); ++i) {
            if (logits_a[i] != logits_b[i]) all_logits_equal = false;
        }
        check(all_logits_equal, "Step 4: complete logits are bit-identical at every step, A vs. B");
        check(selected_a == selected_b, "Step 4: selected tokens are identical at every step, A vs. B");
        check(continuation_a == continuation_b, "Step 4: generated continuation ID sequence is identical, A vs. B");
        check(cache_a.current_length() == cache_b.current_length(),
              "Step 4: committed KV-cache lengths are identical, A vs. B");
        check(caches_equal(cache_a, cache_b),
              "Step 4: committed KV-cache contents are identical (every layer/kv_head/position), A vs. B");

        // --- Step 5/6/7: the generated continuation decodes through the
        // new one-shot decoder AND through the streaming accumulator, and
        // the two agree exactly. The MODEL produced these CONTINUATION ids
        // (Step 2 above); this step hands them to the native Phase 5B
        // DECODER -- the reverse direction from Step 1, and a completely
        // separate ID sequence from the tokenizer's own PROMPT ids.
        //
        // This is a FIXED fixture, not a property test over arbitrary
        // continuations: for the established greedy continuation
        // [339, 5248, 1535, 288] (decoding to " I'm here to"), every byte
        // is complete, valid UTF-8 -- there is no incomplete trailing
        // sequence for this specific fixture, so no exception is an
        // acceptable outcome here. A conditional "exception is also fine"
        // branch would silently convert a real streaming-decoder
        // regression into a passing check for this exact fixture; the
        // dedicated incomplete-trailing-sequence contract is already
        // covered by test_streaming_decode.cpp and is not duplicated here. ---
        std::printf("\n--- Decoding the generated continuation (one-shot and streaming) ---\n");
        const std::string one_shot_decoded = profile.decode(continuation_a);
        std::printf("[INFO] one-shot decoded continuation: \"%s\"\n", one_shot_decoded.c_str());
        check(one_shot_decoded == " I'm here to",
              "Step 5: one-shot decoded continuation equals the established expected bytes exactly");

        Utf8StreamDecoder stream(profile);
        std::string streaming_decoded;
        for (int64_t id : continuation_a) streaming_decoded += stream.feed(id);
        stream.finish();
        check(streaming_decoded == one_shot_decoded,
              "Step 6/7: streaming-decoded continuation bytes agree exactly with the one-shot decode");

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
