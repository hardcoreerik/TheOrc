// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Real-model driver for Phase 5A: loads a real GGUF model fully resident
// (Phase 2's materialize_gguf_model -- Phase 5A does NOT yet compose with
// Phase 3/4's streaming/row-region virtualization, see
// docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md's composition-audit section),
// then runs BOTH the frozen Phase-1 full-prefix reference AND Phase-5A's
// prefill+incremental-decode cached path on the same growing sequence,
// emitting both traces plus selected real KV-cache content as JSON for a
// Python differential harness to compare against HF/PyTorch independently.
#include <array>
#include <chrono>
#include <cstdio>
#include <cstdint>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#endif

#include "orcengine/context.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

void print_floats(const std::vector<float>& values) {
    std::printf("[");
    for (size_t i = 0; i < values.size(); ++i) std::printf("%s%.9g", i == 0 ? "" : ",", values[i]);
    std::printf("]");
}

// Process-level working-set sample only -- NOT a weight/KV/activation
// breakdown. Phase 3's ResidencyLedger samples the same OS counter for the
// streaming architecture; Phase 5A is not yet composed with that
// architecture (see composition-audit note above the fully-resident load
// call), so this is measured independently here rather than reused.
uint64_t sample_process_working_set_bytes() {
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS counters{};
    if (GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof(counters))) {
        return static_cast<uint64_t>(counters.WorkingSetSize);
    }
