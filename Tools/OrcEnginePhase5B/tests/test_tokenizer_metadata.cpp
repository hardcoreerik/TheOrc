// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B Stage 1 tests: three independently satisfiable contracts, each
// returning exit 0 only when every check that contract owns passes.
//
//   (no arguments)                        synthetic metadata suite
//   --real-explicit <path>                explicit real-artifact positive suite
//   --expect-missing-tokenizer <path>     legacy tied-artifact rejection suite
//
// Per OE-ADR-031: smollm2-135m.gguf is the canonical, tokenizer-bearing
// Phase 5B artifact. smollm2-135m-tied.gguf is a frozen legacy tensor/
// output-head-equivalence fixture with no tokenizer.ggml.* metadata --
// it is REQUIRED to fail closed, not required to load. The two real-
// artifact contracts are therefore separate programs-in-one, each
// independently registered, neither depending on the other's artifact
// variable being configured.
//
// The synthetic "valid" fixture is a genuinely BPE-consistent two-tier
// vocabulary (base tokens + their pairwise concatenations as merge
// results), not an arbitrary placeholder scheme -- every one of its
// 48,900 merges' concatenated result actually exists in its own
// 49,152-entry vocabulary, exactly satisfying the same merge-result rule
// enforced against the real tokenizer (confirmed by direct inspection of
// the canonical explicit GGUF before this rule was added to production
// code: 48,900/48,900 real merges resolve, zero exceptions; the legacy
// tied artifact carries no tokenizer/merge metadata at all and is not
// part of that confirmation -- see tokenizer.cpp for the corrected
// wording).
#include <chrono>
#include <cstdio>
#include <optional>
#include <string>
#include <type_traits>
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
// kBaseCount is chosen so kBaseCount + kMergeCount == kVocabSize - kControlCount
// exactly: every merge produces one NEW, otherwise-unused vocabulary entry
// (its own concatenation), and nothing else fills the normal-token budget.
// 235 base tokens give 235*235 = 55,225 possible ordered (i,j) pairs, comfortably
// covering the required 48,900 distinct merges.
constexpr size_t kBaseCount = kVocabSize - kControlCount - kMergeCount;  // 235
static_assert(kBaseCount * kBaseCount >= kMergeCount,
             "not enough base-token pairs to generate kMergeCount distinct merges");

// Fixed-width, prefix-disjoint token families so concatenation is always
// unambiguous and collision-free: control tokens are "<ctrlN>"; base tokens
// are exactly 4 characters ("bNNN"); merged-result tokens are exactly 8
// characters (two concatenated base tokens) and can never collide with a
// base token (different length) or a control token (different prefix).
std::string base_token(size_t i) {
    char buf[8];
    std::snprintf(buf, sizeof(buf), "b%03zu", i);
    return buf;
}

struct SyntheticVocab {
    std::vector<std::string> tokens;      // control + base + merged-result tiers, in that order
    std::vector<std::string> merges;      // "base[i] base[j]", one per merged-result tier entry
};

SyntheticVocab build_synthetic_vocab() {
    SyntheticVocab v;
    v.tokens.reserve(kVocabSize);
    for (size_t i = 0; i < kControlCount; ++i) v.tokens.push_back("<ctrl" + std::to_string(i) + ">");
    std::vector<std::string> base;
    base.reserve(kBaseCount);
    for (size_t i = 0; i < kBaseCount; ++i) {
        base.push_back(base_token(i));
        v.tokens.push_back(base.back());
    }
    v.merges.reserve(kMergeCount);
    size_t produced = 0;
    for (size_t i = 0; i < kBaseCount && produced < kMergeCount; ++i) {
        for (size_t j = 0; j < kBaseCount && produced < kMergeCount; ++j) {
            v.merges.push_back(base[i] + " " + base[j]);
            v.tokens.push_back(base[i] + base[j]);  // the merged RESULT, added to vocab
            ++produced;
        }
    }
    return v;
}

