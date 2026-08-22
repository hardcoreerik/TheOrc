// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B streaming decode test (A2): Utf8StreamDecoder. Uses the pinned
// golden fixture 'multibyte_utf8_boundary' (Tools/OrcEnginePhase0/artifacts/
// tokenizer_golden_fixtures.json, confirmed by direct read) as the primary
// real-split-sequence proof: three consecutive 4-byte-UTF-8 emoji, each
// one's raw bytes distributed across exactly 3 token IDs (2 raw bytes from
// the first token, 1 each from the next two) -- a genuine cross-token
// UTF-8 boundary from the real oracle-verified corpus, not a synthetic
// contrivance. Expected per-triplet emission is cross-checked against
// TokenizerProfile::decode()'s already A1-proven one-shot result for that
// same ID sub-sequence, rather than hand-encoding UTF-8 byte literals here.
#include <cstdio>
#include <string>
#include <vector>

#include "orcengine/tokenizer.hpp"

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

        // 'multibyte_utf8_boundary': "\U0001F600\U0001F601\U0001F602 consecutive multibyte emoji"
        const std::vector<int64_t> ids = {
            10813, 242, 218,   // U+1F600, split 2+1+1 raw bytes across 3 tokens
            10813, 242, 219,   // U+1F601
            10813, 242, 220,   // U+1F602
            19996, 1575, 505, 105, 676, 649, 33777,  // " consecutive multibyte emoji"
        };

        std::printf("=== Streaming decode: real cross-token 4-byte UTF-8 boundary ===\n");
        {
            Utf8StreamDecoder stream(profile);
            std::string accumulated;
            const std::vector<std::pair<size_t, size_t>> triplets = {{0, 3}, {3, 3}, {6, 3}};
            for (const auto& [start, len] : triplets) {
                std::string emitted;
                for (size_t k = 0; k < len; ++k) {
                    const std::string chunk = stream.feed(ids[start + k]);
                    if (k + 1 < len) {
                        check(chunk.empty(), "token " + std::to_string(start + k) +
                              " of an incomplete 4-byte sequence emits nothing yet");
                    } else {
                        const std::vector<int64_t> triplet_ids(ids.begin() + static_cast<long>(start),
                                                                ids.begin() + static_cast<long>(start + len));
                        const std::string expected = profile.decode(triplet_ids);
                        check(chunk == expected,
                              "3rd token of the triplet at offset " + std::to_string(start) +
                              " emits the complete 4-byte emoji, matching one-shot decode() of the same IDs");
                    }
                    accumulated += chunk;
                }
            }
            // Remaining ASCII tokens: each is immediately complete on its own.
            for (size_t i = 9; i < ids.size(); ++i) {
                const std::string chunk = stream.feed(ids[i]);
                check(!chunk.empty(), "ASCII token " + std::to_string(i) + " emits immediately, non-empty");
                accumulated += chunk;
            }
            stream.finish();  // must not throw -- nothing left pending

            const std::string one_shot = profile.decode(ids);
            check(accumulated == one_shot,
                  "streaming-accumulated output across all tokens matches one-shot decode() exactly");
            check(accumulated == "\xF0\x9F\x98\x80\xF0\x9F\x98\x81\xF0\x9F\x98\x82 consecutive multibyte emoji",
                  "streaming output matches the fixture's raw text bytes exactly");
        }

        // --- Determinism: repeating the same feed sequence on a fresh
        // decoder produces the identical accumulated output. -------------
        {
            Utf8StreamDecoder stream(profile);
            std::string accumulated;
            for (int64_t id : ids) accumulated += stream.feed(id);
            stream.finish();
            check(accumulated == profile.decode(ids), "streaming decode is deterministic across a fresh decoder instance");
        }

        // --- Two-byte sequence split across a token boundary: use the raw-
        // prompt "Hello, world!" fixture's own tokens (19556="Hello",
        // 28=",", 905=" world", 17="!") -- all ASCII, each completes
        // immediately; included as the simplest possible streaming case to
        // anchor the boundary tests above against a plain baseline. -------
        {
            Utf8StreamDecoder stream(profile);
            std::string accumulated;
            for (int64_t id : {19556, 28, 905, 17}) {
                const std::string chunk = stream.feed(id);
                check(!chunk.empty(), "ASCII raw-prompt token emits immediately");
                accumulated += chunk;
            }
            stream.finish();
            check(accumulated == "Hello, world!", "streaming decode of the raw-prompt fixture == \"Hello, world!\"");
        }

        // --- Empty stream: finish() with no feed() calls does not throw. --
        {
            Utf8StreamDecoder stream(profile);
            bool threw = false;
            try {
                stream.finish();
            } catch (const std::exception&) {
                threw = true;
            }
            check(!threw, "finish() on a stream with zero feed() calls does not throw");
        }

        // --- Incomplete trailing sequence at end-of-stream: feed only the
        // FIRST TWO tokens of a 3-token emoji triplet, then finish() -- must
        // throw DecodingError, explicitly, never silently discard or
        // replace the buffered bytes. ------------------------------------
        {
            Utf8StreamDecoder stream(profile);
            stream.feed(10813);  // F0 9F -- 2 of 4 bytes
            stream.feed(242);    // F0 9F xx -- 3 of 4 bytes, still incomplete
            bool threw = false;
            bool right_type = false;
            try {
                stream.finish();
            } catch (const DecodingError&) {
                threw = true;
                right_type = true;
            } catch (const std::exception&) {
                threw = true;
            }
            check(threw, "finish() with an incomplete trailing 4-byte sequence throws");
            check(right_type, "finish() with an incomplete trailing sequence throws exactly DecodingError");
        }

        // --- Malformed UTF-8 across a token boundary: feed a token whose
        // decoded bytes end in a 4-byte UTF-8 LEAD byte requiring 3
        // continuation bytes (F0 9F, from token 10813), then feed a
        // CONTROL token whose literal spelling starts with '<' (0x3C) --
        // NOT a valid UTF-8 continuation byte (must be 0x80-0xBF). This is
        // genuinely malformed UTF-8 arising from real per-token byte
        // decoding, not a hand-crafted invalid byte string. --------------
        {
            Utf8StreamDecoder stream(profile);
            std::string first = stream.feed(10813);  // F0 9F, buffered
            check(first.empty(), "malformed-UTF-8 setup: first token buffers, emits nothing");
            bool threw = false;
            bool right_type = false;
            try {
                stream.feed(0);  // "<|endoftext|>" -- '<' (0x3C) is not a valid continuation byte
            } catch (const DecodingError&) {
                threw = true;
                right_type = true;
            } catch (const std::exception&) {
                threw = true;
            }
            check(threw, "malformed UTF-8 (invalid continuation byte across a token boundary): feed() throws");
            check(right_type, "malformed UTF-8: feed() throws exactly DecodingError");

            // --- Poisoned-state proof: after the error above, this SAME
            // decoder must refuse further use rather than silently
            // continuing from a possibly-inconsistent position. ----------
            bool feed_after_error_threw = false;
            try {
                stream.feed(19556);  // an otherwise perfectly valid token
            } catch (const DecodingError&) {
                feed_after_error_threw = true;
            }
            check(feed_after_error_threw, "poisoned state: feed() after a previous error throws DecodingError, "
                  "even for an otherwise-valid token ID");

            bool finish_after_error_threw = false;
            try {
                stream.finish();
            } catch (const DecodingError&) {
                finish_after_error_threw = true;
            }
            check(finish_after_error_threw, "poisoned state: finish() after a previous error also throws DecodingError");
        }

        // --- Invalid token ID mid-stream: feed() throws DecodingError, and
        // the decoder is poisoned exactly as for a malformed-UTF-8 error. --
        {
            Utf8StreamDecoder stream(profile);
            stream.feed(19556);  // valid, "Hello"
            const int64_t vocab = static_cast<int64_t>(profile.vocab_size());
            bool threw = false;
            try {
                stream.feed(vocab);  // out of range
            } catch (const DecodingError&) {
                threw = true;
            }
            check(threw, "invalid token ID mid-stream: feed() throws DecodingError");
            bool poisoned = false;
            try {
                stream.feed(28);  // otherwise valid
            } catch (const DecodingError&) {
                poisoned = true;
            }
            check(poisoned, "invalid token ID mid-stream: decoder is poisoned afterward");
        }

        // --- CONTROL-token policy respected during streaming. ------------
        {
            Utf8StreamDecoder preserve_stream(profile, DecodeControlPolicy::PreserveControlTokens);
            std::string preserved;
            for (int64_t id : {19556, 0, 19556}) preserved += preserve_stream.feed(id);
            preserve_stream.finish();
            check(preserved == "Hello<|endoftext|>Hello", "streaming Preserve policy matches one-shot decode()");

            Utf8StreamDecoder skip_stream(profile, DecodeControlPolicy::SkipControlTokens);
            std::string skipped;
            for (int64_t id : {19556, 0, 19556}) skipped += skip_stream.feed(id);
            skip_stream.finish();
            check(skipped == "HelloHello", "streaming Skip policy matches one-shot decode()");
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
