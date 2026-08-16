# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Ablation sweep using gguf_streaming_loader.StreamingGGUFModel -- the
actual fix for the capacity ceiling every prior fleet run hit (CPU:
~3-4B params from full fp32 RAM materialization; GPU: still blocked on
8B+ models in fp16 VRAM, since gguf_gpu_loader.py also loads the whole
model at once, just into VRAM instead of RAM). This module trades
speed for capacity: peak memory is one layer's weights + the resident
embedding matrix, REGARDLESS of total model size, at the cost of
re-reading every layer from disk on every single forward call (nothing
but the embedding is cached between calls).

Defaults to "layers_only" ablation scope for exactly that reason -- a
full-component sweep would multiply an already-expensive per-forward
re-read cost by ~28x (every head + every FFN sub-matrix per layer) for
models this tool exists specifically to make merely POSSIBLE, not fast.
"""
from __future__ import annotations

import time

import numpy as np
import torch

from oracle.ablation_sweep import AblationSpec, _all_specs, _divergence_per_position, _mean_of
from oracle.gguf_streaming_loader import StreamingGGUFModel
from oracle.model import ModelConfig
from oracle.model_gpu import TorchLayerWeights, _apply_rope, _rmsnorm, _rope_cos_sin


def _zero_component_inplace(lw: TorchLayerWeights, spec: AblationSpec, config: ModelConfig) -> None:
    """Zeros the targeted component in a FRESHLY LOADED (not shared/resident) layer -- no
    restore needed, since this layer gets discarded after this one forward call regardless."""
    if spec.component == "full_layer":
        for name in ("w_q", "w_k", "w_v", "w_o", "w_gate", "w_up", "w_down"):
            getattr(lw, name).zero_()
    elif spec.component == "attn_head":
        assert spec.head_idx is not None
        start, end = spec.head_idx * config.head_dim, (spec.head_idx + 1) * config.head_dim
        lw.w_o[:, start:end] = 0.0
    elif spec.component in ("ffn_gate", "ffn_up", "ffn_down"):
        attr = {"ffn_gate": "w_gate", "ffn_up": "w_up", "ffn_down": "w_down"}[spec.component]
        getattr(lw, attr).zero_()
    else:
        raise ValueError(f"unknown ablation component: {spec.component!r}")


def forward_streaming(token_ids: torch.Tensor, model: StreamingGGUFModel,
                       ablation_spec: AblationSpec | None = None) -> torch.Tensor:
    """One full-prefix forward pass, loading each layer from disk just-in-time and
    discarding it before moving to the next. Full float32 compute throughout (same
    precision policy as model_gpu.forward_gpu, same reasons -- see that module's
    docstring for the overflow bugs that made "fp16 storage, fp32 compute" the right
    default rather than an optional extra)."""
    config = model.config
    device = token_ids.device
    x = model.token_embedding[token_ids].float()
    seq = x.shape[0]

    rotary_dim = config.rotary_dim if config.rotary_dim is not None else config.head_dim
    cos, sin = _rope_cos_sin(seq, rotary_dim, config.rope_theta, device, torch.float32)
    causal_mask = torch.triu(
        torch.full((seq, seq), float("-inf"), device=device, dtype=torch.float32), diagonal=1
    )
    scale = 1.0 / (config.head_dim ** 0.5)
    group_size = config.n_q_heads // config.n_kv_heads

    for layer_idx in range(config.n_layers):
        lw = model.get_layer(layer_idx)
        if ablation_spec is not None and ablation_spec.layer_idx == layer_idx:
            _zero_component_inplace(lw, ablation_spec, config)

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

        q = q.view(seq, config.n_q_heads, config.head_dim).transpose(0, 1)
        k = k.view(seq, config.n_kv_heads, config.head_dim).transpose(0, 1)
        v = v.view(seq, config.n_kv_heads, config.head_dim).transpose(0, 1)
        q = _apply_rope(q, cos, sin)
        k = _apply_rope(k, cos, sin)
        k_rep = k.repeat_interleave(group_size, dim=0)
        v_rep = v.repeat_interleave(group_size, dim=0)

        scores = torch.einsum("hsd,htd->hst", q, k_rep) * scale
        scores = scores + causal_mask.unsqueeze(0)
        probs = torch.softmax(scores, dim=-1)
        context = torch.einsum("hst,htd->hsd", probs, v_rep)
        context_flat = context.transpose(0, 1).reshape(seq, config.n_q_heads * config.head_dim)

        attn_out = context_flat @ lw.w_o.float().T
        r = x + attn_out
        f = _rmsnorm(r, lw.ffn_norm_weight.float(), config.rmsnorm_epsilon)
        gate = f @ lw.w_gate.float().T
        up = f @ lw.w_up.float().T
        activated = torch.nn.functional.silu(gate) * up
        ffn = activated @ lw.w_down.float().T
        x = r + ffn

        del lw  # discard this layer's weights before loading the next -- the whole point

    final_normed = _rmsnorm(x, model.final_norm_weight.float(), config.rmsnorm_epsilon)
    logits = final_normed @ model.token_embedding.float().T
    return logits


def run_sweep_streaming(gguf_path: str, prompts: list[np.ndarray], *, model_label: str,
                         device: str = "cuda", components: str = "layers_only",
                         log_progress: bool = True) -> dict:
    t0 = time.time()
    model = StreamingGGUFModel(gguf_path, device=device)
    config = model.config
    specs = _all_specs(config, components=components)
    if log_progress:
        print(f"  streaming model: n_layers={config.n_layers} hidden={config.hidden} "
              f"vocab={config.vocab} -- {len(specs)} specs x {len(prompts) + 1} forward passes "
              f"= {len(specs) * len(prompts) + len(prompts)} full model re-reads from disk")

    prompts_gpu = [torch.from_numpy(p).to(device) for p in prompts]
    with torch.no_grad():
        baseline_logits_by_prompt = [
            forward_streaming(p, model).float().cpu().numpy() for p in prompts_gpu
        ]
    if log_progress:
        print(f"  baseline done ({time.time() - t0:.1f}s elapsed)")

    results = []
    with torch.no_grad():
        for i, spec in enumerate(specs):
            per_prompt = []
            for prompt_np, prompt_gpu, baseline_logits in zip(
                prompts, prompts_gpu, baseline_logits_by_prompt, strict=True
            ):
                ablated_logits = forward_streaming(prompt_gpu, model, ablation_spec=spec).float().cpu().numpy()
                per_prompt.append({
                    "token_ids": prompt_np.tolist(),
                    **_divergence_per_position(baseline_logits, ablated_logits),
                })
            aggregated = [_mean_of(pp) for pp in per_prompt]
            mean_across_prompts = {k: float(np.mean([a[k] for a in aggregated])) for k in aggregated[0]}
            results.append({
                "label": spec.label, "layer_idx": spec.layer_idx, "component": spec.component,
                "head_idx": spec.head_idx, **mean_across_prompts, "per_prompt": per_prompt,
            })
            if log_progress and (i + 1) % max(1, len(specs) // 10) == 0:
                print(f"  {i + 1}/{len(specs)} specs done ({time.time() - t0:.1f}s elapsed)")

    results.sort(key=lambda r: r["logit_l2"], reverse=True)
    return {
        "model_label": model_label, "n_prompts": len(prompts), "components_swept": len(results),
        "elapsed_seconds": time.time() - t0, "results": results, "device": device,
        "streaming": True, "gguf_info": model.info(),
    }
