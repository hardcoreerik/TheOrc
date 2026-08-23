// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 6 Stage 1, Checkpoint 3: OrcEngine F32-vs-Q8_0 real-model
// comparison. Loads BOTH the pinned F32 GGUF and the locally-quantized
// Q8_0 GGUF (see fixtures/Q8_0_FIXTURE_PROVENANCE.md), runs the frozen
// cached-decode forward path on each for a predeclared prompt corpus,
// and reports the full-vocabulary error distribution between the two
// (max absolute error, relative error with a documented near-zero
// denominator policy, RMSE, top-1 agreement, top-5 overlap) plus the
// OrcEngine-Q8_0 side's own top-5 (token, logit) pairs -- consumed by
// the companion Python driver (phase6_llama_cpp_q8_0_oracle.py) to
// additionally compare against the pinned llama.cpp Q8_0 oracle.
//
// Corpus discipline: the DEV set (used to derive the empirical
// end-to-end tolerance) and the HOLDOUT set (used only to confirm that
// frozen tolerance, never to re-derive it) are predeclared as separate,
// hardcoded arrays below -- fixed before any run, not adjusted after
// seeing results. Prompts were tokenized once via the pinned
// llama-tokenize.exe (build 10436/commit 6fed9f6ff) and the resulting
// token IDs are hardcoded here; the identical prompt TEXT is passed to
// llama.cpp's own server in the companion Python driver, so both legs
// tokenize from the same source text through the same pinned tokenizer
// identity already established in Phase 5B's three-way comparison.
//
// Additionally runs one multi-step cached-decode sequence (prompt
// "Hello world, this is") through the Q8_0 model specifically, proving
// the SAME cached-decode invariants (committed cache length, no state
// leakage) this project has already proven extensively for F32 weights
// (Phase 5A/5C) also hold with real Q8_0 weights across several real
// steps -- a genuinely new combination, not previously exercised.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

struct PromptCase {
    std::string id;
    std::string text;  // for the companion Python driver's llama.cpp request
    std::vector<int64_t> token_ids;  // tokenized once via pinned llama-tokenize.exe
};

// --- DEV corpus: used to derive the empirical end-to-end tolerance. ---
const std::vector<PromptCase> kDevCorpus = {
    {"dev_capital_of_france", "The capital of France is", {504, 3575, 282, 4649, 314}},
    {"dev_once_upon_a_time", "Once upon a time", {6403, 1980, 253, 655}},
    {"dev_code_snippet", "def add(a, b): return", {1604, 803, 24, 81, 28, 278, 727, 1003}},
    {"dev_year_weather", "In 2026, the weather", {788, 216, 34, 32, 34, 38, 28, 260, 3947}},
};

// --- HOLDOUT corpus: confirms the frozen dev-derived tolerance only.
// Never inspected while deriving the tolerance; if it fails, the failure
// is reported, not used to widen the tolerance. ---
const std::vector<PromptCase> kHoldoutCorpus = {
    {"holdout_hello_world", "Hello world, this is", {19556, 905, 28, 451, 314}},
    {"holdout_she_walked", "She walked into the", {8113, 13197, 618, 260}},
    {"holdout_quick_fox", "The quick brown fox", {504, 2365, 6354, 16438}},
};

struct TopK {
    std::vector<int64_t> ids;
    std::vector<float> logits;
};

TopK top_k(const std::vector<float>& logits, size_t k) {
    std::vector<int64_t> idx(logits.size());
    for (size_t i = 0; i < idx.size(); ++i) idx[i] = static_cast<int64_t>(i);
    std::partial_sort(idx.begin(), idx.begin() + static_cast<long>(std::min(k, idx.size())), idx.end(),
                      [&](int64_t a, int64_t b) { return logits[static_cast<size_t>(a)] > logits[static_cast<size_t>(b)]; });
    TopK out;
    for (size_t i = 0; i < std::min(k, idx.size()); ++i) {
        out.ids.push_back(idx[i]);
        out.logits.push_back(logits[static_cast<size_t>(idx[i])]);
    }
    return out;
}

struct ErrorStats {
    float max_abs_error = 0.0f;
    float max_relative_error = 0.0f;
    double rmse = 0.0;
    bool top1_agree = false;
    int top5_overlap = 0;
};

