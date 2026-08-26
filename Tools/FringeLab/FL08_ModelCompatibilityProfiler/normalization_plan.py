# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
FL-08 Gate 6/7: normalization plan generator.

Given a compatibility profile JSON produced by profiler.py, produces a
PROPOSED normalization plan (never applied -- this module contains no
code path that writes or transforms any artifact). The plan is only
emitted when the profile's runtime_compatibility.result is
VERIFIED_NORMALIZATION_REQUIRED AND the profile itself validates as
well-formed (schema version recognized, required fields present, no
unresolved ambiguity, pair identity verified when a reference was
used); any other result, or a malformed/ambiguous/unverified profile,
produces no plan.

Round-2 remediation (Codex/Grok review): the raw-to-canonical Q/K
permutation is NOT self-inverse (confirmed empirically in Phase 6 and
in this module's own tests) -- applying the SAME operation names in
both directions was wrong. This module now emits a plan ONLY for the
proven FORWARD direction (raw-HF -> canonical llama.cpp, the exact,
independently-verified permute() transform). For the REVERSE direction
(canonical -> raw-HF, needed when the artifact is canonical but the
target is orcengine-current), this module deliberately emits NO plan
-- a genuine inverse formula has not been derived or round-trip-tested
in this experiment, and inventing one to fill the gap would be exactly
the kind of unproven claim this profiler exists to avoid making.

Usage:
    python normalization_plan.py PROFILE.json [--out PLAN.json]
"""
from __future__ import annotations

import argparse
import json
import sys

SUPPORTED_PROFILE_SCHEMA_VERSIONS = (2,)

_REQUIRED_TOP_LEVEL_FIELDS = (
    "schema_version", "artifact", "qk_layout", "pair_identity", "runtime_compatibility",
    "confidence_level", "unresolved_ambiguities", "execution_authorization",
)


def validate_profile(profile: dict) -> str | None:
    """Returns an error string if the profile is malformed/unusable as
    input to plan-building, or None if it is well-formed enough to
    proceed. Deliberately narrow -- this is not a general JSON-schema
    validator, only the checks build_plan actually depends on."""
    if not isinstance(profile, dict):
        return "profile is not a JSON object"
    missing = [f for f in _REQUIRED_TOP_LEVEL_FIELDS if f not in profile]
    if missing:
        return f"profile missing required field(s): {missing}"
    if profile["schema_version"] not in SUPPORTED_PROFILE_SCHEMA_VERSIONS:
        return (f"profile schema_version={profile['schema_version']!r} not supported by this plan "
                f"generator (supports {SUPPORTED_PROFILE_SCHEMA_VERSIONS!r}) -- refusing to interpret "
                f"an unrecognized schema rather than guessing field meanings")
    if not profile["artifact"].get("sha256"):
        return "profile's artifact.sha256 is missing/empty -- cannot bind a plan to an unidentified artifact"
    return None


def build_plan(profile: dict) -> tuple[dict | None, str | None]:
    """Returns (plan, refusal_reason). Exactly one is None."""
    schema_error = validate_profile(profile)
    if schema_error:
        return None, f"profile failed validation: {schema_error}"

    if profile["runtime_compatibility"]["result"] != "VERIFIED_NORMALIZATION_REQUIRED":
        return None, (f"profile's runtime_compatibility.result is "
                      f"{profile['runtime_compatibility']['result']!r}, not "
                      f"VERIFIED_NORMALIZATION_REQUIRED -- nothing to propose")

    if profile["pair_identity"]["status"] != "VERIFIED":
        return None, ("profile's pair_identity.status is not VERIFIED -- refusing to propose a plan "
                      "derived from a Q/K relationship whose underlying same-model pairing was not "
                      "itself confirmed")

    if profile["unresolved_ambiguities"]:
        return None, (f"profile carries {len(profile['unresolved_ambiguities'])} unresolved "
                      f"ambiguit(y/ies) -- refusing to propose a plan from an incomplete profile")

    qk = profile["qk_layout"]
    target = profile["runtime_compatibility"]["target"]

    if qk["classification"] == "RAW_HF" and target == "canonical-llama.cpp" and \
       qk["confidence"] == "NUMERICALLY_VERIFIED":
        source_layout, target_layout = "hf_raw_rotate_half", "llama_cpp_interleaved_rope"
    elif qk["classification"] == "CANONICAL_LLAMA_CPP" and target == "orcengine-current":
        return None, ("the required transform is canonical -> raw-HF (the REVERSE of the proven "
                      "permute() direction). A genuine inverse formula has not been derived or "
                      "round-trip-tested in this experiment -- emitting no plan rather than inventing "
                      "an unproven inverse operation")
    else:
        return None, (f"no named plan for classification={qk['classification']!r} target={target!r} "
                      f"confidence={qk['confidence']!r} -- fail closed rather than guess")

    n_layers = qk["layers_total"]
    if n_layers <= 0:
        return None, f"profile reports layers_total={n_layers} -- refusing to build a plan over zero layers"

    return {
        "schema_version": 1,
        "source_artifact_sha256": profile["artifact"]["sha256"],
        "reference_artifact_sha256": (profile.get("reference") or {}).get("sha256"),
        "source_layout": source_layout,
        "target_layout": target_layout,
        "required_operations": [
            {
                "operation": "permute_q_projection",
                "layers": f"0-{n_layers - 1}",
                "evidence": f"Q/K fingerprint check verified on {qk['qk_tensors_checked']}/"
                            f"{qk['qk_tensors_total']} tensors, {qk['layers_checked']}/{qk['layers_total']} "
                            f"layers ({qk['confidence']}); pair identity VERIFIED against reference",
            },
            {
                "operation": "permute_k_projection",
                "layers": f"0-{n_layers - 1}",
                "evidence": f"Q/K fingerprint check verified on {qk['qk_tensors_checked']}/"
                            f"{qk['qk_tensors_total']} tensors, {qk['layers_checked']}/{qk['layers_total']} "
                            f"layers ({qk['confidence']}); pair identity VERIFIED against reference",
            },
        ],
        "destructive": False,
        "produces_new_artifact": True,
        "modifies_source_artifact": False,
        "execution_authorized": False,
    }, None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("profile_json")
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    with open(args.profile_json, encoding="utf-8") as f:
        profile = json.load(f)

    plan, refusal_reason = build_plan(profile)
    if plan is None:
        print(f"No normalization plan: {refusal_reason}")
        return 1

    text = json.dumps(plan, indent=2, sort_keys=True)
    print(text)
    if args.out:
        with open(args.out, "w", encoding="utf-8") as f:
            f.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
