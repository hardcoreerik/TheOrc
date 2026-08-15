# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 3 prep (ENGINEERING_ROADMAP.md Phase 3, test/fixture work only, no
engine code -- see phase2_prep/README.md for why this is permitted under
the Phase 0 stop gate): the tokenizer golden-fixture corpus required by
docs/OrcEngine/TOKENIZER_AND_PROMPT_PIPELINE.md's "Golden fixtures" list:

  empty input; ASCII words and punctuation; leading/trailing/repeated
  whitespace; newline and tab; non-ASCII Latin, CJK, emoji, combining
  marks; embedded NUL byte if supported by API; text resembling special
  tokens; unknown or byte-fallback cases; BOS/EOS combinations; tokens
  that split a multibyte UTF-8 code point; encode-decode and
  decode-encode caveats.

  "Compare raw bytes, token IDs, token pieces, offsets where available,
  and decoded bytes."

Runs against the REAL pinned tokenizer (SmolLM2-135M's tokenizer.json,
already downloaded by oracle/download_candidate.py) via the `tokenizers`
library -- the same "primary oracle" role oracle/tokenizer_dual_source_check.py
already uses, extended here to the full required category list rather than
5 spot-check fixtures.

Output goes to a JSON file, not stdout -- printing raw token piece strings
(which contain GPT2 byte-marker characters like U+0120) crashes on a
Windows console using a non-UTF-8 codepage. This is a real, repeatedly-hit
issue this session; writing to a UTF-8 file sidesteps it entirely.

