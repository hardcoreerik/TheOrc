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
// the SPECIFIC invariant it actually checks -- committed cache length
// advances by exactly one at every step -- also holds with real Q8_0
// weights across several real steps (a genuinely new combination, not
// previously exercised). This does NOT independently re-derive or
// replay cache CONTENTS, so it is not, by itself, a proof of "no state
// leakage" in the stronger sense (no cross-step content corruption);
// narrowed from an earlier overstated claim per Codex review.
//
// Evidence schema v2 (Codex remediation Stage 2): the companion Python
// driver's earlier top-5-only log-softmax reconstruction was
// mathematically invalid (normalizing a truncated top-5 slice and
// comparing it to llama.cpp's FULL-VOCABULARY log-probability). This
// tool now computes and emits the FULL-VOCABULARY logsumexp for the
// OrcEngine Q8_0 logits at every compared position, at max_digits10
// round-trip precision, so the Python driver can compute an exact
// orc_logprob = raw_orc_logit - full_vocab_logsumexp for any token,
// not just tokens inside a top-k slice. Also emits: exact prompt token
// IDs (for the driver's own token-ID-identity proof against
// llama.cpp's tokenizer), both engines' top-5 (id, logit) pairs, the
// compared position, a metric-contract version, and a run-level
// manifest recording the F32/Q8_0 artifact paths/sizes/expected SHA-256
// (asserted equal to the values independently recorded and provenance-
// documented in fixtures/Q8_0_FIXTURE_PROVENANCE.md; this tool does not
// itself implement SHA-256 -- it performs a cheap fail-closed file-size
// sanity check against the recorded provenance and otherwise trusts
// that already-established, separately-verified provenance record
// rather than re-deriving cryptographic hashes in C++, which would add
// a numerical/crypto dependency out of proportion to Stage 1's scope).
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <limits>
#include <sstream>
#include <string>
#include <vector>
#include <algorithm>

#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

constexpr int kMetricContractVersion = 2;

// Expected provenance, asserted (via file size) against
// fixtures/Q8_0_FIXTURE_PROVENANCE.md's independently recorded values --
// SHA-256 authority lives in that document (computed there via Python
// hashlib, not re-derived here); this constant is the run-level manifest
// reference this tool's evidence carries forward.
constexpr const char* kF32ExpectedSha256 = "fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac";
constexpr const char* kQ8ExpectedSha256 = "3aed955db7e8e7e73e12a05964ad9efb79cef77a895120a77475d7743609d398";

struct PromptCase {
    std::string id;
    std::string text;  // for the companion Python driver's llama.cpp request
    std::vector<int64_t> token_ids;  // tokenized once via pinned llama-tokenize.exe
};

// Full-vocabulary logsumexp, numerically stabilized against the max
// logit. This is the exact denominator log-softmax needs; computing it
// over the FULL logits vector (not a top-k slice) is the whole point --
// a top-k-only normalization is a different, smaller quantity and must
// never be described as the softmax denominator.
double full_vocab_logsumexp(const std::vector<float>& logits) {
    double max_logit = -std::numeric_limits<double>::infinity();
    for (float v : logits) max_logit = std::max(max_logit, static_cast<double>(v));
    double sum = 0.0;
    for (float v : logits) sum += std::exp(static_cast<double>(v) - max_logit);
    return std::log(sum) + max_logit;
}

