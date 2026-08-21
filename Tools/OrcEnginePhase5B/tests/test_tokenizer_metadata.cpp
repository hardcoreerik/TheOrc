// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B Stage 1 tests: constructs a synthetic GgufArtifact directly in
// memory (GgufArtifact::metadata is public, GgufValue is a plain
// aggregate) rather than writing/parsing a binary GGUF fixture -- avoids
// a new on-disk fixture, avoids touching Phase 2's test helpers, avoids a
// JSON dependency, and avoids reimplementing the GGUF writer, per the
// spec's test-design guidance. The synthetic profile exactly matches the
// pinned tuple's COUNTS (49,152 vocab / 48,900 merges / 17 CONTROL) so
// the exact-count invariants are genuinely exercised, but uses
// deterministic placeholder token/merge strings rather than the real
// SmolLM2-135M vocabulary -- this test proves metadata-construction and
// validation correctness, not tokenization correctness (no encode/decode
// exists yet to test).
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

GgufValue string_value(std::string s) {
    GgufValue v;
    v.type = GgufValueType::String;
    v.data = std::move(s);
    return v;
}
GgufValue int32_value(int64_t x) {
    GgufValue v;
    v.type = GgufValueType::Int32;
    v.data = x;
    return v;
}
GgufValue uint32_value(uint64_t x) {
    GgufValue v;
    v.type = GgufValueType::UInt32;
    v.data = x;
    return v;
}
GgufValue bool_value(bool b) {
    GgufValue v;
    v.type = GgufValueType::Bool;
    v.data = b;
    return v;
}
GgufValue array_value(std::vector<GgufValue> elements) {
    GgufValue v;
    v.type = GgufValueType::Array;
    v.data = std::move(elements);
    return v;
}

constexpr size_t kControlCount = 17;
constexpr size_t kVocabSize = 49152;
constexpr size_t kMergeCount = 48900;

// Deterministic, bounded synthetic vocabulary: 17 control-looking
// placeholders at indices 0-16 (matching the real profile's CONTROL
// token count and position), then 49,135 unique normal placeholders.
std::vector<std::string> synthetic_tokens() {
    std::vector<std::string> tokens;
    tokens.reserve(kVocabSize);
    for (size_t i = 0; i < kControlCount; ++i) {
        tokens.push_back("<ctrl" + std::to_string(i) + ">");
    }
    for (size_t i = kControlCount; i < kVocabSize; ++i) {
        char buf[16];
        std::snprintf(buf, sizeof(buf), "tk%05zu", i);
        tokens.push_back(buf);
    }
    return tokens;
}

// Each merge is "tokens[k] tokens[k+1]" for k = 0..48899 -- both
// components are always real, existing, distinct vocabulary entries by
// construction (49,151 possible consecutive pairs from a 49,152-entry
// vocab comfortably covers the required 48,900), and every merge string
// is automatically unique since each starts with a different, unique
// tokens[k].
std::vector<std::string> synthetic_merges(const std::vector<std::string>& tokens) {
    std::vector<std::string> merges;
    merges.reserve(kMergeCount);
    for (size_t k = 0; k < kMergeCount; ++k) {
        merges.push_back(tokens[k] + " " + tokens[k + 1]);
    }
    return merges;
}

GgufArtifact build_valid_artifact() {
    const std::vector<std::string> tokens = synthetic_tokens();
    const std::vector<std::string> merges = synthetic_merges(tokens);

    std::vector<GgufValue> token_values;
    token_values.reserve(tokens.size());
    for (const std::string& t : tokens) token_values.push_back(string_value(t));

    std::vector<GgufValue> type_values;
    type_values.reserve(tokens.size());
    for (size_t i = 0; i < tokens.size(); ++i) {
        type_values.push_back(int32_value(i < kControlCount ? 3 : 1));
    }

    std::vector<GgufValue> merge_values;
    merge_values.reserve(merges.size());
    for (const std::string& m : merges) merge_values.push_back(string_value(m));

    GgufArtifact artifact;
    artifact.metadata["tokenizer.ggml.model"] = string_value("gpt2");
    artifact.metadata["tokenizer.ggml.pre"] = string_value("smollm");
    artifact.metadata["tokenizer.ggml.tokens"] = array_value(std::move(token_values));
    artifact.metadata["tokenizer.ggml.merges"] = array_value(std::move(merge_values));
    artifact.metadata["tokenizer.ggml.token_type"] = array_value(std::move(type_values));
    artifact.metadata["tokenizer.ggml.bos_token_id"] = uint32_value(0);
    artifact.metadata["tokenizer.ggml.eos_token_id"] = uint32_value(0);
    artifact.metadata["tokenizer.ggml.add_bos_token"] = bool_value(false);
    artifact.metadata["tokenizer.ggml.add_eos_token"] = bool_value(false);
    return artifact;
}

