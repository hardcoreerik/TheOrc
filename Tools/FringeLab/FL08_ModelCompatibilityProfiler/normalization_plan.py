# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
FL-08 Gate 6: normalization plan generator.

Given a compatibility profile JSON produced by profiler.py, produces a
PROPOSED normalization plan (never applied -- this module contains no
code path that writes or transforms any artifact). The plan is only
emitted when the profile's runtime_compatibility.result is
VERIFIED_NORMALIZATION_REQUIRED; any other result produces no plan
(there is nothing to normalize, or nothing safe to propose).

Usage:
    python normalization_plan.py PROFILE.json [--out PLAN.json]
"""
from __future__ import annotations

import argparse
import json
import sys


def build_plan(profile: dict) -> dict | None:
    if profile["runtime_compatibility"]["result"] != "VERIFIED_NORMALIZATION_REQUIRED":
        return None

    qk = profile["qk_layout"]
    target = profile["runtime_compatibility"]["target"]

    if qk["classification"] == "RAW_HF" and target == "canonical-llama.cpp":
        source_layout, target_layout = "hf_raw_rotate_half", "llama_cpp_interleaved_rope"
    elif qk["classification"] == "CANONICAL_LLAMA_CPP" and target == "orcengine-current":
        source_layout, target_layout = "llama_cpp_interleaved_rope", "hf_raw_rotate_half"
    else:
        # A normalization-required result this module doesn't have a
        # named plan for yet -- fail closed (no plan) rather than guess
        # at operation names for an axis this prototype doesn't cover.
        return None

    layers_total = qk["layers_total"] // 2  # layers_total counts Q+K tensors combined
    return {
        "schema_version": 1,
        "source_artifact_sha256": profile["artifact"]["sha256"],
        "source_layout": source_layout,
        "target_layout": target_layout,
        "required_operations": [
            {
                "operation": "permute_q_projection",
                "layers": f"0-{layers_total - 1}",
                "evidence": f"Q/K fingerprint check verified on {qk['layers_checked']}/{qk['layers_total']} "
                            f"tensors ({qk['confidence']})",
            },
            {
                "operation": "permute_k_projection",
                "layers": f"0-{layers_total - 1}",
                "evidence": f"Q/K fingerprint check verified on {qk['layers_checked']}/{qk['layers_total']} "
                            f"tensors ({qk['confidence']})",
            },
        ],
        "destructive": False,
        "produces_new_artifact": True,
        "modifies_source_artifact": False,
        "execution_authorized": False,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile_json")
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    with open(args.profile_json, encoding="utf-8") as f:
        profile = json.load(f)

    plan = build_plan(profile)
    if plan is None:
        print("No normalization plan: profile's runtime_compatibility.result is "
              f"{profile['runtime_compatibility']['result']!r}, not VERIFIED_NORMALIZATION_REQUIRED "
              "(or this axis has no named plan yet) -- nothing to propose.")
        return 1

    text = json.dumps(plan, indent=2, sort_keys=True)
    print(text)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
