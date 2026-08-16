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
generator), standard_normal(), scaled by WEIGHT_SCALE (0.1, see below) and
cast to float32. This generator/version pairing is part of the weight
identity -- if NumPy's default_rng algorithm ever changes, weights must be
regenerated and re-hashed, not silently assumed identical.

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
# 0.1, not 0.02: measured 2026-08-14 (see docs/OrcEngine/DECISION_LOG.md
# OE-ADR-015). At 0.02, a transposed w_o fault on Fixture B (n_layers=1)
# produced only a 0.070 max logit diff and did NOT flip argmax -- invisible
# to the fault-injection acceptance check. At 0.1, the same fault produces a
# 1.21 max logit diff and reliably flips argmax. Values above 0.1 detect
# faults even more clearly but were not required to clear this bar.
WEIGHT_SCALE = 0.1


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
    # Optional Q/K/V projection bias -- absent (None) for Profile A and the pinned
    # SmolLM2-135M candidate (both bias-free), but present for Qwen2-family GGUF models
    # (bias on attn_q/k/v only, never attn_output or the FFN). Modeled explicitly rather
    # than silently dropped, per oracle/gguf_model_loader.py's real-model support.
    attn_q_bias: np.ndarray | None = None   # [n_q_heads*head_dim]
    attn_k_bias: np.ndarray | None = None   # [n_kv_heads*head_dim]
    attn_v_bias: np.ndarray | None = None   # [n_kv_heads*head_dim]


@dataclass(frozen=True)
class ModelWeights:
    seed: int
    generator: str
    weight_scale: float
    token_embedding: np.ndarray     # [vocab, hidden] -- input embedding lookup table
    layers: tuple[LayerWeights, ...]
    final_norm_weight: np.ndarray  # [hidden]
    # None (default) = tied: the output projection reuses token_embedding, matching every
    # fixture/model this oracle supported before untied-output support existed (Profile A,
    # SmolLM2-135M, and every "tied_embeddings: true" real model swept so far). When a real
    # GGUF has a distinct output.weight tensor, gguf_model_loader.py populates this field and
    # the forward pass must use IT for the final logits, not token_embedding -- silently using
    # token_embedding.T on an untied model computes mathematically wrong logits (confirmed: the
    # first Llama-3.1-8B streaming ablation result predates this field and used the wrong
    # projection on a genuinely untied model; see DECISION_LOG for the correction). Use
    # effective_lm_head() below rather than reading this field directly.
    lm_head: np.ndarray | None = None  # [vocab, hidden], same layout as token_embedding

    def effective_lm_head(self) -> np.ndarray:
        return self.lm_head if self.lm_head is not None else self.token_embedding


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
