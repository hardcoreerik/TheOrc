# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Exports a tiny deterministic transformer fixture (weights + every intermediate
tap + final logits) to a flat, dependency-free text format the Phase-1 C++
differential test harness reads with plain std::ifstream token parsing --
no JSON library, no external dependency, by design (see
docs/OrcEngine/PHASE1_IMPLEMENTATION.md).

Reuses oracle/fixture_c.py's exact dimensions (the trusted, already-verified
Phase-0 Fixture C shape) rather than inventing a new mathematical target, per
the Phase-1 steering document's explicit preference.

Format (whitespace-delimited tokens, one logical record per line group):

    CONFIG vocab hidden intermediate n_layers n_q_heads n_kv_heads head_dim
           max_positions rmsnorm_epsilon rope_theta
    TIED 0|1
    SEQ <n>
    TOKENS t0 t1 ... t(n-1)
    TENSOR <name> <ndims> <d0> [<d1> ...]
    <flattened row-major float32 values, space-separated>
    ... (repeated TENSOR blocks for weights, grouped under LAYER markers)
    EXPECT <name> <ndims> <d0> [<d1> ...]
    <flattened row-major float32 values>
    ... (repeated EXPECT blocks -- every oracle tap plus final logits/argmax)

Two fixtures are written: one tied (lm_head reuses token_embedding) and one
untied (independent lm_head), both required by the Phase-1 completion gate.
"""
from __future__ import annotations

import dataclasses
import sys

import numpy as np

sys.path.insert(0, ".")
from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

OUT_DIR = "fixtures_phase1"


def w(f, *tokens):
    f.write(" ".join(str(t) for t in tokens) + "\n")


def write_tensor(f, name: str, arr: np.ndarray):
    arr = np.asarray(arr, dtype=np.float32)
    w(f, "TENSOR", name, arr.ndim, *arr.shape)
    f.write(" ".join(f"{v:.9g}" for v in arr.flatten(order="C")) + "\n")


def write_expect(f, name: str, arr) -> None:
    arr = np.asarray(arr)
    if arr.dtype.kind in "iu":
        w(f, "EXPECT_INT", name, arr.ndim, *arr.shape)
        f.write(" ".join(str(int(v)) for v in arr.flatten(order="C")) + "\n")
    else:
        arr = arr.astype(np.float32)
        w(f, "EXPECT", name, arr.ndim, *arr.shape)
        f.write(" ".join(f"{v:.9g}" for v in arr.flatten(order="C")) + "\n")


def export_one(path: str, tied: bool) -> None:
    vocab, hidden, intermediate = 32, 16, 32
    n_layers, n_q_heads, n_kv_heads, head_dim = 2, 4, 2, 4
    max_positions = 16
    config = ModelConfig(vocab=vocab, hidden=hidden, intermediate=intermediate,
                          n_layers=n_layers, n_q_heads=n_q_heads, n_kv_heads=n_kv_heads,
                          head_dim=head_dim, max_positions=max_positions)
    weights = build_weights(seed=20260815, vocab=vocab, hidden=hidden, intermediate=intermediate,
                             n_layers=n_layers, n_q_heads=n_q_heads, n_kv_heads=n_kv_heads,
                             head_dim=head_dim)
    if not tied:
        rng = np.random.default_rng(31415926)
        independent_lm_head = (rng.standard_normal(size=(vocab, hidden)) * 0.02).astype(np.float32)
        weights = dataclasses.replace(weights, lm_head=independent_lm_head)

    token_ids = np.array([1, 5, 9, 3], dtype=np.int64)
    result = forward(weights=weights, config=config, token_ids=token_ids, capture_taps=True)

    with open(path, "w") as f:
        w(f, "CONFIG", vocab, hidden, intermediate, n_layers, n_q_heads, n_kv_heads, head_dim,
          max_positions, f"{config.rmsnorm_epsilon:.9g}", f"{config.rope_theta:.9g}")
        w(f, "TIED", 1 if tied else 0)
        w(f, "SEQ", len(token_ids))
        w(f, "TOKENS", *[int(t) for t in token_ids])

        write_tensor(f, "token_embedding", weights.token_embedding)
        if not tied:
            write_tensor(f, "lm_head", weights.lm_head)
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

        write_expect(f, "input_embedding", result.taps["input_embedding"])
        for li in range(n_layers):
            lt = result.taps[f"layer_{li}"]
            prefix = f"layer{li}."
            for key in ("pre_attention_normalized_state", "q_projection", "k_projection",
                        "v_projection", "q_after_rope", "k_after_rope",
                        "attention_probabilities", "attention_output_before_projection",
                        "attention_output_after_projection", "post_attention_residual",
                        "pre_ffn_normalized_state", "gate_projection", "up_projection",
                        "activated_gated_product", "down_projection", "post_ffn_residual"):
                write_expect(f, prefix + key, lt[key])
        write_expect(f, "final_normalized_state", result.taps["final_normalized_state"])
        write_expect(f, "logits", result.taps["logits"])
        write_expect(f, "selected_token", result.taps["selected_token"])

    print(f"wrote {path} (tied={tied})")


def main() -> int:
    import os
    os.makedirs(OUT_DIR, exist_ok=True)
    export_one(f"{OUT_DIR}/fixture_tied.txt", tied=True)
    export_one(f"{OUT_DIR}/fixture_untied.txt", tied=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
