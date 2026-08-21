#!/usr/bin/env python3
# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Phase 5B Stage 2A dev-only generator/validator. NOT part of the C++
# build or runtime -- CMake never invokes this file. It exists to make the
# committed Unicode range tables (include/orcengine/pretok_tables.hpp) and
# the committed oracle fixture corpus (tests/pretok_oracle_fixtures.hpp,
# tests/pretok_invalid_utf8.hpp) reproducible and re-derivable, per
# DECISION_LOG.md OE-ADR-032's provenance requirement. Run it again only if
# the pinned tokenizer.json or the pinned `tokenizers` package version ever
# changes -- otherwise the committed headers are the checked-in, validated
# result and this script does not need to run.
#
# Requires: `tokenizers==0.22.2` (the pinned oracle) installed, and the
# pinned smollm2-135m/tokenizer.json available locally (not committed to
# this repo -- it ships with the real GGUF conversion artifacts in the
# Phase 2 worktree, see TOKENIZER_JSON below).
#
# ---------------------------------------------------------------------------
# Methodology summary (see docs/OrcEngine/DECISION_LOG.md OE-ADR-032 for the
# full narrative):
#
# 1. Established the exact pinned pretokenizer contract by reading
#    tokenizer.json directly: pre_tokenizer = Sequence(
#      Digits(individual_digits=True),
#      ByteLevel(add_prefix_space=False, trim_offsets=True, use_regex=True))
#    normalizer = None.
#
# 2. Determined the ByteLevel regex's Unicode classification empirically
#    against the LIVE oracle (classify_oracle below), not by assuming any
#    particular library's category tables match. Key discoveries this
#    caught that a naive assumption would have missed:
#      - Python's str.isspace() is NOT the right \s candidate: it
#        incorrectly reports True for U+001C-U+001F (info separator
#        controls, a CPython-specific historical carve-out) which the
#        oracle does NOT treat as whitespace. The correct \s candidate is
#        the canonical, version-stable Unicode White_Space=Y property list
#        (25 codepoints/ranges), which DOES match the oracle exactly at
#        every boundary tested.
#      - An early single-flanking-space disambiguation probe was
#        insufficient to distinguish \s from "Other" (both produce the same
#        token count for a single flanked test character) -- see
#        classify_oracle()'s "doubled-character" design, which exploits the
#        \s+(?!\S) branch's unique "reserve the last char" behavior (a
#        property no other branch has) to disambiguate correctly. An
#        earlier, in-conversation attempt using the simpler probe produced
#        a wrong conclusion for the Digits-stage predicate that this script
#        corrects.
#      - The Digits(individual_digits=true) stage's numeric predicate was
#        confirmed EQUAL to \p{N} (Nd union Nl union No) -- not
#        ASCII-digit-only. Every one of the 71 Nd-category and 84
#        Nl/No-category Unicode script ranges is isolated by the Digits
#        stage exactly like \p{N} classification requires. The only real
#        difference between the two stages is that Digits performs
#        per-codepoint isolation ahead of ByteLevel, so ByteLevel's own
#        \p{N}+ grouping quantifier never observes more than one N-class
#        codepoint at a time in practice.
#      - Digits-stage segment boundaries are a HARD stop for ByteLevel's
#        scan: neither the " ?" optional-space prefix nor the \s+(?!\S)
#        lookahead ever looks past a segment edge (confirmed via
#        "a 5b" -> a, [space], 5, b -- the space does NOT attach to the
#        following digit).
#
# 3. Candidate L (\p{L}) and N (\p{N}) range tables were built from Python's
#    own unicodedata category() function, then validated -- NOT assumed
#    correct -- by probing every range boundary (start-1, start, end,
#    end+1) plus a large seeded random sample against the live oracle.
#
# 4. All discovered differences were patched into the final table (the S
#    table uses the fixed White_Space=Y list, not Python isspace()) before
#    ranges were ever written to the committed C++ header, so the shipped
#    table already reflects the corrected, oracle-verified truth.
# ---------------------------------------------------------------------------

import sys
import json
import time
import random
import hashlib
import unicodedata

sys.stdout.reconfigure(encoding="utf-8")

