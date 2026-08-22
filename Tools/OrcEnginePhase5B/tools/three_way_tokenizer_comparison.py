# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 5B post-freeze evidence hardening (Finding A3): the committed,
independently-reproducible three-way tokenizer comparison driver --
Hugging Face `tokenizers==0.22.2` / pinned llama.cpp b10436 / native
OrcEngine Phase 5B -- over a durably-defined representative corpus.

Prior to this script, the three-way agreement claimed in DECISION_LOG.md
OE-ADR-036 was narrative-only: no committed driver defined the exact
15-item representative subset or re-executed it. This script IS that
driver. It reuses the existing Phase 0 oracle's HF-invocation and
llama.cpp-invocation patterns (tokenizer_dual_source_check.py) rather
than reimplementing tokenization comparison from scratch, and adds the
native leg via the byte-safe native_tokenize_cli.exe (one invocation per
complete-stdin prompt, per the same post-freeze hardening pass's Finding
A2 correction).

Usage:
    python three_way_tokenizer_comparison.py <path-to-orcengine_native_tokenize.exe>

Requires (fails closed, does not silently skip):
    - tokenizers==0.22.2 importable (checked by version string)
    - the pinned tokenizer.json (SHA-256-checked)
    - the canonical smollm2-135m.gguf (SHA-256-checked)
    - ORC_LLAMA_TOKENIZE_PATH set, pointing at a binary whose --version
      output identifies build 10436 / commit 6fed9f6ff
