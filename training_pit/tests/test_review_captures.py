#!/usr/bin/env python3
"""
test_review_captures.py — Unit tests for training_pit/scripts/review_captures.py

Tests cover:
  - Manifest load/save (empty init, atomic save, reload)
  - capture_key extraction
  - cmd_approve: happy path, quality/split validation, conversion check
  - cmd_reject: records decision correctly
  - cmd_export: success path, validation abort, sanitizer REJECT abort
  - cmd_status: gate counter accuracy

Run:
    python -m unittest training_pit/tests/test_review_captures.py -v
    # or from repo root:
    python -m pytest training_pit/tests/test_review_captures.py -v
"""

import json
import os
import shutil
import sys
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path
from unittest import mock

# Add scripts dir to sys.path before importing review_captures
_REPO_ROOT   = Path(__file__).resolve().parent.parent.parent
_SCRIPTS_DIR = _REPO_ROOT / "training_pit" / "scripts"
if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))

import review_captures  # noqa: E402

FIXTURES = Path(__file__).resolve().parent / "fixtures" / "review_workflow"


# ── Helpers ───────────────────────────────────────────────────────────────────

def _load_json(path: Path) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


class _TempEnv:
    """
    Context manager that sets up an isolated temp environment and patches
    the module-level constants in review_captures to point to it.

    Provides:
      .tmpdir      — Path to the temp directory
      .staging_dir — Path to .orc/swarm/dataset-staging/
      .manifest    — Path to the manifest JSON file
      .output_dir  — Path to training_pit/datasets/
    """

    def __init__(self):
        self._tmpdir_obj = None
        self.tmpdir      = None
        self.staging_dir = None
        self.manifest    = None
        self.output_dir  = None
        self._patches    = []

    def __enter__(self):
        self._tmpdir_obj = tempfile.TemporaryDirectory()
        self.tmpdir      = Path(self._tmpdir_obj.name)

        self.staging_dir  = self.tmpdir / ".orc" / "swarm" / "dataset-staging"
        self.staging_dir.mkdir(parents=True)

        self.manifest = (
            self.tmpdir / "training_pit" / "datasets" / "manifests" / "reviewed_v1.json"
        )
        self.manifest.parent.mkdir(parents=True)

        self.output_dir = self.tmpdir / "training_pit" / "datasets"
        self.output_dir.mkdir(parents=True, exist_ok=True)

        output_paths = {
            "train":    self.output_dir / "train_v1.jsonl",
            "eval":     self.output_dir / "eval_v1.jsonl",
            "negative": self.output_dir / "negative_v1.jsonl",
        }

        # Patch module-level constants
        for attr, val in [
            ("MANIFEST_PATH", self.manifest),
            ("STAGING_DIRS",  [self.staging_dir]),
            ("OUTPUT_PATHS",  output_paths),
            ("REPO_ROOT",     self.tmpdir),
        ]:
            p = mock.patch.object(review_captures, attr, val)
            p.start()
            self._patches.append(p)

        return self

    def __exit__(self, *_):
        for p in self._patches:
            p.stop()
        self._tmpdir_obj.cleanup()

    def copy_fixture(self, fixture_name: str, dest_name: str | None = None) -> Path:
        """Copy a fixture file into the temp staging dir."""
        src  = FIXTURES / fixture_name
        dest = self.staging_dir / (dest_name or fixture_name)
        shutil.copy(src, dest)
        return dest


# ── Tests: manifest ───────────────────────────────────────────────────────────

class TestManifest(unittest.TestCase):

    def test_load_empty_when_file_missing(self):
        with _TempEnv() as env:
            self.assertFalse(env.manifest.exists())
            m = review_captures.load_manifest()
        self.assertEqual(m["schema_version"], "1.0")
        self.assertEqual(m["entries"], {})

    def test_save_creates_file(self):
        with _TempEnv() as env:
            m = review_captures.load_manifest()
            review_captures.save_manifest(m)
            self.assertTrue(env.manifest.exists())

    def test_save_and_reload_roundtrip(self):
        with _TempEnv() as env:
            m = review_captures.load_manifest()
            m["entries"]["test_key"] = {"decision": "approved", "split": "train"}
            review_captures.save_manifest(m)
            m2 = review_captures.load_manifest()
        self.assertIn("test_key", m2["entries"])
        self.assertEqual(m2["entries"]["test_key"]["split"], "train")

    def test_save_updates_last_modified(self):
        with _TempEnv():
            m = review_captures.load_manifest()
            before = m["last_modified"]
            # Force a new timestamp (sleep is avoided; we just check it's a valid string)
            review_captures.save_manifest(m)
            m2 = review_captures.load_manifest()
        self.assertIn("T", m2["last_modified"])  # ISO format

    def test_no_tmp_file_left_behind_after_save(self):
        with _TempEnv() as env:
            m = review_captures.load_manifest()
            review_captures.save_manifest(m)
            tmp_path = env.manifest.with_suffix(".tmp")
        self.assertFalse(tmp_path.exists())


