// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B decode test (A1): TokenizerProfile::decode()/decode_token_bytes().
// Reuses the SAME oracle-verified encode_oracle_fixtures.hpp corpus Stage
// 2B's own test_encode.cpp uses (tokenizers==0.22.2, pinned tokenizer.json)
// -- decode is the closed-form mathematical inverse of encode's byte-
// alphabet mapping plus direct CONTROL-token lookup, so round-tripping
// every already oracle-verified `text -> ids` fixture back through
// `ids -> decode() -> bytes` and requiring an EXACT match to the original
// UTF-8 bytes is itself an oracle-anchored proof, not merely a self-
// consistency check against our own encode() output in isolation. No new
// generator machinery or pinned artifact was needed for this.
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "orcengine/tokenizer.hpp"
#include "encode_oracle_fixtures.hpp"

using namespace orcengine;

namespace {
int g_failures = 0;
void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}
}  // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::fprintf(stderr, "usage: %s <canonical-explicit-gguf-path>\n", argv[0]);
        return 2;
    }

    try {
        const TokenizerProfile profile = load_tokenizer_profile(argv[1]);
        check(profile.vocab_size() == 49152, "profile loads with vocab_size == 49152");

        // --- Oracle-anchored round trip: decode(encode(text)) == text, for
        // EVERY fixture in the encode corpus (ASCII, punctuation,
        // whitespace, non-ASCII Latin, CJK, emoji, embedded NUL, combining
        // marks, multibyte UTF-8 boundary, mixed CONTROL/ordinary text, and
        // the exhaustive 17x17 CONTROL-token adjacency matrix). ------------
        std::printf("=== Decode round-trip against every oracle-verified encode fixture ===\n");
        std::size_t exact_pass = 0;
        for (std::size_t i = 0; i < pretok_fixtures::kEncodeFixtureCount; ++i) {
            const auto& fx = pretok_fixtures::kEncodeFixtures[i];
            const std::vector<int64_t> ids(fx.ids, fx.ids + fx.id_count);
            std::string decoded;
            bool threw = false;
            try {
                decoded = profile.decode(ids, DecodeControlPolicy::PreserveControlTokens);
            } catch (const std::exception& ex) {
                threw = true;
                check(false, std::string("fixture '") + fx.id + "' -- decode unexpectedly threw: " + ex.what());
            }
            if (threw) continue;
            const bool matches = decoded.size() == fx.utf8.size() &&
                                 std::memcmp(decoded.data(), fx.utf8.data(), decoded.size()) == 0;
            check(matches, std::string("fixture '") + fx.id + "' -- decode(encode(text), Preserve) == text exactly");
            if (matches) ++exact_pass;

            // Determinism.
            const std::string decoded2 = profile.decode(ids, DecodeControlPolicy::PreserveControlTokens);
            check(decoded == decoded2, std::string("fixture '") + fx.id + "' -- deterministic repeat decode");
        }
        std::printf("[INFO] decode round-trip: %zu/%zu exact-match\n", exact_pass,
                    pretok_fixtures::kEncodeFixtureCount);

        // --- Raw-prompt identity fixture: "Hello, world!" -> [19556, 28,
        // 905, 17] (Tools/OrcEnginePhase0/artifacts/raw_prompt_identity_
        // manifest.json, confirmed by direct read; reused verbatim as the
        // canonical fixture for Section 11's frozen-engine integration
        // proof too). -------------------------------------------------
        {
            const std::vector<int64_t> hello_ids = {19556, 28, 905, 17};
            const std::string decoded = profile.decode(hello_ids);
            check(decoded == "Hello, world!", "raw-prompt identity: decode([19556,28,905,17]) == \"Hello, world!\"");
        }

        // --- CONTROL-token preserve/skip divergence against the pinned
        // golden fixture 'text_resembling_special_tokens'
        // (tokenizer_golden_fixtures.json, confirmed by direct read):
        // raw text "this <|endoftext|> looks like a special token",
        // ids [8232, 216, 0, 5117, 702, 253, 1767, 9624] (id 0 is the
        // CONTROL token <|endoftext|>). Preserve round-trips exactly;
        // Skip reproduces the oracle's own documented default-decode
        // output "this  looks like a special token" (double space, since
        // dropping the CONTROL token leaves the space before and after it
        // both in place -- exactly what the oracle recorded, confirmed by
        // direct read of decoded_text_repr in the committed fixture). ---
        {
            const std::vector<int64_t> ids = {8232, 216, 0, 5117, 702, 253, 1767, 9624};
            const std::string preserved = profile.decode(ids, DecodeControlPolicy::PreserveControlTokens);
            check(preserved == "this <|endoftext|> looks like a special token",
                  "golden fixture 'text_resembling_special_tokens': Preserve round-trips exactly");
            const std::string skipped = profile.decode(ids, DecodeControlPolicy::SkipControlTokens);
            check(skipped == "this  looks like a special token",
                  "golden fixture 'text_resembling_special_tokens': Skip matches the oracle's own documented "
                  "default-decode output exactly (double space where the CONTROL token was dropped)");
            check(preserved != skipped,
                  "golden fixture 'text_resembling_special_tokens': Preserve and Skip genuinely diverge "
                  "(not a vacuous pass)");
        }

        // --- All 17 CONTROL tokens: preserved individually/together, and
        // skipped individually/together. Derived from token_types()
        // directly, not a hard-coded 17 in this test -- though this
        // pinned profile's Stage 1 construction already guarantees
        // exactly 17. -----------------------------------------------
        {
            std::vector<int64_t> control_ids;
            for (size_t i = 0; i < profile.token_types().size(); ++i) {
                if (profile.token_types()[i] == TokenizerTokenType::Control) {
                    control_ids.push_back(static_cast<int64_t>(i));
                }
            }
            check(control_ids.size() == 17, "profile has exactly 17 CONTROL tokens (Stage 1 invariant)");

            std::string expected_preserved;
            for (int64_t id : control_ids) expected_preserved += profile.tokens()[static_cast<size_t>(id)];
            const std::string preserved_all = profile.decode(control_ids, DecodeControlPolicy::PreserveControlTokens);
            check(preserved_all == expected_preserved,
                  "all 17 CONTROL tokens decoded together with Preserve == concatenation of their literal spellings");
            const std::string skipped_all = profile.decode(control_ids, DecodeControlPolicy::SkipControlTokens);
            check(skipped_all.empty(), "all 17 CONTROL tokens decoded together with Skip == empty string");

            for (int64_t id : control_ids) {
                const std::vector<int64_t> single = {id};
                check(profile.decode(single, DecodeControlPolicy::PreserveControlTokens) ==
                          profile.tokens()[static_cast<size_t>(id)],
                      "CONTROL token id " + std::to_string(id) + " preserved individually matches its spelling");
                check(profile.decode(single, DecodeControlPolicy::SkipControlTokens).empty(),
                      "CONTROL token id " + std::to_string(id) + " skipped individually decodes to empty");
            }
        }

        // --- Mixture of NORMAL and CONTROL tokens in one call (beyond the
        // golden-fixture case above): a NORMAL token, a CONTROL token, and
        // another NORMAL token, both policies. -------------------------
        {
            // 19556 == "Hello" (from the raw-prompt identity fixture above).
            const std::vector<int64_t> mixed = {19556, 0, 19556};  // "Hello" <|endoftext|> "Hello"
            const std::string preserved = profile.decode(mixed, DecodeControlPolicy::PreserveControlTokens);
            check(preserved == "Hello<|endoftext|>Hello", "mixed NORMAL/CONTROL/NORMAL: Preserve concatenates exactly");
            const std::string skipped = profile.decode(mixed, DecodeControlPolicy::SkipControlTokens);
            check(skipped == "HelloHello", "mixed NORMAL/CONTROL/NORMAL: Skip drops only the CONTROL token's bytes");
        }

        // --- Empty input. ------------------------------------------------
        {
            check(profile.decode({}).empty(), "empty token-ID vector decodes to empty string (Preserve default)");
            check(profile.decode({}, DecodeControlPolicy::SkipControlTokens).empty(),
                  "empty token-ID vector decodes to empty string (Skip)");
            check(DecodeControlPolicy{} == DecodeControlPolicy::PreserveControlTokens,
                  "PreserveControlTokens is the default enum value");
        }

        // --- Invalid IDs: negative, == vocab_size, > vocab_size -- each
        // rejected with DecodingError, and a failing call never returns a
        // partial result (decode() accumulates into a local buffer that is
        // destroyed, not returned, on any throw -- there is no output
        // parameter through which a partial result could otherwise leak). ---
        {
            const int64_t vocab = static_cast<int64_t>(profile.vocab_size());
            struct Case { int64_t id; const char* label; };
            const Case cases[] = {
                {-1, "negative ID (-1)"},
                {-1000, "negative ID (-1000)"},
                {vocab, "ID == vocab_size"},
                {vocab + 1, "ID == vocab_size + 1"},
                {vocab + 1000, "ID greater than vocab_size"},
            };
            for (const Case& c : cases) {
                bool threw = false;
                bool right_type = false;
                try {
                    std::string result = profile.decode({c.id});
                    (void)result;
                } catch (const DecodingError&) {
                    threw = true;
                    right_type = true;
                } catch (const std::exception&) {
                    threw = true;
                }
                check(threw, std::string(c.label) + ": decode() throws");
                check(right_type, std::string(c.label) + ": decode() throws exactly DecodingError");

                bool token_bytes_threw = false;
                try {
                    std::string r = profile.decode_token_bytes(c.id);
                    (void)r;
                } catch (const DecodingError&) {
                    token_bytes_threw = true;
                }
                check(token_bytes_threw, std::string(c.label) + ": decode_token_bytes() throws DecodingError");
            }

            // Failure without partial output: a multi-ID call where the
            // FIRST id is valid and the SECOND is invalid must throw --
            // decode() never returns the first token's bytes alone.
            const std::vector<int64_t> valid_then_invalid = {19556, vocab};  // "Hello" then out-of-range
            bool threw = false;
            std::string leaked;
            try {
                leaked = profile.decode(valid_then_invalid);
                leaked = "UNREACHABLE-NO-THROW:" + leaked;
            } catch (const DecodingError&) {
                threw = true;
            }
            check(threw, "valid ID followed by an invalid ID: decode() throws (no partial-success return)");
            check(leaked.empty(),
                  "valid ID followed by an invalid ID: the caller's result variable was never assigned "
                  "(no partial output observable)");
        }

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
