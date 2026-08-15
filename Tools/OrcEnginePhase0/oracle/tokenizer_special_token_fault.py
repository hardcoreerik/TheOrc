# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
The 7th and final required fault type for fault_injection, per
PHASE_0_REFERENCE_ORACLE.md's "Fault-injection proof": tokenizer
special-token error. Needed a real tokenizer to test against -- Profile A
has none, which is why this was deferred until the real-candidate
conversion (oracle/convert_real_candidate.py) existed.

Fault: the token "<|im_start|>" (id=1) is correctly TOKEN_TYPE=CONTROL in
the real conversion, meaning llama.cpp's tokenizer matches it as a single
special token by exact string search. This script writes a FAULTED GGUF
where that same token is mislabeled TOKEN_TYPE=NORMAL, so llama.cpp instead
tries to tokenize the literal substring "<|im_start|>" through the normal
byte-level BPE algorithm -- producing a different token sequence than
either the correct GGUF or the true HF tokenizer.

Detection: tokenize a prompt containing "<|im_start|>" against both GGUFs.
The correct GGUF must match the true HF tokenizer (already proven in
oracle/tokenizer_dual_source_check.py); the faulted GGUF must diverge from
BOTH the correct GGUF and the true HF tokenizer. Divergence-from-truth is
the actual proof, not merely "the two GGUFs differ from each other."
"""
from __future__ import annotations

import json
import os
import subprocess

from gguf import TokenType
from tokenizers import Tokenizer

from oracle.convert_real_candidate import SOURCE_DIR, _load_config, _load_tokenizer_arrays, write_gguf

FAULTED_GGUF_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m-faulted-special-token.gguf")
CORRECT_GGUF_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m.gguf")
# No hardcoded local-account default (CodeRabbit finding, PR #102: a hardcoded
# C:\Users\<name>\... path published a local username in the repo). Must be set
# explicitly via env var; run() fails loudly with a clear message if it isn't.
LLAMA_TOKENIZE_PATH = os.environ.get("ORC_LLAMA_TOKENIZE_PATH", "")
TOKENIZER_JSON_PATH = os.path.join(SOURCE_DIR, "tokenizer.json")

FAULT_TOKEN = "<|im_start|>"
PROMPT = "<|im_start|>user"


def _write_faulted_gguf() -> str:
    config = _load_config()
    tokens, merges, token_types = _load_tokenizer_arrays()

    fault_idx = tokens.index(FAULT_TOKEN)
    assert token_types[fault_idx] == int(TokenType.CONTROL), \
        f"expected {FAULT_TOKEN!r} to be CONTROL before faulting"
    token_types = list(token_types)
    token_types[fault_idx] = int(TokenType.NORMAL)  # THE FAULT: control -> normal

    # Same writer as the correct conversion (oracle.convert_real_candidate.write_gguf) --
    # only token_types differs, so this fault is isolated to exactly the one variable
    # under test (CodeRabbit finding, PR #102: previously a hand-duplicated copy of the
    # whole writer, which could silently drift from convert_real_candidate.py over time).
    return write_gguf(FAULTED_GGUF_PATH, "SmolLM2-135M-faulted-special-token", config,
                       tokens, merges, token_types)


def _llama_cpp_tokenize(gguf_path: str, text: str) -> list[int]:
    proc = subprocess.run(
        [LLAMA_TOKENIZE_PATH, "-m", gguf_path, "-p", text, "--ids"],
        capture_output=True, timeout=30,
    )
    stdout = proc.stdout.decode("utf-8", errors="replace")
    for line in reversed(stdout.splitlines()):
        line = line.strip()
        if line.startswith("[") and line.endswith("]"):
            return json.loads(line)
    raise RuntimeError(f"could not find token ID list in output: {stdout!r}")


def run() -> bool:
    if not LLAMA_TOKENIZE_PATH:
        print("FAIL: ORC_LLAMA_TOKENIZE_PATH is not set (path to llama-tokenize.exe)")
        return False
    if not os.path.isfile(CORRECT_GGUF_PATH):
        print("FAIL: correct GGUF not found, run oracle.convert_real_candidate first")
        return False

    _write_faulted_gguf()

    true_ids = Tokenizer.from_file(TOKENIZER_JSON_PATH).encode(PROMPT).ids
    correct_gguf_ids = _llama_cpp_tokenize(CORRECT_GGUF_PATH, PROMPT)
    faulted_gguf_ids = _llama_cpp_tokenize(FAULTED_GGUF_PATH, PROMPT)

    print(f"prompt: {PROMPT!r}")
    print(f"  true (HF tokenizer.json):        {true_ids}")
    print(f"  correct GGUF (CONTROL type):     {correct_gguf_ids}")
    print(f"  faulted GGUF (NORMAL type):      {faulted_gguf_ids}")

    correct_matches_truth = correct_gguf_ids == true_ids
    faulted_diverges_from_truth = faulted_gguf_ids != true_ids
    faulted_diverges_from_correct = faulted_gguf_ids != correct_gguf_ids

    print(f"\n  correct GGUF matches true tokenizer: {correct_matches_truth}")
    print(f"  faulted GGUF diverges from truth:    {faulted_diverges_from_truth}")
    print(f"  faulted GGUF diverges from correct:  {faulted_diverges_from_correct}")

    return correct_matches_truth and faulted_diverges_from_truth and faulted_diverges_from_correct


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: tokenizer_special_token_error fault detected")
    raise SystemExit(0 if ok else 1)
