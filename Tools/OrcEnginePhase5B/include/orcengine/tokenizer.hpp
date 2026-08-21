// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B Stage 1: native GGUF tokenizer-metadata construction and
// fail-closed validation, per docs/OrcEngine/PHASE5B_TOKENIZER_SPEC.md
// and DECISION_LOG.md OE-ADR-030. Constructs and validates the exact
// pinned SmolLM2-135M tokenizer profile (Section 3/8 of the spec) from
// already-parsed GGUF metadata.
//
// Stage 2B (DECISION_LOG.md OE-ADR-034) extends this same profile with
// native text-to-token-ID encoding: byte-to-Unicode mapping, ranked BPE
// merge execution, and the two accepted special-token policies. Decode,
// streaming decode, and frozen-engine integration remain unimplemented.
//
// Reuses the frozen Phase 2 GGUF reader's existing public API
// (GgufArtifact::metadata, require_metadata, GgufValueType::Array)
// without modifying it -- see the spec's Section 4 for why no Phase 2
// change is required.
#pragma once

#include <cstdint>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

#include "orcengine/gguf.hpp"

namespace orcengine {

// Thrown for any Phase-5B-specific tokenizer profile invariant this
// pinned profile requires that isn't already covered by Phase 2's own
// GgufError (missing keys, wrong scalar types already throw GgufError
// via require_metadata/metadata_string/metadata_u64).
class TokenizerMetadataError : public std::runtime_error {
public:
    explicit TokenizerMetadataError(const std::string& message)
        : std::runtime_error("Phase 5B tokenizer metadata validation failed: " + message) {}
};

// Thrown by TokenizerProfile::encode() for input-independent internal
// failures only: invalid UTF-8 (propagated from Stage 2A's
// PretokenizeError) or a BPE final symbol that cannot resolve to a
// vocabulary ID (a fail-closed invariant violation -- never silently
// substituted with an unknown-token ID or byte fallback).
class EncodingError : public std::runtime_error {
public:
    explicit EncodingError(const std::string& message)
        : std::runtime_error("Phase 5B encoding failed: " + message) {}
};

// The two accepted special-token policies (DECISION_LOG.md OE-ADR-030).
// Deliberately named for what they DO, not mirroring the Hugging Face
// `encode_special_tokens` property (whose polarity is the OPPOSITE of
// its name's intuitive reading -- confirmed empirically against the
// pinned oracle: encode_special_tokens=False, the oracle's own default,
// is what RECOGNIZES literal control-token spellings; =True is what
// treats them as ordinary text).
enum class SpecialTokenMode {
    // Every byte of input is ordinary user text, including substrings
    // that spell a CONTROL token exactly (e.g. "<|endoftext|>"). This is
    // the native default -- literal user text must never silently
    // activate control-token semantics.
    LiteralText,
    // Exact CONTROL-token substrings are recognized and emitted as their
    // CONTROL IDs; everything else is ordinary text. Explicit opt-in
    // only -- never the default.
    RecognizeControlTokens,
};

// Matches the two token_type values this pinned profile's GGUF actually
// contains (confirmed distribution: {NORMAL: 49135, CONTROL: 17}).
// Deliberately not a general GGUF token_type enum -- other values (e.g.
// UNKNOWN, BYTE, UNUSED, USER_DEFINED) are out of scope for this one
// profile and are rejected during construction, not represented here.
enum class TokenizerTokenType : int64_t {
    Normal = 1,
    Control = 3,
};

// Immutable tokenizer profile: the vocabulary, merge table, per-token
// types, and BOS/EOS/add-token configuration for exactly the pinned
// SmolLM2-135M tokenizer.ggml.model="gpt2"/pre="smollm" profile.
// Construction validates every invariant in the spec's Section 8; a
// successfully constructed instance is fully valid and never mutated
// afterward. There is no public mutator.
class TokenizerProfile {
public:
    // Validates `artifact.metadata` against the pinned compatibility
    // tuple and constructs the tables. Throws GgufError (from the
    // reused Phase 2 accessors) or TokenizerMetadataError (from this
    // profile's own invariants) on any violation, before returning any
    // object -- there is no partially-constructed result on failure.
    static TokenizerProfile from_gguf_metadata(const GgufArtifact& artifact);

    const std::vector<std::string>& tokens() const { return tokens_; }
    const std::vector<std::string>& merges() const { return merges_; }
    const std::vector<TokenizerTokenType>& token_types() const { return token_types_; }
    int64_t bos_token_id() const { return bos_token_id_; }
    int64_t eos_token_id() const { return eos_token_id_; }
    bool add_bos_token() const { return add_bos_token_; }
    bool add_eos_token() const { return add_eos_token_; }
    size_t vocab_size() const { return tokens_.size(); }

    // Stage 2B: encodes valid UTF-8 text to exact token IDs --
    //   text -> `mode` policy -> Stage 2A pretokens -> GPT-2 byte-to-
    //   Unicode mapping -> ranked BPE merge execution -> vocabulary IDs.
    // Neither mode inserts BOS or EOS (this profile's add_bos_token()/
    // add_eos_token() are both false, validated at construction).
    // Throws PretokenizeError (via Stage 2A) if `utf8_text` is not valid
    // UTF-8 -- no partial result is ever returned. Throws EncodingError
    // if a final BPE symbol cannot resolve to a vocabulary ID (never a
    // silent unknown-token/byte-fallback substitution). Empty input
    // returns an empty vector.
    std::vector<int64_t> encode(std::string_view utf8_text,
                                SpecialTokenMode mode = SpecialTokenMode::LiteralText) const;

private:
    TokenizerProfile() = default;

    std::vector<std::string> tokens_;
    std::vector<std::string> merges_;
    std::vector<TokenizerTokenType> token_types_;
    int64_t bos_token_id_ = 0;
    int64_t eos_token_id_ = 0;
    bool add_bos_token_ = false;
    bool add_eos_token_ = false;

    // Derived lookup structures, built exactly once at construction time
    // (from_gguf_metadata) from the already-validated tables above --
    // never rebuilt per encode() call. Hash maps are used only for
    // exact-key lookup (vocabulary string -> ID, merge pair -> rank);
    // merge SELECTION and CONTROL-token precedence are never decided by
    // unordered-container iteration order (see tokenizer.cpp).
    std::unordered_map<std::string, int64_t> vocab_index_;
    std::unordered_map<std::string, size_t> merge_rank_;  // key: left + '\x01' + right
    // Number of CONTROL tokens (validated by Stage 1 to occupy exactly the
    // contiguous prefix of vocabulary IDs [0, control_count_)). CONTROL
    // token scanning in RecognizeControlTokens mode walks tokens_[0..
    // control_count_) directly in this fixed ID order -- never an
    // unordered_map traversal -- so precedence is never iteration-order
    // dependent.
    size_t control_count_ = 0;
};

// Convenience: index_gguf(path) (already-public Phase 2 entry point)
// followed by TokenizerProfile::from_gguf_metadata(). Provided only to
// remove call-site duplication for the common case of loading directly
// from a GGUF file; does not add any parsing of its own.
TokenizerProfile load_tokenizer_profile(const std::filesystem::path& gguf_path);

}  // namespace orcengine
