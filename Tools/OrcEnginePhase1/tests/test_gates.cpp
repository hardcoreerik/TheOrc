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
#include "orcengine/validation.hpp"

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
        if (!std::isfinite(a) || !std::isfinite(e)) {
            std::printf("[FAIL] %s: non-finite value at index %zu (actual=%.6g expected=%.6g)\n",
                        label.c_str(), i, a, e);
            ++g_failures;
            return false;
        }
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

void run_fixture_gate(const LoadedFixture& fx, const std::string& fixture_path, const std::string& tag) {
    std::printf("\n=== Gate: %s (%s) ===\n", tag.c_str(), fixture_path.c_str());
    ForwardResult result = forward(fx.model, fx.token_ids);

    // Every structurally-required tap must be present and match.
    for (const ExpectedRequirement& req : required_forward_expectations(
             fx.model.config(), static_cast<int64_t>(fx.token_ids.size()))) {
        if (req.name == "logits" || req.name == "selected_token") continue;
        auto it = result.taps.find(req.name);
        if (it == result.taps.end()) {
            std::printf("[FAIL] %s: tap missing from C++ forward result\n", req.name.c_str());
            ++g_failures;
            continue;
        }
        compare_tap(tag + "::" + req.name, it->second, fx.expected.at(req.name));
    }

    // Final logits (full precision comparison).
    ActivationBuffer actual_logits{{static_cast<int64_t>(fx.token_ids.size()), fx.model.config().vocab}, result.logits};
    compare_tap(tag + "::logits", actual_logits, fx.expected.at("logits"));

    // Greedy argmax agreement -- exact integer match required, no tolerance.
    const ActivationBuffer& expected_selected = fx.expected.at("selected_token");
    bool all_match = true;
    for (size_t i = 0; i < result.selected_token.size(); ++i) {
        int64_t actual = result.selected_token[i];
        int64_t expected = static_cast<int64_t>(expected_selected.data[i]);
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

}  // namespace

int main(int argc, char** argv) {
    try {
        std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
        const std::string tied_path = fixtures_dir + "/fixture_tied.txt";
        const std::string untied_path = fixtures_dir + "/fixture_untied.txt";
        LoadedFixture tied = load_fixture(tied_path);
        LoadedFixture untied = load_fixture(untied_path);

        validate_forward_expectations(tied.model.config(), static_cast<int64_t>(tied.token_ids.size()), tied.expected);
        validate_forward_expectations(untied.model.config(), static_cast<int64_t>(untied.token_ids.size()), untied.expected);
        const size_t required = required_forward_expectations(
            tied.model.config(), static_cast<int64_t>(tied.token_ids.size())).size() +
            required_forward_expectations(
                untied.model.config(), static_cast<int64_t>(untied.token_ids.size())).size();
        const size_t loaded = tied.expected.size() + untied.expected.size();
        std::printf("Required expectations: %zu\nLoaded expectations:   %zu\nFixture completeness:  PASS\n",
                    required, loaded);

        run_fixture_gate(tied, tied_path, "tied");
        run_fixture_gate(untied, untied_path, "untied");
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }

    std::printf("\n=== Summary ===\n");
    if (g_failures == 0) {
        std::printf("ALL GATES PASSED (tolerance: abs=%.1e rel=%.1e)\n", kAbsTol, kRelTol);
        return 0;
    }
    std::printf("%d FAILURES\n", g_failures);
    return 1;
}
