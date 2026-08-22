// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B Commit 3: real-GGUF evidence. Extends the same StickyLayerPlan /
// StickyLayerCachedModel harness Commits 1-2A already built and proved
// against a 2-layer synthetic fixture -- no parallel planner
// implementation, no new residency mechanism. Uses the canonical real
// SmolLM2-135M F32 GGUF (the same artifact
// Tools/OrcEnginePhase5A/tests/test_real_cache_attacks.cpp and Phase 5B's
// own frozen-engine integration proof already use) via Phase 3's own
// bind_gguf_source() seam -- reads real per-layer weights from disk,
// never modifies Phase 1/2/3/5A source.
//
// Research question this commit answers with real-model evidence: does
// keeping a selected subset of real transformer layers resident reduce
// repeated backing I/O during multi-step cached decoding, while
// preserving the frozen model's numerical result and respecting the
// declared memory budget? See EXPERIMENT.md's "Commit 3" section for the
// full disposition; this file is the evidence, not the conclusion.
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <limits>
#include <map>
#include <memory>
#include <optional>
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
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

class TrackingMaterializer {
public:
    explicit TrackingMaterializer(TensorMaterializer inner) : inner_(std::move(inner)) {}
    ResidentView operator()(const LogicalTensor& logical, const BackingExtent& backing) {
        ++counts_[logical.name()];
        return inner_(logical, backing);
    }
    int count(const std::string& name) const {
        const auto it = counts_.find(name);
        return it == counts_.end() ? 0 : it->second;
    }

private:
    TensorMaterializer inner_;
    std::map<std::string, int> counts_;
};

class CorruptingMaterializer {
public:
    explicit CorruptingMaterializer(TensorMaterializer inner) : inner_(std::move(inner)) {}
    ResidentView operator()(const LogicalTensor& logical, const BackingExtent& backing) {
        ResidentView view = inner_(logical, backing);
        if (target_name_.has_value() && logical.name() == *target_name_) {
            ++calls_to_target_;
            if (calls_to_target_ == target_occurrence_) {
                std::fill(view.raw().begin(), view.raw().end(), std::numeric_limits<float>::quiet_NaN());
            }
        }
        return view;
    }
    void set_target(std::string name, int occurrence) {
        target_name_ = std::move(name);
        target_occurrence_ = occurrence;
    }

private:
    TensorMaterializer inner_;
    std::optional<std::string> target_name_;
    int target_occurrence_ = 0;
    int calls_to_target_ = 0;
};

