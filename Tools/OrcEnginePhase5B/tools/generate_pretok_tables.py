#!/usr/bin/env python3
# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Phase 5B Stage 2A dev-only generator/validator. NOT part of the C++
# build or runtime -- CMake never invokes this file. It exists to make the
# committed Unicode range tables (include/orcengine/pretok_tables.hpp) and
# the committed oracle fixture corpus (tests/pretok_oracle_fixtures.hpp,
# tests/pretok_invalid_utf8.hpp) reproducible and re-derivable, per
# DECISION_LOG.md OE-ADR-032/OE-ADR-033's provenance requirement. Run it
# again only if the pinned tokenizer.json or the pinned `tokenizers`
# package version ever changes -- otherwise the committed headers are the
# checked-in, validated result and this script does not need to run.
#
# Usage:
#   python generate_pretok_tables.py --tokenizer-json <path/to/tokenizer.json>
#   python generate_pretok_tables.py --tokenizer-json <path> --check
#
# --check performs a read-only dry run: it regenerates every output in
# memory and compares it byte-for-byte against the committed files,
# exiting nonzero (without writing anything) if they differ. This is the
# mode CI/reviewers should run to confirm the committed headers are still
# exactly what this script (with this environment) produces. Without
# --check, all 5 generated headers are written (Stage 2A's tables/fixtures/
# invalid-UTF-8 headers plus Stage 2B's byte-alphabet and encode-fixture
# headers, added by DECISION_LOG.md OE-ADR-034).
#
# Requires: the exact pinned `tokenizers==0.22.2` oracle installed (this
# script verifies the installed version itself and aborts before doing
# any work if it differs -- it never trusts a hard-coded version label),
# and the pinned smollm2-135m/tokenizer.json supplied explicitly via
# --tokenizer-json (not committed to this repo -- it ships with the real
# GGUF conversion artifacts in the Phase 2 worktree). The supplied file's
# SHA-256 is checked against the pinned value below and the script aborts
# before generating or writing anything if it differs, or if the file's
# own declared pretokenizer contract doesn't match what Stage 2A assumes.
#
# All repository-relative paths (golden fixtures, raw-prompt manifest, and
# the 5 generated header destinations) are resolved relative to this
# script's own location (`Path(__file__).resolve()`), not the caller's
# current working directory -- running this script from the repository
# root or from its own directory produces identical results.
#
# ---------------------------------------------------------------------------
# Methodology summary (see docs/OrcEngine/DECISION_LOG.md OE-ADR-032 for the
# full narrative):
#
# 1. Established the exact pinned pretokenizer contract by reading
#    tokenizer.json directly: pre_tokenizer = Sequence(
#      Digits(individual_digits=True),
#      ByteLevel(add_prefix_space=False, trim_offsets=True, use_regex=True))
#    normalizer = None. This script now PARSES and VERIFIES that exact
#    contract against the supplied file (see verify_tokenizer_contract())
#    rather than assuming it from the file's path or name.
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
#
# This corpus matches the pinned oracle across the 63-entry fixture
# corpus, all generated Unicode category boundaries, and the recorded
# seeded random sample; exhaustive equivalence over every possible
# Unicode string is not claimed.
# ---------------------------------------------------------------------------

import argparse
import ast
import hashlib
import json
import random
import sys
import time
import unicodedata
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

# ---------------------------------------------------------------------------
# Step 1: verify the installed tokenizers package itself, before importing
# anything else from it or doing any other work. Never trust a hard-coded
# version label without checking the actual installed package.
# ---------------------------------------------------------------------------
REQUIRED_TOKENIZERS_VERSION = "0.22.2"

import tokenizers as _tokenizers_pkg  # noqa: E402  (must follow the version-check comment)

if _tokenizers_pkg.__version__ != REQUIRED_TOKENIZERS_VERSION:
    print(
        f"ABORTING: installed tokenizers=={_tokenizers_pkg.__version__}, "
        f"required tokenizers=={REQUIRED_TOKENIZERS_VERSION}. Refusing to generate or "
        f"write anything against an unverified oracle version.",
        file=sys.stderr,
    )
    sys.exit(1)

