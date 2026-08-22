// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// FL-07B Commit 2: multi-step cached-decode integration tests.
// StickyLayerCachedModel (sticky_layer_cached_model.hpp) threads a
// persistent ContiguousAttentionKVStore across a prefill step plus several
// decode steps -- including a token id repeated at two different absolute
// positions, to prove correctness depends on POSITION (RoPE), not on
// caching keyed by token identity. Every plan's step sequence is compared,
// step-by-step, against an INDEPENDENTLY-CONSTRUCTED fully-resident
// Reference Path B run (Phase 5A's own forward_cached_step against the
// plain in-memory Model) over: complete logits, selected token, committed
// cache length, and the complete COMMITTED KV-cache contents (every
// layer/kv_head/position from 0 up to current_length() -- unused capacity
// out to max_positions() is never compared, since Phase 5A's own contract
// only defines meaning for committed positions; see
// caches_committed_contents_identical()'s own comment). Reuses Phase 5A's
// execute_cached_transformer_layer seam via StickyLayerCachedModel;
// forward_cached_step itself (the reference driver here) is Phase 5A code,
// called but not modified.
//
// Commit 2A: a Codex review of Commit 2 found four gaps, addressed here --
// (1) plan_first_k could select the wrong layer for an unordered input
// vector (fixed in sticky_layer_plan.cpp, regression-tested in
// test_fl07b_sticky_layer.cpp, not this file); (2) this file's cumulative
// materialization evidence was a bare "> 0" check, replaced below with
// exact per-plan formulas for materialization_count/release_count/
// backing_bytes_read/repeated_backing_bytes_read/peak_active_layers/
// peak_resident_weight_bytes; (3) the "cold-layer execution fault" test
// fired its hook BEFORE execute_cached_transformer_layer ran at all, so it
// never actually failed from inside cached-layer execution -- replaced
// with a genuine in-execution failure (a corrupted cold-layer weight
// tripping Phase 5A's own check_finite() from inside
// execute_cached_transformer_layer, after K/V has already been written for
// that step but before current_length() commits); (4) "physical" cache
// comparison wording corrected to "committed" throughout, matching what
// caches_committed_contents_identical() actually compares.
//
// The synthetic fixture (fixtures_phase1/fixture_untied.txt) has only 2
// transformer layers, which caps how many DISTINCT sticky sets exist (four:
// {}, {0}, {1}, {0,1}) -- the plan matrix below constructs 6 plans across
// those 4 sets (including two MaterializationCostPerByte constructions that
// land on different sets depending on budget), documented as a fixture
// limitation in EXPERIMENT.md rather than overclaimed as 7 distinct sets.
#include <algorithm>
#include <cstdio>
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
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/materialization.hpp"

using namespace orcengine;
using namespace fringelab;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

// Identical to test_fl07b_sticky_layer.cpp's own copy -- kept as this file's
// own local helper per this project's established convention (each test
// file owns its small synthetic-fixture-binding helpers rather than sharing
// a header purely for test code).
ModelSourceBinding bind_memory_model(const Model& model) {
    ModelSource source;
    source.config = model.config();
    source.tied_embeddings = model.manifest.tied_embeddings;
    auto add = [&](TensorRole role, int64_t layer, const std::string& name, const ResidentView& view) {
        const uint64_t bytes = static_cast<uint64_t>(view.raw().size()) * sizeof(float);
        source.tensors.push_back({{role, layer}, LogicalTensor(name, view.shape()),
                                  BackingExtent::FromF32(view.raw()), bytes, std::string("memory:") + name});
    };
    add(TensorRole::TokenEmbedding, -1, "token_embedding", model.token_embedding);
    add(TensorRole::FinalNorm, -1, "final_norm", model.final_norm_weight);
    if (model.lm_head) add(TensorRole::OutputHead, -1, "output_head", *model.lm_head);
    for (int64_t layer = 0; layer < model.config().n_layers; ++layer) {
        const LayerWeights& w = model.layers.at(static_cast<size_t>(layer));
        const std::string prefix = "layer" + std::to_string(layer) + ".";
        add(TensorRole::AttentionNorm, layer, prefix + "attention_norm", w.attn_norm_weight);
        add(TensorRole::AttentionQuery, layer, prefix + "attention_query", w.w_q);
        add(TensorRole::AttentionKey, layer, prefix + "attention_key", w.w_k);
        add(TensorRole::AttentionValue, layer, prefix + "attention_value", w.w_v);
        add(TensorRole::AttentionOutput, layer, prefix + "attention_output", w.w_o);
        add(TensorRole::FfnNorm, layer, prefix + "ffn_norm", w.ffn_norm_weight);
        add(TensorRole::FfnGate, layer, prefix + "ffn_gate", w.w_gate);
        add(TensorRole::FfnUp, layer, prefix + "ffn_up", w.w_up);
        add(TensorRole::FfnDown, layer, prefix + "ffn_down", w.w_down);
    }
    TensorMaterializer full = [](const LogicalTensor& logical, const BackingExtent& backing) {
        return materialize(logical, backing);
    };
    return {std::move(source), std::move(full), nullptr};
}

