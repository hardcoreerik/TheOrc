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


def _rope_causal_scale(config: ModelConfig, seq: int, device: torch.device):
    rotary_dim = config.rotary_dim if config.rotary_dim is not None else config.head_dim
    cos, sin = _rope_cos_sin(seq, rotary_dim, config.rope_theta, device, torch.float32)
    causal_mask = torch.triu(
        torch.full((seq, seq), float("-inf"), device=device, dtype=torch.float32), diagonal=1
    )
    scale = 1.0 / (config.head_dim ** 0.5)
    group_size = config.n_q_heads // config.n_kv_heads
    return cos, sin, causal_mask, scale, group_size


def _apply_one_layer(x: torch.Tensor, lw: TorchLayerWeights, config: ModelConfig,
                      cos: torch.Tensor, sin: torch.Tensor, causal_mask: torch.Tensor,
                      scale: float, group_size: int) -> torch.Tensor:
    """One transformer block's contribution to the residual stream x -> x'. Factored out of
    forward_streaming() so both the spec-major path (below) and the layer-major path
    (run_sweep_streaming_layer_major) share the exact same block math -- the only thing that
    differs between them is WHICH weights (baseline vs ablated) get passed in for a given
    layer, not how a layer is computed."""
    seq = x.shape[0]
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
    return r + ffn


def _clone_layer(lw: TorchLayerWeights) -> TorchLayerWeights:
    return TorchLayerWeights(
        attn_norm_weight=lw.attn_norm_weight.clone(), w_q=lw.w_q.clone(), w_k=lw.w_k.clone(),
        w_v=lw.w_v.clone(), w_o=lw.w_o.clone(), ffn_norm_weight=lw.ffn_norm_weight.clone(),
        w_gate=lw.w_gate.clone(), w_up=lw.w_up.clone(), w_down=lw.w_down.clone(),
        attn_q_bias=lw.attn_q_bias.clone() if lw.attn_q_bias is not None else None,
        attn_k_bias=lw.attn_k_bias.clone() if lw.attn_k_bias is not None else None,
        attn_v_bias=lw.attn_v_bias.clone() if lw.attn_v_bias is not None else None,
    )


def forward_streaming(token_ids: torch.Tensor, model: StreamingGGUFModel,
                       ablation_spec: AblationSpec | None = None) -> torch.Tensor:
    """One full-prefix forward pass, loading each layer from disk just-in-time and
    discarding it before moving to the next. Full float32 compute throughout (same
    precision policy as model_gpu.forward_gpu, same reasons -- see that module's
    docstring for the overflow bugs that made "fp16 storage, fp32 compute" the right
    default rather than an optional extra).

    SPEC-MAJOR: one full model re-read per (spec, prompt) pair. Kept as the simple/reference
    path -- run_sweep_streaming_layer_major() below is the one actually used for real sweeps,
    since it reads each layer from disk exactly once regardless of how many specs there are.
    This function still exists because it's the easiest thing to verify against the CPU
    oracle and against the layer-major path's own output (see run() in this module)."""
    config = model.config
    device = token_ids.device
    x = model.token_embedding[token_ids].float()
    seq = x.shape[0]
    cos, sin, causal_mask, scale, group_size = _rope_causal_scale(config, seq, device)

    for layer_idx in range(config.n_layers):
        lw = model.get_layer(layer_idx)
        if ablation_spec is not None and ablation_spec.layer_idx == layer_idx:
            _zero_component_inplace(lw, ablation_spec, config)
        x = _apply_one_layer(x, lw, config, cos, sin, causal_mask, scale, group_size)
        del lw  # discard this layer's weights before loading the next -- the whole point

    final_normed = _rmsnorm(x, model.final_norm_weight.float(), config.rmsnorm_epsilon)
    logits = final_normed @ model.token_embedding.float().T
    return logits


