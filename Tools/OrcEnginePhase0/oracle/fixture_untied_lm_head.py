# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Synthetic untied-output-head fixture. Proves the CPU oracle actually uses
ModelWeights.lm_head for the final logit projection when it differs from
token_embedding, rather than silently falling back to token_embedding.T
regardless -- the real bug found and fixed this session (confirmed to
have corrupted the first Llama-3.1-8B streaming ablation result, whose
GGUF has a genuinely distinct output.weight).

Construction: build ordinary synthetic weights (build_weights), forward
once with lm_head=None (tied) and once with lm_head set to an
INDEPENDENTLY-SEEDED random matrix of the same shape (untied). If the
forward pass actually reads lm_head, the two logit vectors must differ
substantially -- if it silently ignores lm_head and always uses
token_embedding, they would be IDENTICAL, which is exactly the bug this
fixture exists to catch.
"""
from __future__ import annotations

import dataclasses

import numpy as np

from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

SEED = 20260814


def _config() -> ModelConfig:
    return ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=2,
                        n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)


def run() -> bool:
    config = _config()
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    token_ids = np.array([1, 5, 9, 3], dtype=np.int64)

    # Case 1: tied (lm_head=None, the default -- matches every fixture before this one).
    tied_logits = forward(token_ids, weights, config, capture_taps=False).logits

    # Case 2: untied -- an independently-seeded lm_head, same shape, deliberately DIFFERENT
    # values from token_embedding so a forward pass that ignores lm_head and falls back to
    # token_embedding would produce IDENTICAL logits to case 1 (the bug), not just similar ones.
    rng = np.random.default_rng(SEED + 999)
    independent_lm_head = (rng.standard_normal(weights.token_embedding.shape) * 0.1).astype(np.float32)
    untied_weights = dataclasses.replace(weights, lm_head=independent_lm_head)
    untied_logits = forward(token_ids, untied_weights, config, capture_taps=False).logits

    max_diff = float(np.abs(untied_logits.astype(np.float64) - tied_logits.astype(np.float64)).max())
    print(f"tied vs untied max logit diff: {max_diff:.4f}")

    # A real, substantial divergence is expected (independent random matrices, not a rounding
    # difference) -- a near-zero diff here means the untied path is silently not being used.
    diverges = max_diff > 1.0
    print(f"{'PASS' if diverges else 'FAIL'}: untied lm_head actually changes the computed logits")

    # Sanity: the effective_lm_head() helper itself must resolve correctly in both directions.
    resolves_correctly = (
        weights.effective_lm_head() is weights.token_embedding
        and untied_weights.effective_lm_head() is independent_lm_head
    )
    print(f"{'PASS' if resolves_correctly else 'FAIL'}: effective_lm_head() resolves tied/untied correctly")

    return diverges and resolves_correctly


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: fixture_untied_lm_head")
    raise SystemExit(0 if ok else 1)
