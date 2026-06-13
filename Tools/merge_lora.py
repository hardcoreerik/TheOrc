#!/usr/bin/env python3
"""
Merge a PEFT/LoRA adapter into a HuggingFace base model and save the result.

Usage:
    python tools/merge_lora.py \
        --base google/gemma-4-12b-it \
        --adapter training_pit/outputs/lora_v1/adapter \
        --out training_pit/outputs/lora_v1/merged

Requirements:
    pip install transformers peft torch accelerate

Memory:
    Loads the base model in the chosen dtype on CPU by default.
    float16 / bfloat16 ~25 GB RAM.  Add --device auto to use GPU offload if available.
"""

import argparse
import sys
from pathlib import Path


def parse_args():
    p = argparse.ArgumentParser(description="Merge a LoRA adapter into a base HF model")
    p.add_argument("--base",    required=True, help="Base model HF id or local path")
    p.add_argument("--adapter", required=True, help="LoRA adapter directory")
    p.add_argument("--out",     required=True, help="Output directory for merged model")
    p.add_argument("--dtype",   default="float16",
                   choices=["float16", "bfloat16", "float32"],
                   help="Torch dtype for loading (default: float16)")
    p.add_argument("--device",  default="cpu",
                   help="device_map for from_pretrained (default: cpu; use 'auto' with GPU)")
    return p.parse_args()


def main():
    args = parse_args()

    adapter_path = Path(args.adapter)
    out_path     = Path(args.out)

    if not adapter_path.exists():
        print(f"ERROR: adapter path not found: {adapter_path}", file=sys.stderr)
        sys.exit(1)

    try:
        import torch
        from transformers import AutoModelForCausalLM, AutoTokenizer
        from peft import PeftModel
    except ImportError as e:
        print(f"ERROR: missing dependency — {e}", file=sys.stderr)
        print("Fix:  pip install transformers peft torch accelerate", file=sys.stderr)
        sys.exit(1)

    dtype_map = {
        "float16":  torch.float16,
        "bfloat16": torch.bfloat16,
        "float32":  torch.float32,
    }
    dtype = dtype_map[args.dtype]

    print(f"[1/4] Loading base model  : {args.base}  (dtype={args.dtype}, device={args.device})")
    model = AutoModelForCausalLM.from_pretrained(
        args.base,
        torch_dtype=dtype,
        device_map=args.device,
        low_cpu_mem_usage=True,
    )
    tokenizer = AutoTokenizer.from_pretrained(args.base)

    print(f"[2/4] Loading adapter     : {adapter_path}")
    # offload_folder lets PEFT page layers to disk when VRAM+RAM can't hold
    # base model + adapter simultaneously (common with device_map="auto").
    offload_dir = out_path.parent / "peft_offload_tmp"
    offload_dir.mkdir(parents=True, exist_ok=True)
    model = PeftModel.from_pretrained(model, str(adapter_path), offload_folder=str(offload_dir))

    print("[3/4] Merging LoRA weights into base model ...")
    model = model.merge_and_unload()

    out_path.mkdir(parents=True, exist_ok=True)
    print(f"[4/4] Saving merged model : {out_path}")
    model.save_pretrained(str(out_path))
    tokenizer.save_pretrained(str(out_path))

    print()
    print("Merge complete. Next step:")
    print(f"  python llama.cpp/convert_hf_to_gguf.py {out_path} \\")
    print(f"    --outfile theorc-boss-gemma4-ft.Q4_0.gguf --outtype q4_0")


if __name__ == "__main__":
    main()
