// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/tokenizer.hpp"

#include <algorithm>
#include <array>
#include <cstdio>
#include <limits>
#include <unordered_set>

#include "orcengine/pretokenize.hpp"

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

// Merge-rank map key: an unambiguous join of the two components. '\x01' is
// an ENFORCED reserved separator, not merely an assumed-safe one: every
// tokenizer.ggml.tokens entry is explicitly rejected at the GGUF trust
// boundary (from_gguf_metadata(), below) if it contains a raw 0x01 byte,
// specifically so this join can never collide -- every merge component was
// already separately validated (Stage 1) to resolve against
// tokenizer.ggml.tokens, so no successfully constructed TokenizerProfile can
// ever reach this function with a component containing 0x01.
std::string merge_key(std::string_view left, std::string_view right) {
    std::string key;
    key.reserve(left.size() + 1 + right.size());
    key.append(left);
    key.push_back('\x01');
    key.append(right);
    return key;
}

// GPT-2's closed-form byte_encoder: bytes in [0x21,0x7E]∪[0xA1,0xAC]∪
// [0xAE,0xFF] map to themselves; the remaining 68 "unprintable" byte values
// map to 0x100+n in ascending byte-value order. This same closed-form
// construction was validated byte-for-byte against the live
// tokenizers==0.22.2 oracle in Python (DECISION_LOG.md OE-ADR-034; see
// generate_pretok_tables.py's build_byte_to_unicode()/
// verify_byte_to_unicode()) before these 256 literal values were written
// here. tests/test_encode.cpp's compiled-in proof cross-checks an
// INDEPENDENT C++ reconstruction of this same algorithm against the
// oracle-generated table -- it does not call into this private array
// directly (this array is not reachable outside this translation unit).
// Not exposed as a general Unicode facility -- used only to map the raw
// bytes of one Stage 2A pretoken span before BPE.
constexpr std::array<uint32_t, 256> kByteToCodepoint = {
    0x100, 0x101, 0x102, 0x103, 0x104, 0x105, 0x106, 0x107, 0x108, 0x109, 0x10A, 0x10B, 0x10C, 0x10D, 0x10E, 0x10F,
    0x110, 0x111, 0x112, 0x113, 0x114, 0x115, 0x116, 0x117, 0x118, 0x119, 0x11A, 0x11B, 0x11C, 0x11D, 0x11E, 0x11F,
    0x120, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
    0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
    0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C, 0x4D, 0x4E, 0x4F,
    0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F,
    0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
    0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B, 0x7C, 0x7D, 0x7E, 0x121,
    0x122, 0x123, 0x124, 0x125, 0x126, 0x127, 0x128, 0x129, 0x12A, 0x12B, 0x12C, 0x12D, 0x12E, 0x12F, 0x130, 0x131,
    0x132, 0x133, 0x134, 0x135, 0x136, 0x137, 0x138, 0x139, 0x13A, 0x13B, 0x13C, 0x13D, 0x13E, 0x13F, 0x140, 0x141,
    0x142, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0x143, 0xAE, 0xAF,
    0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF,
    0xC0, 0xC1, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF,
    0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xDB, 0xDC, 0xDD, 0xDE, 0xDF,
    0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xEB, 0xEC, 0xED, 0xEE, 0xEF,
    0xF0, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA, 0xFB, 0xFC, 0xFD, 0xFE, 0xFF,
};

