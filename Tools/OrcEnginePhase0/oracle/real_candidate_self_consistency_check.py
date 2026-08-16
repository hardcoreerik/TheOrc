# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Diagnostic for the real_candidate_logits investigation (see
docs/OrcEngine/DECISION_LOG.md OE-ADR-017): compares our own NumPy
(oracle/model.py) and PyTorch (oracle/torch_oracle.py) implementations
against EACH OTHER on SmolLM2-135M's real weights, at real scale (30
layers, hidden=576). This isolates whether the large divergence seen
against llama.cpp (up to 0.95 in log-softmax, a top-3 ranking swap) comes
from a bug in our own code or is specific to llama.cpp's computation.

Result (2026-08-15): our two independent implementations agree to
max_abs_diff=3.29e-05 -- essentially perfect agreement, ~30000x tighter
than the divergence against llama.cpp. This rules out a bug in our own
forward pass at real scale. The remaining discrepancy is specific to
llama.cpp's computation (still unexplained; see this file's __main__ output
and DECISION_LOG.md for what's been ruled out so far: KV-cache precision).
"""
from __future__ import annotations

import numpy as np

from oracle.model import forward
from oracle.real_candidate_logits_check import load_real_weights
from oracle.torch_oracle import forward_torch
from tokenizers import Tokenizer
import os

TOKENIZER_JSON_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m", "tokenizer.json")

DISPUTED_TOKENS = [260, 1217, 7042, 3807, 1343, 253, 13010, 281, 216, 3365]


def run() -> bool:
    print("loading real SmolLM2-135M weights...")
    weights, config = load_real_weights()
    tokenizer = Tokenizer.from_file(TOKENIZER_JSON_PATH)
    token_ids = np.array(tokenizer.encode("The capital of France is").ids, dtype=np.int64)

    print("running our NumPy oracle (oracle/model.py)...")
    numpy_result = forward(token_ids, weights, config, capture_taps=False)
    numpy_last = numpy_result.logits[-1].astype(np.float64)

    print("running our PyTorch oracle (oracle/torch_oracle.py)...")
    torch_logits = forward_torch(
        token_ids, weights,
        hidden=config.hidden, n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
        head_dim=config.head_dim, rmsnorm_epsilon=config.rmsnorm_epsilon, rope_theta=config.rope_theta,
    ).detach().numpy()
    torch_last = torch_logits[-1].astype(np.float64)

    diff = np.abs(numpy_last - torch_last)
    max_diff = float(diff.max())
    argmax_numpy = int(np.argmax(numpy_last))
    argmax_torch = int(np.argmax(torch_last))

    print(f"\nmax_abs_diff across full vocab (numpy vs torch, both ours): {max_diff:.3e}")
    print(f"argmax: numpy={argmax_numpy} torch={argmax_torch} match={argmax_numpy == argmax_torch}")
    print("\nranking-disputed tokens (from the llama.cpp comparison) -- do OUR two implementations agree on them?")
    for tid in DISPUTED_TOKENS:
        print(f"  token {tid:6d}: numpy={numpy_last[tid]:.6f} torch={torch_last[tid]:.6f} "
              f"diff={abs(numpy_last[tid] - torch_last[tid]):.6f}")

    self_consistent = max_diff < 1e-3  # arbitrary-but-generous bar for "these two agree with each other"
    print(f"\nself-consistency (our two implementations agree with each other): {self_consistent}")
    if self_consistent:
        print("CONCLUSION: the divergence against llama.cpp is NOT a bug in our own forward pass -- "
              "our two independently-coded implementations agree with each other far more tightly "
              "than either agrees with llama.cpp. The discrepancy is specific to llama.cpp's "
              "computation and needs further investigation there, not here.")
    return self_consistent


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: real_candidate_self_consistency (diagnostic, not a Phase 0 gate check)")
    raise SystemExit(0 if ok else 1)
