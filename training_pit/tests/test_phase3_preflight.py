#!/usr/bin/env python3
"""
test_phase3_preflight.py — Unit tests for phase3_preflight.py

Tests use isolated temp directories and explicit path parameters to
avoid any interaction with the live repo manifest or datasets.

Run:
    python training_pit/tests/test_phase3_preflight.py
"""

import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

# ── Path setup ────────────────────────────────────────────────────────────────
# Add training_pit/scripts to sys.path so we can import the module under test.
_TESTS_DIR = Path(__file__).resolve().parent
_REPO_ROOT = _TESTS_DIR.parent.parent
_SCRIPTS_DIR = _REPO_ROOT / "training_pit" / "scripts"

if str(_SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPTS_DIR))

from phase3_preflight import (   # noqa: E402
    run_preflight,
    format_human,
    format_json_output,
    CheckResult,
    PreflightResult,
)


# ── Test helpers ──────────────────────────────────────────────────────────────

def _make_manifest(entries: dict | None = None) -> dict:
    """Return a minimal valid manifest dict."""
    return {
        "schema_version": "1.0",
        "created_at": "2026-01-01T00:00:00Z",
        "last_modified": "2026-01-01T00:00:00Z",
        "entries": entries or {},
    }


def _make_entry(split: str, quality: str = "silver", decision: str = "approved") -> dict:
    """Return a minimal manifest entry."""
    return {
        "decision": decision,
        "split": split,
        "quality": quality,
        "reviewed_at": "2026-01-01T00:00:00Z",
        "reviewed_by": "test",
    }


def _make_valid_line(goal_suffix: str = "test", quality: str = "silver") -> str:
    """
    Return a valid chat-JSONL line that passes both validate_dataset.py
    and sanitize_dataset.py.
    """
    obj = {
        "messages": [
            {
                "role": "system",
                "content": "You are a planning assistant that decomposes goals into tasks.",
            },
            {
                "role": "user",
                "content": f"Goal: Write a utility module for {goal_suffix}",
            },
            {
                "role": "assistant",
                "content": json.dumps({
                    "plan": f"Implement {goal_suffix} utility",
                    "tasks": [
                        {
                            "role": "CODER",
                            "title": f"Write {goal_suffix}_util.py",
                            "description": (
                                f"Implement the {goal_suffix} utility module with proper "
                                "error handling, type hints, and a brief docstring. "
                                "Output to the project src/ directory."
                            ),
                            "output_file": f"{goal_suffix}_util.py",
                        }
                    ],
                }),
            },
        ],
        "metadata": {
            "category": "boss_planning",
            "source": "manual",
            "quality": quality,
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "test",
        },
    }
    return json.dumps(obj, separators=(",", ":"))


def _make_sanitize_reject_line() -> str:
    """Return a JSONL line that will trigger a REJECT in sanitize_dataset.py."""
    obj = {
        "messages": [
            {
                "role": "system",
                "content": "You are a planning assistant.",
            },
            {
                "role": "user",
                "content": "Goal: Configure the database",
            },
            {
                "role": "assistant",
                # password = 'value' pattern triggers REJECT in sanitize_dataset.py
                "content": "# DO NOT COMMIT\ndb_password = 'fake_test_secret_value'\nhost = 'localhost'",
            },
        ],
        "metadata": {
            "category": "boss_planning",
            "source": "manual",
            "quality": "silver",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "test",
            "notes": "TEST FIXTURE ONLY — intentionally contains a REJECT pattern for sanitizer tests",
        },
    }
    return json.dumps(obj, separators=(",", ":"))


def _make_invalid_jsonl_line() -> str:
    """Return a JSONL line that fails validate_dataset.py (missing required fields)."""
    return '{"messages": [], "metadata": {}}'


