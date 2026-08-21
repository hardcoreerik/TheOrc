// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Real-model driver for Phase 5A's composition audit (OE-ADR-026). Runs
// THREE legs against the same real, pinned GGUF model and the same growing
// token sequence, in one process so they can be compared directly:
//   A. Frozen Phase-4 virtualized full-prefix (StreamingModel, virtualize_bookends=true)
//   B. Phase-5A fully-resident cached reference (forward_cached_step)
//   C. Phase-5A virtualized cached target (VirtualizedCachedModel::step)
// Plus: real KV cache-content dumps from leg C, and full StreamingTelemetry
// (backing bytes read, materialization counts, peak resident weight bytes,
// peak process working set, peak active layers) from legs A and C, so
// backing I/O and residency accounting are MEASURED, not estimated. A
// Python script (real_5way_composed_differential.py) adds HF/PyTorch's
// full-prefix and native-cached legs (D, E) to complete the 5-way.
#include <array>
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

#include "orcengine/context.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/forward_cached_virtualized.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/gguf_source.hpp"
#include "orcengine/streaming.hpp"

using namespace orcengine;

namespace {

void print_floats(const std::vector<float>& values) {
    std::printf("[");
    for (size_t i = 0; i < values.size(); ++i) std::printf("%s%.9g", i == 0 ? "" : ",", values[i]);
    std::printf("]");
}

uint64_t sample_process_working_set_bytes() {
#ifdef _WIN32
    PROCESS_MEMORY_COUNTERS counters{};
    if (GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof(counters))) {
        return static_cast<uint64_t>(counters.WorkingSetSize);
    }
#endif
    return 0;
}

