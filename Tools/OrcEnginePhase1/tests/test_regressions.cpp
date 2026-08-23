// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <cmath>
#include <cstdio>
#include <functional>
#include <limits>
#include <string>
#include <vector>

#include "decode_test_support.hpp"
#include "orcengine/context.hpp"
#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/materialization.hpp"
#include "orcengine/ops.hpp"
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

void expect_clean_failure(const std::string& name, const std::function<void()>& action) {
    try {
        action();
        fail(name, "operation unexpectedly accepted the mutation");
    } catch (const std::exception&) {
        pass(name);
    }
}

// Stronger than expect_clean_failure: requires the SPECIFIC exception type
// this project's dimension contract promises (std::invalid_argument), and
// that the message contains the expected diagnostic substring -- so a
// hostile case can't silently "pass" via an unrelated std::length_error
// (bad_alloc from a garbage element count), std::overflow_error, or any
// other std::exception that happens to be thrown for the wrong reason.
void expect_invalid_argument_containing(const std::string& name, const std::function<void()>& action,
                                        const std::string& expected_substring) {
    try {
        action();
        fail(name, "operation unexpectedly accepted the mutation");
    } catch (const std::invalid_argument& ex) {
        const std::string what = ex.what();
        if (what.find(expected_substring) != std::string::npos) {
            pass(name);
        } else {
            fail(name, "invalid_argument thrown but message did not contain '" + expected_substring +
                       "': " + what);
        }
    } catch (const std::exception& ex) {
        fail(name, std::string("wrong exception type (not std::invalid_argument): ") + ex.what());
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
        expect_clean_failure("direct group_size rejects invalid GQA metadata", [&] {
            ModelConfig config = tied.model.config();
            config.n_kv_heads = 0;
            (void)config.group_size();
        });
        expect_clean_failure("KV-store dimension product overflow", [&] {
            (void)ContiguousAttentionKVStore(
                std::numeric_limits<int64_t>::max(), 2, 2, 2);
        });
        expect_validation_failure("backing extent size mismatch", [&] {
            const LogicalTensor logical("bad_backing", TensorShape({2, 2}));
            const BackingExtent backing = BackingExtent::FromF32({1.0f, 2.0f, 3.0f});
            (void)materialize(logical, backing);
        });
        expect_validation_failure("unsupported backing encoding", [&] {
            const LogicalTensor logical("text_backing", TensorShape({1}));
            const BackingExtent backing("fixture.txt", 0, sizeof(float), BackingEncoding::F32Text);
            (void)materialize(logical, backing);
        });

        // Hostile-input coverage for ops::rmsnorm_into/linear_no_bias_into's
        // dimension contract (Phase 5C independent-review follow-up,
        // DECISION_LOG.md OE-ADR-041/OE-ADR-042): negative and non-positive
        // dimensions must be rejected before any multiplication runs, not
        // merely produce a wrong answer or undefined behavior.
        {
            const int64_t kMin = std::numeric_limits<int64_t>::min();
            std::vector<float> buf4(4, 0.0f);

            // --- _into forms: check_positive_dim() is the first thing that
            // runs, so these exercise THAT contract specifically. Asserted
            // against the exact expected diagnostic, not just "some
            // exception," so a case can't silently pass via an unrelated
            // span-size mismatch or overflow_error thrown for the wrong
            // reason. ---
            expect_invalid_argument_containing("rmsnorm_into rejects negative rows", [&] {
                ops::rmsnorm_into(buf4, -1, 4, buf4, 1e-5f, buf4);
            }, "rmsnorm rows");
            expect_invalid_argument_containing("rmsnorm_into rejects zero rows", [&] {
                std::vector<float> empty;
                ops::rmsnorm_into(empty, 0, 4, buf4, 1e-5f, empty);
            }, "rmsnorm rows");
            expect_invalid_argument_containing("rmsnorm_into rejects negative cols", [&] {
                ops::rmsnorm_into(buf4, 1, -4, buf4, 1e-5f, buf4);
            }, "rmsnorm cols");
            expect_invalid_argument_containing("rmsnorm_into rejects zero cols (would divide 0/0)", [&] {
                std::vector<float> empty;
                ops::rmsnorm_into(empty, 1, 0, empty, 1e-5f, empty);
            }, "rmsnorm cols");
            expect_invalid_argument_containing("rmsnorm_into rejects INT64_MIN rows", [&] {
                ops::rmsnorm_into(buf4, kMin, 4, buf4, 1e-5f, buf4);
            }, "rmsnorm rows");
            expect_invalid_argument_containing("rmsnorm_into rejects INT64_MIN cols", [&] {
                ops::rmsnorm_into(buf4, 1, kMin, buf4, 1e-5f, buf4);
            }, "rmsnorm cols");
            expect_invalid_argument_containing("linear_no_bias_into rejects negative rows", [&] {
                ops::linear_no_bias_into(buf4, -1, 4, buf4, 1, buf4);
            }, "linear_no_bias rows");
            expect_invalid_argument_containing("linear_no_bias_into rejects zero rows", [&] {
                std::vector<float> empty;
                ops::linear_no_bias_into(empty, 0, 4, buf4, 1, empty);
            }, "linear_no_bias rows");
            expect_invalid_argument_containing("linear_no_bias_into rejects negative in_features", [&] {
                ops::linear_no_bias_into(buf4, 1, -4, buf4, 1, buf4);
            }, "linear_no_bias in_features");
            expect_invalid_argument_containing("linear_no_bias_into rejects zero in_features", [&] {
                std::vector<float> empty;
                ops::linear_no_bias_into(buf4, 1, 0, empty, 1, buf4);
            }, "linear_no_bias in_features");
            expect_invalid_argument_containing("linear_no_bias_into rejects negative out_features", [&] {
                ops::linear_no_bias_into(buf4, 1, 4, buf4, -1, buf4);
            }, "linear_no_bias out_features");
            expect_invalid_argument_containing("linear_no_bias_into rejects zero out_features", [&] {
                std::vector<float> empty;
                ops::linear_no_bias_into(buf4, 1, 4, buf4, 0, empty);
            }, "linear_no_bias out_features");
            expect_invalid_argument_containing("linear_no_bias_into rejects INT64_MIN in_features", [&] {
                ops::linear_no_bias_into(buf4, 1, kMin, buf4, 1, buf4);
            }, "linear_no_bias in_features");
            expect_invalid_argument_containing("linear_no_bias_into rejects INT64_MIN out_features", [&] {
                ops::linear_no_bias_into(buf4, 1, 4, buf4, kMin, buf4);
            }, "linear_no_bias out_features");

            // --- Return-by-value wrappers: rmsnorm()/linear_no_bias() call
            // checked_mul_i64() DIRECTLY (to size their output buffer)
            // BEFORE ever calling into rmsnorm_into()/linear_no_bias_into(),
            // so check_positive_dim() has not run yet at that point -- this
            // is the ONLY place checked_mul_i64()'s own a<0||b<0 guard is
            // reached first. A genuine discriminating pair must defeat the
            // OLD bound (`b > INT64_MAX/a`, checked only for a != 0):
            //
            //   a=-1, b=INT64_MIN: OLD bound computes MAX/a = MAX/-1 = -MAX,
            //   then checks b > -MAX, i.e. INT64_MIN > -MAX. Since
            //   INT64_MIN == -(MAX)-1, this is INT64_MIN > -MAX, which is
            //   FALSE -- the old code would NOT throw here and would fall
            //   through to `a * b`, negating INT64_MIN, which is signed
            //   overflow (undefined behavior; on this project's target
            //   two's-complement platforms it silently wraps back to
            //   INT64_MIN, an element count nowhere close to correct, that
            //   would then reach `static_cast<size_t>(...)` and either
            //   attempt a garbage-sized allocation or produce some other
            //   confusing failure -- never a clean, attributable
            //   std::invalid_argument). The new code's explicit `a<0||b<0`
            //   check rejects it immediately, before any arithmetic.
            //
            // (The PREVIOUS version of this test used rows=-2 paired with
            // out_features=INT64_MAX/2 through the _into forms and claimed
            // this proved the same gap -- that was wrong: hand-tracing the
            // OLD bound for that pair shows `b > INT64_MAX/a` was ALREADY
            // true there (a large positive b compared against a negative
            // quotient), so the old code already threw for it. That case
            // never discriminated old from new behavior. See
            // DECISION_LOG.md OE-ADR-042 for the correction record.)
            expect_invalid_argument_containing(
                "rmsnorm() wrapper: checked_mul_i64 rejects (rows=-1, cols=INT64_MIN) before check_positive_dim runs",
                [&] { (void)ops::rmsnorm(buf4, -1, kMin, buf4, 1e-5f); },
                "received a negative dimension");
            expect_invalid_argument_containing(
                "linear_no_bias() wrapper: checked_mul_i64 rejects (rows=-1, out_features=INT64_MIN) before "
                "check_positive_dim runs",
                [&] { (void)ops::linear_no_bias(buf4, -1, 4, buf4, kMin); },
                "received a negative dimension");
        }

        // Direct ops::embedding_lookup hostile-input coverage (Phase 5C
        // independent-review follow-up, Gemini/PR#103 finding 1.2): before
        // this fix there was NO validation at all -- an out-of-range token
        // ID indexed table[token*hidden+h] straight past the end of the
        // backing vector, an out-of-bounds heap read.
        {
            // vocab=4, hidden=3; row r's values are 100*r, 100*r+1, 100*r+2
            // so a correctly-returned row can be verified by VALUE, not
            // merely by output size.
            std::vector<float> table;
            for (int64_t r = 0; r < 4; ++r) {
                for (int64_t h = 0; h < 3; ++h) {
                    table.push_back(static_cast<float>(100 * r + h));
                }
            }
            expect_invalid_argument_containing("embedding_lookup rejects negative token ID", [&] {
                (void)ops::embedding_lookup(table, 3, {-1});
            }, "token ID -1 is outside [0, 4)");
            expect_invalid_argument_containing("embedding_lookup rejects token ID == vocab (off-by-one)", [&] {
                (void)ops::embedding_lookup(table, 3, {4});
            }, "token ID 4 is outside [0, 4)");
            expect_invalid_argument_containing("embedding_lookup rejects token ID far past vocab", [&] {
                (void)ops::embedding_lookup(table, 3, {1000000000});
            }, "is outside [0, 4)");
            expect_invalid_argument_containing("embedding_lookup rejects zero hidden", [&] {
                (void)ops::embedding_lookup(table, 0, {0});
            }, "hidden must be positive");
            expect_invalid_argument_containing("embedding_lookup rejects negative hidden", [&] {
                (void)ops::embedding_lookup(table, -3, {0});
            }, "hidden must be positive");
            expect_invalid_argument_containing("embedding_lookup rejects table.size() not a multiple of hidden", [&] {
                std::vector<float> odd_table(10, 0.0f);  // 10 is not a multiple of 3
                (void)ops::embedding_lookup(odd_table, 3, {0});
            }, "table.size() is not a multiple of hidden");

            // Valid boundary tokens: 0 and vocab-1 (3) must still work, and
            // must return the ACTUAL correct row values, not merely an
            // output vector of the right size (a size-only check would not
            // catch an off-by-one in the row-start offset arithmetic).
            const std::string name0 = "embedding_lookup: valid boundary token 0 returns correct row values";
            try {
                std::vector<float> out = ops::embedding_lookup(table, 3, {0});
                if (out.size() == 3 && out[0] == 0.0f && out[1] == 1.0f && out[2] == 2.0f) {
                    pass(name0);
                } else {
                    fail(name0, "unexpected output values or size");
                }
            } catch (const std::exception& ex) {
                fail(name0, std::string("unexpectedly rejected valid input: ") + ex.what());
            }
            const std::string name1 = "embedding_lookup: valid boundary token vocab-1 (3) returns correct row values";
            try {
                std::vector<float> out = ops::embedding_lookup(table, 3, {3});
                if (out.size() == 3 && out[0] == 300.0f && out[1] == 301.0f && out[2] == 302.0f) {
                    pass(name1);
                } else {
                    fail(name1, "unexpected output values or size");
                }
            } catch (const std::exception& ex) {
                fail(name1, std::string("unexpectedly rejected valid input: ") + ex.what());
            }
        }

        if (failures == 0) {
            std::printf("ALL HARDENING REGRESSIONS PASSED\n");
            return 0;
        }
        std::printf("%d HARDENING REGRESSION FAILURES\n", failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] regression setup: %s\n", ex.what());
        return 1;
    }
}