// Serializes a double at full round-trip precision (max_digits10), not
// the stream's default six significant digits -- required so the
// companion Python driver can reconstruct exact log-probabilities from
// this evidence rather than losing precision to truncated text.
std::string precise(double value) {
    std::ostringstream os;
    os.precision(std::numeric_limits<double>::max_digits10);
    os << value;
    return os.str();
}
std::string precise(float value) { return precise(static_cast<double>(value)); }

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
        const std::filesystem::path f32_path = argv[1];
        const std::filesystem::path q8_path = argv[2];
        std::error_code size_ec;
        const uintmax_t f32_size = std::filesystem::file_size(f32_path, size_ec);
        if (size_ec) throw std::runtime_error("cannot stat F32 GGUF '" + f32_path.string() + "': " + size_ec.message());
        const uintmax_t q8_size = std::filesystem::file_size(q8_path, size_ec);
        if (size_ec) throw std::runtime_error("cannot stat Q8_0 GGUF '" + q8_path.string() + "': " + size_ec.message());
        // Cheap fail-closed sanity check against the independently recorded
        // provenance record (Q8_0_FIXTURE_PROVENANCE.md): the SHA-256 values
        // there were computed via Python hashlib against these exact files at
        // provenance-recording time; a gross file-size mismatch here means
        // the wrong file was passed and must abort before comparing anything.
        constexpr uintmax_t kExpectedF32Size = 653091040;  // exact byte count of the pinned F32 SmolLM2-135M GGUF
        constexpr uintmax_t kExpectedQ8Size = 174891296;   // exact byte count of the pinned Q8_0 fixture, per Q8_0_FIXTURE_PROVENANCE.md
        if (f32_size != kExpectedF32Size) {
            throw std::runtime_error("F32 GGUF size " + std::to_string(f32_size) +
                                     " does not match provenance-recorded size " + std::to_string(kExpectedF32Size) +
                                     " -- wrong file or provenance is stale, ABORTING");
        }
        if (q8_size != kExpectedQ8Size) {
            throw std::runtime_error("Q8_0 GGUF size " + std::to_string(q8_size) +
                                     " does not match provenance-recorded size " + std::to_string(kExpectedQ8Size) +
                                     " -- wrong file or provenance is stale, ABORTING");
        }

        const Model f32_model = materialize_gguf_model(map_llama_model(index_gguf(f32_path)));
        const Model q8_model = materialize_gguf_model(map_llama_model(index_gguf(q8_path)));
        const ModelConfig& cfg = f32_model.config();

        // Fail closed on F32/Q8 model configuration mismatch -- comparing
        // logits from architecturally different models would be meaningless,
        // not merely inaccurate.
        const ModelConfig& q8_cfg = q8_model.config();
        if (cfg.vocab != q8_cfg.vocab || cfg.hidden != q8_cfg.hidden || cfg.n_layers != q8_cfg.n_layers ||
            cfg.n_q_heads != q8_cfg.n_q_heads || cfg.n_kv_heads != q8_cfg.n_kv_heads ||
            cfg.head_dim != q8_cfg.head_dim || cfg.max_positions != q8_cfg.max_positions) {
            throw std::runtime_error("F32 and Q8_0 model configurations differ -- ABORTING "
                                     "(cannot compare logits from architecturally different models)");
        }

        std::ofstream evidence(argv[3]);
        if (!evidence) throw std::runtime_error("cannot open evidence output file '" + std::string(argv[3]) + "'");
        evidence.exceptions(std::ios::badbit);  // fail closed on a write-time I/O error

        std::printf("=== Checkpoint 3: OrcEngine F32 vs Q8_0 real-model comparison ===\n");
        std::printf("model: vocab=%lld hidden=%lld n_layers=%lld\n",
                    (long long)cfg.vocab, (long long)cfg.hidden, (long long)cfg.n_layers);
        std::printf("F32 artifact: %s (%llu bytes, expected sha256 %s)\n",
                    f32_path.string().c_str(), (unsigned long long)f32_size, kF32ExpectedSha256);
        std::printf("Q8_0 artifact: %s (%llu bytes, expected sha256 %s)\n",
                    q8_path.string().c_str(), (unsigned long long)q8_size, kQ8ExpectedSha256);

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

                // Fail closed: empty logits, and non-finite values in EITHER
                // engine's output (previously only Q8_0 was checked).
                if (f32_last.empty() || q8_last.empty()) {
                    throw std::runtime_error(pc.id + ": empty logits vector");
                }
                if (f32_last.size() != static_cast<size_t>(cfg.vocab) ||
                    q8_last.size() != static_cast<size_t>(cfg.vocab)) {
                    throw std::runtime_error(pc.id + ": logits size does not match vocabulary size");
                }
                bool f32_finite = true, q8_finite = true;
                for (float v : f32_last) { if (!std::isfinite(v)) { f32_finite = false; break; } }
                for (float v : q8_last) { if (!std::isfinite(v)) { q8_finite = false; break; } }
                check(f32_finite, pc.id + ": all F32 logits finite");
                check(q8_finite, pc.id + ": all Q8_0 logits finite");
                if (!f32_finite || !q8_finite) {
                    throw std::runtime_error(pc.id + ": non-finite logits, aborting comparison for this prompt");
                }

                const ErrorStats s = compare(f32_last, q8_last);
                if (out_stats) out_stats->push_back(s);
                std::printf("  %-24s max_abs=%.6f max_rel=%.6f rmse=%.6f top1_agree=%d top5_overlap=%d/5 "
                            "(F32 selected=%lld, Q8_0 selected=%lld)\n",
                            pc.id.c_str(), s.max_abs_error, s.max_relative_error, s.rmse, s.top1_agree ? 1 : 0,
                            s.top5_overlap, (long long)f32_result.selected_token.back(),
                            (long long)q8_result.selected_token.back());

                const TopK q8_top5 = top_k(q8_last, 5);
                const TopK f32_top5 = top_k(f32_last, 5);
                const double q8_logsumexp = full_vocab_logsumexp(q8_last);
                const int64_t position = static_cast<int64_t>(pc.token_ids.size()) - 1;  // last-position comparison only

                evidence << "{\"schema_version\":" << kMetricContractVersion
                        << ",\"id\":\"" << pc.id << "\",\"text\":\"" << pc.text << "\","
                        << "\"token_ids\":[";
                for (size_t i = 0; i < pc.token_ids.size(); ++i) {
                    evidence << pc.token_ids[i] << (i + 1 < pc.token_ids.size() ? "," : "");
                }
                evidence << "],\"compared_position\":" << position
                        << ",\"f32_artifact_sha256\":\"" << kF32ExpectedSha256 << "\""
                        << ",\"q8_artifact_sha256\":\"" << kQ8ExpectedSha256 << "\""
                        << ",\"f32_selected\":" << f32_result.selected_token.back()
                        << ",\"q8_selected\":" << q8_result.selected_token.back()
                        << ",\"max_abs_error\":" << precise(s.max_abs_error)
                        << ",\"max_relative_error\":" << precise(s.max_relative_error)
                        << ",\"rmse\":" << precise(s.rmse)
                        << ",\"top1_agree\":" << (s.top1_agree ? "true" : "false")
                        << ",\"top5_overlap\":" << s.top5_overlap
                        << ",\"q8_full_vocab_logsumexp\":" << precise(q8_logsumexp)
                        << ",\"q8_top5_ids\":[";
                for (size_t i = 0; i < q8_top5.ids.size(); ++i) {
                    evidence << q8_top5.ids[i] << (i + 1 < q8_top5.ids.size() ? "," : "");
                }
                evidence << "],\"q8_top5_logits\":[";
                for (size_t i = 0; i < q8_top5.logits.size(); ++i) {
                    evidence << precise(q8_top5.logits[i]) << (i + 1 < q8_top5.logits.size() ? "," : "");
                }
                evidence << "],\"f32_top5_ids\":[";
                for (size_t i = 0; i < f32_top5.ids.size(); ++i) {
                    evidence << f32_top5.ids[i] << (i + 1 < f32_top5.ids.size() ? "," : "");
                }
                evidence << "],\"f32_top5_logits\":[";
                for (size_t i = 0; i < f32_top5.logits.size(); ++i) {
                    evidence << precise(f32_top5.logits[i]) << (i + 1 < f32_top5.logits.size() ? "," : "");
                }
                evidence << "]}\n";
                if (!evidence.good()) throw std::runtime_error(pc.id + ": evidence write failed");
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
        // proving this project's existing committed-cache-LENGTH invariant
        // (advances by exactly one per step) also holds with real Q8_0
        // weights across several real steps -- not previously exercised
        // (every prior cached-decode test used F32). This specifically does
        // NOT independently verify cache CONTENT correctness at each step; see
        // the file-header comment for the narrowed claim. ---
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
            check(all_steps_ok, "5-step cached decode through Q8_0 weights: committed cache LENGTH advances "
                                "correctly at every step (same invariant this project already proves for F32; "
                                "this does not independently verify cache content correctness)");
            std::printf("  final F32 continuation last token=%lld, Q8_0 continuation last token=%lld\n",
                        (long long)f32_next[0], (long long)q8_next[0]);
        }

        evidence.close();
        if (evidence.fail()) throw std::runtime_error("evidence file failed to close cleanly");

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
