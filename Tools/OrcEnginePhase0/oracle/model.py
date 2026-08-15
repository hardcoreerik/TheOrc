# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Full-prefix forward pass for OE-L0-SYNTH-1 (Profile A), implementing the
exact 12-step block semantics in
docs/OrcEngine/PHASE_0_ARCHITECTURE_PROFILE.md, generic over layer count so
the same code serves Fixture B (n_layers=1) and, later, Fixture C
(n_layers=2, the full pinned profile, with incremental-decode cache
equivalence -- not implemented here yet).

This module is the "primary semantic oracle" from PHASE_0_REFERENCE_ORACLE.md
-- built from oracle/ops.py primitives, each of which is independently
covered by oracle/microcases.py's hand-derived checks.

This is a full-prefix (non-incremental) evaluation only: every position's
K/V is (re)computed from the full input each call. Cache-write/incremental-
decode equivalence is explicitly Fixture C's job (PHASE_0_REFERENCE_ORACLE.md
"Cache equivalence test"), not this module's.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

import numpy as np

from oracle import ops
from oracle.weights import LayerWeights, ModelWeights

DTYPE = np.float32
RMSNORM_EPSILON = 1e-5
ROPE_THETA = 10000.0


@dataclass
class ModelConfig:
    vocab: int = 32
    hidden: int = 16
    intermediate: int = 32
    n_layers: int = 2
    n_q_heads: int = 4
    n_kv_heads: int = 2
    head_dim: int = 4
    max_positions: int = 16
    rmsnorm_epsilon: float = RMSNORM_EPSILON
    rope_theta: float = ROPE_THETA


@dataclass
class ForwardResult:
    logits: np.ndarray                     # [seq, vocab] -- one row per position
    taps: dict[str, Any] = field(default_factory=dict)


def _gqa_kv_head_for_query_head(q_head: int) -> int:
    """Query head h maps to KV head floor(h/2) -- the only accepted mapping for Profile A."""
    return q_head // 2


def _split_heads(x: np.ndarray, n_heads: int, head_dim: int) -> np.ndarray:
    """[seq, n_heads*head_dim] -> [n_heads, seq, head_dim]."""
    seq = x.shape[0]
    return x.reshape(seq, n_heads, head_dim).transpose(1, 0, 2)