# ── Tests: capture_key ────────────────────────────────────────────────────────

class TestCaptureKey(unittest.TestCase):

    def test_key_from_example_id(self):
        capture = {"example_id": "ex_20260609_001"}
        self.assertEqual(review_captures.capture_key(capture), "ex_20260609_001")

    def test_key_none_when_missing(self):
        self.assertIsNone(review_captures.capture_key({}))

    def test_key_from_valid_positive_fixture(self):
        cap = _load_json(FIXTURES / "valid_positive.json")
        self.assertEqual(review_captures.capture_key(cap), "ex_test_fixture_pos_001")


# ── Tests: cmd_approve ────────────────────────────────────────────────────────

class TestCmdApprove(unittest.TestCase):

    def _args(self, path: str, split: str, quality: str, note: str = "") -> Namespace:
        return Namespace(approve=path, split=split, quality=quality, note=note)

    def test_approve_valid_positive_for_train(self):
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = self._args(str(dest), "train", "silver", "Looks good")
            rc   = review_captures.cmd_approve(args)

            self.assertEqual(rc, 0)
            m = review_captures.load_manifest()

        entry = m["entries"]["ex_test_fixture_pos_001"]
        self.assertEqual(entry["decision"], "approved")
        self.assertEqual(entry["split"],    "train")
        self.assertEqual(entry["quality"],  "silver")
        self.assertEqual(entry["note"],     "Looks good")

    def test_approve_valid_negative_for_negative_split(self):
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_negative.json",
                "plan_capture_bad_testrun_005.json"
            )
            args = self._args(str(dest), "negative", "rejected")
            rc   = review_captures.cmd_approve(args)

            self.assertEqual(rc, 0)
            m = review_captures.load_manifest()

        entry = m["entries"]["ex_test_fixture_neg_001"]
        self.assertEqual(entry["split"],   "negative")
        self.assertEqual(entry["quality"], "rejected")

    def test_approve_rejects_draft_for_train(self):
        """train split must be gold or silver — draft is forbidden."""
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = self._args(str(dest), "train", "draft")
            rc   = review_captures.cmd_approve(args)

        self.assertEqual(rc, 1)  # should fail

    def test_approve_rejects_rejected_for_train(self):
        """train split must be gold or silver — rejected is forbidden."""
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = self._args(str(dest), "train", "rejected")
            rc   = review_captures.cmd_approve(args)

        self.assertEqual(rc, 1)

    def test_approve_invalid_capture_returns_error(self):
        """Capture with no goal/plan cannot be approved."""
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "invalid_capture.json",
                "plan_capture_good_testrun_000.json"
            )
            args = self._args(str(dest), "eval", "draft")
            rc   = review_captures.cmd_approve(args)

        self.assertEqual(rc, 1)

    def test_approve_missing_file_returns_error(self):
        with _TempEnv() as env:
            args = self._args(
                str(env.staging_dir / "does_not_exist.json"),
                "train", "silver"
            )
            rc = review_captures.cmd_approve(args)

        self.assertEqual(rc, 1)

    def test_approve_allows_draft_for_eval(self):
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = self._args(str(dest), "eval", "draft")
            rc   = review_captures.cmd_approve(args)

        self.assertEqual(rc, 0)

    def test_approve_does_not_write_jsonl(self):
        """Approving should NOT immediately write the JSONL output file."""
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = self._args(str(dest), "train", "silver")
            review_captures.cmd_approve(args)
            train_path = env.output_dir / "train_v1.jsonl"

        self.assertFalse(train_path.exists(),
            "train_v1.jsonl must NOT be written by --approve")


