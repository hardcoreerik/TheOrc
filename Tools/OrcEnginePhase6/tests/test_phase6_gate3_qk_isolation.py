# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Targeted regression tests for phase6_gate3_qk_isolation.py (combined
Codex/Grok remediation round 7, Gate 3). Exercises the permutation
formula and the diff-classification logic against small synthetic
arrays/fakes -- does not require a real GGUF file or llama-server.

Run: python -m unittest Tools/OrcEnginePhase6/tests/test_phase6_gate3_qk_isolation.py -v
"""
from __future__ import annotations

import os
import sys
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import phase6_gate3_qk_isolation as gate3  # noqa: E402


class TestOfficialPermute(unittest.TestCase):
    def test_matches_hand_derived_2head_example(self):
        # n_head=2, head_dim=4 (2*2), 1 output column. Rows grouped as
        # [head0: rows 0-3][head1: rows 4-7]; within each head, permute()
        # reshapes to (2, half=2, ...) and swaps those two axes -- i.e.
        # interleaves the first-half/second-half rows pairwise.
        weights = np.arange(8, dtype=np.float32).reshape(8, 1)
        result = gate3._official_permute(weights.copy(), n_head=2, n_head_kv=2)
        # head0 rows [0,1,2,3] -> reshape(2,2,1) = [[0],[1]],[[2],[3]] ->
        # swapaxes(0,1 within head) -> [[0],[2]],[[1],[3]] -> flat [0,2,1,3]
        # head1 rows [4,5,6,7] -> flat [4,6,5,7]
        expected = np.array([[0], [2], [1], [3], [4], [6], [5], [7]], dtype=np.float32)
        np.testing.assert_array_equal(result, expected)

    def test_n_head_kv_override_when_different(self):
        # n_head=4 but n_head_kv=2 -> function uses n_head_kv=2 as the
        # effective head count for K's reshape (matches conversion/llama.py:
        # "if n_head_kv is not None and n_head != n_head_kv: n_head = n_head_kv").
        weights = np.arange(8, dtype=np.float32).reshape(8, 1)
        via_override = gate3._official_permute(weights.copy(), n_head=4, n_head_kv=2)
        via_direct = gate3._official_permute(weights.copy(), n_head=2, n_head_kv=2)
        np.testing.assert_array_equal(via_override, via_direct)

    def test_permute_is_self_inverse_for_this_shape(self):
        # Applying permute() twice with the same head count returns the
        # original -- a property this project's own artifacts rely on
        # implicitly (the transform is a pure axis swap, not lossy).
        rng = np.random.default_rng(0)
        weights = rng.standard_normal((16, 3)).astype(np.float32)
        once = gate3._official_permute(weights.copy(), n_head=4, n_head_kv=4)
        twice = gate3._official_permute(once.copy(), n_head=4, n_head_kv=4)
        np.testing.assert_array_equal(twice, weights)

    def test_not_identity_in_general(self):
        weights = np.arange(8, dtype=np.float32).reshape(8, 1)
        result = gate3._official_permute(weights.copy(), n_head=2, n_head_kv=2)
        self.assertFalse(np.array_equal(result, weights), "permute() must actually change row order")


class _FakeField:
    def __init__(self, types, value):
        self.types = types
        self._value = value

    def contents(self):
        return self._value


class _FakeReader:
    def __init__(self, fields: dict):
        self.fields = fields


class TestClassifyMetadataDiff(unittest.TestCase):
    def test_identical_fields_classified_correctly(self):
        from gguf import GGUFValueType
        custom = _FakeReader({
            "GGUF.version": _FakeField([GGUFValueType.UINT32], 3),
            "general.architecture": _FakeField([GGUFValueType.STRING], "llama"),
        })
        canon = _FakeReader({
            "GGUF.version": _FakeField([GGUFValueType.UINT32], 3),
            "general.architecture": _FakeField([GGUFValueType.STRING], "llama"),
        })
        report = gate3._classify_metadata_diff(custom, canon)
        self.assertEqual(report["identical_fields"], ["general.architecture"])
        self.assertEqual(report["differing_fields"], [])
        self.assertEqual(report["only_in_existing_custom"], [])
        self.assertEqual(report["only_in_canonical"], [])

    def test_differing_and_only_on_one_side_classified_correctly(self):
        from gguf import GGUFValueType
        custom = _FakeReader({
            "general.name": _FakeField([GGUFValueType.STRING], "SmolLM2-135M"),
            "custom_only_field": _FakeField([GGUFValueType.STRING], "x"),
        })
        canon = _FakeReader({
            "general.name": _FakeField([GGUFValueType.STRING], "Smollm2 135m"),
            "canon_only_field": _FakeField([GGUFValueType.STRING], "y"),
        })
        report = gate3._classify_metadata_diff(custom, canon)
        self.assertEqual(report["only_in_existing_custom"], ["custom_only_field"])
        self.assertEqual(report["only_in_canonical"], ["canon_only_field"])
        self.assertEqual([k for k, _, _ in report["differing_fields"]], ["general.name"])

    def test_internal_header_fields_excluded_from_report(self):
        from gguf import GGUFValueType
        custom = _FakeReader({"GGUF.tensor_count": _FakeField([GGUFValueType.UINT64], 5)})
        canon = _FakeReader({"GGUF.tensor_count": _FakeField([GGUFValueType.UINT64], 7)})
        report = gate3._classify_metadata_diff(custom, canon)
        # A real internal-count mismatch must NOT surface as a metadata
        # "difference" -- it is header bookkeeping the writer recomputes.
        self.assertEqual(report["identical_fields"], [])
        self.assertEqual(report["differing_fields"], [])
        self.assertEqual(report["only_in_existing_custom"], [])
        self.assertEqual(report["only_in_canonical"], [])


if __name__ == "__main__":
    unittest.main()
