// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <vector>

#include "orcengine/forward.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

void print_ints(const std::vector<int64_t>& values) {
    std::printf("[");
    for (size_t i = 0; i < values.size(); ++i) {
        std::printf("%s%lld", i == 0 ? "" : ",", static_cast<long long>(values[i]));
    }
    std::printf("]");
}

void print_floats(const float* values, size_t count) {
    std::printf("[");
    for (size_t i = 0; i < count; ++i) std::printf("%s%.9g", i == 0 ? "" : ",", values[i]);
    std::printf("]");
}

void print_tap(const std::string& name, const ActivationBuffer& tap, bool comma) {
    std::printf("      \"%s\": {\"dims\":", name.c_str());
    print_ints(tap.dims);
    std::printf(",\"data\":");
    print_floats(tap.data.data(), tap.data.size());
    std::printf("}%s\n", comma ? "," : "");
}

}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc < 3) throw std::runtime_error("usage: orcengine_gguf_forward MODEL.gguf TOKEN_ID... [--steps N]");
        const std::filesystem::path path = argv[1];
        size_t steps = 1;
        std::vector<int64_t> tokens;
        for (int i = 2; i < argc; ++i) {
            if (std::string(argv[i]) == "--steps") {
                if (++i >= argc) throw std::runtime_error("--steps requires a value");
                steps = static_cast<size_t>(std::stoull(argv[i]));
            } else {
                tokens.push_back(std::stoll(argv[i]));
            }
        }
        if (tokens.empty() || steps == 0 || steps > 8) {
            throw std::runtime_error("provide at least one token and 1..8 steps");
        }

        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);
        const auto materialize_started = std::chrono::steady_clock::now();
        const Model model = materialize_gguf_model(manifest);
        const double materialize_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - materialize_started).count();
        uint64_t materialized_bytes = 0;
        for (const auto& tensor : manifest.mapped_tensors) {
            materialized_bytes += static_cast<uint64_t>(tensor.logical.shape().element_count()) * sizeof(float);
        }

        const std::vector<std::string> taps = {
            "input_embedding",
            "layer0.pre_attention_normalized_state",
            "layer0.q_projection",
            "layer0.k_projection",
            "layer0.v_projection",
            "layer0.attention_output_after_projection",
            "layer0.down_projection",
            "final_normalized_state",
        };

        std::printf("{\n  \"materialized_bytes\": %llu,\n  \"materialize_milliseconds\": %.6f,\n  \"steps\": [\n",
                    static_cast<unsigned long long>(materialized_bytes), materialize_ms);
        for (size_t step = 0; step < steps; ++step) {
            const auto forward_started = std::chrono::steady_clock::now();
            const ForwardResult result = forward(model, tokens);
            const double forward_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - forward_started).count();
            const int64_t selected = result.selected_token.back();
            const size_t vocab = static_cast<size_t>(model.config().vocab);
            std::printf("    {\"tokens\":");
            print_ints(tokens);
            std::printf(",\"selected\":%lld,\"forward_milliseconds\":%.6f,\"logits_last\":",
                        static_cast<long long>(selected), forward_ms);
            print_floats(result.logits.data() + result.logits.size() - vocab, vocab);
            if (step == 0) {
                std::printf(",\"taps\":{\n");
                for (size_t i = 0; i < taps.size(); ++i) {
                    print_tap(taps[i], result.taps.at(taps[i]), i + 1 != taps.size());
                }
                std::printf("    }");
            }
            std::printf("}%s\n", step + 1 == steps ? "" : ",");
            tokens.push_back(selected);
        }
        std::printf("  ]\n}\n");
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "%s\n", ex.what());
        return 1;
    }
}
