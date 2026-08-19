# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Four-way real-model differential for Phase 5A:

    HF full-prefix (use_cache=False)
    HF native cached decode (use_cache=True, past_key_values)
    OrcEngine frozen Phase-1 full-prefix (from gguf_cached_forward's "full_steps")
    OrcEngine Phase-5A cached decode (from gguf_cached_forward's "cached_steps")

HF's own cached path independently constructs and consumes its own KV state --
it is NOT derived from or fed by OrcEngine's cache in any way, satisfying the
"do not reuse an OrcEngine-generated cache as the HF reference" requirement.
"""
from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
import torch
from transformers import AutoModelForCausalLM

ABS_TOL = 1e-3
REL_TOL = 1e-3


def close(actual: np.ndarray, expected: np.ndarray) -> tuple[bool, float, float]:
    absolute = np.abs(actual - expected)
    relative = absolute / np.maximum(1.0, np.abs(expected))
    ok = bool(np.isfinite(actual).all() and np.all((absolute <= ABS_TOL) | (relative <= REL_TOL)))
    return ok, float(absolute.max(initial=0.0)), float(relative.max(initial=0.0))


def main(executable: str, gguf_path: str, source_dir: str, steps: int) -> None:
    initial = [1, 5]
    cpp = json.loads(subprocess.check_output(
        [executable, gguf_path, str(steps), *map(str, initial)], text=True))
    if len(cpp["full_steps"]) != steps or len(cpp["cached_steps"]) != steps:
        raise AssertionError("C++ step count mismatch")

    model = AutoModelForCausalLM.from_pretrained(
        Path(source_dir), dtype=torch.float32, local_files_only=True, attn_implementation="eager")
    model.eval()

    results = []
    with torch.inference_mode():
        # --- HF full-prefix (use_cache=False), recompute whole sequence each step ---
        tokens = list(initial)
        hf_full_logits = []
        hf_full_selected = []
        t0 = time.perf_counter()
        for _ in range(steps):
            out = model(input_ids=torch.tensor([tokens], dtype=torch.long), use_cache=False)
            last = out.logits[0, -1].to(dtype=torch.float32).cpu().numpy()
            hf_full_logits.append(last)
            sel = int(np.argmax(last))
            hf_full_selected.append(sel)
            tokens.append(sel)
        hf_full_seconds = time.perf_counter() - t0

        # --- HF native cached decode (use_cache=True), independently constructed ---
        hf_cached_logits = []
        hf_cached_selected = []
        t0 = time.perf_counter()
        out = model(input_ids=torch.tensor([initial], dtype=torch.long), use_cache=True)
        past = out.past_key_values
        last = out.logits[0, -1].to(dtype=torch.float32).cpu().numpy()
        hf_cached_logits.append(last)
        sel = int(np.argmax(last))
        hf_cached_selected.append(sel)
        for _ in range(steps - 1):
            out = model(input_ids=torch.tensor([[sel]], dtype=torch.long),
                        past_key_values=past, use_cache=True)
            past = out.past_key_values
            last = out.logits[0, -1].to(dtype=torch.float32).cpu().numpy()
            hf_cached_logits.append(last)
            sel = int(np.argmax(last))
            hf_cached_selected.append(sel)
        hf_cached_seconds = time.perf_counter() - t0

    # --- Compare all four legs pairwise ---
    for i in range(steps):
        cpp_full = np.asarray(cpp["full_steps"][i]["logits_last"], dtype=np.float32)
        cpp_cached = np.asarray(cpp["cached_steps"][i]["logits_last"], dtype=np.float32)
        hf_f = hf_full_logits[i]
        hf_c = hf_cached_logits[i]

        ok1, ma1, mr1 = close(cpp_full, hf_f)
        ok2, ma2, mr2 = close(cpp_cached, hf_f)
        ok3, ma3, mr3 = close(hf_c, hf_f)
        cpp_agree = bool(np.array_equal(cpp_full, cpp_cached))

        results.append({
            "step": i,
            "cpp_full_selected": cpp["full_steps"][i]["selected"],
            "cpp_cached_selected": cpp["cached_steps"][i]["selected"],
            "hf_full_selected": hf_full_selected[i],
            "hf_cached_selected": hf_cached_selected[i],
            "cpp_full_vs_hf_full": {"pass": ok1, "max_abs": ma1, "max_rel": mr1},
            "cpp_cached_vs_hf_full": {"pass": ok2, "max_abs": ma2, "max_rel": mr2},
            "hf_cached_vs_hf_full": {"pass": ok3, "max_abs": ma3, "max_rel": mr3},
            "cpp_full_bit_identical_to_cpp_cached": cpp_agree,
        })
        print(f"step {i}: cpp_full={results[-1]['cpp_full_selected']} "
              f"cpp_cached={results[-1]['cpp_cached_selected']} "
              f"hf_full={results[-1]['hf_full_selected']} "
              f"hf_cached={results[-1]['hf_cached_selected']} | "
              f"cpp_full_vs_hf_full: pass={ok1} max_abs={ma1:.6g} | "
              f"cpp_cached_vs_hf_full: pass={ok2} max_abs={ma2:.6g} | "
              f"hf_cached_vs_hf_full: pass={ok3} max_abs={ma3:.6g}")

    sequences = {
        "cpp_full": [r["cpp_full_selected"] for r in results],
        "cpp_cached": [r["cpp_cached_selected"] for r in results],
        "hf_full": [r["hf_full_selected"] for r in results],
        "hf_cached": [r["hf_cached_selected"] for r in results],
    }
    all_seq_match = len(set(tuple(v) for v in sequences.values())) == 1
    all_gates_pass = all(r["cpp_full_vs_hf_full"]["pass"] and r["cpp_cached_vs_hf_full"]["pass"]
                         and r["hf_cached_vs_hf_full"]["pass"] for r in results)
    all_cpp_bit_identical = all(r["cpp_full_bit_identical_to_cpp_cached"] for r in results)

    print()
    print("REAL 4-WAY DIFFERENTIAL:", "PASS" if (all_seq_match and all_gates_pass) else "FAIL")
    print("sequences:", sequences)
    print("cpp full==cached bit-identical every step:", all_cpp_bit_identical)
    print(f"timing: hf_full_total_s={hf_full_seconds:.3f} hf_cached_total_s={hf_cached_seconds:.3f} "
          f"cpp_full_total_ms={cpp['full_total_milliseconds']:.1f} "
          f"cpp_prefill_ms={cpp['prefill_milliseconds']:.1f} "
          f"cpp_decode_ms={cpp['decode_milliseconds']}")

    if not (all_seq_match and all_gates_pass):
        raise AssertionError("4-way real differential failed")


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit("usage: real_hf_cached_differential.py EXE MODEL.gguf HF_SOURCE_DIR STEPS")
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]))
