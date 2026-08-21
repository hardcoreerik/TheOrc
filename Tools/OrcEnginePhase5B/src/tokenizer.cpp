// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/tokenizer.hpp"

#include <algorithm>
#include <unordered_set>

namespace orcengine {

namespace {

// Pinned compatibility tuple, per PHASE5B_TOKENIZER_SPEC.md Section 3/8.
// Direct, profile-specific constants -- deliberately not a generic
// schema/config layer (the spec explicitly says not to build one).
constexpr const char* kExpectedModel = "gpt2";
constexpr const char* kExpectedPre = "smollm";
constexpr size_t kExpectedVocabSize = 49152;
constexpr size_t kExpectedMergeCount = 48900;
constexpr size_t kExpectedControlTokenCount = 17;
constexpr int64_t kExpectedBosEosId = 0;

const GgufValue& require_array(const GgufArtifact& artifact, const std::string& key) {
    const GgufValue& value = require_metadata(artifact, key);
    if (value.type != GgufValueType::Array) {
        throw TokenizerMetadataError("metadata key '" + key + "' must be an array");
    }
    return value;
}

bool require_bool(const GgufArtifact& artifact, const std::string& key) {
    const GgufValue& value = require_metadata(artifact, key);
    if (value.type != GgufValueType::Bool) {
        throw TokenizerMetadataError("metadata key '" + key + "' must be boolean");
    }
    return std::get<bool>(value.data);
}

std::vector<std::string> extract_string_array(const GgufArtifact& artifact, const std::string& key) {
    const GgufValue& array_value = require_array(artifact, key);
    const auto& elements = std::get<std::vector<GgufValue>>(array_value.data);
    std::vector<std::string> out;
    out.reserve(elements.size());
    for (size_t i = 0; i < elements.size(); ++i) {
        if (elements[i].type != GgufValueType::String) {
            throw TokenizerMetadataError("metadata key '" + key + "' element " + std::to_string(i) +
                                         " must be a string");
        }
        out.push_back(std::get<std::string>(elements[i].data));
    }
    return out;
}

std::vector<int64_t> extract_int_array(const GgufArtifact& artifact, const std::string& key) {
    const GgufValue& array_value = require_array(artifact, key);
    const auto& elements = std::get<std::vector<GgufValue>>(array_value.data);
    std::vector<int64_t> out;
    out.reserve(elements.size());
    for (size_t i = 0; i < elements.size(); ++i) {
        switch (elements[i].type) {
            case GgufValueType::Int8:
            case GgufValueType::Int16:
            case GgufValueType::Int32:
            case GgufValueType::Int64:
                out.push_back(std::get<int64_t>(elements[i].data));
                break;
            default:
                throw TokenizerMetadataError("metadata key '" + key + "' element " + std::to_string(i) +
                                             " must be a signed integer");
        }
    }
    return out;
}

}  // namespace

TokenizerProfile TokenizerProfile::from_gguf_metadata(const GgufArtifact& artifact) {
    // --- Model/pre identity: reject anything but the exact pinned pair. ---
    const std::string model = metadata_string(artifact, "tokenizer.ggml.model");
    if (model.empty()) throw TokenizerMetadataError("tokenizer.ggml.model must not be empty");
    if (model != kExpectedModel) {
        throw TokenizerMetadataError("unsupported tokenizer.ggml.model '" + model +
                                     "' -- Phase 5B Stage 1 supports only '" +
                                     std::string(kExpectedModel) + "'");
    }
    const std::string pre = metadata_string(artifact, "tokenizer.ggml.pre");
    if (pre.empty()) throw TokenizerMetadataError("tokenizer.ggml.pre must not be empty");
    if (pre != kExpectedPre) {
        throw TokenizerMetadataError("unsupported tokenizer.ggml.pre '" + pre +
                                     "' -- Phase 5B Stage 1 supports only '" +
                                     std::string(kExpectedPre) + "'");
    }

    // --- Vocabulary. ---
    std::vector<std::string> tokens = extract_string_array(artifact, "tokenizer.ggml.tokens");
    if (tokens.size() != kExpectedVocabSize) {
        throw TokenizerMetadataError("tokenizer.ggml.tokens has " + std::to_string(tokens.size()) +
                                     " entries, expected exactly " + std::to_string(kExpectedVocabSize));
    }
    std::unordered_set<std::string> vocab_set;
    vocab_set.reserve(tokens.size());
    for (size_t i = 0; i < tokens.size(); ++i) {
        if (tokens[i].empty()) {
            throw TokenizerMetadataError("tokenizer.ggml.tokens[" + std::to_string(i) + "] is empty");
        }
        if (!vocab_set.insert(tokens[i]).second) {
            throw TokenizerMetadataError("tokenizer.ggml.tokens contains a duplicate entry: '" +
                                         tokens[i] + "' (index " + std::to_string(i) +
                                         ") -- duplicate vocabulary tokens make merge resolution ambiguous");
        }
    }

    // --- Token types. ---
    const std::vector<int64_t> raw_token_types = extract_int_array(artifact, "tokenizer.ggml.token_type");
    if (raw_token_types.size() != tokens.size()) {
        throw TokenizerMetadataError("tokenizer.ggml.token_type has " +
                                     std::to_string(raw_token_types.size()) +
                                     " entries, expected exactly " + std::to_string(tokens.size()) +
                                     " (must match tokenizer.ggml.tokens length)");
    }
    std::vector<TokenizerTokenType> token_types;
    token_types.reserve(raw_token_types.size());
    std::vector<size_t> control_indices;
    for (size_t i = 0; i < raw_token_types.size(); ++i) {
        const int64_t raw = raw_token_types[i];
        if (raw == static_cast<int64_t>(TokenizerTokenType::Normal)) {
            token_types.push_back(TokenizerTokenType::Normal);
        } else if (raw == static_cast<int64_t>(TokenizerTokenType::Control)) {
            token_types.push_back(TokenizerTokenType::Control);
            control_indices.push_back(i);
        } else {
            throw TokenizerMetadataError("tokenizer.ggml.token_type[" + std::to_string(i) + "] = " +
                                         std::to_string(raw) +
                                         " is outside the permitted set {1 (NORMAL), 3 (CONTROL)} for this profile");
        }
    }
    if (control_indices.size() != kExpectedControlTokenCount) {
        throw TokenizerMetadataError("found " + std::to_string(control_indices.size()) +
                                     " CONTROL tokens, expected exactly " +
                                     std::to_string(kExpectedControlTokenCount));
    }
    for (size_t i = 0; i < control_indices.size(); ++i) {
        if (control_indices[i] != i) {
            throw TokenizerMetadataError("CONTROL tokens are not exactly IDs 0-" +
                                         std::to_string(kExpectedControlTokenCount - 1) +
                                         " -- found a CONTROL token at unexpected index " +
                                         std::to_string(control_indices[i]));
        }
    }

    // --- Merges. ---
    std::vector<std::string> merges = extract_string_array(artifact, "tokenizer.ggml.merges");
    if (merges.size() != kExpectedMergeCount) {
        throw TokenizerMetadataError("tokenizer.ggml.merges has " + std::to_string(merges.size()) +
                                     " entries, expected exactly " + std::to_string(kExpectedMergeCount));
    }
    std::unordered_set<std::string> merge_set;
    merge_set.reserve(merges.size());
    for (size_t i = 0; i < merges.size(); ++i) {
        const std::string& merge = merges[i];
        const size_t space = merge.find(' ');
        if (space == std::string::npos || space == 0 || space == merge.size() - 1 ||
            merge.find(' ', space + 1) != std::string::npos) {
            throw TokenizerMetadataError("tokenizer.ggml.merges[" + std::to_string(i) + "] ('" + merge +
                                         "') does not contain exactly two space-separated components");
        }
        const std::string left = merge.substr(0, space);
        const std::string right = merge.substr(space + 1);
        if (vocab_set.find(left) == vocab_set.end()) {
            throw TokenizerMetadataError("tokenizer.ggml.merges[" + std::to_string(i) +
                                         "]'s left component '" + left +
                                         "' cannot be resolved against the vocabulary");
        }
        if (vocab_set.find(right) == vocab_set.end()) {
            throw TokenizerMetadataError("tokenizer.ggml.merges[" + std::to_string(i) +
                                         "]'s right component '" + right +
                                         "' cannot be resolved against the vocabulary");
        }
        // The merged RESULT (concatenation, no separator) must also exist in the
        // vocabulary and resolve unambiguously to a token ID. Confirmed empirically
        // against the canonical tokenizer-bearing explicit GGUF (smollm2-135m.gguf)
        // before adding this check: all 48,900 of its real merges satisfy
        // left+right -- exists in vocab with zero exceptions, so this is enforced
        // as a hard invariant, not a best-effort heuristic. The legacy tied
        // artifact (smollm2-135m-tied.gguf) carries no tokenizer.ggml.merges or
        // any other tokenizer.ggml.* metadata at all (see OE-ADR-031) and was not
        // and could not be part of this confirmation -- it is tested separately
        // for fail-closed rejection, not for merge-result resolution.
        const std::string merged = left + right;
        const auto merged_it = vocab_set.find(merged);
        if (merged_it == vocab_set.end()) {
            throw TokenizerMetadataError("tokenizer.ggml.merges[" + std::to_string(i) +
                                         "]'s merged result '" + merged +
                                         "' (from left '" + left + "' + right '" + right +
                                         "') does not exist in tokenizer.ggml.tokens");
        }
        if (!merge_set.insert(merge).second) {
            throw TokenizerMetadataError("tokenizer.ggml.merges contains a duplicate or ambiguous entry: '" +
                                         merge + "' (index " + std::to_string(i) + ")");
        }
    }

    // --- BOS/EOS/add-token configuration. ---
    const uint64_t bos_raw = metadata_u64(artifact, "tokenizer.ggml.bos_token_id");
    const uint64_t eos_raw = metadata_u64(artifact, "tokenizer.ggml.eos_token_id");
    if (bos_raw >= tokens.size()) {
        throw TokenizerMetadataError("tokenizer.ggml.bos_token_id (" + std::to_string(bos_raw) +
                                     ") is outside the vocabulary [0, " + std::to_string(tokens.size()) + ")");
    }
    if (eos_raw >= tokens.size()) {
        throw TokenizerMetadataError("tokenizer.ggml.eos_token_id (" + std::to_string(eos_raw) +
                                     ") is outside the vocabulary [0, " + std::to_string(tokens.size()) + ")");
    }
    if (static_cast<int64_t>(bos_raw) != kExpectedBosEosId) {
        throw TokenizerMetadataError("tokenizer.ggml.bos_token_id must be exactly " +
                                     std::to_string(kExpectedBosEosId) + " for this pinned profile, got " +
                                     std::to_string(bos_raw));
    }
    if (static_cast<int64_t>(eos_raw) != kExpectedBosEosId) {
        throw TokenizerMetadataError("tokenizer.ggml.eos_token_id must be exactly " +
                                     std::to_string(kExpectedBosEosId) + " for this pinned profile, got " +
                                     std::to_string(eos_raw));
    }

    const bool add_bos = require_bool(artifact, "tokenizer.ggml.add_bos_token");
    if (add_bos) {
        throw TokenizerMetadataError("tokenizer.ggml.add_bos_token must be false for this pinned profile");
    }
    const bool add_eos = require_bool(artifact, "tokenizer.ggml.add_eos_token");
    if (add_eos) {
        throw TokenizerMetadataError("tokenizer.ggml.add_eos_token must be false for this pinned profile");
    }

    // All invariants held -- construct the immutable result. No partially
    // valid TokenizerProfile is ever returned on any earlier throw above.
    TokenizerProfile profile;
    profile.tokens_ = std::move(tokens);
    profile.merges_ = std::move(merges);
    profile.token_types_ = std::move(token_types);
    profile.bos_token_id_ = static_cast<int64_t>(bos_raw);
    profile.eos_token_id_ = static_cast<int64_t>(eos_raw);
    profile.add_bos_token_ = add_bos;
    profile.add_eos_token_ = add_eos;
    return profile;
}

TokenizerProfile load_tokenizer_profile(const std::filesystem::path& gguf_path) {
    const GgufArtifact artifact = index_gguf(gguf_path);
    return TokenizerProfile::from_gguf_metadata(artifact);
}

}  // namespace orcengine