from tokenizers import Tokenizer
from tokenizers.pre_tokenizers import ByteLevel, Digits

# ---------------------------------------------------------------------------
# Paths (adjust TOKENIZER_JSON if re-running from a different worktree
# layout -- the real GGUF conversion artifacts, including tokenizer.json,
# live alongside the Phase 2 worktree's own artifacts, not in this repo).
# ---------------------------------------------------------------------------
TOKENIZER_JSON = "F:/Ai/OrchestratorIDE-phase2-gguf/Tools/OrcEnginePhase0/artifacts/smollm2-135m/tokenizer.json"
GOLDEN_FIXTURES = "../../OrcEnginePhase0/artifacts/tokenizer_golden_fixtures.json"
RAW_PROMPT_MANIFEST = "../../OrcEnginePhase0/artifacts/raw_prompt_identity_manifest.json"
OUT_TABLES_HPP = "../include/orcengine/pretok_tables.hpp"
OUT_FIXTURES_HPP = "../tests/pretok_oracle_fixtures.hpp"
OUT_INVALID_HPP = "../tests/pretok_invalid_utf8.hpp"

SEED = 20260820  # fixed for reproducibility; see report for corpus size

bl = ByteLevel(add_prefix_space=False, trim_offsets=True, use_regex=True)
digits = Digits(individual_digits=True)


def classify_oracle(cp):
    """Robust 4-way (L/N/S/O) classifier against the LIVE ByteLevel
    pretokenizer. See module docstring point 2 for why the doubled-char
    probe is required (a single-flanking-space probe cannot distinguish S
    from O)."""
    if cp == 0x20:
        return "S"  # literal ASCII space: known S; the doubled-probe is
                     # confounded for this one codepoint by its unique dual
                     # role as the optional prefix of the L/N/O branches.
    if 0xD800 <= cp <= 0xDFFF:
        return None
    c = chr(cp)
    rL = bl.pre_tokenize_str("a" + c + "a")
    if len(rL) == 1:
        return "L"
    rN = bl.pre_tokenize_str("5" + c + "5")
    if len(rN) == 1:
        return "N"
    rD = bl.pre_tokenize_str("aa" + c + c + "aa")
    n = len(rD)
    if n == 4:
        return "S"
    if n == 3:
        return "O"
    return "AMBIG"


L_CATS = {"Lu", "Ll", "Lt", "Lm", "Lo"}
N_CATS = {"Nd", "Nl", "No"}
# Canonical Unicode White_Space=Y property (25 codepoints/ranges, stable
# since Unicode 6.3). Deliberately NOT derived from Python str.isspace().
S_FIXED = (
    list(range(0x09, 0x0E)) + [0x20, 0x85, 0xA0, 0x1680]
    + list(range(0x2000, 0x200B)) + [0x2028, 0x2029, 0x202F, 0x205F, 0x3000]
)
S_SET = set(S_FIXED)


def candidate_class(cp):
    if cp in S_SET:
        return "S"
    if 0xD800 <= cp <= 0xDFFF:
        return None
    cat = unicodedata.category(chr(cp))
    if cat in L_CATS:
        return "L"
    if cat in N_CATS:
        return "N"
    return "O"


def build_candidate_ranges():
    ranges = {"L": [], "N": []}
    cur_class = None
    cur_start = None

    def flush(end_exclusive):
        nonlocal cur_class, cur_start
        if cur_class in ("L", "N"):
            ranges[cur_class].append((cur_start, end_exclusive - 1))
        cur_class = None
        cur_start = None

    for cp in range(0, 0x110000):
        cls = None if 0xD800 <= cp <= 0xDFFF else candidate_class(cp)
        if cls == "O":
            cls = None
        if cls != cur_class:
            flush(cp)
            cur_class = cls
            cur_start = cp
    flush(0x110000)
    return ranges


def s_ranges_from_codepoints(cps):
    cps = sorted(cps)
    out = []
    start = prev = cps[0]
    for cp in cps[1:]:
        if cp == prev + 1:
            prev = cp
            continue
        out.append((start, prev))
        start = prev = cp
    out.append((start, prev))
    return out


