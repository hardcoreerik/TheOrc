# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
GPU-accelerated fleet ablation sweep. Same model-discovery logic as
ablation_sweep_fleet.py (models_dir, extra dirs, Ollama store,
content-based de-dup) but loads via oracle/gguf_gpu_loader.py (streaming
straight to VRAM in fp16, no full-model CPU RAM materialization) and
runs oracle/ablation_sweep_gpu.py's forward passes on CUDA.

Two real consequences of moving to GPU, both verified empirically (see
oracle/verify_gpu_against_cpu.py) rather than assumed:
  - Speed: seconds-to-low-minutes per model instead of the CPU path's
    tens of minutes for large-vocab models.
  - Capacity: the binding constraint becomes VRAM (16GB on this fleet's
    5070 Ti) instead of system RAM, and fp16 weights roughly halve the
    footprint again vs the CPU loader's mandatory float32 -- but VRAM is
    also smaller than system RAM in absolute terms, so this is a
    DIFFERENT ceiling, not strictly a higher one. Still architecture-gated
    the same as the CPU path (llama/qwen2/phi3 only).
Uses components="full" (every layer/head/FFN-submatrix) by default,
unlike the CPU fleet runner's "layers_only" -- GPU speed makes the full
sweep affordable at fleet scale, which the CPU version explicitly wasn't.
"""
from __future__ import annotations

import os
import time
import traceback

import torch
import yaml

from oracle.ablation_sweep import _all_specs, _demo_per_position_query, _print_summary, _write_report
from oracle.ablation_sweep_fleet import (
    EXTRA_MODELS_DIRS,
    MODELS_DIR,
    OLLAMA_MODELS_DIR,
    _discover_all_models,
    _prompts_for_vocab,
)
from oracle.ablation_sweep_gpu import run_sweep_gpu
from oracle.gguf_gpu_loader import DEFAULT_DTYPE, load_gguf_to_gpu
from oracle.gguf_model_loader import SUPPORTED_ARCHITECTURES

FLEET_REPORT_DIR = os.path.join(os.path.dirname(__file__), "..", "artifacts", "ablation_fleet_gpu")

# Fraction of currently-FREE VRAM (not total -- other processes may already be using some,
# confirmed 3.5GB in use by something else on this machine at the time this was written)
# a model's estimated fp16 weight size may consume before it's skipped.
MAX_VRAM_FRACTION = 0.7


def _estimate_fp16_bytes(path: str) -> tuple[int, str | None]:
    from gguf import GGUFReader
    import numpy as np
    reader = GGUFReader(path)
    arch_field = reader.fields.get("general.architecture")
    arch = bytes(arch_field.parts[arch_field.data[0]]).decode("utf-8") if arch_field else None
    total_elements = sum(int(np.prod(t.shape)) for t in reader.tensors)
    return total_elements * 2, arch  # fp16 = 2 bytes/element


def run(components: str = "full") -> bool:
    if not torch.cuda.is_available():
        print("FAIL: CUDA not available")
        return False
    device_name = torch.cuda.get_device_name(0)
    free_bytes, total_bytes = torch.cuda.mem_get_info()
    vram_budget = free_bytes * MAX_VRAM_FRACTION
    print(f"GPU: {device_name}  total={total_bytes / 1e9:.1f}GB  free={free_bytes / 1e9:.1f}GB  "
          f"budget={vram_budget / 1e9:.1f}GB ({MAX_VRAM_FRACTION:.0%} of free)\n")

    discovered = _discover_all_models()
    if not discovered:
        print("FAIL: no models found across models_dir, extra_dirs, or the Ollama store")
        return False
    print(f"found {len(discovered)} model(s) after de-duplication\n")

    fleet_summary = []
    for model in discovered:
        path, name = model.path, model.display_name
        try:
            est_bytes, arch = _estimate_fp16_bytes(path)
        except Exception as e:
            print(f"[{name}] SKIP: could not read GGUF metadata ({e})")
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"unreadable: {e}"})
            continue

        if arch not in SUPPORTED_ARCHITECTURES:
            print(f"[{name}] SKIP: architecture={arch!r} not in {SUPPORTED_ARCHITECTURES}")
            fleet_summary.append({"file": name, "status": "skipped",
                                   "reason": f"unsupported architecture: {arch}"})
            continue

        if est_bytes > vram_budget:
            print(f"[{name}] SKIP: architecture={arch!r} but estimated fp16 size "
                  f"{est_bytes / 1e9:.1f}GB exceeds the {vram_budget / 1e9:.1f}GB VRAM budget")
            fleet_summary.append({"file": name, "status": "skipped",
                                   "reason": f"too large for VRAM: {est_bytes / 1e9:.1f}GB "
                                             f"estimated > {vram_budget / 1e9:.1f}GB budget"})
            continue

        print(f"[{name}] loading to GPU (arch={arch}, ~{est_bytes / 1e9:.1f}GB fp16 estimated)...")
        t0 = time.time()
        try:
            weights, config, info = load_gguf_to_gpu(path, device="cuda", dtype=DEFAULT_DTYPE)
        except Exception as e:
            print(f"[{name}] SKIP: load failed: {e}")
            traceback.print_exc()
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"load failed: {e}"})
            torch.cuda.empty_cache()
            continue
        print(f"  loaded in {time.time() - t0:.1f}s: n_layers={config.n_layers} "
              f"hidden={config.hidden} n_q_heads={config.n_q_heads} n_kv_heads={config.n_kv_heads} "
              f"vocab={config.vocab} quant_types={info['quant_types']} "
              f"tied_embeddings={info['tied_embeddings']} has_qkv_bias={info['has_qkv_bias']}")

        prompts = _prompts_for_vocab(config.vocab)
        specs = _all_specs(config, components=components)
        print(f"  sweeping {len(specs)} components x {len(prompts)} prompts on GPU...")
        try:
            report = run_sweep_gpu(weights, config, prompts, specs, model_label=name)
        except Exception as e:
            print(f"[{name}] SKIP: sweep failed: {e}")
            traceback.print_exc()
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"sweep failed: {e}"})
            del weights
            torch.cuda.empty_cache()
            continue

        report["gguf_info"] = info
        report["source"] = model.source
        _print_summary(report)
        _demo_per_position_query(report)
        base = name[:-5] if name.lower().endswith(".gguf") else name
        safe_name = base.replace("/", "__").replace("\\", "__").replace(":", "_")
        out_path = os.path.join(FLEET_REPORT_DIR, f"{safe_name}.yaml")
        _write_report(report, out_path)

        most_impactful = report["results"][0]
        least_impactful = report["results"][-1]
        fleet_summary.append({
            "file": name, "source": model.source, "status": "completed", "architecture": arch,
            "n_layers": config.n_layers, "hidden": config.hidden,
            "elapsed_seconds": report["elapsed_seconds"], "components_swept": len(specs),
            "most_impactful_layer": most_impactful["label"],
            "most_impactful_logit_l2": most_impactful["logit_l2"],
            "least_impactful_layer": least_impactful["label"],
            "least_impactful_logit_l2": least_impactful["logit_l2"],
        })
        del weights, report
        torch.cuda.empty_cache()

    os.makedirs(FLEET_REPORT_DIR, exist_ok=True)
    summary_path = os.path.join(FLEET_REPORT_DIR, "_fleet_summary.yaml")
    with open(summary_path, "w", encoding="utf-8") as f:
        yaml.safe_dump({
            "device": device_name, "models_dir": MODELS_DIR, "extra_models_dirs": EXTRA_MODELS_DIRS,
            "ollama_models_dir": OLLAMA_MODELS_DIR, "components": components, "results": fleet_summary,
        }, f, sort_keys=False, default_flow_style=False)

    print(f"\n{'=' * 60}\nGPU fleet sweep summary\n{'=' * 60}")
    for r in fleet_summary:
        if r["status"] == "completed":
            print(f"  {r['file']}: {r['components_swept']} components in {r['elapsed_seconds']:.1f}s, "
                  f"most-impactful={r['most_impactful_layer']} ({r['most_impactful_logit_l2']:.2f}), "
                  f"least-impactful={r['least_impactful_layer']} ({r['least_impactful_logit_l2']:.2f})")
        else:
            print(f"  {r['file']}: SKIPPED -- {r['reason']}")
    print(f"\nwrote {os.path.abspath(summary_path)}")

    completed = sum(1 for r in fleet_summary if r["status"] == "completed")
    return completed > 0


if __name__ == "__main__":
    import sys
    components = "layers_only" if "--layers-only" in sys.argv else "full"
    ok = run(components=components)
    print(f"\n{'PASS' if ok else 'FAIL'}: ablation_sweep_fleet_gpu")
    raise SystemExit(0 if ok else 1)
