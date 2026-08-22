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

// Thrown by TokenizerProfile::encode() only for a fail-closed encoding
// INVARIANT violation: a final BPE symbol that cannot resolve to any
// vocabulary ID (never silently substituted with an unknown-token ID or
// byte fallback). Invalid UTF-8 is a separate, input-dependent condition
// and propagates as Stage 2A's PretokenizeError instead -- callers must
// not conflate the two exception types.
class EncodingError : public std::runtime_error {
public:
    explicit EncodingError(const std::string& message)
        : std::runtime_error("Phase 5B encoding failed: " + message) {}
};

// Thrown by TokenizerProfile::decode()/decode_token_bytes() and by
// Utf8StreamDecoder for any fail-closed decode-boundary violation: a
// token ID outside [0, vocab_size()), or (streaming only) malformed UTF-8
// / an incomplete trailing sequence at end-of-stream. Per Decision
// Register items 7/8 (PHASE5B_TOKENIZER_SPEC.md Section 19, APPROVED
// 2026-08-20): decode never silently substitutes a placeholder token,
// inserts U+FFFD, or discards bytes -- every such condition is an
// explicit error, not a best-effort recovery.
class DecodingError : public std::runtime_error {
public:
    explicit DecodingError(const std::string& message)
        : std::runtime_error("Phase 5B decoding failed: " + message) {}
};

// Whether decode() preserves or strips CONTROL-token text. Deliberately
// separate from encode()'s SpecialTokenMode (Decision Register item 5) --
// encode's policy governs whether literal CONTROL-token SPELLINGS in
// INPUT TEXT are recognized as control tokens; decode's policy governs
// whether already-tokenized CONTROL token IDS are rendered back to text.
// The two are orthogonal questions with independent default answers.
enum class DecodeControlPolicy {
    // Default: every CONTROL token's literal spelling (e.g.
    // "<|endoftext|>") is emitted verbatim, so decode is byte-exact
    // (round-trips exactly) for any token-ID sequence produced by this
    // profile's own encode(), including RecognizeControlTokens output.
    // Matches the pinned oracle's skip_special_tokens=False behavior,
    // NOT its default (skip_special_tokens=True silently drops these
    // substrings) -- a decode boundary defaults to lossless, not to the
    // oracle's own default, per the accepted Decision Register item 5.
    PreserveControlTokens,
    // Explicit opt-in only: CONTROL token IDs contribute zero bytes to
    // the output, for callers that specifically want display text with
    // control-token spellings stripped.
    SkipControlTokens,
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

    // Decodes exactly one token ID to its raw decoded bytes, per `policy`.
    // NORMAL tokens decode through the inverse GPT-2 byte-alphabet mapping
    // (the exact inverse of encode()'s kByteToCodepoint); CONTROL tokens
    // decode to their literal vocabulary spelling verbatim (or to an empty
    // string if `policy` is SkipControlTokens) -- CONTROL token strings
    // are ordinary ASCII text, never byte-alphabet-mapped, both on the
    // encode and decode side. Throws DecodingError if `token_id` is
    // negative or >= vocab_size(). This is the same per-ID logic decode()
    // uses in a loop; exposed as its own method so Utf8StreamDecoder can
    // reuse it without duplicating the CONTROL/NORMAL branching.
    std::string decode_token_bytes(int64_t token_id,
                                   DecodeControlPolicy policy = DecodeControlPolicy::PreserveControlTokens) const;

    // Decodes a complete sequence of token IDs to their exact concatenated
    // raw bytes (the primary correctness result; the returned std::string
    // is a byte container, not necessarily valid UTF-8 on its own if the
    // input token-ID sequence does not correspond to valid UTF-8 as a
    // whole -- interpreting it as UTF-8 text is a secondary view of the
    // same bytes, not a separate decode step). Empty input returns an
    // empty string. Throws DecodingError if ANY token_ids[i] is negative
    // or >= vocab_size() -- validated up front; on failure, no partial
    // result is returned (the exception propagates before any output is
    // constructed for the caller to observe).
    std::string decode(const std::vector<int64_t>& token_ids,
                       DecodeControlPolicy policy = DecodeControlPolicy::PreserveControlTokens) const;

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
    // Precomputed once at construction (Stage decode): decoded_bytes_[id]
    // is the exact raw bytes NORMAL token `id` decodes to (via the inverse
    // byte-alphabet mapping); empty/unused for CONTROL ids, which decode()
    // handles directly from tokens_[id] instead (their spelling IS their
    // literal decoded text, not byte-mapped). Precomputing here means (a)
    // every NORMAL token's decodability through the inverse alphabet is
    // validated ONCE, at construction (fail-closed early, matching every
    // other invariant this profile already validates up front), and (b)
    // decode_token_bytes() is a plain table lookup, never re-deriving the
    // inverse mapping per call.
    std::vector<std::string> normal_decoded_bytes_;
};

// Stateful, single-use incremental UTF-8 decoder: feed token IDs one at a
// time, get back only the COMPLETE, valid UTF-8 bytes ready to emit now;
// an incomplete trailing multi-byte sequence is buffered internally and
// completed by a later feed() call. Reuses TokenizerProfile::decode_token_bytes()
// for the per-token byte conversion -- does not reimplement CONTROL/NORMAL
// handling. Not a callback framework or async stream abstraction -- the
// smallest concrete stateful accumulator this decode boundary needs.
class Utf8StreamDecoder {
public:
    explicit Utf8StreamDecoder(const TokenizerProfile& profile,
                               DecodeControlPolicy policy = DecodeControlPolicy::PreserveControlTokens)
        : profile_(profile), policy_(policy) {}

    // Feeds one token ID. Returns the newly-complete, valid UTF-8 bytes
    // (may be empty, e.g. if the token's bytes only extend a still-
    // incomplete trailing sequence). Throws DecodingError for an invalid
    // token ID or malformed UTF-8 (bad lead/continuation byte, overlong
    // encoding, surrogate codepoint, codepoint > U+10FFFF). After ANY
    // exception from this method, the decoder is POISONED: every
    // subsequent call to feed() or finish() throws DecodingError
    // immediately without touching any buffered state, rather than
    // silently continuing from a possibly-inconsistent position.
    std::string feed(int64_t token_id);

    // Call after the last feed(). Throws DecodingError if an incomplete
    // trailing UTF-8 sequence remains buffered -- end-of-stream with
    // unfinished bytes is an explicit error, never silently discarded or
    // replaced. Also throws (and this call itself does not poison a
    // clean decoder further) if the decoder was already poisoned by an
    // earlier feed() failure.
    void finish();

private:
    const TokenizerProfile& profile_;
    DecodeControlPolicy policy_;
    std::string pending_;
    bool poisoned_ = false;
};

// Convenience: index_gguf(path) (already-public Phase 2 entry point)
// followed by TokenizerProfile::from_gguf_metadata(). Provided only to
// remove call-site duplication for the common case of loading directly
// from a GGUF file; does not add any parsing of its own.
TokenizerProfile load_tokenizer_profile(const std::filesystem::path& gguf_path);

}  // namespace orcengine