// UTF-8-encodes one codepoint from kByteToCodepoint's range (max 0x143, so
// at most 2 bytes are ever produced -- a general encoder is not needed).
std::string utf8_encode_codepoint(uint32_t cp) {
    std::string out;
    if (cp < 0x80) {
        out.push_back(static_cast<char>(cp));
    } else {
        out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
    return out;
}

// Inverse of kByteToCodepoint: kCodepointToByte[cp] is the raw byte value
// b such that kByteToCodepoint[b] == cp, or -1 if cp is not in
// kByteToCodepoint's image. kByteToCodepoint's values never exceed 0x143
// (see its own comment), so this table need only cover [0, 0x144).
// Bijective by construction (kByteToCodepoint is a bijection over 256
// distinct byte values onto 256 distinct codepoints), built once here
// rather than searched linearly per decode.
std::array<int32_t, 0x144> build_codepoint_to_byte() {
    std::array<int32_t, 0x144> table;
    table.fill(-1);
    for (int b = 0; b < 256; ++b) {
        table[kByteToCodepoint[static_cast<size_t>(b)]] = b;
    }
    return table;
}
const std::array<int32_t, 0x144> kCodepointToByte = build_codepoint_to_byte();

// Decodes one NORMAL vocabulary token's UTF-8 string back to raw bytes via
// the inverse byte-alphabet mapping. Every codepoint in a NORMAL token's
// spelling is, by construction of the encode-side alphabet, either a
// single ASCII byte (< 0x80, self-mapped) or a 2-byte UTF-8 sequence
// encoding a codepoint in [0x80, 0x143] (see utf8_encode_codepoint(),
// which never produces a 3- or 4-byte form for this alphabet's range) --
// so this decoder only ever needs to handle those two forms; anything
// else (a 3/4-byte lead byte, a bad continuation byte, or a codepoint
// outside the alphabet's image) means this vocabulary token cannot
// possibly have come from this alphabet, and is a construction-time
// TokenizerMetadataError, not a per-call decode failure.
std::string decode_normal_token_via_alphabet(std::string_view token, size_t token_index) {
    std::string out;
    out.reserve(token.size());
    size_t i = 0;
    while (i < token.size()) {
        const unsigned char b0 = static_cast<unsigned char>(token[i]);
        uint32_t cp;
        size_t len;
        if (b0 < 0x80) {
            cp = b0;
            len = 1;
        } else if ((b0 & 0xE0) == 0xC0) {
            if (i + 1 >= token.size()) {
                throw TokenizerMetadataError("tokenizer.ggml.tokens[" + std::to_string(token_index) +
                                             "] ends mid-UTF-8-sequence -- not decodable through the GPT-2 "
                                             "byte-alphabet mapping");
            }
            const unsigned char b1 = static_cast<unsigned char>(token[i + 1]);
            if ((b1 & 0xC0) != 0x80) {
                throw TokenizerMetadataError("tokenizer.ggml.tokens[" + std::to_string(token_index) +
                                             "] contains an invalid UTF-8 continuation byte -- not decodable "
                                             "through the GPT-2 byte-alphabet mapping");
            }
            cp = (static_cast<uint32_t>(b0 & 0x1F) << 6) | (b1 & 0x3F);
            len = 2;
        } else {
            throw TokenizerMetadataError("tokenizer.ggml.tokens[" + std::to_string(token_index) +
                                         "] contains a codepoint outside the GPT-2 byte-alphabet's range "
                                         "(3/4-byte UTF-8 lead byte encountered) -- not decodable through the "
                                         "inverse byte-alphabet mapping");
        }
        if (cp >= kCodepointToByte.size() || kCodepointToByte[cp] < 0) {
            throw TokenizerMetadataError("tokenizer.ggml.tokens[" + std::to_string(token_index) +
                                         "] contains codepoint U+" +
                                         [&] { char buf[8]; std::snprintf(buf, sizeof(buf), "%04X", cp); return std::string(buf); }() +
                                         ", which is not in the GPT-2 byte-alphabet's image -- not decodable "
                                         "through the inverse byte-alphabet mapping");
        }
        out.push_back(static_cast<char>(static_cast<unsigned char>(kCodepointToByte[cp])));
        i += len;
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
        // Reserved for merge_rank_'s internal key join (see merge_key()
        // below); rejecting it here at the trust boundary makes that join's
        // "canonical tokens never contain 0x01" assumption an ENFORCED
        // invariant of every successfully constructed profile, not merely a
        // documented one.
        if (tokens[i].find('\x01') != std::string::npos) {
            throw TokenizerMetadataError("tokenizer.ggml.tokens[" + std::to_string(i) + "] ('" + tokens[i] +
                                         "') contains the reserved byte 0x01, which Stage 2B's internal "
                                         "merge-rank key join requires to never appear in a vocabulary token");
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
    // encode()'s RecognizeControlTokens scan walks tokens_[0..control_count_)
    // in fixed ID order and relies on the property that no CONTROL spelling
    // is a literal prefix of another (in EITHER direction) -- otherwise the
    // fixed scan order would silently decide a real precedence ambiguity
    // instead of merely being iteration-order-independent bookkeeping.
    // Enforced here, at construction, rather than only documented: checked
    // over exactly the 17 validated CONTROL entries, not a generic
    // added-token framework.
    for (size_t i = 0; i < control_indices.size(); ++i) {
        for (size_t j = 0; j < control_indices.size(); ++j) {
            if (i == j) continue;
            const std::string& a = tokens[i];
            const std::string& b = tokens[j];
            if (a.size() < b.size() && b.compare(0, a.size(), a) == 0) {
                throw TokenizerMetadataError("CONTROL token '" + a + "' (index " + std::to_string(i) +
                                             ") is a literal prefix of CONTROL token '" + b + "' (index " +
                                             std::to_string(j) +
                                             ") -- RecognizeControlTokens scanning requires no CONTROL "
                                             "spelling to prefix another");
            }
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

    // --- Derived lookup structures (Stage 2B), built exactly once here. ---
    profile.vocab_index_.reserve(profile.tokens_.size());
    for (size_t i = 0; i < profile.tokens_.size(); ++i) {
        profile.vocab_index_.emplace(profile.tokens_[i], static_cast<int64_t>(i));
    }

    profile.merge_rank_.reserve(profile.merges_.size());
    for (size_t i = 0; i < profile.merges_.size(); ++i) {
        const std::string& m = profile.merges_[i];
        const size_t space = m.find(' ');  // already validated to exist exactly once
        profile.merge_rank_.emplace(merge_key(std::string_view(m).substr(0, space),
                                              std::string_view(m).substr(space + 1)),
                                    i);
    }

    // CONTROL token count, derived directly from validated token_types_ --
    // never a separate hard-coded 17-entry list. Stage 1 already validated
    // CONTROL tokens occupy exactly a contiguous prefix of IDs starting at 0.
    size_t control_count = 0;
    while (control_count < profile.token_types_.size() &&
           profile.token_types_[control_count] == TokenizerTokenType::Control) {
        ++control_count;
    }
    profile.control_count_ = control_count;

    // Decode-side derived table: for every NORMAL token, precompute (and
    // validate, fail-closed) its raw decoded bytes through the inverse
    // byte-alphabet mapping. CONTROL tokens' entries are left empty --
    // decode_token_bytes() reads tokens_[id] directly for those, they are
    // never byte-alphabet-mapped.
    profile.normal_decoded_bytes_.resize(profile.tokens_.size());
    for (size_t i = 0; i < profile.tokens_.size(); ++i) {
        if (profile.token_types_[i] == TokenizerTokenType::Normal) {
            profile.normal_decoded_bytes_[i] = decode_normal_token_via_alphabet(profile.tokens_[i], i);
        }
    }

    return profile;
}

TokenizerProfile load_tokenizer_profile(const std::filesystem::path& gguf_path) {
    const GgufArtifact artifact = index_gguf(gguf_path);
    return TokenizerProfile::from_gguf_metadata(artifact);
}

namespace {

// Runs the classic GPT-2 BPE algorithm on one mapped pretoken's initial
// per-byte symbols: repeatedly find the adjacent pair with the lowest
// (best) merge rank among those PRESENT in `merge_rank`, merge every
// non-overlapping occurrence of that exact pair left-to-right in one pass,
// and repeat until no adjacent pair has any rank. Merge ranks are unique
// per pair (Stage 1 validated no duplicate merge entries), so there is
// never a tie to break -- only which PAIR is currently the best-ranked one
// present, which is a plain deterministic minimum over the (few) adjacent
// pairs actually in the current symbol list, never decided by hash-map
// iteration order.
std::vector<std::string> run_bpe(std::vector<std::string> symbols,
                                 const std::unordered_map<std::string, size_t>& merge_rank) {
    if (symbols.size() < 2) return symbols;
    for (;;) {
        size_t best_rank = std::numeric_limits<size_t>::max();
        size_t best_pos = std::numeric_limits<size_t>::max();
        for (size_t i = 0; i + 1 < symbols.size(); ++i) {
            const auto it = merge_rank.find(merge_key(symbols[i], symbols[i + 1]));
            if (it != merge_rank.end() && it->second < best_rank) {
                best_rank = it->second;
                best_pos = i;
            }
        }
        if (best_pos == std::numeric_limits<size_t>::max()) break;  // no mergeable adjacent pair remains

        const std::string winning_left = symbols[best_pos];
        const std::string winning_right = symbols[best_pos + 1];
        std::vector<std::string> merged;
        merged.reserve(symbols.size());
        size_t i = 0;
        while (i < symbols.size()) {
            if (i + 1 < symbols.size() && symbols[i] == winning_left && symbols[i + 1] == winning_right) {
                merged.push_back(winning_left + winning_right);
                i += 2;
            } else {
                merged.push_back(std::move(symbols[i]));
                ++i;
            }
        }
        symbols = std::move(merged);
        if (symbols.size() < 2) break;
    }
    return symbols;
}

// Encodes one Stage 2A pretoken (a raw UTF-8 byte span, no special-token
// meaning) to token IDs: map each raw byte through the GPT-2 alphabet,
// run BPE, then resolve every final symbol to a vocabulary ID.
void encode_pretoken(std::string_view raw_bytes, const std::unordered_map<std::string, int64_t>& vocab_index,
                     const std::unordered_map<std::string, size_t>& merge_rank, std::vector<int64_t>& out) {
    std::vector<std::string> symbols;
    symbols.reserve(raw_bytes.size());
    for (unsigned char b : raw_bytes) {
        symbols.push_back(utf8_encode_codepoint(kByteToCodepoint[b]));
    }
    const std::vector<std::string> resolved = run_bpe(std::move(symbols), merge_rank);
    out.reserve(out.size() + resolved.size());
    for (const std::string& symbol : resolved) {
        const auto it = vocab_index.find(symbol);
        if (it == vocab_index.end()) {
            throw EncodingError("final BPE symbol '" + symbol +
                                "' does not resolve to any vocabulary ID -- no unknown-token or byte "
                                "fallback exists for this profile");
        }
        out.push_back(it->second);
    }
}

}  // namespace

std::vector<int64_t> TokenizerProfile::encode(std::string_view utf8_text, SpecialTokenMode mode) const {
    std::vector<int64_t> ids;
    if (utf8_text.empty()) return ids;

    if (mode == SpecialTokenMode::LiteralText || control_count_ == 0) {
        for (const PretokenSpan& span : pretokenize(utf8_text)) {
            encode_pretoken(utf8_text.substr(span.begin, span.end - span.begin), vocab_index_, merge_rank_, ids);
        }
        return ids;
    }

    // RecognizeControlTokens: scan left to right. At each position, check
    // tokens_[0..control_count_) -- the validated CONTROL prefix, walked in
    // fixed ID order, never an unordered_map traversal -- for an exact
    // substring match. No two CONTROL strings are a literal prefix of one
    // another for this pinned profile (confirmed empirically against the
    // oracle; see DECISION_LOG.md OE-ADR-034), so at most one can ever
    // match at a given position and the fixed scan order does not affect
    // which one is found -- it exists only to keep the implementation free
    // of any container-iteration-order dependency. Ordinary spans between/
    // around recognized CONTROL tokens are handed to Stage 2A + BPE exactly
    // as in LiteralText mode.
    size_t ordinary_begin = 0;
    size_t i = 0;
    const size_t n = utf8_text.size();
    auto flush_ordinary = [&](size_t end) {
        if (end > ordinary_begin) {
            for (const PretokenSpan& span : pretokenize(utf8_text.substr(ordinary_begin, end - ordinary_begin))) {
                encode_pretoken(utf8_text.substr(ordinary_begin + span.begin, span.end - span.begin), vocab_index_,
                                merge_rank_, ids);
            }
        }
    };
    while (i < n) {
        bool matched = false;
        for (size_t control_id = 0; control_id < control_count_; ++control_id) {
            const std::string& control_str = tokens_[control_id];
            const size_t len = control_str.size();
            if (i + len <= n && utf8_text.substr(i, len) == control_str) {
                flush_ordinary(i);
                ids.push_back(static_cast<int64_t>(control_id));
                i += len;
                ordinary_begin = i;
                matched = true;
                break;
            }
        }
        if (!matched) ++i;
    }
    flush_ordinary(n);
    return ids;
}

std::string TokenizerProfile::decode_token_bytes(int64_t token_id, DecodeControlPolicy policy) const {
    if (token_id < 0 || static_cast<size_t>(token_id) >= tokens_.size()) {
        throw DecodingError("token ID " + std::to_string(token_id) + " is outside the vocabulary [0, " +
                            std::to_string(tokens_.size()) + ")");
    }
    const size_t id = static_cast<size_t>(token_id);
    if (token_types_[id] == TokenizerTokenType::Control) {
        if (policy == DecodeControlPolicy::SkipControlTokens) return std::string();
        return tokens_[id];  // CONTROL spellings are literal text, not byte-mapped
    }
    return normal_decoded_bytes_[id];
}

std::string TokenizerProfile::decode(const std::vector<int64_t>& token_ids, DecodeControlPolicy policy) const {
    // Validate every ID before appending any output, so a rejection never
    // leaves a caller with a partial result to accidentally observe --
    // decode_token_bytes() itself already validates per call, so a single
    // pass that accumulates into a local (not caller-visible) buffer and
    // only returns on full success already satisfies this: on throw, the
    // local `out` is destroyed with the exception, never returned.
    std::string out;
    for (int64_t id : token_ids) {
        out += decode_token_bytes(id, policy);
    }
    return out;
}

namespace {

// Scans `data` for the longest prefix consisting only of COMPLETE, valid
// UTF-8 sequences. A well-formed but not-yet-complete trailing sequence
// (a valid lead byte followed by zero or more valid continuation bytes,
// short of the length its lead byte demands) is deliberately excluded
// from the returned prefix -- Utf8StreamDecoder buffers it for a later
// feed() call, not this function's concern. Throws DecodingError for any
// genuinely malformed byte sequence within the scanned prefix: an invalid
// lead byte, a continuation byte that fails the 10xxxxxx pattern
// (including in what would otherwise look like an incomplete trailing
// sequence -- a structurally wrong continuation byte is never "maybe
// still incomplete"), an overlong encoding, a UTF-16 surrogate codepoint
// (U+D800-U+DFFF), or a codepoint exceeding U+10FFFF.
size_t longest_complete_utf8_prefix(std::string_view data) {
    size_t i = 0;
    size_t complete_end = 0;
    while (i < data.size()) {
        const unsigned char b0 = static_cast<unsigned char>(data[i]);
        size_t len;
        uint32_t min_cp;
        uint32_t cp;
        if (b0 < 0x80) {
            len = 1;
            min_cp = 0;
            cp = b0;
        } else if ((b0 & 0xE0) == 0xC0) {
            len = 2;
            min_cp = 0x80;
            cp = b0 & 0x1Fu;
        } else if ((b0 & 0xF0) == 0xE0) {
            len = 3;
            min_cp = 0x800;
            cp = b0 & 0x0Fu;
        } else if ((b0 & 0xF8) == 0xF0) {
            len = 4;
            min_cp = 0x10000;
            cp = b0 & 0x07u;
        } else {
            throw DecodingError("invalid UTF-8 lead byte at stream offset " + std::to_string(i));
        }
        if (i + len > data.size()) {
            // Possibly-incomplete trailing sequence -- the continuation
            // bytes actually present so far must still be structurally
            // valid, or this is a real error, not merely "not yet enough
            // bytes".
            for (size_t k = i + 1; k < data.size(); ++k) {
                if ((static_cast<unsigned char>(data[k]) & 0xC0) != 0x80) {
                    throw DecodingError("invalid UTF-8 continuation byte at stream offset " + std::to_string(k));
                }
            }
            break;
        }
        for (size_t k = 1; k < len; ++k) {
            const unsigned char c = static_cast<unsigned char>(data[i + k]);
            if ((c & 0xC0) != 0x80) {
                throw DecodingError("invalid UTF-8 continuation byte at stream offset " + std::to_string(i + k));
            }
            cp = (cp << 6) | (c & 0x3Fu);
        }
        if (cp < min_cp) {
            throw DecodingError("overlong UTF-8 encoding at stream offset " + std::to_string(i));
        }
        if (cp >= 0xD800 && cp <= 0xDFFF) {
            throw DecodingError("UTF-8 sequence encodes a surrogate codepoint at stream offset " + std::to_string(i));
        }
        if (cp > 0x10FFFF) {
            throw DecodingError("UTF-8 sequence encodes a codepoint beyond U+10FFFF at stream offset " +
                                std::to_string(i));
        }
        i += len;
        complete_end = i;
    }
    return complete_end;
}

}  // namespace

std::string Utf8StreamDecoder::feed(int64_t token_id) {
    if (poisoned_) {
        throw DecodingError("Utf8StreamDecoder::feed() called after a previous error -- this decoder is "
                            "poisoned and must not be reused");
    }
    try {
        pending_ += profile_.decode_token_bytes(token_id, policy_);
        const size_t complete_len = longest_complete_utf8_prefix(pending_);
        std::string emit = pending_.substr(0, complete_len);
        pending_.erase(0, complete_len);
        return emit;
    } catch (...) {
        poisoned_ = true;
        throw;
    }
}

void Utf8StreamDecoder::finish() {
    if (poisoned_) {
        throw DecodingError("Utf8StreamDecoder::finish() called on a decoder already poisoned by a previous "
                            "feed() error");
    }
    if (!pending_.empty()) {
        poisoned_ = true;
        throw DecodingError("Utf8StreamDecoder::finish(): " + std::to_string(pending_.size()) +
                            " incomplete trailing UTF-8 byte(s) remain buffered at end-of-stream");
    }
}

}  // namespace orcengine
