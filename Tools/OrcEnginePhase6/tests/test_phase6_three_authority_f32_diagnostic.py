# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Codex remediation round 4, Gate 3: targeted regression coverage for
phase6_three_authority_f32_diagnostic.py's provenance verification and
exact-corpus loading -- the boundary this round made genuinely
fail-closed (it previously hashed model.safetensors against a
hardcoded constant only, never opened the conversion manifest, and
never hashed the actual F32 GGUF used for the comparison).

Run: python -m unittest Tools/OrcEnginePhase6/tests/test_phase6_three_authority_f32_diagnostic.py -v
"""
from __future__ import annotations

import hashlib
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "tools"))
import phase6_three_authority_f32_diagnostic as diag  # noqa: E402


def _write(path: str, content: bytes) -> None:
    with open(path, "wb") as f:
        f.write(content)


def _sha256(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


class ManifestValidationTests(unittest.TestCase):
    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()

    def tearDown(self):
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _manifest_path(self) -> str:
        return os.path.join(self.tmpdir, "manifest.json")

    def test_missing_manifest_aborts(self):
        with self.assertRaises(SystemExit):
            diag._load_and_validate_manifest(os.path.join(self.tmpdir, "does-not-exist.json"))

    def test_malformed_manifest_aborts(self):
        path = self._manifest_path()
        _write(path, b"{not valid json")
        with self.assertRaises(SystemExit):
            diag._load_and_validate_manifest(path)

    def test_missing_source_hash_field_aborts(self):
        path = self._manifest_path()
        _write(path, json.dumps({"output_gguf_sha256": "a" * 64}).encode())
        with self.assertRaises(SystemExit):
            diag._load_and_validate_manifest(path)

    def test_null_source_hash_field_aborts(self):
        path = self._manifest_path()
        _write(path, json.dumps({"source_safetensors_sha256": None, "output_gguf_sha256": "a" * 64}).encode())
        with self.assertRaises(SystemExit):
            diag._load_and_validate_manifest(path)

    def test_missing_output_hash_field_aborts(self):
        path = self._manifest_path()
        _write(path, json.dumps({"source_safetensors_sha256": "a" * 64}).encode())
        with self.assertRaises(SystemExit):
            diag._load_and_validate_manifest(path)

    def test_null_output_hash_field_aborts(self):
        path = self._manifest_path()
        _write(path, json.dumps({"source_safetensors_sha256": "a" * 64, "output_gguf_sha256": ""}).encode())
        with self.assertRaises(SystemExit):
            diag._load_and_validate_manifest(path)

    def test_well_formed_manifest_passes(self):
        path = self._manifest_path()
        _write(path, json.dumps({"source_safetensors_sha256": "a" * 64, "output_gguf_sha256": "b" * 64}).encode())
        manifest = diag._load_and_validate_manifest(path)
        self.assertEqual(manifest["source_safetensors_sha256"], "a" * 64)


class PyTorchAuthorityVerificationTests(unittest.TestCase):
    """Covers the full chain: manifest <-> actual files <-> independently
    pinned constants <-> evidence rows."""

    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()
        self.hf_dir = os.path.join(self.tmpdir, "hf_model")
        os.makedirs(self.hf_dir)
        self.safetensors_content = b"fake safetensors content"
        self.gguf_content = b"fake gguf content"
        _write(os.path.join(self.hf_dir, "model.safetensors"), self.safetensors_content)
        _write(os.path.join(self.hf_dir, "config.json"),
              json.dumps({"architectures": ["LlamaForCausalLM"], "model_type": "llama"}).encode())
        self.gguf_path = os.path.join(self.tmpdir, "f32.gguf")
        _write(self.gguf_path, self.gguf_content)
        self.safetensors_hash = _sha256(self.safetensors_content)
        self.gguf_hash = _sha256(self.gguf_content)
        self.manifest_path = os.path.join(self.tmpdir, "manifest.json")
        _write(self.manifest_path, json.dumps({
            "source_safetensors_sha256": self.safetensors_hash,
            "output_gguf_sha256": self.gguf_hash,
        }).encode())

    def tearDown(self):
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _patched(self):
        return mock.patch.multiple(
            diag,
            EXPECTED_SOURCE_SAFETENSORS_SHA256=self.safetensors_hash,
            EXPECTED_F32_GGUF_SHA256=self.gguf_hash,
        )

    def test_valid_complete_chain_passes(self):
        with self._patched():
            manifest = diag._verify_pytorch_authority(self.hf_dir, self.manifest_path, self.gguf_path, [])
        self.assertEqual(manifest["source_safetensors_sha256"], self.safetensors_hash)

    def test_source_mismatch_aborts(self):
        # Corrupt the actual safetensors file so its real hash no longer
        # matches either the manifest or the pinned constant.
        _write(os.path.join(self.hf_dir, "model.safetensors"), b"tampered content")
        with self._patched():
            with self.assertRaises(SystemExit):
                diag._verify_pytorch_authority(self.hf_dir, self.manifest_path, self.gguf_path, [])

    def test_manifest_vs_pinned_constant_divergence_aborts(self):
        # Manifest says one hash, independently pinned constant says
        # another -- even though the ACTUAL file matches the manifest,
        # divergence from the independent pin must still abort.
        with mock.patch.multiple(diag, EXPECTED_SOURCE_SAFETENSORS_SHA256="0" * 64,
                                 EXPECTED_F32_GGUF_SHA256=self.gguf_hash):
            with self.assertRaises(SystemExit):
                diag._verify_pytorch_authority(self.hf_dir, self.manifest_path, self.gguf_path, [])

    def test_actual_f32_gguf_mismatch_aborts(self):
        _write(self.gguf_path, b"a completely different gguf")
        with self._patched():
            with self.assertRaises(SystemExit):
                diag._verify_pytorch_authority(self.hf_dir, self.manifest_path, self.gguf_path, [])

    def test_evidence_row_hash_mismatch_aborts(self):
        with self._patched():
            with self.assertRaises(SystemExit):
                diag._verify_pytorch_authority(
                    self.hf_dir, self.manifest_path, self.gguf_path,
                    [{"id": "x", "f32_artifact_sha256": "0" * 64}])

    def test_evidence_row_matching_hash_passes(self):
        with self._patched():
            diag._verify_pytorch_authority(
                self.hf_dir, self.manifest_path, self.gguf_path,
                [{"id": "x", "f32_artifact_sha256": self.gguf_hash}])

    def test_missing_gguf_file_aborts(self):
        with self._patched():
            with self.assertRaises(SystemExit):
                diag._verify_pytorch_authority(self.hf_dir, self.manifest_path,
                                               os.path.join(self.tmpdir, "does-not-exist.gguf"), [])


class ExactCorpusLoaderTests(unittest.TestCase):
    """Covers: the three-authority loader must fail closed on duplicate,
    missing, or unexpected prompt IDs rather than letting a dict
    comprehension silently overwrite duplicates."""

    def setUp(self):
        self.tmpdir = tempfile.mkdtemp()

    def tearDown(self):
        import shutil
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _write_jsonl(self, rows: list[dict]) -> str:
        path = os.path.join(self.tmpdir, "rows.jsonl")
        with open(path, "w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps(r) + "\n")
        return path

    def _all_seven_rows(self) -> list[dict]:
        ids = list(diag.CONTROL_PROMPT_IDS) + list(diag.DIVERGENT_PROMPT_IDS)
        return [{"id": pid, "value": i} for i, pid in enumerate(ids)]

    def test_all_seven_present_exactly_once_passes(self):
        path = self._write_jsonl(self._all_seven_rows())
        result = diag._load_exact_corpus(path, "test")
        self.assertEqual(len(result), 7)

    def test_missing_prompt_aborts(self):
        rows = self._all_seven_rows()[:-1]
        path = self._write_jsonl(rows)
        with self.assertRaises(SystemExit):
            diag._load_exact_corpus(path, "test")

    def test_duplicate_prompt_aborts(self):
        rows = self._all_seven_rows()
        rows.append({"id": diag.CONTROL_PROMPT_IDS[0], "value": "duplicate"})
        path = self._write_jsonl(rows)
        with self.assertRaises(SystemExit):
            diag._load_exact_corpus(path, "test")

    def test_empty_id_aborts(self):
        rows = self._all_seven_rows()
        rows[0]["id"] = ""
        path = self._write_jsonl(rows)
        with self.assertRaises(SystemExit):
            diag._load_exact_corpus(path, "test")

    def test_unrelated_extra_entries_are_ignored_not_rejected(self):
        rows = self._all_seven_rows()
        rows.append({"id": "some_unrelated_prompt_not_in_this_diagnostic", "value": "extra"})
        path = self._write_jsonl(rows)
        result = diag._load_exact_corpus(path, "test")
        self.assertEqual(len(result), 7)
        self.assertNotIn("some_unrelated_prompt_not_in_this_diagnostic", result)

    def test_duplicate_of_a_LATER_entry_does_not_silently_overwrite(self):
        # The specific bug this replaces: a plain dict-comprehension
        # loader would let the SECOND occurrence silently win with no
        # error at all. This must abort instead.
        rows = self._all_seven_rows()
        first_id = rows[0]["id"]
        rows.insert(3, {"id": first_id, "value": "sneaky duplicate"})
        path = self._write_jsonl(rows)
        with self.assertRaises(SystemExit):
            diag._load_exact_corpus(path, "test")


if __name__ == "__main__":
    unittest.main()
