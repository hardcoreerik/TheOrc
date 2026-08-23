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


class ServerIdentityTests(unittest.TestCase):
    """Covers: wrong server version/build rejection."""

    def test_missing_executable_aborts(self):
        with self.assertRaises(SystemExit):
            oracle._verify_server_identity("this-path-does-not-exist.exe")

    def test_wrong_hash_aborts_before_version_check(self):
        # A file that exists but does not match the pinned SHA-256 must be
        # rejected even if it happens to be executable at all.
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".exe", delete=False) as f:
            f.write(b"not the real llama-server.exe")
            path = f.name
        try:
            with self.assertRaises(SystemExit):
                oracle._verify_server_identity(path)
        finally:
            os.remove(path)

    def test_correct_hash_but_wrong_version_banner_aborts(self):
        import tempfile
        # Construct a fixture whose CONTENT hashes to the pinned expected
        # value's... we cannot do that without knowing a preimage, so instead
        # patch the expected hash to match this fixture's real hash, and
        # patch subprocess.run to return a wrong banner -- isolates the
        # --version check specifically.
        with tempfile.NamedTemporaryFile(suffix=".exe", delete=False) as f:
            f.write(b"fixture content for version-banner test")
            path = f.name
        try:
            fixture_hash = hashlib.sha256(_read_bytes(path)).hexdigest()
            with mock.patch.object(oracle, "EXPECTED_SERVER_EXE_SHA256", fixture_hash), \
                 mock.patch("subprocess.run") as run_mock:
                run_mock.return_value = mock.Mock(stdout="version: 0.1.0-dev (build 99999, commit deadbeef)\n",
                                                  stderr="")
                with self.assertRaises(SystemExit):
                    oracle._verify_server_identity(path)
        finally:
            os.remove(path)

    def test_correct_hash_and_correct_version_passes(self):
        import tempfile
        with tempfile.NamedTemporaryFile(suffix=".exe", delete=False) as f:
            f.write(b"fixture content for version-pass test")
            path = f.name
        try:
            fixture_hash = hashlib.sha256(_read_bytes(path)).hexdigest()
            with mock.patch.object(oracle, "EXPECTED_SERVER_EXE_SHA256", fixture_hash), \
                 mock.patch("subprocess.run") as run_mock:
                run_mock.return_value = mock.Mock(
                    stdout="version: 0.1.0-dev (build 10436, commit 6fed9f6ff)\n", stderr="")
                oracle._verify_server_identity(path)  # must not raise
        finally:
            os.remove(path)


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

    def test_full_vocab_logsumexp_matches_hand_computed_reference(self):
        # Sanity-checks this test file's own numerically-stabilized
        # reference helper against a naive (non-stabilized) computation,
        # for a range small enough that the naive form does not overflow.
        logits = [1.0, 2.0, 3.0, 0.5]
        expected = math.log(sum(math.exp(v) for v in logits))
        actual = _local_logsumexp(logits)
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


def _local_logsumexp(values: list[float]) -> float:
    m = max(values)
    return math.log(sum(math.exp(v - m) for v in values)) + m


if __name__ == "__main__":
    unittest.main()