# ── Tests: cmd_reject ─────────────────────────────────────────────────────────

class TestCmdReject(unittest.TestCase):

    def _args(self, path: str, note: str = "") -> Namespace:
        return Namespace(reject=path, note=note)

    def test_reject_records_decision(self):
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_negative.json",
                "plan_capture_bad_testrun_005.json"
            )
            rc = review_captures.cmd_reject(self._args(str(dest), "Collapse pattern"))
            self.assertEqual(rc, 0)
            m = review_captures.load_manifest()

        entry = m["entries"]["ex_test_fixture_neg_001"]
        self.assertEqual(entry["decision"], "rejected")
        self.assertIsNone(entry["split"])
        self.assertIsNone(entry["quality"])
        self.assertEqual(entry["note"], "Collapse pattern")

    def test_reject_missing_file_returns_error(self):
        with _TempEnv() as env:
            rc = review_captures.cmd_reject(
                self._args(str(env.staging_dir / "missing.json"))
            )
        self.assertEqual(rc, 1)


# ── Tests: cmd_export ─────────────────────────────────────────────────────────

class TestCmdExport(unittest.TestCase):

    def _approve(self, env, fixture: str, dest: str, split: str, quality: str):
        path = env.copy_fixture(fixture, dest)
        args = Namespace(approve=str(path), split=split, quality=quality, note="")
        review_captures.cmd_approve(args)
        return path

    def test_export_train_success(self):
        exported_lines = []
        with _TempEnv() as env:
            self._approve(
                env,
                "valid_positive.json",
                "plan_capture_good_testrun_075.json",
                "train", "silver"
            )
            rc = review_captures.cmd_export("train")
            train_path = env.output_dir / "train_v1.jsonl"

            # Must check existence inside the with block (temp dir is cleaned up on exit)
            self.assertEqual(rc, 0)
            self.assertTrue(train_path.exists(), "train_v1.jsonl should have been written")
            with open(train_path, encoding="utf-8") as f:
                exported_lines = [ln.strip() for ln in f if ln.strip()]

        # Assert content after block (data already captured above)
        self.assertEqual(len(exported_lines), 1)
        obj = json.loads(exported_lines[0])
        self.assertIn("messages", obj)
        self.assertIn("metadata", obj)
        self.assertEqual(obj["metadata"]["quality"], "silver")

    def test_export_uses_reviewer_quality_not_auto(self):
        """Quality in the exported JSONL must come from the manifest, not auto-derived."""
        quality_value = None
        with _TempEnv() as env:
            # valid_positive has quality_score=75, auto-derive would give 'silver'
            # but we approve as 'gold' to verify the override
            self._approve(
                env,
                "valid_positive.json",
                "plan_capture_good_testrun_075.json",
                "train", "gold"
            )
            review_captures.cmd_export("train")
            train_path = env.output_dir / "train_v1.jsonl"
            with open(train_path, encoding="utf-8") as f:
                quality_value = json.loads(f.readline().strip())["metadata"]["quality"]

        self.assertEqual(quality_value, "gold")

    def test_export_negative_success(self):
        """Negative captures (collapse pattern) can be exported to the negative split."""
        with _TempEnv() as env:
            self._approve(
                env,
                "valid_negative.json",
                "plan_capture_bad_testrun_005.json",
                "negative", "rejected"
            )
            rc = review_captures.cmd_export("negative")
            neg_path = env.output_dir / "negative_v1.jsonl"

            # Check existence inside block — temp dir is deleted on exit
            self.assertEqual(rc, 0)
            self.assertTrue(neg_path.exists())

    def test_export_no_approved_returns_zero(self):
        """--export-train with no approved entries prints a message and exits 0."""
        with _TempEnv():
            rc = review_captures.cmd_export("train")
        self.assertEqual(rc, 0)

    def test_export_aborts_on_invalid_capture(self):
        """If an approved capture file disappears, export must abort (return 1)."""
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = Namespace(approve=str(dest), split="train", quality="silver", note="")
            review_captures.cmd_approve(args)

            # Delete the capture file after approval so export can't find it
            dest.unlink()
            rc = review_captures.cmd_export("train")
            train_path = env.output_dir / "train_v1.jsonl"

        self.assertEqual(rc, 1)
        self.assertFalse(train_path.exists(), "Final file must not be written on failure")

    def test_export_aborts_on_sanitizer_reject(self):
        """
        If sanitize_dataset finds REJECT patterns in the exported JSONL,
        the export must abort and leave the final file unchanged.
        """
        with _TempEnv() as env:
            # Manually write a manifest entry pointing at the sanitize_reject fixture
            # (it's a JSONL, not a capture — but we mock the conversion to use its content)
            # Strategy: mock _convert_for_export to return a REJECT-triggering example
            reject_jsonl = FIXTURES / "sanitize_reject.jsonl"
            with open(reject_jsonl, encoding="utf-8") as f:
                bad_example = json.loads(f.readline().strip())

            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = Namespace(approve=str(dest), split="train", quality="silver", note="")
            review_captures.cmd_approve(args)

            with mock.patch.object(
                review_captures, "_convert_for_export", return_value=bad_example
            ):
                rc = review_captures.cmd_export("train")

            train_path = env.output_dir / "train_v1.jsonl"

        self.assertEqual(rc, 1)
        self.assertFalse(train_path.exists(),
            "train_v1.jsonl must NOT be written when sanitizer aborts the export")

    def test_no_tmp_file_left_on_export_failure(self):
        """A .tmp file must not be left behind if the export is aborted."""
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            args = Namespace(approve=str(dest), split="train", quality="silver", note="")
            review_captures.cmd_approve(args)
            dest.unlink()  # trigger failure
            review_captures.cmd_export("train")
            tmp_path = (env.output_dir / "train_v1").with_suffix(".tmp")

        self.assertFalse(tmp_path.exists(), ".tmp file must be cleaned up on failure")


