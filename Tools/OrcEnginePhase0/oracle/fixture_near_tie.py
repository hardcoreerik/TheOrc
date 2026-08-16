# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
near_tie_logits acceptance check, per PHASE_0_REFERENCE_ORACLE.md:

  "At least one fixture must deliberately create near-tied top logits. It
  records both logits, their margin, the selected token, and the exact-tie
  rule; token agreement alone cannot pass this fixture."

Profile A's decode rule (PHASE_0_ARCHITECTURE_PROFILE.md): "greedy, lowest
token ID wins an exact tie."

Construction: since token embedding and output projection are tied
(logits = final_normed @ token_embedding^T), making two embedding rows
identical (or nearly identical) forces their logits to be identical (or
nearly identical) for ANY hidden state -- this builds a near-tie/exact-tie
through the real model machinery rather than hand-faking a logits vector.
"""
from __future__ import annotations

import dataclasses

import numpy as np

from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

SEED = 20260814
NEAR_TIE_TOKEN_A = 7
NEAR_TIE_TOKEN_B = 8
NEAR_TIE_EPSILON = 1e-4  # embedding-row perturbation for the near-tie case


def _config() -> ModelConfig:
    return ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=1,
                        n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)


DOMINANCE_BOOST = 50.0  # added to rows A/B along a direction ALIGNED WITH THE ACTUAL
                         # final_normed vector for this specific token sequence (computed
                         # once from an unmodified probe run -- see _boost_direction_for_sequence),
                         # not an arbitrary fixed direction. A first attempt used a fixed uniform
                         # direction and it was empirically near-orthogonal to the real hidden
                         # state (caught by _assert_tied_pair_is_global_top2 below, not silently
                         # accepted -- CodeRabbit finding, PR #102). Aligning with the real vector
                         # guarantees a large positive dot-product contribution by construction:
                         # tokens 7/8 never appear in this fixture's input token_ids, so modifying
                         # their embedding rows cannot change the layer stack's own computation of
                         # final_normed, only the final output-projection logit for those two rows.


def _boost_direction_for_sequence(token_ids: np.ndarray, config: ModelConfig) -> np.ndarray:
    """Unit vector aligned with the REAL final_normed hidden state for this token sequence,
    computed from unmodified weights. Valid as long as NEAR_TIE_TOKEN_A/B never appear in
    token_ids (asserted below) -- otherwise modifying their embedding rows would also change
    the input embedding lookup and this probe wouldn't reflect the real run."""
    assert NEAR_TIE_TOKEN_A not in token_ids.tolist() and NEAR_TIE_TOKEN_B not in token_ids.tolist(), \
        "boost-direction probe is invalid if the tied tokens appear in the input sequence"
    probe_weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                                   intermediate=config.intermediate, n_layers=config.n_layers,
                                   n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                                   head_dim=config.head_dim)
    probe_result = forward(token_ids, probe_weights, config, capture_taps=True)
    final_normed = probe_result.taps["final_normalized_state"][-1].astype(np.float64)
    norm = np.linalg.norm(final_normed)
    assert norm > 1e-8, "final_normed is degenerate (near-zero norm); cannot construct a boost direction"
    return (final_normed / norm).astype(np.float32)


def _weights_with_tied_rows(*, exact: bool, token_ids: np.ndarray):
    config = _config()
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    embedding = weights.token_embedding.copy()
    boost_direction = _boost_direction_for_sequence(token_ids, config)
    embedding[NEAR_TIE_TOKEN_A] = embedding[NEAR_TIE_TOKEN_A] + DOMINANCE_BOOST * boost_direction
    if exact:
        embedding[NEAR_TIE_TOKEN_B] = embedding[NEAR_TIE_TOKEN_A]
    else:
        rng = np.random.default_rng(SEED + 1)
        perturbation = (rng.standard_normal(embedding.shape[1]) * NEAR_TIE_EPSILON).astype(np.float32)
        embedding[NEAR_TIE_TOKEN_B] = embedding[NEAR_TIE_TOKEN_A] + perturbation
    return dataclasses.replace(weights, token_embedding=embedding), config


