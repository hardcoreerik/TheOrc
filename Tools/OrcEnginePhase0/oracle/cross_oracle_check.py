# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Cross-validates oracle/model.py's NumPy forward pass against
oracle/torch_oracle.py's independent PyTorch implementation on identical
weights and token IDs -- the "primary semantic oracle" half of
three_way_oracle_independence (PHASE_0_REFERENCE_ORACLE.md's three
independence classes: hand-derived ground truth [done, microcases.py],
primary semantic oracle [this], secondary deployment oracle [llama.cpp,
still open -- see README.md]).

Two configurations: Fixture B (n_layers=1) and the full pinned Profile A
(n_layers=2, Fixture C's config), so agreement isn't a coincidence of one
particular layer count.
"""
from __future__ import annotations

import numpy as np

from oracle.model import ModelConfig, forward
from oracle.torch_oracle import forward_torch
from oracle.weights import build_weights

ATOL = 1e-5   # slightly looser than the 1e-6 used within a single implementation --
RTOL = 1e-4   # two different libraries' float32 reduction order can differ at the ULP level.
SEED = 20260814


def _check_one(n_layers: int) -> tuple[bool, float]:
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=n_layers,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    token_ids = np.array([1, 5, 9, 3, 7], dtype=np.int64)

    numpy_result = forward(token_ids, weights, config, capture_taps=False)
    torch_logits = forward_torch(
        token_ids, weights,
        hidden=config.hidden, n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
        head_dim=config.head_dim, rmsnorm_epsilon=config.rmsnorm_epsilon, rope_theta=config.rope_theta,
    ).detach().numpy()

    max_diff = float(np.abs(numpy_result.logits.astype(np.float64) - torch_logits.astype(np.float64)).max())
    ok = bool(np.allclose(numpy_result.logits, torch_logits, atol=ATOL, rtol=RTOL))
    argmax_agree = bool(np.array_equal(np.argmax(numpy_result.logits, -1), np.argmax(torch_logits, -1)))
    print(f"n_layers={n_layers}: max_abs_diff={max_diff:.3e}  within_tolerance={ok}  argmax_agree={argmax_agree}")
    return ok and argmax_agree, max_diff


def run() -> bool:
    ok_b, diff_b = _check_one(n_layers=1)   # Fixture B config
    ok_c, diff_c = _check_one(n_layers=2)   # full Profile A / Fixture C config
    return ok_b and ok_c


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: NumPy oracle vs PyTorch oracle agreement")
    raise SystemExit(0 if ok else 1)