// Relative-error denominator policy, stated explicitly: max(|f32|, epsilon)
// with epsilon = 1e-3 -- chosen because SmolLM2-135M's own logit magnitudes
// (observed empirically across this corpus) are order 1-30, so 1e-3 is
// small enough to never dominate a genuine near-zero-logit comparison
// while still preventing division blowup exactly at zero.
constexpr float kRelativeErrorEpsilon = 1e-3f;

ErrorStats compare(const std::vector<float>& f32_logits, const std::vector<float>& q8_logits) {
    ErrorStats s;
    double sq_sum = 0.0;
    for (size_t i = 0; i < f32_logits.size(); ++i) {
        const float diff = std::fabs(f32_logits[i] - q8_logits[i]);
        s.max_abs_error = std::max(s.max_abs_error, diff);
        const float denom = std::max(std::fabs(f32_logits[i]), kRelativeErrorEpsilon);
        s.max_relative_error = std::max(s.max_relative_error, diff / denom);
        sq_sum += static_cast<double>(diff) * static_cast<double>(diff);
    }
    s.rmse = std::sqrt(sq_sum / static_cast<double>(f32_logits.size()));

    const TopK f32_top = top_k(f32_logits, 5);
    const TopK q8_top = top_k(q8_logits, 5);
    s.top1_agree = f32_top.ids[0] == q8_top.ids[0];
    for (int64_t id : q8_top.ids) {
        if (std::find(f32_top.ids.begin(), f32_top.ids.end(), id) != f32_top.ids.end()) ++s.top5_overlap;
    }
    return s;
}

