# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Artifact manifest writer, per PHASE_0_REFERENCE_ORACLE.md "Artifact
manifest" schema:

  schema_version, model{source_url, source_revision, sha256, license},
  gguf{converter_source, converter_revision, command, sha256},
  tokenizer{files: [{path, sha256}]},
  oracle{implementation, version_or_commit, environment_lock_sha256},
  fixture{id, input_bytes_sha256, rendered_prompt_sha256, token_ids_sha256},
  numeric{dtype, tolerance_profile}

For the current synthetic-only fixtures (Profile A / OE-L0-SYNTH-1), the
model/gguf/tokenizer sections are honestly "not_applicable: synthetic
profile, no real model/GGUF/tokenizer" rather than fabricated values --
those sections become real once Fixture D (the real-model candidate) is
built. tensor_artifacts uses artifact_record.TensorArtifactRecord so every
retained tensor has name, dtype, logical shape, byte strides, layout,
endianness, and hash, per the acceptance check's own wording.
"""
from __future__ import annotations

import hashlib
import os
import platform
import subprocess
from dataclasses import dataclass, field
from typing import Any

import numpy as np
import yaml

from oracle.artifact_record import TensorArtifactRecord, build_tensor_artifact_record

SCHEMA_VERSION = 1
_REQUIREMENTS_PATH = os.path.join(os.path.dirname(__file__), "..", "requirements.txt")


def _sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _git_commit() -> str:
    """Real commit SHA at manifest-generation time, not a 'see git log' placeholder --
    this is what actually binds a manifest's tensor checksums to the exact oracle/model.py
    revision that generated them (CodeRabbit finding, PR #102)."""
    try:
        out = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True,
                              cwd=os.path.dirname(__file__), timeout=10)
        sha = out.stdout.strip()
        if out.returncode == 0 and sha:
            dirty = subprocess.run(["git", "status", "--porcelain"], capture_output=True, text=True,
                                    cwd=os.path.dirname(__file__), timeout=10).stdout.strip()
            return f"{sha}{'-dirty' if dirty else ''}"
    except Exception:
        pass
    return "unknown (git rev-parse failed)"


def _environment_lock_sha256() -> str:
    """SHA-256 of requirements.txt's exact pinned-dependency content -- the environment-lock
    identity this manifest's oracle results depend on."""
    try:
        with open(_REQUIREMENTS_PATH, "rb") as f:
            return _sha256_bytes(f.read())
    except Exception:
        return "unknown (requirements.txt unreadable)"


@dataclass
class OracleManifest:
    fixture_id: str
    seed: int
    token_ids: np.ndarray
    tolerance_profile: str
    tensor_artifacts: list[TensorArtifactRecord] = field(default_factory=list)

    def to_dict(self) -> dict[str, Any]:
        token_ids_bytes = np.ascontiguousarray(self.token_ids).tobytes()
        return {
            "schema_version": SCHEMA_VERSION,
            "model": {
                "not_applicable": True,
                "reason": "synthetic profile OE-L0-SYNTH-1, no real model source",
            },
            "gguf": {
                "not_applicable": True,
                "reason": "synthetic profile OE-L0-SYNTH-1, no GGUF conversion involved",
            },
            "tokenizer": {
                "not_applicable": True,
                "reason": "Profile A consumes raw token IDs directly, no tokenizer exists",
            },
            "oracle": {
                "implementation": "Tools/OrcEnginePhase0/oracle/model.py (NumPy reference)",
                "version_or_commit": _git_commit(),
                "environment_lock_sha256": _environment_lock_sha256(),
                "environment": {
                    "python": platform.python_version(),
                    "numpy": np.__version__,
                    "platform": platform.platform(),
                },
            },
            "fixture": {
                "id": self.fixture_id,
                "seed": self.seed,
                "token_ids": self.token_ids.tolist(),
                "token_ids_sha256": _sha256_bytes(token_ids_bytes),
            },
            "numeric": {
                "dtype": "float32",
                "tolerance_profile": self.tolerance_profile,
            },
            "tensor_artifacts": [t.to_dict() for t in self.tensor_artifacts],
        }

    def to_yaml(self) -> str:
        return yaml.safe_dump(self.to_dict(), sort_keys=False, default_flow_style=False)


REQUIRED_TOP_LEVEL_KEYS = {
    "schema_version", "model", "gguf", "tokenizer", "oracle", "fixture", "numeric", "tensor_artifacts",
}
REQUIRED_TENSOR_RECORD_KEYS = {
    "name", "dtype", "logical_shape", "byte_strides", "layout", "endianness", "sha256",
}
REQUIRED_ORACLE_KEYS = {"implementation", "version_or_commit", "environment_lock_sha256"}
_PLACEHOLDER_MARKERS = ("see git log", "unknown")


def validate_manifest_dict(d: dict) -> list[str]:
    """Returns a list of problems; empty list means the manifest is schema-complete."""
    problems = []
    missing_top = REQUIRED_TOP_LEVEL_KEYS - set(d.keys())
    if missing_top:
        problems.append(f"missing top-level keys: {sorted(missing_top)}")

    oracle = d.get("oracle", {})
    missing_oracle = REQUIRED_ORACLE_KEYS - set(oracle.keys())
    if missing_oracle:
        problems.append(f"oracle section missing keys: {sorted(missing_oracle)}")
    for key in ("version_or_commit", "environment_lock_sha256"):
        val = str(oracle.get(key, ""))
        if any(marker in val for marker in _PLACEHOLDER_MARKERS):
            problems.append(f"oracle.{key} is a placeholder, not an immutable value: {val!r}")

    for i, t in enumerate(d.get("tensor_artifacts", [])):
        missing = REQUIRED_TENSOR_RECORD_KEYS - set(t.keys())
        if missing:
            problems.append(f"tensor_artifacts[{i}] ('{t.get('name', '?')}') missing keys: {sorted(missing)}")
    return problems
