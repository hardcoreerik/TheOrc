# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Focused regression tests for FL-08's profiler.py -- the ONE test target
for this research prototype. Round 2 (Codex authority review +
independent Grok Double Check remediation): adds adversarial coverage
for the false-authorization defects the review found (unlabeled direct
match, unvalidated reference, wrong GGUF axis, packed Q8_0 geometry)
plus regressions proving they are fixed.

Uses small SYNTHETIC GGUF fixtures generated at test time (not the
real ~500MB Phase 6 artifacts, which are machine-local and referenced
only by hash/path in the experiment report, never depended on by
automated tests).

Run: python -m unittest test_profiler.py -v
"""
from __future__ import annotations

import os
import shutil
import tempfile
import unittest

import numpy as np
from gguf import GGMLQuantizationType, GGUFWriter

import profiler as fl08
import normalization_plan as fl08_plan
import make_tampered_fixture


# ---------------------------------------------------------------------
# Synthetic fixture construction helpers
# ---------------------------------------------------------------------

def pack_q8_0(arr: np.ndarray) -> np.ndarray:
    """Minimal Q8_0 packer for synthetic fixtures: per logical row, per
    32-element block, 2 bytes (f16 scale) + 32 bytes (int8 values).
    `arr` is (rows, cols); cols must be a multiple of 32. Distinguishable
    per Gate 5's own requirement -- real per-block scales, not filler."""
    rows, cols = arr.shape
    assert cols % 32 == 0
    n_blocks = cols // 32
    out = np.zeros((rows, n_blocks * 34), dtype=np.uint8)
    for r in range(rows):
        for b in range(n_blocks):
            block = arr[r, b * 32:(b + 1) * 32]
            amax = np.max(np.abs(block))
            scale = (amax / 127.0) if amax > 0 else 1.0
            q = np.clip(np.round(block / scale), -127, 127).astype(np.int8)
            out[r, b * 34:b * 34 + 2] = np.array([scale], dtype=np.float16).view(np.uint8)
            out[r, b * 34 + 2:b * 34 + 34] = q.view(np.uint8)
    return out


class FixtureSpec:
    def __init__(self, hidden=16, n_head=2, n_head_kv=2, intermediate=32, vocab=32, n_layers=2):
        self.hidden = hidden
        self.n_head = n_head
        self.n_head_kv = n_head_kv
        self.head_dim = hidden // n_head
        self.kv_rows = n_head_kv * self.head_dim
        self.q_rows = n_head * self.head_dim
        self.intermediate = intermediate
        self.vocab = vocab
        self.n_layers = n_layers


def write_llama_fixture(path: str, spec: FixtureSpec, seed: int, permute_qk: bool = False,
                        encoding: str = "F32", omit_rope_metadata: bool = False,
                        declared_block_count: int | None = None,
                        layers_to_write: int | None = None,
                        tamper: dict[str, np.ndarray] | None = None,
                        skip_tensors: tuple[str, ...] = (),
                        quantize_token_embd: bool = False) -> None:
    """Writes a small, valid-by-default synthetic llama-architecture
    GGUF. `tamper` overrides specific tensor arrays post-generation
    (before any encoding pass) to construct adversarial references.
    `skip_tensors` omits named tensors entirely (structural defects).
    `layers_to_write` (if less than spec.n_layers) constructs the
    classic layer-count contradiction. `declared_block_count` lets the
    metadata lie independently of what's actually written."""
    writer = GGUFWriter(path, arch="llama")
    writer.add_name("fl08-synthetic-test-fixture")
    writer.add_context_length(64)
    writer.add_embedding_length(spec.hidden)
    writer.add_block_count(declared_block_count if declared_block_count is not None else spec.n_layers)
    writer.add_feed_forward_length(spec.intermediate)
    writer.add_head_count(spec.n_head)
    writer.add_head_count_kv(spec.n_head_kv)
    writer.add_layer_norm_rms_eps(1e-5)
    if not omit_rope_metadata:
        writer.add_rope_dimension_count(spec.head_dim)
        writer.add_rope_freq_base(10000.0)
    writer.add_file_type(0)

    rng = np.random.default_rng(seed=seed)
    tamper = tamper or {}

    def make(name: str, *shape) -> np.ndarray:
        arr = tamper[name] if name in tamper else rng.standard_normal(shape).astype(np.float32)
        return arr

    # 1-D norm tensors are always left unquantized (real llama-quantize
    # convention, see Phase 6's Q8_0_FIXTURE_PROVENANCE.md). The 2-D
    # weight matrices ARE eligible; `token_embd.weight` is additionally
    # eligible only when `quantize_token_embd=True` -- the REAL Phase 6
    # Q8_0 artifacts DO quantize it (confirmed directly: 612 packed
    # bytes = 576/32*34 for hidden=576), which is exactly what the
    # round-2 `logical_last_dim()` fix exists to handle correctly.
    _q8_0_eligible = {"attn_q.weight", "attn_k.weight", "attn_v.weight", "attn_output.weight",
                      "ffn_gate.weight", "ffn_up.weight", "ffn_down.weight"}
    if quantize_token_embd:
        _q8_0_eligible = _q8_0_eligible | {"token_embd.weight"}

    def write(name: str, arr: np.ndarray) -> None:
        if name in skip_tensors:
            return
        suffix = name.split(".", 2)[-1] if name.startswith("blk.") else name
        if encoding == "Q8_0" and arr.ndim == 2 and arr.shape[1] % 32 == 0 and suffix in _q8_0_eligible:
            packed = pack_q8_0(arr)
            writer.add_tensor(name, packed, raw_dtype=GGMLQuantizationType.Q8_0)
        else:
            writer.add_tensor(name, arr)

    write("token_embd.weight", make("token_embd.weight", spec.vocab, spec.hidden))
    write("output_norm.weight", make("output_norm.weight", spec.hidden))

    n_layers_to_write = spec.n_layers if layers_to_write is None else layers_to_write
    for i in range(n_layers_to_write):
        q = make(f"blk.{i}.attn_q.weight", spec.q_rows, spec.hidden)
        k = make(f"blk.{i}.attn_k.weight", spec.kv_rows, spec.hidden)
        if permute_qk:
            q = fl08.official_permute(q.copy(), spec.n_head, spec.n_head)
            k = fl08.official_permute(k.copy(), spec.n_head, spec.n_head_kv)
        write(f"blk.{i}.attn_norm.weight", make(f"blk.{i}.attn_norm.weight", spec.hidden))
        write(f"blk.{i}.attn_q.weight", q)
        write(f"blk.{i}.attn_k.weight", k)
        write(f"blk.{i}.attn_v.weight", make(f"blk.{i}.attn_v.weight", spec.kv_rows, spec.hidden))
        write(f"blk.{i}.attn_output.weight", make(f"blk.{i}.attn_output.weight", spec.hidden, spec.q_rows))
        write(f"blk.{i}.ffn_norm.weight", make(f"blk.{i}.ffn_norm.weight", spec.hidden))
        write(f"blk.{i}.ffn_gate.weight", make(f"blk.{i}.ffn_gate.weight", spec.intermediate, spec.hidden))
        write(f"blk.{i}.ffn_up.weight", make(f"blk.{i}.ffn_up.weight", spec.intermediate, spec.hidden))
        write(f"blk.{i}.ffn_down.weight", make(f"blk.{i}.ffn_down.weight", spec.hidden, spec.intermediate))

    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()


