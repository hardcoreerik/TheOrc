"""Unit tests for Tools/check_orcengine_docs_truth.py (one pass + one fail per check)."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from check_orcengine_docs_truth import run_checks

STATE = """schema_version: 1
as_of:
  date: "2026-09-18"
status:
  lifecycle: phase_1_frozen
  phase1_spec: PHASE1.md
  phase1_freeze_tag: orcengine-phase1-freeze
"""
LOG = "# Log\n\n## OE-ADR-001 — first\n\n## OE-ADR-002 — second\n"
TRUTH = "# Project Truth\n\n> Snapshot date: 2026-09-18 America/Los_Angeles\n"


def git(root, *args, date=None):
    env = dict(os.environ, GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@t", GIT_COMMITTER_NAME="t",
               GIT_COMMITTER_EMAIL="t@t")
    if date:
        env["GIT_COMMITTER_DATE"] = env["GIT_AUTHOR_DATE"] = date
    subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True, env=env)


class DocsTruthTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.write("docs/OrcEngine/CURRENT_STATE.yaml", STATE)
        self.write("docs/OrcEngine/DECISION_LOG.md", LOG)
        self.write("docs/OrcEngine/PROJECT_TRUTH.md", TRUTH)
        self.write("docs/OrcEngine/PHASE1.md", "spec\n")
        self.write("docs/OrcEngine/README.md", "[Truth](PROJECT_TRUTH.md) see OE-ADR-002\n")
        self.write("docs/OrcEngine/ENGINEERING_ROADMAP.md", "# Roadmap\n")

    def tearDown(self):
        self.temp.cleanup()

    def write(self, relative, text):
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def report(self):
        return run_checks(self.root)

    def codes(self, level="errors"):
        return {item.code for item in getattr(self.report(), level)}

    def make_repo(self, code_date):
        git(self.root, "init", "-q")
        self.write("Tools/OrcEnginePhase1/a.cpp", "x\n")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-q", "-m", "c", date=code_date)
        git(self.root, "tag", "-a", "orcengine-phase1-freeze", "-m", "t")

    def test_consistent_docs_pass(self):
        self.assertEqual([], self.report().errors)

    def test_missing_state_file_fails(self):
        (self.root / "docs/OrcEngine/CURRENT_STATE.yaml").unlink()
        self.assertIn("state-file", self.codes())

    def test_missing_as_of_date_fails(self):
        self.write("docs/OrcEngine/CURRENT_STATE.yaml", "status:\n  lifecycle: x\n")
        self.assertIn("state-file", self.codes())

    def test_missing_spec_reference_fails(self):
        (self.root / "docs/OrcEngine/PHASE1.md").unlink()
        self.assertIn("spec-reference", self.codes())

    def test_dangling_adr_reference_fails(self):
        self.write("docs/OrcEngine/README.md", "see OE-ADR-099\n")
        self.assertIn("adr", self.codes())

    def test_duplicate_adr_fails(self):
        self.write("docs/OrcEngine/DECISION_LOG.md", LOG + "\n## OE-ADR-002 — again\n")
        self.assertIn("adr", self.codes())

    def test_snapshot_date_drift_fails(self):
        self.write("docs/OrcEngine/PROJECT_TRUTH.md", "> Snapshot date: 2026-08-01 x\n")
        self.assertIn("snapshot-sync", self.codes())

    def test_broken_link_fails(self):
        self.write("docs/OrcEngine/README.md", "[x](MISSING.md)\n")
        self.assertIn("local-link", self.codes())

    def test_missing_file_reference_fails(self):
        self.write("docs/OrcEngine/README.md", "see `Tools/OrcEnginePhase9/none.cpp`\n")
        self.assertIn("file-reference", self.codes())

    def test_existing_file_reference_passes(self):
        self.write("Tools/OrcEnginePhase1/a.cpp", "x\n")
        self.write("docs/OrcEngine/README.md", "see `Tools/OrcEnginePhase1/a.cpp`\n")
        self.assertNotIn("file-reference", self.codes())

    def test_resolved_claim_in_current_doc_fails(self):
        self.write("docs/OrcEngine/README.md", "There is zero C++ engine code.\n")
        self.assertIn("resolved-claim", self.codes())

    def test_resolved_claim_in_append_only_log_is_allowed(self):
        self.write("docs/OrcEngine/DECISION_LOG.md", LOG + "\nhistorical: zero C++ engine code then.\n")
        self.assertNotIn("resolved-claim", self.codes())

    def test_prompt_residue_warns_without_failing(self):
        self.write("docs/OrcEngine/README.md", "Your job is to implement the following.\n")
        report = self.report()
        self.assertEqual([], report.errors)
        self.assertIn("prompt-residue", {i.code for i in report.warnings})

    def test_existing_freeze_tag_passes(self):
        self.make_repo("2026-09-17T00:00:00")
        report = self.report()
        self.assertNotIn("freeze-tag", {i.code for i in report.errors})
        self.assertTrue(any("freeze tags exist" in p for p in report.passes))

    def test_missing_freeze_tag_fails_when_other_tags_exist(self):
        self.make_repo("2026-09-17T00:00:00")
        git(self.root, "tag", "-d", "orcengine-phase1-freeze")
        git(self.root, "tag", "-a", "other", "-m", "t")
        self.assertIn("freeze-tag", self.codes())

    def test_freeze_commit_mismatch_fails(self):
        self.make_repo("2026-09-17T00:00:00")
        self.write("docs/OrcEngine/CURRENT_STATE.yaml", STATE + "  phase1_freeze_commit: " + "0" * 40 + "\n")
        self.assertIn("freeze-tag", self.codes())

    def test_code_far_ahead_of_state_fails(self):
        self.make_repo("2026-12-01T00:00:00")
        self.assertIn("code-lag", self.codes())

    def test_code_moderately_ahead_of_state_warns(self):
        self.make_repo("2026-10-10T00:00:00")
        report = self.report()
        self.assertNotIn("code-lag", {i.code for i in report.errors})
        self.assertIn("code-lag", {i.code for i in report.warnings})


if __name__ == "__main__":
    unittest.main()