class _TempDatasetEnv:
    """
    Context manager that creates a fully isolated temp directory for testing.

    Layout:
        tmpdir/
            manifests/
                reviewed_v1.json
            train_v1.jsonl
            eval_v1.jsonl
            negative_v1.jsonl
            evals/
                (optional eval prompt files)
            staging/
                (optional staged capture files)
    """

    def __init__(self):
        self.tmpdir: Path | None = None
        self.manifest_path: Path | None = None
        self.datasets_dir: Path | None = None
        self.evals_dir: Path | None = None
        self.staging_dir: Path | None = None

    def __enter__(self) -> "_TempDatasetEnv":
        self.tmpdir = Path(tempfile.mkdtemp(prefix="test_preflight_"))
        manifests_dir = self.tmpdir / "manifests"
        manifests_dir.mkdir()
        self.manifest_path = manifests_dir / "reviewed_v1.json"
        self.datasets_dir = self.tmpdir / "datasets"
        self.datasets_dir.mkdir()
        self.evals_dir = self.tmpdir / "evals"
        self.evals_dir.mkdir()
        self.staging_dir = self.tmpdir / "staging"
        self.staging_dir.mkdir()
        return self

    def __exit__(self, *_):
        if self.tmpdir and self.tmpdir.exists():
            shutil.rmtree(self.tmpdir)

    def write_manifest(self, manifest: dict) -> None:
        self.manifest_path.write_text(
            json.dumps(manifest, indent=2), encoding="utf-8"
        )

    def write_jsonl(self, split: str, lines: list[str]) -> Path:
        filename = {"train": "train_v1.jsonl", "eval": "eval_v1.jsonl", "negative": "negative_v1.jsonl"}[split]
        path = self.datasets_dir / filename
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path

    def write_eval_prompts(self, prompts: list[str]) -> Path:
        """Write an eval prompts JSONL file (format: {"prompt": "..."})."""
        path = self.evals_dir / "eval_prompts.jsonl"
        lines = [json.dumps({"id": f"ev{i:03d}", "prompt": p}) for i, p in enumerate(prompts)]
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path

    def run(self, **kwargs) -> PreflightResult:
        """Run preflight with this env's paths."""
        return run_preflight(
            manifest_path=self.manifest_path,
            datasets_dir=self.datasets_dir,
            evals_dir=self.evals_dir,
            staging_dirs=[self.staging_dir],
            **kwargs,
        )


# ── Test classes ──────────────────────────────────────────────────────────────

class TestPreflightMissingManifest(unittest.TestCase):
    """Check 1: manifest file does not exist."""

    def test_missing_manifest_is_blocked(self):
        with _TempDatasetEnv() as env:
            # Don't write a manifest
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        self.assertFalse(result.ready)
        manifest_check = next(c for c in result.checks if c.name == "manifest")
        self.assertEqual(manifest_check.status, "BLOCKED")

    def test_missing_manifest_skips_remaining_checks(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        skipped = [c for c in result.checks if c.status == "SKIP"]
        self.assertGreater(len(skipped), 0, "Expected checks to be skipped when manifest missing")


class TestPreflightEmptyManifest(unittest.TestCase):
    """Check 2: manifest exists but has no approved entries."""

    def test_empty_manifest_is_blocked(self):
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest())
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        self.assertFalse(result.ready)
        counts_check = next(c for c in result.checks if c.name == "counts")
        self.assertEqual(counts_check.status, "BLOCKED")

    def test_empty_manifest_counts_are_zero(self):
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest())
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        self.assertEqual(result.counts["train"]["approved"], 0)
        self.assertEqual(result.counts["eval"]["approved"], 0)
        self.assertEqual(result.counts["negative"]["approved"], 0)