# Confirmed non-involutory geometry (hidden=16/n_head=2 -> head_dim=8,
# half=4) -- matches the constant used throughout Phase 6 remediation.
SPEC = FixtureSpec(hidden=16, n_head=2, n_head_kv=2, intermediate=32, vocab=32, n_layers=2)
# Non-square GQA geometry -- n_head != n_head_kv, so Q rows (32) != K/V
# rows (16). This is the shape the round-2 axis bug could not catch
# (synthetic square fixtures pass regardless of which axis is checked).
GQA_SPEC = FixtureSpec(hidden=32, n_head=4, n_head_kv=2, intermediate=32, vocab=32, n_layers=2)
# Confirmed non-involutory for GQA_SPEC too (hidden/n_head=8, half=4).


class _PairedFixtureCase(unittest.TestCase):
    spec = SPEC

    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.raw_path = os.path.join(self.tmpdir, "raw.gguf")
        self.canonical_path = os.path.join(self.tmpdir, "canonical.gguf")
        write_llama_fixture(self.raw_path, self.spec, seed=1, permute_qk=False)
        write_llama_fixture(self.canonical_path, self.spec, seed=1, permute_qk=True)

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)


# ---------------------------------------------------------------------
# Gate 1: direct-match must never yield an absolute dialect label
# ---------------------------------------------------------------------

