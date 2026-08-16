# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Ablation-study extension to Phase 0's fault-injection suite.

Where the existing fault fixtures (oracle/model.py FaultSpec,
oracle/tokenizer_special_token_fault.py, ...) each prove the oracle
DETECTS one specific kind of mistake, this module answers a different
question: of the components that are NOT mistaken, how much does each one
actually matter? Zero one component at a time (a full transformer layer,
a single attention head's output contribution, or one FFN sub-block),
re-run the same prompts, and measure how far the output moved.

This is diagnostic tooling, not a correctness gate -- there is no
pass/fail acceptance criterion here (unlike PHASE_0_ACCEPTANCE.yaml's
checks). The output is a ranked "component importance" report, which is
the building block for two later, larger pieces of work:
  1. A per-query "explain this answer" visualization (Phase 1+, needs a
     real running engine): rank which layers/heads most affected a
     specific real response, TOKEN BY TOKEN -- see the per-position
     breakdown and query_top_components_for_position() below, which is
     the actual data shape that feature would consume.
  2. Compression/quantization decisions: components with low ablation
     impact are better candidates for aggressive quantization or pruning.

Ablation mechanics, by component type:
  - full_layer: zero every weight matrix in the layer. Since RMSNorm of
    an all-zero vector is degenerate (0/0), zeroing the WEIGHTS (not the
    normed activations) means attn_out and ffn both come out exactly
    zero regardless of the norm step, so the residual stream passes
    through this layer completely unchanged. This is the cleanest way to
    "skip a layer" without changing the model's shape/config.
  - attn_head: zero only the COLUMNS of w_o (the output projection) that
    correspond to this head's slice of the concatenated context vector.
    This removes exactly that head's contribution to attn_out without
    touching its Q/K/V projections or any other head -- surgically
    isolates one head's causal effect on the output.
  - ffn_gate / ffn_up / ffn_down: zero that whole matrix for one layer,
    isolating the SwiGLU sub-block's contribution.

Divergence metrics, computed PER POSITION then averaged for ranking:
  - logit_l2: per-position L2 distance between baseline and ablated logit
    vectors. Scale-sensitive, easy to compare across components.
  - kl_divergence: per-position KL(baseline_softmax || ablated_softmax).
    Captures probability-mass shift even when logits are close.
  - argmax_flip: per-position bool, whether the greedy-selected token
    changed. The most user-visible signal -- "did the answer actually
    change here."
The per-position arrays are retained (not just their mean) precisely so a
later query like "which component mattered most for token N of THIS
prompt" can be answered directly from the report -- see
query_top_components_for_position().
"""
from __future__ import annotations

import dataclasses
import os
import time
from dataclasses import dataclass

import numpy as np
import yaml

from oracle.model import ModelConfig, forward
from oracle.weights import ModelWeights, build_weights

SEED = 20260814
OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "ablation_sweep_report.yaml")
REAL_OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "ablation_sweep_report_real.yaml")

# More layers than the other synthetic fixtures (Fixture C uses 2) so the
# per-layer sweep produces an actual curve worth looking at, not just two points.
SWEEP_CONFIG = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=6,
                            n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)

# Multiple prompts so a single component's impact isn't an artifact of one
# specific token sequence. Distinct lengths and distinct token sets on purpose.
SWEEP_PROMPTS = [
    np.array([1, 5, 9, 3], dtype=np.int64),
    np.array([2, 17, 4, 22, 11], dtype=np.int64),
    np.array([30, 0, 15], dtype=np.int64),
]

# Real-model prompts, kept short and few -- a 30-layer/576-hidden forward pass in plain
# NumPy is orders of magnitude slower than the synthetic 6-layer/16-hidden one, so the
# real-model sweep intentionally does NOT ablate every component (that would be ~400
# components x N prompts x 30-layer forward passes each -- hours, not minutes). Default
# real-model scope is full_layer only across all 30 layers; head/FFN ablation for the
# real model is opt-in via components="full" and will be slow (logged, not silently run).
REAL_PROMPTS_TEXT = [
    "The capital of France is",
    "Once upon a time",
]


@dataclass(frozen=True)
class AblationSpec:
    layer_idx: int
    component: str  # "full_layer" | "attn_head" | "ffn_gate" | "ffn_up" | "ffn_down"
    head_idx: int | None = None  # only for component == "attn_head"

    @property
    def label(self) -> str:
        if self.component == "attn_head":
            return f"layer{self.layer_idx}.attn_head{self.head_idx}"
        return f"layer{self.layer_idx}.{self.component}"


def apply_ablation(weights: ModelWeights, spec: AblationSpec, config: ModelConfig) -> ModelWeights:
    """Returns a NEW ModelWeights with one component zeroed. Never mutates the input --
    every fixture in this suite treats weights as immutable, per the existing convention
    in fixture_near_tie.py's _weights_with_tied_rows."""
    layers = list(weights.layers)
    lw = layers[spec.layer_idx]

    if spec.component == "full_layer":
        zeroed = dataclasses.replace(
            lw,
            w_q=np.zeros_like(lw.w_q), w_k=np.zeros_like(lw.w_k), w_v=np.zeros_like(lw.w_v),
            w_o=np.zeros_like(lw.w_o), w_gate=np.zeros_like(lw.w_gate),
            w_up=np.zeros_like(lw.w_up), w_down=np.zeros_like(lw.w_down),
        )
    elif spec.component == "attn_head":
        assert spec.head_idx is not None
        w_o = lw.w_o.copy()  # [hidden, n_q_heads*head_dim]
        start = spec.head_idx * config.head_dim
        end = start + config.head_dim
        w_o[:, start:end] = 0.0
        zeroed = dataclasses.replace(lw, w_o=w_o)
    elif spec.component == "ffn_gate":
        zeroed = dataclasses.replace(lw, w_gate=np.zeros_like(lw.w_gate))
    elif spec.component == "ffn_up":
        zeroed = dataclasses.replace(lw, w_up=np.zeros_like(lw.w_up))
    elif spec.component == "ffn_down":
        zeroed = dataclasses.replace(lw, w_down=np.zeros_like(lw.w_down))
    else:
        raise ValueError(f"unknown ablation component: {spec.component!r}")

    layers[spec.layer_idx] = zeroed
    return dataclasses.replace(weights, layers=tuple(layers))


def _log_softmax(logits: np.ndarray) -> np.ndarray:
    shifted = logits - np.max(logits, axis=-1, keepdims=True)
    return (shifted - np.log(np.sum(np.exp(shifted), axis=-1, keepdims=True))).astype(np.float64)


def _kl_divergence_per_position(baseline_logits: np.ndarray, ablated_logits: np.ndarray) -> np.ndarray:
    log_p = _log_softmax(baseline_logits.astype(np.float64))
    log_q = _log_softmax(ablated_logits.astype(np.float64))
    p = np.exp(log_p)
    return np.sum(p * (log_p - log_q), axis=-1)  # [seq]


def _divergence_per_position(baseline_logits: np.ndarray, ablated_logits: np.ndarray) -> dict:
    """Per-position metrics -- the raw material both the ranking table (mean over
    positions) and query_top_components_for_position() (one specific position) are
    built from. Kept as plain lists so the YAML report is self-contained."""
    diff = ablated_logits.astype(np.float64) - baseline_logits.astype(np.float64)
    logit_l2_per_pos = np.linalg.norm(diff, axis=-1)
    kl_per_pos = _kl_divergence_per_position(baseline_logits, ablated_logits)
    baseline_argmax = np.argmax(baseline_logits, axis=-1)
    ablated_argmax = np.argmax(ablated_logits, axis=-1)
    flips = (baseline_argmax != ablated_argmax)
    return {
        "logit_l2_per_position": logit_l2_per_pos.tolist(),
        "kl_divergence_per_position": kl_per_pos.tolist(),
        "argmax_flip_per_position": [bool(f) for f in flips],
    }


def _mean_of(per_position: dict) -> dict:
    return {
        "logit_l2": float(np.mean(per_position["logit_l2_per_position"])),
        "kl_divergence": float(np.mean(per_position["kl_divergence_per_position"])),
        "argmax_flip_rate": float(np.mean(per_position["argmax_flip_per_position"])),
    }


def _all_specs(config: ModelConfig, components: str = "full") -> list[AblationSpec]:
    """components: "full" (full_layer + every head + every FFN sub-matrix, the synthetic-
    model default) or "layers_only" (full_layer ablation only, for the real-model sweep
    where a full component-level sweep would take hours)."""
    specs = []
    for layer_idx in range(config.n_layers):
        specs.append(AblationSpec(layer_idx, "full_layer"))
        if components == "full":
            for head_idx in range(config.n_q_heads):
                specs.append(AblationSpec(layer_idx, "attn_head", head_idx))
            specs.append(AblationSpec(layer_idx, "ffn_gate"))
            specs.append(AblationSpec(layer_idx, "ffn_up"))
            specs.append(AblationSpec(layer_idx, "ffn_down"))
    return specs


def query_top_components_for_position(report: dict, prompt_idx: int, position: int, top_n: int = 5,
                                       metric: str = "logit_l2_per_position") -> list[dict]:
    """The direct precursor to an "explain this answer" feature: given an already-computed
    report (as returned by run_sweep, or reloaded from the YAML artifact), answer "which
    components mattered most for THIS token at THIS position in THIS prompt." Returns the
    top_n components sorted by their impact at exactly that (prompt, position)."""
    scored = []
    for r in report["results"]:
        value = r["per_prompt"][prompt_idx][metric][position]
        scored.append({"label": r["label"], "value": value})
    scored.sort(key=lambda s: s["value"], reverse=True)
    return scored[:top_n]


def run_sweep(weights: ModelWeights, config: ModelConfig, prompts: list[np.ndarray],
              specs: list[AblationSpec], *, model_label: str, log_progress: bool = False) -> dict:
    """Generic sweep runner -- works for both the fast synthetic model and the slow real
    model, given whatever (weights, config, prompts, specs) the caller has already built."""
    t0 = time.time()
    baseline_logits_by_prompt = []
    for i, prompt in enumerate(prompts):
        baseline_logits_by_prompt.append(forward(prompt, weights, config, capture_taps=False).logits)
        if log_progress:
            print(f"  baseline prompt {i + 1}/{len(prompts)} done ({time.time() - t0:.1f}s elapsed)")

    results = []
    for spec_i, spec in enumerate(specs):
        ablated_weights = apply_ablation(weights, spec, config)
        per_prompt = []
        for prompt, baseline_logits in zip(prompts, baseline_logits_by_prompt, strict=True):
            ablated_logits = forward(prompt, ablated_weights, config, capture_taps=False).logits
            per_prompt.append({
                "token_ids": prompt.tolist(),
                **_divergence_per_position(baseline_logits, ablated_logits),
            })
        aggregated = [_mean_of(pp) for pp in per_prompt]
        mean_across_prompts = {
            k: float(np.mean([a[k] for a in aggregated])) for k in aggregated[0]
        }
        results.append({
            "label": spec.label,
            "layer_idx": spec.layer_idx,
            "component": spec.component,
            "head_idx": spec.head_idx,
            **mean_across_prompts,
            "per_prompt": per_prompt,
        })
        if log_progress and (spec_i + 1) % 10 == 0:
            print(f"  component {spec_i + 1}/{len(specs)} done ({time.time() - t0:.1f}s elapsed)")

    results.sort(key=lambda r: r["logit_l2"], reverse=True)
    return {
        "model_label": model_label,
        "n_prompts": len(prompts),
        "components_swept": len(results),
        "elapsed_seconds": time.time() - t0,
        "results": results,
    }


def _print_summary(report: dict) -> None:
    print(f"\nablation sweep ({report['model_label']}): {report['components_swept']} components, "
          f"{report['n_prompts']} prompts, {report['elapsed_seconds']:.1f}s\n")
    print(f"{'component':<28} {'logit_l2':>12} {'kl_div':>12} {'argmax_flip':>12}")
    for r in report["results"]:
        print(f"{r['label']:<28} {r['logit_l2']:>12.4f} {r['kl_divergence']:>12.4f} "
              f"{r['argmax_flip_rate']:>12.2%}")


def _write_report(report: dict, output_path: str) -> None:
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        yaml.safe_dump(report, f, sort_keys=False, default_flow_style=False)
    print(f"\nwrote {os.path.abspath(output_path)}")


def _sanity_check_full_layer_dominance(report: dict) -> bool:
    """Not a correctness gate -- a sanity check on the ablation mechanics themselves. A
    full-layer ablation should never be LESS impactful than ablating a single sub-component
    of that same layer; if it were, something's wrong with how components are being zeroed."""
    ok = True
    by_layer: dict[int, list[dict]] = {}
    for r in report["results"]:
        by_layer.setdefault(r["layer_idx"], []).append(r)
    for layer_idx, layer_results in by_layer.items():
        full = next((r for r in layer_results if r["component"] == "full_layer"), None)
        sub_components = [r for r in layer_results if r["component"] != "full_layer"]
        if full is None or not sub_components:
            continue
        max_sub = max(r["logit_l2"] for r in sub_components)
        if full["logit_l2"] + 1e-6 < max_sub:
            print(f"SANITY FAIL: layer {layer_idx} full_layer ablation ({full['logit_l2']:.4f}) "
                  f"is less impactful than a sub-component ({max_sub:.4f})")
            ok = False
    return ok


def _demo_per_position_query(report: dict) -> None:
    """Demonstrates the actual data shape the future 'explain this answer' visualization
    would consume: for the last position of the first prompt, which components mattered most
    RIGHT THERE, as opposed to on average across the whole sweep."""
    last_prompt = report["results"][0]["per_prompt"][0]
    last_pos = len(last_prompt["token_ids"]) - 1
    top = query_top_components_for_position(report, prompt_idx=0, position=last_pos, top_n=5)
    print(f"\nper-position query demo: top components for prompt 0, position {last_pos} "
          f"(token_id={last_prompt['token_ids'][last_pos]}):")
    for entry in top:
        print(f"  {entry['label']:<28} logit_l2={entry['value']:.4f}")


def run() -> bool:
    """Synthetic-model sweep (fast, full component coverage) -- the default entry point."""
    config = SWEEP_CONFIG
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    specs = _all_specs(config, components="full")
    report = run_sweep(weights, config, SWEEP_PROMPTS, specs, model_label="synthetic-6layer")
    _print_summary(report)
    _demo_per_position_query(report)
    _write_report(report, OUTPUT_PATH)
    return _sanity_check_full_layer_dominance(report)


def run_real(components: str = "layers_only") -> bool:
    """Real-model sweep against the pinned SmolLM2-135M candidate. Reuses
    real_candidate_logits_check.load_real_weights() rather than re-implementing
    safetensors loading -- same real weights used for the logits cross-check against
    llama.cpp. Defaults to full_layer-only ablation (30 components) since a full
    component-level sweep (~13 components/layer x 30 layers = ~390) at this model's size
    would take a long time in plain NumPy; pass components="full" to opt into that."""
    from oracle.convert_real_candidate import SOURCE_DIR
    from oracle.real_candidate_logits_check import load_real_weights
    from tokenizers import Tokenizer

    if not os.path.isdir(SOURCE_DIR):
        print(f"FAIL: {SOURCE_DIR!r} not found. Run oracle.download_candidate first.")
        return False

    print("loading real SmolLM2-135M weights...")
    weights, config = load_real_weights()
    print(f"  config: n_layers={config.n_layers} hidden={config.hidden} "
          f"n_q_heads={config.n_q_heads} n_kv_heads={config.n_kv_heads} head_dim={config.head_dim}")

    tokenizer = Tokenizer.from_file(os.path.join(SOURCE_DIR, "tokenizer.json"))
    prompts = [np.array(tokenizer.encode(t).ids, dtype=np.int64) for t in REAL_PROMPTS_TEXT]
    for text, ids in zip(REAL_PROMPTS_TEXT, prompts, strict=True):
        print(f"  prompt: {text!r} -> {len(ids)} tokens")

    specs = _all_specs(config, components=components)
    n_forward_passes = len(specs) * len(prompts) + len(prompts)
    print(f"  scope: components={components!r} -> {len(specs)} components, "
          f"{n_forward_passes} total 30-layer forward passes -- this will take a while")

    report = run_sweep(weights, config, prompts, specs, model_label="SmolLM2-135M (real)",
                        log_progress=True)
    _print_summary(report)
    _demo_per_position_query(report)
    _write_report(report, REAL_OUTPUT_PATH)
    return _sanity_check_full_layer_dominance(report)


if __name__ == "__main__":
    import sys
    if "--real" in sys.argv:
        components = "full" if "--full" in sys.argv else "layers_only"
        ok = run_real(components=components)
        label = "ablation_sweep_real"
    else:
        ok = run()
        label = "ablation_sweep"
    print(f"\n{'PASS' if ok else 'FAIL'}: {label} sanity checks")
    raise SystemExit(0 if ok else 1)
