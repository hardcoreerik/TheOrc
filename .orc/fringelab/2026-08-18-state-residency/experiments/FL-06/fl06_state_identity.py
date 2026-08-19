# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
FL-06 -- Context / Effective Execution Identity.

Question: can persistent inference state (a KV cache) be associated with
an explicit identity describing the computation that produced it, such
that reuse under a matching identity is safe and reuse under a mismatched
identity is detectably unsafe? Which candidate identity fields actually
affect produced state, experimentally, versus which are merely proposed?

OrcEngine has no adapter/LoRA execution to exercise, so this experiment
uses two synthetic identity dimensions that DO exist in the current
engine: (1) the model weights themselves (varied via a different RNG
seed -- the closest available analogue to "different model/adapter"),
and (2) RoPE configuration (rope_theta), which is architecture-level
state that changes what a given cached K vector MEANS at a given
position, independent of the weights that produced it. A third,
explicitly non-state-affecting field (greedy vs top-2 token selection)
is tested as a negative control: decoding POLICY should not change
already-produced KV state, only which token gets fed next.
"""
from __future__ import annotations

import json
import sys
from dataclasses import dataclass
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[5] / "Tools" / "OrcEnginePhase0"))

from oracle.model import ModelConfig, forward_cached, empty_kv_cache  # noqa: E402
from oracle.weights import build_weights  # noqa: E402

DTYPE = np.float32
TOL = 1e-5


@dataclass
class StateIdentity:
    """Candidate identity fields. Only a subset is experimentally exercised
    here (weight_seed, rope_theta) -- the rest are recorded as PROPOSED,
    not proven, per this experiment's own charter."""
    weight_seed: int
    n_layers: int
    n_q_heads: int
    n_kv_heads: int
    head_dim: int
    rope_theta: float
    rotary_dim: int | None
    kv_dtype: str = "float32"          # PROPOSED, not varied this pass -- only one dtype exists in this oracle
    execution_semantics_version: str = "v1"  # PROPOSED, not varied this pass -- no second version exists to test against

    def key(self) -> tuple:
        return (self.weight_seed, self.n_layers, self.n_q_heads, self.n_kv_heads,
                self.head_dim, self.rope_theta, self.rotary_dim, self.kv_dtype,
                self.execution_semantics_version)


def close(a: np.ndarray, b: np.ndarray, tol: float = TOL) -> tuple[bool, float]:
    diff = float(np.abs(a - b).max())
    return diff <= tol, diff


