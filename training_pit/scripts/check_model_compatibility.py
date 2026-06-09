#!/usr/bin/env python3
"""
check_model_compatibility.py — Print base model compatibility status.

Usage:
    python training_pit/scripts/check_model_compatibility.py
    python training_pit/scripts/check_model_compatibility.py --model gemma4:12b

Reads: training_pit/configs/base_model_compat.json

Requirements: none (stdlib only)
"""

import json
import sys
from pathlib import Path

# Force UTF-8 output on Windows (cp1252 can't encode box-drawing / emoji chars)
if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

COMPAT_JSON = Path(__file__).parent.parent / "configs" / "base_model_compat.json"

STATE_SYMBOL = {
    "verified":            "✅ verified",
    "confirmed-external":  "🟢 confirmed-external",
    "inferred":            "🔶 inferred",
    "unknown":             "❓ unknown",
    "incompatible":        "❌ incompatible",
}


def load_compat() -> dict:
    if not COMPAT_JSON.exists():
        print(f"ERROR: {COMPAT_JSON} not found")
        sys.exit(1)
    with open(COMPAT_JSON, encoding="utf-8") as f:
        return json.load(f)


def print_model(m: dict):
    print(f"\n  {'─' * 54}")
    print(f"  Model:      {m['id']}")
    print(f"  HF repo:    {m.get('hf_repo', 'n/a')}")
    print(f"  Priority:   {m.get('priority', 'n/a')}")
    print(f"  Ollama:     {'available' if m.get('ollama_available') else 'not available'}")

    vram = m.get("vram", {})
    if vram:
        print(f"  VRAM:       infer={vram.get('infer_q4_gb','?')} GB Q4  |  "
              f"LoRA={vram.get('train_lora_bf16_gb','?')} GB  |  "
              f"QLoRA={vram.get('train_qlora_nf4_gb','?')} GB")

    cs = m.get("compat_state", {})
    if cs:
        lora_state  = STATE_SYMBOL.get(cs.get("unsloth_lora",  "unknown"), "❓")
        qlora_state = STATE_SYMBOL.get(cs.get("unsloth_qlora", "unknown"), "❓")
        print(f"  Unsloth LoRA:  {lora_state}")
        print(f"  Unsloth QLoRA: {qlora_state}")
        if cs.get("_note"):
            print(f"  Note:       {cs['_note']}")

    ft_status = m.get("fine_tune_status", "n/a")
    ft_prio   = m.get("fine_tune_priority", "n/a")
    print(f"  FT status:  {ft_status}  (priority {ft_prio})")

    known_issues = m.get("known_issues", [])
    if known_issues:
        print(f"  Known issues:")
        for issue in known_issues:
            print(f"    • {issue}")

    qat = m.get("qat_variant", {})
    if qat.get("available"):
        installed = "✅ installed on server" if qat.get("installed_on_server") else "not installed"
        print(f"  QAT variant: {qat.get('ollama_hf_tag', 'n/a')} — {installed}")


def main():
    compat = load_compat()
    models = compat.get("models", [])

    filter_id = None
    if len(sys.argv) > 1 and sys.argv[1] == "--model" and len(sys.argv) > 2:
        filter_id = sys.argv[2].lower()

    print("=" * 60)
    print("TheOrc Training Pit — Model Compatibility")
    print("=" * 60)

    for m in models:
        if filter_id and filter_id not in m["id"].lower():
            continue
        print_model(m)

    fw = compat.get("framework_notes", {})
    if fw:
        print(f"\n  {'─' * 54}")
        print("  Training Framework Notes:")
        for name, info in fw.items():
            rec = "✅ recommended" if info.get("recommended") else "not recommended"
            print(f"  {name}: {rec} — {info.get('reason', '')}")

    print(f"\n{'=' * 60}")
    print("Legend: ✅ verified  🟢 confirmed-external  🔶 inferred  ❓ unknown  ❌ incompatible")
    print("=" * 60)


if __name__ == "__main__":
    main()