void print_telemetry(const StreamingTelemetry& t) {
    std::printf(
        "{\"current_resident_weight_bytes\":%llu,\"peak_resident_weight_bytes\":%llu,"
        "\"cumulative_materialized_bytes\":%llu,\"materialization_count\":%llu,"
        "\"release_count\":%llu,\"backing_bytes_read\":%llu,"
        "\"repeated_backing_bytes_read\":%llu,\"read_count\":%llu,"
        "\"peak_process_working_set_bytes\":%llu,\"peak_active_layers\":%llu,"
        "\"row_region_materialization_count\":%llu,\"embedding_row_region_count\":%llu,"
        "\"output_row_region_count\":%llu,\"embedding_backing_bytes_read\":%llu,"
        "\"output_backing_bytes_read\":%llu,\"embedding_milliseconds\":%.6f,"
        "\"output_projection_milliseconds\":%.6f}",
        (unsigned long long)t.current_resident_weight_bytes, (unsigned long long)t.peak_resident_weight_bytes,
        (unsigned long long)t.cumulative_materialized_bytes, (unsigned long long)t.materialization_count,
        (unsigned long long)t.release_count, (unsigned long long)t.backing_bytes_read,
        (unsigned long long)t.repeated_backing_bytes_read, (unsigned long long)t.read_count,
        (unsigned long long)t.peak_process_working_set_bytes, (unsigned long long)t.peak_active_layers,
        (unsigned long long)t.row_region_materialization_count, (unsigned long long)t.embedding_row_region_count,
        (unsigned long long)t.output_row_region_count, (unsigned long long)t.embedding_backing_bytes_read,
        (unsigned long long)t.output_backing_bytes_read, t.embedding_milliseconds, t.output_projection_milliseconds);
}

}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc < 4) {
            throw std::runtime_error(
                "usage: gguf_cached_forward_virtualized MODEL.gguf STEPS TOKEN_ID... "
                "[--dump-cache LAYER,KV_HEAD,POSITION ...] [--output-chunk-rows N]");
        }
        const std::filesystem::path path = argv[1];
        const int64_t steps = std::stoll(argv[2]);
        std::vector<int64_t> initial;
        std::vector<std::array<int64_t, 3>> dump_requests;
        uint64_t output_chunk_rows = 1024;
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
            } else if (arg == "--output-chunk-rows") {
                if (++i >= argc) throw std::runtime_error("--output-chunk-rows requires a value");
                output_chunk_rows = std::stoull(argv[i]);
            } else {
                initial.push_back(std::stoll(arg));
            }
        }
        if (initial.empty() || steps <= 0 || steps > 8) {
            throw std::runtime_error("provide at least one initial token and 1..8 steps");
        }

        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);

        // --- Leg B: fully-resident cached reference. ---
        const auto load_b_t0 = std::chrono::steady_clock::now();
        const Model model_b = materialize_gguf_model(manifest);
        const double load_b_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - load_b_t0).count();
        ContiguousAttentionKVStore cache_b(model_b.config().n_layers, model_b.config().n_kv_heads,
                                           model_b.config().max_positions, model_b.config().head_dim);
        std::vector<std::vector<float>> b_logits;
        std::vector<int64_t> b_selected;
        std::vector<double> b_ms;
        {
            const auto t0 = std::chrono::steady_clock::now();
            CachedStepResult r = forward_cached_step(model_b, cache_b, initial, 0);
            b_ms.push_back(std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
            const int64_t vocab = model_b.config().vocab;
            b_logits.emplace_back(r.logits.end() - vocab, r.logits.end());
            b_selected.push_back(r.selected_token.back());
        }
        int64_t pos_b = static_cast<int64_t>(initial.size());
        int64_t sel_b = b_selected.back();
        for (int64_t s = 1; s < steps; ++s) {
            const auto t0 = std::chrono::steady_clock::now();
            CachedStepResult r = forward_cached_step(model_b, cache_b, {sel_b}, pos_b);
            b_ms.push_back(std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
            const int64_t vocab = model_b.config().vocab;
            b_logits.emplace_back(r.logits.end() - vocab, r.logits.end());
            sel_b = r.selected_token.back();
            b_selected.push_back(sel_b);
            ++pos_b;
        }

        // --- Leg A: frozen Phase-4 virtualized full-prefix, on the same growing sequence. ---
        ModelSourceBinding binding_a = bind_gguf_source(manifest);
        StreamingConfig config_a;
        config_a.virtualize_bookends = true;
        config_a.row_region_materializer = binding_a.materialize_rows;
        config_a.output_chunk_rows = output_chunk_rows;
        const auto load_a_t0 = std::chrono::steady_clock::now();
        StreamingModel model_a(binding_a.source, binding_a.materialize, config_a);
        const double load_a_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - load_a_t0).count();
        std::vector<int64_t> tokens_a = initial;
        std::vector<std::vector<float>> a_logits;
        std::vector<int64_t> a_selected;
        std::vector<double> a_ms;
        for (int64_t s = 0; s < steps; ++s) {
            const auto t0 = std::chrono::steady_clock::now();
            ForwardResult r = model_a.forward(tokens_a);
            a_ms.push_back(std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
            const int64_t vocab = model_a.config().vocab;
            a_logits.emplace_back(r.logits.end() - vocab, r.logits.end());
            const int64_t selected = r.selected_token.back();
            a_selected.push_back(selected);
            tokens_a.push_back(selected);
        }

        // --- Leg C: Phase-5A virtualized cached target, on the same growing sequence. ---
        ModelSourceBinding binding_c = bind_gguf_source(manifest);
        VirtualizedCachedConfig config_c;
        config_c.materializer = binding_c.materialize;
        config_c.row_region_materializer = binding_c.materialize_rows;
        config_c.output_chunk_rows = output_chunk_rows;
        const auto load_c_t0 = std::chrono::steady_clock::now();
        VirtualizedCachedModel model_c(std::move(binding_c.source), std::move(config_c));
        const double load_c_ms = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - load_c_t0).count();
        ContiguousAttentionKVStore cache_c(model_c.config().n_layers, model_c.config().n_kv_heads,
                                           model_c.config().max_positions, model_c.config().head_dim);
        std::vector<std::vector<float>> c_logits;
        std::vector<int64_t> c_selected;
        std::vector<double> c_prefill_ms;
        std::vector<double> c_decode_ms;
        const uint64_t working_set_before_c = sample_process_working_set_bytes();
        {
            const auto t0 = std::chrono::steady_clock::now();
            CachedStepResult r = model_c.step(cache_c, initial, 0);
            c_prefill_ms.push_back(
                std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
            const int64_t vocab = model_c.config().vocab;
            c_logits.emplace_back(r.logits.end() - vocab, r.logits.end());
            c_selected.push_back(r.selected_token.back());
        }
        int64_t pos_c = static_cast<int64_t>(initial.size());
        int64_t sel_c = c_selected.back();
        for (int64_t s = 1; s < steps; ++s) {
            const auto t0 = std::chrono::steady_clock::now();
            CachedStepResult r = model_c.step(cache_c, {sel_c}, pos_c);
            c_decode_ms.push_back(
                std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
            const int64_t vocab = model_c.config().vocab;
            c_logits.emplace_back(r.logits.end() - vocab, r.logits.end());
            sel_c = r.selected_token.back();
            c_selected.push_back(sel_c);
            ++pos_c;
        }
        const uint64_t working_set_after_c = sample_process_working_set_bytes();

        // --- Output ---
        std::printf("{\n");
        std::printf("  \"load_milliseconds\": {\"leg_a\": %.3f, \"leg_b\": %.3f, \"leg_c\": %.3f},\n",
                    load_a_ms, load_b_ms, load_c_ms);
        std::printf("  \"process_working_set_bytes\": {\"before_leg_c\": %llu, \"after_leg_c\": %llu},\n",
                    (unsigned long long)working_set_before_c, (unsigned long long)working_set_after_c);

        std::printf("  \"leg_a_full_prefix_virtualized\": {\"selected\": [");
        for (size_t i = 0; i < a_selected.size(); ++i) std::printf("%s%lld", i == 0 ? "" : ",", (long long)a_selected[i]);
        std::printf("], \"milliseconds\": [");
        for (size_t i = 0; i < a_ms.size(); ++i) std::printf("%s%.3f", i == 0 ? "" : ",", a_ms[i]);
        std::printf("], \"logits\": [");
        for (size_t i = 0; i < a_logits.size(); ++i) { print_floats(a_logits[i]); std::printf("%s", i + 1 < a_logits.size() ? "," : ""); }
        std::printf("], \"telemetry\": ");
        print_telemetry(model_a.telemetry());
        std::printf("},\n");

        std::printf("  \"leg_b_resident_cached\": {\"selected\": [");
        for (size_t i = 0; i < b_selected.size(); ++i) std::printf("%s%lld", i == 0 ? "" : ",", (long long)b_selected[i]);
        std::printf("], \"milliseconds\": [");
        for (size_t i = 0; i < b_ms.size(); ++i) std::printf("%s%.3f", i == 0 ? "" : ",", b_ms[i]);
        std::printf("], \"logits\": [");
        for (size_t i = 0; i < b_logits.size(); ++i) { print_floats(b_logits[i]); std::printf("%s", i + 1 < b_logits.size() ? "," : ""); }
        std::printf("]},\n");

        std::printf("  \"leg_c_virtualized_cached\": {\"selected\": [");
        for (size_t i = 0; i < c_selected.size(); ++i) std::printf("%s%lld", i == 0 ? "" : ",", (long long)c_selected[i]);
        std::printf("], \"prefill_milliseconds\": [");
        for (size_t i = 0; i < c_prefill_ms.size(); ++i) std::printf("%s%.3f", i == 0 ? "" : ",", c_prefill_ms[i]);
        std::printf("], \"decode_milliseconds\": [");
        for (size_t i = 0; i < c_decode_ms.size(); ++i) std::printf("%s%.3f", i == 0 ? "" : ",", c_decode_ms[i]);
        std::printf("], \"logits\": [");
        for (size_t i = 0; i < c_logits.size(); ++i) { print_floats(c_logits[i]); std::printf("%s", i + 1 < c_logits.size() ? "," : ""); }
        std::printf("], \"telemetry\": ");
        print_telemetry(model_c.telemetry());
        std::printf(",\n  \"cache_dumps\": [\n");
        for (size_t i = 0; i < dump_requests.size(); ++i) {
            const auto& req = dump_requests[i];
            std::vector<float> k_vals(static_cast<size_t>(model_c.config().head_dim));
            std::vector<float> v_vals(static_cast<size_t>(model_c.config().head_dim));
            const float* k = cache_c.k_row(req[0], req[1], req[2]);
            const float* v = cache_c.v_row(req[0], req[1], req[2]);
            for (int64_t d = 0; d < model_c.config().head_dim; ++d) {
                k_vals[static_cast<size_t>(d)] = k[d];
                v_vals[static_cast<size_t>(d)] = v[d];
            }
            std::printf("    {\"layer\": %lld, \"kv_head\": %lld, \"position\": %lld, \"k\": ",
                        (long long)req[0], (long long)req[1], (long long)req[2]);
            print_floats(k_vals);
            std::printf(", \"v\": ");
            print_floats(v_vals);
            std::printf("}%s\n", i + 1 < dump_requests.size() ? "," : "");
        }
        std::printf("  ]\n  }\n");
        std::printf("}\n");
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "{\"error\": \"%s\"}\n", ex.what());
        return 1;
    }
}
