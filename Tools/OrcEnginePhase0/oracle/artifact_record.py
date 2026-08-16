# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Tensor artifact records for the manifest schema in
PHASE_0_REFERENCE_ORACLE.md: "Every retained tensor artifact also records
byte strides, logical layout, endianness, and contiguous/non-contiguous
status. Shape alone is insufficient to diagnose transposition and view
errors."

Distinct from comparison.ComparisonRecord (which is a pass/fail comparison
between two arrays) -- this is a description of ONE array as a retained
artifact, independent of whether it's being compared to anything.
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass

import numpy as np


def _sha256(arr: np.ndarray) -> str:
    return hashlib.sha256(np.ascontiguousarray(arr).tobytes()).hexdigest()


def _endianness(arr: np.ndarray) -> str:
    # numpy dtype.byteorder: '=' native, '<' little, '>' big, '|' not applicable (e.g. int8)
    code = arr.dtype.byteorder
    if code == "|":
        return "not-applicable"
    if code == "=":
        return "little" if np.little_endian else "big"  # native resolved to actual machine order
    return {"<": "little", ">": "big"}[code]


@dataclass(frozen=True)
class TensorArtifactRecord:
    name: str
    dtype: str
    logical_shape: tuple[int, ...]
    byte_strides: tuple[int, ...]
    layout: str          # "C-contiguous" | "F-contiguous" | "non-contiguous"
    endianness: str       # "little" | "big" | "not-applicable"
    sha256: str

    def to_dict(self) -> dict:
        return {
            "name": self.name,
            "dtype": self.dtype,
            "logical_shape": list(self.logical_shape),
            "byte_strides": list(self.byte_strides),
            "layout": self.layout,
            "endianness": self.endianness,
            "sha256": self.sha256,
        }


def build_tensor_artifact_record(name: str, arr: np.ndarray) -> TensorArtifactRecord:
    if arr.flags["C_CONTIGUOUS"]:
        layout = "C-contiguous"
    elif arr.flags["F_CONTIGUOUS"]:
        layout = "F-contiguous"
    else:
        layout = "non-contiguous"
    return TensorArtifactRecord(
        name=name,
        dtype=str(arr.dtype),
        logical_shape=arr.shape,
        byte_strides=arr.strides,
        layout=layout,
        endianness=_endianness(arr),
        sha256=_sha256(arr),
    )
