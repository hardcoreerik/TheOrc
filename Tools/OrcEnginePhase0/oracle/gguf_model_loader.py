# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Loads an arbitrary real, on-disk GGUF file (any ggml quantization -- Q4_K,
Q5_0, Q6_K, Q8_0, F16, F32, ...) into oracle/model.py's ModelWeights, so
the same reference-oracle forward pass and ablation_sweep.py machinery
that were built for the pinned SmolLM2-135M candidate can run against
ANY llama-architecture GGUF already sitting in TheOrc's model store --
not just the one Phase 0 pinned and converted from safetensors.

Uses gguf-py's own GGUFReader (strict, independent parser -- same
convention as oracle/convert_real_candidate.py, which always prefers the
official `gguf` package over hand-rolled parsing) plus its `dequantize`
helper, which implements the real ggml block-dequantization math for
every quant type gguf-py knows about. Nothing here reimplements
quantization math -- if `dequantize` doesn't support a tensor's type it
fails loudly rather than approximating.

Architecture scope: "llama", "qwen2", and "phi3" GGUF -- the block
structures oracle/model.py's forward() actually implements (RMSNorm,
GQA, split-half RoPE, SwiGLU; qwen2 additionally has Q/K/V projection
bias; phi3 additionally has a FUSED attn_qkv weight and a FUSED
ffn_up-with-gate weight, split at load time below, plus a "partial
rotary factor" -- only the leading rope.dimension_count columns of each
head get rotated, the rest pass through untouched, see
oracle/ops.py's rope_cos_sin/apply_rope rotary_dim parameter). Any other
`general.architecture` (e.g. "qwen35"/"qwen3", "gemma4", "deepseek2",
"gpt-oss", "nemotron_h" -- confirmed via the actual GGUF metadata of
models in this fleet to use meaningfully different mechanisms: QK-norm,
sliding-window/soft-capped attention, multi-head latent attention,
mixture-of-experts, or Mamba/SSM state-space blocks instead of attention
entirely) is out of scope and rejected with a clear message rather than
silently misinterpreted -- extending to a new architecture family means
actually implementing its block semantics in oracle/model.py first, not
guessing that it's "close enough" to what's already supported.
"""
from __future__ import annotations

import numpy as np
from gguf import GGUFReader, dequantize

from oracle.model import ModelConfig
from oracle.weights import LayerWeights, ModelWeights


def _meta_scalar(reader: GGUFReader, key: str, *, required: bool = True):
    field = reader.fields.get(key)
    if field is None:
        if required:
            raise KeyError(f"required GGUF metadata key {key!r} not found")
        return None
    return field.parts[field.data[0]][0]


def _meta_str(reader: GGUFReader, key: str) -> str:
    field = reader.fields.get(key)
    raw = field.parts[field.data[0]]
    return bytes(raw).decode("utf-8")


SUPPORTED_ARCHITECTURES = ("llama", "qwen2", "phi3")


def load_gguf_as_model_weights(path: str) -> tuple[ModelWeights, ModelConfig, dict]:
    """Returns (weights, config, info) where info carries provenance/description fields
    (file path, architecture, per-tensor quant types actually seen, tied-embedding flag)
    useful for labeling ablation-sweep reports without re-opening the file."""
    reader = GGUFReader(path)

    arch = _meta_str(reader, "general.architecture")
    if arch not in SUPPORTED_ARCHITECTURES:
        raise ValueError(
            f"{path!r}: architecture {arch!r} is not supported -- oracle/model.py only "
            f"implements block semantics for {SUPPORTED_ARCHITECTURES}"
        )

    # Metadata keys are namespaced by the model's own architecture string (llama.*,
    # qwen2.*, ...), not a fixed "llama." prefix -- Qwen2 GGUF files use "qwen2.*" keys
    # even though the block structure is llama-like.
    n_layers = int(_meta_scalar(reader, f"{arch}.block_count"))
    hidden = int(_meta_scalar(reader, f"{arch}.embedding_length"))
    intermediate = int(_meta_scalar(reader, f"{arch}.feed_forward_length"))
    n_heads = int(_meta_scalar(reader, f"{arch}.attention.head_count"))
    n_kv_heads = int(_meta_scalar(reader, f"{arch}.attention.head_count_kv"))
    rms_eps = float(_meta_scalar(reader, f"{arch}.attention.layer_norm_rms_epsilon"))
    rope_theta = float(_meta_scalar(reader, f"{arch}.rope.freq_base", required=False) or 10000.0)
    max_pos = int(_meta_scalar(reader, f"{arch}.context_length"))
    # head_dim is a property of the Q/K/V tensor shapes (out_features / n_heads), NOT the
    # same thing as rope.dimension_count -- those happen to be equal for full-rotary models
    # (llama, qwen2) but NOT for partial-rotary models like phi3 (rope.dimension_count=96,
    # actual head_dim=128). Conflating them here was a latent bug this fleet's phi3 support
    # surfaced: computing head_dim from rope.dimension_count would silently truncate every
    # Q/K/V projection to 96 columns instead of the real 128, corrupting the attention math.
    key_length_meta = _meta_scalar(reader, f"{arch}.attention.key_length", required=False)
    head_dim = int(key_length_meta) if key_length_meta is not None else hidden // n_heads
    rotary_dim_meta = _meta_scalar(reader, f"{arch}.rope.dimension_count", required=False)
    rotary_dim = int(rotary_dim_meta) if rotary_dim_meta is not None else head_dim

    tensors_by_name = {t.name: t for t in reader.tensors}
    quant_types_seen: set[str] = set()

    def get(name: str) -> np.ndarray:
        t = tensors_by_name[name]
        quant_types_seen.add(t.tensor_type.name)
        # gguf's dequantize() returns the logical (out_features, in_features) shape for
        # 2D tensors, matching the HF Linear convention oracle/weights.py already assumes
        # (verified empirically against SmolLM2-360M: token_embd.weight -> (vocab, hidden)).
        arr = dequantize(t.data, t.tensor_type)
        return np.ascontiguousarray(arr, dtype=np.float32)

    def get_optional(name: str) -> np.ndarray | None:
        if name not in tensors_by_name:
            return None
        return get(name)

    def get_qkv(p: str) -> tuple[np.ndarray, np.ndarray, np.ndarray,
                                  np.ndarray | None, np.ndarray | None, np.ndarray | None]:
        """(w_q, w_k, w_v, bias_q, bias_k, bias_v). Handles both the separate-tensor layout
        (llama, qwen2) and phi3's fused attn_qkv.weight, split by output row per llama.cpp's
        own build_phi3 graph convention: rows [0:q_rows) = Q, [q_rows:q_rows+kv_rows) = K,
        the remainder = V -- verified against phi4-mini's actual tensor shape
        (attn_qkv.weight logical shape [5120, 3072] = [(24+2*8)*128, 3072], matching
        n_q_heads=24, n_kv_heads=8, head_dim=128 exactly)."""
        if (p + "attn_q.weight") in tensors_by_name:
            return (get(p + "attn_q.weight"), get(p + "attn_k.weight"), get(p + "attn_v.weight"),
                    get_optional(p + "attn_q.bias"), get_optional(p + "attn_k.bias"),
                    get_optional(p + "attn_v.bias"))
        fused = get(p + "attn_qkv.weight")
        q_rows, kv_rows = n_heads * head_dim, n_kv_heads * head_dim
        w_q, w_k, w_v = fused[:q_rows], fused[q_rows:q_rows + kv_rows], fused[q_rows + kv_rows:]
        fused_bias = get_optional(p + "attn_qkv.bias")
        if fused_bias is None:
            return w_q, w_k, w_v, None, None, None
        return (w_q, w_k, w_v, fused_bias[:q_rows], fused_bias[q_rows:q_rows + kv_rows],
                fused_bias[q_rows + kv_rows:])

    def get_gate_up(p: str) -> tuple[np.ndarray, np.ndarray]:
        """(w_gate, w_up). phi3 fuses these into one ffn_up.weight, output rows split
        [0:intermediate)=gate, [intermediate:2*intermediate)=up -- llama.cpp's build_phi3
        graph does the same split (ggml_view of the first/second half of the projection
        output), verified against phi4-mini's ffn_up.weight logical shape [16384, 3072]
        = [2*8192, 3072] matching feed_forward_length=8192 exactly."""
        if (p + "ffn_gate.weight") in tensors_by_name:
            return get(p + "ffn_gate.weight"), get(p + "ffn_up.weight")
        fused = get(p + "ffn_up.weight")
        half = fused.shape[0] // 2
        return fused[:half], fused[half:]

    token_embedding = get("token_embd.weight")
    final_norm_weight = get("output_norm.weight")
    # Load the REAL output.weight when present, rather than only detecting-and-discarding it.
    # Previously this loader reported tied_embeddings=False in `info` but every forward pass
    # still unconditionally used token_embedding.T for logits -- a silent wrong-math bug for any
    # untied model (confirmed: it corrupted the first Llama-3.1-8B streaming ablation result,
    # see DECISION_LOG). lm_head stays None (tied) when output.weight doesn't exist.
    lm_head = get_optional("output.weight")
    tied = lm_head is None

    layers = []
    for i in range(n_layers):
        p = f"blk.{i}."
        w_q, w_k, w_v, q_bias, k_bias, v_bias = get_qkv(p)
        w_gate, w_up = get_gate_up(p)
        layers.append(LayerWeights(
            attn_norm_weight=get(p + "attn_norm.weight"),
            w_q=w_q, w_k=w_k, w_v=w_v,
            w_o=get(p + "attn_output.weight"),
            ffn_norm_weight=get(p + "ffn_norm.weight"),
            w_gate=w_gate, w_up=w_up,
            w_down=get(p + "ffn_down.weight"),
            attn_q_bias=q_bias, attn_k_bias=k_bias, attn_v_bias=v_bias,
        ))

    weights = ModelWeights(
        seed=-1, generator=f"real GGUF file (dequantized): {path}",
        weight_scale=float("nan"), token_embedding=token_embedding,
        layers=tuple(layers), final_norm_weight=final_norm_weight,
        lm_head=lm_head,
    )
    config = ModelConfig(
        vocab=token_embedding.shape[0], hidden=hidden, intermediate=intermediate,
        n_layers=n_layers, n_q_heads=n_heads, n_kv_heads=n_kv_heads, head_dim=head_dim,
        max_positions=max_pos, rmsnorm_epsilon=rms_eps, rope_theta=rope_theta,
        rotary_dim=rotary_dim,
    )
    info = {
        "path": path,
        "architecture": arch,
        "tied_embeddings": tied,
        "has_qkv_bias": any(lw.attn_q_bias is not None for lw in layers),
        "has_fused_qkv": ("blk.0.attn_qkv.weight" in tensors_by_name),
        "partial_rotary": rotary_dim != head_dim,
        "quant_types": sorted(quant_types_seen),
        "n_tensors": len(tensors_by_name),
    }
    return weights, config, info
