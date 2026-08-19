# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Real-model 5-way differential for Phase 5A's composition audit (OE-ADR-026):

    A. Frozen Phase-4 virtualized full-prefix   (from the C++ driver's leg_a)
    B. Phase-5A fully-resident cached reference  (from the C++ driver's leg_b)
    C. Phase-5A virtualized cached target        (from the C++ driver's leg_c)
    D. HF/PyTorch full-prefix (use_cache=False)
    E. HF/PyTorch native cached decode (use_cache=True, independently constructed)

The most diagnostic pairwise comparison is B vs C: both are OrcEngine's own
cached-decode math, differing ONLY in weight residency architecture
(fully-resident vs one-layer-at-a-time virtualized). Any divergence there
would isolate a residency-composition bug from a cache-math bug.

Also performs a real KV-cache numeric cross-check: HF's own past_key_values
(itself independently constructed, never fed by OrcEngine) compared against
OrcEngine's Reference Path C cache dumps at the SAME layer/kv_head/position,
reporting shape, max_abs, max_rel -- not plausibility.
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


def bit_identical(actual: np.ndarray, expected: np.ndarray) -> bool:
    return bool(np.array_equal(actual, expected))


def main(executable: str, gguf_path: str, source_dir: str, steps: int) -> None:
    initial = [1, 5]
    dump_specs = [(0, 0, 0), (0, 0, 1), (15, 1, 0), (29, 2, 1), (0, 0, 4)]
    dump_args = []
    for layer, kv_head, position in dump_specs:
        dump_args += ["--dump-cache", f"{layer},{kv_head},{position}"]
    cpp = json.loads(subprocess.check_output(
        [executable, gguf_path, str(steps), *map(str, initial), *dump_args,
         "--output-chunk-rows", "4096"], text=True))

    leg_a = cpp["leg_a_full_prefix_virtualized"]
    leg_b = cpp["leg_b_resident_cached"]
    leg_c = cpp["leg_c_virtualized_cached"]
    if len(leg_a["selected"]) != steps or len(leg_b["selected"]) != steps or len(leg_c["selected"]) != steps:
        raise AssertionError("C++ step count mismatch across legs A/B/C")

    model = AutoModelForCausalLM.from_pretrained(
        Path(source_dir), dtype=torch.float32, local_files_only=True, attn_implementation="eager")
    model.eval()

    results = []
    hf_full_selected: list[int] = []
    hf_cached_selected: list[int] = []
    hf_cache_layers = None
    with torch.inference_mode():
        # --- D: HF full-prefix (use_cache=False) ---
        tokens = list(initial)
        hf_full_logits = []
        t0 = time.perf_counter()
        for _ in range(steps):
            out = model(input_ids=torch.tensor([tokens], dtype=torch.long), use_cache=False)
            last = out.logits[0, -1].to(dtype=torch.float32).cpu().numpy()
            hf_full_logits.append(last)
            sel = int(np.argmax(last))
            hf_full_selected.append(sel)
            tokens.append(sel)
        hf_full_seconds = time.perf_counter() - t0

        # --- E: HF native cached decode (use_cache=True), independently constructed ---
        hf_cached_logits = []
        t0 = time.perf_counter()
        out = model(input_ids=torch.tensor([initial], dtype=torch.long), use_cache=True)
        past = out.past_key_values
        last = out.logits[0, -1].to(dtype=torch.float32).cpu().numpy()
        hf_cached_logits.append(last)
        sel = int(np.argmax(last))
        hf_cached_selected.append(sel)
        for _ in range(steps - 1):
            out = model(input_ids=torch.tensor([[sel]], dtype=torch.long), past_key_values=past, use_cache=True)
            past = out.past_key_values
            last = out.logits[0, -1].to(dtype=torch.float32).cpu().numpy()
            hf_cached_logits.append(last)
            sel = int(np.argmax(last))
            hf_cached_selected.append(sel)
        hf_cached_seconds = time.perf_counter() - t0
        hf_cache_layers = past  # DynamicCache / legacy tuple, used ONLY for the KV numeric cross-check below

    # P5A-RVW-004 closure: require and record ALL six pairwise relationships
    # actually claimed by OE-ADR-027/the active gate, not just the subset
    # that happened to be checked before. Internal OrcEngine comparisons
    # (A vs B, A vs C, B vs C) are real-model evidence that the C++ side
    # already ENFORCES bit-identical via test_real_composed_evidence.cpp;
    # this script re-derives the SAME requirement independently in Python
    # rather than trusting the printed diagnostic that used to exist here.
    # Cross-framework comparisons against HF keep the established
    # absolute-or-relative tolerance.
    for i in range(steps):
        a = np.asarray(leg_a["logits"][i], dtype=np.float32)
        b = np.asarray(leg_b["logits"][i], dtype=np.float32)
        c = np.asarray(leg_c["logits"][i], dtype=np.float32)
        d = hf_full_logits[i]
        e = hf_cached_logits[i]

        ab_bit = bit_identical(a, b)
        ac_bit = bit_identical(a, c)
        bc_bit = bit_identical(b, c)
        ok_ad, ma_ad, mr_ad = close(a, d)
        ok_cd, ma_cd, mr_cd = close(c, d)
        ok_ce, ma_ce, mr_ce = close(c, e)
        ok_ed, ma_ed, mr_ed = close(e, d)
        results.append({
            "step": i,
            "selected": {"a": leg_a["selected"][i], "b": leg_b["selected"][i], "c": leg_c["selected"][i],
                        "d": hf_full_selected[i], "e": hf_cached_selected[i]},
            "a_vs_b_bit_identical": ab_bit,
            "a_vs_c_bit_identical": ac_bit,
            "b_vs_c_bit_identical": bc_bit,
            "a_vs_hf_full": {"pass": ok_ad, "max_abs": ma_ad, "max_rel": mr_ad},
            "c_vs_hf_full": {"pass": ok_cd, "max_abs": ma_cd, "max_rel": mr_cd},
            "c_vs_hf_cached": {"pass": ok_ce, "max_abs": ma_ce, "max_rel": mr_ce},
            "hf_cached_vs_hf_full": {"pass": ok_ed, "max_abs": ma_ed, "max_rel": mr_ed},
        })
        print(f"step {i}: selected a={leg_a['selected'][i]} b={leg_b['selected'][i]} c={leg_c['selected'][i]} "
              f"d={hf_full_selected[i]} e={hf_cached_selected[i]} | "
              f"a==b={ab_bit} a==c={ac_bit} b==c={bc_bit} | "
              f"c_vs_hf_full max_abs={ma_cd:.6g} c_vs_hf_cached max_abs={ma_ce:.6g}")

    sequences = {k: [r["selected"][k] for r in results] for k in ("a", "b", "c", "d", "e")}
    all_seq_match = len(set(tuple(v) for v in sequences.values())) == 1
    all_steps_present = len(results) == steps
    all_internal_bit_identical = all(
        r["a_vs_b_bit_identical"] and r["a_vs_c_bit_identical"] and r["b_vs_c_bit_identical"] for r in results)
    all_hf_gates_pass = all(
        r["a_vs_hf_full"]["pass"] and r["c_vs_hf_full"]["pass"] and
        r["c_vs_hf_cached"]["pass"] and r["hf_cached_vs_hf_full"]["pass"] for r in results)
    all_gates_pass = all_internal_bit_identical and all_hf_gates_pass

    print()
    print("REAL 5-WAY COMPOSED DIFFERENTIAL:",
          "PASS" if (all_seq_match and all_steps_present and all_gates_pass) else "FAIL")
    print("sequences:", sequences)
    print(f"exact trace lengths: expected={steps} actual={len(results)} (must match, not just be >=)")
    print("A == B == C bit-identical every step (ENFORCED, not printed-only):", all_internal_bit_identical)
    print(f"timing: hf_full_total_s={hf_full_seconds:.3f} hf_cached_total_s={hf_cached_seconds:.3f}")
    print(f"leg_a load_ms={cpp['load_milliseconds']['leg_a']:.3f} "
          f"leg_b load_ms={cpp['load_milliseconds']['leg_b']:.3f} "
          f"leg_c load_ms={cpp['load_milliseconds']['leg_c']:.3f}")
    print(f"leg_a per-step ms={leg_a['milliseconds']}")
    print(f"leg_b per-step ms={leg_b['milliseconds']}")
    print(f"leg_c prefill_ms={leg_c['prefill_milliseconds']} decode_ms={leg_c['decode_milliseconds']}")
    print("telemetry_a:", leg_a["telemetry"])
    print("telemetry_c:", leg_c["telemetry"])

    # --- Real KV numeric cross-check: HF's own cache vs Reference Path C's cache dumps. ---
    # P5A-RVW-005 closure: a missing/unresolved sample is now a HARD ERROR, not a
    # silent `continue` that shrinks the comparison set -- completeness
    # (len(kv_results) == len(dump_specs)) is required before PASS is possible.
    print()
    print("=== Real KV numeric cross-check (HF native cache vs OrcEngine Reference Path C) ===")
    kv_results = []
    kv_errors = []
    for dump in cpp["leg_c_virtualized_cached"]["cache_dumps"]:
        layer, kv_head, position = dump["layer"], dump["kv_head"], dump["position"]
        # HF's DynamicCache (current transformers API): past.layers[layer].keys / .values,
        # each [batch, n_kv_heads, seq_len, head_dim]. Independently constructed above --
        # never fed by or derived from OrcEngine's cache.
        try:
            hf_layer = hf_cache_layers.layers[layer]
            hf_k = hf_layer.keys[0, kv_head, position].to(torch.float32).cpu().numpy()
            hf_v = hf_layer.values[0, kv_head, position].to(torch.float32).cpu().numpy()
        except (IndexError, AttributeError) as ex:
            msg = f"layer={layer} kv_head={kv_head} position={position}: ERROR ({ex}) -- sample could not be resolved"
            print(f"  {msg}")
            kv_errors.append(msg)
            continue
        oe_k = np.asarray(dump["k"], dtype=np.float32)
        oe_v = np.asarray(dump["v"], dtype=np.float32)
        ok_k, ma_k, mr_k = close(oe_k, hf_k)
        ok_v, ma_v, mr_v = close(oe_v, hf_v)
        kv_results.append({"layer": layer, "kv_head": kv_head, "position": position,
                           "k": {"shape": list(hf_k.shape), "pass": ok_k, "max_abs": ma_k, "max_rel": mr_k},
                           "v": {"shape": list(hf_v.shape), "pass": ok_v, "max_abs": ma_v, "max_rel": mr_v}})
        print(f"  layer={layer} kv_head={kv_head} position={position}: "
              f"K pass={ok_k} max_abs={ma_k:.6g} max_rel={mr_k:.6g} | "
              f"V pass={ok_v} max_abs={ma_v:.6g} max_rel={mr_v:.6g}")

    all_kv_present = len(kv_results) == len(dump_specs)
    all_kv_values_pass = all(r["k"]["pass"] and r["v"]["pass"] for r in kv_results)
    all_kv_pass = all_kv_present and all_kv_values_pass
    print("REAL KV NUMERIC CROSS-CHECK:", "PASS" if all_kv_pass else "FAIL/INCOMPLETE",
          f"({len(kv_results)}/{len(dump_specs)} dumps checked -- ALL REQUIRED, not best-effort)")

    if not (all_seq_match and all_steps_present and all_gates_pass):
        raise AssertionError("5-way real composed differential failed "
                             f"(all_seq_match={all_seq_match} all_steps_present={all_steps_present} "
                             f"all_gates_pass={all_gates_pass})")
    if not all_kv_pass:
        raise AssertionError(
            f"real KV numeric cross-check failed or incomplete: {len(kv_results)}/{len(dump_specs)} resolved, "
            f"values_pass={all_kv_values_pass}, errors={kv_errors}")


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit("usage: real_5way_composed_differential.py EXE MODEL.gguf HF_SOURCE_DIR STEPS")
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]))
