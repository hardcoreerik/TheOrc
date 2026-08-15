# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Fixture A -- hand-derived operator microcases, per the "Independent ground
truth" oracle class and "Fixture A: operator microcases" section of
docs/OrcEngine/PHASE_0_REFERENCE_ORACLE.md.

Independence note (documented honestly, not overclaimed): expected values
here come from a *scalar*, math-stdlib reference path (this file) that is
implemented separately from ops.py's vectorized NumPy production path, plus
by-hand arithmetic recorded in the comments below for every case. This is
the "independent ground truth" leg of the three-way oracle. It is NOT yet
the primary semantic oracle (a second, independent NumPy/PyTorch
implementation) or the secondary deployment oracle (pinned llama.cpp) --
those legs are tracked separately and are still open acceptance items
(three_way_oracle_independence in PHASE_0_ACCEPTANCE.yaml is not satisfied
by this file alone).

Every expected array is computed with Python's `math` module in a plain
Python loop -- never by calling into oracle/ops.py -- so the microcase
check is not circular.
"""
from __future__ import annotations

import math

import numpy as np

from oracle import ops
from oracle.comparison import ComparisonRecord, compare_exact, compare_tolerant

ATOL = 1e-6
RTOL = 1e-5
PROFILE = "microcase-fp32-tight"


def _ref_matmul_no_bias(x: list[list[float]], w_out_in: list[list[float]]) -> np.ndarray:
    """Scalar reference for x @ W^T, W stored [out_features, in_features]."""
    rows_out = []
    for row in x:
        out_row = []
        for w_row in w_out_in:
            out_row.append(sum(a * b for a, b in zip(row, w_row)))
        rows_out.append(out_row)
    return np.array(rows_out, dtype=np.float32)


def _ref_rmsnorm(x: list[float], weight: list[float], epsilon: float) -> np.ndarray:
    mean_sq = sum(v * v for v in x) / len(x)
    inv_rms = 1.0 / math.sqrt(mean_sq + epsilon)
    return np.array([v * inv_rms * w for v, w in zip(x, weight)], dtype=np.float32)


def _ref_softmax(x: list[float]) -> np.ndarray:
    m = max(x)
    exps = [math.exp(v - m) for v in x]
    s = sum(exps)
    return np.array([e / s for e in exps], dtype=np.float32)


def _ref_silu(x: list[float]) -> np.ndarray:
    return np.array([v * (1.0 / (1.0 + math.exp(-v))) for v in x], dtype=np.float32)


def _ref_rope_position1_head4() -> np.ndarray:
    """
    By-hand derivation (also cross-checked against this scalar path):
    head_dim=4, theta=10000, position=1, x=[1,0,0,0].
      half=2; freqs = [theta**0, theta**-0.5] = [1.0, 0.01]
      angles = position * freqs = [1.0, 0.01]
      cos = [cos(1.0), cos(0.01), cos(1.0), cos(0.01)]
      sin = [sin(1.0), sin(0.01), sin(1.0), sin(0.01)]
      rotate_half([1,0,0,0]) = [-0, -0, 1, 0] = [0, 0, 1, 0]
      result = x*cos + rotate_half(x)*sin
             = [cos(1.0), 0, 0, 0] + [0, 0, sin(1.0), 0]
             = [cos(1.0), 0, sin(1.0), 0]
             ~= [0.5403023, 0.0, 0.8414710, 0.0]
    """
    return np.array(
        [math.cos(1.0), 0.0, math.sin(1.0), 0.0], dtype=np.float32
    )


def run_all() -> list[ComparisonRecord]:
    records: list[ComparisonRecord] = []

    # 1. matmul / linear_no_bias: x=[[1,2,3]], W=[[1,0,1],[2,1,0]] -> [[4,4]]
    x1 = [[1.0, 2.0, 3.0]]
    w1 = [[1.0, 0.0, 1.0], [2.0, 1.0, 0.0]]
    expected1 = _ref_matmul_no_bias(x1, w1)
    actual1 = ops.linear_no_bias(np.array(x1, dtype=np.float32), np.array(w1, dtype=np.float32))
    records.append(compare_tolerant("matmul_linear_no_bias", expected1, actual1,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 2. RMSNorm: x=[3,4], weight=[1,1], epsilon=1e-5
    #    mean(x^2) = 12.5; inv_rms = 1/sqrt(12.500010) ~= 0.28284232
    #    expected ~= [0.8485269, 1.1313692]
    x2, w2, eps2 = [3.0, 4.0], [1.0, 1.0], 1e-5
    expected2 = _ref_rmsnorm(x2, w2, eps2)
    actual2 = ops.rmsnorm(np.array(x2, dtype=np.float32), np.array(w2, dtype=np.float32), eps2)
    records.append(compare_tolerant("rmsnorm", expected2, actual2,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 3. softmax: x=[1,2,3] -> [0.09003057, 0.24472847, 0.66524096]
    x3 = [1.0, 2.0, 3.0]
    expected3 = _ref_softmax(x3)
    actual3 = ops.softmax_last_axis(np.array(x3, dtype=np.float32))
    records.append(compare_tolerant("softmax", expected3, actual3,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 4. causal_mask: scores=[[1,2],[3,4]] -> [[1,-inf],[3,4]] (exact structural check)
    scores4 = np.array([[1.0, 2.0], [3.0, 4.0]], dtype=np.float32)
    expected4 = np.array([[1.0, float("-inf")], [3.0, 4.0]], dtype=np.float32)
    actual4 = ops.causal_mask(scores4)
    # -inf vs -inf: compare_tolerant's abs-error would be NaN (inf-inf), so treat this
    # as a finite/-inf structural equality check, not a tolerant numeric comparison.
    struct_ok = np.array_equal(np.isneginf(expected4), np.isneginf(actual4)) and np.allclose(
        expected4[~np.isneginf(expected4)], actual4[~np.isneginf(actual4)], atol=ATOL, rtol=RTOL
    )
    records.append(ComparisonRecord(
        name="causal_mask", layer=None, position=None,
        dtype=str(actual4.dtype), shape=actual4.shape, strides=actual4.strides,
        expected_hash="n/a (contains -inf)", actual_hash="n/a (contains -inf)",
        max_abs_error=0.0 if struct_ok else float("inf"), max_abs_error_index=None,
        max_rel_error=0.0 if struct_ok else float("inf"), max_rel_error_index=None,
        mean_abs_error=0.0 if struct_ok else float("inf"),
        nan_count=int(np.isnan(actual4).sum()), inf_count=int(np.isinf(actual4).sum()),
        cosine_similarity=None, tolerance_profile="structural-exact", passed=struct_ok,
        reason="" if struct_ok else "causal mask structure mismatch",
    ))

    # 5. RoPE identity at position 0: any x is unchanged (cos=1, sin=0 everywhere)
    x5 = np.array([1.0, 2.0, 3.0, 4.0], dtype=np.float32)
    cos5, sin5 = ops.rope_cos_sin(position=0, head_dim=4, theta=10000.0)
    expected5 = x5.copy()
    actual5 = ops.apply_rope(x5, cos5, sin5)
    records.append(compare_tolerant("rope_identity_at_position_0", expected5, actual5,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 6. RoPE at position 1, head_dim=4, theta=10000, x=[1,0,0,0] (see derivation above)
    x6 = np.array([1.0, 0.0, 0.0, 0.0], dtype=np.float32)
    cos6, sin6 = ops.rope_cos_sin(position=1, head_dim=4, theta=10000.0)
    expected6 = _ref_rope_position1_head4()
    actual6 = ops.apply_rope(x6, cos6, sin6)
    records.append(compare_tolerant("rope_position_1_head4", expected6, actual6,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 7. SiLU: x=[0,1,-1,2] -> [0, 0.7310586, -0.2689414, 1.7615942]
    x7 = [0.0, 1.0, -1.0, 2.0]
    expected7 = _ref_silu(x7)
    actual7 = ops.silu(np.array(x7, dtype=np.float32))
    records.append(compare_tolerant("silu", expected7, actual7,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 8a. elementwise add: [1,2,3]+[4,5,6] = [5,7,9]
    a8, b8 = np.array([1.0, 2.0, 3.0], dtype=np.float32), np.array([4.0, 5.0, 6.0], dtype=np.float32)
    expected8a = np.array([a + b for a, b in zip([1.0, 2.0, 3.0], [4.0, 5.0, 6.0])], dtype=np.float32)
    actual8a = a8 + b8
    records.append(compare_tolerant("elementwise_add", expected8a, actual8a,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 8b. elementwise multiply: [1,2,3]*[4,5,6] = [4,10,18]
    expected8b = np.array([a * b for a, b in zip([1.0, 2.0, 3.0], [4.0, 5.0, 6.0])], dtype=np.float32)
    actual8b = a8 * b8
    records.append(compare_tolerant("elementwise_multiply", expected8b, actual8b,
                                     atol=ATOL, rtol=RTOL, tolerance_profile=PROFILE))

    # 9. embedding lookup: table 4x2, ids=[2,0,3] -> [[2,2],[0,0],[3,3]] (exact)
    table9 = np.array([[0.0, 0.0], [1.0, 1.0], [2.0, 2.0], [3.0, 3.0]], dtype=np.float32)
    ids9 = np.array([2, 0, 3], dtype=np.int64)
    expected9 = np.array([table9[i].tolist() for i in [2, 0, 3]], dtype=np.float32)
    actual9 = ops.embedding_lookup(table9, ids9)
    records.append(compare_exact("embedding_lookup", expected9, actual9))

    return records


if __name__ == "__main__":
    results = run_all()
    n_pass = sum(1 for r in results if r.passed)
    for r in results:
        status = "PASS" if r.passed else "FAIL"
        print(f"[{status}] {r.name}: max_abs_error={r.max_abs_error:.3e} "
              f"tolerance_profile={r.tolerance_profile} {('- ' + r.reason) if r.reason else ''}")
    print(f"\n{n_pass}/{len(results)} microcases passed")
    raise SystemExit(0 if n_pass == len(results) else 1)