"""
from __future__ import annotations

import hashlib
import json
import os
import subprocess
import sys

import tokenizers
from tokenizers import Tokenizer

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
TOKENIZER_JSON_PATH = os.path.join(SCRIPT_DIR, "..", "..", "OrcEnginePhase0", "artifacts",
                                    "smollm2-135m", "tokenizer.json")
GGUF_PATH = os.path.join(SCRIPT_DIR, "..", "..", "OrcEnginePhase0", "artifacts", "smollm2-135m.gguf")
LLAMA_TOKENIZE_PATH = os.environ.get("ORC_LLAMA_TOKENIZE_PATH", "")

EXPECTED_TOKENIZERS_VERSION = "0.22.2"
EXPECTED_TOKENIZER_JSON_SHA256 = "9ca9acddb6525a194ec8ac7a87f24fbba7232a9a15ffa1af0c1224fcd888e47c"
EXPECTED_GGUF_SHA256 = "fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac"
EXPECTED_LLAMA_BUILD = "10436"
EXPECTED_LLAMA_COMMIT = "6fed9f6ff"

# --- Durably-defined representative corpus. -------------------------------
# category -> (fixture_id, text). "canonical" entries are the five original
# dual-source fixtures tokenizer_dual_source_check.py has always used;
# reusing the identical strings here, not redefining a near-duplicate set.
CORPUS = [
    ("canonical_capital_of_france", "canonical", "The capital of France is"),
    ("canonical_hello_world", "canonical", "Hello, world!"),
    ("canonical_digits", "canonical", "12345 test"),
    ("canonical_leading_spaces", "canonical", "  leading spaces"),
    ("canonical_unicode_latin", "canonical", "unicode: café résumé"),
    ("ascii_words", "ascii", "Hello, world! This is a test: 123."),
    ("punctuation", "punctuation", "!@#$%^&*()_+-=[]{}|;':\",./<>?"),
    ("contractions", "contractions", "don't isn't I've I'll I'm I'd we're can't"),
    ("non_ascii_latin", "non_ascii_latin", "café résumé naïve Zürich"),
    ("non_ascii_cjk", "non_ascii_cjk", "你好世界 こんにちは 한국어"),
    ("emoji", "emoji", "Hello \U0001F600 World \U0001F601\U0001F602"),
    ("digit_long_run", "digit_long_run", "The year 20231231 was long: 999999999999"),
    ("arabic_indic_digits", "arabic_indic_digits", "١٢٣"),
    ("tabs", "whitespace", "a\tb\tc"),
    ("embedded_lf", "whitespace", "line one\nline two"),
    ("embedded_crlf", "whitespace", "line one\r\nline two"),
    ("trailing_whitespace", "whitespace", "trailing spaces   "),
    ("repeated_whitespace", "whitespace", "a    b     c"),
    ("control_lookalike", "control_lookalike", "text with <|endoftext|> inside and <|im_start|> too"),
]
# Embedded NUL is deliberately NOT included: llama-tokenize.exe's `-p`
# argument is a C-string passed through argv, which cannot carry an
# embedded NUL byte at all (not a policy choice, a hard interface
# limitation of that CLI) -- native_tokenize_cli.exe and the HF Python
# leg both support it, but llama.cpp does not, so a three-way NUL fixture
# cannot be run as specified ("if supported by EVERY invoked interface").
# Recorded here rather than silently omitted.
NUL_FIXTURE_NOTE = (
    "embedded_nul_char: EXCLUDED from this driver's corpus -- "
    "llama-tokenize.exe's -p argument is argv-based and cannot carry an "
    "embedded NUL byte; native and HF both support it (see test_decode.cpp's "
    "own embedded-NUL coverage via test_encode.cpp's oracle fixtures), so "
    "this is a documented llama.cpp CLI interface limitation, not a "
    "three-way disagreement."
)


def sha256_file(path: str) -> str:
    return hashlib.sha256(open(path, "rb").read()).hexdigest()


def sha256_ids(ids) -> str:
    return hashlib.sha256(",".join(str(i) for i in ids).encode("utf-8")).hexdigest()


def verify_environment(native_cli_path: str) -> None:
    actual_version = tokenizers.__version__
    if actual_version != EXPECTED_TOKENIZERS_VERSION:
        sys.exit(f"ABORT: tokenizers=={actual_version}, expected =={EXPECTED_TOKENIZERS_VERSION}")

    actual_tok_hash = sha256_file(TOKENIZER_JSON_PATH)
    if actual_tok_hash != EXPECTED_TOKENIZER_JSON_SHA256:
        sys.exit(f"ABORT: tokenizer.json sha256={actual_tok_hash}, expected={EXPECTED_TOKENIZER_JSON_SHA256}")

    if not os.path.isfile(GGUF_PATH):
        sys.exit(f"ABORT: {GGUF_PATH!r} not found")
    actual_gguf_hash = sha256_file(GGUF_PATH)
    if actual_gguf_hash != EXPECTED_GGUF_SHA256:
        sys.exit(f"ABORT: smollm2-135m.gguf sha256={actual_gguf_hash}, expected={EXPECTED_GGUF_SHA256}")

    if not LLAMA_TOKENIZE_PATH:
        sys.exit("ABORT: ORC_LLAMA_TOKENIZE_PATH is not set")
    if not os.path.isfile(LLAMA_TOKENIZE_PATH):
        sys.exit(f"ABORT: ORC_LLAMA_TOKENIZE_PATH={LLAMA_TOKENIZE_PATH!r} does not exist")
    proc = subprocess.run([LLAMA_TOKENIZE_PATH, "--version"], capture_output=True, timeout=30)
    version_out = (proc.stdout + proc.stderr).decode("utf-8", errors="replace")
    if EXPECTED_LLAMA_BUILD not in version_out or EXPECTED_LLAMA_COMMIT not in version_out:
        sys.exit(f"ABORT: llama-tokenize --version did not confirm build {EXPECTED_LLAMA_BUILD} / "
                  f"commit {EXPECTED_LLAMA_COMMIT}; got: {version_out!r}")

    if not os.path.isfile(native_cli_path):
        sys.exit(f"ABORT: native CLI {native_cli_path!r} does not exist -- build "
                  f"orcengine_native_tokenize first")


def hf_tokenize(tok: Tokenizer, text: str, recognize_control_tokens: bool) -> list[int]:
    tok.encode_special_tokens = not recognize_control_tokens  # native LiteralText == HF encode_special_tokens=True
    return tok.encode(text).ids


def llama_cpp_tokenize(text: str) -> list[int]:
    proc = subprocess.run([LLAMA_TOKENIZE_PATH, "-m", GGUF_PATH, "-p", text, "--ids"],
                          capture_output=True, timeout=30)
    stdout = proc.stdout.decode("utf-8", errors="replace")
    for line in reversed(stdout.splitlines()):
        line = line.strip()
        if line.startswith("[") and line.endswith("]"):
            return json.loads(line)
    raise RuntimeError(f"could not find token ID list in llama-tokenize output: {stdout!r}")


def native_tokenize(native_cli_path: str, text: str, recognize_control_tokens: bool) -> list[int]:
    args = [native_cli_path, GGUF_PATH]
    if recognize_control_tokens:
        args.append("--recognize-control-tokens")
    proc = subprocess.run(args, input=text.encode("utf-8"), capture_output=True, timeout=30)
    if proc.returncode != 0:
        raise RuntimeError(f"native CLI exited {proc.returncode}: {proc.stderr.decode('utf-8', errors='replace')}")
    line = proc.stdout.decode("utf-8", errors="replace").strip()
    return [int(x) for x in line.split(",")] if line else []


def run(native_cli_path: str) -> bool:
    verify_environment(native_cli_path)
    tok = Tokenizer.from_file(TOKENIZER_JSON_PATH)

    all_ok = True
    exact_three_way = 0
    two_way_policy_limited = 0

    print(f"=== Three-way tokenizer comparison ({len(CORPUS)} fixtures) ===")
    print(f"tokenizers=={tokenizers.__version__}, tokenizer.json sha256={EXPECTED_TOKENIZER_JSON_SHA256[:16]}..., "
          f"gguf sha256={EXPECTED_GGUF_SHA256[:16]}..., llama.cpp build {EXPECTED_LLAMA_BUILD}")
    print(NUL_FIXTURE_NOTE)
    print()

    for fixture_id, category, text in CORPUS:
        hf_ids = hf_tokenize(tok, text, recognize_control_tokens=False)
        native_ids = native_tokenize(native_cli_path, text, recognize_control_tokens=False)
        llama_ids = llama_cpp_tokenize(text)

        hf_native_agree = hf_ids == native_ids
        if category == "control_lookalike":
            # llama-tokenize.exe's --ids CLI has no literal-text mode -- it
            # always recognizes CONTROL substrings (established and
            # root-caused in OE-ADR-036's A4 finding). Comparing it against
            # HF/native's LiteralText result here would be an incomparable-
            # mode false failure, not a real disagreement -- so for this
            # ONE fixture, the three-way check is done in RECOGNIZE mode
            # instead, where all three interfaces share a common policy.
            hf_recognize_ids = hf_tokenize(tok, text, recognize_control_tokens=True)
            native_recognize_ids = native_tokenize(native_cli_path, text, recognize_control_tokens=True)
            three_way_recognize_agree = hf_recognize_ids == native_recognize_ids == llama_ids
            ok = hf_native_agree and three_way_recognize_agree
            all_ok = all_ok and ok
            two_way_policy_limited += 1
            print(f"[{'PASS' if ok else 'FAIL'}] {fixture_id} ({category})")
            print(f"  LiteralText   : HF={hf_ids} native={native_ids} "
                  f"(2-way agree={hf_native_agree}; llama.cpp CANNOT exercise this mode -- interface limitation, "
                  f"not compared)")
            print(f"  RecognizeCtrl : HF={hf_recognize_ids} native={native_recognize_ids} llama.cpp={llama_ids} "
                  f"(3-way agree={three_way_recognize_agree})")
            if not ok:
                print("  MISMATCH -- see values above")
            continue

        ok = hf_ids == native_ids == llama_ids
        all_ok = all_ok and ok
        if ok:
            exact_three_way += 1
        print(f"[{'PASS' if ok else 'FAIL'}] {fixture_id} ({category})")
        print(f"  HF     : {hf_ids}")
        print(f"  native : {native_ids}")
        print(f"  llama  : {llama_ids}")
        if not ok:
            print(f"  MISMATCH -- hf==native:{hf_ids == native_ids} hf==llama:{hf_ids == llama_ids} "
                  f"native==llama:{native_ids == llama_ids}")

    print()
    print(f"Exact three-way agreement: {exact_three_way}/{len(CORPUS) - two_way_policy_limited} ordinary fixtures")
    print(f"Policy-limited fixtures (2-way LiteralText + 3-way RecognizeControlTokens): "
          f"{two_way_policy_limited}/{len(CORPUS)}")
    print(f"Excluded (interface limitation, not run): 1 (embedded_nul_char)")
    return all_ok


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(f"usage: {sys.argv[0]} <path-to-orcengine_native_tokenize.exe>")
    ok = run(sys.argv[1])
    print()
    print("PASS: three_way_tokenizer_comparison" if ok else "FAIL: three_way_tokenizer_comparison")
    sys.exit(0 if ok else 1)
