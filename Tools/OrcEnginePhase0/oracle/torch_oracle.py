# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Second independent semantic oracle for OE-L0-SYNTH-1 (Profile A), written
in PyTorch -- the "primary semantic oracle" independence leg from
PHASE_0_REFERENCE_ORACLE.md, distinct from oracle/model.py's NumPy
implementation. Deliberately re-derived from PHASE_0_ARCHITECTURE_PROFILE.md
directly, not translated line-by-line from oracle/model.py or oracle/ops.py
-- e.g. uses torch's built-in scaled_dot_product_attention-style manual
softmax instead of ops.softmax_last_axis, and torch.nn.functional primitives
where they exist, rather than reimplementing NumPy's exact code shape.

Takes the SAME weights (converted from oracle.weights.ModelWeights) and the
SAME token IDs as the NumPy oracle, so any divergence is attributable to an
implementation bug in one of the two, not different inputs.
"""
from __future__ import annotations

import numpy as np
import torch

from oracle.weights import ModelWeights

torch.set_default_dtype(torch.float32)
torch.use_deterministic_algorithms(True)


def _rmsnorm_t(x: torch.Tensor, weight: torch.Tensor, epsilon: float) -> torch.Tensor:
    variance = x.pow(2).mean(dim=-1, keepdim=True)
    return x * torch.rsqrt(variance + epsilon) * weight


def _rope_tables_t(seq_len: int, head_dim: int, theta: float) -> tuple[torch.Tensor, torch.Tensor]:
    half = head_dim // 2
    inv_freq = theta ** (-2.0 * torch.arange(half, dtype=torch.float64) / head_dim)
    positions = torch.arange(seq_len, dtype=torch.float64)
    angles = torch.outer(positions, inv_freq)  # [seq_len, half]
    cos = torch.cat([angles.cos(), angles.cos()], dim=-1).to(torch.float32)  # [seq_len, head_dim]
    sin = torch.cat([angles.sin(), angles.sin()], dim=-1).to(torch.float32)
    return cos, sin


def _apply_rope_t(x: torch.Tensor, cos: torch.Tensor, sin: torch.Tensor) -> torch.Tensor:
    # x: [..., head_dim]; cos/sin: [..., head_dim] broadcastable
    half = x.shape[-1] // 2
    x1, x2 = x[..., :half], x[..., half:]
    rotated = torch.cat([-x2, x1], dim=-1)
    return x * cos + rotated * sin


def forward_torch(token_ids: np.ndarray, weights: ModelWeights, *,
                   hidden: int, n_q_heads: int, n_kv_heads: int, head_dim: int,
                   rmsnorm_epsilon: float, rope_theta: float) -> torch.Tensor:
    """Returns logits [seq, vocab] as a torch.Tensor (float32, CPU)."""
    seq = token_ids.shape[0]
    tok = torch.from_numpy(token_ids.astype(np.int64))
    embedding = torch.from_numpy(weights.token_embedding)  # [vocab, hidden]
    x = embedding[tok]  # [seq, hidden]

    cos_table, sin_table = _rope_tables_t(seq, head_dim, rope_theta)  # [seq, head_dim] each

    causal = torch.triu(torch.ones(seq, seq, dtype=torch.bool), diagonal=1)  # True = masked

    for lw in weights.layers:
        attn_norm_w = torch.from_numpy(lw.attn_norm_weight)
        w_q = torch.from_numpy(lw.w_q)
        w_k = torch.from_numpy(lw.w_k)
        w_v = torch.from_numpy(lw.w_v)
        w_o = torch.from_numpy(lw.w_o)
        ffn_norm_w = torch.from_numpy(lw.ffn_norm_weight)
        w_gate = torch.from_numpy(lw.w_gate)
        w_up = torch.from_numpy(lw.w_up)
        w_down = torch.from_numpy(lw.w_down)

        a = _rmsnorm_t(x, attn_norm_w, rmsnorm_epsilon)

        q = a @ w_q.T  # [seq, n_q_heads*head_dim]
        k = a @ w_k.T  # [seq, n_kv_heads*head_dim]
        v = a @ w_v.T

        q = q.view(seq, n_q_heads, head_dim).transpose(0, 1)   # [n_q_heads, seq, head_dim]
        k = k.view(seq, n_kv_heads, head_dim).transpose(0, 1)  # [n_kv_heads, seq, head_dim]
        v = v.view(seq, n_kv_heads, head_dim).transpose(0, 1)

        cos_b = cos_table.unsqueeze(0)  # [1, seq, head_dim] broadcasts over heads
        sin_b = sin_table.unsqueeze(0)
        q = _apply_rope_t(q, cos_b, sin_b)
        k = _apply_rope_t(k, cos_b, sin_b)

        scale = 1.0 / (head_dim ** 0.5)
        context_per_head = []
        for h in range(n_q_heads):
            kv_h = h // 2  # GQA mapping, per Profile A
            scores = (q[h] @ k[kv_h].T) * scale  # [seq, seq]
            scores = scores.masked_fill(causal, float("-inf"))
            probs = torch.softmax(scores, dim=-1)
            context_per_head.append(probs @ v[kv_h])  # [seq, head_dim]
        context = torch.stack(context_per_head, dim=0)  # [n_q_heads, seq, head_dim]
        context = context.transpose(0, 1).reshape(seq, n_q_heads * head_dim)

        attn_out = context @ w_o.T
        r = x + attn_out

        f = _rmsnorm_t(r, ffn_norm_w, rmsnorm_epsilon)
        gate = f @ w_gate.T
        up = f @ w_up.T
        activated = torch.nn.functional.silu(gate) * up
        ffn_out = activated @ w_down.T

        x = r + ffn_out

    final_norm_w = torch.from_numpy(weights.final_norm_weight)
    final_normed = _rmsnorm_t(x, final_norm_w, rmsnorm_epsilon)
    logits = final_normed @ embedding.T
    return logits