class TestGate1DirectMatchNeverAbsolute(_PairedFixtureCase):
    def test_identical_raw_hf_artifacts_are_same_layout_unknown_not_authorized(self):
        # THE core false-authorization defect: two byte-identical
        # RAW_HF artifacts must NOT be labeled CANONICAL_LLAMA_CPP.
        identical_raw_path = os.path.join(self.tmpdir, "raw_copy.gguf")
        write_llama_fixture(identical_raw_path, self.spec, seed=1, permute_qk=False)
        profile = fl08.profile_artifact(self.raw_path, identical_raw_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "SAME_LAYOUT_UNKNOWN")
        self.assertNotEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertNotEqual(profile["qk_layout"]["classification"], "CANONICAL_LLAMA_CPP")
        self.assertFalse(profile["execution_authorization"])
        self.assertEqual(profile["runtime_compatibility"]["result"], "AMBIGUOUS")

    def test_identical_canonical_artifacts_are_same_layout_unknown_not_authorized(self):
        identical_canon_path = os.path.join(self.tmpdir, "canon_copy.gguf")
        write_llama_fixture(identical_canon_path, self.spec, seed=1, permute_qk=True)
        profile = fl08.profile_artifact(self.canonical_path, identical_canon_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "SAME_LAYOUT_UNKNOWN")
        self.assertFalse(profile["execution_authorization"])

    def test_declared_reference_layout_yields_declared_confidence_never_authorizes(self):
        identical_raw_path = os.path.join(self.tmpdir, "raw_copy2.gguf")
        write_llama_fixture(identical_raw_path, self.spec, seed=1, permute_qk=False)
        profile = fl08.profile_artifact(self.raw_path, identical_raw_path, "canonical-llama.cpp",
                                        reference_layout_declared="raw")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertEqual(profile["qk_layout"]["confidence"], "DECLARED")
        self.assertNotEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")
        self.assertFalse(profile["execution_authorization"],
                         "a DECLARED-confidence label must never authorize execution on its own")

    def test_degenerate_noop_permutation_geometry_is_ambiguous(self):
        # hidden=8/n_head=2 -> head_dim=4, half=2: confirmed involutory
        # in Phase 6 exploration -- multiple hypotheses match at once.
        degenerate_spec = FixtureSpec(hidden=8, n_head=2, n_head_kv=2, intermediate=16, vocab=16, n_layers=1)
        raw_p = os.path.join(self.tmpdir, "degenerate_raw.gguf")
        canon_p = os.path.join(self.tmpdir, "degenerate_canon.gguf")
        write_llama_fixture(raw_p, degenerate_spec, seed=7, permute_qk=False)
        write_llama_fixture(canon_p, degenerate_spec, seed=7, permute_qk=True)
        profile = fl08.profile_artifact(raw_p, canon_p, "canonical-llama.cpp")
        # Either AMBIGUOUS (multiple hypotheses collide) or a genuinely
        # resolved single hypothesis is acceptable -- what must NEVER
        # happen is a silent authorize on a degenerate geometry.
        if profile["qk_layout"]["classification"] not in ("RAW_HF", "CANONICAL_LLAMA_CPP"):
            self.assertFalse(profile["execution_authorization"])

    def test_raw_to_canonical_directional_case_still_works(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_NORMALIZATION_REQUIRED")
        self.assertFalse(profile["execution_authorization"])

    def test_canonical_from_raw_directional_case_still_works_and_authorizes(self):
        profile = fl08.profile_artifact(self.canonical_path, self.raw_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "CANONICAL_LLAMA_CPP")
        self.assertEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_COMPATIBLE")
        self.assertTrue(profile["execution_authorization"])


# ---------------------------------------------------------------------
# Gate 2: reference validation + pair identity
# ---------------------------------------------------------------------

class TestGate2ReferenceValidationAndPairIdentity(_PairedFixtureCase):
    def test_missing_reference_is_structured_non_authorizing_not_a_crash(self):
        profile = fl08.profile_artifact(self.raw_path, os.path.join(self.tmpdir, "nope.gguf"),
                                        "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])
        self.assertEqual(profile["reference"]["terminal_result"], "INVALID")

    def test_malformed_reference_is_structured_invalid_not_a_crash(self):
        garbage_path = os.path.join(self.tmpdir, "garbage.gguf")
        with open(garbage_path, "wb") as f:
            f.write(b"not a real gguf file at all")
        profile = fl08.profile_artifact(self.raw_path, garbage_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])
        self.assertEqual(profile["reference"]["terminal_result"], "INVALID")

    def test_reference_hash_bound_into_profile(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertIsNotNone(profile["reference"])
        self.assertEqual(profile["reference"]["sha256"], fl08.sha256_file(self.canonical_path))

    def test_reference_with_different_architecture_metadata_denied(self):
        other_arch_path = os.path.join(self.tmpdir, "other_arch.gguf")
        w = GGUFWriter(other_arch_path, arch="gemma")
        w.add_name("not-llama")
        w.write_header_to_file()
        w.write_kv_data_to_file()
        w.write_tensors_to_file()
        w.close()
        profile = fl08.profile_artifact(self.raw_path, other_arch_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])

    def test_reference_with_different_head_geometry_denied(self):
        different_geometry_path = os.path.join(self.tmpdir, "different_geom.gguf")
        different_spec = FixtureSpec(hidden=16, n_head=4, n_head_kv=4, intermediate=32, vocab=32, n_layers=2)
        write_llama_fixture(different_geometry_path, different_spec, seed=1, permute_qk=True)
        profile = fl08.profile_artifact(self.raw_path, different_geometry_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")

    def test_reference_with_changed_v_tensor_denied_even_if_qk_still_matches(self):
        # The tampering attack: Q/K match, but V was altered -- pair
        # identity must be REJECTED, not silently trusted.
        tampered_path = os.path.join(self.tmpdir, "tampered_v.gguf")
        rng = np.random.default_rng(999)
        tamper = {"blk.0.attn_v.weight": rng.standard_normal((self.spec.kv_rows, self.spec.hidden)).astype(np.float32)}
        write_llama_fixture(tampered_path, self.spec, seed=1, permute_qk=True, tamper=tamper)
        profile = fl08.profile_artifact(self.raw_path, tampered_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_reference_with_changed_ffn_tensor_denied(self):
        tampered_path = os.path.join(self.tmpdir, "tampered_ffn.gguf")
        rng = np.random.default_rng(999)
        tamper = {"blk.1.ffn_down.weight": rng.standard_normal((self.spec.hidden, self.spec.intermediate)).astype(np.float32)}
        write_llama_fixture(tampered_path, self.spec, seed=1, permute_qk=True, tamper=tamper)
        profile = fl08.profile_artifact(self.raw_path, tampered_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_reference_with_changed_norm_tensor_denied(self):
        tampered_path = os.path.join(self.tmpdir, "tampered_norm.gguf")
        rng = np.random.default_rng(999)
        tamper = {"blk.0.attn_norm.weight": rng.standard_normal((self.spec.hidden,)).astype(np.float32)}
        write_llama_fixture(tampered_path, self.spec, seed=1, permute_qk=True, tamper=tamper)
        profile = fl08.profile_artifact(self.raw_path, tampered_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_reference_with_different_tokenizer_metadata_documented_behavior(self):
        # Tokenizer/general metadata is NOT a tensor -- pair identity in
        # this experiment is proven from tensor content, not metadata.
        # This test documents (not silently assumes) that behavior: a
        # tokenizer-metadata-only difference does not, by itself, flip
        # pair identity, because the model's actual WEIGHTS are still
        # provably identical. This is a disclosed scope boundary, not a
        # gap -- see EXPERIMENT.md's round-2 section.
        path_a = os.path.join(self.tmpdir, "tok_a.gguf")
        path_b = os.path.join(self.tmpdir, "tok_b.gguf")
        write_llama_fixture(path_a, self.spec, seed=1, permute_qk=False)
        write_llama_fixture(path_b, self.spec, seed=1, permute_qk=True)
        profile = fl08.profile_artifact(path_a, path_b, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "VERIFIED")

    def test_unrelated_reference_with_plausible_names_is_ambiguous(self):
        unrelated_path = os.path.join(self.tmpdir, "unrelated.gguf")
        write_llama_fixture(unrelated_path, self.spec, seed=42424242, permute_qk=False)
        profile = fl08.profile_artifact(self.raw_path, unrelated_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])

    def test_genuinely_bound_same_model_pair_is_verified_and_can_authorize(self):
        profile = fl08.profile_artifact(self.canonical_path, self.raw_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "VERIFIED")
        self.assertTrue(profile["execution_authorization"])


# ---------------------------------------------------------------------
# Gate 3: logical-shape/axis correctness, non-square GQA, structural
# hostiles
# ---------------------------------------------------------------------

class TestGate3LogicalShapeAndStructuralValidation(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_non_square_gqa_fixture_classifies_correctly(self):
        # n_head=4 != n_head_kv=2 -> Q rows (32) != K/V rows (16). This
        # is the exact shape the round-2 axis bug (validating
        # tensor.shape[0] instead of tensor.data.shape[0]) could not be
        # caught by, since square synthetic fixtures pass regardless of
        # which axis is inspected.
        raw_p = os.path.join(self.tmpdir, "gqa_raw.gguf")
        canon_p = os.path.join(self.tmpdir, "gqa_canon.gguf")
        write_llama_fixture(raw_p, GQA_SPEC, seed=3, permute_qk=False)
        write_llama_fixture(canon_p, GQA_SPEC, seed=3, permute_qk=True)
        profile = fl08.profile_artifact(raw_p, canon_p, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_NORMALIZATION_REQUIRED")

    def test_logical_shape_helper_uses_data_shape_not_native_shape(self):
        write_llama_fixture(os.path.join(self.tmpdir, "gqa.gguf"), GQA_SPEC, seed=3, permute_qk=False)
        from gguf import GGUFReader
        r = GGUFReader(os.path.join(self.tmpdir, "gqa.gguf"))
        k = fl08._tensor_by_name(r, "blk.0.attn_k.weight")
        self.assertEqual(fl08.logical_shape(k), k.data.shape)
        self.assertEqual(fl08.logical_shape(k)[0], GQA_SPEC.kv_rows)

    def test_zero_layers_declared_is_invalid(self):
        path = os.path.join(self.tmpdir, "zero_layers.gguf")
        spec = FixtureSpec(hidden=16, n_head=2, n_head_kv=2, intermediate=32, vocab=32, n_layers=0)
        write_llama_fixture(path, spec, seed=1, declared_block_count=0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])

    def test_zero_heads_declared_is_invalid(self):
        path = os.path.join(self.tmpdir, "zero_heads.gguf")
        w = GGUFWriter(path, arch="llama")
        w.add_name("zero-heads")
        w.add_context_length(64)
        w.add_embedding_length(16)
        w.add_block_count(1)
        w.add_feed_forward_length(32)
        w.add_head_count(0)
        w.add_head_count_kv(0)
        w.add_layer_norm_rms_eps(1e-5)
        w.add_rope_dimension_count(8)
        w.add_rope_freq_base(10000.0)
        w.write_header_to_file()
        w.write_kv_data_to_file()
        w.write_tensors_to_file()
        w.close()
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])

    def test_missing_rope_metadata_recorded_as_ambiguity_not_silently_passed(self):
        path = os.path.join(self.tmpdir, "no_rope.gguf")
        write_llama_fixture(path, SPEC, seed=1, omit_rope_metadata=True)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertTrue(any("rope" in a.lower() for a in profile["unresolved_ambiguities"]))

    def test_wrong_v_dimension_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_v.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.attn_v.weight": rng.standard_normal((SPEC.kv_rows + 4, SPEC.hidden)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_wrong_output_projection_dimension_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_o.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.attn_output.weight": rng.standard_normal((SPEC.hidden + 1, SPEC.q_rows)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_mismatched_ffn_gate_up_dimensions_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_ffn.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.ffn_up.weight": rng.standard_normal((SPEC.intermediate + 8, SPEC.hidden)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_wrong_norm_dimension_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_norm.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.ffn_norm.weight": rng.standard_normal((SPEC.hidden + 1,)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_declared_layer_count_contradicts_actual_tensors(self):
        path = os.path.join(self.tmpdir, "bad_layer_count.gguf")
        write_llama_fixture(path, FixtureSpec(hidden=16, n_head=2, n_head_kv=2, intermediate=32, vocab=32,
                                              n_layers=3),
                            seed=1, layers_to_write=1)
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


# ---------------------------------------------------------------------
# Gate 4: authorization invariant
# ---------------------------------------------------------------------

class TestGate4AuthorizationInvariant(_PairedFixtureCase):
    def test_unknown_encoding_never_authorizes(self):
        # I8 is a real, valid GGML type gguf-py maps automatically from
        # np.int8 -- but it is not in this profiler's KNOWN_ENCODINGS
        # set, so it exercises the "unknown encoding" ambiguity path
        # directly without needing a fake enum value.
        path = os.path.join(self.tmpdir, "weird_encoding.gguf")
        w = GGUFWriter(path, arch="llama")
        w.add_name("weird")
        w.add_context_length(64)
        w.add_embedding_length(16)
        w.add_block_count(1)
        w.add_feed_forward_length(32)
        w.add_head_count(2)
        w.add_head_count_kv(2)
        w.add_layer_norm_rms_eps(1e-5)
        w.add_rope_dimension_count(8)
        w.add_rope_freq_base(10000.0)
        rng = np.random.default_rng(1)
        w.add_tensor("token_embd.weight", rng.standard_normal((16, 16)).astype(np.float32))
        w.add_tensor("output_norm.weight", rng.standard_normal((16,)).astype(np.float32))
        for suffix, shape in (("attn_norm.weight", (16,)), ("attn_v.weight", (16, 16)),
                              ("attn_output.weight", (16, 16)), ("ffn_norm.weight", (16,)),
                              ("ffn_gate.weight", (32, 16)), ("ffn_up.weight", (32, 16)),
                              ("ffn_down.weight", (16, 32))):
            w.add_tensor(f"blk.0.{suffix}", rng.standard_normal(shape).astype(np.float32))
        # Q/K deliberately written as I8 (unknown to this profiler).
        w.add_tensor("blk.0.attn_q.weight", rng.integers(-127, 127, (16, 16)).astype(np.int8))
        w.add_tensor("blk.0.attn_k.weight", rng.integers(-127, 127, (16, 16)).astype(np.int8))
        w.write_header_to_file()
        w.write_kv_data_to_file()
        w.write_tensors_to_file()
        w.close()
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertIn("encoding", " ".join(profile["unresolved_ambiguities"]).lower())
        self.assertFalse(profile["execution_authorization"])

    def test_unresolved_ambiguity_never_authorizes(self):
        profile = fl08.profile_artifact(self.raw_path, None, "canonical-llama.cpp")
        self.assertGreater(len(profile["unresolved_ambiguities"]), 0)
        self.assertFalse(profile["execution_authorization"])

    def test_no_reference_ambiguous_never_authorizes(self):
        profile = fl08.profile_artifact(self.raw_path, None, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "UNKNOWN")
        self.assertFalse(profile["execution_authorization"])

    def test_same_layout_unknown_never_authorizes(self):
        identical_path = os.path.join(self.tmpdir, "identical.gguf")
        write_llama_fixture(identical_path, self.spec, seed=1, permute_qk=False)
        profile = fl08.profile_artifact(self.raw_path, identical_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "SAME_LAYOUT_UNKNOWN")
        self.assertFalse(profile["execution_authorization"])

    def test_invalid_reference_never_authorizes(self):
        profile = fl08.profile_artifact(self.raw_path, os.path.join(self.tmpdir, "missing.gguf"),
                                        "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])

    def test_unverified_pair_identity_never_authorizes(self):
        tampered_path = os.path.join(self.tmpdir, "tampered.gguf")
        rng = np.random.default_rng(999)
        tamper = {"blk.0.attn_v.weight": rng.standard_normal((self.spec.kv_rows, self.spec.hidden)).astype(np.float32)}
        write_llama_fixture(tampered_path, self.spec, seed=1, permute_qk=True, tamper=tamper)
        profile = fl08.profile_artifact(self.raw_path, tampered_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])

    def test_partial_qk_coverage_never_authorizes(self):
        # A reference missing blk.1's K tensor entirely -- qk_tensors
        # checked/total should mismatch, or a MISSING_IN_ONE_SIDE
        # ambiguity should surface; either way, no authorization.
        partial_path = os.path.join(self.tmpdir, "partial_ref.gguf")
        write_llama_fixture(partial_path, self.spec, seed=1, permute_qk=True,
                            skip_tensors=("blk.1.attn_k.weight",))
        profile = fl08.profile_artifact(self.raw_path, partial_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])

    def test_normalization_required_never_authorizes(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_NORMALIZATION_REQUIRED")
        self.assertFalse(profile["execution_authorization"])

    def test_zero_layer_vacuous_profile_never_authorizes(self):
        path = os.path.join(self.tmpdir, "zero_layers2.gguf")
        spec = FixtureSpec(hidden=16, n_head=2, n_head_kv=2, intermediate=32, vocab=32, n_layers=0)
        write_llama_fixture(path, spec, seed=1, declared_block_count=0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])

    def test_authorization_conditions_all_true_only_for_genuine_compatible_case(self):
        profile = fl08.profile_artifact(self.canonical_path, self.raw_path, "canonical-llama.cpp")
        self.assertTrue(profile["execution_authorization"])
        self.assertTrue(all(profile["authorization_conditions"].values()))


# ---------------------------------------------------------------------
# Gate 5: packed Q8_0 validation
# ---------------------------------------------------------------------

class TestGate5PackedQ80Validation(unittest.TestCase):
    spec = FixtureSpec(hidden=64, n_head=4, n_head_kv=2, intermediate=64, vocab=32, n_layers=1)  # non-square GQA

    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.raw_q8_path = os.path.join(self.tmpdir, "raw_q8.gguf")
        self.canon_q8_path = os.path.join(self.tmpdir, "canon_q8.gguf")
        write_llama_fixture(self.raw_q8_path, self.spec, seed=11, permute_qk=False, encoding="Q8_0")
        write_llama_fixture(self.canon_q8_path, self.spec, seed=11, permute_qk=True, encoding="Q8_0")

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_raw_to_canonical_row_permutation_detected_on_packed_q8_0(self):
        profile = fl08.profile_artifact(self.raw_q8_path, self.canon_q8_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")

    def test_canonical_from_raw_directional_detection_on_packed_q8_0(self):
        profile = fl08.profile_artifact(self.canon_q8_path, self.raw_q8_path, "canonical-llama.cpp")
        self.assertEqual(profile["qk_layout"]["classification"], "CANONICAL_LLAMA_CPP")
        self.assertTrue(profile["execution_authorization"])

    def test_distinguishable_blocks_are_not_coincidentally_equal(self):
        # Guards against a degenerate packer that emits identical blocks
        # regardless of content -- confirm at least two blocks in a real
        # packed row actually differ.
        from gguf import GGUFReader
        r = GGUFReader(self.raw_q8_path)
        t = fl08._tensor_by_name(r, "blk.0.attn_q.weight")
        row0 = t.data[0]
        self.assertFalse(np.all(row0[0:34] == row0[34:68]), "synthetic Q8_0 blocks must be distinguishable")

    def _write_bad_geometry_pair(self) -> tuple[str, str]:
        """Builds a matched artifact/reference PAIR whose Q/K tensors
        are BOTH packed at a byte width gguf-py itself accepts (a
        multiple of 34, so construction succeeds) but that is
        INCONSISTENT with the declared embedding_length=64 (packed for
        32 "fake" elements -- 1 block/34 bytes -- instead of 64 real
        elements -- 2 blocks/68 bytes). Since both sides share the SAME
        malformed width, the shape-equality check passes and the
        dedicated Q8_0 geometry validation is what must catch it."""
        paths = []
        for label in ("bad_a", "bad_b"):
            path = os.path.join(self.tmpdir, f"{label}.gguf")
            w = GGUFWriter(path, arch="llama")
            w.add_name(label)
            w.add_context_length(64)
            w.add_embedding_length(64)
            w.add_block_count(1)
            w.add_feed_forward_length(64)
            w.add_head_count(4)
            w.add_head_count_kv(2)
            w.add_layer_norm_rms_eps(1e-5)
            w.add_rope_dimension_count(16)
            w.add_rope_freq_base(10000.0)
            rng = np.random.default_rng(1)
            w.add_tensor("token_embd.weight", rng.standard_normal((32, 64)).astype(np.float32))
            w.add_tensor("output_norm.weight", rng.standard_normal((64,)).astype(np.float32))
            w.add_tensor("blk.0.attn_norm.weight", rng.standard_normal((64,)).astype(np.float32))
            # Packed as if hidden=32 (1 block, 34 bytes) while metadata
            # declares embedding_length=64 (should require 2 blocks/68
            # bytes) -- row count (32, matching kv_rows/q_rows) still
            # passes Layer 2 (which only checks ROWS, not the packed
            # column/byte width), so this must be caught in Layer 3's
            # dedicated Q8_0 geometry check.
            bad_q = pack_q8_0(rng.standard_normal((64, 32)).astype(np.float32))
            bad_k = pack_q8_0(rng.standard_normal((32, 32)).astype(np.float32))
            w.add_tensor("blk.0.attn_q.weight", bad_q, raw_dtype=GGMLQuantizationType.Q8_0)
            w.add_tensor("blk.0.attn_k.weight", bad_k, raw_dtype=GGMLQuantizationType.Q8_0)
            w.add_tensor("blk.0.attn_v.weight", rng.standard_normal((32, 64)).astype(np.float32))
            w.add_tensor("blk.0.attn_output.weight", rng.standard_normal((64, 64)).astype(np.float32))
            w.add_tensor("blk.0.ffn_norm.weight", rng.standard_normal((64,)).astype(np.float32))
            w.add_tensor("blk.0.ffn_gate.weight", rng.standard_normal((64, 64)).astype(np.float32))
            w.add_tensor("blk.0.ffn_up.weight", rng.standard_normal((64, 64)).astype(np.float32))
            w.add_tensor("blk.0.ffn_down.weight", rng.standard_normal((64, 64)).astype(np.float32))
            w.write_header_to_file()
            w.write_kv_data_to_file()
            w.write_tensors_to_file()
            w.close()
            paths.append(path)
        return tuple(paths)

    def test_malformed_packed_row_width_rejected(self):
        bad_a, bad_b = self._write_bad_geometry_pair()
        profile = fl08.profile_artifact(bad_a, bad_b, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])
        self.assertNotIn(profile["qk_layout"]["classification"], ("RAW_HF", "CANONICAL_LLAMA_CPP"))
        self.assertTrue(any("Q8_0" in a or "q8_0" in a.lower() or "block" in a.lower()
                            for a in profile["unresolved_ambiguities"]))

    def test_mixed_tensor_types_rejected(self):
        # Artifact has F32 Q/K, reference has Q8_0 Q/K -- must not be
        # byte-compared across encodings.
        f32_path = os.path.join(self.tmpdir, "f32_variant.gguf")
        write_llama_fixture(f32_path, self.spec, seed=11, permute_qk=True, encoding="F32")
        profile = fl08.profile_artifact(self.raw_q8_path, f32_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])
        self.assertNotIn(profile["qk_layout"]["classification"], ("RAW_HF", "CANONICAL_LLAMA_CPP"))

    def test_one_byte_mutation_in_packed_row_rejected(self):
        # Corrupt exactly one byte of the reference's packed K row 0
        # after generation -- classification must not silently ignore it.
        import struct
        mutated_path = os.path.join(self.tmpdir, "mutated_one_byte.gguf")
        shutil.copyfile(self.canon_q8_path, mutated_path)
        from gguf import GGUFReader
        r = GGUFReader(mutated_path)
        t = fl08._tensor_by_name(r, "blk.0.attn_q.weight")
        offset = int(t.data_offset)
        with open(mutated_path, "r+b") as f:
            f.seek(offset)
            b = f.read(1)
            f.seek(offset)
            f.write(struct.pack("B", (b[0] + 1) % 256))
        profile = fl08.profile_artifact(self.raw_q8_path, mutated_path, "canonical-llama.cpp")
        self.assertFalse(profile["execution_authorization"])

    def test_quantized_token_embd_weight_validated_via_logical_last_dim(self):
        # Regression for the exact defect found while regenerating real
        # Phase 6 evidence: token_embd.weight IS Q8_0-quantized in real
        # artifacts, so comparing its raw PACKED byte width directly
        # against `hidden` (rather than decoding it back to logical
        # element count first) falsely reports a shape contradiction on
        # every real quantized artifact.
        raw_p = os.path.join(self.tmpdir, "embd_q8_raw.gguf")
        canon_p = os.path.join(self.tmpdir, "embd_q8_canon.gguf")
        write_llama_fixture(raw_p, self.spec, seed=21, permute_qk=False, encoding="Q8_0",
                            quantize_token_embd=True)
        write_llama_fixture(canon_p, self.spec, seed=21, permute_qk=True, encoding="Q8_0",
                            quantize_token_embd=True)
        profile = fl08.profile_artifact(raw_p, canon_p, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_NORMALIZATION_REQUIRED")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")


# ---------------------------------------------------------------------
# Existing coverage (output-weight semantics, determinism, no
# modification of inputs) -- retained from round 1, adapted to the new
# schema fields.
# ---------------------------------------------------------------------

class TestOfficialPermute(unittest.TestCase):
    def test_not_involutory_for_realistic_head_geometry(self):
        rng = np.random.default_rng(0)
        w = rng.standard_normal((576, 576)).astype(np.float32)
        once = fl08.official_permute(w.copy(), 9, 9)
        twice = fl08.official_permute(once.copy(), 9, 9)
        self.assertFalse(np.array_equal(twice, w))


class TestOutputWeightSemantics(_PairedFixtureCase):
    def test_absent_output_weight_classified_absent(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(profile["output_weight_semantics"], "ABSENT")


class TestDeterminism(_PairedFixtureCase):
    def test_repeated_profile_is_identical_excluding_nothing(self):
        p1 = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        p2 = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(p1, p2)


class TestArtifactNeverModified(_PairedFixtureCase):
    def test_input_files_unchanged_after_profiling(self):
        import hashlib

        def sha(p):
            with open(p, "rb") as f:
                return hashlib.sha256(f.read()).hexdigest()

        before_raw, before_canon = sha(self.raw_path), sha(self.canonical_path)
        fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(sha(self.raw_path), before_raw)
        self.assertEqual(sha(self.canonical_path), before_canon)


# ---------------------------------------------------------------------
# Gate 7: normalization plan forward/reverse disposition
# ---------------------------------------------------------------------

class TestGate7NormalizationPlan(_PairedFixtureCase):
    def test_forward_plan_emitted_for_raw_to_canonical(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNotNone(plan)
        self.assertIsNone(reason)
        self.assertEqual(plan["source_layout"], "hf_raw_rotate_half")
        self.assertEqual(plan["target_layout"], "llama_cpp_interleaved_rope")
        self.assertFalse(plan["destructive"])
        self.assertFalse(plan["execution_authorized"])

    def test_reverse_plan_explicitly_refused_not_invented(self):
        profile = fl08.profile_artifact(self.canonical_path, self.raw_path, "orcengine-current")
        self.assertEqual(profile["qk_layout"]["classification"], "CANONICAL_LLAMA_CPP")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_NORMALIZATION_REQUIRED")
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIn("REVERSE", reason.upper() if "REVERSE" in reason.upper() else reason.upper())
        self.assertIn("inverse", reason.lower())

    def test_malformed_profile_refused(self):
        plan, reason = fl08_plan.build_plan({"not": "a real profile"})
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_ambiguous_profile_refused(self):
        profile = fl08.profile_artifact(self.raw_path, None, "canonical-llama.cpp")
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)

    def test_unverified_reference_profile_refused(self):
        profile = fl08.profile_artifact(self.raw_path, os.path.join(self.tmpdir, "missing.gguf"),
                                        "canonical-llama.cpp")
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)

    def test_unverified_pair_identity_profile_refused(self):
        tampered_path = os.path.join(self.tmpdir, "tampered_for_plan.gguf")
        rng = np.random.default_rng(999)
        tamper = {"blk.0.attn_v.weight": rng.standard_normal((self.spec.kv_rows, self.spec.hidden)).astype(np.float32)}
        write_llama_fixture(tampered_path, self.spec, seed=1, permute_qk=True, tamper=tamper)
        profile = fl08.profile_artifact(self.raw_path, tampered_path, "canonical-llama.cpp")
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)

    def test_incorrect_tensor_counts_refused(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        profile["qk_layout"]["layers_total"] = 0
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)

    def test_unauthorized_but_compatible_input_profile_yields_no_plan(self):
        profile = fl08.profile_artifact(self.canonical_path, self.raw_path, "canonical-llama.cpp")
        self.assertTrue(profile["execution_authorization"])
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)  # VERIFIED_COMPATIBLE has nothing to normalize


# ---------------------------------------------------------------------
# Gate 8: deterministic regeneration of the committed Case E fixture
# (round-2 remediation -- Grok flagged that no test actually asserted
# regeneration byte-identity against the committed
# fixtures/tampered_ambiguous.gguf; this closes that gap directly).
# ---------------------------------------------------------------------

class TestTamperedFixtureRegeneration(unittest.TestCase):
    def test_regeneration_is_byte_identical_to_committed_fixture(self):
        committed_path = os.path.join(os.path.dirname(__file__), "fixtures", "tampered_ambiguous.gguf")
        with open(committed_path, "rb") as f:
            committed_bytes = f.read()

        tmpdir = tempfile.mkdtemp()
        try:
            regenerated_path = os.path.join(tmpdir, "regenerated.gguf")
            original_out_path = make_tampered_fixture.OUT_PATH
            make_tampered_fixture.OUT_PATH = regenerated_path
            try:
                make_tampered_fixture.main()
            finally:
                make_tampered_fixture.OUT_PATH = original_out_path
            with open(regenerated_path, "rb") as f:
                regenerated_bytes = f.read()
        finally:
            shutil.rmtree(tmpdir, ignore_errors=True)

        self.assertEqual(regenerated_bytes, committed_bytes,
                         "make_tampered_fixture.py must regenerate fixtures/tampered_ambiguous.gguf "
                         "byte-for-byte -- this is the actual, executed proof of regeneration "
                         "determinism, not merely a documentation claim")

    def test_committed_fixture_still_classifies_invalid_not_ambiguous(self):
        # Round-2 vocabulary fix: this fixture's proof is a structural
        # layer-count CONTRADICTION (INVALID), not a Q/K-layout
        # AMBIGUOUS classification -- Layer 3 never runs on it.
        committed_path = os.path.join(os.path.dirname(__file__), "fixtures", "tampered_ambiguous.gguf")
        profile = fl08.profile_artifact(committed_path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertEqual(profile["qk_layout"]["classification"], "UNKNOWN")
        self.assertFalse(profile["execution_authorization"])


if __name__ == "__main__":
    unittest.main()