#endif
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc < 4) {
            throw std::runtime_error(
                "usage: gguf_cached_forward MODEL.gguf STEPS TOKEN_ID... "
                "[--dump-cache LAYER,KV_HEAD,POSITION ...]");
        }
        const std::filesystem::path path = argv[1];
        const int64_t steps = std::stoll(argv[2]);
        std::vector<int64_t> initial;
        std::vector<std::array<int64_t, 3>> dump_requests;
        for (int i = 3; i < argc; ++i) {
            const std::string arg = argv[i];
            if (arg == "--dump-cache") {
                if (++i >= argc) throw std::runtime_error("--dump-cache requires LAYER,KV_HEAD,POSITION");
                std::string spec = argv[i];
                std::array<int64_t, 3> triple{};
                size_t pos = 0;
                for (int k = 0; k < 3; ++k) {
                    size_t comma = spec.find(',', pos);
                    triple[static_cast<size_t>(k)] = std::stoll(spec.substr(pos, comma - pos));
                    pos = comma + 1;
                }
                dump_requests.push_back(triple);
            } else {
                initial.push_back(std::stoll(arg));
            }
        }
        if (initial.empty() || steps <= 0 || steps > 8) {
            throw std::runtime_error("provide at least one initial token and 1..8 steps");
        }

        const uint64_t working_set_before_load = sample_process_working_set_bytes();
        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);
        const auto load_started = std::chrono::steady_clock::now();
        const Model model = materialize_gguf_model(manifest);
        const double load_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - load_started).count();
        const uint64_t working_set_after_load = sample_process_working_set_bytes();

        // --- Frozen Phase-1 full-prefix reference: recompute the whole
        // growing sequence from scratch at every step. ---
        std::vector<int64_t> tokens_full = initial;
        std::vector<std::vector<float>> full_logits_per_step;
        std::vector<int64_t> full_selected;
        double full_total_ms = 0.0;
        for (int64_t s = 0; s < steps; ++s) {
            const auto t0 = std::chrono::steady_clock::now();
            ForwardResult r = forward(model, tokens_full);
            full_total_ms += std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - t0).count();
            const int64_t vocab = model.config().vocab;
            std::vector<float> last(r.logits.end() - vocab, r.logits.end());
            const int64_t selected = r.selected_token.back();
            full_logits_per_step.push_back(last);
            full_selected.push_back(selected);
            tokens_full.push_back(selected);
        }

        // --- Phase 5A cached path: prefill once, then incremental steps. ---
        const uint64_t working_set_before_cache_alloc = sample_process_working_set_bytes();
        ContiguousAttentionKVStore cache(model.config().n_layers, model.config().n_kv_heads,
                                         model.config().max_positions, model.config().head_dim);
        const uint64_t working_set_after_cache_alloc = sample_process_working_set_bytes();
        // Derived, not trusted from any external prompt: bytes/token = layers
        // * kv_heads * head_dim * 2 (K and V) * sizeof(float). ContiguousAttentionKVStore's
        // constructor allocates n_layers*n_kv_heads*max_positions*head_dim floats for EACH
        // of k_ and v_ unconditionally (see context.hpp), so KV reserved == bytes/token *
        // max_positions regardless of how many positions are actually committed.
        const int64_t kv_bytes_per_token = model.config().n_layers * model.config().n_kv_heads *
                                            model.config().head_dim * 2 * static_cast<int64_t>(sizeof(float));
        const int64_t kv_reserved_bytes = kv_bytes_per_token * model.config().max_positions;
        std::vector<std::vector<float>> cached_logits_per_step;
        std::vector<int64_t> cached_selected;
        double prefill_ms = 0.0;
        std::vector<double> decode_ms;

        const auto prefill_t0 = std::chrono::steady_clock::now();
        CachedStepResult prefill_result = forward_cached_step(model, cache, initial, 0);
        prefill_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - prefill_t0).count();
        cache.set_current_length(static_cast<int64_t>(initial.size()));
        {
            const int64_t vocab = model.config().vocab;
            std::vector<float> last(prefill_result.logits.end() - vocab, prefill_result.logits.end());
            cached_logits_per_step.push_back(last);
            cached_selected.push_back(prefill_result.selected_token.back());
        }
        int64_t position = static_cast<int64_t>(initial.size());
        int64_t selected = cached_selected.back();
        for (int64_t s = 1; s < steps; ++s) {
            const auto t0 = std::chrono::steady_clock::now();
            CachedStepResult r = forward_cached_step(model, cache, {selected}, position);
            decode_ms.push_back(std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - t0).count());
            cache.set_current_length(position + 1);
            const int64_t vocab = model.config().vocab;
            std::vector<float> last(r.logits.end() - vocab, r.logits.end());
            cached_logits_per_step.push_back(last);
            selected = r.selected_token.back();
            cached_selected.push_back(selected);
            ++position;
        }

        const uint64_t working_set_after_decode = sample_process_working_set_bytes();

        // --- Output ---
        std::printf("{\n");
        std::printf("  \"load_milliseconds\": %.3f,\n", load_ms);
        std::printf("  \"full_total_milliseconds\": %.3f,\n", full_total_ms);
        std::printf("  \"prefill_milliseconds\": %.3f,\n", prefill_ms);
        std::printf("  \"memory_model\": {\n");
        std::printf("    \"kv_bytes_per_token\": %lld,\n", static_cast<long long>(kv_bytes_per_token));
        std::printf("    \"kv_reserved_bytes\": %lld,\n", static_cast<long long>(kv_reserved_bytes));
        std::printf("    \"kv_committed_bytes\": %lld,\n",
                    static_cast<long long>(kv_bytes_per_token * static_cast<int64_t>(initial.size() + static_cast<size_t>(steps) - 1)));
        std::printf("    \"working_set_before_load_bytes\": %llu,\n", (unsigned long long)working_set_before_load);
        std::printf("    \"working_set_after_load_bytes\": %llu,\n", (unsigned long long)working_set_after_load);
        std::printf("    \"working_set_before_cache_alloc_bytes\": %llu,\n", (unsigned long long)working_set_before_cache_alloc);
        std::printf("    \"working_set_after_cache_alloc_bytes\": %llu,\n", (unsigned long long)working_set_after_cache_alloc);
        std::printf("    \"working_set_after_decode_bytes\": %llu\n", (unsigned long long)working_set_after_decode);
        std::printf("  },\n");
        std::printf("  \"decode_milliseconds\": [");
        for (size_t i = 0; i < decode_ms.size(); ++i) std::printf("%s%.3f", i == 0 ? "" : ",", decode_ms[i]);
        std::printf("],\n");
        std::printf("  \"full_steps\": [\n");
        for (int64_t s = 0; s < steps; ++s) {
            std::printf("    {\"selected\": %lld, \"logits_last\": ",
                        static_cast<long long>(full_selected[static_cast<size_t>(s)]));
            print_floats(full_logits_per_step[static_cast<size_t>(s)]);
            std::printf("}%s\n", s + 1 < steps ? "," : "");
        }
        std::printf("  ],\n");
        std::printf("  \"cached_steps\": [\n");
        for (int64_t s = 0; s < steps; ++s) {
            std::printf("    {\"selected\": %lld, \"logits_last\": ",
                        static_cast<long long>(cached_selected[static_cast<size_t>(s)]));
            print_floats(cached_logits_per_step[static_cast<size_t>(s)]);
            std::printf("}%s\n", s + 1 < steps ? "," : "");
        }
        std::printf("  ],\n");
        std::printf("  \"cache_dumps\": [\n");
        for (size_t i = 0; i < dump_requests.size(); ++i) {
            const auto& req = dump_requests[i];
            std::vector<float> k_vals(static_cast<size_t>(model.config().head_dim));
            std::vector<float> v_vals(static_cast<size_t>(model.config().head_dim));
            const float* k = cache.k_row(req[0], req[1], req[2]);
            const float* v = cache.v_row(req[0], req[1], req[2]);
            for (int64_t d = 0; d < model.config().head_dim; ++d) {
                k_vals[static_cast<size_t>(d)] = k[d];
                v_vals[static_cast<size_t>(d)] = v[d];
            }
            std::printf("    {\"layer\": %lld, \"kv_head\": %lld, \"position\": %lld, \"k\": ",
                        static_cast<long long>(req[0]), static_cast<long long>(req[1]), static_cast<long long>(req[2]));
            print_floats(k_vals);
            std::printf(", \"v\": ");
            print_floats(v_vals);
            std::printf("}%s\n", i + 1 < dump_requests.size() ? "," : "");
        }
        std::printf("  ]\n");
        std::printf("}\n");
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "{\"error\": \"%s\"}\n", ex.what());
        return 1;
    }
}
