# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Streaming GGUF -> GPU loader for the ablation-sweep fleet tooling.

oracle/gguf_model_loader.py (the CPU/NumPy loader) dequantizes every
tensor for the WHOLE model into system RAM before returning -- that's
what caps fleet runs at roughly <=3-4B params on a 33GB machine (a 7B
model needs ~28GB just for its own float32 weights, before any
intermediate activations).

This loader dequantizes ONE TENSOR AT A TIME, immediately uploads it to
the GPU, and discards the CPU-side NumPy array before moving to the
next tensor -- so peak CPU RAM usage is bounded by the single largest
tensor in the model (a few hundred MB), not the model's total size.
GPU VRAM becomes the real ceiling instead, and using fp16 (default; see
DTYPE below) roughly halves that requirement again versus float32.

Reuses the exact same architecture-support scope and fused-tensor
splitting logic as gguf_model_loader.py (llama/qwen2/phi3, QKV/gate-up
fusion, partial rotary) -- this is a loading STRATEGY difference
(stream-to-GPU vs materialize-in-RAM), not a different set of supported
architectures. Keeping the two loaders' architecture logic in sync is a
known duplication risk; if a new architecture is added, update both.
"""
from __future__ import annotations

import numpy as np
import torch
from gguf import GGUFReader, dequantize

from oracle.gguf_model_loader import SUPPORTED_ARCHITECTURES, _meta_scalar, _meta_str
from oracle.model import ModelConfig
from oracle.model_gpu import TorchLayerWeights, TorchModelWeights

# fp16 halves VRAM vs float32 at the cost of some precision -- acceptable for this
# DIAGNOSTIC tool (ablation sweeps have no pass/fail correctness gate), NOT acceptable
# for the Phase 0 reference oracle itself, which stays float32-only by design.
DEFAULT_DTYPE = torch.float16


def load_gguf_to_gpu(
    path: str, device: str = "cuda", dtype: torch.dtype = DEFAULT_DTYPE,
) -> tuple[TorchModelWeights, ModelConfig, dict]:
    reader = GGUFReader(path)
    arch = _meta_str(reader, "general.architecture")
    if arch not in SUPPORTED_ARCHITECTURES:
        raise ValueError(
            f"{path!r}: architecture {arch!r} is not supported -- oracle/model_gpu.py only "
            f"implements block semantics for {SUPPORTED_ARCHITECTURES}"
        )

    n_layers = int(_meta_scalar(reader, f"{arch}.block_count"))
    hidden = int(_meta_scalar(reader, f"{arch}.embedding_length"))
    intermediate = int(_meta_scalar(reader, f"{arch}.feed_forward_length"))
    n_heads = int(_meta_scalar(reader, f"{arch}.attention.head_count"))
    n_kv_heads = int(_meta_scalar(reader, f"{arch}.attention.head_count_kv"))
    rms_eps = float(_meta_scalar(reader, f"{arch}.attention.layer_norm_rms_epsilon"))
    rope_theta = float(_meta_scalar(reader, f"{arch}.rope.freq_base", required=False) or 10000.0)
    max_pos = int(_meta_scalar(reader, f"{arch}.context_length"))
    key_length_meta = _meta_scalar(reader, f"{arch}.attention.key_length", required=False)
    head_dim = int(key_length_meta) if key_length_meta is not None else hidden // n_heads
    rotary_dim_meta = _meta_scalar(reader, f"{arch}.rope.dimension_count", required=False)
    rotary_dim = int(rotary_dim_meta) if rotary_dim_meta is not None else head_dim

    tensors_by_name = {t.name: t for t in reader.tensors}
    quant_types_seen: set[str] = set()

    def get(name: str) -> torch.Tensor:
        """Dequantize ONE tensor on CPU (numpy), immediately move to GPU, let the numpy
        array get garbage-collected -- this function never holds more than one
        dequantized tensor in CPU RAM at a time."""
        t = tensors_by_name[name]
        quant_types_seen.add(t.tensor_type.name)
        arr = dequantize(t.data, t.tensor_type)
        arr = np.ascontiguousarray(arr, dtype=np.float32)
        tensor = torch.from_numpy(arr).to(device=device, dtype=dtype)
        del arr
        return tensor

    def get_optional(name: str) -> torch.Tensor | None:
        if name not in tensors_by_name:
            return None
        return get(name)

    def get_qkv(p: str):
        """Same fused-tensor split as gguf_model_loader.get_qkv(), operating on GPU
        tensors after upload instead of numpy arrays before it."""
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

    def get_gate_up(p: str):
        if (p + "ffn_gate.weight") in tensors_by_name:
            return get(p + "ffn_gate.weight"), get(p + "ffn_up.weight")
        fused = get(p + "ffn_up.weight")
        half = fused.shape[0] // 2
        return fused[:half], fused[half:]

    token_embedding = get("token_embd.weight")
    final_norm_weight = get("output_norm.weight")
    tied = "output.weight" not in tensors_by_name

    layers = []
    for i in range(n_layers):
        p = f"blk.{i}."
        w_q, w_k, w_v, q_bias, k_bias, v_bias = get_qkv(p)
        w_gate, w_up = get_gate_up(p)
        layers.append(TorchLayerWeights(
            attn_norm_weight=get(p + "attn_norm.weight"),
            w_q=w_q, w_k=w_k, w_v=w_v,
            w_o=get(p + "attn_output.weight"),
            ffn_norm_weight=get(p + "ffn_norm.weight"),
            w_gate=w_gate, w_up=w_up,
            w_down=get(p + "ffn_down.weight"),
            attn_q_bias=q_bias, attn_k_bias=k_bias, attn_v_bias=v_bias,
        ))

    weights = TorchModelWeights(token_embedding=token_embedding, layers=layers,
                                 final_norm_weight=final_norm_weight)
    config = ModelConfig(
        vocab=token_embedding.shape[0], hidden=hidden, intermediate=intermediate,
        n_layers=n_layers, n_q_heads=n_heads, n_kv_heads=n_kv_heads, head_dim=head_dim,
        max_positions=max_pos, rmsnorm_epsilon=rms_eps, rope_theta=rope_theta,
        rotary_dim=rotary_dim,
    )
    info = {
        "path": path, "architecture": arch, "tied_embeddings": tied,
        "has_qkv_bias": any(lw.attn_q_bias is not None for lw in layers),
        "has_fused_qkv": ("blk.0.attn_qkv.weight" in tensors_by_name),
        "partial_rotary": rotary_dim != head_dim,
        "quant_types": sorted(quant_types_seen), "n_tensors": len(tensors_by_name),
        "dtype": str(dtype), "device": device,
    }
    return weights, config, info