uint64_t bookend_backing_bytes(const ModelSource& source) {
    uint64_t bytes = 0;
    int matched = 0;
    for (const SourceTensor& t : source.tensors) {
        if (t.identity.layer != -1) continue;
        if (t.identity.role == TensorRole::TokenEmbedding || t.identity.role == TensorRole::FinalNorm ||
            t.identity.role == TensorRole::OutputHead) {
            bytes += static_cast<uint64_t>(t.backing.byte_length());
            ++matched;
        }
    }
    const int expected = source.tied_embeddings ? 2 : 3;
    if (matched != expected) throw std::runtime_error("bookend_backing_bytes: unexpected bookend tensor count");
    return bytes;
}
uint64_t bookend_resident_bytes(const ModelSource& source) {
    uint64_t bytes = 0;
    for (const SourceTensor& t : source.tensors) {
        if (t.identity.layer == -1 &&
            (t.identity.role == TensorRole::TokenEmbedding || t.identity.role == TensorRole::FinalNorm ||
             t.identity.role == TensorRole::OutputHead)) {
            bytes += t.resident_bytes;
        }
    }
    return bytes;
}
uint64_t sum_backing_bytes(const std::vector<LayerCostInfo>& layer_costs, const std::vector<int64_t>& ids) {
    uint64_t total = 0;
    for (int64_t id : ids) total += layer_costs[static_cast<size_t>(id)].backing_bytes;
    return total;
}
uint64_t largest_resident_among(const std::vector<LayerCostInfo>& layer_costs, const std::vector<int64_t>& ids) {
    uint64_t largest = 0;
    for (int64_t id : ids) largest = std::max(largest, layer_costs[static_cast<size_t>(id)].resident_bytes);
    return largest;
}
std::vector<int64_t> cold_layer_ids(const std::vector<LayerCostInfo>& layer_costs, const StickyLayerPlan& plan) {
    std::vector<int64_t> cold;
    for (const auto& l : layer_costs) {
        if (!plan.is_sticky(l.layer_id)) cold.push_back(l.layer_id);
    }
    return cold;
}
bool caches_committed_contents_identical(const ContiguousAttentionKVStore& a, const ContiguousAttentionKVStore& b) {
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
bool all_finite(const std::vector<float>& v) {
    for (float x : v) {
        if (!std::isfinite(x)) return false;
    }
    return true;
}

struct ScheduleStep {
    std::vector<int64_t> tokens;
    int64_t start_position;
};
struct NamedPlan {
    std::string label;
    StickyLayerPlan plan;
};

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

        std::printf("=== FL-07B Commit 3: real-model evidence ===\n");
        const auto load_t0 = std::chrono::steady_clock::now();
        const Model reference_model = materialize_gguf_model(manifest);
        const double load_ms =
            std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - load_t0).count();
        const ModelConfig& cfg = reference_model.config();
        std::printf("[INFO] model loaded in %.1f ms: n_layers=%lld n_q_heads=%lld n_kv_heads=%lld "
                    "head_dim=%lld vocab=%lld max_positions=%lld\n",
                    load_ms, (long long)cfg.n_layers, (long long)cfg.n_q_heads, (long long)cfg.n_kv_heads,
                    (long long)cfg.head_dim, (long long)cfg.vocab, (long long)cfg.max_positions);

        ModelSourceBinding probe = bind_gguf_source(manifest);
        const std::vector<LayerCostInfo> layer_costs = layer_costs_from_source(probe.source);
        check(static_cast<int64_t>(layer_costs.size()) == cfg.n_layers,
              "layer_costs_from_source discovers exactly cfg.n_layers real transformer layers");
        const uint64_t bookend_backing = bookend_backing_bytes(probe.source);
        const uint64_t bookend_resident = bookend_resident_bytes(probe.source);
        uint64_t total_layer_bytes = 0;
        for (const auto& l : layer_costs) total_layer_bytes += l.resident_bytes;
        const uint64_t one_layer_bytes = layer_costs[0].resident_bytes;
        std::printf("[INFO] one transformer layer: %llu resident bytes (%.1f MB); all %lld layers: %llu bytes "
                    "(%.1f MB); bookends: %llu resident bytes (%.1f MB)\n",
                    (unsigned long long)one_layer_bytes, one_layer_bytes / 1e6, (long long)cfg.n_layers,
                    (unsigned long long)total_layer_bytes, total_layer_bytes / 1e6,
                    (unsigned long long)bookend_resident, bookend_resident / 1e6);

        // --- Discover the real model's own greedy continuation for the
        // established "Hello, world!" prompt (Phase 5B's own Section 11
        // integration proof uses the identical prompt ids on the identical
        // GGUF -- FL-07B does not depend on Phase 5B's tokenizer, these
        // four integers are used here as plain literal token ids, exactly
        // like every other FL-07B schedule so far). ----------------------
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
            // Deliberate repeated-token/new-position step: reuse the
            // prompt's own first token id at a brand-new position, proving
            // RoPE-position correctness (not token-identity caching) on
            // the real model too, matching Commit 2's synthetic schedule.
            schedule.push_back({{prompt_ids[0]}, pos});
        }
        std::printf("[INFO] real multi-step schedule (%zu steps): prefill(%zu tokens) + %zu decode steps "
                    "(including 1 deliberate repeated-token/new-position step)\n",
                    schedule.size(), schedule[0].tokens.size(), schedule.size() - 1);

        // --- Authoritative reference: independently-built fully-resident
        // Model, driven through the now-FIXED schedule. --------------------
        ContiguousAttentionKVStore reference_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        std::vector<CachedStepResult> reference_results;
        for (const ScheduleStep& s : schedule) {
            CachedStepResult r = forward_cached_step(reference_model, reference_cache, s.tokens, s.start_position);
            check(all_finite(r.logits), "reference step: logits are all finite");
            reference_results.push_back(std::move(r));
        }

        // --- Plan matrix, equivalent byte budgets used wherever policies
        // are being compared against each other. --------------------------
        std::vector<NamedPlan> plans;
        plans.push_back({"zero-sticky", plan_first_k(layer_costs, 0)});
        plans.push_back({"firstk-small", plan_first_k(layer_costs, one_layer_bytes)});
        const uint64_t medium_budget = total_layer_bytes / 2;
        plans.push_back({"firstk-medium", plan_first_k(layer_costs, medium_budget)});
        {
            // Non-prefix explicit set at the SAME budget as firstk-medium's
            // actual selection count, so the comparison below is at an
            // equivalent residency allocation, not a different one.
            const StickyLayerPlan& fm = plans.back().plan;
            const size_t k = fm.sticky_layer_ids().size();
            std::vector<int64_t> nonprefix_ids;
            for (size_t i = 0; i < k && i < layer_costs.size(); ++i) {
                nonprefix_ids.push_back(layer_costs[layer_costs.size() - 1 - i].layer_id);  // last k layers
            }
            uint64_t nonprefix_budget = 0;
            for (int64_t id : nonprefix_ids) nonprefix_budget += layer_costs[static_cast<size_t>(id)].resident_bytes;
            plans.push_back({"explicit-nonprefix-lastk", plan_explicit_set(layer_costs, nonprefix_ids, nonprefix_budget)});
        }
        {
            // MaterializationCostPerByte with SYNTHETIC benefit data
            // (CostSource::Synthetic, honestly disclosed) -- ranks
            // non-prefix (odd-indexed) layers higher, at the SAME budget
            // as firstk-medium, so it selects a genuinely different,
            // cost-driven set rather than reproducing FirstK's prefix.
            std::vector<LayerCostInfo> ranked = layer_costs;
            for (auto& l : ranked) {
                l.benefit_estimate = (l.layer_id % 2 == 1) ? 2.0 : 1.0;
                l.cost_source = CostSource::Synthetic;
            }
            plans.push_back({"cost-per-byte-synthetic", plan_cost_per_byte(ranked, medium_budget)});
        }
        plans.push_back({"all-sticky", plan_first_k(layer_costs, total_layer_bytes)});

        std::printf("\n=== Per-plan correctness, residency, and I/O evidence ===\n");
        std::vector<uint64_t> backing_bytes_read_by_plan(plans.size(), 0);
        for (size_t pi = 0; pi < plans.size(); ++pi) {
            const NamedPlan& np = plans[pi];
            ModelSourceBinding binding = bind_gguf_source(manifest);
            const uint64_t expected_baseline = bookend_resident + np.plan.planned_resident_bytes();
            StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize), np.plan);
            check(model.telemetry().current_resident_weight_bytes == expected_baseline,
                  np.label + ": resident bytes immediately after construction equal bookends+sticky baseline");
            check(model.sticky_resident_bytes() == np.plan.planned_resident_bytes(),
                  np.label + ": sticky_resident_bytes() equals plan.planned_resident_bytes()");
            check(np.plan.planned_resident_bytes() <= np.plan.byte_budget(),
                  np.label + ": declared byte budget is never exceeded");

            const std::vector<int64_t> cold_ids = cold_layer_ids(layer_costs, np.plan);
            const int64_t sticky_count = static_cast<int64_t>(np.plan.sticky_layer_ids().size());
            const int64_t cold_count = static_cast<int64_t>(cold_ids.size());
            const uint64_t sticky_backing = sum_backing_bytes(layer_costs, np.plan.sticky_layer_ids());
            const uint64_t cold_backing_per_step = sum_backing_bytes(layer_costs, cold_ids);
            const int64_t steps = static_cast<int64_t>(schedule.size());

            ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            for (size_t i = 0; i < schedule.size(); ++i) {
                const ScheduleStep& s = schedule[i];
                CachedStepResult r = model.step(cache, s.tokens, s.start_position);
                check(r.logits == reference_results[i].logits,
                      np.label + ": step " + std::to_string(i) + " complete logits bit-identical to reference");
                check(r.selected_token == reference_results[i].selected_token,
                      np.label + ": step " + std::to_string(i) + " selected token matches reference");
                check(all_finite(r.logits), np.label + ": step " + std::to_string(i) + " logits are all finite");
            }
            check(cache.current_length() == reference_cache.current_length(),
                  np.label + ": committed KV-cache length matches reference");
            check(caches_committed_contents_identical(cache, reference_cache),
                  np.label + ": committed KV-cache contents (every layer/kv_head/position) match reference");
            check(model.telemetry().current_resident_weight_bytes == expected_baseline,
                  np.label + ": resident bytes return to baseline after the full sequence");

            const uint64_t expected_materialization_count =
                3u + static_cast<uint64_t>(sticky_count) * 9u + static_cast<uint64_t>(steps * cold_count) * 9u;
            const uint64_t expected_release_count = static_cast<uint64_t>(steps * cold_count) * 9u;
            const uint64_t expected_backing_bytes_read =
                bookend_backing + sticky_backing + static_cast<uint64_t>(steps) * cold_backing_per_step;
            const uint64_t expected_repeated =
                steps > 0 ? static_cast<uint64_t>(steps - 1) * cold_backing_per_step : 0;
            check(model.telemetry().materialization_count == expected_materialization_count,
                  np.label + ": materialization_count == bookends(3) + sticky*9 + steps*cold*9");
            check(model.telemetry().release_count == expected_release_count,
                  np.label + ": release_count == steps*cold*9");
            check(model.telemetry().backing_bytes_read == expected_backing_bytes_read,
                  np.label + ": backing_bytes_read == bookend + sticky + steps*cold backing bytes");
            check(model.telemetry().repeated_backing_bytes_read == expected_repeated,
                  np.label + ": repeated_backing_bytes_read == (steps-1)*cold backing bytes");
            check(model.telemetry().peak_active_layers == (cold_count > 0 ? 1 : 0),
                  np.label + ": peak_active_layers == 1 iff a cold layer ever executes, else 0");
            const uint64_t expected_peak =
                expected_baseline + (cold_count > 0 ? largest_resident_among(layer_costs, cold_ids) : 0);
            check(model.telemetry().peak_resident_weight_bytes == expected_peak,
                  np.label + ": peak_resident_weight_bytes == baseline + the largest single cold layer resident");

            backing_bytes_read_by_plan[pi] = model.telemetry().backing_bytes_read;
            const double utilization = np.plan.byte_budget() > 0
                ? 100.0 * static_cast<double>(np.plan.planned_resident_bytes()) / static_cast<double>(np.plan.byte_budget())
                : 0.0;
            std::printf("[INFO] %-24s sticky=%2lld/%lld cold=%2lld budget_util=%5.1f%% "
                        "materialized=%llu released=%llu backing_read=%.1fMB repeated_read=%.1fMB "
                        "peak_resident=%.1fMB\n",
                        np.label.c_str(), (long long)sticky_count, (long long)cfg.n_layers, (long long)cold_count,
                        utilization, (unsigned long long)model.telemetry().materialization_count,
                        (unsigned long long)model.telemetry().release_count,
                        model.telemetry().backing_bytes_read / 1e6, model.telemetry().repeated_backing_bytes_read / 1e6,
                        model.telemetry().peak_resident_weight_bytes / 1e6);
        }

        std::printf("\n=== Bytes avoided relative to the zero-sticky baseline (descriptive; no timing "
                    "conclusion drawn here) ===\n");
        const uint64_t zero_sticky_backing = backing_bytes_read_by_plan[0];
        for (size_t pi = 1; pi < plans.size(); ++pi) {
            const int64_t avoided =
                static_cast<int64_t>(zero_sticky_backing) - static_cast<int64_t>(backing_bytes_read_by_plan[pi]);
            std::printf("[INFO] %-24s backing bytes avoided vs. zero-sticky: %.1f MB (%.1f%%)\n",
                        plans[pi].label.c_str(), avoided / 1e6,
                        zero_sticky_backing > 0 ? 100.0 * avoided / static_cast<double>(zero_sticky_backing) : 0.0);
        }

        // --- Determinism: rerun firstk-medium's full sequence a second
        // time from scratch and require bit-identical logits. -------------
        {
            std::printf("\n=== Determinism: repeated correctness run (firstk-medium) ===\n");
            const NamedPlan& fm = plans[2];
            std::vector<std::vector<float>> run_a, run_b;
            for (int run = 0; run < 2; ++run) {
                ModelSourceBinding binding = bind_gguf_source(manifest);
                StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize), fm.plan);
                ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                for (const ScheduleStep& s : schedule) {
                    CachedStepResult r = model.step(cache, s.tokens, s.start_position);
                    (run == 0 ? run_a : run_b).push_back(r.logits);
                }
            }
            bool identical = run_a.size() == run_b.size();
            for (size_t i = 0; identical && i < run_a.size(); ++i) identical = (run_a[i] == run_b[i]);
            check(identical, "firstk-medium: two independent full-sequence runs produce bit-identical logits");
        }

        // --- B3: genuine real-model in-execution fault. Corrupts a real
        // cold layer's own w_down weight to NaN so Phase 5A's own
        // check_finite() throws from inside execute_cached_transformer_layer
        // (after that layer's K/V is written, before the step commits) --
        // same technique Commit 2A used on the synthetic fixture, now
        // exercised on real per-layer weights read from disk. -------------
        {
            std::printf("\n=== Real-model fault evidence ===\n");
            ModelSourceBinding binding = bind_gguf_source(manifest);
            auto corrupting = std::make_shared<CorruptingMaterializer>(std::move(binding.materialize));
            TensorMaterializer wrapped = [corrupting](const LogicalTensor& l, const BackingExtent& b) {
                return (*corrupting)(l, b);
            };
            const StickyLayerPlan p = plans[1].plan;  // firstk-small: layer 0 sticky, rest cold
            const int64_t cold_target_layer = p.sticky_layer_ids().empty() ? 0 : p.sticky_layer_ids().back() + 1;
            const std::string target_name = "layer" + std::to_string(cold_target_layer) + ".w_down";
            const uint64_t expected_baseline = bookend_resident + p.planned_resident_bytes();
            corrupting->set_target(target_name, 1);  // this layer's very first (only, per step) materialization

            StickyLayerCachedModel model(std::move(binding.source), wrapped, p);
            ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            const int64_t pre_fault_length = 0;  // fault targets the VERY FIRST step (prefill)

            bool threw = false;
            std::string what;
            try {
                model.step(cache, schedule[0].tokens, schedule[0].start_position);
            } catch (const std::exception& ex) {
                threw = true;
                what = ex.what();
            }
            check(threw, "real fault: step() propagates the exception");
            check(what.find("NaN/Inf detected") != std::string::npos,
                  "real fault: the exception is Phase 5A's own check_finite() firing from inside "
                  "execute_cached_transformer_layer, not a pre-execution stand-in (message: \"" + what + "\")");
            check(cache.current_length() == pre_fault_length,
                  "real fault: cache.current_length() stays at its pre-step value (committed decode state does "
                  "not advance)");
            check(model.telemetry().current_layer == -1, "real fault: telemetry.current_layer == -1 immediately after");
            check(model.telemetry().current_resident_weight_bytes == expected_baseline,
                  "real fault: resident bytes return exactly to the bookends+sticky baseline (transient "
                  "residency cleaned up)");

            CachedStepResult recovered = model.step(cache, schedule[0].tokens, schedule[0].start_position);
            check(recovered.logits == reference_results[0].logits,
                  "real fault recovery: retried step 0 bit-identical to reference (retry succeeds and matches)");
        }

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