def validate_boundaries(ranges):
    boundary_points = set()
    for cls in ("L", "N"):
        for (start, end) in ranges[cls]:
            for cp in (start - 1, start, end, end + 1):
                if 0 <= cp <= 0x10FFFF:
                    boundary_points.add(cp)
    for cp in S_FIXED:
        for probe_cp in (cp - 1, cp, cp + 1):
            if 0 <= probe_cp <= 0x10FFFF:
                boundary_points.add(probe_cp)

    mismatches = []
    checked = 0
    for cp in sorted(boundary_points):
        cand = candidate_class(cp)
        if cand is None:
            continue
        oracle = classify_oracle(cp)
        checked += 1
        if oracle != cand:
            mismatches.append((cp, cand, oracle))
    return checked, mismatches


def validate_random(n_samples):
    random.seed(SEED)
    sample = sorted(set(random.randint(0, 0x10FFFF) for _ in range(n_samples)))
    mismatches = []
    checked = 0
    for cp in sample:
        cand = candidate_class(cp)
        if cand is None:
            continue
        oracle = classify_oracle(cp)
        checked += 1
        if oracle != cand:
            mismatches.append((cp, cand, oracle))
    return len(sample), checked, mismatches


def validate_digits_stage(ranges):
    """Confirms the Digits(individual_digits=true) isolation predicate is
    IDENTICAL to the N table above (Nd union Nl union No), at every N-range
    boundary."""
    def isolated(cp):
        if 0xD800 <= cp <= 0xDFFF:
            return None
        r = digits.pre_tokenize_str("a" + chr(cp) + "a")
        return len(r) == 3

    boundary_points = set()
    for (start, end) in ranges["N"]:
        for cp in (start - 1, start, end, end + 1):
            if 0 <= cp <= 0x10FFFF:
                boundary_points.add(cp)

    def in_n(cp):
        for (s, e) in ranges["N"]:
            if s <= cp <= e:
                return True
            if cp < s:
                break
        return False

    mismatches = []
    checked = 0
    for cp in sorted(boundary_points):
        exp = in_n(cp)
        obs = isolated(cp)
        if obs is None:
            continue
        checked += 1
        if exp != obs:
            mismatches.append((cp, exp, obs))
    return checked, mismatches


# ---------------------------------------------------------------------------
# Oracle fixture corpus (byte-offset pretoken boundaries for the exact,
# full two-stage Sequence(Digits, ByteLevel) pretokenizer, computed from
# tokenizer.json directly -- not the ByteLevel-only probe helper above).
# ---------------------------------------------------------------------------

def cp_offsets_to_byte_offsets(text, spans):
    cum = [0] * (len(text) + 1)
    for i, ch in enumerate(text):
        cum[i + 1] = cum[i] + len(ch.encode("utf-8"))
    return [(cum[s], cum[e]) for (s, e) in spans]


