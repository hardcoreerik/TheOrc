// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <cmath>
#include <cstdio>
#include <functional>
#include <limits>
#include <string>
#include <vector>

#include "decode_test_support.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/validation.hpp"

using namespace orcengine;

namespace {

int failures = 0;

void pass(const std::string& name) {
    std::printf("[PASS] %s\n", name.c_str());
}

void fail(const std::string& name, const std::string& detail) {
    std::printf("[FAIL] %s: %s\n", name.c_str(), detail.c_str());
    ++failures;
}

void expect_validation_failure(const std::string& name, const std::function<void()>& action) {
    try {
        action();
        fail(name, "validation unexpectedly accepted the mutation");
    } catch (const ValidationError&) {
        pass(name);
    } catch (const std::exception& ex) {
        fail(name, std::string("wrong exception type: ") + ex.what());
    }
}

}  // namespace

int main(int argc, char** argv) {
    try {
        const std::string fixtures_dir = argc > 1 ? argv[1] : "fixtures_phase1";
        const LoadedFixture tied = load_fixture(fixtures_dir + "/fixture_tied.txt");
        const LoadedFixture untied = load_fixture(fixtures_dir + "/fixture_untied.txt");
        const LoadedFixture decode_fixture = load_fixture(fixtures_dir + "/fixture_decode_weights.txt");
        const auto trace = decode_test_support::load_decode_trace(
            fixtures_dir + "/fixture_decode_trace_python.txt");

        validate_forward_expectations(tied.model.config(), static_cast<int64_t>(tied.token_ids.size()), tied.expected);
        validate_forward_expectations(untied.model.config(), static_cast<int64_t>(untied.token_ids.size()), untied.expected);
        decode_test_support::validate_decode_trace(
            trace, decode_fixture.token_ids, decode_fixture.model.config().vocab,
            decode_fixture.model.config().max_positions);
        pass("pristine fixtures validate");

        expect_validation_failure("missing final logits expectation", [&] {
            auto expected = tied.expected;
            expected.erase("logits");
            validate_forward_expectations(tied.model.config(), static_cast<int64_t>(tied.token_ids.size()), expected);
        });
        expect_validation_failure("missing selected token expectation", [&] {
            auto expected = tied.expected;
            expected.erase("selected_token");
            validate_forward_expectations(tied.model.config(), static_cast<int64_t>(tied.token_ids.size()), expected);
        });
        expect_validation_failure("expected NaN", [&] {
            auto expected = tied.expected;
            expected.at("input_embedding").data[0] = std::numeric_limits<float>::quiet_NaN();
            validate_forward_expectations(tied.model.config(), static_cast<int64_t>(tied.token_ids.size()), expected);
        });
        expect_validation_failure("expected Inf", [&] {
            auto expected = tied.expected;
            expected.at("input_embedding").data[0] = std::numeric_limits<float>::infinity();
            validate_forward_expectations(tied.model.config(), static_cast<int64_t>(tied.token_ids.size()), expected);
        });

        {
            auto mutated_trace = trace;
            mutated_trace[0].logits_last[0] += 1000.0f;
            const ForwardResult result = forward(decode_fixture.model, decode_fixture.token_ids);
            const int64_t vocab = decode_fixture.model.config().vocab;
            const std::vector<float> logits_last(result.logits.end() - vocab, result.logits.end());
            const auto comparison = decode_test_support::compare_decode_step(
                logits_last, result.selected_token.back(), mutated_trace[0]);
            if (!comparison.pass() && !comparison.logits_match && comparison.token_matches) {
                pass("decode logit mutation with unchanged argmax");
            } else {
                fail("decode logit mutation with unchanged argmax", "numerical gate did not reject it");
            }
        }
        expect_validation_failure("decode STEPS=0", [&] {
            std::vector<decode_test_support::DecodeStep> no_steps;
            decode_test_support::validate_decode_trace(
                no_steps, decode_fixture.token_ids, decode_fixture.model.config().vocab,
                decode_fixture.model.config().max_positions);
        });

        expect_validation_failure("malformed final_norm shape [1,16]", [&] {
            Model model = tied.model;
            model.final_norm_weight = ResidentView(
                TensorShape({1, model.config().hidden}), model.final_norm_weight.raw());
            (void)forward(model, tied.token_ids);
        });
        expect_validation_failure("max_positions smaller than input", [&] {
            Model model = tied.model;
            model.manifest.config.max_positions = 1;
            (void)forward(model, tied.token_ids);
        });
        expect_validation_failure("n_kv_heads=0", [&] {
            Model model = tied.model;
            model.manifest.config.n_kv_heads = 0;
            (void)forward(model, tied.token_ids);
        });
        expect_validation_failure("zero hidden dimension", [&] {
            Model model = tied.model;
            model.manifest.config.hidden = 0;
            (void)forward(model, tied.token_ids);
        });
        expect_validation_failure("invalid token ID", [&] {
            auto tokens = tied.token_ids;
            tokens[0] = tied.model.config().vocab;
            (void)forward(tied.model, tokens);
        });
        expect_validation_failure("missing required layer tensor", [&] {
            Model model = tied.model;
            model.layers[0].w_q = ResidentView();
            (void)forward(model, tied.token_ids);
        });
        expect_validation_failure("untied model missing lm_head", [&] {
            Model model = untied.model;
            model.lm_head.reset();
            (void)forward(model, untied.token_ids);
        });
        expect_validation_failure("tied duplicate with different values", [&] {
            Model model = tied.model;
            std::vector<float> values = model.token_embedding.raw();
            values[0] += 1.0f;
            model.lm_head = ResidentView(model.token_embedding.shape(), std::move(values));
            (void)forward(model, tied.token_ids);
        });

        if (failures == 0) {
            std::printf("ALL 14 HARDENING REGRESSIONS PASSED\n");
            return 0;
        }
        std::printf("%d HARDENING REGRESSION FAILURES\n", failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] regression setup: %s\n", ex.what());
        return 1;
    }
}
