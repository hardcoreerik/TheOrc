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
    # None = full rotation (head_dim), the Profile A / Llama / Qwen2 default. Set smaller
    # than head_dim for "partial rotary factor" models (Phi-3/Phi-4: rotary_dim=96 of
    # head_dim=128) -- see ops.rope_cos_sin/apply_rope for the actual partial-rotation math.
    rotary_dim: int | None = None


@dataclass
class ForwardResult:
    logits: np.ndarray                     # [seq, vocab] -- one row per position
    taps: dict[str, Any] = field(default_factory=dict)


@dataclass
class FaultSpec:
    """
    Deliberate, named perturbations for the Phase 0 fault-injection proof
    (PHASE_0_REFERENCE_ORACLE.md "Fault-injection proof"). Every field is
    None/False by default (no fault). This is test-only surface area on the
    reference implementation, not part of Profile A's real semantics --
    weight-level faults (e.g. a transposed projection matrix) don't need a
    field here; inject those by building a perturbed ModelWeights instead.
    """
    q_rope_position_offset: int = 0        # off-by-one position fault (applied to Q only)
    wrong_rope_pairing: bool = False       # incorrect RoPE pairing (interleaved instead of split-half)
    skip_causal_mask: bool = False         # missing causal mask fault
    attn_rmsnorm_epsilon_override: float | None = None  # changed RMSNorm epsilon fault
    swap_kv_on_cache_write: bool = False   # swapped K/V cache write fault (forward_cached only)


def _gqa_kv_head_for_query_head(q_head: int, n_q_heads: int, n_kv_heads: int) -> int:
    """
    Query head h maps to KV head floor(h / group_size), group_size = n_q_heads // n_kv_heads.
    Profile A's group_size is 2 (4 q heads : 2 kv heads), which is where the original
    hardcoded "h // 2" came from -- generalized here because real models use other
    ratios (e.g. SmolLM2-135M: 9 q heads : 3 kv heads, group_size 3, NOT 2).
    """
    assert n_q_heads % n_kv_heads == 0, f"n_q_heads={n_q_heads} not divisible by n_kv_heads={n_kv_heads}"
    group_size = n_q_heads // n_kv_heads
    return q_head // group_size


def _split_heads(x: np.ndarray, n_heads: int, head_dim: int) -> np.ndarray:
    """[seq, n_heads*head_dim] -> [n_heads, seq, head_dim]."""
    seq = x.shape[0]
    return x.reshape(seq, n_heads, head_dim).transpose(1, 0, 2)


def _wrong_pairing_rotate(x: np.ndarray) -> np.ndarray:
    """
    Incorrect ("interleaved") RoPE pairing fault: rotates adjacent (even,
    odd) pairs instead of the correct split-half (first_half, second_half)
    pairing used by ops.rope_rotate_half. Deliberately wrong for Profile A.
    """
    head_dim = x.shape[-1]
    out = np.zeros_like(x)
    out[..., 0::2] = -x[..., 1::2]
    out[..., 1::2] = x[..., 0::2]
    return out.astype(DTYPE)


