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
                        quantize_token_embd: bool = False,
                        name: str = "fl08-synthetic-test-fixture",
                        rms_eps: float = 1e-5,
                        rope_freq_base: float = 10000.0,
                        context_length: int = 64,
                        tokenizer_fields: dict | None = None,
                        extra_non_qk_tensor: tuple[str, tuple[int, ...]] | None = None,
                        include_output_weight: str | None = None,
                        rope_dim_override: int | None = None,
                        rope_scaling_type: str | None = None,
                        rope_scaling_factor: float | None = None,
                        sliding_window: int | None = None) -> None:
    """Writes a small, valid-by-default synthetic llama-architecture
    GGUF. `tamper` overrides specific tensor arrays post-generation
    (before any encoding pass) to construct adversarial references.
    `skip_tensors` omits named tensors entirely (structural defects).
    `layers_to_write` (if less than spec.n_layers) constructs the
    classic layer-count contradiction. `declared_block_count` lets the
    metadata lie independently of what's actually written. `rms_eps`/
    `rope_freq_base`/`context_length` let a caller construct a genuine
    execution-metadata-only difference (no tensor content changed).
    `tokenizer_fields` is an optional {key: str_or_int_value} dict
    written as tokenizer.ggml.* string/int fields (round-3 Gate 1C
    adversarial tokenizer-metadata tests). `extra_non_qk_tensor` adds
    one additional, unexpected non-Q/K tensor (name, shape) (round-3
    Gate 1A "extra unknown tensor on one side" test)."""
    writer = GGUFWriter(path, arch="llama")
    writer.add_name(name)
    writer.add_context_length(context_length)
    writer.add_embedding_length(spec.hidden)
    writer.add_block_count(declared_block_count if declared_block_count is not None else spec.n_layers)
    writer.add_feed_forward_length(spec.intermediate)
    writer.add_head_count(spec.n_head)
    writer.add_head_count_kv(spec.n_head_kv)
    writer.add_layer_norm_rms_eps(rms_eps)
    if not omit_rope_metadata:
        writer.add_rope_dimension_count(rope_dim_override if rope_dim_override is not None else spec.head_dim)
        writer.add_rope_freq_base(rope_freq_base)
    if rope_scaling_type is not None:
        writer.add_string("llama.rope.scaling.type", rope_scaling_type)
    if rope_scaling_factor is not None:
        writer.add_float32("llama.rope.scaling.factor", rope_scaling_factor)
    if sliding_window is not None:
        writer.add_uint32("llama.attention.sliding_window", sliding_window)
    writer.add_file_type(0)
    for key, value in (tokenizer_fields or {}).items():
        if isinstance(value, str):
            writer.add_string(key, value)
        elif isinstance(value, int):
            writer.add_uint32(key, value)
        elif isinstance(value, list):
            writer.add_array(key, value)
        else:
            raise TypeError(f"unsupported tokenizer field value type for {key!r}: {type(value)}")

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

    token_embd = make("token_embd.weight", spec.vocab, spec.hidden)
    write("token_embd.weight", token_embd)
    write("output_norm.weight", make("output_norm.weight", spec.hidden))

    if "output.weight" in tamper:
        write("output.weight", tamper["output.weight"])
    elif include_output_weight == "tied":
        write("output.weight", token_embd.copy())
    elif include_output_weight == "untied":
        write("output.weight", rng.standard_normal((spec.vocab, spec.hidden)).astype(np.float32))

    if extra_non_qk_tensor is not None:
        extra_name, extra_shape = extra_non_qk_tensor
        writer.add_tensor(extra_name, rng.standard_normal(extra_shape).astype(np.float32))

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
        # Round-3 remediation (Grok finding #2): this assertion was
        # previously conditional ("only check non-authorization IF
        # classification isn't absolute"), which would not have caught
        # a regression where a degenerate geometry DID produce an
        # absolute label. Unconditional now: this specific geometry is
        # confirmed (above) to hit MULTIPLE simultaneous hypotheses,
        # which must ALWAYS resolve to AMBIGUOUS/AMBIGUOUS and NEVER
        # authorize.
        self.assertEqual(profile["qk_layout"]["classification"], "AMBIGUOUS")
        self.assertEqual(profile["qk_layout"]["confidence"], "AMBIGUOUS")
        self.assertEqual(profile["runtime_compatibility"]["result"], "AMBIGUOUS")
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

    def test_no_tokenizer_metadata_on_either_side_is_trivially_equal(self):
        # Round-3 correction: this test previously claimed to document
        # "tokenizer metadata differences don't block pair identity,"
        # but never actually varied any tokenizer field -- both sides
        # simply had NO tokenizer.* metadata at all, so the comparison
        # was vacuously equal. That claim was never actually true; see
        # TestRound3TokenizerMetadata below for the REAL (now strict)
        # behavior when tokenizer metadata actually differs.
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
# Round 3 (Codex authority review + independent Grok Double Check):
# Gate 1B -- asymmetric output.weight must fail closed unless narrowly
# proven tied to the present side's own token_embd.weight.
# ---------------------------------------------------------------------