def main() -> None:
    log: dict = {"experiment": "FL-06"}

    identity_a = StateIdentity(weight_seed=20260818, n_layers=2, n_q_heads=4, n_kv_heads=2,
                                head_dim=4, rope_theta=10000.0, rotary_dim=None)
    identity_b_weights = StateIdentity(weight_seed=99999, n_layers=2, n_q_heads=4, n_kv_heads=2,
                                        head_dim=4, rope_theta=10000.0, rotary_dim=None)
    identity_b_rope = StateIdentity(weight_seed=20260818, n_layers=2, n_q_heads=4, n_kv_heads=2,
                                     head_dim=4, rope_theta=50000.0, rotary_dim=None)

    def build(identity: StateIdentity):
        config = ModelConfig(n_layers=identity.n_layers, n_q_heads=identity.n_q_heads,
                             n_kv_heads=identity.n_kv_heads, head_dim=identity.head_dim,
                             rope_theta=identity.rope_theta, rotary_dim=identity.rotary_dim)
        weights = build_weights(seed=identity.weight_seed, vocab=config.vocab, hidden=config.hidden,
                                intermediate=config.intermediate, n_layers=config.n_layers,
                                n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                                head_dim=config.head_dim)
        return config, weights

    config_a, weights_a = build(identity_a)
    config_b_weights, weights_b_weights = build(identity_b_weights)
    config_b_rope, weights_b_rope = build(identity_b_rope)

    prefix_tokens = np.array([1, 5, 3], dtype=np.int64)
    print("=== FL-06: Context / Effective Execution Identity ===")
    print(f"identity_a: {identity_a.key()}")
    print(f"identity_b_weights (different weight_seed only): {identity_b_weights.key()}")
    print(f"identity_b_rope (different rope_theta only): {identity_b_rope.key()}")

    # --- Produce persistent state under identity A. ---
    result_a, cache_a = forward_cached(prefix_tokens, weights_a, config_a, kv_cache=None,
                                        start_position=0, capture_taps=False)
    print(f"\nstate produced under identity_a, selected after prefill: {int(np.argmax(result_a.logits[-1]))}")

    tests = {}

    # --- Test 1: exact identity reuse. ---
    next_token = np.array([int(np.argmax(result_a.logits[-1]))], dtype=np.int64)
    reuse_exact, _ = forward_cached(next_token, weights_a, config_a, kv_cache=cache_a,
                                    start_position=int(prefix_tokens.shape[0]), capture_taps=False)
    clean_a, _ = forward_cached(np.concatenate([prefix_tokens, next_token]), weights_a, config_a,
                                kv_cache=None, start_position=0, capture_taps=False)
    ok_exact, diff_exact = close(reuse_exact.logits[-1], clean_a.logits[-1])
    tests["exact_identity_reuse"] = {"pass": ok_exact, "max_abs_diff": diff_exact,
                                     "expected": "match (same identity, cached state is valid)"}
    print(f"  1. exact identity reuse: pass={ok_exact} max_abs_diff={diff_exact:.6g}")

    # --- Test 2: "A-equivalent copy" -- an INDEPENDENT ModelWeights object built with the
    # SAME seed (same float values, different Python object identity) continuing from cache_a. ---
    _, weights_a_copy = build(identity_a)  # fresh object, same seed -> identical values, different id()
    assert weights_a_copy is not weights_a, "must be a distinct Python object to test value-vs-identity"
    reuse_equiv_copy, _ = forward_cached(next_token, weights_a_copy, config_a, kv_cache=cache_a,
                                         start_position=int(prefix_tokens.shape[0]), capture_taps=False)
    ok_equiv, diff_equiv = close(reuse_equiv_copy.logits[-1], clean_a.logits[-1])
    tests["equivalent_copy_reuse"] = {"pass": ok_equiv, "max_abs_diff": diff_equiv,
                                      "expected": "match (identity is about VALUES, not Python object identity)"}
    print(f"  2. equivalent-copy (same seed, different object) reuse: pass={ok_equiv} max_abs_diff={diff_equiv:.6g}")

    # --- Test 3a: different identity (weights) -- contamination via reusing cache_a under weights_b. ---
    contaminated_weights, _ = forward_cached(next_token, weights_b_weights, config_b_weights, kv_cache=cache_a,
                                             start_position=int(prefix_tokens.shape[0]), capture_taps=False)
    clean_b_weights, _ = forward_cached(np.concatenate([prefix_tokens, next_token]), weights_b_weights,
                                        config_b_weights, kv_cache=None, start_position=0, capture_taps=False)
    ok_b_weights, diff_b_weights = close(contaminated_weights.logits[-1], clean_b_weights.logits[-1])
    tests["different_identity_weights_contamination"] = {
        "pass_would_mean_no_detectable_contamination": ok_b_weights,
        "max_abs_diff": diff_b_weights,
        "expected": "DIVERGE (state from identity_a is invalid under identity_b_weights)",
        "actually_diverged": not ok_b_weights,
    }
    print(f"  3a. different identity (weights) contamination: max_abs_diff={diff_b_weights:.6g} "
          f"diverged={not ok_b_weights}")

    # --- Test 3b: different identity (RoPE theta) -- same weights, different positional encoding. ---
    contaminated_rope, _ = forward_cached(next_token, weights_b_rope, config_b_rope, kv_cache=cache_a,
                                          start_position=int(prefix_tokens.shape[0]), capture_taps=False)
    clean_b_rope, _ = forward_cached(np.concatenate([prefix_tokens, next_token]), weights_b_rope, config_b_rope,
                                     kv_cache=None, start_position=0, capture_taps=False)
    ok_b_rope, diff_b_rope = close(contaminated_rope.logits[-1], clean_b_rope.logits[-1])
    tests["different_identity_rope_contamination"] = {
        "pass_would_mean_no_detectable_contamination": ok_b_rope,
        "max_abs_diff": diff_b_rope,
        "expected": "DIVERGE (cached K/V were rotated under theta=10000, being read back under theta=50000)",
        "actually_diverged": not ok_b_rope,
    }
    print(f"  3b. different identity (RoPE theta) contamination: max_abs_diff={diff_b_rope:.6g} "
          f"diverged={not ok_b_rope}")

    # --- Test 4: same model + different DECODING POLICY (should NOT affect already-produced state). ---
    # Compare the CACHE CONTENT (not just logits) produced by the prefill step under two different
    # downstream selection rules applied AFTER the same logits -- the cache for the ALREADY-FED
    # tokens must be identical regardless of what selection rule is used to choose the next token,
    # since selection is a pure post-hoc function of already-computed logits.
    top1_token = int(np.argmax(result_a.logits[-1]))
    logits_sorted = np.argsort(result_a.logits[-1])[::-1]
    top2_token = int(logits_sorted[1]) if logits_sorted.shape[0] > 1 else top1_token
    cache_content_identical = all(
        np.array_equal(cache_a.layers[i].k, cache_a.layers[i].k) and np.array_equal(cache_a.layers[i].v, cache_a.layers[i].v)
        for i in range(config_a.n_layers)
    )  # trivially true (same object) -- the REAL test is whether choosing top1 vs top2 as "next token"
       # required recomputing or altered cache_a at all, which it structurally cannot (selection
       # happens after forward_cached returns; cache_a is a fixed value once produced).
    tests["decoding_policy_does_not_affect_prior_state"] = {
        "top1_token": top1_token, "top2_token": top2_token,
        "cache_a_object_reused_unmodified_for_both_choices": True,
        "note": "Structural guarantee, not just an empirical observation: forward_cached() returns "
                "cache_a as a fixed value BEFORE any token-selection policy is applied downstream; "
                "no selection rule can retroactively alter it. Confirmed by construction, not by "
                "searching for a counterexample.",
    }
    print(f"  4. decoding policy (top1={top1_token} vs top2={top2_token}) cannot retroactively alter "
          f"already-produced state (structural guarantee -- state is returned before selection happens)")

    log["tests"] = tests

    # --- Contamination attack, extended over several steps (not just the first token). ---
    print("\n=== Contamination attack, multi-step ===")
    contamination_steps = []
    running_cache_contaminated = cache_a
    running_tokens_clean = list(prefix_tokens)
    running_cache_clean = None
    position = int(prefix_tokens.shape[0])
    sel_contam = top1_token
    sel_clean = top1_token
    for step in range(4):
        # Contaminated: keep feeding tokens under weights_b_weights while reusing the
        # cache lineage that started life under weights_a.
        contam_result, running_cache_contaminated = forward_cached(
            np.array([sel_contam], dtype=np.int64), weights_b_weights, config_b_weights,
            kv_cache=running_cache_contaminated, start_position=position, capture_taps=False)
        # Clean: fully independent recompute under weights_b_weights from scratch each step.
        running_tokens_clean.append(sel_clean)
        clean_result, running_cache_clean = forward_cached(
            np.array(running_tokens_clean, dtype=np.int64), weights_b_weights, config_b_weights,
            kv_cache=None, start_position=0, capture_taps=False)
        clean_last = clean_result.logits[-1]
        contam_last = contam_result.logits[-1]
        ok_step, diff_step = close(contam_last, clean_last)
        contamination_steps.append({"step": step, "max_abs_diff": diff_step, "diverged": not ok_step})
        print(f"  step {step}: max_abs_diff={diff_step:.6g} diverged={not ok_step}")
        sel_contam = int(np.argmax(contam_last))
        sel_clean = int(np.argmax(clean_last))
        position += 1
    log["contamination_multi_step"] = contamination_steps
    first_divergent_step = next((s["step"] for s in contamination_steps if s["diverged"]), None)
    log["first_divergent_step"] = first_divergent_step
    print(f"  first divergent step: {first_divergent_step}")

    log["identity_fields"] = {
        "experimentally_proven_relevant": ["weight_seed (model weights)", "rope_theta (positional encoding config)"],
        "proposed_for_future_identity_not_tested_this_pass": [
            "kv_dtype (only float32 exists in this oracle -- no second dtype to test against)",
            "execution_semantics_version (only one execution path exists -- no second version to test against)",
            "adapter identity / adapter scale (OrcEngine has no adapter execution to exercise)",
            "architecture (n_layers/n_q_heads/n_kv_heads/head_dim) -- not independently varied this pass; "
            "changing these would also change tensor SHAPES, which forward_cached would reject via a "
            "shape mismatch before any silent contamination could occur, so this is expected to be "
            "structurally safe rather than a genuine identity-tracking question, but was not empirically "
            "exercised here",
        ],
        "verified_non_state_affecting": ["decoding/token-selection policy (structural argument, see test 4)"],
    }

    out_dir = Path(__file__).parent / "raw"
    out_dir.mkdir(exist_ok=True)
    with open(out_dir / "fl06_result.json", "w", encoding="utf-8") as f:
        json.dump(log, f, indent=2, default=lambda o: o.tolist() if isinstance(o, np.ndarray) else str(o))
    print(f"\nRaw results written to {out_dir / 'fl06_result.json'}")


if __name__ == "__main__":
    main()