from tokenizers import Tokenizer  # noqa: E402
from tokenizers.pre_tokenizers import ByteLevel, Digits  # noqa: E402

# ---------------------------------------------------------------------------
# Pinned tokenizer.json identity. The SHA-256 below was computed once from
# the accepted SmolLM2-135M artifact (pinned revision
# 93efa2f097d58c2a74874c7e644dbc9b0cee75a2) and is checked against every
# --tokenizer-json argument -- this script never relies on the path or
# filename alone to decide it is looking at the right file.
# ---------------------------------------------------------------------------
EXPECTED_TOKENIZER_JSON_SHA256 = "9ca9acddb6525a194ec8ac7a87f24fbba7232a9a15ffa1af0c1224fcd888e47c"

SCRIPT_DIR = Path(__file__).resolve().parent
GOLDEN_FIXTURES = SCRIPT_DIR / "../../OrcEnginePhase0/artifacts/tokenizer_golden_fixtures.json"
RAW_PROMPT_MANIFEST = SCRIPT_DIR / "../../OrcEnginePhase0/artifacts/raw_prompt_identity_manifest.json"
OUT_TABLES_HPP = SCRIPT_DIR / "../include/orcengine/pretok_tables.hpp"
OUT_FIXTURES_HPP = SCRIPT_DIR / "../tests/pretok_oracle_fixtures.hpp"
OUT_INVALID_HPP = SCRIPT_DIR / "../tests/pretok_invalid_utf8.hpp"
OUT_BYTE_ALPHABET_HPP = SCRIPT_DIR / "../tests/byte_alphabet_oracle.hpp"
OUT_ENCODE_HPP = SCRIPT_DIR / "../tests/encode_oracle_fixtures.hpp"

SEED = 20260820  # fixed for reproducibility; see report for corpus size

