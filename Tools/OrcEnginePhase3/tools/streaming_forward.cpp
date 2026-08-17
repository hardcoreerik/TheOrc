// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

#include "orcengine/gguf_source.hpp"
#include "orcengine/streaming.hpp"

using namespace orcengine;

namespace {
void print_ints(const std::vector<int64_t>& values) {
    std::printf("[");
    for (size_t i = 0; i < values.size(); ++i)
        std::printf("%s%lld", i == 0 ? "" : ",", static_cast<long long>(values[i]));
    std::printf("]");
}

void print_floats(const float* values, size_t count) {
    std::printf("[");
    for (size_t i = 0; i < count; ++i)
        std::printf("%s%.9g", i == 0 ? "" : ",", values[i]);
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
        if (argc < 3) throw std::runtime_error(
            "usage: orcengine_gguf_streaming_forward MODEL.gguf TOKEN_ID... [--steps N] [--budget-bytes N] [--require-full-resident] [--reverse-layer-materialization] [--virtualize-bookends] [--output-chunk-rows N]");
        const std::filesystem::path path = argv[1];
        size_t steps = 1;
        bool reverse = false;
        bool require_full = false;
        bool virtualize_bookends = false;
        uint64_t output_chunk_rows = 1024;
        uint64_t budget = std::numeric_limits<uint64_t>::max();
        std::vector<int64_t> tokens;
        for (int i = 2; i < argc; ++i) {
            const std::string arg = argv[i];
            if (arg == "--steps") {
                if (++i >= argc) throw std::runtime_error("--steps requires a value");
                steps = static_cast<size_t>(std::stoull(argv[i]));
            } else if (arg == "--reverse-layer-materialization") {
                reverse = true;
            } else if (arg == "--budget-bytes") {
                if (++i >= argc) throw std::runtime_error("--budget-bytes requires a value");
                budget = std::stoull(argv[i]);
            } else if (arg == "--require-full-resident") {
                require_full = true;
            } else if (arg == "--virtualize-bookends") {
                virtualize_bookends = true;
            } else if (arg == "--output-chunk-rows") {
                if (++i >= argc) throw std::runtime_error("--output-chunk-rows requires a value");
                output_chunk_rows = std::stoull(argv[i]);
            } else {
                tokens.push_back(std::stoll(arg));
            }
        }
        if (tokens.empty() || steps == 0 || steps > 8)
            throw std::runtime_error("provide at least one token and 1..8 steps");

        ModelSourceBinding binding = bind_gguf_source(map_llama_model(index_gguf(path)));
        const uint64_t full_bytes = full_resident_bytes(binding.source);
        if (require_full) require_full_resident_budget(binding.source, budget);
        bool full_admitted = true;
        try { require_full_resident_budget(binding.source, budget); }
        catch (const ResidencyBudgetError&) { full_admitted = false; }
        const auto materialize_started = std::chrono::steady_clock::now();
        StreamingConfig config;
        config.residency_budget_bytes = budget;
        config.virtualize_bookends = virtualize_bookends;
        config.region_materializer = std::move(binding.materialize_region);
        config.output_chunk_rows = output_chunk_rows;
        StreamingModel model(std::move(binding.source), std::move(binding.materialize),
                             std::move(config));
        const double materialize_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - materialize_started).count();
        const uint64_t bookend_bytes = model.telemetry().current_resident_weight_bytes;
        const uint64_t bookend_count = model.telemetry().materialization_count;
        const std::vector<std::string> taps = {
            "input_embedding", "layer0.pre_attention_normalized_state",
            "layer0.q_projection", "layer0.k_projection", "layer0.v_projection",
            "layer0.attention_output_after_projection", "layer0.down_projection",
            "final_normalized_state",
        };

