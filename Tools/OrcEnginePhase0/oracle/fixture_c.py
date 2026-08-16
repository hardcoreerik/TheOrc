# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Fixture C -- multi-layer + KV-cache equivalence test, per
PHASE_0_REFERENCE_ORACLE.md "Cache equivalence test":

  For token sequence [t0, t1, ... tn] compare:
    1. full-prefix evaluation producing logits at tn;
    2. prompt evaluation through t(n-1) plus one cached decode of tn.
  The last-position states and logits must meet the approved profile.
  Repeat with context reset to catch stale state.

Uses the FULL Profile A layer count (n_layers=2), not Fixture B's reduced
n_layers=1 -- this is deliberately the pinned OE-L0-SYNTH-1 configuration.
"""
from __future__ import annotations

import numpy as np

from oracle.model import ModelConfig, empty_kv_cache, forward, forward_cached
from oracle.weights import build_weights

ATOL = 1e-6
RTOL = 1e-5
SEED = 20260814


def _config() -> ModelConfig:
    return ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=2,
                        n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)


def _run_once(token_ids: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """Returns (full_prefix_last_logits, incremental_last_logits) for one run."""
    config = _config()
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)

    # 1. Full-prefix evaluation over the whole sequence.
    full = forward(token_ids, weights, config, capture_taps=False)
    full_last_logits = full.logits[-1]

    # 2. Prompt through t(n-1), then one cached decode of tn.
    prefix = token_ids[:-1]
    last_token = token_ids[-1:]
    prefill_result, cache = forward_cached(
        prefix, weights, config, kv_cache=empty_kv_cache(config), start_position=0, capture_taps=False,
    )
    decode_result, _ = forward_cached(
        last_token, weights, config, kv_cache=cache, start_position=len(prefix), capture_taps=False,
    )
    incremental_last_logits = decode_result.logits[-1]

    return full_last_logits, incremental_last_logits


def run() -> bool:
    token_ids = np.array([1, 5, 9, 3, 7], dtype=np.int64)  # seq=5, n=4 (0-indexed last position)

    all_ok = True

    # Run 1.
    full1, inc1 = _run_once(token_ids)
    match1 = np.allclose(full1, inc1, atol=ATOL, rtol=RTOL)
    max_diff1 = float(np.abs(full1.astype(np.float64) - inc1.astype(np.float64)).max())
    print(f"run 1: full-prefix vs incremental-decode last-position logits match: {match1} "
          f"(max_abs_diff={max_diff1:.3e})")
    all_ok = all_ok and match1

    # Run 2: repeat with a fresh context (new cache object, same weights/seed) to catch stale state.
    full2, inc2 = _run_once(token_ids)
    match2 = np.allclose(full2, inc2, atol=ATOL, rtol=RTOL)
    max_diff2 = float(np.abs(full2.astype(np.float64) - inc2.astype(np.float64)).max())
    print(f"run 2 (context reset): match: {match2} (max_abs_diff={max_diff2:.3e})")
    all_ok = all_ok and match2

    # Cross-run determinism: run 1 and run 2 should be bit-identical to each other too.
    cross_match = np.array_equal(full1, full2) and np.array_equal(inc1, inc2)
    print(f"cross-run determinism (run 1 vs run 2, same seed): {cross_match}")
    all_ok = all_ok and cross_match

    return all_ok


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: cache equivalence")
    raise SystemExit(0 if ok else 1)
