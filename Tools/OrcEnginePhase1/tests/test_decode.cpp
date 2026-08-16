// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase-1 freeze-audit item 1: true autoregressive greedy decoding, proven
// against an INDEPENDENTLY-computed Python trace (oracle/export_cpp_phase1_
// decode_fixture.py's run_python_decode -- it picks its own tokens, it is
// never told what C++ will choose). This C++ side does the same: full
// recompute each step (no KV cache -- Phase 1 has none), argmax of the
// LAST position's logits, append, repeat. If either side ever diverges,
// the token sequences would differ from that step forward, and this test
// catches it immediately rather than only checking one fixed input's
// per-position argmax (which the original Phase-1 differential harness
// technically did NOT rule out as a gap -- this test closes that gap).
#include <cmath>
#include <cstdio>
#include <fstream>
#include <string>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"

using namespace orcengine;

namespace {

struct DecodeStep {
    std::vector<int64_t> seq_before;
    std::vector<float> logits_last;
    int64_t selected = 0;
};

struct Reader {
    std::ifstream in;
    explicit Reader(const std::string& path) : in(path) {
        if (!in) throw std::runtime_error("cannot open " + path);
    }
    std::string tok() { std::string t; in >> t; return t; }
    int64_t i() { return std::stoll(tok()); }
    float f() { return std::stof(tok()); }
};

std::vector<DecodeStep> load_python_trace(const std::string& path) {
    Reader r(path);
    std::string tag = r.tok();
    if (tag != "STEPS") throw std::runtime_error("expected STEPS");
    int64_t n_steps = r.i();
    std::vector<DecodeStep> steps(static_cast<size_t>(n_steps));
    for (int64_t s = 0; s < n_steps; ++s) {
        tag = r.tok();
        if (tag != "STEP") throw std::runtime_error("expected STEP");
        r.i();  // step index, unused (implied by position)
        tag = r.tok();
        if (tag != "SEQ_BEFORE") throw std::runtime_error("expected SEQ_BEFORE");
        int64_t seq_len = r.i();
        steps[static_cast<size_t>(s)].seq_before.resize(static_cast<size_t>(seq_len));
        for (int64_t i = 0; i < seq_len; ++i) steps[static_cast<size_t>(s)].seq_before[static_cast<size_t>(i)] = r.i();
        tag = r.tok();
        if (tag != "LOGITS_LAST") throw std::runtime_error("expected LOGITS_LAST");
        int64_t vocab = r.i();
        steps[static_cast<size_t>(s)].logits_last.resize(static_cast<size_t>(vocab));
        for (int64_t i = 0; i < vocab; ++i) steps[static_cast<size_t>(s)].logits_last[static_cast<size_t>(i)] = r.f();
        tag = r.tok();
        if (tag != "SELECTED") throw std::runtime_error("expected SELECTED");
        steps[static_cast<size_t>(s)].selected = r.i();
    }
    return steps;
}

}  // namespace

int main(int argc, char** argv) {
    std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
    LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_decode_weights.txt");
    std::vector<DecodeStep> python_trace = load_python_trace(fixtures_dir + "/fixture_decode_trace_python.txt");

    std::printf("=== Autoregressive decode: C++ vs independently-computed Python trace ===\n");
    std::printf("Initial tokens:");
    for (int64_t t : fx.token_ids) std::printf(" %lld", static_cast<long long>(t));
    std::printf("\n\n");

    std::vector<int64_t> tokens = fx.token_ids;
    int failures = 0;
    std::vector<int64_t> full_sequence = tokens;

    for (size_t step = 0; step < python_trace.size(); ++step) {
        const DecodeStep& expected = python_trace[step];

        // Sanity: the sequence going INTO this step must match Python's --
        // if a prior step already diverged, this catches it explicitly
        // instead of silently comparing against the wrong prefix.
        bool seq_matches = (tokens == expected.seq_before);
        if (!seq_matches) {
            std::printf("[FAIL] step %zu: input sequence diverged from Python before this step "
                        "(prior step's token choice already disagreed) -- stopping comparison here\n", step);
            ++failures;
            break;
        }

        ForwardResult result = forward(fx.model, tokens);
        int64_t vocab = fx.model.config().vocab;
        std::vector<float> logits_last(result.logits.end() - vocab, result.logits.end());
        int64_t selected = result.selected_token.back();

        float max_abs = 0.0f, max_rel = 0.0f;
        for (int64_t v = 0; v < vocab; ++v) {
            float a = logits_last[static_cast<size_t>(v)], e = expected.logits_last[static_cast<size_t>(v)];
            float abs_err = std::fabs(a - e);
            max_abs = std::max(max_abs, abs_err);
            max_rel = std::max(max_rel, abs_err / std::max(1.0f, std::fabs(e)));
        }

        bool token_matches = (selected == expected.selected);
        std::printf("[step %zu] seq_before=[", step);
        for (size_t i = 0; i < tokens.size(); ++i) std::printf("%s%lld", i ? "," : "", static_cast<long long>(tokens[i]));
        std::printf("] cpp_selected=%lld python_selected=%lld max_logit_abs_err=%.6g %s\n",
                    static_cast<long long>(selected), static_cast<long long>(expected.selected),
                    max_abs, token_matches ? "[PASS]" : "[FAIL]");

        if (!token_matches) ++failures;
        tokens.push_back(selected);
        full_sequence.push_back(selected);
    }

    std::printf("\nC++ full generated sequence:   [");
    for (size_t i = 0; i < full_sequence.size(); ++i) std::printf("%s%lld", i ? "," : "", static_cast<long long>(full_sequence[i]));
    std::printf("]\n");

    std::printf("\n=== Summary ===\n");
    if (failures == 0) {
        std::printf("ALL %zu DECODE STEPS MATCHED (token-for-token, independently computed)\n", python_trace.size());
        return 0;
    }
    std::printf("%d STEP FAILURES\n", failures);
    return 1;
}
