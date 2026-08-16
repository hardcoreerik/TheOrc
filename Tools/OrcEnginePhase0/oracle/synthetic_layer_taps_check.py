# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
synthetic_layer_taps acceptance check, per PHASE_0_ACCEPTANCE.yaml:
  "all required OE-L0-SYNTH-1 intermediate taps pass named tolerance profiles"

Compares EVERY required intermediate tap (not just final logits, which
oracle/cross_oracle_check.py already covers) between oracle/model.py's
NumPy forward() and oracle/torch_oracle.py's independently-written PyTorch
forward_torch(), on the full pinned Profile A config (n_layers=2): 17
per-layer taps x 2 layers + 3 top-level taps (input_embedding,
final_normalized_state, logits) = 37 tap comparisons total, per
PHASE_0_REFERENCE_ORACLE.md's "Required capture points" list.
"""
from __future__ import annotations

import numpy as np

from oracle.model import ModelConfig, forward
from oracle.torch_oracle import forward_torch
from oracle.weights import build_weights

ATOL = 1e-5
RTOL = 1e-4
SEED = 20260814

PER_LAYER_TAPS = [
    "pre_attention_normalized_state", "q_projection", "k_projection", "v_projection",
    "q_after_rope", "k_after_rope", "masked_attention_scores", "attention_probabilities",
    "attention_output_before_projection", "attention_output_after_projection",
    "post_attention_residual", "pre_ffn_normalized_state", "gate_projection", "up_projection",
    "activated_gated_product", "down_projection", "post_ffn_residual",
]
TOP_LEVEL_TAPS = ["input_embedding", "final_normalized_state", "logits"]


def _compare(name: str, numpy_arr: np.ndarray, torch_tensor) -> tuple[bool, float]:
    torch_arr = torch_tensor.detach().numpy()
    if numpy_arr.shape != torch_arr.shape:
        print(f"  [FAIL] {name}: SHAPE MISMATCH numpy={numpy_arr.shape} torch={torch_arr.shape}")
        return False, float("inf")
    # masked_attention_scores contains -inf; compare finite entries + inf-pattern separately.
    if np.any(np.isinf(numpy_arr)) or np.any(np.isinf(torch_arr)):
        same_pattern = np.array_equal(np.isneginf(numpy_arr), np.isneginf(torch_arr))
        finite_mask = ~np.isneginf(numpy_arr)
        diff = float(np.abs(numpy_arr[finite_mask].astype(np.float64) -
                             torch_arr[finite_mask].astype(np.float64)).max()) if finite_mask.any() else 0.0
        ok = same_pattern and diff <= ATOL
        print(f"  [{'PASS' if ok else 'FAIL'}] {name}: -inf-pattern-match={same_pattern} "
              f"finite-max-diff={diff:.3e}")
        return ok, diff
    diff = float(np.abs(numpy_arr.astype(np.float64) - torch_arr.astype(np.float64)).max())
    ok = bool(np.allclose(numpy_arr, torch_arr, atol=ATOL, rtol=RTOL))
    print(f"  [{'PASS' if ok else 'FAIL'}] {name}: max_abs_diff={diff:.3e}")
    return ok, diff


def run() -> bool:
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=2,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    token_ids = np.array([1, 5, 9, 3, 7], dtype=np.int64)

    numpy_result = forward(token_ids, weights, config, capture_taps=True)
    _torch_logits, torch_taps = forward_torch(
        token_ids, weights,
        hidden=config.hidden, n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
        head_dim=config.head_dim, rmsnorm_epsilon=config.rmsnorm_epsilon, rope_theta=config.rope_theta,
        capture_taps=True,
    )

    all_ok = True
    max_diff_overall = 0.0

    print("top-level taps:")
    for name in TOP_LEVEL_TAPS:
        ok, diff = _compare(name, numpy_result.taps[name], torch_taps[name])
        all_ok = all_ok and ok
        max_diff_overall = max(max_diff_overall, diff)

    for layer_idx in range(config.n_layers):
        print(f"\nlayer_{layer_idx} taps:")
        numpy_layer = numpy_result.taps[f"layer_{layer_idx}"]
        torch_layer = torch_taps[f"layer_{layer_idx}"]
        for name in PER_LAYER_TAPS:
            ok, diff = _compare(name, numpy_layer[name], torch_layer[name])
            all_ok = all_ok and ok
            max_diff_overall = max(max_diff_overall, diff)

    n_total = len(TOP_LEVEL_TAPS) + config.n_layers * len(PER_LAYER_TAPS)
    print(f"\nmax_abs_diff across all {n_total} tap comparisons: {max_diff_overall:.3e} "
          f"(tolerance_profile: atol={ATOL}, rtol={RTOL})")
    return all_ok


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: synthetic_layer_taps")
    raise SystemExit(0 if ok else 1)
