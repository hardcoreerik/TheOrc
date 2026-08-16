# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Comparison record producer, per the "Comparison record" section of
docs/OrcEngine/PHASE_0_REFERENCE_ORACLE.md:

  name, layer, position, dtype, shape, and strides; expected and actual
  hashes; max absolute error and index; max relative error and index;
  mean absolute error; NaN and infinity counts; optional cosine
  similarity; pass/fail under the named tolerance profile.

Integer, byte, shape, metadata, and token-ID comparisons require exact
equality -- see compare_exact().
"""
from __future__ import annotations

import hashlib
from dataclasses import dataclass, field
from typing import Any

import numpy as np


def _sha256(arr: np.ndarray) -> str:
    return hashlib.sha256(np.ascontiguousarray(arr).tobytes()).hexdigest()


@dataclass
class ComparisonRecord:
    name: str
    layer: int | None
    position: int | None
    dtype: str
    shape: tuple[int, ...]
    strides: tuple[int, ...]
    expected_hash: str
    actual_hash: str
    max_abs_error: float
    max_abs_error_index: tuple[int, ...] | None
    max_rel_error: float
    max_rel_error_index: tuple[int, ...] | None
    mean_abs_error: float
    nan_count: int
    inf_count: int
    cosine_similarity: float | None
    tolerance_profile: str
    passed: bool
    reason: str = ""
    extra: dict[str, Any] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        d = dict(self.__dict__)
        d["shape"] = list(d["shape"])
        d["strides"] = list(d["strides"])
        if d["max_abs_error_index"] is not None:
            d["max_abs_error_index"] = list(d["max_abs_error_index"])
        if d["max_rel_error_index"] is not None:
            d["max_rel_error_index"] = list(d["max_rel_error_index"])
        return d


def compare_tolerant(
    name: str,
    expected: np.ndarray,
    actual: np.ndarray,
    *,
    atol: float,
    rtol: float,
    tolerance_profile: str,
    layer: int | None = None,
    position: int | None = None,
) -> ComparisonRecord:
    expected = np.asarray(expected)
    actual = np.asarray(actual)

    if expected.shape != actual.shape:
        return ComparisonRecord(
            name=name, layer=layer, position=position,
            dtype=str(actual.dtype), shape=actual.shape, strides=actual.strides,
            expected_hash=_sha256(expected), actual_hash=_sha256(actual),
            max_abs_error=float("nan"), max_abs_error_index=None,
            max_rel_error=float("nan"), max_rel_error_index=None,
            mean_abs_error=float("nan"),
            nan_count=int(np.isnan(actual).sum()) if np.issubdtype(actual.dtype, np.floating) else 0,
            inf_count=int(np.isinf(actual).sum()) if np.issubdtype(actual.dtype, np.floating) else 0,
            cosine_similarity=None, tolerance_profile=tolerance_profile, passed=False,
            reason=f"shape mismatch: expected {expected.shape}, actual {actual.shape}",
        )

    abs_err = np.abs(actual.astype(np.float64) - expected.astype(np.float64))
    denom = np.maximum(np.abs(expected.astype(np.float64)), 1e-12)
    rel_err = abs_err / denom

    max_abs_idx = tuple(int(i) for i in np.unravel_index(np.argmax(abs_err), abs_err.shape))
    max_rel_idx = tuple(int(i) for i in np.unravel_index(np.argmax(rel_err), rel_err.shape))

    nan_count = int(np.isnan(actual).sum())
    inf_count = int(np.isinf(actual).sum())

    exp_f = expected.astype(np.float64).ravel()
    act_f = actual.astype(np.float64).ravel()
    denom_cos = (np.linalg.norm(exp_f) * np.linalg.norm(act_f))
    cosine = float(np.dot(exp_f, act_f) / denom_cos) if denom_cos > 0 else None

    within_tol = bool(np.all(abs_err <= (atol + rtol * np.abs(expected.astype(np.float64)))))
    passed = within_tol and nan_count == 0 and inf_count == 0

    return ComparisonRecord(
        name=name, layer=layer, position=position,
        dtype=str(actual.dtype), shape=actual.shape, strides=actual.strides,
        expected_hash=_sha256(expected), actual_hash=_sha256(actual),
        max_abs_error=float(abs_err.max()), max_abs_error_index=max_abs_idx,
        max_rel_error=float(rel_err.max()), max_rel_error_index=max_rel_idx,
        mean_abs_error=float(abs_err.mean()),
        nan_count=nan_count, inf_count=inf_count,
        cosine_similarity=cosine, tolerance_profile=tolerance_profile, passed=passed,
        reason="" if passed else f"max_abs_error={abs_err.max():.3e} exceeds atol/rtol bound",
    )


def compare_exact(
    name: str,
    expected: np.ndarray,
    actual: np.ndarray,
    *,
    layer: int | None = None,
    position: int | None = None,
) -> ComparisonRecord:
    """Exact equality -- for integer/byte/shape/metadata/token-ID comparisons."""
    expected = np.asarray(expected)
    actual = np.asarray(actual)
    shapes_match = expected.shape == actual.shape
    passed = shapes_match and bool(np.array_equal(expected, actual))
    return ComparisonRecord(
        name=name, layer=layer, position=position,
        dtype=str(actual.dtype), shape=actual.shape, strides=actual.strides,
        expected_hash=_sha256(expected), actual_hash=_sha256(actual),
        max_abs_error=0.0 if passed else float("inf"), max_abs_error_index=None,
        max_rel_error=0.0 if passed else float("inf"), max_rel_error_index=None,
        mean_abs_error=0.0 if passed else float("inf"),
        nan_count=0, inf_count=0, cosine_similarity=None,
        tolerance_profile="exact", passed=passed,
        reason="" if passed else "exact comparison mismatch",
    )