def build_corpus(tok):
    pt = tok.pre_tokenizer
    corpus = []

    def add(id_, text):
        corpus.append((id_, text))

    add("empty", "")
    add("ascii_words", "Hello, world! This is a test: 123.")
    add("ascii_punct_only", "!@#$%^&*()_+-=[]{}|;':\",./<>?")
    add("leading_ws", "   leading spaces")
    add("trailing_ws", "trailing spaces   ")
    add("repeated_ws", "a    b     c")
    add("tabs", "a\tb\tc")
    add("lf", "line one\nline two")
    add("crlf", "line one\r\nline two")
    add("only_ws", "   \n\t  ")
    add("contractions", "don't isn't I've I'll I'm I'd we're can't")
    add("digit_one", "a1a")
    add("digit_two", "a12a")
    add("digit_long_run", "The year 20231231 was long: 999999999999")
    add("mixed_letter_number", "abc123def456")
    add("number_letter_boundary", "123abc")
    add("letter_number_boundary", "abc123")
    add("letter_punct_boundary", "abc!!!def")
    add("punct_letter_boundary", "!!!abcdef")
    add("ws_letter_boundary", "   abc")
    add("letter_ws_boundary", "abc   ")
    add("arabic_indic_digits", "\u0661\u0662\u0663")
    add("fullwidth_digits", "\uFF11\uFF12\uFF13")
    add("superscript_digits", "\u00B2\u00B3")
    add("roman_numerals", "\u2160\u2161\u2162")
    add("non_ascii_latin", "café résumé naïve Zürich")
    add("non_ascii_cjk", "你好世界 こんにちは 한국어")
    add("emoji", "Hello \U0001F600 World \U0001F601\U0001F602")
    add("combining_marks", "e\u0301 a\u0300 n\u0303")
    add("embedded_nul", "before\x00after")
    add("special_token_lookalike", "text with <|endoftext|> inside and <|im_start|> too")
    add("punct_adjacent_unicode", "café!résumé,naïve.")
    add("numeric_isolated_between_letters", "a5b")
    add("consecutive_numeric_diff_blocks", "5\u0661\uFF11")
    add("literal_apostrophe_vs_rsquo", "don't don\u2019t")
    add("uppercase_apostrophe", "DON'T")
    add("multi_merge_bpe_case", "unbelievably wonderful transformation")
    add("repeated_adjacent_pairs", "aaaaaaaaaa bbbbbbbbbb")

    gf = json.load(open(GOLDEN_FIXTURES, encoding="utf-8"))
    for f in gf["fixtures"]:
        raw_repr = f.get("raw_text_repr") or f.get("original_text_repr")
        if raw_repr is None:
            continue
        raw_text = eval(raw_repr, {"__builtins__": {}})
        add("golden__" + f["fixture_id"], raw_text)

    rp = json.load(open(RAW_PROMPT_MANIFEST, encoding="utf-8"))
    for r in rp["records"]:
        add("rawprompt__" + r["fixture_id"], r["raw_text"])

    results = []
    for id_, text in corpus:
        b1 = cp_offsets_to_byte_offsets(text, [s for (_p, s) in pt.pre_tokenize_str(text)])
        b2 = cp_offsets_to_byte_offsets(text, [s for (_p, s) in pt.pre_tokenize_str(text)])
        assert b1 == b2, f"non-deterministic result for {id_}"
        utf8 = text.encode("utf-8")
        results.append({"id": id_, "utf8_hex": utf8.hex(), "byte_length": len(utf8), "boundaries": b1})
    return results


INVALID_UTF8_CASES = {
    "lone_continuation_byte": bytes([0x80]).hex(),
    "truncated_2byte": bytes([0xC2]).hex(),
    "truncated_3byte": bytes([0xE0, 0xA0]).hex(),
    "truncated_4byte": bytes([0xF0, 0x90, 0x80]).hex(),
    "overlong_2byte_null": bytes([0xC0, 0x80]).hex(),
    "overlong_3byte": bytes([0xE0, 0x80, 0x80]).hex(),
    "surrogate_encoded": bytes([0xED, 0xA0, 0x80]).hex(),
    "above_10FFFF": bytes([0xF4, 0x90, 0x80, 0x80]).hex(),
}


# ---------------------------------------------------------------------------
# C++ header emission
# ---------------------------------------------------------------------------

def cpp_bytes_literal(raw: bytes) -> str:
    return '"' + "".join(f"\\x{b:02x}" for b in raw) + '"'