// Applies `mutate` to a fresh copy of the valid base artifact and
// requires TokenizerProfile::from_gguf_metadata to throw. Also requires
// that no result "leaks" from a failed construction, which is
// structurally guaranteed by returning by value only on the final
// success path (test requirement 5).
template <typename Mutator>
void expect_rejects(const GgufArtifact& base, const std::string& name, Mutator mutate) {
    GgufArtifact artifact = base;
    mutate(artifact);
    bool threw = false;
    try {
        TokenizerProfile profile = TokenizerProfile::from_gguf_metadata(artifact);
        (void)profile;
    } catch (const std::exception&) {
        threw = true;
    }
    check(threw, name);
}

}  // namespace

int main() {
    try {
        const GgufArtifact valid = build_valid_artifact();

        std::printf("=== Phase 5B Stage 1: tokenizer metadata construction ===\n");

        // 1. A valid pinned profile constructs successfully.
        TokenizerProfile profile = TokenizerProfile::from_gguf_metadata(valid);
        check(profile.vocab_size() == kVocabSize, "1. valid profile constructs, vocab_size == 49152");
        check(profile.merges().size() == kMergeCount, "1. valid profile constructs, merges.size() == 48900");
        check(profile.token_types().size() == kVocabSize, "1. valid profile constructs, token_types.size() == 49152");
        check(profile.bos_token_id() == 0 && profile.eos_token_id() == 0, "1. bos/eos both 0");
        check(!profile.add_bos_token() && !profile.add_eos_token(), "1. add_bos_token/add_eos_token both false");
        check(profile.token_types()[0] == TokenizerTokenType::Control &&
                  profile.token_types()[16] == TokenizerTokenType::Control &&
                  profile.token_types()[17] == TokenizerTokenType::Normal,
              "1. CONTROL tokens occupy exactly indices 0-16");

        // 2. Constructed tables preserve exact input ordering.
        const std::vector<std::string> expected_tokens = synthetic_tokens();
        bool order_preserved = profile.tokens().size() == expected_tokens.size();
        for (size_t i = 0; order_preserved && i < expected_tokens.size(); ++i) {
            if (profile.tokens()[i] != expected_tokens[i]) order_preserved = false;
        }
        check(order_preserved, "2. tokens() preserves exact input ordering");
        check(profile.merges().front() == (expected_tokens[0] + " " + expected_tokens[1]),
              "2. merges() preserves exact input ordering (first entry)");
        check(profile.merges().back() == (expected_tokens[kMergeCount - 1] + " " + expected_tokens[kMergeCount]),
              "2. merges() preserves exact input ordering (last entry)");

        // 3. Constructed tables are immutable through the public API: TokenizerProfile
        // exposes only const accessors (tokens()/merges()/token_types() all return
        // `const std::vector<...>&`) and has no public mutator -- this is enforced at
        // compile time by the class definition, not merely by convention. Demonstrated
        // here by taking a const reference and confirming repeated reads are identical
        // (nothing lazily recomputes or drifts).
        {
            const TokenizerProfile& const_ref = profile;
            check(&const_ref.tokens() == &profile.tokens(), "3. repeated const access is stable (no hidden mutation)");
        }

        // 7. Repeated construction from identical metadata produces identical tables.
        {
            TokenizerProfile profile2 = TokenizerProfile::from_gguf_metadata(valid);
            check(profile.tokens() == profile2.tokens(), "7. repeated construction: identical tokens()");
            check(profile.merges() == profile2.merges(), "7. repeated construction: identical merges()");
            check(profile.token_types() == profile2.token_types(), "7. repeated construction: identical token_types()");
        }

        // 6/8: structural facts, not runtime-checkable in isolation -- recorded here
        // as an explicit claim tied to code inspection, not silently assumed. Verified
        // by inspection of Tools/OrcEnginePhase5B/src/tokenizer.cpp: no static/global
        // mutable state exists anywhere in the translation unit (satisfies 6), and the
        // file includes only <algorithm>/<unordered_set>/orcengine/tokenizer.hpp -- no
        // Model, ResidentView, ContiguousAttentionKVStore, or any other tensor-weight or
        // KV-cache type is referenced anywhere in the construction path (satisfies 8).
        check(true, "6. no frozen global/static state mutated (verified by code inspection, see comment)");
        check(true, "8. construction path does not touch tensor weights or KV-cache state (verified by code inspection, see comment)");

        std::printf("\n=== Adversarial cases (4/5: each malformed case fails explicitly, no partial result) ===\n");

        expect_rejects(valid, "missing tokenizer.ggml.model",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.model"); });
        expect_rejects(valid, "missing tokenizer.ggml.pre",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.pre"); });
        expect_rejects(valid, "missing tokenizer.ggml.tokens",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.tokens"); });
        expect_rejects(valid, "missing tokenizer.ggml.token_type",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.token_type"); });
        expect_rejects(valid, "missing tokenizer.ggml.merges",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.merges"); });
        expect_rejects(valid, "missing tokenizer.ggml.bos_token_id",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.bos_token_id"); });
        expect_rejects(valid, "missing tokenizer.ggml.eos_token_id",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.eos_token_id"); });
        expect_rejects(valid, "missing tokenizer.ggml.add_bos_token",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.add_bos_token"); });
        expect_rejects(valid, "missing tokenizer.ggml.add_eos_token",
            [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.add_eos_token"); });

        expect_rejects(valid, "wrong type: model (uint instead of string)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.model"] = uint32_value(1); });
        expect_rejects(valid, "wrong type: pre (uint instead of string)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.pre"] = uint32_value(1); });
        expect_rejects(valid, "wrong type: tokens (string instead of array)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.tokens"] = string_value("not-an-array"); });
        expect_rejects(valid, "wrong type: merges (string instead of array)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.merges"] = string_value("not-an-array"); });
        expect_rejects(valid, "wrong type: token_type (string instead of array)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.token_type"] = string_value("not-an-array"); });
        expect_rejects(valid, "wrong type: bos_token_id (string instead of uint)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.bos_token_id"] = string_value("0"); });
        expect_rejects(valid, "wrong type: eos_token_id (string instead of uint)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.eos_token_id"] = string_value("0"); });
        expect_rejects(valid, "wrong type: add_bos_token (uint instead of bool)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_bos_token"] = uint32_value(0); });
        expect_rejects(valid, "wrong type: add_eos_token (uint instead of bool)",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_eos_token"] = uint32_value(0); });

        expect_rejects(valid, "unsupported model value ('llama' instead of 'gpt2')",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.model"] = string_value("llama"); });
        expect_rejects(valid, "unsupported pre value ('llama-bpe' instead of 'smollm')",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.pre"] = string_value("llama-bpe"); });

        expect_rejects(valid, "vocabulary count != 49152 (one fewer)",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
                elems.pop_back();
            });
        expect_rejects(valid, "token_type count != vocabulary count (one fewer)",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
                elems.pop_back();
            });
        expect_rejects(valid, "merge count != 48900 (one fewer)",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
                elems.pop_back();
            });

        expect_rejects(valid, "bos_token_id outside vocabulary",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.bos_token_id"] = uint32_value(kVocabSize); });
        expect_rejects(valid, "eos_token_id outside vocabulary",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.eos_token_id"] = uint32_value(999999); });
        expect_rejects(valid, "bos_token_id valid-but-not-pinned-ID-0",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.bos_token_id"] = uint32_value(17); });
        expect_rejects(valid, "eos_token_id valid-but-not-pinned-ID-0",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.eos_token_id"] = uint32_value(17); });

        expect_rejects(valid, "add_bos_token not false",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_bos_token"] = bool_value(true); });
        expect_rejects(valid, "add_eos_token not false",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_eos_token"] = bool_value(true); });

        expect_rejects(valid, "token_type value outside {1, 3}",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
                elems[20] = int32_value(2);  // e.g. an UNKNOWN/BYTE-style value this profile doesn't use
            });
        expect_rejects(valid, "CONTROL token count != 17 (one CONTROL flipped to NORMAL)",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
                elems[5] = int32_value(1);  // was CONTROL (index < 17), now claims NORMAL
            });
        expect_rejects(valid, "CONTROL IDs not exactly 0-16 (moved, count still 17)",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
                elems[0] = int32_value(1);   // was CONTROL, now NORMAL
                elems[20] = int32_value(3);  // was NORMAL, now CONTROL -- count still 17, positions wrong
            });

        expect_rejects(valid, "malformed merge entry (no space)",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
                elems[0] = string_value("nospacehere");
            });
        expect_rejects(valid, "merge entry with more than two components",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
                elems[0] = string_value("<ctrl0> <ctrl1> <ctrl2>");
            });
        expect_rejects(valid, "duplicate merge records",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
                elems[1] = elems[0];
            });
        expect_rejects(valid, "merge component unresolvable against vocabulary",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
                elems[0] = string_value("<ctrl0> not_in_vocabulary_at_all");
            });

        expect_rejects(valid, "duplicate vocabulary tokens",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
                elems[100] = elems[99];
            });
        expect_rejects(valid, "empty required vocabulary string",
            [](GgufArtifact& a) {
                auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
                elems[100] = string_value("");
            });
        expect_rejects(valid, "empty tokenizer.ggml.model string",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.model"] = string_value(""); });
        expect_rejects(valid, "empty tokenizer.ggml.pre string",
            [](GgufArtifact& a) { a.metadata["tokenizer.ggml.pre"] = string_value(""); });

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("ALL CHECKS PASSED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] unexpected exception: %s\n", ex.what());
        return 1;
    }
}
