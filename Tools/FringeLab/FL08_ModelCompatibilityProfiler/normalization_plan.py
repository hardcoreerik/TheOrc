# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
FL-08 Gate 6/7: normalization plan generator.

Given a compatibility profile JSON produced by profiler.py, produces a
PROPOSED normalization plan (never applied -- this module contains no
code path that writes or transforms any artifact). The plan is only
emitted when the profile's layout_compatibility.result is
VERIFIED_LAYOUT_NORMALIZATION_REQUIRED AND the profile itself
validates as well-formed and internally consistent; any other result,
or a malformed/ambiguous/unverified/incomplete profile, produces no
plan.

Round-7 remediation (FL-08 closeout): a proposed plan requires ONLY
layout evidence (pair identity verified, Q/K numerically verified and
complete) -- it deliberately does NOT require
`profile["execution_authorization"]` to be true. Layout normalization
is a useful, disclosed research result on its own; requiring an
authorization bit that this prototype can never set (execution
authorization also requires runtime admission, which this experiment
never evaluates) would silently erase that result. The emitted plan's
own `execution_authorized` field is unconditionally `False` -- this
module proposes, it never authorizes or applies.

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

Round-4 remediation (Codex authority review): three gaps in the
round-3 validator were found and closed:
  1. Python `bool` is a subtype of `int`, so `isinstance(True, int)`
     is True -- `layers_total=True`/`layers_checked=True` previously
     passed integer-type validation. `_get()` now rejects `bool` values
     wherever `int` is the expected type.
  2. A reference whose `container.valid` is `False` (or whose declared
     architecture is unsupported) but whose `terminal_result` happens
     to be `null` could previously still produce a plan -- validity was
     checked via `terminal_result` alone. Both the primary artifact's
     and the reference's `container.valid`/`declared_architecture` are
     now explicitly checked.
  3. `artifact.sha256`/`reference.sha256` were only checked for
     "non-empty string" -- an arbitrary string like `"x"` passed. Both
     are now required to match the exact SHA-256 hex-digest shape
     (64 hex characters).

Usage:
    python normalization_plan.py PROFILE.json [--out PLAN.json]
"""
from __future__ import annotations

import argparse
import json
import re
import sys

SUPPORTED_PROFILE_SCHEMA_VERSIONS = (3,)

_REQUIRED_TOP_LEVEL_FIELDS = (
    "schema_version", "artifact", "container", "declared_architecture", "qk_layout", "pair_identity",
    "reference", "layout_compatibility", "confidence_level", "unresolved_ambiguities",
    "execution_authorization",
)

_SHA256_HEX_RE = re.compile(r"^[0-9a-fA-F]{64}$")


def _get(d, key, expected_type=None):
    """Safe nested-field accessor: returns (value, error). Never
    raises on a missing key, wrong container type, or wrong value
    type -- every malformed-profile path this module must handle.
    `bool` is REJECTED whenever `expected_type` is `int` (or a tuple
    containing `int` but not `bool`) -- `bool` is a Python subtype of
    `int`, so `isinstance(True, int)` is otherwise silently True."""
    if not isinstance(d, dict):
        return None, f"expected a JSON object, got {type(d).__name__}"
    if key not in d:
        return None, f"missing required field {key!r}"
    value = d[key]
    if expected_type is not None:
        types = expected_type if isinstance(expected_type, tuple) else (expected_type,)
        if int in types and bool not in types and isinstance(value, bool):
            return None, f"field {key!r} is a bool, not an int (bool is a Python int subtype -- rejected)"
        if not isinstance(value, expected_type):
            return None, f"field {key!r} has type {type(value).__name__}, expected {expected_type}"
    return value, None


def _valid_sha256(value) -> bool:
    # Round 5: `.match()` with `^...$` anchors is NOT an exact-length
    # check -- Python's `$` matches immediately before a trailing
    # "\n", so `.match()` accepts 64 hex chars followed by a newline.
    # `.fullmatch()` has no such exception.
    return isinstance(value, str) and bool(_SHA256_HEX_RE.fullmatch(value))


def _validate_artifact_record(record, label: str) -> str | None:
    """Shared check for BOTH the primary artifact's top-level profile
    dict and the nested `reference` dict: container must be valid, and
    the declared architecture must be the one this experiment supports
    -- checked directly, not inferred from `terminal_result` alone
    (round-4 remediation, gap 2)."""
    container, err = _get(record, "container", dict)
    if err:
        return f"{label}.container: {err}"
    valid, err = _get(container, "valid", bool)
    if err:
        return f"{label}.container.valid: {err}"
    if not valid:
        return f"{label}.container.valid is False -- not a structurally valid GGUF container"
    arch, err = _get(record, "declared_architecture", str)
    if err:
        return f"{label}.declared_architecture: {err}"
    if arch != "llama":
        return f"{label}.declared_architecture={arch!r} -- this experiment only supports 'llama'"
    return None


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
    if err or not _valid_sha256(sha):
        return (f"profile's artifact.sha256={sha!r} is not a well-formed 64-character hex SHA-256 digest "
                f"-- cannot bind a plan to an unidentified artifact")

    artifact_err = _validate_artifact_record(profile, "profile")
    if artifact_err:
        return artifact_err

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
    if err or not _valid_sha256(ref_sha):
        return (f"profile's reference.sha256={ref_sha!r} is not a well-formed 64-character hex SHA-256 "
                f"digest")
    ref_terminal, err = _get(reference, "terminal_result")
    if err:
        return f"reference.terminal_result: {err}"
    if ref_terminal is not None:
        return f"reference failed validation (terminal_result={ref_terminal!r}) -- not structurally valid"
    reference_err = _validate_artifact_record(reference, "reference")
    if reference_err:
        return reference_err

    layout, err = _get(profile, "layout_compatibility", dict)
    if err:
        return f"layout_compatibility: {err}"
    for key in ("result", "target"):
        _, err = _get(layout, key, str)
        if err:
            return f"layout_compatibility.{key}: {err}"

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
    layout = profile["layout_compatibility"]

    if layout["result"] != "VERIFIED_LAYOUT_NORMALIZATION_REQUIRED":
        return None, (f"profile's layout_compatibility.result is {layout['result']!r}, not "
                      f"VERIFIED_LAYOUT_NORMALIZATION_REQUIRED -- nothing to propose")

    if pair_identity["status"] != "VERIFIED":
        return None, ("profile's pair_identity.status is not exactly VERIFIED -- refusing to propose a "
                      "plan derived from a Q/K relationship whose underlying same-model pairing was not "
                      "itself confirmed")

    if profile["unresolved_ambiguities"]:
        return None, (f"profile carries {len(profile['unresolved_ambiguities'])} unresolved "
                      f"ambiguit(y/ies) -- refusing to propose a plan from an incomplete profile")

    if qk["classification"] == "CANONICAL_LLAMA_CPP" and layout["target"] == "orcengine-current":
        return None, ("the required transform is canonical -> raw-HF (the REVERSE of the proven "
                      "permute() direction). A genuine inverse formula has not been derived or "
                      "round-trip-tested in this experiment -- emitting no plan rather than inventing "
                      "an unproven inverse operation")

    if qk["classification"] != "RAW_HF" or layout["target"] != "canonical-llama.cpp" or \
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