bl = ByteLevel(add_prefix_space=False, trim_offsets=True, use_regex=True)
digits = Digits(individual_digits=True)


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify_tokenizer_identity_and_contract(tokenizer_json_path: Path):
    """Aborts before any generation if the supplied tokenizer.json is not
    byte-identical to the pinned artifact, or if its declared pretokenizer
    contract doesn't match what Stage 2A's algorithm assumes. Never relies
    on the path or filename alone."""
    if not tokenizer_json_path.is_file():
        print(f"ABORTING: --tokenizer-json path does not exist: {tokenizer_json_path}", file=sys.stderr)
        sys.exit(1)

    actual_hash = sha256_file(tokenizer_json_path)
    if actual_hash != EXPECTED_TOKENIZER_JSON_SHA256:
        print(
            f"ABORTING: {tokenizer_json_path} sha256={actual_hash} does not match the "
            f"pinned smollm2-135m/tokenizer.json sha256={EXPECTED_TOKENIZER_JSON_SHA256}. "
            f"Refusing to generate or write anything against an unverified tokenizer file.",
            file=sys.stderr,
        )
        sys.exit(1)

    with open(tokenizer_json_path, encoding="utf-8") as f:
        raw = json.load(f)

    def fail(msg):
        print(f"ABORTING: tokenizer.json contract check failed -- {msg}", file=sys.stderr)
        sys.exit(1)

    if raw.get("normalizer") is not None:
        fail(f"normalizer is not null: {raw.get('normalizer')!r}")

    pt = raw.get("pre_tokenizer")
    if not isinstance(pt, dict) or pt.get("type") != "Sequence":
        fail(f"pre_tokenizer is not a Sequence: {pt!r}")

    stages = pt.get("pretokenizers")
    if not isinstance(stages, list) or len(stages) != 2:
        fail(f"pre_tokenizer.pretokenizers does not have exactly 2 stages: {stages!r}")

    d_stage, bl_stage = stages
    if d_stage.get("type") != "Digits" or d_stage.get("individual_digits") is not True:
        fail(f"stage 0 is not Digits(individual_digits=true): {d_stage!r}")

    if (
        bl_stage.get("type") != "ByteLevel"
        or bl_stage.get("add_prefix_space") is not False
        or bl_stage.get("trim_offsets") is not True
        or bl_stage.get("use_regex") is not True
    ):
        fail(
            "stage 1 is not ByteLevel(add_prefix_space=false, trim_offsets=true, "
            f"use_regex=true): {bl_stage!r}"
        )

    return actual_hash


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
    add("arabic_indic_digits", "١٢٣")
    add("fullwidth_digits", "１２３")
    add("superscript_digits", "²³")
    add("roman_numerals", "ⅠⅡⅢ")
    add("non_ascii_latin", "café résumé naïve Zürich")
    add("non_ascii_cjk", "你好世界 こんにちは 한국어")
    add("emoji", "Hello \U0001F600 World \U0001F601\U0001F602")
    add("combining_marks", "é à ñ")
    add("embedded_nul", "before\x00after")
    add("special_token_lookalike", "text with <|endoftext|> inside and <|im_start|> too")
    add("punct_adjacent_unicode", "café!résumé,naïve.")
    add("numeric_isolated_between_letters", "a5b")
    add("consecutive_numeric_diff_blocks", "5١１")
    add("literal_apostrophe_vs_rsquo", "don't don’t")
    add("uppercase_apostrophe", "DON'T")
    add("multi_merge_bpe_case", "unbelievably wonderful transformation")
    add("repeated_adjacent_pairs", "aaaaaaaaaa bbbbbbbbbb")

    gf = json.loads(GOLDEN_FIXTURES.read_text(encoding="utf-8"))
    for f in gf["fixtures"]:
        raw_repr = f.get("raw_text_repr") or f.get("original_text_repr")
        if raw_repr is None:
            continue
        raw_text = ast.literal_eval(raw_repr)
        add("golden__" + f["fixture_id"], raw_text)

    rp = json.loads(RAW_PROMPT_MANIFEST.read_text(encoding="utf-8"))
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
# Stage 2B: GPT-2 byte-to-Unicode alphabet oracle fixture.
#
# The mapping is the well-known closed-form GPT-2 byte_encoder: bytes in
# [33,126] union [161,172] union [174,255] map to themselves; the remaining
# 68 "unprintable" byte values map to 256+n in ascending byte-value order.
# This is reconstructed here independently and then cross-checked against
# the live oracle's tokenizers.pre_tokenizers.ByteLevel.alphabet() (exact
# 256-member SET equality) and, for every byte value reachable through
# valid UTF-8 (ASCII 0x00-0x7F directly; 0x80-0xBF as continuation bytes;
# 0xC2-0xF4 as multi-byte lead bytes), a DIRECT per-byte mapped-character
# check against ByteLevel.pre_tokenize_str() -- not merely the alphabet
# set. See docs/OrcEngine/DECISION_LOG.md OE-ADR-034 for the full
# methodology and result counts.
# ---------------------------------------------------------------------------

def build_byte_to_unicode():
    bs = list(range(ord("!"), ord("~") + 1)) + list(range(0xA1, 0xAD)) + list(range(0xAE, 0x100))
    cs = bs[:]
    n = 0
    for b in range(256):
        if b not in bs:
            bs.append(b)
            cs.append(256 + n)
            n += 1
    return dict(zip(bs, cs))


