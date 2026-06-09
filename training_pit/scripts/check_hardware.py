#!/usr/bin/env python3
"""
check_hardware.py — Detect GPU/RAM and print training tier estimate.

Usage:
    python training_pit/scripts/check_hardware.py

Detects:
    - GPU model and VRAM (via torch.cuda or subprocess nvidia-smi)
    - System RAM
    - CUDA version
    - Recommended training tier (LoRA / QLoRA / inference-only)

Requirements:
    pip install psutil
    (torch is optional — falls back to nvidia-smi if not installed)
"""

import subprocess
import sys

try:
    import psutil
    HAS_PSUTIL = True
except ImportError:
    HAS_PSUTIL = False

try:
    import torch
    HAS_TORCH = True
except ImportError:
    HAS_TORCH = False


def get_ram_gb() -> float:
    if HAS_PSUTIL:
        return psutil.virtual_memory().total / (1024 ** 3)
    return 0.0


def get_gpu_info() -> dict:
    """Returns dict with name, vram_gb, cuda_version. Falls back to nvidia-smi."""
    if HAS_TORCH and torch.cuda.is_available():
        props = torch.cuda.get_device_properties(0)
        cuda_ver = torch.version.cuda or "unknown"
        return {
            "name": props.name,
            "vram_gb": round(props.total_memory / (1024 ** 3), 1),
            "cuda_version": cuda_ver,
            "device_count": torch.cuda.device_count(),
            "source": "torch.cuda",
        }

    # Fallback: parse nvidia-smi
    try:
        smi = subprocess.check_output(
            ["nvidia-smi", "--query-gpu=name,memory.total,driver_version",
             "--format=csv,noheader,nounits"],
            text=True, stderr=subprocess.DEVNULL
        ).strip().split("\n")[0]
        parts = [p.strip() for p in smi.split(",")]
        if len(parts) >= 2:
            name = parts[0]
            vram_mb = float(parts[1])
            return {
                "name": name,
                "vram_gb": round(vram_mb / 1024, 1),
                "cuda_version": "see nvidia-smi",
                "device_count": 1,
                "source": "nvidia-smi",
            }
    except (subprocess.CalledProcessError, FileNotFoundError, ValueError):
        pass

    return {"name": "NOT DETECTED", "vram_gb": 0.0, "cuda_version": "unknown",
            "device_count": 0, "source": "none"}


def tier_recommendation(vram_gb: float, ram_gb: float) -> str:
    if vram_gb == 0:
        return "NO GPU — inference only (CPU-only Ollama; no local training possible)"
    if vram_gb < 8:
        return "INFERENCE ONLY — VRAM < 8 GB (can run Q4 models up to 7B via Ollama)"
    if vram_gb < 12:
        return "INFERENCE ONLY — VRAM 8-11 GB (can run gemma4:12b Q4 via Ollama, but no local fine-tuning)"
    if vram_gb < 16:
        tier = "QLORA CAPABLE — 12-15 GB VRAM\n"
        tier += "  ✅ QLoRA on gemma4:12b (NF4, ~12 GB)\n"
        tier += "  ❌ LoRA on gemma4:12b (requires 16+ GB)\n"
        tier += "  Config: use qlora_job_template.json with batch_size=1, gradient_accumulation=8"
        if ram_gb < 32:
            tier += "\n  ⚠️  System RAM < 32 GB — paged optimizer may be slow"
        return tier
    if vram_gb < 24:
        tier = "QLORA + LORA (7B) CAPABLE — 16-23 GB VRAM\n"
        tier += "  ✅ QLoRA on gemma4:12b (NF4, ~12 GB) — RECOMMENDED for this hardware\n"
        tier += "  ✅ LoRA on 7B models (bf16, ~10-12 GB)\n"
        tier += "  ❌ LoRA on gemma4:12b (bf16, ~18 GB — does NOT fit at 16 GB)\n"
        tier += "  Config: use qlora_job_template.json"
        return tier
    return (
        "LORA CAPABLE — 24+ GB VRAM\n"
        "  ✅ LoRA on gemma4:12b (bf16, ~18 GB)\n"
        "  ✅ QLoRA on qwen2.5-coder:32b (NF4, ~28 GB — may need gradient_checkpointing)\n"
        "  Config: use lora_job_template.json"
    )


def main():
    print("=" * 60)
    print("TheOrc Training Pit — Hardware Check")
    print("=" * 60)

    gpu = get_gpu_info()
    ram_gb = get_ram_gb()

    print(f"\n  GPU:          {gpu['name']}")
    print(f"  VRAM:         {gpu['vram_gb']} GB")
    print(f"  CUDA version: {gpu['cuda_version']}")
    print(f"  GPU count:    {gpu['device_count']}")
    print(f"  System RAM:   {ram_gb:.1f} GB" if ram_gb > 0 else "  System RAM:   unknown (install psutil)")
    print(f"  PyTorch:      {'installed' if HAS_TORCH else 'not installed'}")
    print(f"  psutil:       {'installed' if HAS_PSUTIL else 'not installed'}")

    print("\n" + "-" * 60)
    print("Training Tier:")
    print()
    print("  " + tier_recommendation(gpu["vram_gb"], ram_gb).replace("\n", "\n  "))
    print()

    if not HAS_TORCH:
        print("Note: Install torch for more accurate VRAM detection:")
        print("  pip install torch --index-url https://download.pytorch.org/whl/cu124")

    print("=" * 60)


if __name__ == "__main__":
    main()
