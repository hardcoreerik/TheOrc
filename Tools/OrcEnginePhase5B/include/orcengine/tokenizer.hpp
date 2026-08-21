// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B Stage 1: native GGUF tokenizer-metadata construction and
// fail-closed validation, per docs/OrcEngine/PHASE5B_TOKENIZER_SPEC.md
// and DECISION_LOG.md OE-ADR-030. Constructs and validates the exact
// pinned SmolLM2-135M tokenizer profile (Section 3/8 of the spec) from
// already-parsed GGUF metadata. Does NOT encode, decode, pretokenize,
// or execute BPE merges -- that is Stage 2+ scope, not authorized here.
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

private:
    TokenizerProfile() = default;

    std::vector<std::string> tokens_;
    std::vector<std::string> merges_;
    std::vector<TokenizerTokenType> token_types_;
    int64_t bos_token_id_ = 0;
    int64_t eos_token_id_ = 0;
    bool add_bos_token_ = false;
    bool add_eos_token_ = false;
};

// Convenience: index_gguf(path) (already-public Phase 2 entry point)
// followed by TokenizerProfile::from_gguf_metadata(). Provided only to
// remove call-site duplication for the common case of loading directly
// from a GGUF file; does not add any parsing of its own.
TokenizerProfile load_tokenizer_profile(const std::filesystem::path& gguf_path);

}  // namespace orcengine