def forward(
    token_ids: np.ndarray,
    weights: ModelWeights,
    config: ModelConfig,
    *,
    capture_taps: bool = True,
    fault: "FaultSpec | None" = None,
) -> ForwardResult:
    taps: dict[str, Any] = {}
    seq = token_ids.shape[0]
    fault = fault or FaultSpec()

    # Step: input embedding lookup.
    x = ops.embedding_lookup(weights.token_embedding, token_ids)  # [seq, hidden]
    if capture_taps:
        taps["input_embedding"] = x.copy()

    # Precompute RoPE cos/sin per position for this head_dim/theta.
    cos_by_pos = []
    sin_by_pos = []
    for p in range(seq):
        c, s = ops.rope_cos_sin(position=p, head_dim=config.head_dim, theta=config.rope_theta,
                                 rotary_dim=config.rotary_dim)
        cos_by_pos.append(c)
        sin_by_pos.append(s)

    for layer_idx, lw in enumerate(weights.layers):
        layer_taps: dict[str, Any] = {}

        # 1. a = RMSNorm(x, attn_norm_weight, epsilon)
        attn_epsilon = (fault.attn_rmsnorm_epsilon_override
                         if fault.attn_rmsnorm_epsilon_override is not None
                         else config.rmsnorm_epsilon)
        a = ops.rmsnorm(x, lw.attn_norm_weight, attn_epsilon)
        if capture_taps:
            layer_taps["pre_attention_normalized_state"] = a.copy()

        # 2. q = a Wq^T (+bias), k = a Wk^T (+bias), v = a Wv^T (+bias)
        # Bias is None for Profile A / SmolLM2-135M, present for Qwen2-family models
        # loaded via oracle/gguf_model_loader.py -- added post-projection, pre-RoPE,
        # matching Qwen2's actual attention block (bias only on Q/K/V, never attn_output).
        q_flat = ops.linear_no_bias(a, lw.w_q)  # [seq, n_q_heads*head_dim]
        k_flat = ops.linear_no_bias(a, lw.w_k)  # [seq, n_kv_heads*head_dim]
        v_flat = ops.linear_no_bias(a, lw.w_v)  # [seq, n_kv_heads*head_dim]
        if lw.attn_q_bias is not None:
            q_flat = (q_flat + lw.attn_q_bias).astype(DTYPE)
        if lw.attn_k_bias is not None:
            k_flat = (k_flat + lw.attn_k_bias).astype(DTYPE)
        if lw.attn_v_bias is not None:
            v_flat = (v_flat + lw.attn_v_bias).astype(DTYPE)
        if capture_taps:
            layer_taps["q_projection"] = q_flat.copy()
            layer_taps["k_projection"] = k_flat.copy()
            layer_taps["v_projection"] = v_flat.copy()

        # 3. Reshape Q to [position, n_q_heads, head_dim]; K, V to [position, n_kv_heads, head_dim].
        q_heads = _split_heads(q_flat, config.n_q_heads, config.head_dim)   # [n_q_heads, seq, head_dim]
        k_heads = _split_heads(k_flat, config.n_kv_heads, config.head_dim)  # [n_kv_heads, seq, head_dim]
        v_heads = _split_heads(v_flat, config.n_kv_heads, config.head_dim)  # [n_kv_heads, seq, head_dim]

        # 4. Apply non-interleaved Llama RoPE to Q and K, per position.
        def _apply_rope_maybe_faulted(vec, cos, sin, *, is_query: bool) -> np.ndarray:
            if is_query and fault.wrong_rope_pairing:
                return (vec.astype(DTYPE) * cos + _wrong_pairing_rotate(vec) * sin).astype(DTYPE)
            return ops.apply_rope(vec, cos, sin)

        q_rope = np.zeros_like(q_heads)
        for h in range(config.n_q_heads):
            for p in range(seq):
                rope_pos = p + fault.q_rope_position_offset
                rope_pos = max(0, min(rope_pos, seq - 1))  # clamp -- this is a test fault, not real decode
                cos_q, sin_q = ((cos_by_pos[rope_pos], sin_by_pos[rope_pos])
                                if fault.q_rope_position_offset else (cos_by_pos[p], sin_by_pos[p]))
                q_rope[h, p] = _apply_rope_maybe_faulted(q_heads[h, p], cos_q, sin_q, is_query=True)
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
            kv_h = _gqa_kv_head_for_query_head(h, config.n_q_heads, config.n_kv_heads)
            scores = (q_rope[h] @ k_rope[kv_h].T).astype(DTYPE) * scale  # [seq, seq]
            masked = scores if fault.skip_causal_mask else ops.causal_mask(scores)
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


