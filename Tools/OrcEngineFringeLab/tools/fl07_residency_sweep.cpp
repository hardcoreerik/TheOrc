// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07 -- Budgeted Weight Residency sweep driver. Runs the
// "keep first N layers permanently resident" policy against a real GGUF
// model at a series of N values, comparing complete logits against the
// frozen Phase-1 fully-resident forward() reference at every point, and
// reporting weight/process memory and timing metrics kept separate per
// this experiment's charter (Section 16: "do not present process working
// set as engine-owned model residency").
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <vector>

#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#include <psapi.h>
#endif

#include "fringelab/residency_budget.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/gguf_source.hpp"

using namespace orcengine;
using fringelab::ResidencyBudgetModel;

namespace {

uint64_t sample_process_working_set_bytes() {
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS counters{};
    if (GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof(counters))) {
        return static_cast<uint64_t>(counters.WorkingSetSize);
    }
#endif
    return 0;
}

void print_floats(const std::vector<float>& values) {
    std::printf("[");
    for (size_t i = 0; i < values.size(); ++i) std::printf("%s%.9g", i == 0 ? "" : ",", values[i]);
    std::printf("]");
}

}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc < 3) {
            throw std::runtime_error("usage: fl07_residency_sweep MODEL.gguf TOKEN_ID... [--n-resident N ...]");
        }
        const std::filesystem::path path = argv[1];
        std::vector<int64_t> tokens;
        std::vector<int64_t> budgets;
        for (int i = 2; i < argc; ++i) {
            const std::string arg = argv[i];
            if (arg == "--n-resident") {
                if (++i >= argc) throw std::runtime_error("--n-resident requires a value");
                budgets.push_back(std::stoll(argv[i]));
            } else {
                tokens.push_back(std::stoll(arg));
            }
        }
        if (tokens.empty()) throw std::runtime_error("provide at least one token id");

        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);

        // --- Reference: frozen Phase-1 fully-resident forward(), unmodified. ---
        const auto ref_load_t0 = std::chrono::steady_clock::now();
        const Model reference_model = materialize_gguf_model(manifest);
        const double ref_load_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - ref_load_t0).count();
        const auto ref_t0 = std::chrono::steady_clock::now();
        ForwardResult reference = forward(reference_model, tokens);
        const double ref_forward_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - ref_t0).count();
        const int64_t vocab = reference_model.config().vocab;
        std::vector<float> reference_last(reference.logits.end() - vocab, reference.logits.end());

        if (budgets.empty()) budgets = {0, 1, 2, 4, 8, 16, reference_model.config().n_layers};

        std::printf("{\n  \"reference\": {\"load_milliseconds\": %.3f, \"forward_milliseconds\": %.3f, "
                    "\"selected\": %lld},\n",
                    ref_load_ms, ref_forward_ms, static_cast<long long>(reference.selected_token.back()));
        std::printf("  \"sweep\": [\n");
        for (size_t bi = 0; bi < budgets.size(); ++bi) {
            const int64_t n_resident = budgets[bi];
            const uint64_t working_set_before = sample_process_working_set_bytes();
            ModelSourceBinding binding = bind_gguf_source(manifest);
            const auto load_t0 = std::chrono::steady_clock::now();
            ResidencyBudgetModel model(std::move(binding.source), std::move(binding.materialize), n_resident);
            const double load_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - load_t0).count();
            const uint64_t working_set_after_load = sample_process_working_set_bytes();

            const auto fwd_t0 = std::chrono::steady_clock::now();
            ForwardResult result = model.forward(tokens);
            const double forward_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - fwd_t0).count();
            const uint64_t working_set_after_forward = sample_process_working_set_bytes();

            std::vector<float> result_last(result.logits.end() - vocab, result.logits.end());
            float max_abs = 0.0f;
            for (size_t i = 0; i < result_last.size(); ++i) {
                max_abs = std::max(max_abs, std::fabs(result_last[i] - reference_last[i]));
            }
            const bool selected_matches = result.selected_token.back() == reference.selected_token.back();
            const StreamingTelemetry& t = model.telemetry();

            std::printf("    {\"n_resident_layers\": %lld, \"resident_layer_weight_bytes\": %llu,\n",
                        static_cast<long long>(n_resident), (unsigned long long)model.resident_layer_weight_bytes());
            std::printf("     \"load_milliseconds\": %.3f, \"forward_milliseconds\": %.3f,\n", load_ms, forward_ms);
            std::printf("     \"working_set_before_bytes\": %llu, \"working_set_after_load_bytes\": %llu, "
                        "\"working_set_after_forward_bytes\": %llu,\n",
                        (unsigned long long)working_set_before, (unsigned long long)working_set_after_load,
                        (unsigned long long)working_set_after_forward);
            std::printf("     \"peak_resident_weight_bytes\": %llu, \"cumulative_materialized_bytes\": %llu,\n",
                        (unsigned long long)t.peak_resident_weight_bytes, (unsigned long long)t.cumulative_materialized_bytes);
            std::printf("     \"backing_bytes_read\": %llu, \"materialization_count\": %llu, \"release_count\": %llu,\n",
                        (unsigned long long)t.backing_bytes_read, (unsigned long long)t.materialization_count,
                        (unsigned long long)t.release_count);
            std::printf("     \"selected_token\": %lld, \"selected_matches_reference\": %s,\n",
                        static_cast<long long>(result.selected_token.back()), selected_matches ? "true" : "false");
            std::printf("     \"max_abs_diff_vs_reference\": %.9g,\n", max_abs);
            std::printf("     \"logits_last\": ");
            print_floats(result_last);
            std::printf("}%s\n", bi + 1 < budgets.size() ? "," : "");
        }
        std::printf("  ]\n}\n");
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "{\"error\": \"%s\"}\n", ex.what());
        return 1;
    }
}
