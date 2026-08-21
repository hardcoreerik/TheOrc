// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5B Stage 2A: exact native pretokenization only, per
// docs/OrcEngine/PHASE5B_TOKENIZER_SPEC.md and DECISION_LOG.md OE-ADR-032.
// Reproduces the pinned SmolLM2 tokenizer's exact
//   Digits(individual_digits=true) -> ByteLevel(add_prefix_space=false,
//   trim_offsets=true, use_regex=true)
// pretokenization sequence, established and empirically validated against
// the live tokenizers==0.22.2 oracle (see
// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py). `pretokenize()`
// itself produces byte RANGE boundaries only -- no byte-to-Unicode alphabet
// remapping, no BPE merge execution, no token-ID production happen in this
// file. Those are implemented as Stage 2B (DECISION_LOG.md OE-ADR-034),
// consuming this file's output: see `TokenizerProfile::encode()` in
// orcengine/tokenizer.hpp, which calls `pretokenize()` internally. Decoding
// remains a separate, not-yet-authorized stage.
#pragma once

#include <cstddef>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace orcengine {

// Thrown when input is not valid UTF-8. Never partially processed -- no
// spans are returned on failure, and malformed bytes are never replaced
// with U+FFFD or silently skipped.
class PretokenizeError : public std::runtime_error {
public:
    explicit PretokenizeError(const std::string& message)
        : std::runtime_error("Phase 5B pretokenization failed: " + message) {}
};

// A single pretoken as a byte range [begin, end) into the original input.
struct PretokenSpan {
    std::size_t begin;
    std::size_t end;
};

// Splits valid UTF-8 `utf8_text` into pretoken byte-range boundaries using
// the pinned tokenizer's exact two-stage sequence (see file comment).
// Embedded NUL bytes are valid input and are never used to truncate the
// scan -- `utf8_text` is a `string_view`, not a NUL-terminated C string.
// Empty input returns an empty vector. Throws PretokenizeError if
// `utf8_text` is not valid UTF-8 (invalid leading/continuation bytes,
// truncated sequences, overlong encodings, encoded surrogates, or
// codepoints above U+10FFFF are all rejected explicitly).
std::vector<PretokenSpan> pretokenize(std::string_view utf8_text);

}  // namespace orcengine