class TestPreflightInsufficientCounts(unittest.TestCase):
    """Check 2: counts in manifest do not meet thresholds."""

    def test_insufficient_train_is_blocked(self):
        entries = {
            "ex_001": _make_entry("train"),   # 1 train
            "ex_002": _make_entry("eval"),    # 1 eval
            "ex_003": _make_entry("negative"), # 1 negative
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            # Require 2 train — should block
            result = env.run(min_train=2, min_eval=1, min_negative=1)

        self.assertFalse(result.ready)
        counts_check = next(c for c in result.checks if c.name == "counts")
        self.assertEqual(counts_check.status, "BLOCKED")

    def test_sufficient_counts_passes(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        counts_check = next(c for c in result.checks if c.name == "counts")
        self.assertEqual(counts_check.status, "PASS")

    def test_rejected_entries_do_not_count(self):
        entries = {
            "ex_001": _make_entry("train", decision="rejected"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        self.assertEqual(result.counts["train"]["approved"], 0)


class TestPreflightFiles(unittest.TestCase):
    """Check 3: JSONL export files exist."""

    def test_missing_files_are_blocked(self):
        entries = {"ex_001": _make_entry("train")}
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        files_check = next(c for c in result.checks if c.name == "files")
        self.assertEqual(files_check.status, "BLOCKED")

    def test_all_files_present_passes_check(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train1")])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1", quality="rejected")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        files_check = next(c for c in result.checks if c.name == "files")
        self.assertEqual(files_check.status, "PASS")


class TestPreflightExportConsistency(unittest.TestCase):
    """Check 4: manifest approved counts must match JSONL line counts."""

    def test_mismatch_is_blocked(self):
        # Manifest says 2 train approved, but JSONL has only 1 line
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("train"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train1")])   # 1 line, not 2
            env.write_jsonl("eval",     [])
            env.write_jsonl("negative", [])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        consistency_check = next(c for c in result.checks if c.name == "export_consistency")
        self.assertEqual(consistency_check.status, "BLOCKED")

    def test_matching_counts_passes(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train1")])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1", quality="rejected")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        consistency_check = next(c for c in result.checks if c.name == "export_consistency")
        self.assertEqual(consistency_check.status, "PASS")


class TestPreflightValidation(unittest.TestCase):
    """Check 5: validate_dataset.py must pass on all JSONL files."""

    def test_invalid_jsonl_is_blocked(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            # train_v1.jsonl has 1 valid line BUT it's structurally invalid
            env.write_jsonl("train",    [_make_invalid_jsonl_line()])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        val_check = next(c for c in result.checks if c.name == "validation")
        self.assertEqual(val_check.status, "BLOCKED")

    def test_valid_jsonl_passes(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train1")])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1", quality="rejected")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        val_check = next(c for c in result.checks if c.name == "validation")
        self.assertEqual(val_check.status, "PASS")


class TestPreflightSanitization(unittest.TestCase):
    """Check 6: sanitize_dataset.py must pass (no REJECT patterns)."""

    def test_secret_in_train_is_blocked(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            # train contains a hardcoded password — REJECT
            env.write_jsonl("train",    [_make_sanitize_reject_line()])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        san_check = next(c for c in result.checks if c.name == "sanitization")
        self.assertEqual(san_check.status, "BLOCKED")

    def test_clean_files_pass_sanitization(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train1")])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1", quality="rejected")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        san_check = next(c for c in result.checks if c.name == "sanitization")
        self.assertEqual(san_check.status, "PASS")


class TestPreflightDuplicates(unittest.TestCase):
    """Check 7: no duplicate examples within or across splits."""

    def test_duplicate_in_train_is_blocked(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("train"),  # 2 approved train
            "ex_003": _make_entry("eval"),
            "ex_004": _make_entry("negative"),
        }
        duplicate_line = _make_valid_line("train1")
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            # Both lines are identical → duplicate
            env.write_jsonl("train",    [duplicate_line, duplicate_line])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        dup_check = next(c for c in result.checks if c.name == "duplicates")
        self.assertEqual(dup_check.status, "BLOCKED")

    def test_train_eval_overlap_is_blocked(self):
        """Same user goal in train and eval is a cross-split contamination."""
        shared_goal = "Goal: Write a utility module for shared_goal_test"
        train_line = json.dumps({
            "messages": [
                {"role": "system", "content": "You are a planning assistant."},
                {"role": "user", "content": shared_goal},
                {"role": "assistant", "content": '{"plan":"p","tasks":[{"role":"CODER","title":"Write f.py","description":"Write the module with error handling and type hints for clarity.","output_file":"f.py"}]}'},
            ],
            "metadata": {
                "category": "boss_planning", "source": "manual", "quality": "silver",
                "contains_sensitive_data": False, "base_model_target": "gemma4:12b", "created_by": "test",
            },
        }, separators=(",", ":"))
        eval_line = json.dumps({
            "messages": [
                {"role": "system", "content": "You are a planning assistant."},
                {"role": "user", "content": shared_goal},
                {"role": "assistant", "content": '{"plan":"p2","tasks":[{"role":"RESEARCHER","title":"Research approach","description":"Read existing patterns and summarize key approaches for the shared module implementation.","output_file":"research.md"}]}'},
            ],
            "metadata": {
                "category": "boss_planning", "source": "manual", "quality": "silver",
                "contains_sensitive_data": False, "base_model_target": "gemma4:12b", "created_by": "test",
            },
        }, separators=(",", ":"))

        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [train_line])
            env.write_jsonl("eval",     [eval_line])
            env.write_jsonl("negative", [_make_valid_line("neg1")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        dup_check = next(c for c in result.checks if c.name == "duplicates")
        self.assertEqual(dup_check.status, "BLOCKED")

    def test_unique_examples_pass_duplicate_check(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train_unique_1")])
            env.write_jsonl("eval",     [_make_valid_line("eval_unique_2")])
            env.write_jsonl("negative", [_make_valid_line("neg_unique_3")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        dup_check = next(c for c in result.checks if c.name == "duplicates")
        self.assertEqual(dup_check.status, "PASS")


class TestPreflightEvalIsolation(unittest.TestCase):
    """Check 8: fixed eval prompts must not appear in train_v1.jsonl."""

    def test_eval_prompt_in_train_is_blocked(self):
        """If a fixed eval prompt appears as a training example, block."""
        eval_prompt = "Goal: Write a utility module for eval_isolation_test_goal"
        # Make a train line whose user message matches the eval prompt exactly
        train_line = json.dumps({
            "messages": [
                {"role": "system", "content": "You are a planning assistant."},
                {"role": "user", "content": eval_prompt},
                {"role": "assistant", "content": '{"plan":"p","tasks":[{"role":"CODER","title":"Write file.py","description":"Implement the requested utility with full error handling and documentation.","output_file":"file.py"}]}'},
            ],
            "metadata": {
                "category": "boss_planning", "source": "manual", "quality": "silver",
                "contains_sensitive_data": False, "base_model_target": "gemma4:12b", "created_by": "test",
            },
        }, separators=(",", ":"))

        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [train_line])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1")])
            # Write the eval prompt to the evals dir
            env.write_eval_prompts([eval_prompt])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        isolation_check = next(c for c in result.checks if c.name == "eval_isolation")
        self.assertEqual(isolation_check.status, "BLOCKED")

    def test_disjoint_eval_prompts_pass(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train_only_goal")])
            env.write_jsonl("eval",     [_make_valid_line("eval1")])
            env.write_jsonl("negative", [_make_valid_line("neg1")])
            env.write_eval_prompts(["Goal: A completely different eval prompt not in train"])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        isolation_check = next(c for c in result.checks if c.name == "eval_isolation")
        self.assertEqual(isolation_check.status, "PASS")


class TestPreflightReady(unittest.TestCase):
    """Full READY path with lowered thresholds and valid data."""

    def test_ready_with_lowered_thresholds(self):
        """With min=1 per split and valid data, preflight should return READY."""
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train_ready_goal")])
            env.write_jsonl("eval",     [_make_valid_line("eval_ready_goal")])
            env.write_jsonl("negative", [_make_valid_line("neg_ready_goal", quality="rejected")])
            # No eval prompts → eval_isolation passes trivially
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        self.assertTrue(result.ready, msg=f"Expected READY but got BLOCKED: {[c.name + ': ' + c.message for c in result.blocked_checks()]}")

    def test_ready_result_has_no_blocked_checks(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("train_a")])
            env.write_jsonl("eval",     [_make_valid_line("eval_b")])
            env.write_jsonl("negative", [_make_valid_line("neg_c", quality="rejected")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)

        blocked = result.blocked_checks()
        self.assertEqual(len(blocked), 0, msg=f"Expected no blocked checks, got: {[c.name for c in blocked]}")


class TestPreflightJsonOutput(unittest.TestCase):
    """JSON output format and correctness."""

    def test_json_output_is_valid_json(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=1, min_eval=1, min_negative=1)
            json_str = format_json_output(result)

        parsed = json.loads(json_str)
        self.assertIsInstance(parsed, dict)

    def test_json_output_has_required_fields(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=1, min_eval=1, min_negative=1)
            json_str = format_json_output(result)

        parsed = json.loads(json_str)
        for field_name in ("ready", "counts", "checks", "blocked_reasons", "errors", "thresholds"):
            self.assertIn(field_name, parsed, msg=f"Missing field: {field_name}")

    def test_json_output_ready_false_when_blocked(self):
        with _TempDatasetEnv() as env:
            # No manifest written → BLOCKED
            json_str = format_json_output(env.run(min_train=1, min_eval=1, min_negative=1))

        parsed = json.loads(json_str)
        self.assertFalse(parsed["ready"])

    def test_json_output_ready_true_when_ready(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("j_train")])
            env.write_jsonl("eval",     [_make_valid_line("j_eval")])
            env.write_jsonl("negative", [_make_valid_line("j_neg", quality="rejected")])
            json_str = format_json_output(env.run(min_train=1, min_eval=1, min_negative=1))

        parsed = json.loads(json_str)
        self.assertTrue(parsed["ready"])

    def test_json_output_thresholds_respected(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=42, min_eval=7, min_negative=13)
            json_str = format_json_output(result)

        parsed = json.loads(json_str)
        thresholds = parsed["thresholds"]
        self.assertEqual(thresholds["min_train"], 42)
        self.assertEqual(thresholds["min_eval"], 7)
        self.assertEqual(thresholds["min_negative"], 13)

    def test_json_checks_have_name_status_message(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=1, min_eval=1, min_negative=1)
            json_str = format_json_output(result)

        parsed = json.loads(json_str)
        for check in parsed["checks"]:
            self.assertIn("name", check)
            self.assertIn("status", check)
            self.assertIn("message", check)


class TestPreflightHumanOutput(unittest.TestCase):
    """Human-readable output contains expected sections."""

    def test_human_output_contains_status(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=1, min_eval=1, min_negative=1)
            output = format_human(result)

        self.assertIn("Status:", output)
        self.assertIn("BLOCKED", output)

    def test_human_output_contains_counts_section(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=1, min_eval=1, min_negative=1)
            output = format_human(result)

        self.assertIn("Counts:", output)

    def test_human_output_contains_checks_section(self):
        with _TempDatasetEnv() as env:
            result = env.run(min_train=1, min_eval=1, min_negative=1)
            output = format_human(result)

        self.assertIn("Checks:", output)

    def test_human_output_ready_says_ready(self):
        entries = {
            "ex_001": _make_entry("train"),
            "ex_002": _make_entry("eval"),
            "ex_003": _make_entry("negative"),
        }
        with _TempDatasetEnv() as env:
            env.write_manifest(_make_manifest(entries))
            env.write_jsonl("train",    [_make_valid_line("h_train")])
            env.write_jsonl("eval",     [_make_valid_line("h_eval")])
            env.write_jsonl("negative", [_make_valid_line("h_neg", quality="rejected")])
            result = env.run(min_train=1, min_eval=1, min_negative=1)
            output = format_human(result)

        self.assertIn("READY", output)


class TestCheckResultHelpers(unittest.TestCase):
    """Unit tests for CheckResult properties."""

    def test_pass_is_not_blocked(self):
        c = CheckResult(name="x", status="PASS", message="ok")
        self.assertTrue(c.passed)
        self.assertFalse(c.blocked)

    def test_blocked_is_blocked(self):
        c = CheckResult(name="x", status="BLOCKED", message="fail")
        self.assertFalse(c.passed)
        self.assertTrue(c.blocked)

    def test_error_is_blocked(self):
        c = CheckResult(name="x", status="ERROR", message="crash")
        self.assertFalse(c.passed)
        self.assertTrue(c.blocked)

    def test_skip_is_not_blocked(self):
        c = CheckResult(name="x", status="SKIP", message="skipped")
        self.assertFalse(c.passed)
        self.assertFalse(c.blocked)


if __name__ == "__main__":
    unittest.main(verbosity=2)
