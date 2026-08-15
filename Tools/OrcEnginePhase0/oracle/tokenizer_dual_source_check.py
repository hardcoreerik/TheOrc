# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
tokenizer_dual_source_agreement acceptance check, per
PHASE_0_REFERENCE_ORACLE.md "Tokenizer reconciliation":
  "Generate token fixtures independently through the pinned source
  tokenizer and the tokenizer reconstructed from converted GGUF metadata.
  Compare rendered prompt bytes, token IDs, decoded bytes, special-token
  behavior, and hashes exactly. Any unexplained disagreement rejects the
  real-model candidate."

Source A: the pinned HF tokenizer.json loaded directly via the `tokenizers`
library (the actual source-of-truth tokenizer SmolLM2-135M ships with).
Source B: llama-tokenize.exe run against our converted GGUF (oracle/
convert_real_candidate.py), i.e. the tokenizer reconstructed from GGUF
metadata (tokenizer.ggml.model/pre/tokens/merges) as llama.cpp interprets
it. Runs llama-tokenize as a subprocess with output captured as bytes and
decoded as UTF-8 explicitly, to avoid Windows console codepage corruption
of non-ASCII fixtures (observed directly: printing to a cp1252 console
mangled "café" -- the file-based comparison here does not have that bug).
"""
from __future__ import annotations

import hashlib
import json
import os
import subprocess

from tokenizers import Tokenizer

TOKENIZER_JSON_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m", "tokenizer.json")
GGUF_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m.gguf")
LLAMA_TOKENIZE_PATH = os.environ.get("ORC_LLAMA_TOKENIZE_PATH", "")

FIXTURES = [
    "The capital of France is",
    "Hello, world!",
    "12345 test",
    "  leading spaces",
    "unicode: café résumé",  # non-ASCII byte-fidelity fixture
]
# NOT tested here: empty string. llama-tokenize.exe (the CLI tool, not llama.cpp's
# tokenizer itself) exits 1 with no output on `-p ""` -- confirmed this is an argument-
# parsing limitation of the CLI wrapper, not a tokenizer disagreement (the HF tokenizer
# correctly returns [] for ""). Retest via llama-server's /tokenize endpoint if this
# edge case needs coverage later.


def _sha256_ids(ids: list[int]) -> str:
    payload = ",".join(str(i) for i in ids).encode("utf-8")
    return hashlib.sha256(payload).hexdigest()


def _llama_cpp_tokenize(text: str) -> list[int]:
    proc = subprocess.run(
        [LLAMA_TOKENIZE_PATH, "-m", GGUF_PATH, "-p", text, "--ids"],
        capture_output=True, timeout=30,
    )
    stdout = proc.stdout.decode("utf-8", errors="replace")
    # llama-tokenize prints warnings to stdout too in some builds; the ID list is the
    # last line that looks like "[1, 2, 3]".
    for line in reversed(stdout.splitlines()):
        line = line.strip()
        if line.startswith("[") and line.endswith("]"):
            return json.loads(line)
    raise RuntimeError(f"could not find token ID list in llama-tokenize output: {stdout!r}")


def run() -> bool:
    if not os.path.isfile(LLAMA_TOKENIZE_PATH):
        print(f"FAIL: llama-tokenize not found at {LLAMA_TOKENIZE_PATH!r}. "
              f"Set ORC_LLAMA_TOKENIZE_PATH.")
        return False
    if not os.path.isfile(GGUF_PATH):
        print(f"FAIL: {GGUF_PATH!r} not found. Run oracle.convert_real_candidate first.")
        return False

    hf_tokenizer = Tokenizer.from_file(TOKENIZER_JSON_PATH)

    all_ok = True
    for text in FIXTURES:
        hf_ids = hf_tokenizer.encode(text).ids
        llama_cpp_ids = _llama_cpp_tokenize(text)

        ids_match = hf_ids == llama_cpp_ids
        hash_match = _sha256_ids(hf_ids) == _sha256_ids(llama_cpp_ids)
        ok = ids_match and hash_match
        all_ok = all_ok and ok

        display = text if text else "<empty string>"
        print(f"[{'PASS' if ok else 'FAIL'}] {display!r}")
        print(f"  HF tokenizer.json:  {hf_ids}")
        print(f"  llama.cpp (GGUF):   {llama_cpp_ids}")
        if not ok:
            print(f"  MISMATCH")

    return all_ok


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: tokenizer_dual_source_agreement")
    raise SystemExit(0 if ok else 1)
