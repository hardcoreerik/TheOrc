# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
GPU-accelerated ablation sweep -- same AblationSpec/metric definitions as
oracle/ablation_sweep.py (component labels, logit_l2/kl_divergence/
argmax_flip_rate, per-position breakdown), but running forward passes on
CUDA via oracle/model_gpu.py instead of oracle/model.py's CPU/NumPy loop.

Ablation is done by IN-PLACE zero-then-restore on the already-uploaded
GPU tensors (see `ablated()` below) rather than building a fresh
ModelWeights copy per component the way the CPU version does
(dataclasses.replace + np.zeros_like) -- copying a 10GB+ model's weights
for every one of ~400 components would be its own VRAM/bandwidth cost.
Zeroing and restoring a slice in place means only ONE model copy ever
sits in VRAM regardless of how many components get swept.
"""
from __future__ import annotations

from contextlib import contextmanager

import numpy as np
import torch

from oracle.ablation_sweep import AblationSpec, _all_specs, _divergence_per_position, _mean_of
from oracle.model import ModelConfig
from oracle.model_gpu import TorchModelWeights, forward_gpu


@contextmanager
def ablated(weights: TorchModelWeights, spec: AblationSpec, config: ModelConfig):
    """Zeroes one component in place for the duration of the `with` block, then restores
    the original values exactly (via a cloned backup) on exit -- including on exception,
    so a failed sweep component can never leave the model silently corrupted for the rest
    of the run."""
    lw = weights.layers[spec.layer_idx]
    if spec.component == "full_layer":
        names = ["w_q", "w_k", "w_v", "w_o", "w_gate", "w_up", "w_down"]
        saved = {n: getattr(lw, n).clone() for n in names}
        for n in names:
            getattr(lw, n).zero_()
        try:
            yield
        finally:
            for n, val in saved.items():
                getattr(lw, n).copy_(val)
    elif spec.component == "attn_head":
        assert spec.head_idx is not None
        start, end = spec.head_idx * config.head_dim, (spec.head_idx + 1) * config.head_dim
        saved = lw.w_o[:, start:end].clone()
        lw.w_o[:, start:end] = 0.0
        try:
            yield
        finally:
            lw.w_o[:, start:end].copy_(saved)
    elif spec.component in ("ffn_gate", "ffn_up", "ffn_down"):
        attr = {"ffn_gate": "w_gate", "ffn_up": "w_up", "ffn_down": "w_down"}[spec.component]
        t = getattr(lw, attr)
        saved = t.clone()
        t.zero_()
        try:
            yield
        finally:
            t.copy_(saved)
    else:
        raise ValueError(f"unknown ablation component: {spec.component!r}")


def run_sweep_gpu(weights: TorchModelWeights, config: ModelConfig, prompts: list[np.ndarray],
                   specs: list[AblationSpec], *, model_label: str, device: str = "cuda") -> dict:
    import time
    t0 = time.time()

    prompts_gpu = [torch.from_numpy(p).to(device) for p in prompts]
    baseline_logits_by_prompt = []
    with torch.no_grad():
        for p in prompts_gpu:
            logits = forward_gpu(p, weights, config).float().cpu().numpy()
            baseline_logits_by_prompt.append(logits)

    results = []
    with torch.no_grad():
        for spec in specs:
            with ablated(weights, spec, config):
                per_prompt = []
                for prompt_np, prompt_gpu, baseline_logits in zip(
                    prompts, prompts_gpu, baseline_logits_by_prompt, strict=True
                ):
                    ablated_logits = forward_gpu(prompt_gpu, weights, config).float().cpu().numpy()
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

    results.sort(key=lambda r: r["logit_l2"], reverse=True)
    return {
        "model_label": model_label, "n_prompts": len(prompts), "components_swept": len(results),
        "elapsed_seconds": time.time() - t0, "results": results, "device": device,
    }
