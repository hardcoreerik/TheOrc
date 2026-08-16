# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Exports (1) a weights-only fixture and (2) an INDEPENDENTLY-COMPUTED Python
greedy-decode trace, for the Phase-1 freeze-audit's autoregressive decoding
proof. The Python side makes its own token choices at each step (full
recompute, no KV cache -- Phase 1 has none) using ONLY the model weights and
its own argmax -- it is not told what the C++ side will choose. The C++ side
(tests/test_decode.cpp) does the same independently, then a comparison
script checks the two traces agree at every step.

Same Fixture-C dimensions and weight seed as export_cpp_phase1_fixture.py's
tied fixture, so this exercises the identical model the differential harness
already validated tap-by-tap -- this test isolates the *decode loop*, not
the *forward pass*, as the thing under test.
"""
from __future__ import annotations

import sys

import numpy as np

sys.path.insert(0, ".")
from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

OUT_DIR = "fixtures_phase1"
N_STEPS = 8
INITIAL_TOKENS = [1, 5]


def w(f, *tokens):
    f.write(" ".join(str(t) for t in tokens) + "\n")


def write_tensor(f, name: str, arr: np.ndarray):
    arr = np.asarray(arr, dtype=np.float32)
    w(f, "TENSOR", name, arr.ndim, *arr.shape)
    f.write(" ".join(f"{v:.9g}" for v in arr.flatten(order="C")) + "\n")


def build_model():
    vocab, hidden, intermediate = 32, 16, 32
    n_layers, n_q_heads, n_kv_heads, head_dim = 2, 4, 2, 4
    max_positions = 16
    config = ModelConfig(vocab=vocab, hidden=hidden, intermediate=intermediate,
                          n_layers=n_layers, n_q_heads=n_q_heads, n_kv_heads=n_kv_heads,
                          head_dim=head_dim, max_positions=max_positions)
    weights = build_weights(seed=20260815, vocab=vocab, hidden=hidden, intermediate=intermediate,
                             n_layers=n_layers, n_q_heads=n_q_heads, n_kv_heads=n_kv_heads,
                             head_dim=head_dim)
    return config, weights


def export_weights(path: str, config: ModelConfig, weights) -> None:
    with open(path, "w") as f:
        w(f, "CONFIG", config.vocab, config.hidden, config.intermediate, config.n_layers,
          config.n_q_heads, config.n_kv_heads, config.head_dim, config.max_positions,
          f"{config.rmsnorm_epsilon:.9g}", f"{config.rope_theta:.9g}")
        w(f, "TIED", 1)
        w(f, "SEQ", len(INITIAL_TOKENS))
        w(f, "TOKENS", *INITIAL_TOKENS)
        write_tensor(f, "token_embedding", weights.token_embedding)
        write_tensor(f, "final_norm_weight", weights.final_norm_weight)
        for li, lw in enumerate(weights.layers):
            w(f, "LAYER", li)
            write_tensor(f, "attn_norm_weight", lw.attn_norm_weight)
            write_tensor(f, "w_q", lw.w_q)
            write_tensor(f, "w_k", lw.w_k)
            write_tensor(f, "w_v", lw.w_v)
            write_tensor(f, "w_o", lw.w_o)
            write_tensor(f, "ffn_norm_weight", lw.ffn_norm_weight)
            write_tensor(f, "w_gate", lw.w_gate)
            write_tensor(f, "w_up", lw.w_up)
            write_tensor(f, "w_down", lw.w_down)


def run_python_decode(config: ModelConfig, weights, n_steps: int) -> list[dict]:
    """Full-recompute greedy decode: no KV cache, matching Phase 1's C++ scope
    exactly. Each step re-runs the ENTIRE forward pass over the growing
    sequence and picks argmax of the LAST position's logits -- Python makes
    this choice independently, using only the weights, not any C++ output."""
    tokens = list(INITIAL_TOKENS)
    steps = []
    for step in range(n_steps):
        token_ids = np.array(tokens, dtype=np.int64)
        result = forward(weights=weights, config=config, token_ids=token_ids, capture_taps=False)
        last_logits = result.logits[-1]  # [vocab]
        selected = int(np.argmax(last_logits))
        steps.append({
            "seq_before": list(tokens),
            "logits_last": last_logits.tolist(),
            "selected": selected,
        })
        tokens.append(selected)
    return steps


def export_trace(path: str, steps: list[dict]) -> None:
    with open(path, "w") as f:
        w(f, "STEPS", len(steps))
        for i, s in enumerate(steps):
            w(f, "STEP", i)
            w(f, "SEQ_BEFORE", len(s["seq_before"]), *s["seq_before"])
            vocab = len(s["logits_last"])
            w(f, "LOGITS_LAST", vocab, *[f"{v:.9g}" for v in s["logits_last"]])
            w(f, "SELECTED", s["selected"])


def main() -> int:
    import os
    os.makedirs(OUT_DIR, exist_ok=True)
    config, weights = build_model()
    export_weights(f"{OUT_DIR}/fixture_decode_weights.txt", config, weights)
    steps = run_python_decode(config, weights, N_STEPS)
    export_trace(f"{OUT_DIR}/fixture_decode_trace_python.txt", steps)
    print(f"Python greedy decode ({N_STEPS} steps from {INITIAL_TOKENS}):")
    tokens = list(INITIAL_TOKENS)
    for s in steps:
        tokens.append(s["selected"])
    print("  full sequence:", tokens)
    print(f"wrote {OUT_DIR}/fixture_decode_weights.txt and {OUT_DIR}/fixture_decode_trace_python.txt")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