def _assert_tied_pair_is_global_top2(logits: np.ndarray) -> None:
    """CodeRabbit finding (PR #102): a tie between tokens A/B is only a meaningful test of
    the exact-tie DECODE RULE if A/B are actually the model's top-2 logits overall -- otherwise
    some other token wins argmax and the tie machinery never gets exercised. Fail loudly
    (not a synthetic fallback) if the fixture doesn't actually produce that."""
    top2_indices = set(np.argsort(-logits)[:2].tolist())
    tied_pair = {NEAR_TIE_TOKEN_A, NEAR_TIE_TOKEN_B}
    if top2_indices != tied_pair:
        raise AssertionError(
            f"fixture construction failed: tied pair {sorted(tied_pair)} is not the model's "
            f"actual top-2 logits (got {sorted(top2_indices)}); DOMINANCE_BOOST is insufficient "
            f"or misdirected. This must be fixed in the fixture, not worked around at test time."
        )


def run() -> bool:
    token_ids = np.array([1, 5, 9, 3], dtype=np.int64)
    ok = True

    # Case 1: near-tie (rows nearly but not exactly identical), and REQUIRED to be the
    # model's actual top-2 logits -- not just any two entries that happen to be close.
    weights_near, config = _weights_with_tied_rows(exact=False, token_ids=token_ids)
    result_near = forward(token_ids, weights_near, config, capture_taps=True)
    last_logits_near = result_near.logits[-1]
    _assert_tied_pair_is_global_top2(last_logits_near)
    logit_a = float(last_logits_near[NEAR_TIE_TOKEN_A])
    logit_b = float(last_logits_near[NEAR_TIE_TOKEN_B])
    margin_ab = abs(logit_a - logit_b)
    selected_near = int(result_near.taps["selected_token"][-1])
    top_margin_near = float(result_near.taps["top_token_margin"][-1])
    is_near_tie = margin_ab < 0.05  # deliberately small relative to the ~1-2 logit spread we've observed elsewhere
    print(f"near-tie case: logit[{NEAR_TIE_TOKEN_A}]={logit_a:.6f} logit[{NEAR_TIE_TOKEN_B}]={logit_b:.6f} "
          f"margin={margin_ab:.6f} (near-tie: {is_near_tie}, confirmed global top-2)")
    print(f"  selected_token={selected_near}  top_token_margin={top_margin_near:.6f}")
    ok = ok and is_near_tie

    # Case 2: exact tie (rows byte-identical), REQUIRED to be the model's actual top-2 --
    # Profile A rule: lowest token ID wins. No synthetic fallback: if the tied pair isn't
    # genuinely the argmax contest, _assert_tied_pair_is_global_top2 raises and the fixture
    # fails loudly rather than silently substituting an unrelated vector.
    weights_exact, config = _weights_with_tied_rows(exact=True, token_ids=token_ids)
    result_exact = forward(token_ids, weights_exact, config, capture_taps=True)
    last_logits_exact = result_exact.logits[-1]
    _assert_tied_pair_is_global_top2(last_logits_exact)
    logit_a_exact = float(last_logits_exact[NEAR_TIE_TOKEN_A])
    logit_b_exact = float(last_logits_exact[NEAR_TIE_TOKEN_B])
    exact_equal = logit_a_exact == logit_b_exact  # bit-exact: rows were byte-identical inputs to a linear op
    selected_exact = int(result_exact.taps["selected_token"][-1])
    tie_rule_respected = selected_exact == min(NEAR_TIE_TOKEN_A, NEAR_TIE_TOKEN_B)
    print(f"\nexact-tie case: logit[{NEAR_TIE_TOKEN_A}]={logit_a_exact!r} logit[{NEAR_TIE_TOKEN_B}]={logit_b_exact!r} "
          f"bit-exact-equal={exact_equal} (confirmed global top-2)")
    print(f"  selected_token={selected_exact}, expected (lowest ID wins)="
          f"{min(NEAR_TIE_TOKEN_A, NEAR_TIE_TOKEN_B)}: {tie_rule_respected}")
    ok = ok and exact_equal and tie_rule_respected

    return ok


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: near_tie_logits")
    raise SystemExit(0 if ok else 1)