def verify_byte_to_unicode(b2u):
    """Returns (ok: bool, details: list[str]) -- see module docstring above
    this function's caller for the exact methodology."""
    details = []
    alphabet_set = set(ByteLevel.alphabet())
    our_set = set(chr(v) for v in b2u.values())
    if alphabet_set != our_set:
        details.append(f"alphabet SET mismatch: missing={alphabet_set - our_set} extra={our_set - alphabet_set}")

    def solo_mapped(s):
        return "".join(p for p, _ in bl.pre_tokenize_str(s))

    # ASCII 0x00-0x7F: single-byte UTF-8, direct.
    for b in range(0, 128):
        mapped = solo_mapped(chr(b))
        expected = chr(b2u[b])
        if mapped != expected:
            details.append(f"ASCII byte 0x{b:02X}: got={mapped!r} expected={expected!r}")

    # Continuation bytes 0x80-0xBF: as the 2nd byte of U+0080..U+00BF (lead=0xC2).
    for k in range(0x80, 0xC0):
        mapped = solo_mapped(chr(k))
        expected = chr(b2u[0xC2]) + chr(b2u[k])
        if mapped != expected:
            details.append(f"continuation byte 0x{k:02X}: got={mapped!r} expected={expected!r}")

    # 2-byte lead bytes 0xC2-0xDF.
    for lead in range(0xC2, 0xE0):
        cp = (lead - 0xC0) << 6
        mapped = solo_mapped(chr(cp))
        expected = chr(b2u[lead]) + chr(b2u[0x80])
        if mapped != expected:
            details.append(f"2-byte lead 0x{lead:02X}: got={mapped!r} expected={expected!r}")

    # 3-byte lead bytes 0xE0-0xEF (skip the surrogate-range construction for 0xED).
    for lead in range(0xE0, 0xF0):
        top4 = lead & 0x0F
        mid6 = 0x20
        cp = (top4 << 12) | (mid6 << 6)
        if 0xD800 <= cp <= 0xDFFF:
            mid6 = 0x00
            cp = (top4 << 12) | (mid6 << 6)
            if 0xD800 <= cp <= 0xDFFF:
                continue
        mapped = solo_mapped(chr(cp))
        expected = chr(b2u[lead]) + chr(b2u[0x80 | mid6]) + chr(b2u[0x80])
        if mapped != expected:
            details.append(f"3-byte lead 0x{lead:02X}: got={mapped!r} expected={expected!r}")

    # 4-byte lead bytes 0xF0-0xF4.
    for lead in range(0xF0, 0xF5):
        top3 = lead & 0x07
        cp = (top3 << 18) | (0x10 << 12)
        if not (0x10000 <= cp <= 0x10FFFF):
            continue
        mapped = solo_mapped(chr(cp))
        expected = chr(b2u[lead]) + chr(b2u[0x90]) + chr(b2u[0x80]) + chr(b2u[0x80])
        if mapped != expected:
            details.append(f"4-byte lead 0x{lead:02X}: got={mapped!r} expected={expected!r}")

    return (len(details) == 0), details


# ---------------------------------------------------------------------------
# Stage 2B: encode() oracle fixtures. The 17 CONTROL strings are hardcoded
# HERE ONLY (as independent oracle-side test input) -- production C++ code
# must derive its own CONTROL string/ID table from validated TokenizerProfile
# metadata (tokens()/token_types()), never from a copy of this list.
# ---------------------------------------------------------------------------

CONTROL_STRINGS = [
    "<|endoftext|>", "<|im_start|>", "<|im_end|>", "<repo_name>", "<reponame>",
    "<file_sep>", "<filename>", "<gh_stars>", "<issue_start>", "<issue_comment>",
    "<issue_closed>", "<jupyter_start>", "<jupyter_text>", "<jupyter_code>",
    "<jupyter_output>", "<jupyter_script>", "<empty_output>",
]


