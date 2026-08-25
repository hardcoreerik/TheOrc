// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 6 Stage 1, Gate 4 (combined Codex/Grok remediation round 7):
// diagnostic-only check of whether OrcEngine's EXISTING, UNMODIFIED
// loader (index_gguf/map_llama_model/materialize_gguf_model) accepts
// the Gate 2/3 canonical and Q/K-isolated GGUF artifacts, and how it
// behaves numerically if it does. Does NOT modify the loader or the
// transformer math -- this is a read-only "what happens if we point
// the existing code at a differently-laid-out file" probe, not a fix.
//
// Unlike phase6_q8_0_comparison.cpp, this tool takes exactly ONE GGUF
// path (not a paired F32+Q8_0 comparison) and has NO hardcoded
// expected-file-size/hash gate -- that gate exists in the Checkpoint 3
// tool specifically to protect the pinned EXISTING fixtures used for
// committed evidence; it would incorrectly reject these new,
// differently-sized Gate 2/3 diagnostic artifacts, which are not
// pinned fixtures and are never committed.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <optional>
#include <string>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

struct PromptCase {
    std::string id;
    std::vector<int64_t> token_ids;
};

// Same fixed 7-prompt corpus as every other Gate 2/3 comparison this
// round (phase6_gate2_canonical_layout_check.py's PROMPTS, matching
// phase6_q8_0_f32_comparison_evidence_v3.jsonl).
const std::vector<PromptCase> kCorpus = {
    {"dev_capital_of_france", {504, 3575, 282, 4649, 314}},
    {"dev_once_upon_a_time", {6403, 1980, 253, 655}},
    {"dev_code_snippet", {1604, 803, 24, 81, 28, 278, 727, 1003}},
    {"dev_year_weather", {788, 216, 34, 32, 34, 38, 28, 260, 3947}},
    {"holdout_hello_world", {19556, 905, 28, 451, 314}},
    {"holdout_she_walked", {8113, 13197, 618, 260}},
    {"holdout_quick_fox", {504, 2365, 6354, 16438}},
};

struct TopK {
    std::vector<int64_t> ids;
    std::vector<float> logits;
};

TopK top_k(const std::vector<float>& logits, size_t k) {
    std::vector<int64_t> idx(logits.size());
    for (size_t i = 0; i < idx.size(); ++i) idx[i] = static_cast<int64_t>(i);
    std::partial_sort(idx.begin(), idx.begin() + static_cast<long>(std::min(k, idx.size())), idx.end(),
                      [&](int64_t a, int64_t b) { return logits[a] > logits[b]; });
    TopK out;
    for (size_t i = 0; i < std::min(k, idx.size()); ++i) {
        out.ids.push_back(idx[i]);
        out.logits.push_back(logits[idx[i]]);
    }
    return out;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: phase6_gate4_canonical_loader_diagnostic GGUF_PATH LABEL\n");
        return 2;
    }
    const std::filesystem::path gguf_path = argv[1];
    const std::string label = argv[2];

    std::error_code size_ec;
    const uintmax_t size = std::filesystem::file_size(gguf_path, size_ec);
    if (size_ec) {
        std::printf("[%s] LOAD FAILED: cannot stat '%s': %s\n", label.c_str(),
                    gguf_path.string().c_str(), size_ec.message().c_str());
        return 1;
    }
    std::printf("[%s] file: %s (%llu bytes)\n", label.c_str(), gguf_path.string().c_str(),
               (unsigned long long)size);

    std::optional<Model> model_opt;
    try {
        model_opt = materialize_gguf_model(map_llama_model(index_gguf(gguf_path)));
    } catch (const std::exception& ex) {
        std::printf("[%s] LOAD FAILED (exception): %s\n", label.c_str(), ex.what());
        return 1;
    }
    const Model& model = *model_opt;
    const ModelConfig& cfg = model.config();
    std::printf("[%s] LOAD OK: vocab=%lld hidden=%lld n_layers=%lld n_q_heads=%lld n_kv_heads=%lld "
               "head_dim=%lld max_positions=%lld\n",
               label.c_str(), (long long)cfg.vocab, (long long)cfg.hidden, (long long)cfg.n_layers,
               (long long)cfg.n_q_heads, (long long)cfg.n_kv_heads, (long long)cfg.head_dim,
               (long long)cfg.max_positions);

    int failures = 0;
    for (const auto& pc : kCorpus) {
        ContiguousAttentionKVStore cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
        CachedStepResult result;
        try {
            result = forward_cached_step(model, cache, pc.token_ids, 0);
        } catch (const std::exception& ex) {
            std::printf("[%s] %-24s FORWARD FAILED (exception): %s\n", label.c_str(), pc.id.c_str(), ex.what());
            ++failures;
            continue;
        }
        const int64_t token_count = static_cast<int64_t>(pc.token_ids.size());
        const int64_t vocab = cfg.vocab;
        if (static_cast<int64_t>(result.logits.size()) != token_count * vocab) {
            std::printf("[%s] %-24s MALFORMED LOGITS: size=%zu expected=%lld\n", label.c_str(), pc.id.c_str(),
                       result.logits.size(), (long long)(token_count * vocab));
            ++failures;
            continue;
        }
        std::vector<float> last(result.logits.end() - vocab, result.logits.end());
        bool all_finite = true;
        for (float v : last) {
            if (!std::isfinite(v)) { all_finite = false; break; }
        }
        if (!all_finite) {
            std::printf("[%s] %-24s NON-FINITE LOGITS\n", label.c_str(), pc.id.c_str());
            ++failures;
            continue;
        }
        const TopK top5 = top_k(last, 5);
        std::printf("[%s] %-24s argmax=%lld top5=[", label.c_str(), pc.id.c_str(),
                   (long long)result.selected_token.back());
        for (size_t i = 0; i < top5.ids.size(); ++i) {
            std::printf("%s(%lld, %.6f)", i ? ", " : "", (long long)top5.ids[i], top5.logits[i]);
        }
        std::printf("]\n");
    }

    std::printf("[%s] %s: %d/%zu prompts failed\n", label.c_str(), failures == 0 ? "PASS" : "FAIL",
               failures, kCorpus.size());
    return failures == 0 ? 0 : 1;
}
