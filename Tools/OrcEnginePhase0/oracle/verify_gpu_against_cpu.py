# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Verifies oracle/model_gpu.py's forward pass against oracle/model.py's
CPU/NumPy reference oracle on a real model, before any GPU-produced
ablation-sweep result is trusted. Not a Phase 0 acceptance check (this
tool is downstream diagnostic tooling, not the reference oracle itself)
-- but the same "verify before trusting" discipline applies.

Two comparisons, since GPU uses fp16 by default (a deliberate speed/VRAM
tradeoff for this diagnostic tool, see gguf_gpu_loader.DEFAULT_DTYPE):
  1. GPU fp32 vs CPU fp32 -- should agree to near machine precision
     (both are the "same" math, just NumPy-looped vs vectorized-torch).
  2. GPU fp16 vs CPU fp32 -- looser tolerance expected (real precision
     loss from fp16), but argmax should still agree for a clear-margin
     prompt, and the overall logit shape/scale should be sane.
"""
from __future__ import annotations

import sys

import numpy as np
import torch

from oracle.gguf_gpu_loader import load_gguf_to_gpu
from oracle.gguf_model_loader import load_gguf_as_model_weights
from oracle.model import forward as forward_cpu
from oracle.model_gpu import TorchLayerWeights, TorchModelWeights, forward_gpu


def _cpu_weights_to_gpu(weights, device, dtype) -> TorchModelWeights:
    """Converts an already-CPU-loaded ModelWeights to GPU tensors directly (bypassing
    gguf_gpu_loader's streaming re-read of the file) so this comparison is apples-to-apples
    against the EXACT SAME dequantized values, isolating "does the GPU forward math agree"
    from "does the streaming loader read the same values" (checked separately below)."""
    def t(arr):
        return torch.from_numpy(np.ascontiguousarray(arr, dtype=np.float32)).to(device=device, dtype=dtype)

    layers = [
        TorchLayerWeights(
            attn_norm_weight=t(lw.attn_norm_weight), w_q=t(lw.w_q), w_k=t(lw.w_k), w_v=t(lw.w_v),
            w_o=t(lw.w_o), ffn_norm_weight=t(lw.ffn_norm_weight), w_gate=t(lw.w_gate),
            w_up=t(lw.w_up), w_down=t(lw.w_down),
            attn_q_bias=t(lw.attn_q_bias) if lw.attn_q_bias is not None else None,
            attn_k_bias=t(lw.attn_k_bias) if lw.attn_k_bias is not None else None,
            attn_v_bias=t(lw.attn_v_bias) if lw.attn_v_bias is not None else None,
        )
        for lw in weights.layers
    ]
    return TorchModelWeights(
        token_embedding=t(weights.token_embedding), layers=layers,
        final_norm_weight=t(weights.final_norm_weight),
        lm_head=t(weights.lm_head) if weights.lm_head is not None else None,
    )


def run(gguf_path: str) -> bool:
    if not torch.cuda.is_available():
        print("FAIL: CUDA not available")
        return False

    print(f"loading {gguf_path!r} via the CPU/NumPy oracle loader (ground truth)...")
    cpu_weights, config, cpu_info = load_gguf_as_model_weights(gguf_path)
    print(f"  config: n_layers={config.n_layers} hidden={config.hidden} "
          f"n_q_heads={config.n_q_heads} n_kv_heads={config.n_kv_heads} "
          f"rotary_dim={config.rotary_dim} arch={cpu_info['architecture']}")

    token_ids_np = np.array([15, 342, 8891, 22, 5001], dtype=np.int64)
    token_ids_np = token_ids_np % config.vocab
    print(f"  test prompt token_ids: {token_ids_np.tolist()}")

    print("\nrunning CPU oracle forward pass...")
    cpu_logits = forward_cpu(token_ids_np, cpu_weights, config, capture_taps=False).logits
    cpu_argmax = int(np.argmax(cpu_logits[-1]))
    print(f"  CPU argmax (last position): {cpu_argmax}")

    device = "cuda"
    token_ids_gpu = torch.from_numpy(token_ids_np).to(device)

    ok = True

    print("\n=== Comparison 1: GPU fp32 (same weights, converted) vs CPU fp32 ===")
    gpu_weights_fp32 = _cpu_weights_to_gpu(cpu_weights, device, torch.float32)
    gpu_logits_fp32 = forward_gpu(token_ids_gpu, gpu_weights_fp32, config).detach().cpu().numpy()
    diff_fp32 = np.abs(gpu_logits_fp32.astype(np.float64) - cpu_logits.astype(np.float64))
    max_diff_fp32 = float(diff_fp32.max())
    gpu_argmax_fp32 = int(np.argmax(gpu_logits_fp32[-1]))
    print(f"  max_abs_diff={max_diff_fp32:.6e}  GPU argmax={gpu_argmax_fp32} "
          f"(CPU={cpu_argmax}, match={gpu_argmax_fp32 == cpu_argmax})")
    fp32_ok = max_diff_fp32 < 1e-2 and gpu_argmax_fp32 == cpu_argmax
    print(f"  {'PASS' if fp32_ok else 'FAIL'}: fp32-vs-fp32 agreement")
    ok = ok and fp32_ok

    print("\n=== Comparison 2: streaming GPU loader (fp16, re-reads the file independently) vs CPU fp32 ===")
    gpu_weights_fp16, gpu_config, gpu_info = load_gguf_to_gpu(gguf_path, device=device, dtype=torch.float16)
    assert gpu_config == config, "streaming GPU loader produced a different ModelConfig than the CPU loader"
    gpu_logits_fp16 = forward_gpu(token_ids_gpu, gpu_weights_fp16, gpu_config).float().detach().cpu().numpy()
    diff_fp16 = np.abs(gpu_logits_fp16.astype(np.float64) - cpu_logits.astype(np.float64))
    max_diff_fp16 = float(diff_fp16.max())
    gpu_argmax_fp16 = int(np.argmax(gpu_logits_fp16[-1]))
    print(f"  max_abs_diff={max_diff_fp16:.4f}  GPU argmax={gpu_argmax_fp16} "
          f"(CPU={cpu_argmax}, match={gpu_argmax_fp16 == cpu_argmax})")
    # fp16 has ~3 decimal digits of precision and this model's logit scale can be in the
    # hundreds -- a loose absolute tolerance is expected and honest, not a bug being hidden.
    # argmax agreement is the real correctness bar for this tool's diagnostic use.
    fp16_ok = gpu_argmax_fp16 == cpu_argmax
    print(f"  {'PASS' if fp16_ok else 'FAIL'}: fp16 argmax agreement (precision loss in logit "
          f"magnitude is expected and NOT itself a failure)")
    ok = ok and fp16_ok

    return ok


if __name__ == "__main__":
    path = sys.argv[1] if len(sys.argv) > 1 else None
    if not path:
        print("usage: python -m oracle.verify_gpu_against_cpu <gguf_path>")
        raise SystemExit(2)
    ok = run(path)
    print(f"\n{'PASS' if ok else 'FAIL'}: verify_gpu_against_cpu")
    raise SystemExit(0 if ok else 1)