uint64_t bookend_bytes(const Model& model) {
    uint64_t bytes = static_cast<uint64_t>(model.token_embedding.raw().size()) * sizeof(float);
    bytes += static_cast<uint64_t>(model.final_norm_weight.raw().size()) * sizeof(float);
    if (model.lm_head) bytes += static_cast<uint64_t>(model.lm_head->raw().size()) * sizeof(float);
    return bytes;
}

// Sum of BACKING (on-disk/source) bytes for the three possible bookend
// tensors (token embedding, final norm, and output head if untied) --
// distinct from bookend_bytes() above, which sums RESIDENT (in-memory
// float) bytes. Needed for Commit 2A's exact backing_bytes_read formula.
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

class TrackingMaterializer {
public:
    explicit TrackingMaterializer(TensorMaterializer inner) : inner_(std::move(inner)) {}
    ResidentView operator()(const LogicalTensor& logical, const BackingExtent& backing) {
        ++counts_[logical.name()];
        if (fault_name_.has_value() && logical.name() == *fault_name_) {
            throw std::runtime_error("FL-07B injected materialization fault for '" + logical.name() + "'");
        }
        return inner_(logical, backing);
    }
    void set_fault(std::string name) { fault_name_ = std::move(name); }
    void clear_fault() { fault_name_.reset(); }
    int count(const std::string& name) const {
        const auto it = counts_.find(name);
        return it == counts_.end() ? 0 : it->second;
    }

private:
    TensorMaterializer inner_;
    std::optional<std::string> fault_name_;
    std::map<std::string, int> counts_;
};

// Test-only materializer wrapper (Commit 2A finding 3): corrupts the
// RESIDENT float data of one specific logical-tensor name on its Nth call
// (1-indexed occurrence), setting every element to NaN, then passes
// through cleanly on every other call. Used to make a cold layer's own
// weight genuinely poisoned, so Phase 5A's own check_finite() inside
// execute_cached_transformer_layer (forward_cached.cpp, unmodified) throws
// FROM WITHIN cached-layer execution -- after that layer's K/V has already
// been written into the cache for the current step, but strictly before
// StickyLayerCachedModel::step() reaches its commit-on-success-only
// cache.set_current_length() call. This is a stronger fault than a
// pre-execution hook: it exercises Phase 5A's actual numerical safety net,
// not a test-injected throw standing in for one.
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
    // Corrupts only the `occurrence`-th (1-indexed) materialization call for
    // `name` -- lets earlier steps' materializations of the SAME cold-layer
    // tensor (which re-materializes fresh every step) stay clean, so the
    // corruption targets one specific step.
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