def run_sweep_streaming_layer_major(gguf_path: str, prompts: list[np.ndarray], *, model_label: str,
                                     device: str = "cuda", components: str = "layers_only",
                                     log_progress: bool = True) -> dict:
    """The real fix for streaming's other problem: forward_streaming()/run_sweep_streaming()
    reload the ENTIRE model from disk for every (spec, prompt) pair -- for N specs that's N
    full re-reads of every layer. Per Infinite_Model_Runtime_Claude_Handoff.md section 21
    ("Redesign the Ablation Sweep Around Weight Reuse" / Phase 8 "layer-major fleet sweeps",
    called out there as "one of the highest-return improvements for the current testing
    workflow"): load each layer from disk EXACTLY ONCE, and advance every (spec, prompt)
    branch's hidden state through it before moving to the next layer.

    Concretely: maintain one hidden-state tensor per (branch, prompt), where branch 0 is the
    unablated baseline and branches 1..N are the N ablation specs. Hidden states are tiny
    ([seq, hidden] float32 -- e.g. 5 tokens x 4096 hidden x 4 bytes = 80KB) compared to a
    layer's weights (hundreds of MB), so keeping dozens of them resident simultaneously costs
    nothing that matters. At layer L, load L's weights ONCE; the one branch whose
    spec.layer_idx == L gets a cloned-and-zeroed copy of L's weights for its own step (cloning
    a single layer, not the whole model), every other branch uses L's real weights unmodified.

    Total layer loads from disk: exactly n_layers, regardless of how many specs are swept --
    versus forward_streaming()'s (n_specs + 1) x n_layers. For a 32-layer model with 32
    layers_only specs, that's 32 loads instead of 1056: expected ~33x fewer disk
    reads/dequantizations for the identical result."""
    t0 = time.time()
    model = StreamingGGUFModel(gguf_path, device=device)
    config = model.config
    specs = _all_specs(config, components=components)
    branches: list[AblationSpec | None] = [None] + list(specs)  # branch 0 = baseline
    if log_progress:
        print(f"  streaming model (layer-major): n_layers={config.n_layers} hidden={config.hidden} "
              f"vocab={config.vocab} -- {len(specs)} specs x {len(prompts)} prompts = "
              f"{len(branches) * len(prompts)} branches, but only {config.n_layers} layer loads total")

    prompts_gpu = [torch.from_numpy(p).to(device) for p in prompts]
    seq_by_prompt = [p.shape[0] for p in prompts_gpu]
    # cos/sin/mask depend only on sequence length, which can differ per prompt -- precompute
    # once per distinct prompt, reused across every layer and every branch for that prompt.
    rope_by_prompt = [_rope_causal_scale(config, seq, device) for seq in seq_by_prompt]

    # hidden[(branch_idx, prompt_idx)] -- initialized to the embedding lookup, advanced one
    # layer at a time below.
    hidden: dict[tuple[int, int], torch.Tensor] = {}
    for b_idx in range(len(branches)):
        for p_idx, prompt_gpu in enumerate(prompts_gpu):
            hidden[(b_idx, p_idx)] = model.token_embedding[prompt_gpu].float()

    for layer_idx in range(config.n_layers):
        lw_baseline = model.get_layer(layer_idx)  # the ONE disk read for this layer
        ablated_branch_idx = next(
            (b_idx for b_idx, spec in enumerate(branches) if spec is not None and spec.layer_idx == layer_idx),
            None,
        )
        lw_ablated = None
        if ablated_branch_idx is not None:
            lw_ablated = _clone_layer(lw_baseline)
            _zero_component_inplace(lw_ablated, branches[ablated_branch_idx], config)

        for b_idx in range(len(branches)):
            lw = lw_ablated if b_idx == ablated_branch_idx else lw_baseline
            for p_idx in range(len(prompts_gpu)):
                cos, sin, causal_mask, scale, group_size = rope_by_prompt[p_idx]
                hidden[(b_idx, p_idx)] = _apply_one_layer(
                    hidden[(b_idx, p_idx)], lw, config, cos, sin, causal_mask, scale, group_size
                )

        del lw_baseline
        if lw_ablated is not None:
            del lw_ablated
        if log_progress and (layer_idx + 1) % max(1, config.n_layers // 10) == 0:
            print(f"  layer {layer_idx + 1}/{config.n_layers} done ({time.time() - t0:.1f}s elapsed)")

    logits_by_branch_prompt: dict[tuple[int, int], np.ndarray] = {}
    final_norm = model.final_norm_weight.float()
    embed_t = model.token_embedding.float().T
    for key, x in hidden.items():
        final_normed = _rmsnorm(x, final_norm, config.rmsnorm_epsilon)
        logits_by_branch_prompt[key] = (final_normed @ embed_t).cpu().numpy()

    baseline_logits_by_prompt = [logits_by_branch_prompt[(0, p_idx)] for p_idx in range(len(prompts))]
    results = []
    for b_idx, spec in enumerate(branches):
        if spec is None:
            continue
        per_prompt = []
        for p_idx, prompt_np in enumerate(prompts):
            per_prompt.append({
                "token_ids": prompt_np.tolist(),
                **_divergence_per_position(baseline_logits_by_prompt[p_idx], logits_by_branch_prompt[(b_idx, p_idx)]),
            })
        aggregated = [_mean_of(pp) for pp in per_prompt]
        mean_across_prompts = {k: float(np.mean([a[k] for a in aggregated])) for k in aggregated[0]}
        results.append({
            "label": spec.label, "layer_idx": spec.layer_idx, "component": spec.component,
            "head_idx": spec.head_idx, **mean_across_prompts, "per_prompt": per_prompt,
        })

    results.sort(key=lambda r: r["logit_l2"], reverse=True)
    return {
        "model_label": model_label, "n_prompts": len(prompts), "components_swept": len(results),
        "elapsed_seconds": time.time() - t0, "results": results, "device": device,
        "streaming": True, "layer_major": True, "layer_loads": config.n_layers,
        "gguf_info": model.info(),
    }


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
