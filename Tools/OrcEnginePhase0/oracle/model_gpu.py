# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
GPU-accelerated forward pass for the ablation-sweep fleet tooling ONLY --
NOT part of the Phase 0 reference oracle (oracle/model.py), which stays
pure-NumPy/CPU/no-fused-paths by deliberate design (see its own
docstring: "intentionally written as plain NumPy... so it is easy to
read against the spec line by line during review"). That discipline
exists to make Phase 0's correctness claims auditable; it has nothing to
do with the ablation-sweep tooling's actual job, which is diagnostic
("no pass/fail acceptance criterion", per oracle/ablation_sweep.py's own
docstring) and was hitting two real limits from being CPU/NumPy-bound:
  1. Speed: a 32-layer, 200k-vocab model's full sweep took ~20 minutes
     in the per-position Python loops oracle/model.py uses.
  2. RAM: full fp32 dequantization into system RAM capped every fleet
     run at roughly <=3-4B params on a 33GB machine.
This module re-implements the SAME block semantics (RMSNorm, GQA,
split-half RoPE with optional partial-rotary, SwiGLU, optional Q/K/V
bias) as oracle/model.py's forward(), but vectorized over the whole
sequence and run on CUDA -- verified to agree with the CPU oracle within
float32-vs-float32 numerical tolerance (see verify_against_cpu_oracle.py)
before being trusted for real fleet results.
"""
from __future__ import annotations

from dataclasses import dataclass

import torch

from oracle.model import ModelConfig


@dataclass
class TorchLayerWeights:
    attn_norm_weight: torch.Tensor
    w_q: torch.Tensor
    w_k: torch.Tensor
    w_v: torch.Tensor
    w_o: torch.Tensor
    ffn_norm_weight: torch.Tensor
    w_gate: torch.Tensor
    w_up: torch.Tensor
    w_down: torch.Tensor
    attn_q_bias: torch.Tensor | None = None
    attn_k_bias: torch.Tensor | None = None
    attn_v_bias: torch.Tensor | None = None


@dataclass
class TorchModelWeights:
    token_embedding: torch.Tensor
    layers: list[TorchLayerWeights]
    final_norm_weight: torch.Tensor


def _rmsnorm(x: torch.Tensor, weight: torch.Tensor, eps: float) -> torch.Tensor:
    mean_sq = x.pow(2).mean(dim=-1, keepdim=True)
    return x * torch.rsqrt(mean_sq + eps) * weight


def _rope_cos_sin(seq_len: int, rotary_dim: int, theta: float,
                   device: torch.device, dtype: torch.dtype) -> tuple[torch.Tensor, torch.Tensor]:
    """[seq_len, rotary_dim] cos/sin, vectorized over all positions at once (the CPU oracle
    computes these one position at a time in a Python loop -- this is the same math, just
    not re-looped per position)."""
    half = rotary_dim // 2
    i = torch.arange(half, device=device, dtype=torch.float64)
    freqs = theta ** (-2.0 * i / rotary_dim)
    positions = torch.arange(seq_len, device=device, dtype=torch.float64)
    angles = torch.outer(positions, freqs)  # [seq, half]
    cos_half, sin_half = torch.cos(angles), torch.sin(angles)
    cos = torch.cat([cos_half, cos_half], dim=-1).to(dtype)  # [seq, rotary_dim]
    sin = torch.cat([sin_half, sin_half], dim=-1).to(dtype)
    return cos, sin


def _rotate_half(x: torch.Tensor) -> torch.Tensor:
    half = x.shape[-1] // 2
    first, second = x[..., :half], x[..., half:]
    return torch.cat([-second, first], dim=-1)


def _apply_rope(x: torch.Tensor, cos: torch.Tensor, sin: torch.Tensor) -> torch.Tensor:
    """x: [n_heads, seq, head_dim]. cos/sin: [seq, rotary_dim] (rotary_dim <= head_dim).
    Same partial-rotary semantics as oracle/ops.py's apply_rope."""
    rotary_dim = cos.shape[-1]
    head_dim = x.shape[-1]
    cos_b, sin_b = cos.unsqueeze(0), sin.unsqueeze(0)  # [1, seq, rotary_dim]
    if rotary_dim == head_dim:
        return x * cos_b + _rotate_half(x) * sin_b
    x_rot, x_pass = x[..., :rotary_dim], x[..., rotary_dim:]
    rotated = x_rot * cos_b + _rotate_half(x_rot) * sin_b
    return torch.cat([rotated, x_pass], dim=-1)


def forward_gpu(token_ids: torch.Tensor, weights: TorchModelWeights, config: ModelConfig) -> torch.Tensor:
    """token_ids: [seq] int64 tensor on the target device. Returns logits [seq, vocab],
    float32. Full-prefix only (no KV cache) -- matches oracle/model.py's forward(), not
    forward_cached(); ablation sweeps only ever use the non-cached path.

    Precision policy: weights are stored in fp16 at rest (that's where the VRAM saving
    this whole GPU tool exists for actually comes from), but EVERY compute step here runs
    in float32 -- x/activations are upcast to float32 immediately after the embedding
    lookup and stay float32 for the entire forward pass; each fp16 weight tensor is cast
    to float32 only transiently, right at its point of use in a matmul (a single-tensor,
    temporary allocation, not a second full-model copy).

    This was NOT the original design -- earlier versions kept everything in fp16 except
    RMSNorm's reduction and attention's softmax, based on where overflow was first found.
    That approach kept finding NEW overflow sites one at a time (verify_gpu_against_cpu.py
    caught RMSNorm's x^2 overflowing at ~20000-magnitude activations; a later ablation-sweep
    run then hit a SECOND overflow in the FFN down-projection, activated~6200 x w_down
    accumulated over the intermediate dimension exceeding fp16's 65504 max). Patching
    overflow sites as they're discovered is an unbounded whack-a-mole against every new
    model/prompt/ablation combination -- full float32 compute closes the entire class at
    once, at the cost of losing the fp16 compute-speed win (VRAM savings from fp16 storage
    are unaffected; only the arithmetic itself, which was never actually the bottleneck
    here, moves back to float32)."""
    device = token_ids.device
    x = weights.token_embedding[token_ids].float()  # [seq, hidden], float32 from here on
    seq = x.shape[0]

    rotary_dim = config.rotary_dim if config.rotary_dim is not None else config.head_dim
    cos, sin = _rope_cos_sin(seq, rotary_dim, config.rope_theta, device, torch.float32)

    causal_mask = torch.triu(
        torch.full((seq, seq), float("-inf"), device=device, dtype=torch.float32), diagonal=1
    )  # [seq, seq], additive mask

    scale = 1.0 / (config.head_dim ** 0.5)
    group_size = config.n_q_heads // config.n_kv_heads

    for lw in weights.layers:
        a = _rmsnorm(x, lw.attn_norm_weight.float(), config.rmsnorm_epsilon)

        q = a @ lw.w_q.float().T
        k = a @ lw.w_k.float().T
        v = a @ lw.w_v.float().T
        if lw.attn_q_bias is not None:
            q = q + lw.attn_q_bias.float()
        if lw.attn_k_bias is not None:
            k = k + lw.attn_k_bias.float()
        if lw.attn_v_bias is not None:
            v = v + lw.attn_v_bias.float()

        q = q.view(seq, config.n_q_heads, config.head_dim).transpose(0, 1)   # [n_q, seq, hd]
        k = k.view(seq, config.n_kv_heads, config.head_dim).transpose(0, 1)  # [n_kv, seq, hd]
        v = v.view(seq, config.n_kv_heads, config.head_dim).transpose(0, 1)

        q = _apply_rope(q, cos, sin)
        k = _apply_rope(k, cos, sin)

        # GQA: repeat each KV head group_size times to line up with query heads.
        k_rep = k.repeat_interleave(group_size, dim=0)  # [n_q, seq, hd]
        v_rep = v.repeat_interleave(group_size, dim=0)

        scores = torch.einsum("hsd,htd->hst", q, k_rep) * scale  # [n_q, seq, seq]
        scores = scores + causal_mask.unsqueeze(0)
        probs = torch.softmax(scores, dim=-1)
        context = torch.einsum("hst,htd->hsd", probs, v_rep)  # [n_q, seq, hd]
        context_flat = context.transpose(0, 1).reshape(seq, config.n_q_heads * config.head_dim)

        attn_out = context_flat @ lw.w_o.float().T
        r = x + attn_out

        f = _rmsnorm(r, lw.ffn_norm_weight.float(), config.rmsnorm_epsilon)
        gate = f @ lw.w_gate.float().T
        up = f @ lw.w_up.float().T
        activated = torch.nn.functional.silu(gate) * up
        ffn = activated @ lw.w_down.float().T
        x = r + ffn

    final_normed = _rmsnorm(x, weights.final_norm_weight.float(), config.rmsnorm_epsilon)
    logits = final_normed @ weights.token_embedding.float().T
    return logits
