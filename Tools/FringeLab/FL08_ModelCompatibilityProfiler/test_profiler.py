# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Focused regression tests for FL-08's profiler.py -- the ONE test target
for this research prototype, per the charter's "one focused test
target" instruction. Uses small SYNTHETIC GGUF fixtures generated at
test time (not the real ~500MB Phase 6 artifacts, which are
machine-local and referenced only by hash/path in the experiment
report, never depended on by automated tests).

Run: python -m unittest test_profiler.py -v
"""
from __future__ import annotations

import os
import shutil
import tempfile
import unittest

import numpy as np
from gguf import GGUFWriter

import profiler as fl08

HIDDEN = 16  # head_dim=8 -- confirmed non-involutory under official_permute (unlike hidden=8/n_head=2)
N_HEAD = 2
N_HEAD_KV = 2
HEAD_DIM = HIDDEN // N_HEAD
INTERMEDIATE = 16
VOCAB = 32
N_LAYERS = 2


def _write_llama_fixture(path: str, seed: int, permute_qk: bool) -> None:
    """Writes a tiny, valid, synthetic llama-architecture GGUF. If
    permute_qk is True, Q/K tensors are written in the "canonical"
    (permuted) form relative to the seed=matching raw fixture --
    letting tests construct exact raw/canonical PAIRS deterministically."""
    writer = GGUFWriter(path, arch="llama")
    writer.add_name("fl08-synthetic-test-fixture")
    writer.add_context_length(64)
    writer.add_embedding_length(HIDDEN)
    writer.add_block_count(N_LAYERS)
    writer.add_feed_forward_length(INTERMEDIATE)
    writer.add_head_count(N_HEAD)
    writer.add_head_count_kv(N_HEAD_KV)
    writer.add_layer_norm_rms_eps(1e-5)
    writer.add_rope_dimension_count(HEAD_DIM)
    writer.add_rope_freq_base(10000.0)
    writer.add_file_type(0)

    rng = np.random.default_rng(seed=seed)

    def const_tensor(*shape) -> np.ndarray:
        return rng.standard_normal(shape).astype(np.float32)

    writer.add_tensor("token_embd.weight", const_tensor(VOCAB, HIDDEN))
    writer.add_tensor("output_norm.weight", const_tensor(HIDDEN))
    for i in range(N_LAYERS):
        q = const_tensor(N_HEAD * HEAD_DIM, HIDDEN)
        k = const_tensor(N_HEAD_KV * HEAD_DIM, HIDDEN)
        if permute_qk:
            q = fl08.official_permute(q.copy(), N_HEAD, N_HEAD)
            k = fl08.official_permute(k.copy(), N_HEAD, N_HEAD_KV)
        writer.add_tensor(f"blk.{i}.attn_norm.weight", const_tensor(HIDDEN))
        writer.add_tensor(f"blk.{i}.attn_q.weight", q)
        writer.add_tensor(f"blk.{i}.attn_k.weight", k)
        writer.add_tensor(f"blk.{i}.attn_v.weight", const_tensor(N_HEAD_KV * HEAD_DIM, HIDDEN))
        writer.add_tensor(f"blk.{i}.attn_output.weight", const_tensor(HIDDEN, N_HEAD * HEAD_DIM))
        writer.add_tensor(f"blk.{i}.ffn_norm.weight", const_tensor(HIDDEN))
        writer.add_tensor(f"blk.{i}.ffn_gate.weight", const_tensor(INTERMEDIATE, HIDDEN))
        writer.add_tensor(f"blk.{i}.ffn_up.weight", const_tensor(INTERMEDIATE, HIDDEN))
        writer.add_tensor(f"blk.{i}.ffn_down.weight", const_tensor(HIDDEN, INTERMEDIATE))

    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()


class SyntheticFixtureCase(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.raw_path = os.path.join(self.tmpdir, "raw.gguf")
        self.canonical_path = os.path.join(self.tmpdir, "canonical.gguf")
        _write_llama_fixture(self.raw_path, seed=1, permute_qk=False)
        _write_llama_fixture(self.canonical_path, seed=1, permute_qk=True)

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)


class TestOfficialPermute(unittest.TestCase):
    def test_not_involutory_for_realistic_head_geometry(self):
        # Regression guard for the exact bug this prototype's Layer 3
        # originally had: assuming permute(permute(x))==x. Confirmed
        # false for a realistic (non-trivial) head geometry -- the two
        # comparison directions (raw->canonical vs canonical->raw) must
        # be checked independently, never inferred from one failing.
        rng = np.random.default_rng(0)
        w = rng.standard_normal((576, 576)).astype(np.float32)
        once = fl08.official_permute(w.copy(), 9, 9)
        twice = fl08.official_permute(once.copy(), 9, 9)
        self.assertFalse(np.array_equal(twice, w))


class TestLayer3QkDialect(SyntheticFixtureCase):
    def test_raw_artifact_classified_raw_hf_against_canonical_reference(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")
        self.assertEqual(profile["qk_layout"]["layers_checked"], N_LAYERS * 2)
        self.assertTrue(profile["qk_layout"]["per_layer_consistent"])
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_NORMALIZATION_REQUIRED")
        self.assertFalse(profile["execution_authorization"])

    def test_canonical_artifact_classified_canonical_against_raw_reference(self):
        profile = fl08.profile_artifact(self.canonical_path, self.raw_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "CANONICAL_LLAMA_CPP")
        self.assertEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_COMPATIBLE")
        self.assertTrue(profile["execution_authorization"])

    def test_no_reference_is_ambiguous_not_a_guess(self):
        profile = fl08.profile_artifact(self.raw_path, None, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "UNKNOWN")
        self.assertEqual(profile["runtime_compatibility"]["result"], "AMBIGUOUS")
        self.assertFalse(profile["execution_authorization"])

    def test_unrelated_reference_is_ambiguous_not_forced_into_a_label(self):
        unrelated_path = os.path.join(self.tmpdir, "unrelated.gguf")
        _write_llama_fixture(unrelated_path, seed=999, permute_qk=False)  # different weights entirely
        profile = fl08.profile_artifact(self.raw_path, unrelated_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "AMBIGUOUS")
        self.assertEqual(profile["runtime_compatibility"]["result"], "AMBIGUOUS")
        self.assertFalse(profile["execution_authorization"])


class TestOutputWeightSemantics(SyntheticFixtureCase):
    def test_absent_output_weight_classified_absent(self):
        # These fixtures never write a separate output.weight tensor.
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(profile["output_weight_semantics"], "ABSENT")


class TestLayer1Layer2FailClosed(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_missing_file_is_invalid(self):
        profile = fl08.profile_artifact(os.path.join(self.tmpdir, "nope.gguf"), None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])

    def test_declared_layer_count_contradicts_actual_tensors(self):
        path = os.path.join(self.tmpdir, "bad_layer_count.gguf")
        writer = GGUFWriter(path, arch="llama")
        writer.add_name("test")
        writer.add_context_length(64)
        writer.add_embedding_length(HIDDEN)
        writer.add_block_count(3)  # declares 3 ...
        writer.add_feed_forward_length(INTERMEDIATE)
        writer.add_head_count(N_HEAD)
        writer.add_head_count_kv(N_HEAD_KV)
        writer.add_layer_norm_rms_eps(1e-5)
        writer.add_rope_dimension_count(HEAD_DIM)
        writer.add_rope_freq_base(10000.0)
        writer.add_file_type(0)
        rng = np.random.default_rng(1)
        writer.add_tensor("token_embd.weight", rng.standard_normal((VOCAB, HIDDEN)).astype(np.float32))
        writer.add_tensor("output_norm.weight", rng.standard_normal((HIDDEN,)).astype(np.float32))
        # ... but writes only layer 0 (matches the committed tampered_ambiguous.gguf fixture's construction).
        for suffix, shape in (
            ("attn_norm.weight", (HIDDEN,)), ("attn_q.weight", (N_HEAD * HEAD_DIM, HIDDEN)),
            ("attn_k.weight", (N_HEAD_KV * HEAD_DIM, HIDDEN)), ("attn_v.weight", (N_HEAD_KV * HEAD_DIM, HIDDEN)),
            ("attn_output.weight", (HIDDEN, N_HEAD * HEAD_DIM)), ("ffn_norm.weight", (HIDDEN,)),
            ("ffn_gate.weight", (INTERMEDIATE, HIDDEN)), ("ffn_up.weight", (INTERMEDIATE, HIDDEN)),
            ("ffn_down.weight", (HIDDEN, INTERMEDIATE)),
        ):
            writer.add_tensor(f"blk.0.{suffix}", rng.standard_normal(shape).astype(np.float32))
        writer.write_header_to_file()
        writer.write_kv_data_to_file()
        writer.write_tensors_to_file()
        writer.close()

        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])
        self.assertTrue(any("block_count" in a for a in profile["unresolved_ambiguities"]))

    def test_non_llama_architecture_is_unsupported_not_invalid(self):
        path = os.path.join(self.tmpdir, "not_llama.gguf")
        writer = GGUFWriter(path, arch="gemma")
        writer.add_name("test-gemma")
        writer.write_header_to_file()
        writer.write_kv_data_to_file()
        writer.write_tensors_to_file()
        writer.close()
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_UNSUPPORTED")
        self.assertFalse(profile["execution_authorization"])


class TestDeterminism(SyntheticFixtureCase):
    def test_repeated_profile_is_identical_excluding_nothing(self):
        # This schema carries no timestamps, so byte-identical dict
        # equality (not just "close enough") is the correct bar.
        p1 = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        p2 = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(p1, p2)


class TestArtifactNeverModified(SyntheticFixtureCase):
    def test_input_files_unchanged_after_profiling(self):
        import hashlib

        def sha(p):
            with open(p, "rb") as f:
                return hashlib.sha256(f.read()).hexdigest()

        before_raw, before_canon = sha(self.raw_path), sha(self.canonical_path)
        fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(sha(self.raw_path), before_raw)
        self.assertEqual(sha(self.canonical_path), before_canon)


if __name__ == "__main__":
    unittest.main()