def build_encode_corpus(tok):
    """Mode A = oracle encode_special_tokens=True (literal text; the pinned
    Phase 5B default). Mode B = oracle encode_special_tokens=False (the
    oracle's OWN default; recognizes CONTROL strings). Verified empirically
    against the live oracle before being encoded into this generator -- see
    DECISION_LOG.md OE-ADR-034."""
    corpus = []

    def add(id_, text, mode):
        tok.encode_special_tokens = (mode == "A")
        ids = tok.encode(text).ids
        # Determinism: re-encode and require identical ids.
        tok.encode_special_tokens = (mode == "A")
        ids2 = tok.encode(text).ids
        assert ids == ids2, f"non-deterministic encode for {id_}/{mode}"
        corpus.append({"id": id_, "mode": mode, "text": text, "ids": ids})

    ascii_and_unicode_cases = [
        ("empty", ""),
        ("ascii_words", "Hello, world! This is a test: 123."),
        ("ascii_punct_only", "!@#$%^&*()_+-=[]{}|;':\",./<>?"),
        ("contractions", "don't isn't I've I'll I'm I'd we're can't"),
        ("leading_ws", "   leading spaces"),
        ("trailing_ws", "trailing spaces   "),
        ("repeated_ws", "a    b     c"),
        ("tabs", "a\tb\tc"),
        ("lf", "line one\nline two"),
        ("crlf", "line one\r\nline two"),
        ("digit_long_run", "The year 20231231 was long: 999999999999"),
        ("arabic_indic_digits", "١٢٣"),
        ("fullwidth_digits", "１２３"),
        ("non_ascii_latin", "café résumé naïve Zürich"),
        ("non_ascii_cjk", "你好世界 こんにちは 한국어"),
        ("emoji", "Hello \U0001F600 World \U0001F601\U0001F602"),
        ("combining_marks", "é à ñ"),
        ("embedded_nul", "before\x00after"),
        ("special_token_lookalike", "text with <|endoftext|> inside and <|im_start|> too"),
    ]
    for id_, text in ascii_and_unicode_cases:
        add(id_, text, "A")

    # golden + raw-prompt fixtures, Mode A (both are ordinary user text by
    # construction; none is expected to contain an intentional literal
    # control-token spelling meant to be recognized).
    gf = json.loads(GOLDEN_FIXTURES.read_text(encoding="utf-8"))
    for f in gf["fixtures"]:
        raw_repr = f.get("raw_text_repr") or f.get("original_text_repr")
        if raw_repr is None:
            continue
        raw_text = ast.literal_eval(raw_repr)
        add("golden__" + f["fixture_id"], raw_text, "A")

    rp = json.loads(RAW_PROMPT_MANIFEST.read_text(encoding="utf-8"))
    for r in rp["records"]:
        add("rawprompt__" + r["fixture_id"], r["raw_text"], "A")

    # Mode A: every CONTROL string alone must NOT collapse to its single ID.
    for s in CONTROL_STRINGS:
        add("modeA_control__" + s, s, "A")

    # Mode B: every CONTROL string alone recognized to its exact ID.
    for s in CONTROL_STRINGS:
        add("modeB_control__" + s, s, "B")

    # Mode B: adjacent CONTROL x CONTROL (all 17x17 pairs -- exhaustive,
    # cheap, and the strongest possible precedence/overlap proof given none
    # of the 17 strings is a literal prefix of another).
    for a in CONTROL_STRINGS:
        for b in CONTROL_STRINGS:
            add(f"modeB_adjacent__{a}__{b}", a + b, "B")

    # Mode B: partial spellings and case-changed lookalikes remain ordinary text.
    for s in ["<|endoftext", "endoftext|>", "<|end", "<|im_star", "im_start|>",
              "<|ENDOFTEXT|>", "<|EndOfText|>", "<REPO_NAME>", "<Repo_Name>"]:
        add("modeB_nonmatch__" + s, s, "B")

    # Mode B: mixed ordinary/control/ordinary, preserving exact order.
    add("modeB_mixed_1", "abc <file_sep> def <gh_stars> ghi", "B")
    add("modeB_mixed_2", "before <|endoftext|> after", "B")

    # BPE-adversarial cases (see DECISION_LOG.md OE-ADR-034 for why each
    # distinguishes correct ranked behavior from a plausible wrong one).
    bpe_cases = [
        ("bpe_no_merge_pretoken", "!"),
        ("bpe_one_merge", "in"),
        ("bpe_multi_sequential_merge", "unbelievably"),
        ("bpe_repeated_adjacent_pair", "aaa"),
        ("bpe_repeated_adjacent_pair_long", "aaaaaaaaaa"),
        ("bpe_multi_simultaneous_pairs", "abcabc"),
        ("bpe_competing_ranks", "wonderful transformation"),
        ("bpe_long_bounded", "The quick brown fox jumps over the lazy dog. " * 4),
    ]
    for id_, text in bpe_cases:
        add(id_, text, "A")

    return corpus