@dataclass
class LayerKVCache:
    k: np.ndarray  # [n_kv_heads, cached_len, head_dim] -- post-RoPE K
    v: np.ndarray  # [n_kv_heads, cached_len, head_dim]


@dataclass
class KVCache:
    layers: tuple[LayerKVCache, ...]

    @property
    def length(self) -> int:
        return self.layers[0].k.shape[1] if self.layers else 0


def empty_kv_cache(config: ModelConfig) -> KVCache:
    layers = tuple(
        LayerKVCache(
            k=np.zeros((config.n_kv_heads, 0, config.head_dim), dtype=DTYPE),
            v=np.zeros((config.n_kv_heads, 0, config.head_dim), dtype=DTYPE),
        )
        for _ in range(config.n_layers)
    )
    return KVCache(layers=layers)


def forward_cached(
    token_ids: np.ndarray,
    weights: ModelWeights,
    config: ModelConfig,
    *,
    kv_cache: "KVCache | None" = None,
    start_position: int = 0,
    capture_taps: bool = True,
    fault: "FaultSpec | None" = None,
) -> tuple[ForwardResult, "KVCache"]:
    """
    Incremental-decode variant of forward(): processes only the NEW tokens
    in token_ids, reusing kv_cache (K/V from prior positions) for attention.
    Returns logits for the new tokens only, plus the updated cache.

    kv_cache=None / start_position=0 with the full sequence is equivalent
    (mathematically) to forward()'s full-prefix path -- this equivalence is
    exactly what Fixture C's cache_equivalence test checks.
    """
    taps: dict[str, Any] = {}
    fault = fault or FaultSpec()
    new_len = token_ids.shape[0]
    if kv_cache is None:
        kv_cache = empty_kv_cache(config)
    cached_len = kv_cache.length

    x = ops.embedding_lookup(weights.token_embedding, token_ids)  # [new_len, hidden]
    if capture_taps:
        taps["input_embedding"] = x.copy()

    cos_by_pos = {}
    sin_by_pos = {}
    for p in range(start_position, start_position + new_len):
        c, s = ops.rope_cos_sin(position=p, head_dim=config.head_dim, theta=config.rope_theta,
                                 rotary_dim=config.rotary_dim)
        cos_by_pos[p] = c
        sin_by_pos[p] = s

    new_layer_caches: list[LayerKVCache] = []

    for layer_idx, lw in enumerate(weights.layers):
        layer_taps: dict[str, Any] = {}

        a = ops.rmsnorm(x, lw.attn_norm_weight, config.rmsnorm_epsilon)
        if capture_taps:
            layer_taps["pre_attention_normalized_state"] = a.copy()

        q_flat = ops.linear_no_bias(a, lw.w_q)
        k_flat_new = ops.linear_no_bias(a, lw.w_k)
        v_flat_new = ops.linear_no_bias(a, lw.w_v)
        if lw.attn_q_bias is not None:
            q_flat = (q_flat + lw.attn_q_bias).astype(DTYPE)
        if lw.attn_k_bias is not None:
            k_flat_new = (k_flat_new + lw.attn_k_bias).astype(DTYPE)
        if lw.attn_v_bias is not None:
            v_flat_new = (v_flat_new + lw.attn_v_bias).astype(DTYPE)
        if capture_taps:
            layer_taps["q_projection"] = q_flat.copy()
            layer_taps["k_projection"] = k_flat_new.copy()
            layer_taps["v_projection"] = v_flat_new.copy()

        q_heads = _split_heads(q_flat, config.n_q_heads, config.head_dim)
        k_heads_new = _split_heads(k_flat_new, config.n_kv_heads, config.head_dim)
        v_heads_new = _split_heads(v_flat_new, config.n_kv_heads, config.head_dim)

        q_rope = np.zeros_like(q_heads)
        for h in range(config.n_q_heads):
            for i, p in enumerate(range(start_position, start_position + new_len)):
                q_rope[h, i] = ops.apply_rope(q_heads[h, i], cos_by_pos[p], sin_by_pos[p])
        k_rope_new = np.zeros_like(k_heads_new)
        for h in range(config.n_kv_heads):
            for i, p in enumerate(range(start_position, start_position + new_len)):
                k_rope_new[h, i] = ops.apply_rope(k_heads_new[h, i], cos_by_pos[p], sin_by_pos[p])
        if capture_taps:
            layer_taps["q_after_rope"] = q_rope.copy()
            layer_taps["k_after_rope"] = k_rope_new.copy()

        cache_k_prior = kv_cache.layers[layer_idx].k
        cache_v_prior = kv_cache.layers[layer_idx].v
        write_k, write_v = (v_heads_new, k_rope_new) if fault.swap_kv_on_cache_write else (k_rope_new, v_heads_new)
        k_full = np.concatenate([cache_k_prior, write_k], axis=1)   # [n_kv_heads, cached_len+new_len, head_dim]
        v_full = np.concatenate([cache_v_prior, write_v], axis=1)
        if capture_taps:
            layer_taps["cache_slice_after_write"] = {"k": k_full.copy(), "v": v_full.copy()}
        new_layer_caches.append(LayerKVCache(k=k_full, v=v_full))

        scale = 1.0 / np.sqrt(np.float32(config.head_dim))
        context_heads = np.zeros_like(q_rope)
        attn_probs_by_head = []
        masked_scores_by_head = []
        for h in range(config.n_q_heads):
            kv_h = _gqa_kv_head_for_query_head(h, config.n_q_heads, config.n_kv_heads)
            scores = (q_rope[h] @ k_full[kv_h].T).astype(DTYPE) * scale  # [new_len, cached_len+new_len]
            masked = ops.causal_mask_rectangular(scores, query_start_position=start_position)
            probs = ops.softmax_last_axis(masked)
            context_heads[h] = (probs @ v_full[kv_h]).astype(DTYPE)
            masked_scores_by_head.append(masked.copy())
            attn_probs_by_head.append(probs.copy())
        if capture_taps:
            layer_taps["masked_attention_scores"] = np.stack(masked_scores_by_head)
            layer_taps["attention_probabilities"] = np.stack(attn_probs_by_head)

        context_flat = context_heads.transpose(1, 0, 2).reshape(new_len, config.n_q_heads * config.head_dim)
        if capture_taps:
            layer_taps["attention_output_before_projection"] = context_flat.copy()
        attn_out = ops.linear_no_bias(context_flat, lw.w_o)
        if capture_taps:
            layer_taps["attention_output_after_projection"] = attn_out.copy()

        r = (x + attn_out).astype(DTYPE)
        if capture_taps:
            layer_taps["post_attention_residual"] = r.copy()

        f = ops.rmsnorm(r, lw.ffn_norm_weight, config.rmsnorm_epsilon)
        if capture_taps:
            layer_taps["pre_ffn_normalized_state"] = f.copy()

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

        y = (r + ffn).astype(DTYPE)
        if capture_taps:
            layer_taps["post_ffn_residual"] = y.copy()

        x = y
        if capture_taps:
            taps[f"layer_{layer_idx}"] = layer_taps

    final_normed = ops.rmsnorm(x, weights.final_norm_weight, config.rmsnorm_epsilon)
    if capture_taps:
        taps["final_normalized_state"] = final_normed.copy()
    logits = ops.linear_no_bias(final_normed, weights.token_embedding)
    if capture_taps:
        taps["logits"] = logits.copy()
        selected = np.argmax(logits, axis=-1)
        top2 = np.sort(logits, axis=-1)[:, -2:]
        margin = top2[:, -1] - top2[:, -2]
        taps["selected_token"] = selected.copy()
        taps["top_token_margin"] = margin.copy()

    return ForwardResult(logits=logits, taps=taps), KVCache(layers=tuple(new_layer_caches))
