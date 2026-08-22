// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B B4: isolated-window timing measurements. NOT a correctness test
// -- reuses the same StickyLayerCachedModel/StickyLayerPlan harness and
// the identical 6-step schedule test_fl07b_real_evidence.cpp already
// proved correct, adding only wall-clock instrumentation around
// construction (weight load / sticky materialization), the prefill step,
// and the steady-state decode steps, measured separately. Must be run in
// an isolated window (no other build/test/sanitizer work concurrently
// consuming CPU) for its numbers to be citable -- see EXPERIMENT.md's
// "Commit 3 timing methodology" section for the exact conditions this
// run was collected under.
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

#include "fringelab/sticky_layer_cached_model.hpp"
#include "fringelab/sticky_layer_model.hpp"
#include "fringelab/sticky_layer_plan.hpp"
#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/gguf_source.hpp"

using namespace orcengine;
using namespace fringelab;

namespace {

struct ScheduleStep {
    std::vector<int64_t> tokens;
    int64_t start_position;
};

struct RunTiming {
    double construction_ms = 0.0;
    double prefill_ms = 0.0;
    double decode_total_ms = 0.0;
    double decode_per_step_ms = 0.0;
};

double median(std::vector<double> v) {
    std::sort(v.begin(), v.end());
    return v[v.size() / 2];
}

RunTiming run_once(const ModelArtifactManifest& manifest, const StickyLayerPlan& plan, const ModelConfig& cfg,
                   const std::vector<ScheduleStep>& schedule) {
    ModelSourceBinding binding = bind_gguf_source(manifest);
    const auto t0 = std::chrono::steady_clock::now();
    StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize), plan);
    const auto t1 = std::chrono::steady_clock::now();

    ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
    model.step(cache, schedule[0].tokens, schedule[0].start_position);
    const auto t2 = std::chrono::steady_clock::now();
    for (size_t i = 1; i < schedule.size(); ++i) {
        model.step(cache, schedule[i].tokens, schedule[i].start_position);
    }
    const auto t3 = std::chrono::steady_clock::now();

    RunTiming r;
    r.construction_ms = std::chrono::duration<double, std::milli>(t1 - t0).count();
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
    std::printf("[%s] n=%zu construction_ms median=%.1f [%.1f,%.1f] "
                "prefill_ms median=%.1f [%.1f,%.1f] "
                "decode_total_ms median=%.1f [%.1f,%.1f] "
                "decode_per_step_ms median=%.1f [%.1f,%.1f]\n",
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
        const Model reference_model = materialize_gguf_model(manifest);
        const ModelConfig& cfg = reference_model.config();

        ModelSourceBinding probe = bind_gguf_source(manifest);
        const std::vector<LayerCostInfo> layer_costs = layer_costs_from_source(probe.source);
        uint64_t total_layer_bytes = 0;
        for (const auto& l : layer_costs) total_layer_bytes += l.resident_bytes;

        const std::vector<int64_t> prompt_ids = {19556, 28, 905, 17};
        std::vector<ScheduleStep> schedule = {{prompt_ids, 0}};
        {
            ContiguousAttentionKVStore discover_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            CachedStepResult r = forward_cached_step(reference_model, discover_cache, prompt_ids, 0);
            int64_t pos = static_cast<int64_t>(prompt_ids.size());
            int64_t next = r.selected_token.back();
            for (int step = 0; step < 4; ++step) {
                schedule.push_back({{next}, pos});
                CachedStepResult rr = forward_cached_step(reference_model, discover_cache, {next}, pos);
                pos += 1;
                next = rr.selected_token.back();
            }
            schedule.push_back({{prompt_ids[0]}, pos});
        }

        struct NamedPlan {
            std::string label;
            StickyLayerPlan plan;
        };
        std::vector<NamedPlan> plans;
        plans.push_back({"zero-sticky", plan_first_k(layer_costs, 0)});
        plans.push_back({"firstk-medium", plan_first_k(layer_costs, total_layer_bytes / 2)});
        {
            std::vector<LayerCostInfo> ranked = layer_costs;
            for (auto& l : ranked) {
                l.benefit_estimate = (l.layer_id % 2 == 1) ? 2.0 : 1.0;
                l.cost_source = CostSource::Synthetic;
            }
            plans.push_back({"cost-per-byte-synthetic", plan_cost_per_byte(ranked, total_layer_bytes / 2)});
        }
        plans.push_back({"all-sticky", plan_first_k(layer_costs, total_layer_bytes)});

        std::printf("=== FL-07B B4: isolated-window timing (n_layers=%lld, 3 repetitions per plan) ===\n",
                    (long long)cfg.n_layers);
        const int repetitions = 3;
        // One untimed warm-up run (not reported) to let OS filesystem
        // caching reach a steady state before the timed repetitions --
        // reported numbers below therefore reflect WARM-CACHE performance,
        // not a cold start; stated explicitly, not left implicit.
        run_once(manifest, plans[0].plan, cfg, schedule);
        for (const NamedPlan& np : plans) {
            std::vector<RunTiming> runs;
            for (int i = 0; i < repetitions; ++i) {
                runs.push_back(run_once(manifest, np.plan, cfg, schedule));
            }
            report(np.label, runs);
        }
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "ERROR: %s\n", ex.what());
        return 1;
    }
}
