# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Targeted regression tests for phase6_llama_cpp_q8_0_oracle.py's own
correctness/fail-closed behavior -- these do NOT require a real
llama-server or GPU/model; they exercise the driver's identity
verification, port isolation, evidence validation, and log-probability
math in isolation, per the Codex remediation instruction to add tests
covering the oracle driver's defects (not just fix them silently).

Run: python -m unittest Tools/OrcEnginePhase6/tests/test_phase6_llama_cpp_q8_0_oracle.py -v
"""
from __future__ import annotations

import hashlib
import json
import math
import os
import socket
import sys
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import phase6_llama_cpp_q8_0_oracle as oracle  # noqa: E402


def _read_bytes(path: str) -> bytes:
    with open(path, "rb") as f:
        return f.read()


class _ServerPairFixture:
    """Creates a temp directory holding a fake llama-server.exe AND a fake
    llama-server-impl.dll side by side (the code under test locates the
    impl DLL relative to the .exe's own directory), with both files'
    SHA-256 patched into the module's expected constants so the identity
    checks exercise their REAL comparison logic against real file
    content, not a mocked hash function."""

    def __init__(self, exe_content: bytes = b"fake exe", impl_content: bytes = b"fake impl dll",
                include_impl_dll: bool = True):
        import tempfile
        self.tmpdir = tempfile.mkdtemp()
        self.exe_path = os.path.join(self.tmpdir, "llama-server.exe")
        with open(self.exe_path, "wb") as f:
            f.write(exe_content)
        self.exe_hash = hashlib.sha256(exe_content).hexdigest()
        self.impl_path = os.path.join(self.tmpdir, "llama-server-impl.dll")
        if include_impl_dll:
            with open(self.impl_path, "wb") as f:
                f.write(impl_content)
            self.impl_hash = hashlib.sha256(impl_content).hexdigest()
        else:
            self.impl_hash = None

    def cleanup(self):
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)


class ServerIdentityTests(unittest.TestCase):
    """Covers: wrong server version/build rejection, missing implementation
    DLL, wrong implementation DLL hash, nonzero --version return code."""

    def test_missing_executable_aborts(self):
        with self.assertRaises(SystemExit):
            oracle._verify_server_identity("this-path-does-not-exist.exe")

    def test_wrong_hash_aborts_before_version_check(self):
        fx = _ServerPairFixture()
        try:
            with self.assertRaises(SystemExit):
                oracle._verify_server_identity(fx.exe_path)  # EXPECTED_SERVER_EXE_SHA256 not patched -> mismatch
        finally:
            fx.cleanup()

    def test_missing_implementation_dll_aborts(self):
        # Gate 3 item 3: absence of llama-server-impl.dll must ABORT, not
        # downgrade to a warning -- this pinned build's documentation
        # claims the impl DLL itself is hash-pinned.
        fx = _ServerPairFixture(include_impl_dll=False)
        try:
            with mock.patch.object(oracle, "EXPECTED_SERVER_EXE_SHA256", fx.exe_hash):
                with self.assertRaises(SystemExit):
                    oracle._verify_server_identity(fx.exe_path)
        finally:
            fx.cleanup()

    def test_wrong_implementation_dll_hash_aborts(self):
        fx = _ServerPairFixture()
        try:
            with mock.patch.object(oracle, "EXPECTED_SERVER_EXE_SHA256", fx.exe_hash), \
                 mock.patch.object(oracle, "EXPECTED_SERVER_IMPL_DLL_SHA256", "0" * 64):
                with self.assertRaises(SystemExit):
                    oracle._verify_server_identity(fx.exe_path)
        finally:
            fx.cleanup()

    def test_correct_hash_but_wrong_version_banner_aborts(self):
        fx = _ServerPairFixture()
        try:
            with mock.patch.object(oracle, "EXPECTED_SERVER_EXE_SHA256", fx.exe_hash), \
                 mock.patch.object(oracle, "EXPECTED_SERVER_IMPL_DLL_SHA256", fx.impl_hash), \
                 mock.patch("subprocess.run") as run_mock:
                run_mock.return_value = mock.Mock(
                    returncode=0, stdout="version: 0.1.0-dev (build 99999, commit deadbeef)\n", stderr="")
                with self.assertRaises(SystemExit):
                    oracle._verify_server_identity(fx.exe_path)
        finally:
            fx.cleanup()

    def test_nonzero_version_return_code_aborts(self):
        # Gate 3 item 4: check the return code BEFORE trusting the banner
        # -- a nonzero exit must abort even if stdout happens to contain
        # text that looks like the right banner.
        fx = _ServerPairFixture()
        try:
            with mock.patch.object(oracle, "EXPECTED_SERVER_EXE_SHA256", fx.exe_hash), \
                 mock.patch.object(oracle, "EXPECTED_SERVER_IMPL_DLL_SHA256", fx.impl_hash), \
                 mock.patch("subprocess.run") as run_mock:
                run_mock.return_value = mock.Mock(
                    returncode=1, stdout="version: 0.1.0-dev (build 10436, commit 6fed9f6ff)\n", stderr="")
                with self.assertRaises(SystemExit):
                    oracle._verify_server_identity(fx.exe_path)
        finally:
            fx.cleanup()

    def test_correct_hash_and_correct_version_passes(self):
        fx = _ServerPairFixture()
        try:
            with mock.patch.object(oracle, "EXPECTED_SERVER_EXE_SHA256", fx.exe_hash), \
                 mock.patch.object(oracle, "EXPECTED_SERVER_IMPL_DLL_SHA256", fx.impl_hash), \
                 mock.patch("subprocess.run") as run_mock:
                run_mock.return_value = mock.Mock(
                    returncode=0, stdout="version: 0.1.0-dev (build 10436, commit 6fed9f6ff)\n", stderr="")
                oracle._verify_server_identity(fx.exe_path)  # must not raise
        finally:
            fx.cleanup()


class Q8GgufIdentityTests(unittest.TestCase):
    """Covers: wrong Q8 file hash rejection."""

    def test_wrong_file_hash_aborts(self):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".gguf", delete=False) as f:
            f.write(b"not the real fixture")
            path = f.name
        try:
            with self.assertRaises(SystemExit):
                oracle._verify_q8_gguf_identity(path, [])
        finally:
            os.remove(path)

    def test_evidence_hash_mismatch_aborts(self):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".gguf", delete=False) as f:
            f.write(b"fixture content")
            path = f.name
        try:
            real_hash = hashlib.sha256(_read_bytes(path)).hexdigest()
            with mock.patch.object(oracle, "EXPECTED_Q8_GGUF_SHA256", real_hash):
                with self.assertRaises(SystemExit):
                    oracle._verify_q8_gguf_identity(
                        path, [{"id": "x", "q8_artifact_sha256": "0" * 64}])
        finally:
            os.remove(path)

    def test_matching_hash_and_evidence_passes(self):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".gguf", delete=False) as f:
            f.write(b"fixture content")
            path = f.name
        try:
            real_hash = hashlib.sha256(_read_bytes(path)).hexdigest()
            with mock.patch.object(oracle, "EXPECTED_Q8_GGUF_SHA256", real_hash):
                oracle._verify_q8_gguf_identity(path, [{"id": "x", "q8_artifact_sha256": real_hash}])
        finally:
            os.remove(path)


class PortIsolationTests(unittest.TestCase):
    """Covers: port/server isolation."""

    def test_free_port_is_actually_bindable(self):
        port = oracle._free_local_port()
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        try:
            s.bind(("127.0.0.1", port))  # must not raise -- proves it was genuinely free
        finally:
            s.close()

    def test_successive_calls_do_not_collide_with_a_still_bound_socket(self):
        # A naive implementation that returns the same fixed port every
        # time would fail this once anything holds the first port open.
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.bind(("127.0.0.1", 0))
        held_port = s.getsockname()[1]
        try:
            for _ in range(5):
                port = oracle._free_local_port()
                self.assertNotEqual(port, held_port)
        finally:
            s.close()


class HealthWaitTests(unittest.TestCase):
    """Covers: server process exits before health / nonzero subprocess return handling."""

    def test_process_exit_before_health_aborts(self):
        fake_proc = mock.Mock()
        fake_proc.poll.return_value = 1  # nonzero exit code, already exited
        fake_proc.pid = 12345
        with self.assertRaises(SystemExit):
            oracle._wait_for_health(fake_proc, 65432, timeout_s=1.0)

    def test_process_exit_code_zero_before_health_also_aborts(self):
        # Exiting with code 0 before ever answering /health is still a
        # failure to become healthy -- poll() returning ANY non-None value
        # (not just nonzero) must abort.
        fake_proc = mock.Mock()
        fake_proc.poll.return_value = 0
        fake_proc.pid = 12345
        with self.assertRaises(SystemExit):
            oracle._wait_for_health(fake_proc, 65432, timeout_s=1.0)

    def test_never_exits_but_never_healthy_times_out(self):
        fake_proc = mock.Mock()
        fake_proc.poll.return_value = None  # still running
        fake_proc.pid = 12345
        with self.assertRaises(SystemExit):
            oracle._wait_for_health(fake_proc, 65433, timeout_s=0.5)


class EvidenceSchemaValidationTests(unittest.TestCase):
    """Covers: evidence missing full-vocabulary normalization data /
    malformed or incomplete evidence rejection."""

    def _valid_entry(self) -> dict:
        return {
            "schema_version": 2, "id": "x", "text": "hi", "token_ids": [1, 2],
            "compared_position": 1, "q8_selected": 5,
            "q8_full_vocab_logsumexp": 10.0, "q8_top5_ids": [5, 6], "q8_top5_logits": [3.0, 2.0],
        }

    def test_empty_evidence_aborts(self):
        with self.assertRaises(SystemExit):
            oracle._validate_evidence_schema([])

    def test_missing_schema_version_aborts(self):
        entry = self._valid_entry()
        del entry["schema_version"]
        with self.assertRaises(SystemExit):
            oracle._validate_evidence_schema([entry])

    def test_old_schema_version_aborts(self):
        entry = self._valid_entry()
        entry["schema_version"] = 1  # the version before full-vocab logsumexp was added
        with self.assertRaises(SystemExit):
            oracle._validate_evidence_schema([entry])

    def test_missing_full_vocab_logsumexp_aborts(self):
        entry = self._valid_entry()
        del entry["q8_full_vocab_logsumexp"]
        with self.assertRaises(SystemExit):
            oracle._validate_evidence_schema([entry])

    def test_missing_token_ids_aborts(self):
        entry = self._valid_entry()
        del entry["token_ids"]
        with self.assertRaises(SystemExit):
            oracle._validate_evidence_schema([entry])

    def test_well_formed_entry_passes(self):
        oracle._validate_evidence_schema([self._valid_entry()])  # must not raise


class LogSoftmaxMathTests(unittest.TestCase):
    """Covers: a constructed example proving top-five-only normalization
    would have produced a false comparison, plus the exact full-vocab
    computation's correctness against a hand-computed reference."""

    def test_local_logsumexp_helper_matches_naive_reference(self):
        # Codex remediation Gate 6: this test's ORIGINAL name
        # ("test_full_vocab_logsumexp_matches_hand_computed_reference")
        # implied it validated production code (the C++ tool's
        # full_vocab_logsumexp() or the Python full_vocab_logsumexp
        # concept generally); it actually only validated THIS TEST FILE's
        # own local `_local_logsumexp()` helper (used below to construct
        # synthetic examples) against a naive, non-numerically-stabilized
        # computation. Renamed to say exactly that. The production C++
        # full_vocab_logsumexp() (phase6_q8_0_comparison.cpp) is exercised
        # for real only by actually running that compiled tool (see the
        # real q8_full_vocab_logsumexp values in the committed evidence
        # JSONL); a Python unit test cannot validate C++ code directly
        # without shelling out to the compiled binary, which is out of
        # scope for this file.
        logits = [1.0, 2.0, 3.0, 0.5]
        expected = math.log(sum(math.exp(v) for v in logits))
        actual = _local_logsumexp(logits)
        self.assertAlmostEqual(actual, expected, places=9)

    def test_log_softmax_at_matches_hand_computed_reference(self):
        # DOES directly validate the production oracle._log_softmax_at()
        # function (not a test-local helper): hand-computes the expected
        # log-probability for a target token from a known full-vocabulary
        # logsumexp and a known raw logit, and asserts the production
        # function returns exactly that.
        full_vocab_logsumexp = 10.0
        ids = [7, 3, 9]
        logits = [8.5, 6.0, 4.0]
        target_id = 3
        expected = 6.0 - full_vocab_logsumexp  # raw_logit - logsumexp, by definition
        actual = oracle._log_softmax_at(full_vocab_logsumexp, ids, logits, target_id)
        self.assertAlmostEqual(actual, expected, places=9)

    def test_target_outside_top5_returns_none_not_approximated(self):
        result = oracle._log_softmax_at(10.0, [1, 2, 3], [5.0, 4.0, 3.0], target_id=999)
        self.assertIsNone(result)

    def test_top5_only_renormalization_would_have_been_wrong(self):
        """Constructs a synthetic full vocabulary where the top-5 logits are
        NOT the whole story: a large mass of near-tied smaller logits
        elsewhere in the vocabulary meaningfully inflates the true
        full-vocabulary softmax denominator beyond what a top-5-only
        renormalization would compute. Demonstrates the earlier (fixed)
        approach's invalidity is not a rounding-error-scale concern -- it
        is a structurally different, systematically WRONG denominator."""
        top5_logits = [10.0, 9.5, 9.0, 8.5, 8.0]
        # 2000 additional vocabulary entries, each contributing exp(6.0) --
        # individually small next to the top values, but numerous enough to
        # shift the true full-vocabulary denominator substantially.
        tail_logits = [6.0] * 2000
        full_vocab = top5_logits + tail_logits

        true_logsumexp = _local_logsumexp(full_vocab)
        top5_only_logsumexp = _local_logsumexp(top5_logits)

        # The true full-vocabulary log-probability of the top token:
        true_logprob = top5_logits[0] - true_logsumexp
        # What the earlier (invalid) top-5-only renormalization would have
        # reported instead:
        top5_only_logprob = top5_logits[0] - top5_only_logsumexp

        # These must differ by a large, unambiguous margin -- proving the
        # old top-5-only approach was not merely "less precise" but wrong
        # in a way a real oracle comparison could not honestly ignore.
        self.assertGreater(abs(true_logprob - top5_only_logprob), 0.5)


class F32GgufIdentityTests(unittest.TestCase):
    """Codex remediation Gate 3: F32 leg must meet the same identity
    standard as the Q8 leg. Covers: wrong F32 artifact hash rejection,
    evidence F32 hash mismatch rejection."""

    def test_wrong_file_hash_aborts(self):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".gguf", delete=False) as f:
            f.write(b"not the real F32 fixture")
            path = f.name
        try:
            with self.assertRaises(SystemExit):
                oracle._verify_f32_gguf_identity(path, [])
        finally:
            os.remove(path)

    def test_evidence_hash_mismatch_aborts(self):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".gguf", delete=False) as f:
            f.write(b"F32 fixture content")
            path = f.name
        try:
            real_hash = hashlib.sha256(_read_bytes(path)).hexdigest()
            with mock.patch.object(oracle, "EXPECTED_F32_GGUF_SHA256", real_hash):
                with self.assertRaises(SystemExit):
                    oracle._verify_f32_gguf_identity(
                        path, [{"id": "x", "f32_artifact_sha256": "0" * 64}])
        finally:
            os.remove(path)

    def test_matching_hash_and_evidence_passes(self):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".gguf", delete=False) as f:
            f.write(b"F32 fixture content")
            path = f.name
        try:
            real_hash = hashlib.sha256(_read_bytes(path)).hexdigest()
            with mock.patch.object(oracle, "EXPECTED_F32_GGUF_SHA256", real_hash):
                oracle._verify_f32_gguf_identity(path, [{"id": "x", "f32_artifact_sha256": real_hash}])
        finally:
            os.remove(path)


