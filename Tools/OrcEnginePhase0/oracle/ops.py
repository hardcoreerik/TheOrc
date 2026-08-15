# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Primitive tensor operators for the OrcEngine Phase 0 reference oracle.

These implement the exact block semantics pinned in
docs/OrcEngine/PHASE_0_ARCHITECTURE_PROFILE.md (Profile A / OE-L0-SYNTH-1).
Everything here is float32, deterministic, and intentionally written as
plain NumPy with no fused/optimized paths, so it is easy to read against
the spec line by line during review.

This module is the "primary semantic oracle" independence class from
PHASE_0_REFERENCE_ORACLE.md. It must NOT be used to generate the
hand-derived microcase expected values in microcases.py -- those are
worked out independently so the oracle can't validate itself.
"""
from __future__ import annotations

import numpy as np

DTYPE = np.float32


def rmsnorm(x: np.ndarray, weight: np.ndarray, epsilon: float) -> np.ndarray:
    """x * rsqrt(mean(x^2) + epsilon) * weight, per PHASE_0_ARCHITECTURE_PROFILE.md."""
    x = x.astype(DTYPE)
    weight = weight.astype(DTYPE)
    mean_sq = np.mean(np.square(x), axis=-1, keepdims=True, dtype=DTYPE)
    inv_rms = (1.0 / np.sqrt(mean_sq + DTYPE(epsilon))).astype(DTYPE)
    return (x * inv_rms * weight).astype(DTYPE)


def silu(x: np.ndarray) -> np.ndarray:
    """SiLU(x) = x * sigmoid(x)."""
    x = x.astype(DTYPE)
    return (x * (1.0 / (1.0 + np.exp(-x)))).astype(DTYPE)


def softmax_last_axis(x: np.ndarray) -> np.ndarray:
    """Max-subtracted softmax over the last axis, float32 throughout."""
    x = x.astype(DTYPE)
    shifted = x - np.max(x, axis=-1, keepdims=True)
    exp = np.exp(shifted).astype(DTYPE)
    return (exp / np.sum(exp, axis=-1, keepdims=True, dtype=DTYPE)).astype(DTYPE)


def causal_mask(scores: np.ndarray) -> np.ndarray:
    """
    Apply a causal mask to attention scores of shape [..., q_pos, k_pos]:
    query position p may attend only to key positions 0..p (inclusive).
    Masked entries are set to -inf before softmax, per the spec's
    "apply the causal mask before a max-subtracted float32 softmax".

    Equivalent to causal_mask_rectangular(scores, query_start_position=0)
    when q_len == k_len; kept separate because it's the common case used
    by the full-prefix (non-cached) forward pass.
    """
    q_len, k_len = scores.shape[-2], scores.shape[-1]
    mask = np.triu(np.ones((q_len, k_len), dtype=bool), k=1)
    out = scores.astype(DTYPE).copy()
    out[..., mask] = np.float32("-inf")
    return out


def causal_mask_rectangular(scores: np.ndarray, query_start_position: int) -> np.ndarray:
    """
    Causal mask for incremental/cached decode, where query rows correspond
    to absolute positions [query_start_position, query_start_position+q_len)
    and key columns correspond to absolute positions [0, k_len) (cached +
    new). Query absolute position p may attend to key positions 0..p.
    """
    q_len, k_len = scores.shape[-2], scores.shape[-1]
    query_abs = np.arange(query_start_position, query_start_position + q_len)
    key_abs = np.arange(k_len)
    mask = key_abs[None, :] > query_abs[:, None]  # True where key is "in the future" of the query
    out = scores.astype(DTYPE).copy()
    out[..., mask] = np.float32("-inf")
    return out


def rope_cos_sin(position: int, head_dim: int, theta: float) -> tuple[np.ndarray, np.ndarray]:
    """
    Non-interleaved Llama RoPE cos/sin vectors for one position.
    freq_i = theta ** (-2i / head_dim) for i in [0, head_dim/2).
    Returned vectors are length head_dim (each half repeats the head_dim/2
    frequencies once), matching the "split into equal first/second halves"
    rotation convention in PHASE_0_ARCHITECTURE_PROFILE.md.
    """
    half = head_dim // 2
    i = np.arange(half, dtype=np.float64)
    freqs = theta ** (-2.0 * i / head_dim)
    angles = np.float64(position) * freqs
    cos_half = np.cos(angles).astype(DTYPE)
    sin_half = np.sin(angles).astype(DTYPE)
    cos = np.concatenate([cos_half, cos_half])
    sin = np.concatenate([sin_half, sin_half])
    return cos, sin


def rope_rotate_half(x: np.ndarray) -> np.ndarray:
    """[-second_half, first_half] rotation used by non-interleaved Llama RoPE."""
    head_dim = x.shape[-1]
    half = head_dim // 2
    first, second = x[..., :half], x[..., half:]
    return np.concatenate([-second, first], axis=-1).astype(DTYPE)


def apply_rope(x: np.ndarray, cos: np.ndarray, sin: np.ndarray) -> np.ndarray:
    """x * cos + rotate_half(x) * sin, applied per position."""
    x = x.astype(DTYPE)
    return (x * cos + rope_rotate_half(x) * sin).astype(DTYPE)


def linear_no_bias(x: np.ndarray, weight_out_in: np.ndarray) -> np.ndarray:
    """
    x @ W^T for a weight stored as [out_features, in_features]
    (PHASE_0_ARCHITECTURE_PROFILE.md's synthetic storage contract).
    """
    x = x.astype(DTYPE)
    weight_out_in = weight_out_in.astype(DTYPE)
    return (x @ weight_out_in.T).astype(DTYPE)


def embedding_lookup(table: np.ndarray, token_ids: np.ndarray) -> np.ndarray:
    """Row lookup: table is [vocab, hidden], token_ids is [seq]."""
    return table[token_ids].astype(DTYPE)