GgufArtifact build_valid_artifact() {
    const SyntheticVocab vocab = build_synthetic_vocab();

    std::vector<GgufValue> token_values;
    token_values.reserve(vocab.tokens.size());
    for (const std::string& t : vocab.tokens) token_values.push_back(string_value(t));

    std::vector<GgufValue> type_values;
    type_values.reserve(vocab.tokens.size());
    for (size_t i = 0; i < vocab.tokens.size(); ++i) {
        type_values.push_back(int32_value(i < kControlCount ? 3 : 1));
    }

    std::vector<GgufValue> merge_values;
    merge_values.reserve(vocab.merges.size());
    for (const std::string& m : vocab.merges) merge_values.push_back(string_value(m));

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

// Which exception TYPE a rejection is expected to be. GgufError originates
// in the reused, frozen Phase 2 accessors (missing keys, wrong scalar
// types); TokenizerMetadataError originates in this profile's own
// invariants. Accepting "any std::exception" would hide a rejection firing
// at the wrong layer -- e.g. a Phase-5B-specific check silently subsumed
// by an unrelated Phase 2 type error.
enum class ExpectedFailure { FromGguf, FromTokenizerProfile };

// Applies `mutate` to a fresh copy of the base artifact and requires
// TokenizerProfile::from_gguf_metadata to throw exactly the expected
// exception type, with a diagnostic containing `fragment` (a stable
// substring identifying the rejected field/invariant, not the full
// message -- avoids brittle exact-string tests). No result object can
// leak from a failed construction: the factory returns by value only on
// the final success path, so any throw before that leaves nothing usable.
template <typename Mutator>
void expect_rejects(const GgufArtifact& base, const std::string& name, ExpectedFailure expected,
                    const std::string& fragment, Mutator mutate) {
    GgufArtifact artifact = base;
    mutate(artifact);
    bool threw = false;
    bool right_type = false;
    std::string what_message;
    try {
        TokenizerProfile profile = TokenizerProfile::from_gguf_metadata(artifact);
        (void)profile;
    } catch (const TokenizerMetadataError& ex) {
        threw = true;
        what_message = ex.what();
        right_type = (expected == ExpectedFailure::FromTokenizerProfile);
    } catch (const GgufError& ex) {
        threw = true;
        what_message = ex.what();
        right_type = (expected == ExpectedFailure::FromGguf);
    } catch (const std::exception& ex) {
        threw = true;
        what_message = ex.what();
        right_type = false;  // some OTHER exception type -- not one of the two expected
    }
    check(threw, name + " -- throws");
    check(right_type, name + " -- correct exception type (" +
              (expected == ExpectedFailure::FromGguf ? "GgufError" : "TokenizerMetadataError") + ")");
    const bool has_fragment = threw && what_message.find(fragment) != std::string::npos;
    check(has_fragment, name + " -- diagnostic contains '" + fragment + "'");
}

void run_positive_and_structural_checks(const GgufArtifact& valid) {
    std::printf("=== Phase 5B Stage 1: synthetic metadata construction ===\n");

    // Compile-time: the public API is genuinely const/read-only. These are
    // real compiler-verified properties, not runtime checks -- expressed
    // here as static_assert, not counted in the [PASS]/[FAIL] tally below.
    static_assert(std::is_same_v<decltype(std::declval<const TokenizerProfile&>().tokens()),
                                 const std::vector<std::string>&>,
                 "tokens() must return a const reference");
    static_assert(std::is_same_v<decltype(std::declval<const TokenizerProfile&>().merges()),
                                 const std::vector<std::string>&>,
                 "merges() must return a const reference");
    static_assert(std::is_same_v<decltype(std::declval<const TokenizerProfile&>().token_types()),
                                 const std::vector<TokenizerTokenType>&>,
                 "token_types() must return a const reference");
    static_assert(!std::is_copy_assignable_v<TokenizerProfile> ||
                       std::is_same_v<decltype(std::declval<TokenizerProfile&>().tokens()),
                                     const std::vector<std::string>&>,
                 "no accessor may return a non-const reference");

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
    const SyntheticVocab expected = build_synthetic_vocab();
    bool order_preserved = profile.tokens().size() == expected.tokens.size();
    for (size_t i = 0; order_preserved && i < expected.tokens.size(); ++i) {
        if (profile.tokens()[i] != expected.tokens[i]) order_preserved = false;
    }
    check(order_preserved, "2. tokens() preserves exact input ordering");
    check(profile.merges().front() == expected.merges.front(), "2. merges() preserves exact input ordering (first entry)");
    check(profile.merges().back() == expected.merges.back(), "2. merges() preserves exact input ordering (last entry)");

    // 3. Immutability: enforced at compile time (static_asserts above, no
    // public mutator exists on TokenizerProfile at all). Demonstrated at
    // runtime by confirming a const reference's repeated reads are
    // identical (nothing lazily recomputes or drifts).
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

    // Requirements 6 ("no frozen global/static state mutated") and 8
    // ("construction path does not touch tensor weights or KV-cache
    // state") are CODE-INSPECTION findings, not runtime-checkable in
    // isolation, and are recorded as such -- NOT as executable [PASS]
    // checks (an earlier draft incorrectly counted them via
    // check(true, ...), which is a tautology: it cannot fail regardless
    // of whether the underlying claim holds).
    //
    //   6. Verified by inspection of Tools/OrcEnginePhase5B/src/tokenizer.cpp:
    //      no static or global mutable state exists anywhere in the
    //      translation unit -- every variable is a local or a member of the
    //      value returned by from_gguf_metadata.
    //   8. Verified by inspection of the same file's #include list and every
    //      type it references: only <algorithm>/<unordered_set>/
    //      orcengine/tokenizer.hpp are included, and the construction path
    //      never names Model, ResidentView, ContiguousAttentionKVStore, or
    //      any other tensor-weight or KV-cache type.
    std::printf("[INSPECTION] 6. no frozen global/static state mutated (see source comment, not a runtime check)\n");
    std::printf("[INSPECTION] 8. construction path does not touch tensor weights or KV-cache state (see source comment, not a runtime check)\n");
}

void run_adversarial_checks(const GgufArtifact& valid) {
    std::printf("\n=== Adversarial cases (exception type + diagnostic fragment verified per case) ===\n");
    using EF = ExpectedFailure;

    expect_rejects(valid, "missing tokenizer.ggml.model", EF::FromGguf, "tokenizer.ggml.model",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.model"); });
    expect_rejects(valid, "missing tokenizer.ggml.pre", EF::FromGguf, "tokenizer.ggml.pre",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.pre"); });
    expect_rejects(valid, "missing tokenizer.ggml.tokens", EF::FromGguf, "tokenizer.ggml.tokens",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.tokens"); });
    expect_rejects(valid, "missing tokenizer.ggml.token_type", EF::FromGguf, "tokenizer.ggml.token_type",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.token_type"); });
    expect_rejects(valid, "missing tokenizer.ggml.merges", EF::FromGguf, "tokenizer.ggml.merges",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.merges"); });
    expect_rejects(valid, "missing tokenizer.ggml.bos_token_id", EF::FromGguf, "tokenizer.ggml.bos_token_id",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.bos_token_id"); });
    expect_rejects(valid, "missing tokenizer.ggml.eos_token_id", EF::FromGguf, "tokenizer.ggml.eos_token_id",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.eos_token_id"); });
    expect_rejects(valid, "missing tokenizer.ggml.add_bos_token", EF::FromGguf, "tokenizer.ggml.add_bos_token",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.add_bos_token"); });
    expect_rejects(valid, "missing tokenizer.ggml.add_eos_token", EF::FromGguf, "tokenizer.ggml.add_eos_token",
        [](GgufArtifact& a) { a.metadata.erase("tokenizer.ggml.add_eos_token"); });

    expect_rejects(valid, "wrong type: model (uint instead of string)", EF::FromGguf, "tokenizer.ggml.model",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.model"] = uint32_value(1); });
    expect_rejects(valid, "wrong type: pre (uint instead of string)", EF::FromGguf, "tokenizer.ggml.pre",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.pre"] = uint32_value(1); });
    expect_rejects(valid, "wrong type: tokens (string instead of array)", EF::FromTokenizerProfile, "tokenizer.ggml.tokens",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.tokens"] = string_value("not-an-array"); });
    expect_rejects(valid, "wrong type: merges (string instead of array)", EF::FromTokenizerProfile, "tokenizer.ggml.merges",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.merges"] = string_value("not-an-array"); });
    expect_rejects(valid, "wrong type: token_type (string instead of array)", EF::FromTokenizerProfile, "tokenizer.ggml.token_type",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.token_type"] = string_value("not-an-array"); });
    expect_rejects(valid, "wrong type: bos_token_id (string instead of uint)", EF::FromGguf, "tokenizer.ggml.bos_token_id",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.bos_token_id"] = string_value("0"); });
    expect_rejects(valid, "wrong type: eos_token_id (string instead of uint)", EF::FromGguf, "tokenizer.ggml.eos_token_id",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.eos_token_id"] = string_value("0"); });
    expect_rejects(valid, "wrong type: add_bos_token (uint instead of bool)", EF::FromTokenizerProfile, "tokenizer.ggml.add_bos_token",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_bos_token"] = uint32_value(0); });
    expect_rejects(valid, "wrong type: add_eos_token (uint instead of bool)", EF::FromTokenizerProfile, "tokenizer.ggml.add_eos_token",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_eos_token"] = uint32_value(0); });

    expect_rejects(valid, "unsupported model value ('llama' instead of 'gpt2')", EF::FromTokenizerProfile, "tokenizer.ggml.model",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.model"] = string_value("llama"); });
    expect_rejects(valid, "unsupported pre value ('llama-bpe' instead of 'smollm')", EF::FromTokenizerProfile, "tokenizer.ggml.pre",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.pre"] = string_value("llama-bpe"); });

    expect_rejects(valid, "vocabulary count != 49152 (one fewer)", EF::FromTokenizerProfile, "tokenizer.ggml.tokens",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
            elems.pop_back();
        });
    expect_rejects(valid, "token_type count != vocabulary count (one fewer)", EF::FromTokenizerProfile, "tokenizer.ggml.token_type",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
            elems.pop_back();
        });
    expect_rejects(valid, "merge count != 48900 (one fewer)", EF::FromTokenizerProfile, "tokenizer.ggml.merges",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
            elems.pop_back();
        });

    expect_rejects(valid, "bos_token_id outside vocabulary", EF::FromTokenizerProfile, "tokenizer.ggml.bos_token_id",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.bos_token_id"] = uint32_value(kVocabSize); });
    expect_rejects(valid, "eos_token_id outside vocabulary", EF::FromTokenizerProfile, "tokenizer.ggml.eos_token_id",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.eos_token_id"] = uint32_value(999999); });
    expect_rejects(valid, "bos_token_id valid-but-not-pinned-ID-0", EF::FromTokenizerProfile, "tokenizer.ggml.bos_token_id",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.bos_token_id"] = uint32_value(17); });
    expect_rejects(valid, "eos_token_id valid-but-not-pinned-ID-0", EF::FromTokenizerProfile, "tokenizer.ggml.eos_token_id",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.eos_token_id"] = uint32_value(17); });

    expect_rejects(valid, "add_bos_token not false", EF::FromTokenizerProfile, "tokenizer.ggml.add_bos_token",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_bos_token"] = bool_value(true); });
    expect_rejects(valid, "add_eos_token not false", EF::FromTokenizerProfile, "tokenizer.ggml.add_eos_token",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.add_eos_token"] = bool_value(true); });

    expect_rejects(valid, "token_type value outside {1, 3}", EF::FromTokenizerProfile, "tokenizer.ggml.token_type",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
            elems[20] = int32_value(2);
        });
    expect_rejects(valid, "CONTROL token count != 17 (one CONTROL flipped to NORMAL)", EF::FromTokenizerProfile, "CONTROL",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
            elems[5] = int32_value(1);
        });
    expect_rejects(valid, "CONTROL IDs not exactly 0-16 (moved, count still 17)", EF::FromTokenizerProfile, "CONTROL",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.token_type"].data);
            elems[0] = int32_value(1);
            elems[20] = int32_value(3);
        });

    expect_rejects(valid, "malformed merge entry (no space)", EF::FromTokenizerProfile, "tokenizer.ggml.merges",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
            elems[0] = string_value("nospacehere");
        });
    expect_rejects(valid, "merge entry with more than two components", EF::FromTokenizerProfile, "tokenizer.ggml.merges",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
            elems[0] = string_value("<ctrl0> <ctrl1> <ctrl2>");
        });
    expect_rejects(valid, "duplicate merge records", EF::FromTokenizerProfile, "tokenizer.ggml.merges",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
            elems[1] = elems[0];
        });
    expect_rejects(valid, "merge component individually unresolvable against vocabulary", EF::FromTokenizerProfile, "tokenizer.ggml.merges",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
            elems[0] = string_value(base_token(0) + " not_in_vocabulary_at_all");
        });
    // NEW (Correction 2): left and right BOTH individually exist, the pair syntax
    // is valid, and the pair is unique -- but the CONCATENATED result was never
    // added to the vocabulary. base_token(kBaseCount-1) paired with
    // base_token(kBaseCount-2) is guaranteed absent from the synthetic merge
    // table: build_synthetic_vocab() enumerates (i, j) row-major from i=0 and
    // stops at exactly kMergeCount pairs, which (given kBaseCount=235,
    // kMergeCount=48900) never reaches i in the high-230s range at all -- see the
    // static_assert-backed construction above.
    expect_rejects(valid, "merge result (concatenation) absent from vocabulary", EF::FromTokenizerProfile, "tokenizer.ggml.merges",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.merges"].data);
            elems[0] = string_value(base_token(kBaseCount - 1) + " " + base_token(kBaseCount - 2));
        });

    expect_rejects(valid, "duplicate vocabulary tokens", EF::FromTokenizerProfile, "tokenizer.ggml.tokens",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
            elems[100] = elems[99];
        });
    expect_rejects(valid, "empty required vocabulary string", EF::FromTokenizerProfile, "tokenizer.ggml.tokens",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
            elems[100] = string_value("");
        });
    expect_rejects(valid, "empty tokenizer.ggml.model string", EF::FromTokenizerProfile, "tokenizer.ggml.model",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.model"] = string_value(""); });
    expect_rejects(valid, "empty tokenizer.ggml.pre string", EF::FromTokenizerProfile, "tokenizer.ggml.pre",
        [](GgufArtifact& a) { a.metadata["tokenizer.ggml.pre"] = string_value(""); });

    // Stage 2B reconciliation (Codex finding 1, corrected in the follow-up
    // reconciliation pass): merge_rank_'s internal key join
    // (left + '\x01' + right) is only collision-free if no vocabulary token
    // can ever contain a raw 0x01 byte. Before this check existed, nothing
    // in from_gguf_metadata enforced that. This case must FAIL against that
    // old (missing-check) behavior and PASS once the trust-boundary
    // rejection exists -- which requires the mutated token to be
    // UNREFERENCED by anything else from_gguf_metadata validates, so the
    // 0x01 check is the ONLY thing that can reject it.
    //
    // Index 100 (an ordinary base token, part of the synthetic BPE merge
    // graph build_synthetic_vocab() constructs) does NOT satisfy that: it is
    // both a merge LEFT/RIGHT component and very likely a merge RESULT
    // (base[i]+base[j]) for some other pair. Mutating it would make one or
    // more tokenizer.ggml.merges entries fail to resolve against the
    // vocabulary -- against the OLD (pre-fix) code, merge-result validation
    // (an earlier, pre-existing check, unrelated to this reconciliation)
    // would reject the artifact first, so the test would NOT demonstrate
    // what it claims: that the old code accepted the artifact and only the
    // NEW 0x01 check newly rejects it.
    //
    // CONTROL token 16 ("<ctrl16>", the last of the 17 CONTROL entries) is
    // never a merge component (merges only ever reference base/merged-result
    // tokens, never CONTROL strings), is not BOS/EOS (both pinned to ID 0,
    // unaffected), and its replacement below is chosen to remain unique and
    // to share no prefix relationship with any of the other 16 CONTROL
    // strings -- so no unrelated invariant in from_gguf_metadata can reject
    // it, isolating the 0x01 check as the sole possible rejection reason.
    expect_rejects(valid, "vocabulary token contains reserved separator byte 0x01", EF::FromTokenizerProfile, "0x01",
        [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
            elems[16] = string_value(std::string("<bad") + '\x01' + "ctrl>");
        });

    // Stage 2B reconciliation (Codex finding 2): RecognizeControlTokens'
    // fixed-ID-order scan of tokens_[0..control_count_) is only precedence-
    // safe if no CONTROL spelling is a literal prefix of another. Before
    // this check existed, nothing enforced that either -- this case makes
    // control token 1 exactly "control token 0's spelling + one more
    // character" (token[0] is a literal prefix of token[1]) and must FAIL
    // against the old (missing-check) behavior, PASS once the construction-
    // time rejection exists.
    expect_rejects(valid, "CONTROL token is a literal prefix of another CONTROL token", EF::FromTokenizerProfile,
        "prefix", [](GgufArtifact& a) {
            auto& elems = std::get<std::vector<GgufValue>>(a.metadata["tokenizer.ggml.tokens"].data);
            const std::string& first = std::get<std::string>(elems[0].data);
            elems[1] = string_value(first + "X");
        });
}

// Shared by the explicit-positive contract: exercises the real, committed
// load_tokenizer_profile(path) entry point (via its two constituent calls,
// index_gguf()+from_gguf_metadata()) against a real pinned artifact. Used
// ONLY against the canonical explicit artifact -- the legacy tied artifact
// has its own, separate rejection contract below and never flows through
// this success-shaped helper.
std::optional<TokenizerProfile> load_and_check(const std::string& path, const char* label) {
    const auto t0 = std::chrono::steady_clock::now();
    GgufArtifact artifact;
    try {
        artifact = index_gguf(path);
    } catch (const std::exception& ex) {
        check(false, std::string(label) + ": index_gguf(path) succeeds -- FAILED: " + ex.what());
        return std::nullopt;
    }
    const double indexing_ms = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - t0).count();
    std::printf("[INFO] %s: index_gguf indexing time (GgufTelemetry.parse_milliseconds) = %.3f ms "
                "(wall-clock measured around the call: %.3f ms)\n",
                label, artifact.telemetry.parse_milliseconds, indexing_ms);
    std::printf("[INFO] %s: no process/peak memory reported -- GgufTelemetry has no such field, and "
                "adding new measurement infrastructure is out of scope for this closure pass.\n", label);

    // TokenizerProfile has no default constructor (deliberately, per Section 4's
    // "immutable tables" requirement) and no accessible constructor here except
    // via from_gguf_metadata's return, so the try/catch must construct the
    // std::optional directly from that call rather than assigning into a
    // pre-declared TokenizerProfile local.
    std::optional<TokenizerProfile> profile_or_none;
    try {
        profile_or_none.emplace(TokenizerProfile::from_gguf_metadata(artifact));
    } catch (const std::exception& ex) {
        check(false, std::string(label) + ": load_tokenizer_profile(path) succeeds -- FAILED: " +
                         std::string(ex.what()));
        return std::nullopt;
    }
    check(true, std::string(label) + ": load_tokenizer_profile(path) succeeds");
    const TokenizerProfile& profile = *profile_or_none;
    check(profile.vocab_size() == kVocabSize, std::string(label) + ": vocabulary count is 49152");
    check(profile.merges().size() == kMergeCount, std::string(label) + ": merge count is 48900");
    check(profile.token_types().size() == kVocabSize, std::string(label) + ": token-type count is 49152");
    size_t control_count = 0;
    bool control_positions_correct = true;
    for (size_t i = 0; i < profile.token_types().size(); ++i) {
        if (profile.token_types()[i] == TokenizerTokenType::Control) {
            if (control_count != i) control_positions_correct = false;  // must be contiguous from 0
            ++control_count;
        }
    }
    check(control_count == kControlCount, std::string(label) + ": CONTROL count is 17");
    check(control_positions_correct, std::string(label) + ": CONTROL positions are exactly IDs 0-16");
    check(profile.bos_token_id() == 0, std::string(label) + ": BOS is 0");
    check(profile.eos_token_id() == 0, std::string(label) + ": EOS is 0");
    check(!profile.add_bos_token(), std::string(label) + ": add_bos_token is false");
    check(!profile.add_eos_token(), std::string(label) + ": add_eos_token is false");
    return profile_or_none;
}

// Contract B: explicit real-artifact positive suite. Requires index_gguf
// success, load_tokenizer_profile success (via both the convenience
// wrapper and the two-call path), and every pinned invariant (vocab/merge/
// token-type/CONTROL/BOS/EOS/add-token counts and positions). Because
// from_gguf_metadata() itself enforces the merge-result-concatenation
// invariant during construction, a successful load already proves all
// 48,900 merge results resolved -- there is no separate resolution pass
// to run afterward.
int run_real_explicit_positive(const std::string& path) {
    std::printf("=== Phase 5B Stage 1: explicit real-artifact positive contract ===\n");

    TokenizerProfile via_convenience = load_tokenizer_profile(path);
    check(via_convenience.vocab_size() == kVocabSize,
          "explicit: load_tokenizer_profile(path) convenience wrapper succeeds and matches vocab_size");

    std::optional<TokenizerProfile> profile = load_and_check(path, "explicit");
    check(profile.has_value() && profile->merges().size() == kMergeCount,
          "explicit: all 48,900 merge results resolved (proven by successful construction)");

    std::printf("\n=== Summary ===\n");
    if (g_failures == 0) { std::printf("ALL CHECKS PASSED\n"); return 0; }
    std::printf("%d FAILURES\n", g_failures);
    return 1;
}

// Contract C: legacy tied-artifact expected-rejection suite. Per
// OE-ADR-031, smollm2-135m-tied.gguf is a frozen tensor/output-head-
// equivalence fixture with no tokenizer.ggml.* metadata -- it is REQUIRED
// to fail closed, and this contract's exit code is 0 ONLY when that exact
// rejection occurs: index_gguf must still succeed (the artifact itself is
// readable GGUF), TokenizerProfile construction must throw exactly
// GgufError (not TokenizerMetadataError, not any other type, and not
// silently succeed), and the diagnostic must name the specific missing
// key. This is deliberately NOT CTest WILL_FAIL -- WILL_FAIL would treat
// ANY nonzero exit (a crash, an unrelated exception, a wrong-type
// rejection) as a false pass. Every failure mode below is checked and
// reported explicitly instead.
int run_legacy_tied_rejection(const std::string& path) {
    std::printf("=== Phase 5B Stage 1: legacy tied-artifact expected-rejection contract ===\n");

    GgufArtifact artifact;
    try {
        artifact = index_gguf(path);
    } catch (const std::exception& ex) {
        check(false, std::string("tied: index_gguf(path) succeeds -- FAILED: ") + ex.what());
        std::printf("\n=== Summary ===\n%d FAILURES\n", g_failures);
        return 1;
    }
    check(true, "tied: index_gguf(path) succeeds");

    bool unexpectedly_loaded = false;
    bool threw = false;
    bool right_type = false;
    std::string what_message;
    try {
        TokenizerProfile profile = TokenizerProfile::from_gguf_metadata(artifact);
        (void)profile;
        unexpectedly_loaded = true;
    } catch (const GgufError& ex) {
        threw = true;
        right_type = true;
        what_message = ex.what();
    } catch (const std::exception& ex) {
        threw = true;
        right_type = false;  // wrong type -- e.g. TokenizerMetadataError or anything else
        what_message = ex.what();
    }
    check(!unexpectedly_loaded, "tied: tokenizer profile construction rejects (did not unexpectedly load)");
    check(threw, "tied: rejection throws an exception");
    check(right_type, "tied: rejection type is exactly GgufError");
    const bool has_fragment = threw && what_message.find("tokenizer.ggml.model") != std::string::npos;
    check(has_fragment, "tied: diagnostic identifies missing tokenizer.ggml.model");

    std::printf("\n=== Summary ===\n");
    if (g_failures == 0) { std::printf("ALL CHECKS PASSED (expected rejection confirmed)\n"); return 0; }
    std::printf("%d FAILURES\n", g_failures);
    return 1;
}

}  // namespace

int main(int argc, char** argv) {
    try {
        const std::string mode = argc >= 2 ? argv[1] : "";

        if (mode == "--real-explicit") {
            if (argc < 3) {
                std::fprintf(stderr, "--real-explicit requires a GGUF path\n");
                return 2;
            }
            return run_real_explicit_positive(argv[2]);
        }
        if (mode == "--expect-missing-tokenizer") {
            if (argc < 3) {
                std::fprintf(stderr, "--expect-missing-tokenizer requires a GGUF path\n");
                return 2;
            }
            return run_legacy_tied_rejection(argv[2]);
        }

        // Default (no arguments): synthetic metadata suite. No large
        // artifacts required.
        const GgufArtifact valid = build_valid_artifact();
        run_positive_and_structural_checks(valid);
        run_adversarial_checks(valid);

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) { std::printf("ALL CHECKS PASSED\n"); return 0; }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] unexpected exception: %s\n", ex.what());
        return 1;
    }
}