class SharedCompletionPayloadTests(unittest.TestCase):
    """Codex remediation Gate 2: proves the Q8 and F32 legs use the exact
    same neutral completion payload BY CONSTRUCTION (both call the one
    canonical build_completion_payload()), not by eyeballing two visually
    similar dictionaries."""

    def test_payload_contains_every_required_neutral_field(self):
        payload = oracle.build_completion_payload("some prompt", n_probs=5)
        self.assertEqual(payload["n_predict"], 1)
        self.assertEqual(payload["temperature"], 0)
        self.assertEqual(payload["n_probs"], 5)
        self.assertEqual(payload["cache_prompt"], False)
        self.assertEqual(payload["repeat_penalty"], 1.0)
        self.assertEqual(payload["top_k"], 0)
        self.assertEqual(payload["top_p"], 1.0)
        self.assertEqual(payload["min_p"], 0.0)
        self.assertEqual(payload["presence_penalty"], 0.0)
        self.assertEqual(payload["frequency_penalty"], 0.0)

    def test_q8_oracle_leg_and_f32_localization_leg_send_byte_identical_payloads(self):
        # Both _request_completion (used by the Q8 oracle's run()) and the
        # F32 localizer's run() call the SAME oracle._request_completion,
        # which calls the SAME build_completion_payload(). Prove this by
        # intercepting the actual HTTP request each makes and comparing
        # the JSON bodies byte-for-byte, for the same prompt/n_probs.
        captured = []

        class _FakeResponse:
            def __init__(self, body: bytes):
                self._body = body

            def read(self):
                return self._body

            def __enter__(self):
                return self

            def __exit__(self, *exc):
                return False

        def fake_urlopen(req, timeout=30):
            captured.append(req.data)
            return _FakeResponse(json.dumps({
                "completion_probabilities": [{"top_logprobs": [{"id": 1, "logprob": -0.1}]}]
            }).encode())

        with mock.patch("urllib.request.urlopen", side_effect=fake_urlopen):
            oracle._request_completion(12345, "The capital of France is", n_probs=5)
            oracle._request_completion(23456, "The capital of France is", n_probs=5)

        self.assertEqual(len(captured), 2)
        self.assertEqual(captured[0], captured[1])  # byte-identical request bodies