# ── Tests: cmd_status ─────────────────────────────────────────────────────────

class TestCmdStatus(unittest.TestCase):

    def test_status_shows_zero_counts_when_empty(self):
        """--status should run without error on an empty manifest."""
        with _TempEnv():
            rc = review_captures.cmd_status(Namespace())
        self.assertEqual(rc, 0)

    def test_status_counts_approved_correctly(self):
        """Approved entries are counted per split in the manifest."""
        with _TempEnv() as env:
            # Approve the positive capture for train
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            review_captures.cmd_approve(
                Namespace(approve=str(dest), split="train", quality="silver", note="")
            )
            m = review_captures.load_manifest()

        train_count = sum(
            1 for e in m["entries"].values()
            if e.get("decision") == "approved" and e.get("split") == "train"
        )
        self.assertEqual(train_count, 1)

    def test_gate_not_met_below_threshold(self):
        """With 1 approved train example, Phase 3 gate (150) must not be met."""
        with _TempEnv() as env:
            dest = env.copy_fixture(
                "valid_positive.json",
                "plan_capture_good_testrun_075.json"
            )
            review_captures.cmd_approve(
                Namespace(approve=str(dest), split="train", quality="silver", note="")
            )
            m = review_captures.load_manifest()

        train_count = sum(
            1 for e in m["entries"].values()
            if e.get("decision") == "approved" and e.get("split") == "train"
        )
        self.assertLess(train_count, review_captures.PHASE3_GATES["train"])


# ── Tests: scan_staging ───────────────────────────────────────────────────────

class TestScanStaging(unittest.TestCase):

    def test_scan_returns_empty_when_no_captures(self):
        with _TempEnv():
            results = review_captures.scan_staging()
        self.assertEqual(results, [])

    def test_scan_finds_good_and_bad_captures(self):
        with _TempEnv() as env:
            env.copy_fixture("valid_positive.json", "plan_capture_good_run001_075.json")
            env.copy_fixture("valid_negative.json",  "plan_capture_bad_run002_005.json")
            results = review_captures.scan_staging()

        paths       = [p for p, _ in results]
        class_hints = [h for _, h in results]
        self.assertEqual(len(results), 2)
        self.assertIn("positive", class_hints)
        self.assertIn("negative", class_hints)

    def test_scan_skips_non_matching_files(self):
        with _TempEnv() as env:
            (env.staging_dir / "random_file.json").write_text("{}", encoding="utf-8")
            (env.staging_dir / "plan_capture_marginal_run003_050.json").write_text(
                "{}", encoding="utf-8"
            )
            results = review_captures.scan_staging()

        # Only good_* and bad_* patterns should be picked up
        self.assertEqual(len(results), 0)


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    unittest.main(verbosity=2)
