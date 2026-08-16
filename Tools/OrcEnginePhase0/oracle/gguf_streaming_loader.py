# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
TRUE streaming GGUF loader: unlike gguf_gpu_loader.py (which still holds
every layer resident in VRAM after loading, just uploads them one at a
time to avoid a CPU RAM spike), this loader keeps only ONE layer's
weights in memory/VRAM at any moment, for the ENTIRE lifetime of a
forward pass -- load layer i, use it, discard it, load layer i+1.

This is the actual fix for the capacity ceiling every fleet run has hit
so far (CPU: ~3-4B params from full fp32 materialization; GPU: still
blocked on 8B+ models even in fp16, since gguf_gpu_loader.py also
materializes the whole model, just in VRAM instead of RAM). Peak memory
here is bounded by ONE layer's weight size (typically a few hundred MB
to ~1GB) plus the token embedding matrix (kept resident across the
whole sweep since it's needed for both the input lookup AND the tied
output projection on every single forward call -- reloading it from
disk every time would be pure waste) -- NOT by the model's total size.
A model with 32 layers costs the same peak memory as one with 80.

The real tradeoff, measured not assumed: every forward call re-reads
and re-dequantizes every layer from disk, since nothing but the
embedding is cached between calls. For a sweep of N ablation specs,
that's N full re-reads of the whole model. This is why
ablation_sweep_streaming.py defaults to "layers_only" scope (N =
n_layers specs, not N = n_layers * ~28 for full component coverage) --
full-component streaming would multiply an already-expensive re-read
cost by ~28x for no better reason than "we could."
"""
from __future__ import annotations

import numpy as np
import torch
from gguf import GGUFReader, dequantize

from oracle.gguf_model_loader import SUPPORTED_ARCHITECTURES, _meta_scalar, _meta_str
from oracle.model import ModelConfig
from oracle.model_gpu import TorchLayerWeights


class StreamingGGUFModel:
    """Opens a GGUF file once (GGUFReader is a memory-mapped, lazy reader -- this does NOT
    read the whole file into memory) and keeps only the token embedding + final norm
    resident. get_layer(i) loads and dequantizes ONE layer's tensors on demand; the caller
    is expected to discard the returned TorchLayerWeights after using it (del + let Python's
    refcounting free the VRAM) before requesting the next layer."""

    def __init__(self, path: str, device: str = "cuda", dtype: torch.dtype = torch.float16):
        self.path = path
        self.device = device
        self.dtype = dtype
        self.reader = GGUFReader(path)

        arch = _meta_str(self.reader, "general.architecture")
        if arch not in SUPPORTED_ARCHITECTURES:
            raise ValueError(
                f"{path!r}: architecture {arch!r} is not supported -- oracle/model_gpu.py "
                f"only implements block semantics for {SUPPORTED_ARCHITECTURES}"
            )
        self.arch = arch

        self.n_layers = int(_meta_scalar(self.reader, f"{arch}.block_count"))
        hidden = int(_meta_scalar(self.reader, f"{arch}.embedding_length"))
        intermediate = int(_meta_scalar(self.reader, f"{arch}.feed_forward_length"))
        n_heads = int(_meta_scalar(self.reader, f"{arch}.attention.head_count"))
        n_kv_heads = int(_meta_scalar(self.reader, f"{arch}.attention.head_count_kv"))
        rms_eps = float(_meta_scalar(self.reader, f"{arch}.attention.layer_norm_rms_epsilon"))
        rope_theta = float(_meta_scalar(self.reader, f"{arch}.rope.freq_base", required=False) or 10000.0)
        max_pos = int(_meta_scalar(self.reader, f"{arch}.context_length"))
        key_length_meta = _meta_scalar(self.reader, f"{arch}.attention.key_length", required=False)
        head_dim = int(key_length_meta) if key_length_meta is not None else hidden // n_heads
        rotary_dim_meta = _meta_scalar(self.reader, f"{arch}.rope.dimension_count", required=False)
        rotary_dim = int(rotary_dim_meta) if rotary_dim_meta is not None else head_dim
        self.n_heads, self.n_kv_heads, self.head_dim = n_heads, n_kv_heads, head_dim

        self.tensors_by_name = {t.name: t for t in self.reader.tensors}
        self.quant_types_seen: set[str] = set()

        self.token_embedding = self._get("token_embd.weight")
        self.final_norm_weight = self._get("output_norm.weight")
        # Load the REAL output.weight when present and keep it resident alongside
        # token_embedding (both are needed on every single forward call: token_embedding for the
        # input lookup, lm_head for the final logits) -- previously this loader only DETECTED
        # tied_embeddings via presence-of-key and then every forward pass unconditionally used
        # token_embedding.T regardless, silently computing wrong logits for genuinely untied
        # models. This is what corrupted the first Llama-3.1-8B streaming ablation result (that
        # model reports tied_embeddings: false) -- see DECISION_LOG for the correction.
        self.lm_head = self._get_optional("output.weight")
        self.tied_embeddings = self.lm_head is None

        self.config = ModelConfig(
            vocab=self.token_embedding.shape[0], hidden=hidden, intermediate=intermediate,
            n_layers=self.n_layers, n_q_heads=n_heads, n_kv_heads=n_kv_heads, head_dim=head_dim,
            max_positions=max_pos, rmsnorm_epsilon=rms_eps, rope_theta=rope_theta,
            rotary_dim=rotary_dim,
        )

    def effective_lm_head(self) -> torch.Tensor:
        return self.lm_head if self.lm_head is not None else self.token_embedding

    def _get(self, name: str) -> torch.Tensor:
        t = self.tensors_by_name[name]
        self.quant_types_seen.add(t.tensor_type.name)
        arr = dequantize(t.data, t.tensor_type)
        arr = np.ascontiguousarray(arr, dtype=np.float32)
        tensor = torch.from_numpy(arr).to(device=self.device, dtype=self.dtype)
        del arr
        return tensor

    def _get_optional(self, name: str) -> torch.Tensor | None:
        return self._get(name) if name in self.tensors_by_name else None

    def get_layer(self, i: int) -> TorchLayerWeights:
        """Loads and dequantizes ONLY layer i's tensors from disk. Caller must discard the
        returned object (del it) before calling this again for a different layer -- nothing
        here caches or reuses a previously-loaded layer."""
        p = f"blk.{i}."
        if (p + "attn_q.weight") in self.tensors_by_name:
            w_q, w_k, w_v = self._get(p + "attn_q.weight"), self._get(p + "attn_k.weight"), self._get(p + "attn_v.weight")
            q_bias = self._get_optional(p + "attn_q.bias")
            k_bias = self._get_optional(p + "attn_k.bias")
            v_bias = self._get_optional(p + "attn_v.bias")
        else:
            fused = self._get(p + "attn_qkv.weight")
            q_rows, kv_rows = self.n_heads * self.head_dim, self.n_kv_heads * self.head_dim
            w_q, w_k, w_v = fused[:q_rows], fused[q_rows:q_rows + kv_rows], fused[q_rows + kv_rows:]
            fused_bias = self._get_optional(p + "attn_qkv.bias")
            if fused_bias is None:
                q_bias = k_bias = v_bias = None
            else:
                q_bias, k_bias = fused_bias[:q_rows], fused_bias[q_rows:q_rows + kv_rows]
                v_bias = fused_bias[q_rows + kv_rows:]

        if (p + "ffn_gate.weight") in self.tensors_by_name:
            w_gate, w_up = self._get(p + "ffn_gate.weight"), self._get(p + "ffn_up.weight")
        else:
            fused = self._get(p + "ffn_up.weight")
            half = fused.shape[0] // 2
            w_gate, w_up = fused[:half], fused[half:]

        return TorchLayerWeights(
            attn_norm_weight=self._get(p + "attn_norm.weight"),
            w_q=w_q, w_k=w_k, w_v=w_v,
            w_o=self._get(p + "attn_output.weight"),
            ffn_norm_weight=self._get(p + "ffn_norm.weight"),
            w_gate=w_gate, w_up=w_up,
            w_down=self._get(p + "ffn_down.weight"),
            attn_q_bias=q_bias, attn_k_bias=k_bias, attn_v_bias=v_bias,
        )

    def info(self) -> dict:
        return {
            "path": self.path, "architecture": self.arch, "tied_embeddings": self.tied_embeddings,
            "quant_types": sorted(self.quant_types_seen), "n_tensors": len(self.tensors_by_name),
            "dtype": str(self.dtype), "device": self.device, "streaming": True,
        }
