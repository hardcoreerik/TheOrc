# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Fixture B -- synthetic one-layer model, per PHASE_0_REFERENCE_ORACLE.md:
"Deterministic seeded weights, tiny vocabulary, tiny embedding, one layer,
grouped-query attention if applicable, and a short token sequence. All
tensors are small enough to inspect."

Uses Profile A (OE-L0-SYNTH-1) dimensions from
PHASE_0_ARCHITECTURE_PROFILE.md, with n_layers overridden to 1 for this
fixture (the pinned profile's own layer count is 2 -- that's Fixture C's
job, once KV-cache incremental-decode equivalence is added).

This script does NOT by itself satisfy the synthetic_layer_taps acceptance
check -- it proves the forward pass runs and captures every required tap
point, and gives same-process determinism, but the check requires the taps
to "pass named tolerance profiles" against an independent comparison,
which needs either a second independent implementation or fault-injection
proof (PHASE_0_REFERENCE_ORACLE.md's "Fault-injection proof" section).
Both are tracked as separate open work in README.md's status table.
"""
from __future__ import annotations

import numpy as np

from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

SEED = 20260814  # pinned: date this fixture was first generated (2026-08-14), documented per
                  # the "pinned algorithm and seed" requirement -- not a "random" magic number.


def run() -> None:
    config = ModelConfig(
        vocab=32, hidden=16, intermediate=32, n_layers=1,
        n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16,
    )
    weights = build_weights(
        seed=SEED, vocab=config.vocab, hidden=config.hidden,
        intermediate=config.intermediate, n_layers=config.n_layers,
        n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
        head_dim=config.head_dim,
    )
    token_ids = np.array([1, 5, 9, 3], dtype=np.int64)  # short token sequence, seq=4

    result1 = forward(token_ids, weights, config, capture_taps=True)
    result2 = forward(token_ids, weights, config, capture_taps=True)

    # Same-process determinism check (necessary but not sufficient for the
    # deterministic_regeneration acceptance check, which requires two
    # independent clean-environment runs -- see README.md status table).
    logits_match = np.array_equal(result1.logits, result2.logits)
    print(f"same-process determinism (logits bit-identical across two calls): {logits_match}")
    if not logits_match:
        raise SystemExit("FAIL: forward() is not deterministic across repeated calls")

    print(f"\ntoken_ids: {token_ids.tolist()}")
    print(f"logits shape: {result1.logits.shape}  (expected: [seq={len(token_ids)}, vocab={config.vocab}])")
    print(f"selected tokens: {result1.taps['selected_token'].tolist()}")
    print(f"top-token margins: {[f'{m:.6f}' for m in result1.taps['top_token_margin'].tolist()]}")

    print("\nrequired capture points present (per PHASE_0_REFERENCE_ORACLE.md):")
    required = [
        "input_embedding", "pre_attention_normalized_state", "q_projection", "k_projection",
        "v_projection", "q_after_rope", "k_after_rope", "cache_slice_after_write",
        "masked_attention_scores", "attention_probabilities",
        "attention_output_before_projection", "attention_output_after_projection",
        "post_attention_residual", "pre_ffn_normalized_state", "gate_projection",
        "up_projection", "activated_gated_product", "down_projection", "post_ffn_residual",
    ]
    layer0 = result1.taps["layer_0"]
    missing = [name for name in required if name not in layer0 and name != "input_embedding"]
    if "input_embedding" not in result1.taps:
        missing.append("input_embedding")
    top_level_required = ["final_normalized_state", "logits", "selected_token", "top_token_margin"]
    for name in top_level_required:
        if name not in result1.taps:
            missing.append(name)

    for name in required:
        source = result1.taps if name == "input_embedding" else layer0
        present = name in source
        print(f"  [{'x' if present else ' '}] {name}")
    for name in top_level_required:
        present = name in result1.taps
        print(f"  [{'x' if present else ' '}] {name}")

    if missing:
        raise SystemExit(f"FAIL: missing required capture points: {missing}")
    print("\nall required capture points present.")


if __name__ == "__main__":
    run()
