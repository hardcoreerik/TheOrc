// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// See include/orcengine/pretokenize.hpp for scope. Implements the exact
// two-stage Digits->ByteLevel algorithm established and validated in
// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.
#include "orcengine/pretokenize.hpp"

#include <array>
#include <string>

#include "orcengine/pretok_tables.hpp"

namespace orcengine {
namespace {

enum class Class : std::uint8_t { Letter, Number, WhiteSpace, Other };

bool in_ranges(const pretok_tables::CodepointRange* ranges, std::size_t count, std::uint32_t cp) {
    std::size_t lo = 0, hi = count;
    while (lo < hi) {
        const std::size_t mid = lo + (hi - lo) / 2;
        if (cp < ranges[mid].first) {
            hi = mid;
        } else if (cp > ranges[mid].last) {
            lo = mid + 1;
        } else {
            return true;
        }
    }
    return false;
}

// Classification priority (WhiteSpace checked first) doesn't affect
// correctness here -- the three candidate tables are disjoint by
// construction (validated in the generator) -- but checking the smallest
// table (WhiteSpace, 10 ranges) first is a trivial, harmless optimization.
Class classify(std::uint32_t cp) {
    using namespace pretok_tables;
    if (in_ranges(kWhiteSpace, kWhiteSpaceCount, cp)) return Class::WhiteSpace;
    if (in_ranges(kLetter, kLetterCount, cp)) return Class::Letter;
    if (in_ranges(kNumber, kNumberCount, cp)) return Class::Number;
    return Class::Other;
}

struct DecodedCodepoint {
    std::uint32_t cp;
    std::size_t byte_offset;
    std::size_t byte_len;
};

// Strict UTF-8 decode: rejects invalid leading/continuation bytes,
// truncated sequences, overlong encodings, encoded surrogates, and
// codepoints above U+10FFFF. Never substitutes U+FFFD. Embedded NUL is a
// perfectly ordinary 1-byte codepoint (0x00 < 0x80) and is preserved like
// any other byte -- `text` is a string_view, never NUL-scanned.
std::vector<DecodedCodepoint> decode_utf8_strict(std::string_view text) {
    std::vector<DecodedCodepoint> out;
    out.reserve(text.size());
    const std::size_t n = text.size();
    std::size_t i = 0;
    auto byte_at = [&](std::size_t idx) -> std::uint8_t { return static_cast<std::uint8_t>(text[idx]); };
    auto require_continuation = [&](std::size_t idx) {
        if (idx >= n) {
            throw PretokenizeError("truncated UTF-8 sequence at offset " + std::to_string(i));
        }
        if ((byte_at(idx) & 0xC0) != 0x80) {
            throw PretokenizeError("invalid UTF-8 continuation byte at offset " + std::to_string(idx));
        }
    };
    while (i < n) {
        const std::uint8_t b0 = byte_at(i);
        std::uint32_t cp;
        std::size_t len;
        if (b0 < 0x80) {
            cp = b0;
            len = 1;
        } else if ((b0 & 0xE0) == 0xC0) {
            len = 2;
            require_continuation(i + 1);
            cp = ((b0 & 0x1Fu) << 6) | (byte_at(i + 1) & 0x3Fu);
            if (cp < 0x80) {
                throw PretokenizeError("overlong UTF-8 2-byte sequence at offset " + std::to_string(i));
            }
        } else if ((b0 & 0xF0) == 0xE0) {
            len = 3;
            require_continuation(i + 1);
            require_continuation(i + 2);
            cp = ((b0 & 0x0Fu) << 12) | ((byte_at(i + 1) & 0x3Fu) << 6) | (byte_at(i + 2) & 0x3Fu);
            if (cp < 0x800) {
                throw PretokenizeError("overlong UTF-8 3-byte sequence at offset " + std::to_string(i));
            }
            if (cp >= 0xD800 && cp <= 0xDFFF) {
                throw PretokenizeError("UTF-8 encodes a surrogate code point at offset " + std::to_string(i));
            }
        } else if ((b0 & 0xF8) == 0xF0) {
            len = 4;
            require_continuation(i + 1);
            require_continuation(i + 2);
            require_continuation(i + 3);
            cp = ((b0 & 0x07u) << 18) | ((byte_at(i + 1) & 0x3Fu) << 12) | ((byte_at(i + 2) & 0x3Fu) << 6) |
                 (byte_at(i + 3) & 0x3Fu);
            if (cp < 0x10000) {
                throw PretokenizeError("overlong UTF-8 4-byte sequence at offset " + std::to_string(i));
            }
            if (cp > 0x10FFFF) {
                throw PretokenizeError("UTF-8 sequence decodes above U+10FFFF at offset " + std::to_string(i));
            }
        } else {
            throw PretokenizeError("invalid UTF-8 leading byte 0x" + std::to_string(b0) + " at offset " +
                                    std::to_string(i));
        }
        out.push_back({cp, i, len});
        i += len;
    }
    return out;
}

// Fixed literal contraction suffixes, matched case-sensitively as ASCII, in
// the exact order the pinned oracle's regex source lists them. All start
// with U+0027 (ASCII apostrophe only -- confirmed the Unicode right single
// quote U+2019 is NOT recognized); the remaining letters are plain ASCII,
// so codepoint count equals character count for these literals.
constexpr std::array<std::string_view, 7> kContractionSuffixes = {"'s", "'t", "'re", "'ve", "'m", "'ll", "'d"};

// Returns the number of codepoints consumed by a contraction match starting
// at index `i` within [i, seg_end), or 0 if none matches.
std::size_t match_contraction(const std::vector<DecodedCodepoint>& cps, std::size_t i, std::size_t seg_end) {
    if (cps[i].cp != 0x27) return 0;
    for (std::string_view suffix : kContractionSuffixes) {
        const std::size_t len = suffix.size();
        if (i + len > seg_end) continue;
        bool match = true;
        for (std::size_t k = 0; k < len; ++k) {
            if (cps[i + k].cp != static_cast<std::uint32_t>(static_cast<unsigned char>(suffix[k]))) {
                match = false;
                break;
            }
        }
        if (match) return len;
    }
    return 0;
}

// Applies the pinned ByteLevel regex to one Digits-stage segment
// [seg_begin, seg_end) of `cps`, appending resulting spans (byte offsets
// into the original text) to `out`. Segment boundaries are a HARD stop for
// this scan -- confirmed empirically against the oracle: neither the
// "\s+(?!\S)" lookahead nor the " ?" optional-space prefix ever looks past
// a Digits-stage segment edge.
void scan_bytelevel_segment(const std::vector<DecodedCodepoint>& cps, std::size_t seg_begin, std::size_t seg_end,
                             std::vector<PretokenSpan>& out) {
    std::size_t i = seg_begin;
    while (i < seg_end) {
        if (const std::size_t n = match_contraction(cps, i, seg_end)) {
            out.push_back({cps[i].byte_offset, cps[i + n - 1].byte_offset + cps[i + n - 1].byte_len});
            i += n;
            continue;
        }

        // " ?\p{L}+" | " ?\p{N}+" | " ?[^\s\p{L}\p{N}]+": optional literal
        // ASCII space (U+0020) prefix, then 1+ codepoints of one of L, N,
        // or O (never S -- \s does not participate in this family at all).
        if (cps[i].cp == 0x20 && i + 1 < seg_end) {
            const Class next_c = classify(cps[i + 1].cp);
            if (next_c == Class::Letter || next_c == Class::Number || next_c == Class::Other) {
                std::size_t j = i + 1;
                while (j < seg_end && classify(cps[j].cp) == next_c) ++j;
                out.push_back({cps[i].byte_offset, cps[j - 1].byte_offset + cps[j - 1].byte_len});
                i = j;
                continue;
            }
        }
        const Class c0 = classify(cps[i].cp);
        if (c0 == Class::Letter || c0 == Class::Number || c0 == Class::Other) {
            std::size_t j = i + 1;
            while (j < seg_end && classify(cps[j].cp) == c0) ++j;
            out.push_back({cps[i].byte_offset, cps[j - 1].byte_offset + cps[j - 1].byte_len});
            i = j;
            continue;
        }

        // "\s+(?!\S)" | "\s+": c0 must be WhiteSpace -- every other class
        // was handled above and classify() is total, so this is the only
        // remaining case. Reserve the run's last char (so a following
        // literal space can still serve as branch 2/3/4's prefix) unless
        // the run is exactly 1 char or reaches the segment's end.
        std::size_t k = i + 1;
        while (k < seg_end && classify(cps[k].cp) == Class::WhiteSpace) ++k;
        const std::size_t run_len = k - i;
        std::size_t consumed = run_len;
        if (run_len >= 2 && k < seg_end) consumed = run_len - 1;
        const std::size_t last = i + consumed - 1;
        out.push_back({cps[i].byte_offset, cps[last].byte_offset + cps[last].byte_len});
        i += consumed;
    }
}

}  // namespace

std::vector<PretokenSpan> pretokenize(std::string_view utf8_text) {
    const std::vector<DecodedCodepoint> cps = decode_utf8_strict(utf8_text);
    std::vector<PretokenSpan> out;
    if (cps.empty()) return out;

    // Digits(individual_digits=true) stage: isolate every Number-class
    // codepoint (the full \p{N} = Nd/Nl/No set, confirmed empirically --
    // NOT ASCII-only) into its own 1-codepoint segment; everything else
    // stays in maximal contiguous non-Number runs.
    const std::size_t n = cps.size();
    std::size_t i = 0;
    while (i < n) {
        if (classify(cps[i].cp) == Class::Number) {
            scan_bytelevel_segment(cps, i, i + 1, out);
            ++i;
        } else {
            std::size_t j = i + 1;
            while (j < n && classify(cps[j].cp) != Class::Number) ++j;
            scan_bytelevel_segment(cps, i, j, out);
            i = j;
        }
    }
    return out;
}

}  // namespace orcengine
