# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Fault-injection proof for Fixture B, per PHASE_0_REFERENCE_ORACLE.md
"Fault-injection proof" and "Failure triage".

The oracle harness is not accepted until it detects at least 7 named
faults, each failing FIRST at its expected checkpoint class (not some
downstream symptom). This module seeds each fault, runs the faulted
forward pass, walks taps in the same order model.py captures them, and
reports the FIRST tap that mismatches baseline beyond tolerance.

Status (honest, not all 7 -- see README.md and the printed summary):
  5/7 implemented and tested against Fixture B (full-prefix, n_layers=1):
    transposed projection matrix, off-by-one position, incorrect RoPE
    pairing, missing causal mask, changed RMSNorm epsilon.
  2/7 explicitly deferred, NOT faked:
    - swapped K/V cache write: Fixture B has no real KV cache (it's a
      full-prefix, non-incremental forward pass) -- an actual cache-write
      fault requires Fixture C's incremental-decode path. Testing a
      same-effect "swap K and V tensors in attention" proxy here would
      not be the fault the acceptance check actually names.
    - tokenizer special-token error: Profile A has no tokenizer (it
      consumes raw token IDs). This fault type requires Fixture D's real
      tokenizer (SmolLM2-135M candidate).
"""
from __future__ import annotations

import dataclasses
from dataclasses import dataclass

import numpy as np

from oracle.model import FaultSpec, ModelConfig, forward
from oracle.weights import build_weights

ATOL = 1e-6
RTOL = 1e-5
SEED = 20260814


@dataclass
class FaultResult:
    name: str
    expected_checkpoint: str
    actual_first_mismatch: str | None
    passed: bool
    detail: str = ""


def _ordered_checkpoints(result_taps: dict) -> list[tuple[str, np.ndarray]]:
    """Flatten one ForwardResult.taps (single-layer) into capture order."""
    layer0 = result_taps["layer_0"]
    order = [
        ("input_embedding", result_taps["input_embedding"]),
        ("pre_attention_normalized_state", layer0["pre_attention_normalized_state"]),
        ("q_projection", layer0["q_projection"]),
        ("k_projection", layer0["k_projection"]),
        ("v_projection", layer0["v_projection"]),
        ("q_after_rope", layer0["q_after_rope"]),
        ("k_after_rope", layer0["k_after_rope"]),
        ("masked_attention_scores", layer0["masked_attention_scores"]),
        ("attention_probabilities", layer0["attention_probabilities"]),
        ("attention_output_before_projection", layer0["attention_output_before_projection"]),
        ("attention_output_after_projection", layer0["attention_output_after_projection"]),
        ("post_attention_residual", layer0["post_attention_residual"]),
        ("pre_ffn_normalized_state", layer0["pre_ffn_normalized_state"]),
        ("gate_projection", layer0["gate_projection"]),
        ("up_projection", layer0["up_projection"]),
        ("activated_gated_product", layer0["activated_gated_product"]),
        ("down_projection", layer0["down_projection"]),
        ("post_ffn_residual", layer0["post_ffn_residual"]),
        ("final_normalized_state", result_taps["final_normalized_state"]),
        ("logits", result_taps["logits"]),
    ]
    return order


def _first_mismatch(baseline_taps: dict, faulted_taps: dict) -> str | None:
    baseline_order = _ordered_checkpoints(baseline_taps)
    faulted_order = dict(_ordered_checkpoints(faulted_taps))
    for name, expected in baseline_order:
        actual = faulted_order[name]
        # masked_attention_scores can legitimately contain -inf (causal mask);
        # treat -inf-vs-finite as a mismatch without letting inf-inf=nan hide it.
        if np.any(np.isinf(expected)) or np.any(np.isinf(actual)):
            same_inf_pattern = np.array_equal(np.isneginf(expected), np.isneginf(actual))
            finite_mask = ~np.isneginf(expected)
            finite_close = np.allclose(expected[finite_mask], actual[finite_mask], atol=ATOL, rtol=RTOL)
            if not (same_inf_pattern and finite_close):
                return name
            continue
        if not np.allclose(expected, actual, atol=ATOL, rtol=RTOL):
            return name
    return None


def _base_config_and_weights():
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=1,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    token_ids = np.array([1, 5, 9, 3], dtype=np.int64)
    return config, weights, token_ids


def run_all() -> list[FaultResult]:
    config, weights, token_ids = _base_config_and_weights()
    baseline = forward(token_ids, weights, config, capture_taps=True)
    results: list[FaultResult] = []

    # 1. Transposed projection matrix (w_q is square here: [16, 16], so .T is shape-valid).
    #    Triage: "Q/K/V differs -> weight mapping or matmul layout" -> expect q_projection.
    faulted_layer = dataclasses.replace(weights.layers[0], w_q=weights.layers[0].w_q.T.copy())
    faulted_weights = dataclasses.replace(weights, layers=(faulted_layer,))
    faulted = forward(token_ids, faulted_weights, config, capture_taps=True)
    first = _first_mismatch(baseline.taps, faulted.taps)
    results.append(FaultResult("transposed_w_q", "q_projection", first, first == "q_projection"))

    # 2. Off-by-one position (Q only). Triage: "post-RoPE differs -> position/frequency/layout"
    #    -> expect q_after_rope (k_after_rope must stay identical to baseline: K wasn't touched).
    faulted = forward(token_ids, weights, config, capture_taps=True,
                       fault=FaultSpec(q_rope_position_offset=1))
    first = _first_mismatch(baseline.taps, faulted.taps)
    results.append(FaultResult("off_by_one_position", "q_after_rope", first, first == "q_after_rope"))

    # 3. Incorrect RoPE pairing (Q only, interleaved instead of split-half).
    #    Triage: "post-RoPE differs" -> expect q_after_rope.
    faulted = forward(token_ids, weights, config, capture_taps=True,
                       fault=FaultSpec(wrong_rope_pairing=True))
    first = _first_mismatch(baseline.taps, faulted.taps)
    results.append(FaultResult("wrong_rope_pairing", "q_after_rope", first, first == "q_after_rope"))

    # 4. Missing causal mask. Triage: "attention differs -> mask/grouping/scale/cache"
    #    -> expect masked_attention_scores (future positions stop being -inf).
    faulted = forward(token_ids, weights, config, capture_taps=True,
                       fault=FaultSpec(skip_causal_mask=True))
    first = _first_mismatch(baseline.taps, faulted.taps)
    results.append(FaultResult("missing_causal_mask", "masked_attention_scores", first,
                                first == "masked_attention_scores"))

    # 5. Changed RMSNorm epsilon (attn norm). Triage: "pre-attn norm differs -> RMSNorm"
    #    -> expect pre_attention_normalized_state. Use a epsilon far from 1e-5 (1.0) so the
    #    effect isn't accidentally within float32 tolerance.
    faulted = forward(token_ids, weights, config, capture_taps=True,
                       fault=FaultSpec(attn_rmsnorm_epsilon_override=1.0))
    first = _first_mismatch(baseline.taps, faulted.taps)
    results.append(FaultResult("changed_rmsnorm_epsilon", "pre_attention_normalized_state", first,
                                first == "pre_attention_normalized_state"))

    return results


DEFERRED = [
    ("swapped_kv_cache_write",
     "requires Fixture C's incremental-decode KV cache (doesn't exist yet); "
     "a same-effect proxy on Fixture B's full-prefix pass would not test the named fault"),
    ("tokenizer_special_token_error",
     "requires Fixture D's real tokenizer (SmolLM2-135M candidate); "
     "Profile A consumes raw token IDs, no tokenizer exists to have a special-token bug"),
]


if __name__ == "__main__":
    results = run_all()
    n_pass = sum(1 for r in results if r.passed)
    for r in results:
        status = "PASS" if r.passed else "FAIL"
        print(f"[{status}] {r.name}: expected first mismatch at '{r.expected_checkpoint}', "
              f"got '{r.actual_first_mismatch}'")
    print(f"\n{n_pass}/{len(results)} implemented fault-injection cases passed")
    print(f"\n{len(DEFERRED)} required fault types deliberately deferred (not faked):")
    for name, reason in DEFERRED:
        print(f"  - {name}: {reason}")
    print(f"\noverall: {n_pass}/7 of the required fault types are detected and proven "
          f"({len(DEFERRED)}/7 blocked on Fixture C/D machinery that doesn't exist yet)")
    raise SystemExit(0 if n_pass == len(results) else 1)
