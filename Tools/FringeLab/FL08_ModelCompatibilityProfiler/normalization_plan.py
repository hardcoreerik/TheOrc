# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
FL-08 Gate 6/7: normalization plan generator.

Given a compatibility profile JSON produced by profiler.py, produces a
PROPOSED normalization plan (never applied -- this module contains no
code path that writes or transforms any artifact). The plan is only
emitted when the profile's runtime_compatibility.result is
VERIFIED_NORMALIZATION_REQUIRED AND the profile itself validates as
well-formed and internally consistent; any other result, or a
malformed/ambiguous/unverified/incomplete profile, produces no plan.

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

Round-3 remediation (Codex authority review + independent Grok Double
Check): `build_plan()` now validates every nested field it actually
consumes -- type, presence, and cross-field consistency (e.g.
qk_tensors_total == layers_total * 2) -- before trusting it, rather
than assuming a well-shaped dict. A malformed nested value returns
`(None, reason)`, never an uncaught exception.

Usage:
    python normalization_plan.py PROFILE.json [--out PLAN.json]
"""
from __future__ import annotations

import argparse
import json
import sys

SUPPORTED_PROFILE_SCHEMA_VERSIONS = (2,)

_REQUIRED_TOP_LEVEL_FIELDS = (
    "schema_version", "artifact", "qk_layout", "pair_identity", "reference", "runtime_compatibility",
    "confidence_level", "unresolved_ambiguities", "execution_authorization",
)


def _get(d, key, expected_type=None):
    """Safe nested-field accessor: returns (value, error). Never
    raises on a missing key, wrong container type, or wrong value
    type -- every malformed-profile path this module must handle."""
    if not isinstance(d, dict):
        return None, f"expected a JSON object, got {type(d).__name__}"
    if key not in d:
        return None, f"missing required field {key!r}"
    value = d[key]
    if expected_type is not None and not isinstance(value, expected_type):
        return None, f"field {key!r} has type {type(value).__name__}, expected {expected_type}"
    return value, None


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

    schema_version, err = _get(profile, "schema_version", int)
    if err:
        return f"schema_version: {err}"
    if schema_version not in SUPPORTED_PROFILE_SCHEMA_VERSIONS:
        return (f"profile schema_version={schema_version!r} not supported by this plan generator "
                f"(supports {SUPPORTED_PROFILE_SCHEMA_VERSIONS!r}) -- refusing to interpret an "
                f"unrecognized schema rather than guessing field meanings")

    artifact, err = _get(profile, "artifact", dict)
    if err:
        return f"artifact: {err}"
    sha, err = _get(artifact, "sha256", str)
    if err or not sha:
        return "profile's artifact.sha256 is missing/empty/malformed -- cannot bind a plan to an " \
               "unidentified artifact"

    qk, err = _get(profile, "qk_layout", dict)
    if err:
        return f"qk_layout: {err}"
    for key, t in (("classification", str), ("confidence", str), ("layers_total", int),
                  ("layers_checked", int), ("qk_tensors_total", int), ("qk_tensors_checked", int),
                  ("per_layer_consistent", bool)):
        _, err = _get(qk, key, t)
        if err:
            return f"qk_layout.{key}: {err}"

    pair_identity, err = _get(profile, "pair_identity", dict)
    if err:
        return f"pair_identity: {err}"
    _, err = _get(pair_identity, "status", str)
    if err:
        return f"pair_identity.status: {err}"

    reference, err = _get(profile, "reference")
    if err:
        return f"reference: {err}"
    if reference is None:
        return "profile.reference is null -- a normalization plan requires a validated paired reference"
    ref_sha, err = _get(reference, "sha256", str)
    if err or not ref_sha:
        return "profile's reference.sha256 is missing/empty/malformed"
    ref_terminal, err = _get(reference, "terminal_result")
    if err:
        return f"reference.terminal_result: {err}"
    if ref_terminal is not None:
        return f"reference failed validation (terminal_result={ref_terminal!r}) -- not structurally valid"

    runtime, err = _get(profile, "runtime_compatibility", dict)
    if err:
        return f"runtime_compatibility: {err}"
    for key in ("result", "target"):
        _, err = _get(runtime, key, str)
        if err:
            return f"runtime_compatibility.{key}: {err}"

    unresolved, err = _get(profile, "unresolved_ambiguities", list)
    if err:
        return f"unresolved_ambiguities: {err}"

    return None


def build_plan(profile: dict) -> tuple[dict | None, str | None]:
    """Returns (plan, refusal_reason). Exactly one is None. Never
    raises -- a malformed/incomplete profile always returns a reason
    string instead."""
    schema_error = validate_profile(profile)
    if schema_error:
        return None, f"profile failed validation: {schema_error}"

    qk = profile["qk_layout"]
    pair_identity = profile["pair_identity"]
    runtime = profile["runtime_compatibility"]

    if runtime["result"] != "VERIFIED_NORMALIZATION_REQUIRED":
        return None, (f"profile's runtime_compatibility.result is {runtime['result']!r}, not "
                      f"VERIFIED_NORMALIZATION_REQUIRED -- nothing to propose")

    if pair_identity["status"] != "VERIFIED":
        return None, ("profile's pair_identity.status is not exactly VERIFIED -- refusing to propose a "
                      "plan derived from a Q/K relationship whose underlying same-model pairing was not "
                      "itself confirmed")

    if profile["unresolved_ambiguities"]:
        return None, (f"profile carries {len(profile['unresolved_ambiguities'])} unresolved "
                      f"ambiguit(y/ies) -- refusing to propose a plan from an incomplete profile")

    if qk["classification"] == "CANONICAL_LLAMA_CPP" and runtime["target"] == "orcengine-current":
        return None, ("the required transform is canonical -> raw-HF (the REVERSE of the proven "
                      "permute() direction). A genuine inverse formula has not been derived or "
                      "round-trip-tested in this experiment -- emitting no plan rather than inventing "
                      "an unproven inverse operation")

    if qk["classification"] != "RAW_HF" or runtime["target"] != "canonical-llama.cpp" or \
       qk["confidence"] != "NUMERICALLY_VERIFIED":
        return None, (f"no named plan for classification={qk['classification']!r} "
                      f"target={runtime['target']!r} confidence={qk['confidence']!r} -- fail closed "
                      f"rather than guess")

    if not qk["per_layer_consistent"]:
        return None, "qk_layout.per_layer_consistent is False -- refusing to propose a plan over an " \
                     "inconsistent Q/K classification"

    layers_total = qk["layers_total"]
    layers_checked = qk["layers_checked"]
    qk_tensors_total = qk["qk_tensors_total"]
    qk_tensors_checked = qk["qk_tensors_checked"]
    if layers_total <= 0:
        return None, f"qk_layout.layers_total={layers_total} -- refusing to build a plan over zero layers"
    if layers_checked != layers_total:
        return None, (f"qk_layout.layers_checked={layers_checked} != layers_total={layers_total} -- "
                      f"refusing to build a plan over an incomplete per-layer check")
    if qk_tensors_total != layers_total * 2:
        return None, (f"qk_layout.qk_tensors_total={qk_tensors_total} != layers_total*2="
                      f"{layers_total * 2} -- internally inconsistent profile, refusing to trust it")
    if qk_tensors_checked != qk_tensors_total:
        return None, (f"qk_layout.qk_tensors_checked={qk_tensors_checked} != "
                      f"qk_tensors_total={qk_tensors_total} -- refusing to build a plan over an "
                      f"incomplete tensor check")

    return {
        "schema_version": 1,
        "source_artifact_sha256": profile["artifact"]["sha256"],
        "reference_artifact_sha256": profile["reference"]["sha256"],
        "source_layout": "hf_raw_rotate_half",
        "target_layout": "llama_cpp_interleaved_rope",
        "required_operations": [
            {
                "operation": "permute_q_projection",
                "layers": f"0-{layers_total - 1}",
                "evidence": f"Q/K fingerprint check verified on {qk_tensors_checked}/{qk_tensors_total} "
                            f"tensors, {layers_checked}/{layers_total} layers ({qk['confidence']}); "
                            f"pair identity VERIFIED against reference",
            },
            {
                "operation": "permute_k_projection",
                "layers": f"0-{layers_total - 1}",
                "evidence": f"Q/K fingerprint check verified on {qk_tensors_checked}/{qk_tensors_total} "
                            f"tensors, {layers_checked}/{layers_total} layers ({qk['confidence']}); "
                            f"pair identity VERIFIED against reference",
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