def emit_tables_header(ranges, s_ranges):
    L = []
    L.append("// Copyright (C) 2025-present hardcoreerik / TheOrc contributors")
    L.append("// SPDX-License-Identifier: AGPL-3.0-or-later")
    L.append("//")
    L.append("// GENERATED FILE -- do not hand-edit. Produced by")
    L.append("// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.")
    L.append("//")
    L.append("// Provenance (Stage 2A pretokenization tables):")
    L.append(f"//   python_version        : {sys.version.split()[0]}")
    L.append(f"//   unicodedata_version   : {unicodedata.unidata_version}")
    L.append("//   tokenizers_version    : 0.22.2 (pinned oracle)")
    L.append("//   tokenizer_source      : smollm2-135m/tokenizer.json, pinned revision")
    L.append("//                           93efa2f097d58c2a74874c7e644dbc9b0cee75a2")
    L.append("//")
    L.append("// L (\\p{L}) / N (\\p{N}) candidate ranges: Unicode General Category")
    L.append("// L*(Lu,Ll,Lt,Lm,Lo) / N*(Nd,Nl,No), computed from the unicodedata version")
    L.append("// above. S (\\s): the FIXED, version-stable Unicode White_Space=Y property")
    L.append("// list (25 codepoints/ranges) -- NOT Python str.isspace(), which incorrectly")
    L.append("// includes U+001C-U+001F; see this script's module docstring for the full")
    L.append("// discovery narrative and DECISION_LOG.md OE-ADR-032 for the record.")
    L.append("//")
    L.append("// Every L/N range boundary (and both neighbors of every S range) was")
    L.append("// validated against a live tokenizers==0.22.2 ByteLevel pretokenizer: see")
    L.append("// this script's validate_boundaries()/validate_random()/")
    L.append("// validate_digits_stage() for exact counts and the run report this file's")
    L.append("// companion commit records.")
    L.append("#pragma once")
    L.append("")
    L.append("#include <cstddef>")
    L.append("#include <cstdint>")
    L.append("")
    L.append("namespace orcengine::pretok_tables {")
    L.append("")
    L.append("struct CodepointRange {")
    L.append("    std::uint32_t first;")
    L.append("    std::uint32_t last;")
    L.append("};")
    L.append("")

    def fmt(name, rs):
        out = [f"inline constexpr CodepointRange k{name}[] = {{"]
        for (s, e) in rs:
            out.append(f"    {{0x{s:X}, 0x{e:X}}},")
        out.append("};")
        out.append(f"inline constexpr std::size_t k{name}Count = {len(rs)};")
        return "\n".join(out)

    L.append(fmt("Letter", ranges["L"]))
    L.append("")
    L.append(fmt("Number", ranges["N"]))
    L.append("")
    L.append(fmt("WhiteSpace", s_ranges))
    L.append("")
    L.append("}  // namespace orcengine::pretok_tables")
    L.append("")
    return "\n".join(L)


def emit_fixtures_header(oracle_fixtures):
    F = []
    F.append("// Copyright (C) 2025-present hardcoreerik / TheOrc contributors")
    F.append("// SPDX-License-Identifier: AGPL-3.0-or-later")
    F.append("//")
    F.append("// GENERATED FILE -- do not hand-edit. Produced by")
    F.append("// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.")
    F.append("//")
    F.append("// Oracle pretokenization boundary fixtures: byte-offset [begin,end) spans")
    F.append("// computed from tokenizers==0.22.2's real Sequence(Digits(individual_digits")
    F.append("// =true), ByteLevel(add_prefix_space=false, trim_offsets=true, use_regex=")
    F.append("// true)) pretokenizer, loaded from the pinned smollm2-135m/tokenizer.json.")
    F.append("// Codepoint offsets from pre_tokenize_str() were converted to UTF-8 byte")
    F.append("// offsets by summing each preceding codepoint's UTF-8 encoded length")
    F.append("// (deterministic, no oracle dependency for that conversion). Every entry")
    F.append("// was computed twice and asserted identical before being written here.")
    F.append(f"//   corpus size           : {len(oracle_fixtures)}")
    F.append("#pragma once")
    F.append("")
    F.append("#include <cstddef>")
    F.append("#include <string_view>")
    F.append("")
    F.append("namespace orcengine::pretok_fixtures {")
    F.append("")
    F.append("struct ByteSpan { std::size_t begin; std::size_t end; };")
    F.append("")
    F.append("struct OracleFixture {")
    F.append("    const char* id;")
    F.append("    std::string_view utf8;")
    F.append("    const ByteSpan* spans;")
    F.append("    std::size_t span_count;")
    F.append("};")
    F.append("")

    span_arrays = []
    entries = []
    for i, e in enumerate(oracle_fixtures):
        arr_name = f"kSpans_{i}"
        spans = e["boundaries"]
        if spans:
            arr = f"inline constexpr ByteSpan {arr_name}[] = {{" + \
                  ", ".join(f"{{{s},{en}}}" for s, en in spans) + "};"
        else:
            arr = f"inline constexpr ByteSpan* {arr_name} = nullptr;"
        span_arrays.append(arr)
        lit = cpp_bytes_literal(bytes.fromhex(e["utf8_hex"])) if e["utf8_hex"] else '""'
        entries.append(
            f'    {{"{e["id"]}", std::string_view({lit}, {e["byte_length"]}), {arr_name}, {len(spans)}}},'
        )

    F.extend(span_arrays)
    F.append("")
    F.append("inline constexpr OracleFixture kFixtures[] = {")
    F.extend(entries)
    F.append("};")
    F.append(f"inline constexpr std::size_t kFixtureCount = {len(oracle_fixtures)};")
    F.append("")
    F.append("}  // namespace orcengine::pretok_fixtures")
    F.append("")
    return "\n".join(F)


