# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Deterministic weight generation for OE-L0-SYNTH-1 (Profile A), per
PHASE_0_ARCHITECTURE_PROFILE.md's "Synthetic storage contract":

  "Weights are generated from a pinned algorithm and seed, then committed
  or retained as immutable artifacts. 'Same seed' is not an adequate
  identity unless the generator implementation and version are also
  pinned."

Pinned algorithm: numpy.random.default_rng(seed) (NumPy's PCG64 bit
generator), standard_normal(), scaled by 0.02 and cast to float32. This
generator/version pairing is part of the weight identity -- if NumPy's
default_rng algorithm ever changes, weights must be regenerated and
re-hashed, not silently assumed identical.

Every matrix is stored as [out_features, in_features] in C row-major
order, per the synthetic storage contract. Norm weights are stored as
plain [hidden] vectors initialized to ones (identity-like start, not
random) so RMSNorm's weight term is trivially auditable by hand.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

DTYPE = np.float32
NUMPY_GENERATOR = "numpy.random.default_rng (PCG64)"
WEIGHT_SCALE = 0.02


@dataclass(frozen=True)
class LayerWeights:
    attn_norm_weight: np.ndarray   # [hidden]
    w_q: np.ndarray                 # [n_q_heads*head_dim, hidden]
    w_k: np.ndarray                 # [n_kv_heads*head_dim, hidden]
    w_v: np.ndarray                 # [n_kv_heads*head_dim, hidden]
    w_o: np.ndarray                 # [hidden, n_q_heads*head_dim]
    ffn_norm_weight: np.ndarray    # [hidden]
    w_gate: np.ndarray              # [intermediate, hidden]
    w_up: np.ndarray                # [intermediate, hidden]
    w_down: np.ndarray              # [hidden, intermediate]


@dataclass(frozen=True)
class ModelWeights:
    seed: int
    generator: str
    weight_scale: float
    token_embedding: np.ndarray     # [vocab, hidden] -- tied with output projection
    layers: tuple[LayerWeights, ...]
    final_norm_weight: np.ndarray  # [hidden]


def _randn(rng: np.random.Generator, shape: tuple[int, ...]) -> np.ndarray:
    return (rng.standard_normal(size=shape) * WEIGHT_SCALE).astype(DTYPE)


def build_weights(
    *,
    seed: int,
    vocab: int,
    hidden: int,
    intermediate: int,
    n_layers: int,
    n_q_heads: int,
    n_kv_heads: int,
    head_dim: int,
) -> ModelWeights:
    rng = np.random.default_rng(seed)

    token_embedding = _randn(rng, (vocab, hidden))

    layers = []
    for _ in range(n_layers):
        layers.append(LayerWeights(
            attn_norm_weight=np.ones((hidden,), dtype=DTYPE),
            w_q=_randn(rng, (n_q_heads * head_dim, hidden)),
            w_k=_randn(rng, (n_kv_heads * head_dim, hidden)),
            w_v=_randn(rng, (n_kv_heads * head_dim, hidden)),
            w_o=_randn(rng, (hidden, n_q_heads * head_dim)),
            ffn_norm_weight=np.ones((hidden,), dtype=DTYPE),
            w_gate=_randn(rng, (intermediate, hidden)),
            w_up=_randn(rng, (intermediate, hidden)),
            w_down=_randn(rng, (hidden, intermediate)),
        ))

    final_norm_weight = np.ones((hidden,), dtype=DTYPE)

    return ModelWeights(
        seed=seed, generator=NUMPY_GENERATOR, weight_scale=WEIGHT_SCALE,
        token_embedding=token_embedding, layers=tuple(layers),
        final_norm_weight=final_norm_weight,
    )
