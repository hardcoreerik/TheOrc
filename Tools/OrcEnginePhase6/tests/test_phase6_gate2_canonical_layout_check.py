# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Targeted regression tests for phase6_gate2_canonical_layout_check.py's
own fail-closed trust-boundary behavior (combined Codex/Grok
remediation round 7, Gate 2). These do NOT require a real llama-server
or GPU/model -- they exercise identity verification, corpus validation,
and result-completeness checks in isolation, using real temp files and
the module's real functions (not mocked hash math), matching the
established pattern in test_phase6_llama_cpp_q8_0_oracle.py.

Run: python -m unittest Tools/OrcEnginePhase6/tests/test_phase6_gate2_canonical_layout_check.py -v
"""
from __future__ import annotations

import hashlib
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import phase6_gate2_canonical_layout_check as gate2  # noqa: E402


class TestVerifyCanonicalGgufIdentity(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.path = os.path.join(self.tmpdir, "canonical.gguf")

    def tearDown(self):
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_missing_file_aborts(self):
        with self.assertRaises(SystemExit) as ctx:
            gate2._verify_canonical_gguf_identity(
                os.path.join(self.tmpdir, "does-not-exist.gguf"), "deadbeef", "test")
        self.assertIn("not found", str(ctx.exception))

    def test_hash_mismatch_aborts(self):
        with open(self.path, "wb") as f:
            f.write(b"real content")
        with self.assertRaises(SystemExit) as ctx:
            gate2._verify_canonical_gguf_identity(self.path, "0" * 64, "test")
        self.assertIn("does not match the pinned authority", str(ctx.exception))

    def test_matching_hash_passes_and_returns_hash(self):
        content = b"real canonical gguf content"
        with open(self.path, "wb") as f:
            f.write(content)
        expected = hashlib.sha256(content).hexdigest()
        actual = gate2._verify_canonical_gguf_identity(self.path, expected, "test")
        self.assertEqual(actual, expected)


class TestValidatePromptIds(unittest.TestCase):
    def test_real_corpus_passes(self):
        gate2._validate_prompt_ids()  # must not raise against the module's own real PROMPTS

    def test_wrong_count_aborts(self):
        fake = {"only_one": {"text": "x", "token_ids": [1]}}
        with mock.patch.object(gate2, "PROMPTS", fake), \
             mock.patch.object(gate2, "EXPECTED_PROMPT_IDS", frozenset(fake)):
            with self.assertRaises(SystemExit) as ctx:
                gate2._validate_prompt_ids()
            self.assertIn("expected exactly 7", str(ctx.exception))

    def test_keys_mismatching_expected_set_aborts(self):
        fake = dict(gate2.PROMPTS)
        fake["unexpected_extra_id"] = fake.pop("dev_capital_of_france")
        with mock.patch.object(gate2, "PROMPTS", fake):
            with self.assertRaises(SystemExit) as ctx:
                gate2._validate_prompt_ids()
            self.assertIn("internal inconsistency", str(ctx.exception))

    def test_empty_text_aborts(self):
        fake = dict(gate2.PROMPTS)
        fake["dev_capital_of_france"] = {"text": "", "token_ids": [1, 2, 3]}
        with mock.patch.object(gate2, "PROMPTS", fake), \
             mock.patch.object(gate2, "EXPECTED_PROMPT_IDS", frozenset(fake)):
            with self.assertRaises(SystemExit) as ctx:
                gate2._validate_prompt_ids()
            self.assertIn("empty/missing 'text'", str(ctx.exception))

    def test_empty_token_ids_aborts(self):
        fake = dict(gate2.PROMPTS)
        fake["dev_capital_of_france"] = {"text": "The capital of France is", "token_ids": []}
        with mock.patch.object(gate2, "PROMPTS", fake), \
             mock.patch.object(gate2, "EXPECTED_PROMPT_IDS", frozenset(fake)):
            with self.assertRaises(SystemExit) as ctx:
                gate2._validate_prompt_ids()
            self.assertIn("empty/missing 'token_ids'", str(ctx.exception))


class TestRunLegFailsClosed(unittest.TestCase):
    """Exercises _run_leg's fail-closed checks by faking out the oracle
    server-launch/request helpers it calls (a real server is out of
    scope for a unit test) while keeping _run_leg's own control flow
    real -- proves THIS module's trust-boundary logic, not the oracle
    module's (already covered by test_phase6_llama_cpp_q8_0_oracle.py)."""

    def _patched(self, tokenize_fn=None, completion_fn=None):
        fake_proc = mock.Mock()
        fake_proc.wait = mock.Mock()
        patches = [
            mock.patch.object(gate2.oracle, "_free_local_port", return_value=12345),
            mock.patch.object(gate2.oracle, "_launch_controlled_server", return_value=fake_proc),
            mock.patch.object(gate2.oracle, "_wait_for_health", return_value=None),
        ]
        if tokenize_fn is not None:
            patches.append(mock.patch.object(gate2.oracle, "_request_tokenize", side_effect=tokenize_fn))
        if completion_fn is not None:
            patches.append(mock.patch.object(gate2.oracle, "_request_completion", side_effect=completion_fn))
        return patches

    def _apply(self, patches):
        for p in patches:
            p.start()
            self.addCleanup(p.stop)

    def test_token_id_mismatch_aborts_before_completion(self):
        called_completion = []

        def bad_tokenize(port, text):
            return [999999]  # never matches any real corpus prompt's token_ids

        def tracking_completion(port, text, n_probs):
            called_completion.append(text)
            return {}

        self._apply(self._patched(tokenize_fn=bad_tokenize, completion_fn=tracking_completion))
        with self.assertRaises(SystemExit) as ctx:
            gate2._run_leg("fake-server", "fake.gguf", "test-leg")
        self.assertIn("token-ID mismatch", str(ctx.exception))
        self.assertEqual(called_completion, [], "must not request completion after a token-ID mismatch")

    def test_missing_completion_probabilities_aborts(self):
        def good_tokenize(port, text):
            for p in gate2.PROMPTS.values():
                if p["text"] == text:
                    return p["token_ids"]
            raise AssertionError(f"unexpected prompt text {text!r}")

        def malformed_completion(port, text, n_probs):
            return {"no_completion_probabilities_here": True}

        self._apply(self._patched(tokenize_fn=good_tokenize, completion_fn=malformed_completion))
        with self.assertRaises(SystemExit) as ctx:
            gate2._run_leg("fake-server", "fake.gguf", "test-leg")
        self.assertIn("malformed /completion response", str(ctx.exception))

    def test_missing_top_logprobs_field_aborts(self):
        def good_tokenize(port, text):
            for p in gate2.PROMPTS.values():
                if p["text"] == text:
                    return p["token_ids"]
            raise AssertionError(f"unexpected prompt text {text!r}")

        def completion_without_top_logprobs(port, text, n_probs):
            return {"completion_probabilities": [{"no_top_logprobs_key": True}]}

        self._apply(self._patched(tokenize_fn=good_tokenize, completion_fn=completion_without_top_logprobs))
        with self.assertRaises(SystemExit) as ctx:
            gate2._run_leg("fake-server", "fake.gguf", "test-leg")
        self.assertIn("no top_logprobs", str(ctx.exception))

    def test_malformed_top_logprobs_entry_aborts(self):
        def good_tokenize(port, text):
            for p in gate2.PROMPTS.values():
                if p["text"] == text:
                    return p["token_ids"]
            raise AssertionError(f"unexpected prompt text {text!r}")

        def completion_with_bad_entry(port, text, n_probs):
            return {"completion_probabilities": [{"top_logprobs": [{"id": 1}]}]}  # missing "logprob"

        self._apply(self._patched(tokenize_fn=good_tokenize, completion_fn=completion_with_bad_entry))
        with self.assertRaises(SystemExit) as ctx:
            gate2._run_leg("fake-server", "fake.gguf", "test-leg")
        self.assertIn("malformed top_logprobs entry", str(ctx.exception))


if __name__ == "__main__":
    unittest.main()