def emit_invalid_header(cases):
    I = []
    I.append("// Copyright (C) 2025-present hardcoreerik / TheOrc contributors")
    I.append("// SPDX-License-Identifier: AGPL-3.0-or-later")
    I.append("//")
    I.append("// GENERATED FILE -- do not hand-edit. Produced by")
    I.append("// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.")
    I.append("#pragma once")
    I.append("")
    I.append("#include <cstddef>")
    I.append("#include <string_view>")
    I.append("")
    I.append("namespace orcengine::pretok_fixtures {")
    I.append("")
    I.append("struct InvalidUtf8Case { const char* id; std::string_view bytes; };")
    I.append("")
    I.append("inline constexpr InvalidUtf8Case kInvalidUtf8Cases[] = {")
    for k, hexval in cases.items():
        b = bytes.fromhex(hexval)
        I.append(f'    {{"{k}", std::string_view({cpp_bytes_literal(b)}, {len(b)})}},')
    I.append("};")
    I.append(f"inline constexpr std::size_t kInvalidUtf8CaseCount = {len(cases)};")
    I.append("")
    I.append("}  // namespace orcengine::pretok_fixtures")
    I.append("")
    return "\n".join(I)


def main():
    t0 = time.time()
    ranges = build_candidate_ranges()
    s_ranges = s_ranges_from_codepoints(S_FIXED)
    print(f"[{time.time()-t0:.2f}s] candidate ranges: L={len(ranges['L'])} N={len(ranges['N'])} S={len(s_ranges)}")

    bchecked, bmis = validate_boundaries(ranges)
    print(f"[{time.time()-t0:.2f}s] boundary validation: checked={bchecked} mismatches={len(bmis)}")
    for cp, cand, oracle in bmis:
        print(f"    MISMATCH U+{cp:04X} candidate={cand} oracle={oracle}")

    rsampled, rchecked, rmis = validate_random(5000)
    print(f"[{time.time()-t0:.2f}s] random validation (seed={SEED}): sampled={rsampled} checked={rchecked} mismatches={len(rmis)}")
    for cp, cand, oracle in rmis:
        print(f"    MISMATCH U+{cp:04X} candidate={cand} oracle={oracle}")

    dchecked, dmis = validate_digits_stage(ranges)
    print(f"[{time.time()-t0:.2f}s] Digits-stage validation: checked={dchecked} mismatches={len(dmis)}")
    for cp, exp, obs in dmis:
        print(f"    MISMATCH U+{cp:04X} expected_N={exp} digits_isolated={obs}")

    if bmis or rmis or dmis:
        print("ABORTING: unresolved mismatches, not writing headers.")
        sys.exit(1)

    tok = Tokenizer.from_file(TOKENIZER_JSON)
    fixtures = build_corpus(tok)
    print(f"[{time.time()-t0:.2f}s] oracle fixture corpus: {len(fixtures)} entries")

    tables_text = emit_tables_header(ranges, s_ranges)
    fixtures_text = emit_fixtures_header(fixtures)
    invalid_text = emit_invalid_header(INVALID_UTF8_CASES)

    open(OUT_TABLES_HPP, "w", encoding="utf-8", newline="\n").write(tables_text)
    open(OUT_FIXTURES_HPP, "w", encoding="utf-8", newline="\n").write(fixtures_text)
    open(OUT_INVALID_HPP, "w", encoding="utf-8", newline="\n").write(invalid_text)

    for name, text in (("tables", tables_text), ("fixtures", fixtures_text), ("invalid", invalid_text)):
        print(f"{name}: {len(text)} bytes, sha256={hashlib.sha256(text.encode()).hexdigest()}")

    print(f"done in {time.time()-t0:.2f}s")


if __name__ == "__main__":
    main()
