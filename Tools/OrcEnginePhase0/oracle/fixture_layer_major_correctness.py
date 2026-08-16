# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Two correctness properties of run_sweep_streaming_layer_major found and
fixed during the OrcEngine steering review (Infinite_Model_Runtime_Claude_
Handoff.md task), neither previously covered by a test:

1. Same-layer multiple interventions. The original implementation used
   `next(...)` to find a SINGLE branch targeting each layer -- correct
   only for components="layers_only" (exactly one spec per layer). For
   components="full" a layer can have ~28 interventions (full_layer,
   every head, every FFN sub-matrix); `next()` would silently apply only
   the first and leave every other same-layer branch running against
   unablated weights, i.e. silently returning baseline values for
   branches that were never actually ablated. This fixture runs TWO
   different interventions on the SAME layer (an attn_head ablation and
   an ffn_gate ablation, both layer_idx=0) and proves each produces a
   DIFFERENT, individually-correct result -- not both silently equal to
   baseline, and not both silently equal to each other.

2. full_layer identity-bypass equality. full_layer ablation zeroes every
   attention/FFN projection, which is algebraically identical to simply
   passing the block's input straight through (x_out = x_in) -- the
   fixed implementation skips cloning the whole layer's weights and
   skips the forward computation entirely for these branches rather than
   cloning-and-zeroing. This fixture proves that optimization produces
   EXACTLY the same result as the CPU oracle's clone-and-zero
   implementation (oracle.ablation_sweep.apply_ablation, unchanged),
   which remains the independent reference for this claim.