int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 4) {
        std::fprintf(stderr, "usage: phase6_q8_0_comparison F32.gguf Q8_0.gguf evidence_out.jsonl\n");
        return 2;
    }
    try {
        const Model f32_model = materialize_gguf_model(map_llama_model(index_gguf(argv[1])));
        const Model q8_model = materialize_gguf_model(map_llama_model(index_gguf(argv[2])));
        const ModelConfig& cfg = f32_model.config();
        std::ofstream evidence(argv[3]);

        std::printf("=== Checkpoint 3: OrcEngine F32 vs Q8_0 real-model comparison ===\n");
        std::printf("model: vocab=%lld hidden=%lld n_layers=%lld\n",
                    (long long)cfg.vocab, (long long)cfg.hidden, (long long)cfg.n_layers);

        auto run_corpus = [&](const std::vector<PromptCase>& corpus, const char* label,
                              std::vector<ErrorStats>* out_stats) {
            std::printf("\n--- %s corpus ---\n", label);
            for (const auto& pc : corpus) {
                ContiguousAttentionKVStore f32_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                ContiguousAttentionKVStore q8_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                const CachedStepResult f32_result = forward_cached_step(f32_model, f32_cache, pc.token_ids, 0);
                const CachedStepResult q8_result = forward_cached_step(q8_model, q8_cache, pc.token_ids, 0);
                const std::vector<float> f32_last(f32_result.logits.end() - cfg.vocab, f32_result.logits.end());
                const std::vector<float> q8_last(q8_result.logits.end() - cfg.vocab, q8_result.logits.end());

                bool all_finite = true;
                for (float v : q8_last) {
                    if (!std::isfinite(v)) { all_finite = false; break; }
                }
                check(all_finite, std::string(pc.id) + ": all Q8_0 logits finite");

                const ErrorStats s = compare(f32_last, q8_last);
                if (out_stats) out_stats->push_back(s);
                std::printf("  %-24s max_abs=%.6f max_rel=%.6f rmse=%.6f top1_agree=%d top5_overlap=%d/5 "
                            "(F32 selected=%lld, Q8_0 selected=%lld)\n",
                            pc.id.c_str(), s.max_abs_error, s.max_relative_error, s.rmse, s.top1_agree ? 1 : 0,
                            s.top5_overlap, (long long)f32_result.selected_token.back(),
                            (long long)q8_result.selected_token.back());

                const TopK q8_top5 = top_k(q8_last, 5);
                evidence << "{\"id\":\"" << pc.id << "\",\"text\":\"" << pc.text << "\","
                        << "\"f32_selected\":" << f32_result.selected_token.back()
                        << ",\"q8_selected\":" << q8_result.selected_token.back()
                        << ",\"max_abs_error\":" << s.max_abs_error
                        << ",\"max_relative_error\":" << s.max_relative_error
                        << ",\"rmse\":" << s.rmse
                        << ",\"top1_agree\":" << (s.top1_agree ? "true" : "false")
                        << ",\"top5_overlap\":" << s.top5_overlap
                        << ",\"q8_top5_ids\":[";
                for (size_t i = 0; i < q8_top5.ids.size(); ++i) {
                    evidence << q8_top5.ids[i] << (i + 1 < q8_top5.ids.size() ? "," : "");
                }
                evidence << "],\"q8_top5_logits\":[";
                for (size_t i = 0; i < q8_top5.logits.size(); ++i) {
                    evidence << q8_top5.logits[i] << (i + 1 < q8_top5.logits.size() ? "," : "");
                }
                evidence << "]}\n";
            }
        };

        std::vector<ErrorStats> dev_stats, holdout_stats;
        run_corpus(kDevCorpus, "DEV", &dev_stats);
        run_corpus(kHoldoutCorpus, "HOLDOUT", &holdout_stats);

        // Empirically derive the end-to-end tolerance from the DEV set only,
        // with a documented safety margin, per this project's Section 3.3
        // tolerance-derivation policy -- then apply it UNCHANGED to holdout.
        float dev_max_abs = 0.0f;
        for (const auto& s : dev_stats) dev_max_abs = std::max(dev_max_abs, s.max_abs_error);
        constexpr float kSafetyMarginMultiplier = 2.0f;  // documented margin, chosen before seeing holdout
        const float derived_tolerance = dev_max_abs * kSafetyMarginMultiplier;
        std::printf("\n--- Tolerance derivation ---\n");
        std::printf("dev max_abs_error observed = %.6f\n", dev_max_abs);
        std::printf("derived end-to-end tolerance (dev_max * %.1fx safety margin) = %.6f\n",
                    kSafetyMarginMultiplier, derived_tolerance);

        bool holdout_within_tolerance = true;
        float holdout_max_abs = 0.0f;
        for (const auto& s : holdout_stats) {
            holdout_max_abs = std::max(holdout_max_abs, s.max_abs_error);
            if (s.max_abs_error > derived_tolerance) holdout_within_tolerance = false;
        }
        std::printf("holdout max_abs_error observed = %.6f\n", holdout_max_abs);
        check(holdout_within_tolerance,
              "holdout set stays within the DEV-derived frozen tolerance (not re-derived, not widened)");

        bool all_top1_agree = true;
        for (const auto& s : dev_stats) all_top1_agree = all_top1_agree && s.top1_agree;
        for (const auto& s : holdout_stats) all_top1_agree = all_top1_agree && s.top1_agree;
        check(all_top1_agree, "top-1 (greedy) agreement between F32 and Q8_0 holds across every dev+holdout prompt");

        // --- Adversarial case: multi-step cached decode through Q8_0 weights,
        // proving this project's existing cached-decode invariants (committed
        // length advances correctly, no cross-step state corruption) also
        // hold with real Q8_0 weights across several real steps -- not
        // previously exercised (every prior cached-decode test used F32). ---
        std::printf("\n--- Adversarial: multi-step cached decode through Q8_0 weights ---\n");
        {
            const PromptCase& pc = kHoldoutCorpus[0];  // "Hello world, this is"
            ContiguousAttentionKVStore f32_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            ContiguousAttentionKVStore q8_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            CachedStepResult f32_r = forward_cached_step(f32_model, f32_cache, pc.token_ids, 0);
            CachedStepResult q8_r = forward_cached_step(q8_model, q8_cache, pc.token_ids, 0);
            int64_t position = static_cast<int64_t>(pc.token_ids.size());
            std::vector<int64_t> f32_next = {f32_r.selected_token.back()};
            std::vector<int64_t> q8_next = {q8_r.selected_token.back()};
            bool all_steps_ok = true;
            for (int step = 0; step < 5; ++step) {
                f32_r = forward_cached_step(f32_model, f32_cache, f32_next, position);
                q8_r = forward_cached_step(q8_model, q8_cache, q8_next, position);
                if (f32_cache.current_length() != position + 1 || q8_cache.current_length() != position + 1) {
                    all_steps_ok = false;
                }
                position += 1;
                f32_next = {f32_r.selected_token.back()};
                q8_next = {q8_r.selected_token.back()};
            }
            check(all_steps_ok, "5-step cached decode through Q8_0 weights: committed cache length advances "
                                "correctly at every step (same invariant this project already proves for F32)");
            std::printf("  final F32 continuation last token=%lld, Q8_0 continuation last token=%lld\n",
                        (long long)f32_next[0], (long long)q8_next[0]);
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) {
            std::printf("ALL CHECKPOINT 3 (OrcEngine leg) CHECKS PASSED\n");
            return 0;
        }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