# ---------------------------------------------------------------------------
# C++ header emission
# ---------------------------------------------------------------------------

def cpp_bytes_literal(raw: bytes) -> str:
    return '"' + "".join(f"\\x{b:02x}" for b in raw) + '"'


def emit_tables_header(ranges, s_ranges, python_version, unicodedata_version):
    L = []
    L.append("// Copyright (C) 2025-present hardcoreerik / TheOrc contributors")
    L.append("// SPDX-License-Identifier: AGPL-3.0-or-later")
    L.append("//")
    L.append("// GENERATED FILE -- do not hand-edit. Produced by")
    L.append("// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.")
    L.append("//")
    L.append("// Provenance (Stage 2A pretokenization tables):")
    L.append(f"//   python_version        : {python_version}")
    L.append(f"//   unicodedata_version   : {unicodedata_version}")
    L.append(f"//   tokenizers_version    : {REQUIRED_TOKENIZERS_VERSION} (pinned oracle, verified at")
    L.append("//                           generation time -- see generate_pretok_tables.py)")
    L.append("//   tokenizer_source      : smollm2-135m/tokenizer.json, pinned revision")
    L.append("//                           93efa2f097d58c2a74874c7e644dbc9b0cee75a2, sha256-pinned")
    L.append("//                           and contract-verified by the generator before use")
    L.append("//")
    L.append("// Full hash record (tokenizer.json, generator, and all 5 generated headers")
    L.append("// -- this file plus the fixture/byte-alphabet/encode headers) is recorded in")
    L.append("// docs/OrcEngine/DECISION_LOG.md OE-ADR-033/OE-ADR-034/OE-ADR-035 (OE-ADR-035")
    L.append("// is the current record, following the Stage 2B reconciliation pass), not")
    L.append("// embedded in this file (a file must not carry its own hash).")
    L.append("//")
    L.append("// L (\\p{L}) / N (\\p{N}) candidate ranges: Unicode General Category")
    L.append("// L*(Lu,Ll,Lt,Lm,Lo) / N*(Nd,Nl,No), computed from the unicodedata version")
    L.append("// above. S (\\s): the FIXED, version-stable Unicode White_Space=Y property")
    L.append("// list (25 codepoints/ranges) -- NOT Python str.isspace(), which incorrectly")
    L.append("// includes U+001C-U+001F; see this script's module docstring for the full")
    L.append("// discovery narrative and DECISION_LOG.md OE-ADR-032 for the record.")
    L.append("//")
    L.append("// Matches the pinned oracle across the 63-entry corpus, all generated")
    L.append("// category boundaries, and the recorded seeded sample; exhaustive")
    L.append("// equivalence over every possible Unicode string is not claimed. See this")
    L.append("// script's validate_boundaries()/validate_random()/validate_digits_stage()")
    L.append("// for exact counts and DECISION_LOG.md OE-ADR-032/OE-ADR-033 for the record.")
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
    F.append("// true)) pretokenizer, loaded from the pinned smollm2-135m/tokenizer.json")
    F.append("// (sha256- and contract-verified by the generator before use -- see")
    F.append("// docs/OrcEngine/DECISION_LOG.md OE-ADR-033 for the full hash record).")
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