// One step of the shared multi-step schedule every plan and the reference
// are driven through: a 3-token prefill, then 4 single-token decode steps.
// Token 5 (first used at position 1) reappears at position 4; token 1
// (first used at position 0) reappears at position 6 -- both are new
// ABSOLUTE POSITIONS for an already-seen token id, so a driver that
// (incorrectly) keyed any state by token id rather than position would
// diverge from the reference here.
struct ScheduleStep {
    std::vector<int64_t> tokens;
    int64_t start_position;
};
std::vector<ScheduleStep> multi_step_schedule() {
    return {
        {{1, 5, 9}, 0},  // prefill
        {{3}, 3},        // decode
        {{5}, 4},        // decode -- token 5 repeated, new position
        {{9}, 5},        // decode
        {{1}, 6},        // decode -- token 1 repeated, new position
    };
}

// Compares every COMMITTED row (positions [0, current_length())) across
// every layer and kv_head, plus cache metadata. Commit 2A wording fix: this
// does NOT compare unused capacity out to max_positions() -- Phase 5A's own
// contract (context.hpp) only defines meaning for committed positions, so
// "identical committed contents" is the accurate claim; "physical"
// (implying every byte in the underlying storage, including never-written
// capacity) is not, and this function's name and every caller's check
// label now say "committed", not "physical".
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

struct NamedCachedPlan {
    std::string label;
    StickyLayerPlan plan;
};

std::vector<NamedCachedPlan> build_plan_matrix(const std::vector<LayerCostInfo>& layer_costs) {
    std::vector<NamedCachedPlan> plans;
    plans.push_back({"zero-sticky", plan_first_k(layer_costs, 0)});
    plans.push_back({"one-sticky-first", plan_first_k(layer_costs, layer_costs[0].resident_bytes)});
    if (layer_costs.size() >= 2) {
        plans.push_back({"one-sticky-second-nonprefix",
                         plan_explicit_set(layer_costs, {1}, layer_costs[1].resident_bytes)});
        uint64_t all_bytes = 0;
        for (const auto& l : layer_costs) all_bytes += l.resident_bytes;
        plans.push_back({"all-sticky", plan_first_k(layer_costs, all_bytes)});

        // MaterializationCostPerByte, synthetic benefit data: layer 1 ranked
        // higher per byte than layer 0 -- a tight budget selects {1} only
        // (a non-prefix, cost-driven choice a byte-order-only policy like
        // FirstK could never produce), a generous budget selects both.
        std::vector<LayerCostInfo> ranked = layer_costs;
        ranked[0].benefit_estimate = 1.0;
        ranked[0].cost_source = CostSource::Synthetic;
        ranked[1].benefit_estimate = 4.0;
        ranked[1].cost_source = CostSource::Synthetic;
        plans.push_back({"cost-per-byte-tight-budget", plan_cost_per_byte(ranked, layer_costs[1].resident_bytes)});
        plans.push_back({"cost-per-byte-generous-budget", plan_cost_per_byte(ranked, all_bytes)});
    }
    return plans;
}