def forward(
    token_ids: np.ndarray,
    weights: ModelWeights,
    config: ModelConfig,
    *,
    capture_taps: bool = True,
) -> ForwardResult:
    taps: dict[str, Any] = {}
    seq = token_ids.shape[0]

    # Step: input embedding lookup.
    x = ops.embedding_lookup(weights.token_embedding, token_ids)  # [seq, hidden]
    if capture_taps:
        taps["input_embedding"] = x.copy()

    # Precompute RoPE cos/sin per position for this head_dim/theta.
    cos_by_pos = []
    sin_by_pos = []
    for p in range(seq):
        c, s = ops.rope_cos_sin(position=p, head_dim=config.head_dim, theta=config.rope_theta)
        cos_by_pos.append(c)
        sin_by_pos.append(s)

    for layer_idx, lw in enumerate(weights.layers):
        layer_taps: dict[str, Any] = {}

        # 1. a = RMSNorm(x, attn_norm_weight, epsilon)
        a = ops.rmsnorm(x, lw.attn_norm_weight, config.rmsnorm_epsilon)
        if capture_taps:
            layer_taps["pre_attention_normalized_state"] = a.copy()

        # 2. q = a Wq^T, k = a Wk^T, v = a Wv^T
        q_flat = ops.linear_no_bias(a, lw.w_q)  # [seq, n_q_heads*head_dim]
        k_flat = ops.linear_no_bias(a, lw.w_k)  # [seq, n_kv_heads*head_dim]
        v_flat = ops.linear_no_bias(a, lw.w_v)  # [seq, n_kv_heads*head_dim]
        if capture_taps:
            layer_taps["q_projection"] = q_flat.copy()
            layer_taps["k_projection"] = k_flat.copy()
            layer_taps["v_projection"] = v_flat.copy()

        # 3. Reshape Q to [position, n_q_heads, head_dim]; K, V to [position, n_kv_heads, head_dim].
        q_heads = _split_heads(q_flat, config.n_q_heads, config.head_dim)   # [n_q_heads, seq, head_dim]
        k_heads = _split_heads(k_flat, config.n_kv_heads, config.head_dim)  # [n_kv_heads, seq, head_dim]
        v_heads = _split_heads(v_flat, config.n_kv_heads, config.head_dim)  # [n_kv_heads, seq, head_dim]

        # 4. Apply non-interleaved Llama RoPE to Q and K, per position.
        q_rope = np.zeros_like(q_heads)
        for h in range(config.n_q_heads):
            for p in range(seq):
                q_rope[h, p] = ops.apply_rope(q_heads[h, p], cos_by_pos[p], sin_by_pos[p])
        k_rope = np.zeros_like(k_heads)
        for h in range(config.n_kv_heads):
            for p in range(seq):
                k_rope[h, p] = ops.apply_rope(k_heads[h, p], cos_by_pos[p], sin_by_pos[p])
        if capture_taps:
            layer_taps["q_after_rope"] = q_rope.copy()
            layer_taps["k_after_rope"] = k_rope.copy()
            layer_taps["cache_slice_after_write"] = {"k": k_rope.copy(), "v": v_heads.copy()}

        # 5+6. Map query head h to KV head floor(h/2); scaled dot-product attention, causal mask.
        scale = 1.0 / np.sqrt(np.float32(config.head_dim))
        context_heads = np.zeros_like(q_rope)  # [n_q_heads, seq, head_dim]
        attn_probs_by_head = []
        masked_scores_by_head = []
        for h in range(config.n_q_heads):
            kv_h = _gqa_kv_head_for_query_head(h)
            scores = (q_rope[h] @ k_rope[kv_h].T).astype(DTYPE) * scale  # [seq, seq]
            masked = ops.causal_mask(scores)
            probs = ops.softmax_last_axis(masked)
            context_heads[h] = (probs @ v_heads[kv_h]).astype(DTYPE)
            masked_scores_by_head.append(masked.copy())
            attn_probs_by_head.append(probs.copy())
        if capture_taps:
            layer_taps["masked_attention_scores"] = np.stack(masked_scores_by_head)
            layer_taps["attention_probabilities"] = np.stack(attn_probs_by_head)

        # 7. Concatenate query-head outputs and compute attn_out = context Wo^T.
        context_flat = context_heads.transpose(1, 0, 2).reshape(seq, config.n_q_heads * config.head_dim)
        if capture_taps:
            layer_taps["attention_output_before_projection"] = context_flat.copy()
        attn_out = ops.linear_no_bias(context_flat, lw.w_o)  # [seq, hidden]
        if capture_taps:
            layer_taps["attention_output_after_projection"] = attn_out.copy()

        # 8. First residual.
        r = (x + attn_out).astype(DTYPE)
        if capture_taps:
            layer_taps["post_attention_residual"] = r.copy()

        # 9. f = RMSNorm(r, ffn_norm_weight, epsilon)
        f = ops.rmsnorm(r, lw.ffn_norm_weight, config.rmsnorm_epsilon)
        if capture_taps:
            layer_taps["pre_ffn_normalized_state"] = f.copy()

        # 10. SwiGLU: ffn = (SiLU(f W_gate^T) * (f W_up^T)) W_down^T
        gate = ops.linear_no_bias(f, lw.w_gate)
        up = ops.linear_no_bias(f, lw.w_up)
        if capture_taps:
            layer_taps["gate_projection"] = gate.copy()
            layer_taps["up_projection"] = up.copy()
        activated = (ops.silu(gate) * up).astype(DTYPE)
        if capture_taps:
            layer_taps["activated_gated_product"] = activated.copy()
        ffn = ops.linear_no_bias(activated, lw.w_down)
        if capture_taps:
            layer_taps["down_projection"] = ffn.copy()

        # 11. Second residual: block result.
        y = (r + ffn).astype(DTYPE)
        if capture_taps:
            layer_taps["post_ffn_residual"] = y.copy()

        x = y
        if capture_taps:
            taps[f"layer_{layer_idx}"] = layer_taps

    # 12. Final RMSNorm, then multiply by transposed (tied) token-embedding matrix for logits.
    final_normed = ops.rmsnorm(x, weights.final_norm_weight, config.rmsnorm_epsilon)
    if capture_taps:
        taps["final_normalized_state"] = final_normed.copy()
    logits = ops.linear_no_bias(final_normed, weights.token_embedding)  # [seq, vocab]
    if capture_taps:
        taps["logits"] = logits.copy()
        selected = np.argmax(logits, axis=-1)
        top2 = np.sort(logits, axis=-1)[:, -2:]
        margin = top2[:, -1] - top2[:, -2]
        taps["selected_token"] = selected.copy()
        taps["top_token_margin"] = margin.copy()

    return ForwardResult(logits=logits, taps=taps)