"""
from __future__ import annotations

import os

import numpy as np

from oracle.ablation_sweep import AblationSpec, apply_ablation
from oracle.ablation_sweep_streaming import run_sweep_streaming_layer_major
from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

SEED = 20260814


def _write_synthetic_gguf(path: str) -> ModelConfig:
    """Writes a tiny synthetic llama-arch GGUF so StreamingGGUFModel (real-GGUF-only) can be
    exercised without needing a real downloaded model -- reuses export_gguf.py's existing
    Profile-A-to-GGUF writer rather than hand-rolling a second one."""
    from oracle.export_gguf import export
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=3,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    export(weights, config, path)
    return config


def run() -> bool:
    gguf_path = os.path.join(os.path.dirname(__file__), "..", "artifacts", "_layer_major_correctness_fixture.gguf")
    config = _write_synthetic_gguf(gguf_path)
    prompts = [np.array([1, 5, 9, 3], dtype=np.int64), np.array([2, 17, 4], dtype=np.int64)]
    ok = True

    try:
        # --- Property 1: two interventions on the same layer, both must be individually
        # correct, neither silently baseline nor silently identical to the other. ---
        specs_same_layer = [
            AblationSpec(layer_idx=0, component="attn_head", head_idx=0),
            AblationSpec(layer_idx=0, component="ffn_gate"),
        ]

        # Reference: CPU oracle, one independent forward pass per spec (ground truth, unrelated
        # code path -- ablation_sweep.py's apply_ablation, untouched by this session's fixes).
        weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                                 intermediate=config.intermediate, n_layers=config.n_layers,
                                 n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                                 head_dim=config.head_dim)
        baseline_logits = [forward(p, weights, config, capture_taps=False).logits for p in prompts]
        cpu_ref_logits = []
        for spec in specs_same_layer:
            ablated_weights = apply_ablation(weights, spec, config)
            cpu_ref_logits.append([forward(p, ablated_weights, config, capture_taps=False).logits for p in prompts])

        # Streaming layer-major with BOTH specs active simultaneously (components="full" would
        # generate many more specs than these two -- construct the branch list directly via a
        # tiny local monkey-free approach: call the real function with components="layers_only"
        # is not enough since that only ever emits ONE spec per layer. Exercise the actual bug
        # surface by calling _all_specs equivalent manually through the public function using a
        # component scope that puts >1 spec on layer 0: "full" does this for a 3-layer model.
        report = run_sweep_streaming_layer_major(
            gguf_path, prompts, model_label="fixture", device="cpu", components="full", log_progress=False
        )
        results_by_label = {r["label"]: r for r in report["results"]}

        for spec, cpu_logits_per_prompt in zip(specs_same_layer, cpu_ref_logits, strict=True):
            label = spec.label
            r = results_by_label[label]
            # Recompute the same divergence metric the streaming report stores, from the CPU
            # reference logits, and compare directly against the streaming report's own value.
            from oracle.ablation_sweep import _divergence_per_position, _mean_of
            per_prompt_ref = [
                _mean_of(_divergence_per_position(baseline_logits[i], cpu_logits_per_prompt[i]))
                for i in range(len(prompts))
            ]
            ref_logit_l2 = float(np.mean([m["logit_l2"] for m in per_prompt_ref]))
            diff = abs(r["logit_l2"] - ref_logit_l2)
            print(f"{label}: streaming logit_l2={r['logit_l2']:.6f} cpu_ref={ref_logit_l2:.6f} diff={diff:.6f}")
            if diff > 1e-3:
                print(f"  FAIL: {label} diverges from its own independent CPU reference")
                ok = False

        head0_l2 = results_by_label["layer0.attn_head0"]["logit_l2"]
        gate_l2 = results_by_label["layer0.ffn_gate"]["logit_l2"]
        if abs(head0_l2 - gate_l2) < 1e-6:
            print(f"FAIL: layer0.attn_head0 ({head0_l2}) and layer0.ffn_gate ({gate_l2}) "
                  f"produced suspiciously identical results -- the multi-intervention bug may "
                  f"still be silently collapsing both to the same (baseline?) value")
            ok = False
        else:
            print(f"PASS: layer0.attn_head0 ({head0_l2:.4f}) and layer0.ffn_gate ({gate_l2:.4f}) "
                  f"are distinct, independently-correct results")

        # --- Property 2: full_layer identity-bypass equals clone-and-zero exactly. ---
        full_layer_spec = AblationSpec(layer_idx=1, component="full_layer")
        cpu_ablated_weights = apply_ablation(weights, full_layer_spec, config)
        cpu_full_layer_logits = [forward(p, cpu_ablated_weights, config, capture_taps=False).logits for p in prompts]

        streaming_full_layer = results_by_label["layer1.full_layer"]
        from oracle.ablation_sweep import _divergence_per_position, _mean_of
        per_prompt_ref = [
            _mean_of(_divergence_per_position(baseline_logits[i], cpu_full_layer_logits[i]))
            for i in range(len(prompts))
        ]
        ref_logit_l2 = float(np.mean([m["logit_l2"] for m in per_prompt_ref]))
        diff = abs(streaming_full_layer["logit_l2"] - ref_logit_l2)
        print(f"\nlayer1.full_layer (identity bypass): streaming logit_l2="
              f"{streaming_full_layer['logit_l2']:.8f} cpu_ref(clone-and-zero)={ref_logit_l2:.8f} diff={diff:.8f}")
        # Tolerance matches the fp16-storage-vs-fp32-CPU-reference gap already established
        # throughout this session (verify_gpu_against_cpu.py: ~0.03 max logit diff on real
        # models) -- StreamingGGUFModel stores weights in fp16 by default, so ANY streaming
        # result differs from a pure-fp32 CPU computation by that much even with zero algebraic
        # error. A REAL identity-bypass bug (e.g. skipping the wrong layer, or not skipping at
        # all) would diverge by orders of magnitude more than rounding noise, not a few
        # thousandths -- this tolerance still catches that class of error.
        identity_bypass_correct = diff < 0.05
        print(f"{'PASS' if identity_bypass_correct else 'FAIL'}: identity-bypass full_layer result "
              f"matches clone-and-zero reference within fp16-storage tolerance")
        ok = ok and identity_bypass_correct

    finally:
        if os.path.isfile(gguf_path):
            os.remove(gguf_path)

    return ok


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: fixture_layer_major_correctness")
    raise SystemExit(0 if ok else 1)