void run_cached_decode_tests(const std::string& fixtures_dir) {
    std::printf("=== StickyLayerCachedModel multi-step cached-decode tests (Commit 2) ===\n");
    LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_untied.txt");
    const ModelConfig& cfg = fx.model.config();
    const std::vector<ScheduleStep> schedule = multi_step_schedule();

    ModelSourceBinding probe = bind_memory_model(fx.model);
    const std::vector<LayerCostInfo> layer_costs = layer_costs_from_source(probe.source);
    const uint64_t bookends = bookend_bytes(fx.model);
    const uint64_t bookend_backing = bookend_backing_bytes(probe.source);
    const int64_t bookend_tensor_count = probe.source.tied_embeddings ? 2 : 3;
    const int64_t steps = static_cast<int64_t>(schedule.size());

    // Independently-constructed reference: Phase 5A's own fully-resident
    // Reference Path B (forward_cached_step against the plain Model),
    // called through its unmodified public seam.
    ContiguousAttentionKVStore reference_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
    std::vector<CachedStepResult> reference_results;
    reference_results.reserve(schedule.size());
    for (const ScheduleStep& s : schedule) {
        reference_results.push_back(forward_cached_step(fx.model, reference_cache, s.tokens, s.start_position));
    }

    const std::vector<NamedCachedPlan> plans = build_plan_matrix(layer_costs);
    check(!plans.empty(), "plan matrix is non-empty");

    for (const NamedCachedPlan& np : plans) {
        ModelSourceBinding binding = bind_memory_model(fx.model);
        const uint64_t expected_baseline = bookends + np.plan.planned_resident_bytes();
        StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize), np.plan);
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              np.label + ": resident bytes immediately after construction equal bookends+sticky baseline");

        check(model.sticky_resident_bytes() == np.plan.planned_resident_bytes(),
              np.label + ": sticky_resident_bytes() equals plan.planned_resident_bytes()");

        const std::vector<int64_t> cold_ids = cold_layer_ids(layer_costs, np.plan);
        const int64_t sticky_count = static_cast<int64_t>(np.plan.sticky_layer_ids().size());
        const int64_t cold_count = static_cast<int64_t>(cold_ids.size());
        const uint64_t sticky_backing = sum_backing_bytes(layer_costs, np.plan.sticky_layer_ids());
        const uint64_t cold_backing_per_step = sum_backing_bytes(layer_costs, cold_ids);

        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        for (size_t i = 0; i < schedule.size(); ++i) {
            const ScheduleStep& s = schedule[i];
            CachedStepResult result = model.step(cache, s.tokens, s.start_position);
            check(result.logits == reference_results[i].logits,
                  np.label + ": step " + std::to_string(i) + " COMPLETE logits bit-identical to reference");
            check(result.selected_token == reference_results[i].selected_token,
                  np.label + ": step " + std::to_string(i) + " selected token matches reference");
            check(cache.current_length() == s.start_position + static_cast<int64_t>(s.tokens.size()),
                  np.label + ": step " + std::to_string(i) + " committed cache length correct after this step");
            check(model.telemetry().current_resident_weight_bytes == expected_baseline,
                  np.label + ": step " + std::to_string(i) +
                      " resident bytes return to the permanent baseline immediately after this step");
        }
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              np.label + ": resident bytes return to baseline after the full multi-step sequence");

        // Commit 2A finding 2: exact deterministic materialization/I-O
        // schedule, not a bare "> 0" check. bookend_tensor_count and
        // sticky_count*9 tensors materialize exactly once (construction);
        // each of cold_count layers' 9 tensors materializes once per step.
        // Bookends and sticky tensors never release (permanently resident);
        // only cold-layer tensors release, once per step.
        const uint64_t expected_materialization_count =
            static_cast<uint64_t>(bookend_tensor_count) + static_cast<uint64_t>(sticky_count) * 9 +
            static_cast<uint64_t>(steps * cold_count) * 9;
        const uint64_t expected_release_count = static_cast<uint64_t>(steps * cold_count) * 9;
        const uint64_t expected_backing_bytes_read =
            bookend_backing + sticky_backing + static_cast<uint64_t>(steps) * cold_backing_per_step;
        // Only cold-layer backing reads repeat (bookends/sticky materialize
        // once, ever); the first step's cold reads are "new", the
        // remaining (steps-1) steps' reads of the SAME cold extent(s) are
        // "repeated" (ResidencyLedger::materialized() keys repetition by
        // backing_identity, seen_extents_).
        const uint64_t expected_repeated_backing_bytes_read =
            steps > 0 ? static_cast<uint64_t>(steps - 1) * cold_backing_per_step : 0;

        check(model.telemetry().materialization_count == expected_materialization_count,
              np.label + ": materialization_count == bookends(" + std::to_string(bookend_tensor_count) +
                  ") + sticky(" + std::to_string(sticky_count) + "x9) + steps*cold(" + std::to_string(steps) +
                  "x" + std::to_string(cold_count) + "x9)");
        check(model.telemetry().release_count == expected_release_count,
              np.label + ": release_count == steps*cold*9 (bookends and sticky layers never release)");
        check(model.telemetry().backing_bytes_read == expected_backing_bytes_read,
              np.label + ": backing_bytes_read == bookend + sticky + steps*cold backing bytes");
        check(model.telemetry().repeated_backing_bytes_read == expected_repeated_backing_bytes_read,
              np.label + ": repeated_backing_bytes_read == (steps-1)*cold backing bytes "
              "(bookends/sticky never repeat, only re-materialized cold layers do)");
        check(model.telemetry().peak_active_layers == (cold_count > 0 ? 1 : 0),
              np.label + ": peak_active_layers == 1 iff at least one cold layer ever executes, else 0");
        const uint64_t expected_peak =
            expected_baseline + (cold_count > 0 ? largest_resident_among(layer_costs, cold_ids) : 0);
        check(model.telemetry().peak_resident_weight_bytes == expected_peak,
              np.label + ": peak_resident_weight_bytes == baseline + the largest single cold layer resident "
              "among this plan's cold set (exactly one cold layer is ever resident at an instant)");

        check(caches_committed_contents_identical(cache, reference_cache),
              np.label + ": complete committed KV-cache contents (every layer/kv_head/position up to "
              "current_length()) and metadata identical to the reference cache after the full sequence");
    }

    // Cross-plan consistency: two plans that select the SAME final set of
    // sticky layers via different policies (all-sticky FirstK vs the
    // cost-per-byte generous-budget plan, both {0,1}) must themselves be
    // pairwise bit-identical across the whole sequence, not merely each
    // individually identical to the reference.
    if (layer_costs.size() >= 2) {
        ModelSourceBinding binding_a = bind_memory_model(fx.model);
        ModelSourceBinding binding_b = bind_memory_model(fx.model);
        uint64_t all_bytes = 0;
        for (const auto& l : layer_costs) all_bytes += l.resident_bytes;
        std::vector<LayerCostInfo> ranked = layer_costs;
        ranked[0].benefit_estimate = 1.0;
        ranked[1].benefit_estimate = 4.0;
        StickyLayerCachedModel model_a(std::move(binding_a.source), std::move(binding_a.materialize),
                                       plan_first_k(layer_costs, all_bytes));
        StickyLayerCachedModel model_b(std::move(binding_b.source), std::move(binding_b.materialize),
                                       plan_cost_per_byte(ranked, all_bytes));
        ContiguousAttentionKVStore cache_a(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        ContiguousAttentionKVStore cache_b(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        for (const ScheduleStep& s : schedule) {
            CachedStepResult ra = model_a.step(cache_a, s.tokens, s.start_position);
            CachedStepResult rb = model_b.step(cache_b, s.tokens, s.start_position);
            check(ra.logits == rb.logits,
                  "cross-plan: FirstK all-sticky and CostPerByte generous-budget (same final set) agree step-by-step");
        }
    }

    // Identity-aware cumulative materialization proof across the WHOLE
    // multi-step sequence, not one call: sticky layer 0's tensor
    // materializes exactly once (construction only); cold layer 1's tensor
    // materializes exactly once PER step it participates in.
    if (layer_costs.size() >= 2) {
        std::printf("\n--- Identity-aware cumulative materialization proof ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerCachedModel model(std::move(binding.source), wrapped,
                                     plan_first_k(layer_costs, layer_costs[0].resident_bytes));
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        for (const ScheduleStep& s : schedule) model.step(cache, s.tokens, s.start_position);

        check(tracker->count("layer0.attention_query") == 1,
              "cumulative: sticky layer 0's tensor materialized exactly ONCE across all 5 steps (construction only)");
        check(tracker->count("layer1.attention_query") == static_cast<int>(schedule.size()),
              "cumulative: cold layer 1's tensor materialized exactly ONCE PER STEP (5 times total, never cached "
              "between steps)");
        // Commit 2A: the bare "backing_bytes_read > 0" check this block
        // used to end with is now superseded by the EXACT per-plan
        // materialization_count/release_count/backing_bytes_read/
        // repeated_backing_bytes_read formulas asserted for every plan
        // (including this exact "one-sticky-first" construction) in the
        // main per-plan loop above -- not repeated here to avoid asserting
        // the same numbers twice under two different names.
    }

    // Fault injection 1: PARTIAL cold-layer materialization failure at a
    // MIDDLE step (step index 2, the repeated-token-5 decode step). Proves
    // logical cache rollback: current_length() stays at its PRE-step value
    // (the committed-length gate, not a physical wipe of any bytes this
    // step's earlier-executing layers may have already written -- matching
    // Phase 5A's own documented "commit on success only" semantics, see
    // context.hpp's write_row comment), and a retried step recovers bit-exact.
    if (layer_costs.size() >= 2) {
        std::printf("\n--- Fault injection 1: partial cold-layer materialization failure mid-sequence ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto tracker = std::make_shared<TrackingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [tracker](const LogicalTensor& l, const BackingExtent& b) {
            return (*tracker)(l, b);
        };
        StickyLayerPlan p = plan_first_k(layer_costs, layer_costs[0].resident_bytes);  // layer 0 sticky, 1 cold
        const uint64_t expected_baseline = bookends + p.planned_resident_bytes();
        StickyLayerCachedModel model(std::move(binding.source), wrapped, p);
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        for (size_t i = 0; i < 2; ++i) model.step(cache, schedule[i].tokens, schedule[i].start_position);
        const int64_t pre_fault_length = cache.current_length();

        tracker->set_fault("layer1.ffn_down");
        bool threw = false;
        try {
            model.step(cache, schedule[2].tokens, schedule[2].start_position);
        } catch (const std::exception&) {
            threw = true;
        }
        tracker->clear_fault();

        check(threw, "partial-fault: step() propagates the exception");
        check(cache.current_length() == pre_fault_length,
              "partial-fault: cache.current_length() stays at its pre-step value (logical rollback via the "
              "commit-on-success-only gate)");
        check(model.telemetry().current_layer == -1, "partial-fault: telemetry.current_layer == -1 immediately after");
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              "partial-fault: resident bytes return exactly to the bookends+sticky baseline");

        CachedStepResult recovered = model.step(cache, schedule[2].tokens, schedule[2].start_position);
        check(recovered.logits == reference_results[2].logits,
              "partial-fault recovery: retried step 2 bit-identical to reference");
        check(cache.current_length() ==
                  schedule[2].start_position + static_cast<int64_t>(schedule[2].tokens.size()),
              "partial-fault recovery: cache length correct after the retried step");
        check(tracker->count("layer0.attention_query") == 1,
              "partial-fault: sticky layer 0 was not rematerialized by the failed or the recovery step");

        for (size_t i = 3; i < schedule.size(); ++i) {
            CachedStepResult r = model.step(cache, schedule[i].tokens, schedule[i].start_position);
            check(r.logits == reference_results[i].logits,
                  "partial-fault recovery: subsequent step " + std::to_string(i) + " remains bit-identical to reference");
        }
        check(caches_committed_contents_identical(cache, reference_cache),
              "partial-fault recovery: complete committed KV-cache contents identical to reference after the "
              "full sequence completes despite the mid-sequence fault and retry");
    }

    // Fault injection 2 (Commit 2A finding 3): a GENUINE in-execution
    // failure, not a pre-execution hook standing in for one. Commit 2's
    // original version of this test used fault_before_cold_execute, which
    // fires BEFORE execute_cached_transformer_layer runs at all -- it
    // proved fully-materialized weights are released when a pre-execution
    // hook throws, but never actually exercised a failure occurring FROM
    // INSIDE cached-layer execution, contrary to the charter's "during the
    // execute call itself" wording. This version corrupts the cold layer's
    // own FFN-down weight to NaN via CorruptingMaterializer, so Phase 5A's
    // own check_finite() (forward_cached.cpp's local helper, called on
    // "layerN.post_ffn_residual" inside execute_cached_transformer_layer,
    // unmodified) is what actually throws -- strictly AFTER that layer's
    // K/V has already been written into the cache for this step (K/V
    // writes happen near the top of execute_cached_transformer_layer,
    // before the FFN), but strictly BEFORE step()'s
    // cache.set_current_length() commit call, which only runs after every
    // layer in the loop has returned successfully.
    if (layer_costs.size() >= 2) {
        std::printf("\n--- Fault injection 2: genuine in-execution cold-layer failure mid-sequence ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        auto corrupting = std::make_shared<CorruptingMaterializer>(std::move(binding.materialize));
        TensorMaterializer wrapped = [corrupting](const LogicalTensor& l, const BackingExtent& b) {
            return (*corrupting)(l, b);
        };
        StickyLayerPlan p = plan_first_k(layer_costs, layer_costs[0].resident_bytes);  // layer 0 sticky, 1 cold
        const uint64_t expected_baseline = bookends + p.planned_resident_bytes();
        StickyLayerCachedModel model(std::move(binding.source), wrapped, p);
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

        // Armed from the START (before any step runs), not right before the
        // targeted call -- the occurrence counter only advances on calls
        // that happen while a target name is set, so arming it here is what
        // makes "occurrence 4" actually land on step index 3's
        // materialization of layer1.ffn_down (steps 0, 1, 2's calls are
        // occurrences 1, 2, 3; the corruption itself only activates on the
        // 4th, everything before and after passes through clean).
        corrupting->set_target("layer1.ffn_down", 4);
        for (size_t i = 0; i < 3; ++i) model.step(cache, schedule[i].tokens, schedule[i].start_position);
        const int64_t pre_fault_length = cache.current_length();

        // Snapshot every COMMITTED row across every layer/kv_head before
        // the fault, to later prove the committed PREFIX is untouched by
        // the failed step (only the failed step's own new positions could
        // possibly have been written and then left uncommitted).
        std::vector<std::vector<float>> pre_fault_k, pre_fault_v;
        for (int64_t li = 0; li < cfg.n_layers; ++li) {
            for (int64_t h = 0; h < cfg.n_kv_heads; ++h) {
                for (int64_t pos = 0; pos < pre_fault_length; ++pos) {
                    const float* k = cache.k_row(li, h, pos);
                    const float* v = cache.v_row(li, h, pos);
                    pre_fault_k.emplace_back(k, k + cfg.head_dim);
                    pre_fault_v.emplace_back(v, v + cfg.head_dim);
                }
            }
        }

        bool threw = false;
        std::string what;
        try {
            model.step(cache, schedule[3].tokens, schedule[3].start_position);
        } catch (const std::exception& ex) {
            threw = true;
            what = ex.what();
        }

        check(threw, "in-execution-fault: step() propagates the exception");
        check(what.find("NaN/Inf detected") != std::string::npos,
              "in-execution-fault: the exception is Phase 5A's own check_finite() firing from inside "
              "execute_cached_transformer_layer (message: \"" + what + "\"), not a test-injected stand-in");
        check(cache.current_length() == pre_fault_length,
              "in-execution-fault: cache.current_length() stays at its pre-step value (logical rollback)");
        check(model.telemetry().current_layer == -1, "in-execution-fault: telemetry.current_layer == -1 immediately after");
        check(model.telemetry().current_resident_weight_bytes == expected_baseline,
              "in-execution-fault: resident bytes return exactly to the bookends+sticky baseline");

        bool prefix_unchanged = true;
        size_t idx = 0;
        for (int64_t li = 0; li < cfg.n_layers && prefix_unchanged; ++li) {
            for (int64_t h = 0; h < cfg.n_kv_heads && prefix_unchanged; ++h) {
                for (int64_t pos = 0; pos < pre_fault_length && prefix_unchanged; ++pos, ++idx) {
                    const float* k = cache.k_row(li, h, pos);
                    const float* v = cache.v_row(li, h, pos);
                    for (int64_t d = 0; d < cfg.head_dim; ++d) {
                        if (k[static_cast<size_t>(d)] != pre_fault_k[idx][static_cast<size_t>(d)] ||
                            v[static_cast<size_t>(d)] != pre_fault_v[idx][static_cast<size_t>(d)]) {
                            prefix_unchanged = false;
                        }
                    }
                }
            }
        }
        check(prefix_unchanged,
              "in-execution-fault: the COMMITTED prefix (every layer/kv_head/position below the pre-fault "
              "length) is byte-for-byte unchanged by the failed step, across every layer including the one "
              "that failed mid-execution");

        CachedStepResult recovered = model.step(cache, schedule[3].tokens, schedule[3].start_position);
        check(recovered.logits == reference_results[3].logits,
              "in-execution-fault recovery: retried step 3 bit-identical to reference");
        // Direct overwrite proof: the retry's NEW position's K/V now
        // exactly matches the reference's K/V at that same position. Note
        // the failed attempt's own K/V write at this position was already
        // FINITE and correct -- the corrupted ffn_down weight only reaches
        // the FFN path (computed downstream of, and independently from,
        // the K/V projections), so check_finite's NaN detection fires on
        // the layer's post-FFN output activation, never on K or V
        // themselves. What this proves is narrower than "overwriting
        // poisoned data": the retry revisits the previously
        // written-but-uncommitted positions (written during the failed
        // attempt, never committed since current_length() did not advance)
        // and leaves their COMMITTED K/V exactly equal to the reference,
        // regardless of what value transiently occupied them beforehand.
        const int64_t new_position = schedule[3].start_position;
        for (int64_t li = 0; li < cfg.n_layers; ++li) {
            for (int64_t h = 0; h < cfg.n_kv_heads; ++h) {
                const float* k = cache.k_row(li, h, new_position);
                const float* rk = reference_cache.k_row(li, h, new_position);
                const float* v = cache.v_row(li, h, new_position);
                const float* rv = reference_cache.v_row(li, h, new_position);
                bool row_matches = true;
                for (int64_t d = 0; d < cfg.head_dim; ++d) {
                    if (k[static_cast<size_t>(d)] != rk[static_cast<size_t>(d)] ||
                        v[static_cast<size_t>(d)] != rv[static_cast<size_t>(d)]) {
                        row_matches = false;
                    }
                }
                check(row_matches, "in-execution-fault recovery: retried step's K/V at layer " +
                      std::to_string(li) + " kv_head " + std::to_string(h) +
                      " (previously written but never committed by the failed attempt) now matches the "
                      "reference exactly");
            }
        }

        CachedStepResult last = model.step(cache, schedule[4].tokens, schedule[4].start_position);
        check(last.logits == reference_results[4].logits,
              "in-execution-fault recovery: final step 4 bit-identical to reference");
        check(caches_committed_contents_identical(cache, reference_cache),
              "in-execution-fault recovery: complete committed KV-cache contents identical to reference after "
              "the full sequence completes despite the mid-sequence in-execution fault and retry");
    }

    // Position-invariant proof: the safe step() entry point rejects a
    // position mismatch before any mutation, mirroring Phase 5A's own
    // P5A-RVW-002 guard (forward_cached_step / VirtualizedCachedModel::step).
    {
        std::printf("\n--- Position-invariant enforcement ---\n");
        ModelSourceBinding binding = bind_memory_model(fx.model);
        StickyLayerCachedModel model(std::move(binding.source), std::move(binding.materialize),
                                     plan_first_k(layer_costs, layer_costs[0].resident_bytes));
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        model.step(cache, schedule[0].tokens, schedule[0].start_position);
        bool threw = false;
        try {
            model.step(cache, schedule[1].tokens, schedule[1].start_position + 1);  // wrong position
        } catch (const KVCacheError&) {
            threw = true;
        }
        check(threw, "step(): position mismatch rejected with KVCacheError before any mutation");
        check(cache.current_length() == schedule[0].start_position + static_cast<int64_t>(schedule[0].tokens.size()),
              "step(): rejected position-mismatch call left cache length unchanged");
    }
}

}  // namespace

int main(int argc, char** argv) {
    try {
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
        run_cached_decode_tests(fixtures_dir);

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