REAL FINDING (2026-08-15): the two "text resembling special tokens"
fixtures do NOT round-trip exactly under this library's DEFAULT decode
behavior (skip_special_tokens=True) -- e.g. "this <|endoftext|> looks
like..." loses the literal "<|endoftext|>" substring on decode, because
token id 0 (SmolLM2's actual "<|endoftext|>" control token) gets silently
dropped. Confirmed this is exactly a skip_special_tokens default effect,
not a tokenizer bug: with skip_special_tokens=False, both fixtures
round-trip exactly. This is precisely the caveat
TOKENIZER_AND_PROMPT_PIPELINE.md's "special-token recognition policy...
explicit, never an invisible guess" requirement anticipates -- Phase 3
must pick and document a decode default deliberately, not inherit
whatever a library defaults to.
"""
from __future__ import annotations

import hashlib
import json
import os

from tokenizers import Tokenizer

TOKENIZER_JSON_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m", "tokenizer.json")
OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "tokenizer_golden_fixtures.json")


def _sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _sha256_ids(ids: list[int]) -> str:
    return _sha256_bytes(",".join(str(i) for i in ids).encode("utf-8"))


# (fixture_id, category, raw_text) -- category names match TOKENIZER_AND_PROMPT_PIPELINE.md's list.
FIXTURES: list[tuple[str, str, str]] = [
    ("empty_input", "empty_input", ""),
    ("ascii_words", "ascii_words_and_punctuation", "Hello, world! This is a test: 123."),
    ("ascii_punctuation_only", "ascii_words_and_punctuation", "!@#$%^&*()_+-=[]{}|;':\",./<>?"),
    ("leading_whitespace", "whitespace", "   leading spaces"),
    ("trailing_whitespace", "whitespace", "trailing spaces   "),
    ("repeated_whitespace", "whitespace", "a    b     c"),
    ("newline_and_tab", "whitespace", "line one\nline two\tafter tab"),
    ("only_whitespace", "whitespace", "   \n\t  "),
    ("non_ascii_latin", "non_ascii", "café résumé naïve Zürich"),
    ("non_ascii_cjk", "non_ascii", "你好世界 こんにちは世界 안녕하세요"),
    ("non_ascii_emoji", "non_ascii", "hello \U0001F600 world \U0001F30D emoji \U0001F680"),
    ("non_ascii_combining_marks", "non_ascii", "é à ô combining diacritics"),  # e + combining acute, etc.
    ("text_resembling_special_tokens", "special_token_lookalike", "this <|endoftext|> looks like a special token"),
    ("text_resembling_special_tokens_2", "special_token_lookalike", "<|im_start|>not really a chat turn<|im_end|>"),
    ("unusual_unicode_ranges", "unknown_or_byte_fallback", "☃❤﻿ mixed symbols and BOM-like char"),
    ("multibyte_utf8_boundary", "multibyte_utf8_boundary", "\U0001F600\U0001F601\U0001F602 consecutive multibyte emoji"),
    ("mixed_script", "non_ascii", "English 中文 日本語 한국어 العربية"),
]

# Embedded NUL byte -- handled separately since it needs bytes-level construction, not a plain
# Python string literal edge case, and the `tokenizers` library's Python API takes str, not bytes,
# so this fixture tests whether a NUL *character* (not byte, since Python str has no raw bytes
# concept until encoded) round-trips, which is the closest equivalent the API surface allows.
NUL_FIXTURE_TEXT = "before\x00after"


def _tokenize_fixture(tokenizer: Tokenizer, fixture_id: str, category: str, text: str) -> dict:
    raw_bytes = text.encode("utf-8")
    encoding = tokenizer.encode(text)
    decoded_default = tokenizer.decode(encoding.ids)  # skip_special_tokens=True, this library's default
    decoded_keep_special = tokenizer.decode(encoding.ids, skip_special_tokens=False)
    decoded_bytes = decoded_default.encode("utf-8")

    return {
        "fixture_id": fixture_id,
        "category": category,
        "raw_text_repr": repr(text),  # repr() so control/combining chars are visible & ASCII-safe in the JSON
        "raw_bytes_sha256": _sha256_bytes(raw_bytes),
        "raw_bytes_length": len(raw_bytes),
        "token_ids": encoding.ids,
        "token_ids_sha256": _sha256_ids(encoding.ids),
        "token_pieces": encoding.tokens,
        "offsets": list(encoding.offsets),
        "decoded_text_repr": repr(decoded_default),
        "decoded_bytes_sha256": _sha256_bytes(decoded_bytes),
        "encode_decode_round_trips_exactly": decoded_default == text,
        "encode_decode_round_trips_with_special_tokens_kept": decoded_keep_special == text,
    }


def run() -> bool:
    tokenizer = Tokenizer.from_file(TOKENIZER_JSON_PATH)
    results = []

    for fixture_id, category, text in FIXTURES:
        results.append(_tokenize_fixture(tokenizer, fixture_id, category, text))

    # Embedded NUL byte, handled explicitly (see NUL_FIXTURE_TEXT comment above).
    try:
        results.append(_tokenize_fixture(tokenizer, "embedded_nul_char", "embedded_nul", NUL_FIXTURE_TEXT))
        nul_supported = True
    except Exception as e:
        results.append({
            "fixture_id": "embedded_nul_char", "category": "embedded_nul",
            "raw_text_repr": repr(NUL_FIXTURE_TEXT), "error": f"{type(e).__name__}: {e}",
            "encode_decode_round_trips_exactly": False,
        })
        nul_supported = False

    # BOS/EOS combinations: SmolLM2's bos_token_id == eos_token_id == 0 (per config.json),
    # both == "<|endoftext|>". Test manual prepend/append against the tokenizer's own encoding,
    # since Tokenizer.encode() in this library takes add_special_tokens as a kwarg.
    bos_id = 0
    eos_id = 0
    plain = tokenizer.encode("test", add_special_tokens=False).ids
    with_specials = tokenizer.encode("test", add_special_tokens=True).ids
    results.append({
        "fixture_id": "bos_eos_combination", "category": "bos_eos_combinations",
        "plain_ids": plain,
        "with_add_special_tokens_true_ids": with_specials,
        "add_special_tokens_changes_output": plain != with_specials,
        "bos_id": bos_id, "eos_id": eos_id,
        "note": "SmolLM2-135M's bos_token_id == eos_token_id == 0 (both '<|endoftext|>'), per config.json",
    })

    # Encode-decode vs decode-encode caveat: does decode(encode(x)) == x, and does
    # encode(decode(encode(x))) == encode(x) (a weaker, idempotence-after-one-round property
    # that can hold even when the first isn't exact, e.g. due to whitespace normalization)?
    caveat_text = "  Multiple   spaces\tand\ttabs  "
    ids1 = tokenizer.encode(caveat_text).ids
    decoded1 = tokenizer.decode(ids1)
    ids2 = tokenizer.encode(decoded1).ids
    results.append({
        "fixture_id": "encode_decode_encode_caveat", "category": "encode_decode_caveat",
        "original_text_repr": repr(caveat_text),
        "first_decode_repr": repr(decoded1),
        "exact_round_trip": decoded1 == caveat_text,
        "ids_stable_after_one_round_trip": ids1 == ids2,
    })

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        json.dump({
            "tokenizer_source": "artifacts/smollm2-135m/tokenizer.json (SmolLM2-135M, pinned revision "
                                 "93efa2f097d58c2a74874c7e644dbc9b0cee75a2)",
            "categories_covered": sorted(set(r.get("category", "") for r in results)),
            "fixtures": results,
        }, f, indent=2, sort_keys=False, ensure_ascii=True)

    n_exact_round_trip = sum(1 for r in results if r.get("encode_decode_round_trips_exactly") is True)
    n_total_checked = sum(1 for r in results if "encode_decode_round_trips_exactly" in r)
    n_round_trip_with_special_kept = sum(
        1 for r in results if r.get("encode_decode_round_trips_with_special_tokens_kept") is True)
    print(f"wrote {len(results)} fixtures to {OUTPUT_PATH}")
    print(f"categories covered: {sorted(set(r.get('category', '') for r in results))}")
    print(f"exact encode-decode round trip (default decode): {n_exact_round_trip}/{n_total_checked} fixtures")
    print(f"exact encode-decode round trip (skip_special_tokens=False): "
          f"{n_round_trip_with_special_kept}/{n_total_checked} fixtures")
    if n_round_trip_with_special_kept > n_exact_round_trip:
        print("FINDING: default decode (skip_special_tokens=True) silently drops text that happens to "
              "match a special-token string -- see fixtures with 'special_token_lookalike' category. "
              "Phase 3 must choose and document this default deliberately, not inherit it silently.")
    print(f"embedded NUL character supported by this tokenizer's API: {nul_supported}")

    required_categories = {
        "empty_input", "ascii_words_and_punctuation", "whitespace", "non_ascii",
        "special_token_lookalike", "unknown_or_byte_fallback", "bos_eos_combinations",
        "multibyte_utf8_boundary", "encode_decode_caveat", "embedded_nul",
    }
    covered = set(r.get("category", "") for r in results)
    missing = required_categories - covered
    if missing:
        print(f"\nMISSING required categories: {missing}")
    return not missing


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: all required TOKENIZER_AND_PROMPT_PIPELINE.md golden-fixture categories covered")
    raise SystemExit(0 if ok else 1)
