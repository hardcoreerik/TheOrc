# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Exports (1) a weights-only fixture and (2) an INDEPENDENTLY-computed
prefill + incremental-decode trace, for Phase 5A's KV-cached decode proof.

Reuses oracle/model.py's forward_cached() and KVCache -- already proven
equivalent to full-prefix forward() by Phase 0's own cache_equivalence
acceptance check (PHASE_0_ACCEPTANCE.yaml). This script does not re-derive
that equivalence; it re-exercises the SAME trusted function to produce a
trace a fresh C++ cached-decode implementation can be compared against,
mirroring export_cpp_phase1_decode_fixture.py's pattern exactly but with
FULL CACHE CONTENT recorded per step, not just logits -- a logit match alone
cannot distinguish "correct cache" from "wrong cache that still happens to
select the right argmax" (see PHASE5A_KV_CACHE_SPEC.md's oracle section).
"""
from __future__ import annotations

import sys

import numpy as np

sys.path.insert(0, ".")
from oracle.model import ModelConfig, empty_kv_cache, forward_cached
from oracle.weights import build_weights

OUT_DIR = "fixtures_phase5a"
N_DECODE_STEPS = 8
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
    # Same seed as export_cpp_phase1_fixture.py's tied fixture -- same
    # trusted model every OrcEngine C++ phase has already validated against.
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


def run_python_cached_trace(config: ModelConfig, weights) -> list[dict]:
    """Prefill, then N_DECODE_STEPS incremental steps -- Python decides its
    own next token at each step independently (same discipline as Phase 1's
    autoregressive decode proof), and records full per-layer cache K/V
    content after each step for structural comparison, not just logits."""
    initial = np.array(INITIAL_TOKENS, dtype=np.int64)
    result, cache = forward_cached(initial, weights, config, kv_cache=None,
                                    start_position=0, capture_taps=False)
    steps = []
    tokens = list(INITIAL_TOKENS)
    last_logits = result.logits[-1]
    selected = int(np.argmax(last_logits))

    def snapshot_cache(cache) -> list[dict]:
        return [{"k": lc.k.copy(), "v": lc.v.copy()} for lc in cache.layers]

    steps.append({
        "kind": "prefill",
        "seq_before": [],
        "new_tokens": list(INITIAL_TOKENS),
        "start_position": 0,
        "logits_last": last_logits.tolist(),
        "selected": selected,
        "cache_after": snapshot_cache(cache),
    })
    tokens.append(selected)
    position = len(INITIAL_TOKENS)

    for _ in range(N_DECODE_STEPS):
        new_token = np.array([selected], dtype=np.int64)
        result, cache = forward_cached(new_token, weights, config, kv_cache=cache,
                                        start_position=position, capture_taps=False)
        last_logits = result.logits[-1]
        selected = int(np.argmax(last_logits))
        steps.append({
            "kind": "decode",
            "seq_before": list(tokens),
            "new_tokens": [int(new_token[0])],
            "start_position": position,
            "logits_last": last_logits.tolist(),
            "selected": selected,
            "cache_after": snapshot_cache(cache),
        })
        tokens.append(selected)
        position += 1

    return steps


def export_trace(path: str, config: ModelConfig, steps: list[dict]) -> None:
    with open(path, "w") as f:
        w(f, "STEPS", len(steps))
        for i, s in enumerate(steps):
            w(f, "STEP", i, s["kind"])
            w(f, "SEQ_BEFORE", len(s["seq_before"]), *s["seq_before"])
            w(f, "NEW_TOKENS", len(s["new_tokens"]), *s["new_tokens"])
            w(f, "START_POSITION", s["start_position"])
            vocab = len(s["logits_last"])
            w(f, "LOGITS_LAST", vocab, *[f"{v:.9g}" for v in s["logits_last"]])
            w(f, "SELECTED", s["selected"])
            for layer_idx, layer_cache in enumerate(s["cache_after"]):
                k = layer_cache["k"]  # [n_kv_heads, cur_len, head_dim]
                v = layer_cache["v"]
                w(f, "CACHE_K", layer_idx, k.ndim, *k.shape)
                f.write(" ".join(f"{val:.9g}" for val in k.flatten(order="C")) + "\n")
                w(f, "CACHE_V", layer_idx, v.ndim, *v.shape)
                f.write(" ".join(f"{val:.9g}" for val in v.flatten(order="C")) + "\n")


def main() -> int:
    import os
    os.makedirs(OUT_DIR, exist_ok=True)
    config, weights = build_model()
    export_weights(f"{OUT_DIR}/fixture_cache_weights.txt", config, weights)
    steps = run_python_cached_trace(config, weights)
    export_trace(f"{OUT_DIR}/fixture_cache_trace_python.txt", config, steps)
    tokens = list(INITIAL_TOKENS) + [s["selected"] for s in steps]
    print(f"Python prefill+{N_DECODE_STEPS}-step cached decode from {INITIAL_TOKENS}:")
    print("  full sequence:", tokens)
    print(f"wrote {OUT_DIR}/fixture_cache_weights.txt and {OUT_DIR}/fixture_cache_trace_python.txt")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
