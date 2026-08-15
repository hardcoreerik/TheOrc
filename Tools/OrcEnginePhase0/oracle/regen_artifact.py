# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Single-run artifact generator for the deterministic_regeneration acceptance
check. Meant to be invoked as a fresh `python3 -m oracle.regen_artifact`
subprocess -- each invocation is a genuinely separate process (its own
interpreter, its own numpy import, its own RNG state from scratch), not a
second in-process function call. Prints one JSON object to stdout:
non-floating identities (shapes, dtypes, integer arrays -- hashed) plus
floating arrays (logits, taps -- hashed AND with raw values for a numeric
comparison, since "identical hash" and "within tolerance" are different
claims and PHASE_0_ACCEPTANCE.yaml's evidence line asks for both).
"""
from __future__ import annotations

import hashlib
import json
import sys

import numpy as np

from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

SEED = 20260814


def _sha256(arr: np.ndarray) -> str:
    return hashlib.sha256(np.ascontiguousarray(arr).tobytes()).hexdigest()


def main() -> None:
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=2,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    token_ids = np.array([1, 5, 9, 3, 7], dtype=np.int64)
    result = forward(token_ids, weights, config, capture_taps=True)

    non_floating_identities = {
        "logits_shape": list(result.logits.shape),
        "logits_dtype": str(result.logits.dtype),
        "selected_token": result.taps["selected_token"].tolist(),
        "token_embedding_shape": list(weights.token_embedding.shape),
        "token_embedding_sha256": _sha256(weights.token_embedding),
        "layer0_w_q_sha256": _sha256(weights.layers[0].w_q),
        "layer1_w_down_sha256": _sha256(weights.layers[1].w_down),
    }
    floating = {
        "logits_sha256": _sha256(result.logits),
        "logits_values": result.logits.tolist(),
        "top_token_margin": result.taps["top_token_margin"].tolist(),
        "layer0_post_ffn_residual_sha256": _sha256(result.taps["layer_0"]["post_ffn_residual"]),
    }

    print(json.dumps({"non_floating_identities": non_floating_identities, "floating": floating}))


if __name__ == "__main__":
    main()