def emit_byte_alphabet_header(b2u):
    B = []
    B.append("// Copyright (C) 2025-present hardcoreerik / TheOrc contributors")
    B.append("// SPDX-License-Identifier: AGPL-3.0-or-later")
    B.append("//")
    B.append("// GENERATED FILE -- do not hand-edit. Produced by")
    B.append("// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.")
    B.append("//")
    B.append("// The 256-entry GPT-2 byte-to-Unicode-codepoint mapping, cross-checked")
    B.append("// against the live tokenizers==0.22.2 oracle (ByteLevel.alphabet() set")
    B.append("// equality plus a direct per-byte mapped-character check for every byte")
    B.append("// value reachable through valid UTF-8). See DECISION_LOG.md OE-ADR-034.")
    B.append("#pragma once")
    B.append("")
    B.append("#include <cstddef>")
    B.append("#include <cstdint>")
    B.append("")
    B.append("namespace orcengine::pretok_fixtures {")
    B.append("")
    B.append("inline constexpr std::uint32_t kOracleByteToCodepoint[256] = {")
    for i in range(0, 256, 16):
        row = ", ".join(f"0x{b2u[b]:X}" for b in range(i, i + 16))
        B.append(f"    {row},")
    B.append("};")
    B.append("")
    B.append("}  // namespace orcengine::pretok_fixtures")
    B.append("")
    return "\n".join(B)


