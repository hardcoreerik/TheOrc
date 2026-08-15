# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
artifact_schema_complete acceptance check: generates a real manifest from a
live Fixture C run, writes it to artifacts/, reloads it from disk, and
validates every required top-level and tensor-record field is present.
Round-trips through actual YAML on disk -- not just an in-memory dict.
"""
from __future__ import annotations

import os

import numpy as np
import yaml

from oracle.artifact_record import build_tensor_artifact_record
from oracle.manifest import REQUIRED_TOP_LEVEL_KEYS, OracleManifest, validate_manifest_dict
from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

SEED = 20260814
OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "fixture_c_manifest.yaml")


def run() -> bool:
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=2,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    token_ids = np.array([1, 5, 9, 3, 7], dtype=np.int64)
    result = forward(token_ids, weights, config, capture_taps=True)

    tensor_artifacts = [
        build_tensor_artifact_record("input_embedding", result.taps["input_embedding"]),
        build_tensor_artifact_record("layer_0.pre_attention_normalized_state",
                                      result.taps["layer_0"]["pre_attention_normalized_state"]),
        build_tensor_artifact_record("layer_0.q_after_rope", result.taps["layer_0"]["q_after_rope"]),
        build_tensor_artifact_record("layer_1.post_ffn_residual", result.taps["layer_1"]["post_ffn_residual"]),
        build_tensor_artifact_record("final_normalized_state", result.taps["final_normalized_state"]),
        build_tensor_artifact_record("logits", result.taps["logits"]),
        build_tensor_artifact_record("token_embedding_weight", weights.token_embedding),
    ]

    manifest = OracleManifest(
        fixture_id="OE-L0-SYNTH-1-fixtureC-2026-08-15",
        seed=SEED,
        token_ids=token_ids,
        tolerance_profile="fixture-c-fp32 (atol=1e-6, rtol=1e-5)",
        tensor_artifacts=tensor_artifacts,
    )

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        f.write(manifest.to_yaml())
    print(f"wrote manifest to {os.path.abspath(OUTPUT_PATH)}")

    with open(OUTPUT_PATH, "r", encoding="utf-8") as f:
        reloaded = yaml.safe_load(f)

    problems = validate_manifest_dict(reloaded)
    if problems:
        print("SCHEMA INCOMPLETE:")
        for p in problems:
            print(f"  - {p}")
        return False

    print(f"schema-complete: all {len(REQUIRED_TOP_LEVEL_KEYS)} top-level sections present, "
          f"{len(reloaded['tensor_artifacts'])} tensor artifacts each with all 7 required fields")
    return True


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: artifact_schema_complete")
    raise SystemExit(0 if ok else 1)
