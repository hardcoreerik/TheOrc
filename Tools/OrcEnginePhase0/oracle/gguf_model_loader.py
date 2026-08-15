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

Architecture scope: "llama" and "qwen2" GGUF only -- the two block
structures oracle/model.py's forward() actually implements (RMSNorm,
GQA, split-half RoPE, SwiGLU; qwen2 additionally has Q/K/V projection
bias, modeled explicitly in oracle/weights.py's LayerWeights). Any other
`general.architecture` (e.g. "qwen35", the Qwen3.8 GGUF family, which
uses different attention normalization) is out of scope and rejected
with a clear message rather than silently misinterpreted -- extending to
a new architecture family means actually implementing its block
semantics in oracle/model.py first, not guessing that it's "close enough"
to llama/qwen2.
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


SUPPORTED_ARCHITECTURES = ("llama", "qwen2")


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
    head_dim_meta = _meta_scalar(reader, f"{arch}.rope.dimension_count", required=False)
    head_dim = int(head_dim_meta) if head_dim_meta is not None else hidden // n_heads

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

    token_embedding = get("token_embd.weight")
    final_norm_weight = get("output_norm.weight")
    tied = "output.weight" not in tensors_by_name

    layers = []
    for i in range(n_layers):
        p = f"blk.{i}."
        layers.append(LayerWeights(
            attn_norm_weight=get(p + "attn_norm.weight"),
            w_q=get(p + "attn_q.weight"),
            w_k=get(p + "attn_k.weight"),
            w_v=get(p + "attn_v.weight"),
            w_o=get(p + "attn_output.weight"),
            ffn_norm_weight=get(p + "ffn_norm.weight"),
            w_gate=get(p + "ffn_gate.weight"),
            w_up=get(p + "ffn_up.weight"),
            w_down=get(p + "ffn_down.weight"),
            attn_q_bias=get_optional(p + "attn_q.bias"),
            attn_k_bias=get_optional(p + "attn_k.bias"),
            attn_v_bias=get_optional(p + "attn_v.bias"),
        ))

    weights = ModelWeights(
        seed=-1, generator=f"real GGUF file (dequantized): {path}",
        weight_scale=float("nan"), token_embedding=token_embedding,
        layers=tuple(layers), final_norm_weight=final_norm_weight,
    )
    config = ModelConfig(
        vocab=token_embedding.shape[0], hidden=hidden, intermediate=intermediate,
        n_layers=n_layers, n_q_heads=n_heads, n_kv_heads=n_kv_heads, head_dim=head_dim,
        max_positions=max_pos, rmsnorm_epsilon=rms_eps, rope_theta=rope_theta,
    )
    info = {
        "path": path,
        "architecture": arch,
        "tied_embeddings": tied,
        "has_qkv_bias": any(lw.attn_q_bias is not None for lw in layers),
        "quant_types": sorted(quant_types_seen),
        "n_tensors": len(tensors_by_name),
    }
    return weights, config, info
