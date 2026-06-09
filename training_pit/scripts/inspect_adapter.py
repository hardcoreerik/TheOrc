#!/usr/bin/env python3
"""
inspect_adapter.py — Inspect a LoRA/QLoRA adapter and print its base model info.

Usage:
    python training_pit/scripts/inspect_adapter.py adapters/local/theorc-boss-lora-v1/

Reads: <adapter_dir>/adapter_config.json

Requirements: none (stdlib only)
"""

import json
import sys
from pathlib import Path

REGISTRY_JSON = Path(__file__).parent.parent / "adapters" / "registry.json"


def inspect_adapter(adapter_dir: str):
    dir_path = Path(adapter_dir)

    if not dir_path.exists():
        print(f"ERROR: Directory not found: {adapter_dir}")
        sys.exit(1)

    config_path = dir_path / "adapter_config.json"
    if not config_path.exists():
        print(f"ERROR: adapter_config.json not found in {adapter_dir}")
        print("This does not appear to be a valid adapter directory.")
        sys.exit(1)

    with open(config_path, encoding="utf-8") as f:
        config = json.load(f)

    print("=" * 60)
    print(f"Adapter: {dir_path.name}")
    print("=" * 60)

    # Core identity
    print(f"\n  Adapter type:       {config.get('peft_type', 'unknown')}")
    print(f"  Task type:          {config.get('task_type', 'unknown')}")
    print(f"  Base model:         {config.get('base_model_name_or_path', 'unknown')}")

    # LoRA parameters
    print(f"\n  LoRA rank (r):      {config.get('r', 'unknown')}")
    print(f"  LoRA alpha:         {config.get('lora_alpha', 'unknown')}")
    print(f"  LoRA dropout:       {config.get('lora_dropout', 'unknown')}")

    target_modules = config.get("target_modules", [])
    if target_modules:
        print(f"  Target modules:     {', '.join(target_modules)}")

    # Quantization
    quant = config.get("quantization_config", {})
    if quant:
        load_in_4bit = quant.get("load_in_4bit", False)
        quant_type = quant.get("bnb_4bit_quant_type", "unknown")
        print(f"\n  Quantization:       {'4-bit ' + quant_type if load_in_4bit else 'none (full precision base)'}")
    else:
        print(f"\n  Quantization:       none (full precision base)")

    # Check registry
    if REGISTRY_JSON.exists():
        with open(REGISTRY_JSON, encoding="utf-8") as f:
            registry = json.load(f)

        adapters = registry.get("adapters", [])
        matching = [a for a in adapters if dir_path.name in a.get("adapter_dir", "")]
        if matching:
            reg_entry = matching[0]
            print(f"\n  Registry status:    {reg_entry.get('eval_status', 'not registered')}")
            print(f"  Registry name:      {reg_entry.get('name', 'n/a')}")
        else:
            print(f"\n  Registry status:    ⚠️  NOT FOUND in adapters/registry.json")
            print("  This adapter has not been registered. Do not use in production.")

    # Safety check
    base = config.get("base_model_name_or_path", "")
    if "gemma" in base.lower():
        print(f"\n  ⚠️  Gemma base detected. Requires RENDERER gemma4 / PARSER gemma4")
        print("  in the Ollama Modelfile after merging.")
    if "qwen" in base.lower():
        print(f"\n  ℹ️  Qwen base detected. Use Unsloth qwen_2_5 tokenizer template.")

    print("\n" + "=" * 60)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python inspect_adapter.py <adapter_directory>")
        print("Example: python inspect_adapter.py adapters/local/theorc-boss-lora-v1/")
        sys.exit(1)

    inspect_adapter(sys.argv[1])
