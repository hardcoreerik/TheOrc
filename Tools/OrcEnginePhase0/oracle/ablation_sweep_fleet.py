# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Runs ablation_sweep's full-layer-impact sweep against every real, on-disk
GGUF model TheOrc already has in its model store (%APPDATA%/OrchestratorIDE/
Models by default), not just the pinned SmolLM2-135M Phase 0 candidate.

This is deliberately honest about two hard limits rather than silently
skipping or silently approximating past them:

  1. Architecture support: oracle/model.py only implements "llama" and
     "qwen2" block semantics (see oracle/gguf_model_loader.py's module
     docstring for why "qwen35" -- the Qwen3.8 GGUF family -- is out of
     scope for now). Unsupported-architecture files are skipped with a
     clear reason, not silently misinterpreted as llama.
  2. Memory: this whole suite dequantizes weights to full float32 in
     RAM (no on-the-fly/streamed dequantization). A model's real
     parameter count x 4 bytes must fit comfortably in available system
     memory or the run is skipped with the estimated requirement printed
     -- per this project's "no silent caps" discipline, every skip is
     logged with its reason, never just absent from the output.
"""
from __future__ import annotations

import os
import time
import traceback

import numpy as np
import psutil
import yaml
from gguf import GGUFReader

from oracle.ablation_sweep import _all_specs, _demo_per_position_query, _print_summary, _write_report, run_sweep
from oracle.gguf_model_loader import SUPPORTED_ARCHITECTURES, load_gguf_as_model_weights

MODELS_DIR = os.environ.get(
    "ORC_MODELS_DIR",
    os.path.join(os.environ.get("APPDATA", ""), "OrchestratorIDE", "Models"),
)
FLEET_REPORT_DIR = os.path.join(os.path.dirname(__file__), "..", "artifacts", "ablation_fleet")

# Fraction of TOTAL physical RAM a model's dequantized-to-float32 weight size may consume
# before we refuse to load it. Conservative -- forward-pass intermediates, the Python
# process itself, and the OS all need headroom too; this is not "would technically fit."
MAX_RAM_FRACTION = 0.5

# Deterministic, tokenizer-free prompts: plain token-ID sequences avoiding IDs 0-9 (where
# BOS/EOS/PAD/UNK/control tokens conventionally live across most tokenizer vocabularies).
# Ablation impact is a property of the WEIGHTS' internal computation, not of prompt
# semantics, so real natural-language text isn't required for this measurement -- and
# skipping per-model tokenizer loading keeps this runnable against any GGUF uniformly.
def _prompts_for_vocab(vocab: int) -> list[np.ndarray]:
    rng = np.random.default_rng(20260814)
    low = 100 if vocab > 200 else 10
    high = vocab - 1
    return [
        rng.integers(low, high, size=n, endpoint=True).astype(np.int64)
        for n in (4, 5, 3)
    ]


def _discover_gguf_files(models_dir: str) -> list[str]:
    if not os.path.isdir(models_dir):
        return []
    paths = []
    for root, _dirs, files in os.walk(models_dir):
        for name in files:
            if name.lower().endswith(".gguf"):
                paths.append(os.path.join(root, name))
    return sorted(paths)


def _estimate_param_bytes(path: str) -> tuple[int, str | None]:
    """Returns (estimated_float32_bytes, architecture_or_None). Reads only metadata/tensor
    shapes (GGUFReader is a memory-mapped, lazy reader -- this does NOT dequantize)."""
    reader = GGUFReader(path)
    arch_field = reader.fields.get("general.architecture")
    arch = bytes(arch_field.parts[arch_field.data[0]]).decode("utf-8") if arch_field else None
    total_elements = sum(int(np.prod(t.shape)) for t in reader.tensors)
    return total_elements * 4, arch  # float32 = 4 bytes/element


def run() -> bool:
    models_dir = MODELS_DIR
    print(f"scanning {models_dir} for *.gguf ...")
    paths = _discover_gguf_files(models_dir)
    if not paths:
        print(f"FAIL: no .gguf files found under {models_dir!r}")
        return False

    total_ram = psutil.virtual_memory().total
    ram_budget = total_ram * MAX_RAM_FRACTION
    print(f"found {len(paths)} GGUF file(s); system RAM={total_ram / 1e9:.1f}GB, "
          f"per-model budget={ram_budget / 1e9:.1f}GB ({MAX_RAM_FRACTION:.0%} of total)\n")

    fleet_summary = []
    for path in paths:
        name = os.path.basename(path)
        try:
            est_bytes, arch = _estimate_param_bytes(path)
        except Exception as e:
            print(f"[{name}] SKIP: could not read GGUF metadata ({e})")
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"unreadable: {e}"})
            continue

        if arch not in SUPPORTED_ARCHITECTURES:
            print(f"[{name}] SKIP: architecture={arch!r} not in {SUPPORTED_ARCHITECTURES} "
                  f"(oracle/model.py doesn't implement this block structure yet)")
            fleet_summary.append({"file": name, "status": "skipped",
                                   "reason": f"unsupported architecture: {arch}"})
            continue

        if est_bytes > ram_budget:
            print(f"[{name}] SKIP: architecture={arch!r} but estimated float32 size "
                  f"{est_bytes / 1e9:.1f}GB exceeds the {ram_budget / 1e9:.1f}GB budget "
                  f"(this suite dequantizes fully into RAM, no streaming)")
            fleet_summary.append({"file": name, "status": "skipped",
                                   "reason": f"too large: {est_bytes / 1e9:.1f}GB estimated "
                                             f"> {ram_budget / 1e9:.1f}GB budget"})
            continue

        print(f"[{name}] loading (arch={arch}, ~{est_bytes / 1e9:.1f}GB estimated)...")
        t0 = time.time()
        try:
            weights, config, info = load_gguf_as_model_weights(path)
        except Exception as e:
            print(f"[{name}] SKIP: load failed: {e}")
            traceback.print_exc()
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"load failed: {e}"})
            continue
        print(f"  loaded in {time.time() - t0:.1f}s: n_layers={config.n_layers} "
              f"hidden={config.hidden} n_q_heads={config.n_q_heads} n_kv_heads={config.n_kv_heads} "
              f"vocab={config.vocab} quant_types={info['quant_types']} "
              f"tied_embeddings={info['tied_embeddings']} has_qkv_bias={info['has_qkv_bias']}")

        prompts = _prompts_for_vocab(config.vocab)
        specs = _all_specs(config, components="layers_only")
        print(f"  sweeping {len(specs)} layers x {len(prompts)} prompts...")
        try:
            report = run_sweep(weights, config, prompts, specs, model_label=name, log_progress=False)
        except Exception as e:
            print(f"[{name}] SKIP: sweep failed: {e}")
            traceback.print_exc()
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"sweep failed: {e}"})
            continue

        report["gguf_info"] = info
        _print_summary(report)
        _demo_per_position_query(report)
        out_path = os.path.join(FLEET_REPORT_DIR, f"{os.path.splitext(name)[0]}.yaml")
        _write_report(report, out_path)

        most_impactful = report["results"][0]
        least_impactful = report["results"][-1]
        fleet_summary.append({
            "file": name, "status": "completed", "architecture": arch,
            "n_layers": config.n_layers, "hidden": config.hidden,
            "elapsed_seconds": report["elapsed_seconds"],
            "most_impactful_layer": most_impactful["label"],
            "most_impactful_logit_l2": most_impactful["logit_l2"],
            "least_impactful_layer": least_impactful["label"],
            "least_impactful_logit_l2": least_impactful["logit_l2"],
        })
        # Free the dequantized weights before the next model -- these are large arrays
        # and there's no reason to hold more than one model's worth in memory at a time.
        del weights, report

    os.makedirs(FLEET_REPORT_DIR, exist_ok=True)
    summary_path = os.path.join(FLEET_REPORT_DIR, "_fleet_summary.yaml")
    with open(summary_path, "w", encoding="utf-8") as f:
        yaml.safe_dump({"models_dir": models_dir, "results": fleet_summary}, f,
                        sort_keys=False, default_flow_style=False)

    print(f"\n{'=' * 60}\nfleet sweep summary\n{'=' * 60}")
    for r in fleet_summary:
        if r["status"] == "completed":
            print(f"  {r['file']}: most-impactful={r['most_impactful_layer']} "
                  f"({r['most_impactful_logit_l2']:.2f}), least-impactful="
                  f"{r['least_impactful_layer']} ({r['least_impactful_logit_l2']:.2f})")
        else:
            print(f"  {r['file']}: SKIPPED -- {r['reason']}")
    print(f"\nwrote {os.path.abspath(summary_path)}")

    completed = sum(1 for r in fleet_summary if r["status"] == "completed")
    return completed > 0


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: ablation_sweep_fleet ({'at least one model completed' if ok else 'no models completed'})")
    raise SystemExit(0 if ok else 1)