def emit_encode_header(encode_fixtures):
    E = []
    E.append("// Copyright (C) 2025-present hardcoreerik / TheOrc contributors")
    E.append("// SPDX-License-Identifier: AGPL-3.0-or-later")
    E.append("//")
    E.append("// GENERATED FILE -- do not hand-edit. Produced by")
    E.append("// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.")
    E.append("//")
    E.append("// Stage 2B encode() oracle fixtures: exact token-ID sequences from the")
    E.append("// real tokenizers==0.22.2 Tokenizer.encode(), loaded from the pinned")
    E.append("// smollm2-135m/tokenizer.json (sha256- and contract-verified by the")
    E.append("// generator before use -- see docs/OrcEngine/DECISION_LOG.md OE-ADR-034")
    E.append("// for the full hash record). mode 'A' = oracle encode_special_tokens=True")
    E.append("// (native LiteralText); mode 'B' = oracle encode_special_tokens=False,")
    E.append("// the oracle's OWN default (native RecognizeControlTokens). Every entry")
    E.append("// was computed twice and asserted identical before being written here.")
    E.append(f"//   corpus size           : {len(encode_fixtures)}")
    E.append("#pragma once")
    E.append("")
    E.append("#include <cstddef>")
    E.append("#include <cstdint>")
    E.append("#include <string_view>")
    E.append("")
    E.append("namespace orcengine::pretok_fixtures {")
    E.append("")
    E.append("struct EncodeFixture {")
    E.append("    const char* id;")
    E.append("    char mode;  // 'A' = LiteralText, 'B' = RecognizeControlTokens")
    E.append("    std::string_view utf8;")
    E.append("    const std::int64_t* ids;")
    E.append("    std::size_t id_count;")
    E.append("};")
    E.append("")

    id_arrays = []
    entries = []
    for i, e in enumerate(encode_fixtures):
        arr_name = f"kIds_{i}"
        ids = e["ids"]
        if ids:
            arr = f"inline constexpr std::int64_t {arr_name}[] = {{" + ", ".join(str(x) for x in ids) + "};"
        else:
            arr = f"inline constexpr std::int64_t* {arr_name} = nullptr;"
        id_arrays.append(arr)
        utf8 = e["text"].encode("utf-8")
        lit = cpp_bytes_literal(utf8) if utf8 else '""'
        safe_id = e["id"].replace("\\", "\\\\").replace('"', '\\"')
        entries.append(
            f'    {{"{safe_id}", \'{e["mode"]}\', std::string_view({lit}, {len(utf8)}), {arr_name}, {len(ids)}}},'
        )

    E.extend(id_arrays)
    E.append("")
    E.append("inline constexpr EncodeFixture kEncodeFixtures[] = {")
    E.extend(entries)
    E.append("};")
    E.append(f"inline constexpr std::size_t kEncodeFixtureCount = {len(encode_fixtures)};")
    E.append("")
    E.append("}  // namespace orcengine::pretok_fixtures")
    E.append("")
    return "\n".join(E)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--tokenizer-json",
        required=True,
        type=Path,
        help="Path to the pinned smollm2-135m/tokenizer.json (sha256- and contract-verified before use).",
    )
    parser.add_argument(
        "--check",
        action="store_true",
        help="Read-only: regenerate all output in memory and compare byte-for-byte against the "
             "committed headers. Exits nonzero on drift. Never writes any file.",
    )
    args = parser.parse_args()

    t0 = time.time()
    python_version = sys.version.split()[0]
    print(f"python_version={python_version} tokenizers_version={_tokenizers_pkg.__version__} "
          f"unicodedata_version={unicodedata.unidata_version}")

    tokenizer_json_path = args.tokenizer_json.resolve()
    tokenizer_json_sha256 = verify_tokenizer_identity_and_contract(tokenizer_json_path)
    print(f"tokenizer.json verified: {tokenizer_json_path} sha256={tokenizer_json_sha256}")

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
        print("ABORTING: unresolved mismatches, not writing or checking headers.")
        sys.exit(1)

    tok = Tokenizer.from_file(str(tokenizer_json_path))
    fixtures = build_corpus(tok)
    print(f"[{time.time()-t0:.2f}s] oracle fixture corpus: {len(fixtures)} entries")

    b2u = build_byte_to_unicode()
    byte_ok, byte_details = verify_byte_to_unicode(b2u)
    print(f"[{time.time()-t0:.2f}s] byte-to-unicode verification: {'OK' if byte_ok else 'FAILED'}")
    for d in byte_details:
        print(f"    MISMATCH {d}")
    if not byte_ok:
        print("ABORTING: byte-to-unicode mismatches, not writing or checking headers.")
        sys.exit(1)

    encode_fixtures = build_encode_corpus(tok)
    print(f"[{time.time()-t0:.2f}s] encode oracle fixture corpus: {len(encode_fixtures)} entries")

    tables_text = emit_tables_header(ranges, s_ranges, python_version, unicodedata.unidata_version)
    fixtures_text = emit_fixtures_header(fixtures)
    invalid_text = emit_invalid_header(INVALID_UTF8_CASES)
    byte_alphabet_text = emit_byte_alphabet_header(b2u)
    encode_text = emit_encode_header(encode_fixtures)

    outputs = [
        (OUT_TABLES_HPP, tables_text, "tables"),
        (OUT_FIXTURES_HPP, fixtures_text, "fixtures"),
        (OUT_INVALID_HPP, invalid_text, "invalid"),
        (OUT_BYTE_ALPHABET_HPP, byte_alphabet_text, "byte_alphabet"),
        (OUT_ENCODE_HPP, encode_text, "encode"),
    ]

    for path, text, name in outputs:
        print(f"{name}: {len(text)} bytes, sha256={hashlib.sha256(text.encode()).hexdigest()}")

    if args.check:
        drift = []
        for path, text, name in outputs:
            resolved = path.resolve()
            if not resolved.is_file():
                drift.append((name, resolved, "MISSING"))
                continue
            existing_bytes = resolved.read_bytes()
            if existing_bytes != text.encode("utf-8"):
                drift.append((name, resolved, "DIFFERS"))
        if drift:
            print("CHECK FAILED -- generated content differs from committed files:")
            for name, resolved, reason in drift:
                print(f"    {name} ({resolved}): {reason}")
            sys.exit(1)
        print(f"CHECK PASSED -- all {len(outputs)} generated headers match the committed files "
              f"byte-for-byte (in {time.time()-t0:.2f}s). No file was written.")
        return

    for path, text, _name in outputs:
        path.resolve().write_text(text, encoding="utf-8", newline="\n")

    print(f"done in {time.time()-t0:.2f}s")


if __name__ == "__main__":
    main()
