// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5C Stage 1 benchmark: return-by-value reference path vs
// reusable-workspace path, on the real SmolLM2-135M F32 GGUF, isolated
// window, interleaved ordering (reference/workspace alternate within
// each repetition, not blocked, so neither path benefits from being
// measured first/warm). One untimed warm-up repetition of each path
// precedes the reported repetitions -- reported numbers reflect
// WARM-CACHE performance (OS filesystem cache steady state), not a cold
// start. ActivationWorkspace construction cost is measured separately
// from decode timing, once per workspace-path repetition. This is a
// measurement tool, not a correctness test (see test_activation_workspace_real.cpp
// for that) -- and it makes NO improvement claim: Stage 1 converts only
// three primitives (rmsnorm/linear_no_bias/silu) for nine of the many
// per-layer allocations; a neutral or even negative result is a valid,
// reportable outcome, not a failure to fix.
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

#include "orcengine/activation_workspace.hpp"
#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/forward_cached_workspace.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

struct ScheduleStep {
    std::vector<int64_t> tokens;
    int64_t start_position;
};

struct RunTiming {
    double construction_ms = 0.0;  // 0 for the reference path (nothing extra to construct)
    double prefill_ms = 0.0;
    double decode_total_ms = 0.0;
    double decode_per_step_ms = 0.0;
};

double median(std::vector<double> v) {
    std::sort(v.begin(), v.end());
    return v[v.size() / 2];
}

RunTiming run_reference(const Model& model, const ModelConfig& cfg, const std::vector<ScheduleStep>& schedule) {
    ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
    const auto t1 = std::chrono::steady_clock::now();
    forward_cached_step(model, cache, schedule[0].tokens, schedule[0].start_position);
    const auto t2 = std::chrono::steady_clock::now();
    for (size_t i = 1; i < schedule.size(); ++i) {
        forward_cached_step(model, cache, schedule[i].tokens, schedule[i].start_position);
    }
    const auto t3 = std::chrono::steady_clock::now();

    RunTiming r;
    r.prefill_ms = std::chrono::duration<double, std::milli>(t2 - t1).count();
    r.decode_total_ms = std::chrono::duration<double, std::milli>(t3 - t2).count();
    r.decode_per_step_ms = r.decode_total_ms / static_cast<double>(schedule.size() - 1);
    return r;
}

RunTiming run_workspace(const Model& model, const ModelConfig& cfg, const std::vector<ScheduleStep>& schedule,
                        int64_t q_dim, int64_t kv_dim, int64_t max_tokens_per_step) {
    ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
    const auto tc0 = std::chrono::steady_clock::now();
    ActivationWorkspace workspace(cfg.hidden, cfg.intermediate, q_dim, kv_dim, cfg.vocab, max_tokens_per_step);
    const auto tc1 = std::chrono::steady_clock::now();

    const auto t1 = std::chrono::steady_clock::now();
    forward_cached_step_workspace(model, cache, schedule[0].tokens, schedule[0].start_position, workspace);
    const auto t2 = std::chrono::steady_clock::now();
    for (size_t i = 1; i < schedule.size(); ++i) {
        forward_cached_step_workspace(model, cache, schedule[i].tokens, schedule[i].start_position, workspace);
    }
    const auto t3 = std::chrono::steady_clock::now();

    RunTiming r;
    r.construction_ms = std::chrono::duration<double, std::milli>(tc1 - tc0).count();
    r.prefill_ms = std::chrono::duration<double, std::milli>(t2 - t1).count();
    r.decode_total_ms = std::chrono::duration<double, std::milli>(t3 - t2).count();
    r.decode_per_step_ms = r.decode_total_ms / static_cast<double>(schedule.size() - 1);
    return r;
}

void report(const std::string& label, const std::vector<RunTiming>& runs) {
    std::vector<double> construction, prefill, decode_total, decode_per_step;
    for (const auto& r : runs) {
        construction.push_back(r.construction_ms);
        prefill.push_back(r.prefill_ms);
        decode_total.push_back(r.decode_total_ms);
        decode_per_step.push_back(r.decode_per_step_ms);
    }
    auto minmax = [](const std::vector<double>& v) {
        return std::pair<double, double>(*std::min_element(v.begin(), v.end()), *std::max_element(v.begin(), v.end()));
    };
    auto [cmin, cmax] = minmax(construction);
    auto [pmin, pmax] = minmax(prefill);
    auto [dmin, dmax] = minmax(decode_total);
    auto [smin, smax] = minmax(decode_per_step);
    std::printf("[%s] n=%zu construction_ms median=%.3f [%.3f,%.3f] "
                "prefill_ms median=%.2f [%.2f,%.2f] "
                "decode_total_ms median=%.2f [%.2f,%.2f] "
                "decode_per_step_ms median=%.2f [%.2f,%.2f]\n",
                label.c_str(), runs.size(), median(construction), cmin, cmax, median(prefill), pmin, pmax,
                median(decode_total), dmin, dmax, median(decode_per_step), smin, smax);
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 2) {
        std::fprintf(stderr, "usage: %s <canonical-real-f32-gguf-path>\n", argv[0]);
        return 2;
    }
    try {
        const std::filesystem::path path = argv[1];
        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);
        const Model model = materialize_gguf_model(manifest);
        const ModelConfig& cfg = model.config();
        const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
        const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;

        const std::vector<int64_t> prompt_ids = {1, 5};
        std::vector<ScheduleStep> schedule = {{prompt_ids, 0}};
        {
            ContiguousAttentionKVStore discover_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            CachedStepResult r = forward_cached_step(model, discover_cache, prompt_ids, 0);
            int64_t pos = static_cast<int64_t>(prompt_ids.size());
            int64_t next = r.selected_token.back();
            for (int step = 0; step < 12; ++step) {
                schedule.push_back({{next}, pos});
                CachedStepResult rr = forward_cached_step(model, discover_cache, {next}, pos);
                pos += 1;
                next = rr.selected_token.back();
            }
        }
        const int64_t max_tokens_per_step = 2;  // covers this schedule's 2-token prefill

        std::printf("=== Phase 5C Stage 1 benchmark: reference vs workspace path, real SmolLM2-135M, %zu-step schedule ===\n",
                    schedule.size());
        std::printf("Model GGUF SHA-256: fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac (pinned)\n");
        std::printf("Warm-cache: one untimed warm-up repetition of each path precedes the reported repetitions.\n");
        std::printf("Ordering: interleaved (reference, workspace) per repetition -- neither path measured first every time.\n");
        std::printf("No improvement is promised or assumed; report the numbers as observed.\n\n");

        // Untimed warm-up, one per path.
        run_reference(model, cfg, schedule);
        run_workspace(model, cfg, schedule, q_dim, kv_dim, max_tokens_per_step);

        const int repetitions = 5;
        std::vector<RunTiming> ref_runs, ws_runs;
        for (int i = 0; i < repetitions; ++i) {
            ref_runs.push_back(run_reference(model, cfg, schedule));
            ws_runs.push_back(run_workspace(model, cfg, schedule, q_dim, kv_dim, max_tokens_per_step));
        }

        report("reference (return-by-value)", ref_runs);
        report("workspace (reusable buffers)", ws_runs);
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "ERROR: %s\n", ex.what());
        return 1;
    }
}
