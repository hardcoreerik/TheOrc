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
#include <string>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"
#include "decode_test_support.hpp"

using namespace orcengine;

int main(int argc, char** argv) {
    try {
    std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
    LoadedFixture fx = load_fixture(fixtures_dir + "/fixture_decode_weights.txt");
    std::vector<decode_test_support::DecodeStep> python_trace =
        decode_test_support::load_decode_trace(fixtures_dir + "/fixture_decode_trace_python.txt");
    decode_test_support::validate_decode_trace(
        python_trace, fx.token_ids, fx.model.config().vocab, fx.model.config().max_positions);

    std::printf("=== Autoregressive decode: C++ vs independently-computed Python trace ===\n");
    std::printf("Initial tokens:");
    for (int64_t t : fx.token_ids) std::printf(" %lld", static_cast<long long>(t));
    std::printf("\n\n");

    std::vector<int64_t> tokens = fx.token_ids;
    int failures = 0;
    std::vector<int64_t> full_sequence = tokens;

    for (size_t step = 0; step < python_trace.size(); ++step) {
        const decode_test_support::DecodeStep& expected = python_trace[step];

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

        const decode_test_support::DecodeComparison comparison =
            decode_test_support::compare_decode_step(logits_last, selected, expected);
        std::printf("[step %zu] seq_before=[", step);
        for (size_t i = 0; i < tokens.size(); ++i) std::printf("%s%lld", i ? "," : "", static_cast<long long>(tokens[i]));
        std::printf("] cpp_selected=%lld python_selected=%lld max_logit_abs_err=%.6g "
                    "max_logit_rel_err=%.6g %s\n",
                    static_cast<long long>(selected), static_cast<long long>(expected.selected),
                    comparison.max_abs, comparison.max_rel, comparison.pass() ? "[PASS]" : "[FAIL]");

        if (!comparison.pass()) ++failures;
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
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