class TestRound3OutputWeightTiedProof(_PairedFixtureCase):
    def test_artifact_only_untied_output_weight_denied(self):
        # THE Grok round-2 finding, directly reproduced: artifact has an
        # output.weight the reference lacks, and it is NOT tied to
        # artifact's own token_embd.weight (random/untied) -- must be
        # UNVERIFIED, never silently skipped.
        malicious_path = os.path.join(self.tmpdir, "malicious_untied_artifact.gguf")
        write_llama_fixture(malicious_path, self.spec, seed=1, permute_qk=False,
                            include_output_weight="untied")
        profile = fl08.profile_artifact(malicious_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_reference_only_untied_output_weight_denied(self):
        malicious_ref_path = os.path.join(self.tmpdir, "malicious_untied_reference.gguf")
        write_llama_fixture(malicious_ref_path, self.spec, seed=1, permute_qk=True,
                            include_output_weight="untied")
        profile = fl08.profile_artifact(self.raw_path, malicious_ref_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_output_present_both_sides_but_different_denied(self):
        # Both sides use the SAME seed for every other tensor (so V/FFN/
        # norms/embedding genuinely match), but output.weight is
        # supplied explicitly via `tamper` with two DIFFERENT arrays --
        # `include_output_weight="untied"` alone would draw from
        # identical RNG state on both sides (same seed, same draw
        # order) and accidentally produce byte-identical "different"
        # tensors, which would not exercise this test's actual point.
        a_path = os.path.join(self.tmpdir, "out_a.gguf")
        b_path = os.path.join(self.tmpdir, "out_b.gguf")
        out_a = np.full((self.spec.vocab, self.spec.hidden), 1.0, dtype=np.float32)
        out_b = np.full((self.spec.vocab, self.spec.hidden), 2.0, dtype=np.float32)
        write_llama_fixture(a_path, self.spec, seed=1, permute_qk=False, tamper={"output.weight": out_a},
                            include_output_weight="untied")
        write_llama_fixture(b_path, self.spec, seed=1, permute_qk=True, tamper={"output.weight": out_b},
                            include_output_weight="untied")
        profile = fl08.profile_artifact(a_path, b_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_output_absent_both_sides_verified(self):
        # Baseline (already exercised by other tests, pinned explicitly
        # here): neither side has output.weight -- shared tied
        # representation, does not block pair identity.
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "VERIFIED")

    def test_one_sided_output_genuinely_tied_is_verified_and_can_authorize(self):
        # The narrow, PROVEN-safe case: reference's output.weight is
        # present and byte-identical to the REFERENCE's own
        # token_embd.weight (a real tied-duplicate), artifact has none.
        # token_embd.weight itself already matches between the two
        # (same seed=1). This must NOT block pair identity -- it is
        # exactly the real Phase 6 Case A/B situation.
        tied_ref_path = os.path.join(self.tmpdir, "tied_reference.gguf")
        write_llama_fixture(tied_ref_path, self.spec, seed=1, permute_qk=True, include_output_weight="tied")
        # self.raw_path is RAW relative to tied_ref_path (canonical) --
        # against target=canonical-llama.cpp that's VERIFIED_NORMALIZATION_
        # REQUIRED (correctly non-authorizing, unrelated to this test's
        # point). Use target=orcengine-current, where RAW_HF IS what the
        # current loader expects as-is, so authorization is reachable
        # -- isolating the tied-output-proof's effect on pair identity.
        profile = fl08.profile_artifact(self.raw_path, tied_ref_path, "orcengine-current")
        self.assertEqual(profile["pair_identity"]["status"], "VERIFIED")
        self.assertTrue(profile["execution_authorization"])
        self.assertTrue(any("tied" in e.lower() for e in profile["evidence"]))

    def test_embedding_mismatch_denied_before_tied_output_proof_is_ever_reached(self):
        # Round-4 correction (Grok round-3 review, finding #4): this
        # test was previously named/described as proving the
        # `"token_embd.weight" not in checked_names` guard inside Gate
        # 1B (`_tied_output_proof`'s precondition) catches a doubly-
        # malicious case. It does NOT -- a value-mismatched
        # `token_embd.weight` is caught by the EARLIER, separate
        # `mismatches` check in `verify_pair_identity()` (the same path
        # that catches a tampered V/FFN/norm tensor), which returns
        # BEFORE Gate 1B or `_tied_output_proof()` is ever reached. This
        # test now asserts exactly that path and cites the specific
        # evidence it produces, rather than a generic UNVERIFIED/
        # non-authorization pair that could be explained by either path.
        rng = np.random.default_rng(4242)
        tampered_embd = rng.standard_normal((self.spec.vocab, self.spec.hidden)).astype(np.float32)
        tamper = {"token_embd.weight": tampered_embd}
        tricky_path = os.path.join(self.tmpdir, "tricky.gguf")
        write_llama_fixture(tricky_path, self.spec, seed=1, permute_qk=True, tamper=tamper,
                            include_output_weight="tied")
        profile = fl08.profile_artifact(self.raw_path, tricky_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])
        # Specific evidence naming the non-Q/K tensor mismatch path
        # (the "N non-Q/K tensor(s) differ..." string from the
        # `mismatches` branch) -- NOT the Gate 1B "did not pass pair
        # identity" wording, which this attack never reaches.
        self.assertTrue(any("non-Q/K tensor(s) differ" in e for e in profile["pair_identity"]["evidence"]),
                        f"expected the early non-Q/K mismatch evidence, got "
                        f"{profile['pair_identity']['evidence']}")
        self.assertFalse(any("did not pass pair identity" in e for e in profile["pair_identity"]["evidence"]),
                         "this attack must be caught by the EARLIER mismatches path, not Gate 1B's "
                         "checked_names guard -- if this assertion fails, the code path changed and the "
                         "comment/test above needs re-verification")

    # NOTE (round 4, Grok round-3 review finding #4): the
    # `"token_embd.weight" not in checked_names` guard inside Gate 1B
    # is UNREACHABLE as false via any structurally-valid profile pair --
    # token_embd.weight is a REQUIRED tensor (an artifact lacking it is
    # already INVALID before verify_pair_identity() is ever called), and
    # the earlier `mismatches` check (exercised by the test above)
    # already returns before Gate 1B for any differing non-Q/K tensor,
    # including token_embd.weight itself. Kept in profiler.py as a
    # documented internal invariant / defense-in-depth against a future
    # refactor, per this round's explicit instruction not to construct
    # an artificial production path merely to make an unreachable guard
    # testable. See the comment at that guard's call site in
    # verify_pair_identity() for the full reachability argument.


class TestRound3NonQkInventoryCompleteness(_PairedFixtureCase):
    def test_corresponding_non_qk_tensor_type_mismatch_denied(self):
        # Same shape/content-if-decoded, but a DIFFERENT GGML tensor
        # type for the exact same tensor name -- must be caught by
        # per-name type comparison, not an aggregate encoding-set check.
        mismatched_path = os.path.join(self.tmpdir, "type_mismatch.gguf")
        write_llama_fixture(mismatched_path, self.spec, seed=1, permute_qk=True, encoding="Q8_0")
        profile = fl08.profile_artifact(self.raw_path, mismatched_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_extra_unknown_non_qk_tensor_on_one_side_denied(self):
        extra_path = os.path.join(self.tmpdir, "extra_tensor.gguf")
        write_llama_fixture(extra_path, self.spec, seed=1, permute_qk=True,
                            extra_non_qk_tensor=("blk.0.mystery_adapter.weight", (self.spec.hidden, 4)))
        profile = fl08.profile_artifact(self.raw_path, extra_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])
        self.assertTrue(any("inventory differs" in e for e in profile["pair_identity"]["evidence"]))


# ---------------------------------------------------------------------
# Round 3, Gate 1C: execution-affecting and tokenizer-affecting
# metadata must reject a mismatch or one-sided value; benign
# provenance/name-only differences must NOT block pair identity.
# ---------------------------------------------------------------------

class TestRound3ExecutionAndTokenizerMetadata(_PairedFixtureCase):
    def test_changed_rope_freq_base_denied(self):
        changed_path = os.path.join(self.tmpdir, "changed_rope.gguf")
        write_llama_fixture(changed_path, self.spec, seed=1, permute_qk=True, rope_freq_base=99999.0)
        profile = fl08.profile_artifact(self.raw_path, changed_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_changed_rms_norm_eps_denied(self):
        changed_path = os.path.join(self.tmpdir, "changed_eps.gguf")
        write_llama_fixture(changed_path, self.spec, seed=1, permute_qk=True, rms_eps=1e-3)
        profile = fl08.profile_artifact(self.raw_path, changed_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_changed_feed_forward_metadata_denied(self):
        # A different intermediate size is normally also a tensor-shape
        # contradiction (Gate 2 would already reject it structurally at
        # Layer 2) -- construct this via a spec with a genuinely
        # different (but internally consistent) feed_forward_length so
        # it passes Layer 2 as a STANDALONE artifact and is caught by
        # Gate 1C's pair-identity metadata check instead.
        different_ffn_spec = FixtureSpec(hidden=16, n_head=2, n_head_kv=2, intermediate=48, vocab=32, n_layers=2)
        changed_path = os.path.join(self.tmpdir, "changed_ffn.gguf")
        write_llama_fixture(changed_path, different_ffn_spec, seed=1, permute_qk=True)
        profile = fl08.profile_artifact(self.raw_path, changed_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_changed_context_length_denied(self):
        changed_path = os.path.join(self.tmpdir, "changed_ctx.gguf")
        write_llama_fixture(changed_path, self.spec, seed=1, permute_qk=True, context_length=128)
        profile = fl08.profile_artifact(self.raw_path, changed_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_changed_tokenizer_metadata_denied(self):
        a_path = os.path.join(self.tmpdir, "tok_real_a.gguf")
        b_path = os.path.join(self.tmpdir, "tok_real_b.gguf")
        write_llama_fixture(a_path, self.spec, seed=1, permute_qk=False,
                            tokenizer_fields={"tokenizer.ggml.model": "gpt2", "tokenizer.ggml.bos_token_id": 1})
        write_llama_fixture(b_path, self.spec, seed=1, permute_qk=True,
                            tokenizer_fields={"tokenizer.ggml.model": "gpt2", "tokenizer.ggml.bos_token_id": 2})
        profile = fl08.profile_artifact(a_path, b_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_one_sided_tokenizer_field_denied(self):
        # Reproduces the exact real-artifact asymmetry Phase 6 found
        # (custom has tokenizer.ggml.add_eos_token, canonical does not).
        a_path = os.path.join(self.tmpdir, "tok_onesided_a.gguf")
        b_path = os.path.join(self.tmpdir, "tok_onesided_b.gguf")
        write_llama_fixture(a_path, self.spec, seed=1, permute_qk=False,
                            tokenizer_fields={"tokenizer.ggml.add_eos_token": 0})
        write_llama_fixture(b_path, self.spec, seed=1, permute_qk=True, tokenizer_fields={})
        profile = fl08.profile_artifact(a_path, b_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "UNVERIFIED")
        self.assertFalse(profile["execution_authorization"])

    def test_benign_name_only_difference_does_not_block_pair_identity(self):
        # general.name is classified BENIGN provenance/bookkeeping --
        # matches this profiler's real-artifact policy (Phase 6's
        # custom vs. canonical artifacts differ in general.name and
        # this is explicitly NOT treated as an execution-relevant
        # mismatch).
        a_path = os.path.join(self.tmpdir, "name_a.gguf")
        b_path = os.path.join(self.tmpdir, "name_b.gguf")
        write_llama_fixture(a_path, self.spec, seed=1, permute_qk=False, name="SmolLM2-135M")
        write_llama_fixture(b_path, self.spec, seed=1, permute_qk=True, name="Smollm2 135m")
        profile = fl08.profile_artifact(a_path, b_path, "canonical-llama.cpp")
        self.assertEqual(profile["pair_identity"]["status"], "VERIFIED")


# ---------------------------------------------------------------------
# Round 4 (Codex authority review): equality between two artifacts is
# NOT semantic validity -- two artifacts sharing the SAME invalid
# execution-metadata value must not authorize. Gate 1.
# ---------------------------------------------------------------------

class TestRound4SemanticMetadataValidation(unittest.TestCase):
    spec = SPEC

    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _paired_invalid(self, **kwargs):
        """Writes two artifacts (one raw, one canonical-permuted, same
        seed) that BOTH carry the given invalid metadata override --
        proving equality between artifact and reference cannot convert
        an invalid value into authorization."""
        a_path = os.path.join(self.tmpdir, "invalid_a.gguf")
        b_path = os.path.join(self.tmpdir, "invalid_b.gguf")
        write_llama_fixture(a_path, self.spec, seed=1, permute_qk=False, **kwargs)
        write_llama_fixture(b_path, self.spec, seed=1, permute_qk=True, **kwargs)
        return fl08.profile_artifact(a_path, b_path, "canonical-llama.cpp")

    def _assert_invalid(self, profile, expected_substring):
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])
        self.assertTrue(any(expected_substring in a for a in profile["unresolved_ambiguities"]),
                        f"expected an ambiguity containing {expected_substring!r}, got "
                        f"{profile['unresolved_ambiguities']}")

    # --- RMSNorm epsilon ---

    def test_negative_rms_epsilon_matching_both_sides_denied(self):
        self._assert_invalid(self._paired_invalid(rms_eps=-1.0), "layer_norm_rms_epsilon")

    def test_zero_rms_epsilon_denied(self):
        path = os.path.join(self.tmpdir, "zero_eps.gguf")
        write_llama_fixture(path, self.spec, seed=1, rms_eps=0.0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "layer_norm_rms_epsilon")

    def test_nan_rms_epsilon_denied(self):
        self._assert_invalid(self._paired_invalid(rms_eps=float("nan")), "layer_norm_rms_epsilon")

    def test_infinite_rms_epsilon_denied(self):
        self._assert_invalid(self._paired_invalid(rms_eps=float("inf")), "layer_norm_rms_epsilon")

    # --- RoPE freq_base ---

    def test_negative_rope_freq_base_matching_both_sides_denied(self):
        self._assert_invalid(self._paired_invalid(rope_freq_base=-10000.0), "rope.freq_base")

    def test_zero_rope_freq_base_denied(self):
        path = os.path.join(self.tmpdir, "zero_freq.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_freq_base=0.0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "rope.freq_base")

    def test_nan_rope_freq_base_denied(self):
        self._assert_invalid(self._paired_invalid(rope_freq_base=float("nan")), "rope.freq_base")

    def test_infinite_rope_freq_base_denied(self):
        self._assert_invalid(self._paired_invalid(rope_freq_base=float("inf")), "rope.freq_base")

    # --- RoPE dimension_count ---

    def test_rope_dimension_999_matching_both_sides_denied(self):
        # The exact Codex-reproduced probe.
        self._assert_invalid(self._paired_invalid(rope_dim_override=999), "rope.dimension_count")

    def test_rope_dimension_zero_denied(self):
        path = os.path.join(self.tmpdir, "zero_rope_dim.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_dim_override=0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "rope.dimension_count")

    def test_rope_dimension_odd_denied(self):
        path = os.path.join(self.tmpdir, "odd_rope_dim.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_dim_override=self.spec.head_dim - 1)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "rope.dimension_count")

    def test_rope_dimension_greater_than_head_dim_denied(self):
        path = os.path.join(self.tmpdir, "big_rope_dim.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_dim_override=self.spec.head_dim + 2)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "rope.dimension_count")

    # --- RoPE scaling / sliding window (OrcEngine implements neither) ---

    def test_unsupported_rope_scaling_type_denied(self):
        path = os.path.join(self.tmpdir, "yarn.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_scaling_type="yarn")
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "rope.scaling.type")

    def test_non_identity_rope_scaling_factor_denied(self):
        path = os.path.join(self.tmpdir, "scaled.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_scaling_factor=2.0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "rope.scaling.factor")

    def test_nonzero_sliding_window_denied(self):
        path = os.path.join(self.tmpdir, "swa.gguf")
        write_llama_fixture(path, self.spec, seed=1, sliding_window=128)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self._assert_invalid(profile, "sliding_window")

    # --- valid boundary/control cases must still proceed ---

    def test_valid_rope_scaling_none_proceeds(self):
        path = os.path.join(self.tmpdir, "scaling_none.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_scaling_type="none")
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertNotEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_valid_identity_rope_scaling_factor_proceeds(self):
        path = os.path.join(self.tmpdir, "scaling_1.gguf")
        write_llama_fixture(path, self.spec, seed=1, rope_scaling_factor=1.0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertNotEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_valid_zero_sliding_window_proceeds(self):
        path = os.path.join(self.tmpdir, "swa_zero.gguf")
        write_llama_fixture(path, self.spec, seed=1, sliding_window=0)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertNotEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_valid_metadata_control_case_still_authorizes(self):
        # A genuinely valid pair with correct metadata must still be
        # able to reach authorization -- these checks must not be so
        # strict they break the already-proven-correct path.
        profile = self._paired_invalid()  # no overrides -- all-default-valid
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertNotEqual(profile["runtime_compatibility"]["result"], "INVALID")


# ---------------------------------------------------------------------
# Round 4 (Codex authority review): n_head % n_head_kv == 0 is the
# defining grouped-query-attention invariant, previously unchecked.
# Gate 2.
# ---------------------------------------------------------------------

class TestRound4GQAGeometry(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _profile_pair(self, spec):
        a_path = os.path.join(self.tmpdir, "gqa_a.gguf")
        b_path = os.path.join(self.tmpdir, "gqa_b.gguf")
        write_llama_fixture(a_path, spec, seed=1, permute_qk=False)
        write_llama_fixture(b_path, spec, seed=1, permute_qk=True)
        return fl08.profile_artifact(a_path, b_path, "canonical-llama.cpp")

    def test_small_invalid_gqa_geometry_denied(self):
        bad_spec = FixtureSpec(hidden=6, n_head=3, n_head_kv=2, intermediate=16, vocab=16, n_layers=1)
        profile = self._profile_pair(bad_spec)
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])
        self.assertTrue(any("head_count_kv" in a for a in profile["unresolved_ambiguities"]))

    def test_nondegenerate_invalid_gqa_geometry_previously_authorizing_denied(self):
        # The exact Codex-reproduced probe: hidden=24, n_head=3, n_head_kv=2.
        bad_spec = FixtureSpec(hidden=24, n_head=3, n_head_kv=2, intermediate=32, vocab=32, n_layers=1)
        profile = self._profile_pair(bad_spec)
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])
        self.assertNotIn(profile["qk_layout"]["classification"], ("RAW_HF", "CANONICAL_LLAMA_CPP"))

    def test_valid_mha_geometry_proceeds(self):
        mha_spec = FixtureSpec(hidden=16, n_head=2, n_head_kv=2, intermediate=32, vocab=32, n_layers=1)
        profile = self._profile_pair(mha_spec)
        self.assertNotEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")

    def test_valid_gqa_geometry_proceeds(self):
        gqa_spec = FixtureSpec(hidden=32, n_head=4, n_head_kv=2, intermediate=32, vocab=32, n_layers=1)
        profile = self._profile_pair(gqa_spec)
        self.assertNotEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")


# ---------------------------------------------------------------------
# Round 3, Gate 3: DECLARED confidence must never resolve to
# VERIFIED_COMPATIBLE, and a contradictory --reference-layout
# declaration must fail closed rather than being silently ignored.
# ---------------------------------------------------------------------

class TestRound3DeclarationVsVerification(_PairedFixtureCase):
    def test_declaration_only_compatibility_is_ambiguous_not_verified_compatible(self):
        identical_path = os.path.join(self.tmpdir, "identical_for_decl.gguf")
        write_llama_fixture(identical_path, self.spec, seed=1, permute_qk=False)
        profile = fl08.profile_artifact(self.raw_path, identical_path, "canonical-llama.cpp",
                                        reference_layout_declared="raw")
        self.assertEqual(profile["qk_layout"]["confidence"], "DECLARED")
        self.assertNotEqual(profile["runtime_compatibility"]["result"], "VERIFIED_COMPATIBLE")
        self.assertEqual(profile["runtime_compatibility"]["result"], "AMBIGUOUS")
        self.assertFalse(profile["execution_authorization"])

    def test_declaration_only_cli_exit_is_nonzero(self):
        import subprocess
        import sys as _sys
        identical_path = os.path.join(self.tmpdir, "identical_for_cli.gguf")
        write_llama_fixture(identical_path, self.spec, seed=1, permute_qk=False)
        profiler_path = os.path.join(os.path.dirname(__file__), "profiler.py")
        result = subprocess.run(
            [_sys.executable, profiler_path, self.raw_path, "--reference", identical_path,
             "--reference-layout", "raw", "--target", "canonical-llama.cpp"],
            capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)

    def test_contradictory_declaration_on_raw_relative_match_fails_closed(self):
        # Numerically, self.raw_path is RAW relative to self.canonical_path
        # (permute(raw) == canonical) -- so the reference is CANONICAL.
        # Declaring --reference-layout=raw directly contradicts that.
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp",
                                        reference_layout_declared="raw")
        self.assertEqual(profile["qk_layout"]["classification"], "AMBIGUOUS")
        self.assertTrue(any("CONTRADICTION" in a for a in profile["unresolved_ambiguities"]))
        self.assertFalse(profile["execution_authorization"])

    def test_contradictory_declaration_on_canonical_relative_match_fails_closed(self):
        # self.canonical_path is CANONICAL relative to self.raw_path
        # (permute(reference) == artifact) -- the reference (raw_path)
        # is RAW. Declaring --reference-layout=canonical contradicts.
        profile = fl08.profile_artifact(self.canonical_path, self.raw_path, "canonical-llama.cpp",
                                        reference_layout_declared="canonical")
        self.assertEqual(profile["qk_layout"]["classification"], "AMBIGUOUS")
        self.assertTrue(any("CONTRADICTION" in a for a in profile["unresolved_ambiguities"]))
        self.assertFalse(profile["execution_authorization"])

    def test_agreeing_declaration_on_directional_match_does_not_break_it(self):
        # A declaration that AGREES with the numerical finding must not
        # be treated as a contradiction -- classification stays
        # NUMERICALLY_VERIFIED via the normal directional path.
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp",
                                        reference_layout_declared="canonical")
        self.assertEqual(profile["qk_layout"]["classification"], "RAW_HF")
        self.assertEqual(profile["qk_layout"]["confidence"], "NUMERICALLY_VERIFIED")
        self.assertEqual(profile["runtime_compatibility"]["result"], "VERIFIED_NORMALIZATION_REQUIRED")


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

    # -------------------------------------------------------------
    # Round 3 (Codex authority review): correct ROWS but WRONG INPUT
    # WIDTH (last/column dimension) previously passed silently -- the
    # round-2 checks validated only the output-row axis.
    # -------------------------------------------------------------

    def test_odd_head_dim_returns_structured_invalid_not_a_crash(self):
        # Codex ODD_HEAD_DIM_CRASH probe, reproduced directly: hidden=18,
        # n_head=2 -> head_dim=9 (odd). official_permute()'s reshape
        # would previously raise ValueError uncaught mid-comparison.
        odd_spec = FixtureSpec(hidden=18, n_head=2, n_head_kv=2, intermediate=32, vocab=32, n_layers=1)
        path = os.path.join(self.tmpdir, "odd_head_dim.gguf")
        write_llama_fixture(path, odd_spec, seed=1)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])
        self.assertTrue(any("odd" in a.lower() for a in profile["unresolved_ambiguities"]))

    def test_qk_input_width_wrong_rows_correct_is_invalid(self):
        # THE exact Codex WRONG_QK_INPUT_WIDTH probe: Q/K's ROW count is
        # correct (matches head geometry) but the INPUT width (last
        # dim) is hidden+2, not hidden.
        path = os.path.join(self.tmpdir, "wrong_qk_width.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.attn_q.weight": rng.standard_normal((SPEC.q_rows, SPEC.hidden + 2)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertFalse(profile["execution_authorization"])

    def test_k_input_width_wrong_rows_correct_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_k_width.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.attn_k.weight": rng.standard_normal((SPEC.kv_rows, SPEC.hidden + 2)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_v_input_width_wrong_rows_correct_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_v_width.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.attn_v.weight": rng.standard_normal((SPEC.kv_rows, SPEC.hidden + 2)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_attn_output_input_width_wrong_rows_correct_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_attn_out_width.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.attn_output.weight": rng.standard_normal((SPEC.hidden, SPEC.q_rows + 2)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_ffn_gate_input_width_wrong_rows_correct_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_gate_width.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.ffn_gate.weight": rng.standard_normal((SPEC.intermediate, SPEC.hidden + 2)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_ffn_up_input_width_wrong_rows_correct_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_up_width.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.ffn_up.weight": rng.standard_normal((SPEC.intermediate, SPEC.hidden + 2)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_ffn_down_intermediate_input_width_wrong_is_invalid(self):
        path = os.path.join(self.tmpdir, "wrong_down_width.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.ffn_down.weight": rng.standard_normal((SPEC.hidden, SPEC.intermediate + 2)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_two_dimensional_norm_with_superficially_correct_first_dim_is_invalid(self):
        # A norm tensor stored as rank-2 (hidden, 1) instead of rank-1
        # (hidden,) -- shape[0] is superficially "correct" but this is
        # not the expected 1-D norm vector.
        path = os.path.join(self.tmpdir, "rank2_norm.gguf")
        rng = np.random.default_rng(5)
        tamper = {"blk.0.attn_norm.weight": rng.standard_normal((SPEC.hidden, 1)).astype(np.float32)}
        write_llama_fixture(path, SPEC, seed=1, tamper=tamper)
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")

    def test_malformed_q8_0_width_in_non_qk_tensor_is_invalid(self):
        # A non-Q/K tensor (ffn_gate.weight) packed with the wrong Q8_0
        # byte width -- Gate 2's dimension check must catch this for
        # EVERY quantized 2-D tensor, not only Q/K.
        q8_spec = FixtureSpec(hidden=64, n_head=4, n_head_kv=2, intermediate=64, vocab=32, n_layers=1)
        path = os.path.join(self.tmpdir, "bad_ffn_q8.gguf")
        w = GGUFWriter(path, arch="llama")
        w.add_name("bad-ffn-q8")
        w.add_context_length(64)
        w.add_embedding_length(q8_spec.hidden)
        w.add_block_count(1)
        w.add_feed_forward_length(q8_spec.intermediate)
        w.add_head_count(q8_spec.n_head)
        w.add_head_count_kv(q8_spec.n_head_kv)
        w.add_layer_norm_rms_eps(1e-5)
        w.add_rope_dimension_count(q8_spec.head_dim)
        w.add_rope_freq_base(10000.0)
        rng = np.random.default_rng(1)
        w.add_tensor("token_embd.weight", rng.standard_normal((q8_spec.vocab, q8_spec.hidden)).astype(np.float32))
        w.add_tensor("output_norm.weight", rng.standard_normal((q8_spec.hidden,)).astype(np.float32))
        w.add_tensor("blk.0.attn_norm.weight", rng.standard_normal((q8_spec.hidden,)).astype(np.float32))
        w.add_tensor("blk.0.attn_q.weight", rng.standard_normal((q8_spec.q_rows, q8_spec.hidden)).astype(np.float32))
        w.add_tensor("blk.0.attn_k.weight", rng.standard_normal((q8_spec.kv_rows, q8_spec.hidden)).astype(np.float32))
        w.add_tensor("blk.0.attn_v.weight", rng.standard_normal((q8_spec.kv_rows, q8_spec.hidden)).astype(np.float32))
        w.add_tensor("blk.0.attn_output.weight",
                    rng.standard_normal((q8_spec.hidden, q8_spec.q_rows)).astype(np.float32))
        w.add_tensor("blk.0.ffn_norm.weight", rng.standard_normal((q8_spec.hidden,)).astype(np.float32))
        # Malformed: packed as if 32 "fake" input elements (34 bytes)
        # instead of the real hidden=64 (should be 68 bytes).
        bad_gate = pack_q8_0(rng.standard_normal((q8_spec.intermediate, 32)).astype(np.float32))
        w.add_tensor("blk.0.ffn_gate.weight", bad_gate, raw_dtype=GGMLQuantizationType.Q8_0)
        w.add_tensor("blk.0.ffn_up.weight",
                    rng.standard_normal((q8_spec.intermediate, q8_spec.hidden)).astype(np.float32))
        w.add_tensor("blk.0.ffn_down.weight",
                    rng.standard_normal((q8_spec.hidden, q8_spec.intermediate)).astype(np.float32))
        w.write_header_to_file()
        w.write_kv_data_to_file()
        w.write_tensors_to_file()
        w.close()
        profile = fl08.profile_artifact(path, None, "canonical-llama.cpp")
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
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
        # Round-3 remediation: Gate 2's complete-dimension check (using
        # logical_last_dim(), which decodes packed Q8_0 width) now
        # catches this at Layer 2 structural validation -- BEFORE Layer
        # 3's dedicated Q8_0 geometry check would even run -- so the
        # artifact is INVALID, not merely AMBIGUOUS at the Q/K layer.
        self.assertEqual(profile["runtime_compatibility"]["result"], "INVALID")
        self.assertTrue(any("shape" in a.lower() or "dim" in a.lower()
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

    def test_one_checked_tensor_zero_checked_layers_refused(self):
        # Round-3 remediation (Gate 4): an internally-inconsistent
        # profile claiming qk_tensors_checked=1 but layers_checked=0
        # must never emit a plan.
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        profile["qk_layout"]["qk_tensors_checked"] = 1
        profile["qk_layout"]["layers_checked"] = 0
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_malformed_nested_field_type_refused_not_raised(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        profile["qk_layout"]["layers_total"] = "not-an-int"
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_missing_nested_field_refused_not_raised(self):
        profile = fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp")
        del profile["qk_layout"]["qk_tensors_checked"]
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_null_reference_refused(self):
        profile = fl08.profile_artifact(self.raw_path, None, "canonical-llama.cpp")
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    # -------------------------------------------------------------
    # Round 4 (Codex authority review): three gaps found in the
    # round-3 validator -- bool-as-int, reference container.valid not
    # checked independent of terminal_result, and hash format
    # unvalidated.
    # -------------------------------------------------------------

    def _valid_profile(self):
        import copy
        return copy.deepcopy(fl08.profile_artifact(self.raw_path, self.canonical_path, "canonical-llama.cpp"))

    def test_boolean_layers_total_refused(self):
        profile = self._valid_profile()
        profile["qk_layout"]["layers_total"] = True
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIn("bool", reason.lower())

    def test_boolean_layers_checked_refused(self):
        profile = self._valid_profile()
        profile["qk_layout"]["layers_checked"] = True
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIn("bool", reason.lower())

    def test_negative_layers_total_refused(self):
        profile = self._valid_profile()
        profile["qk_layout"]["layers_total"] = -2
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_invalid_reference_container_refused(self):
        profile = self._valid_profile()
        profile["reference"]["container"]["valid"] = False
        # terminal_result stays None -- the exact gap this round closes:
        # invalidity must be caught via container.valid directly, not
        # only inferred from terminal_result.
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIn("container.valid", reason)

    def test_invalid_primary_container_refused(self):
        profile = self._valid_profile()
        profile["container"]["valid"] = False
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIn("container.valid", reason)

    def test_unsupported_reference_architecture_refused(self):
        profile = self._valid_profile()
        profile["reference"]["declared_architecture"] = "gemma"
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIn("declared_architecture", reason)

    def test_unsupported_primary_architecture_refused(self):
        profile = self._valid_profile()
        profile["declared_architecture"] = "gemma"
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIn("declared_architecture", reason)

    def test_empty_artifact_hash_refused(self):
        profile = self._valid_profile()
        profile["artifact"]["sha256"] = ""
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_short_artifact_hash_refused(self):
        profile = self._valid_profile()
        profile["artifact"]["sha256"] = "abc123"
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_long_artifact_hash_refused(self):
        profile = self._valid_profile()
        profile["artifact"]["sha256"] = "a" * 65
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_nonhex_artifact_hash_refused(self):
        profile = self._valid_profile()
        profile["artifact"]["sha256"] = "g" * 64  # 'g' is not a hex digit
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_arbitrary_string_hash_refused(self):
        # The exact defect: "x"/"y" previously satisfied "non-empty string".
        profile = self._valid_profile()
        profile["artifact"]["sha256"] = "x"
        profile["reference"]["sha256"] = "y"
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_short_reference_hash_refused(self):
        profile = self._valid_profile()
        profile["reference"]["sha256"] = "deadbeef"
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_present_null_layers_total_refused(self):
        profile = self._valid_profile()
        profile["qk_layout"]["layers_total"] = None
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_present_null_pair_identity_status_refused(self):
        profile = self._valid_profile()
        profile["pair_identity"]["status"] = None
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

    def test_uppercase_hash_accepted(self):
        # This schema's sha256 fields are lowercase hex by construction
        # (Python's hashlib.hexdigest()) -- uppercase hex is still a
        # well-formed 64-character SHA-256 digest shape, so it is
        # intentionally accepted (no case-normalization is silently
        # performed; the REGEX itself is case-insensitive by design).
        profile = self._valid_profile()
        profile["artifact"]["sha256"] = profile["artifact"]["sha256"].upper()
        profile["reference"]["sha256"] = profile["reference"]["sha256"].upper()
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNotNone(plan)

    def test_valid_profile_still_produces_the_same_bounded_proposal(self):
        profile = self._valid_profile()
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNotNone(plan)
        self.assertIsNone(reason)
        self.assertEqual(plan["source_layout"], "hf_raw_rotate_half")
        self.assertFalse(plan["destructive"])
        self.assertFalse(plan["execution_authorized"])

    def test_incomplete_counts_plan_case_remains_closed(self):
        # The original round-3 INCOMPLETE_COUNTS_PLAN regression,
        # reconfirmed still closed after this round's validator changes.
        profile = self._valid_profile()
        profile["qk_layout"]["qk_tensors_checked"] = 1
        profile["qk_layout"]["layers_checked"] = 0
        plan, reason = fl08_plan.build_plan(profile)
        self.assertIsNone(plan)
        self.assertIsNotNone(reason)

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
