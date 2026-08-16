// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Differential test harness: runs the C++ forward pass against a fixture
// exported from the Python oracle and compares every intermediate tap
// (not just final logits), reporting layer/tensor/max_abs_error/
// max_rel_error/first_bad_index on any divergence -- per the Phase-1
// steering document's explicit acceptance-reporting requirement.
//
// Gate order (informal, within one executable for Phase 1): tensor/shape
// sanity -> embedding -> per-layer attention/FFN taps in forward order ->
// final norm+lm_head -> full logits -> tied fixture -> untied fixture ->
// greedy argmax agreement. See docs/OrcEngine/PHASE1_IMPLEMENTATION.md for
// the acceptance thresholds used below and their rationale.
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"

using namespace orcengine;

namespace {

constexpr float kAbsTol = 1e-3f;
constexpr float kRelTol = 1e-3f;

int g_failures = 0;

bool dims_equal(const std::vector<int64_t>& a, const std::vector<int64_t>& b) {
    return a == b;
}

std::string dims_to_string(const std::vector<int64_t>& d) {
    std::string s = "[";
    for (size_t i = 0; i < d.size(); ++i) {
        if (i) s += ", ";
        s += std::to_string(d[i]);
    }
    return s + "]";
}

// Compares a computed tap against the expected tap, printing a detailed
// divergence report (layer/tensor name is `label`) if it fails.
bool compare_tap(const std::string& label, const ActivationBuffer& actual, const ActivationBuffer& expected) {
    if (!dims_equal(actual.dims, expected.dims)) {
        std::printf("[FAIL] %s: shape mismatch actual=%s expected=%s\n", label.c_str(),
                    dims_to_string(actual.dims).c_str(), dims_to_string(expected.dims).c_str());
        ++g_failures;
        return false;
    }
    if (actual.data.size() != expected.data.size()) {
        std::printf("[FAIL] %s: element count mismatch actual=%zu expected=%zu\n", label.c_str(),
                    actual.data.size(), expected.data.size());
        ++g_failures;
        return false;
    }
    float max_abs_err = 0.0f, max_rel_err = 0.0f;
    size_t first_bad_index = static_cast<size_t>(-1);
    for (size_t i = 0; i < actual.data.size(); ++i) {
        float a = actual.data[i], e = expected.data[i];
        float abs_err = std::fabs(a - e);
        float rel_err = abs_err / std::max(1.0f, std::fabs(e));
        bool bad = abs_err > kAbsTol && rel_err > kRelTol;
        if (bad && first_bad_index == static_cast<size_t>(-1)) first_bad_index = i;
        max_abs_err = std::max(max_abs_err, abs_err);
        max_rel_err = std::max(max_rel_err, rel_err);
    }
    if (first_bad_index != static_cast<size_t>(-1)) {
        std::printf("[FAIL] %s: max_abs_error=%.6g max_rel_error=%.6g first_bad_index=%zu "
                    "(actual=%.6g expected=%.6g)\n",
                    label.c_str(), max_abs_err, max_rel_err, first_bad_index,
                    actual.data[first_bad_index], expected.data[first_bad_index]);
        ++g_failures;
        return false;
    }
    std::printf("[PASS] %s: max_abs_error=%.6g max_rel_error=%.6g (%zu elements)\n",
                label.c_str(), max_abs_err, max_rel_err, actual.data.size());
    return true;
}

void run_fixture_gate(const std::string& fixture_path, const std::string& tag) {
    std::printf("\n=== Gate: %s (%s) ===\n", tag.c_str(), fixture_path.c_str());
    LoadedFixture fx = load_fixture(fixture_path);
    ForwardResult result = forward(fx.model, fx.token_ids);

    // Every tap the Python oracle captured must be present and match.
    for (const auto& [name, expected_tap] : fx.expected) {
        if (name == "logits" || name == "selected_token") continue;  // checked separately below
        auto it = result.taps.find(name);
        if (it == result.taps.end()) {
            std::printf("[FAIL] %s: tap missing from C++ forward result\n", name.c_str());
            ++g_failures;
            continue;
        }
        compare_tap(tag + "::" + name, it->second, expected_tap);
    }

    // Final logits (full precision comparison).
    auto logits_it = fx.expected.find("logits");
    if (logits_it != fx.expected.end()) {
        ActivationBuffer actual_logits{{static_cast<int64_t>(fx.token_ids.size()), fx.model.config().vocab}, result.logits};
        compare_tap(tag + "::logits", actual_logits, logits_it->second);
    }

    // Greedy argmax agreement -- exact integer match required, no tolerance.
    auto selected_it = fx.expected.find("selected_token");
    if (selected_it != fx.expected.end()) {
        bool all_match = true;
        for (size_t i = 0; i < result.selected_token.size(); ++i) {
            int64_t actual = result.selected_token[i];
            int64_t expected = static_cast<int64_t>(selected_it->second.data[i]);
            if (actual != expected) {
                std::printf("[FAIL] %s::selected_token[%zu]: actual=%lld expected=%lld\n",
                            tag.c_str(), i, static_cast<long long>(actual), static_cast<long long>(expected));
                all_match = false;
                ++g_failures;
            }
        }
        if (all_match) {
            std::printf("[PASS] %s::selected_token: exact match on all %zu positions\n",
                        tag.c_str(), result.selected_token.size());
        }
    }
}

}  // namespace

int main(int argc, char** argv) {
    std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
    run_fixture_gate(fixtures_dir + "/fixture_tied.txt", "tied");
    run_fixture_gate(fixtures_dir + "/fixture_untied.txt", "untied");

    std::printf("\n=== Summary ===\n");
    if (g_failures == 0) {
        std::printf("ALL GATES PASSED (tolerance: abs=%.1e rel=%.1e)\n", kAbsTol, kRelTol);
        return 0;
    }
    std::printf("%d FAILURES\n", g_failures);
    return 1;
}