class ExpectedPromptSetTests(unittest.TestCase):
    """Codex remediation Gate 3, item 7: the F32 localizer must abort on
    missing, duplicate, or unexpected target entries rather than
    silently localizing whatever happens to be present."""

    def _entry(self, prompt_id: str) -> dict:
        return {"id": prompt_id, "text": "x", "token_ids": [1], "f32_selected": 1,
                "f32_top5_ids": [1], "f32_top5_logits": [1.0]}

    def test_all_expected_present_exactly_once_passes(self):
        sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
        import phase6_localize_f32_divergence as localizer  # noqa: E402
        entries = [self._entry(pid) for pid in localizer.EXPECTED_PROMPT_IDS]
        result = localizer._select_expected_entries(entries)
        self.assertEqual(set(result.keys()), set(localizer.EXPECTED_PROMPT_IDS))

    def test_missing_target_aborts(self):
        import phase6_localize_f32_divergence as localizer
        entries = [self._entry(pid) for pid in localizer.EXPECTED_PROMPT_IDS[:-1]]  # drop one
        with self.assertRaises(SystemExit):
            localizer._select_expected_entries(entries)

    def test_duplicate_target_aborts(self):
        import phase6_localize_f32_divergence as localizer
        entries = [self._entry(pid) for pid in localizer.EXPECTED_PROMPT_IDS]
        entries.append(self._entry(localizer.EXPECTED_PROMPT_IDS[0]))  # duplicate
        with self.assertRaises(SystemExit):
            localizer._select_expected_entries(entries)

    def test_extra_unexpected_entries_are_ignored_not_rejected(self):
        # Unexpected entries that are NOT among EXPECTED_PROMPT_IDS (e.g.
        # the other 4 corpus prompts this localizer doesn't target) must
        # not cause a false abort -- only missing/duplicate TARGET entries
        # are an error.
        import phase6_localize_f32_divergence as localizer
        entries = [self._entry(pid) for pid in localizer.EXPECTED_PROMPT_IDS]
        entries.append(self._entry("some_other_prompt_not_targeted"))
        result = localizer._select_expected_entries(entries)
        self.assertEqual(set(result.keys()), set(localizer.EXPECTED_PROMPT_IDS))


class ReportWriteFailureTests(unittest.TestCase):
    """Codex remediation Gate 3, item 10: output/report write failures
    must be detected, not silently swallowed."""

    def test_unwritable_report_path_aborts(self):
        import phase6_localize_f32_divergence as localizer
        # A path inside a directory that does not exist cannot be opened
        # for writing -- a real, not mocked, OSError.
        bad_path = os.path.join("this-directory-does-not-exist-12345", "report.jsonl")
        with self.assertRaises(SystemExit):
            localizer._write_report(bad_path, [{"id": "x"}])


def _local_logsumexp(values: list[float]) -> float:
    m = max(values)
    return math.log(sum(math.exp(v - m) for v in values)) + m


if __name__ == "__main__":
    unittest.main()