        std::printf("{\n  \"virtualized_bookends\": %s,\n  \"output_chunk_rows\": %llu,\n"
                    "  \"materialized_bytes\": %llu,\n  \"materialize_milliseconds\": %.6f,\n"
                    "  \"residency_budget_bytes\": %llu,\n  \"full_resident_required_bytes\": %llu,\n"
                    "  \"full_resident_admitted\": %s,\n  \"initial_materialization_count\": %llu,\n"
                    "  \"steps\": [\n",
                    virtualize_bookends ? "true" : "false",
                    static_cast<unsigned long long>(output_chunk_rows),
                    static_cast<unsigned long long>(bookend_bytes), materialize_ms,
                    static_cast<unsigned long long>(budget),
                    static_cast<unsigned long long>(full_bytes),
                    full_admitted ? "true" : "false",
                    static_cast<unsigned long long>(bookend_count));
        for (size_t step = 0; step < steps; ++step) {
            const auto started = std::chrono::steady_clock::now();
            const ForwardResult result = model.forward(tokens, {reverse, -1});
            const double forward_ms = std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - started).count();
            const int64_t selected = result.selected_token.back();
            const size_t vocab = static_cast<size_t>(model.config().vocab);
            std::printf("    {\"tokens\":");
            print_ints(tokens);
            std::printf(",\"selected\":%lld,\"forward_milliseconds\":%.6f,\"logits_last\":",
                        static_cast<long long>(selected), forward_ms);
            print_floats(result.logits.data() + result.logits.size() - vocab, vocab);
            if (step == 0) {
                std::printf(",\"taps\":{\n");
                for (size_t i = 0; i < taps.size(); ++i)
                    print_tap(taps[i], result.taps.at(taps[i]), i + 1 != taps.size());
                std::printf("    }");
            }
            std::printf("}%s\n", step + 1 == steps ? "" : ",");
            tokens.push_back(selected);
        }
        const StreamingTelemetry& t = model.telemetry();
        std::printf("  ],\n  \"telemetry\": {\"current_resident_weight_bytes\":%llu,"
                    "\"peak_resident_weight_bytes\":%llu,\"cumulative_materialized_bytes\":%llu,"
                    "\"materialization_count\":%llu,\"release_count\":%llu,"
                    "\"backing_bytes_read\":%llu,\"repeated_backing_bytes_read\":%llu,"
                    "\"read_count\":%llu,\"peak_process_working_set_bytes\":%llu,"
                    "\"peak_active_layers\":%llu,\"observer_event_count\":%llu,"
                    "\"observer_failure_count\":%llu,\"region_materialization_count\":%llu,"
                    "\"embedding_region_count\":%llu,\"output_region_count\":%llu,"
                    "\"embedding_backing_bytes_read\":%llu,\"output_backing_bytes_read\":%llu,"
                    "\"embedding_milliseconds\":%.6f,\"output_projection_milliseconds\":%.6f,"
                    "\"current_layer\":%lld,\"layer_timings\":[",
                    static_cast<unsigned long long>(t.current_resident_weight_bytes),
                    static_cast<unsigned long long>(t.peak_resident_weight_bytes),
                    static_cast<unsigned long long>(t.cumulative_materialized_bytes),
                    static_cast<unsigned long long>(t.materialization_count),
                    static_cast<unsigned long long>(t.release_count),
                    static_cast<unsigned long long>(t.backing_bytes_read),
                    static_cast<unsigned long long>(t.repeated_backing_bytes_read),
                    static_cast<unsigned long long>(t.read_count),
                    static_cast<unsigned long long>(t.peak_process_working_set_bytes),
                    static_cast<unsigned long long>(t.peak_active_layers),
                    static_cast<unsigned long long>(t.observer_event_count),
                    static_cast<unsigned long long>(t.observer_failure_count),
                    static_cast<unsigned long long>(t.region_materialization_count),
                    static_cast<unsigned long long>(t.embedding_region_count),
                    static_cast<unsigned long long>(t.output_region_count),
                    static_cast<unsigned long long>(t.embedding_backing_bytes_read),
                    static_cast<unsigned long long>(t.output_backing_bytes_read),
                    t.embedding_milliseconds,
                    t.output_projection_milliseconds,
                    static_cast<long long>(t.current_layer));
        for (size_t i = 0; i < t.layer_timings.size(); ++i) {
            const LayerTiming& timing = t.layer_timings[i];
            std::printf("%s{\"layer\":%lld,\"resident_bytes\":%llu,"
                        "\"materialize_milliseconds\":%.6f,\"execute_milliseconds\":%.6f}",
                        i == 0 ? "" : ",", static_cast<long long>(timing.layer),
                        static_cast<unsigned long long>(timing.resident_bytes),
                        timing.materialize_milliseconds, timing.execute_milliseconds);
        }
        std::printf("]}\n}\n");
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "%s\n", ex.what());
        return 1;
    }
}
