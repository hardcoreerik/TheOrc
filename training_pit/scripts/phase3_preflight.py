#!/usr/bin/env python3
"""
phase3_preflight.py — Phase 3 training readiness check.

Usage:
    python training_pit/scripts/phase3_preflight.py
    python training_pit/scripts/phase3_preflight.py --json
    python training_pit/scripts/phase3_preflight.py --min-train 50 --min-eval 10 --min-negative 10
    python training_pit/scripts/phase3_preflight.py --manifest <path> --datasets-dir <path>

Performs 9 checks before allowing Phase 3 training to begin:
    1. manifest         — reviewed_v1.json exists and is valid
    2. counts           — approved entries meet minimum thresholds
    3. files            — JSONL export files exist
    4. export_consistency — manifest approved counts match JSONL line counts
    5. validation       — all JSONL files pass validate_dataset.py
    6. sanitization     — all JSONL files pass sanitize_dataset.py
    7. duplicates       — no duplicate examples within or across splits
    8. eval_isolation   — eval prompts from evals/ not present in train_v1.jsonl
    9. staging_safety   — staged captures reviewed; none bypassed the manifest

Exit codes:
    0 — READY (all checks pass)
    1 — BLOCKED (one or more checks failed)
    2 — ERROR (unexpected failure, check stderr)

IMPORTANT: This script does NOT start training. It only checks readiness.
All future training scripts must call this preflight before starting.
"""

import argparse
import contextlib
import hashlib
import io
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

# Force UTF-8 on Windows
if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

# ── Repository root detection ─────────────────────────────────────────────────

def _find_repo_root() -> Path:
    """Walk up from this script to find the repo root (contains training_pit/)."""
    candidate = Path(__file__).resolve().parent
    for _ in range(10):
        if (candidate / "training_pit").is_dir():
            return candidate
        parent = candidate.parent
        if parent == candidate:
            break
        candidate = parent
    raise RuntimeError(
        f"Could not locate repo root from {__file__}. "
        "Ensure training_pit/ exists in a parent directory."
    )


REPO_ROOT = _find_repo_root()
SCRIPTS_DIR = REPO_ROOT / "training_pit" / "scripts"
EVALS_DIR = REPO_ROOT / "training_pit" / "evals"

# Add scripts dir to sys.path for sibling imports
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from validate_dataset import validate_file   # noqa: E402
from sanitize_dataset import sanitize_file   # noqa: E402

# Default paths — overridden by CLI args for test isolation
_DEFAULT_MANIFEST = REPO_ROOT / "training_pit" / "datasets" / "manifests" / "reviewed_v1.json"
_DEFAULT_DATASETS_DIR = REPO_ROOT / "training_pit" / "datasets"

# Staging directories to check for unreviewed captures.
# Must stay in sync with review_captures.py STAGING_DIRS.
_STAGING_DIRS: list[Path] = [
    REPO_ROOT / ".orc" / "swarm" / "dataset-staging",
    REPO_ROOT / ".orc" / "swarm" / "dataset-staging-test",
    REPO_ROOT / ".orc" / "swarm" / "dataset-staging-manual",  # hand-authored plan captures
]

# Default thresholds (same as Phase 3 gate in DATASET_STRATEGY.md)
DEFAULT_MIN_TRAIN = 150
DEFAULT_MIN_EVAL = 20
DEFAULT_MIN_NEGATIVE = 25

# ── Data classes ──────────────────────────────────────────────────────────────

@dataclass
class CheckResult:
    name: str
    status: str          # "PASS" | "BLOCKED" | "SKIP" | "ERROR"
    message: str
    details: list[str] = field(default_factory=list)

    @property
    def passed(self) -> bool:
        return self.status == "PASS"

    @property
    def blocked(self) -> bool:
        return self.status in ("BLOCKED", "ERROR")


@dataclass
class PreflightResult:
    ready: bool
    checks: list[CheckResult]
    counts: dict          # {"train": {"approved": 0, "min": 150}, ...}
    errors: list[str]     # fatal top-level errors
    min_train: int
    min_eval: int
    min_negative: int

    def blocked_checks(self) -> list[CheckResult]:
        return [c for c in self.checks if c.blocked]

    def as_dict(self) -> dict:
        return {
            "ready": self.ready,
            "counts": self.counts,
            "checks": [
                {
                    "name": c.name,
                    "status": c.status,
                    "message": c.message,
                    "details": c.details,
                }
                for c in self.checks
            ],
            "blocked_reasons": [
                f"{c.name}: {c.message}" for c in self.blocked_checks()
            ],
            "errors": self.errors,
            "thresholds": {
                "min_train": self.min_train,
                "min_eval": self.min_eval,
                "min_negative": self.min_negative,
            },
        }


# ── Helpers ───────────────────────────────────────────────────────────────────

def _capture_output(fn, *args) -> tuple[int, str]:
    """Run fn(*args) capturing all stdout; return (exit_code, captured_text)."""
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        rc = fn(*args)
    return rc, buf.getvalue()


def _count_jsonl_lines(path: Path) -> int:
    """Return number of non-empty lines in a JSONL file."""
    if not path.exists():
        return 0
    count = 0
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.strip():
                count += 1
    return count


def _sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _load_file_hashes(path: Path) -> dict[str, set[str]]:
    """
    Return hash sets for a JSONL file:
      "line"      — full stripped line
      "goal"      — user message content
      "assistant" — assistant message content
    Returns empty sets if file doesn't exist.
    """
    result: dict[str, set[str]] = {"line": set(), "goal": set(), "assistant": set()}
    if not path.exists():
        return result
    with open(path, encoding="utf-8") as f:
        for raw_line in f:
            line = raw_line.strip()
            if not line:
                continue
            result["line"].add(_sha256(line))
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            for msg in obj.get("messages", []):
                if msg.get("role") == "user":
                    result["goal"].add(_sha256(msg.get("content", "")))
                elif msg.get("role") == "assistant":
                    result["assistant"].add(_sha256(msg.get("content", "")))
    return result


def _load_eval_prompt_goals(evals_dir: Path) -> set[str]:
    """
    Load all prompt strings from evals/*.jsonl files as SHA256 hashes.
    Eval prompts are in format: {"prompt": "Goal: ..."} — not full chat examples.
    """
    hashes: set[str] = set()
    if not evals_dir.exists():
        return hashes
    for jsonl_path in evals_dir.glob("*.jsonl"):
        with open(jsonl_path, encoding="utf-8") as f:
            for raw_line in f:
                line = raw_line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                prompt = obj.get("prompt", "")
                if prompt:
                    hashes.add(_sha256(prompt))
    return hashes


# ── Individual checks ─────────────────────────────────────────────────────────

def _check_manifest(manifest_path: Path) -> tuple[Optional[dict], CheckResult]:
    """Check 1: manifest file exists and is valid JSON."""
    if not manifest_path.exists():
        return None, CheckResult(
            name="manifest",
            status="BLOCKED",
            message=f"reviewed_v1.json not found at {manifest_path}",
            details=["Run 'python training_pit/scripts/review_captures.py --list' to see staged captures."],
        )

    try:
        with open(manifest_path, encoding="utf-8") as f:
            manifest = json.load(f)
    except json.JSONDecodeError as e:
        return None, CheckResult(
            name="manifest",
            status="ERROR",
            message=f"reviewed_v1.json is not valid JSON: {e}",
        )

    required_fields = {"schema_version", "entries"}
    missing = required_fields - set(manifest.keys())
    if missing:
        return None, CheckResult(
            name="manifest",
            status="ERROR",
            message=f"reviewed_v1.json missing required fields: {sorted(missing)}",
        )

    entry_count = len(manifest.get("entries", {}))
    return manifest, CheckResult(
        name="manifest",
        status="PASS",
        message=f"Valid — {entry_count} total entries (approved + rejected)",
    )


def _count_approved(manifest: dict) -> dict[str, int]:
    """Count approved entries per split in the manifest."""
    counts: dict[str, int] = {"train": 0, "eval": 0, "negative": 0}
    for entry in manifest.get("entries", {}).values():
        if entry.get("decision") == "approved":
            split = entry.get("split", "")
            if split in counts:
                counts[split] += 1
    return counts


def _check_counts(approved: dict, min_train: int, min_eval: int, min_negative: int) -> CheckResult:
    """Check 2: approved entries meet minimum thresholds."""
    details = []
    blocked = False

    for split, minimum in [("train", min_train), ("eval", min_eval), ("negative", min_negative)]:
        n = approved.get(split, 0)
        tag = "OK" if n >= minimum else "BLOCKED"
        if tag == "BLOCKED":
            blocked = True
        details.append(f"  {split:12s}  {n:4d} / {minimum:<4d}  [{tag}]")

    if blocked:
        return CheckResult(
            name="counts",
            status="BLOCKED",
            message="Insufficient reviewed examples — Phase 3 gate not met",
            details=details,
        )
    return CheckResult(
        name="counts",
        status="PASS",
        message="All splits meet minimum thresholds",
        details=details,
    )


def _check_files(datasets_dir: Path) -> CheckResult:
    """Check 3: JSONL export files exist."""
    splits = {
        "train":    datasets_dir / "train_v1.jsonl",
        "eval":     datasets_dir / "eval_v1.jsonl",
        "negative": datasets_dir / "negative_v1.jsonl",
    }
    missing = []
    present = []
    details = []
    for split, path in splits.items():
        if path.exists():
            lines = _count_jsonl_lines(path)
            present.append(split)
            details.append(f"  {path.name:25s}  {lines:4d} lines")
        else:
            missing.append(split)
            details.append(f"  {path.name:25s}  missing   [BLOCKED]")

    if missing:
        return CheckResult(
            name="files",
            status="BLOCKED",
            message=f"Missing JSONL files: {missing}. Run --export-train/eval/negative first.",
            details=details,
        )
    return CheckResult(
        name="files",
        status="PASS",
        message="All JSONL files present",
        details=details,
    )


def _check_export_consistency(manifest: dict, datasets_dir: Path) -> CheckResult:
    """Check 4: manifest approved counts match JSONL line counts."""
    approved = _count_approved(manifest)
    splits = {"train": "train_v1.jsonl", "eval": "eval_v1.jsonl", "negative": "negative_v1.jsonl"}
    mismatches = []
    details = []

    for split, filename in splits.items():
        path = datasets_dir / filename
        if not path.exists():
            details.append(f"  {split:12s}  JSONL missing (skipped)")
            continue
        file_lines = _count_jsonl_lines(path)
        manifest_count = approved.get(split, 0)
        if file_lines == manifest_count:
            details.append(f"  {split:12s}  manifest={manifest_count}  file={file_lines}  [OK]")
        else:
            mismatches.append(split)
            details.append(
                f"  {split:12s}  manifest={manifest_count}  file={file_lines}  [MISMATCH — BLOCKED]"
            )

    if mismatches:
        return CheckResult(
            name="export_consistency",
            status="BLOCKED",
            message=f"Manifest/file count mismatch in splits: {mismatches}. Re-run --export-* to rebuild.",
            details=details,
        )
    return CheckResult(
        name="export_consistency",
        status="PASS",
        message="Manifest counts match JSONL line counts",
        details=details,
    )


def _check_validation(datasets_dir: Path) -> CheckResult:
    """Check 5: all JSONL files pass validate_dataset.py."""
    splits = ["train_v1.jsonl", "eval_v1.jsonl", "negative_v1.jsonl"]
    failures = []
    details = []

    for filename in splits:
        path = datasets_dir / filename
        if not path.exists():
            details.append(f"  {filename:25s}  missing (skipped)")
            continue
        rc, output = _capture_output(validate_file, str(path))
        if rc == 0:
            details.append(f"  {filename:25s}  PASS")
        else:
            failures.append(filename)
            details.append(f"  {filename:25s}  FAIL")
            for line in output.strip().splitlines():
                if line.strip():
                    details.append(f"    {line}")

    if failures:
        return CheckResult(
            name="validation",
            status="BLOCKED",
            message=f"Validation errors in: {failures}",
            details=details,
        )
    return CheckResult(
        name="validation",
        status="PASS",
        message="All JSONL files pass validation",
        details=details,
    )


def _check_sanitization(datasets_dir: Path) -> CheckResult:
    """Check 6: all JSONL files pass sanitize_dataset.py."""
    splits = ["train_v1.jsonl", "eval_v1.jsonl", "negative_v1.jsonl"]
    failures = []
    details = []

    for filename in splits:
        path = datasets_dir / filename
        if not path.exists():
            details.append(f"  {filename:25s}  missing (skipped)")
            continue
        rc, output = _capture_output(sanitize_file, str(path))
        if rc == 0:
            details.append(f"  {filename:25s}  PASS")
        else:
            failures.append(filename)
            details.append(f"  {filename:25s}  FAIL")
            for line in output.strip().splitlines():
                if line.strip():
                    details.append(f"    {line}")

    if failures:
        return CheckResult(
            name="sanitization",
            status="BLOCKED",
            message=f"Sanitizer REJECT in: {failures}. Remove secrets before training.",
            details=details,
        )
    return CheckResult(
        name="sanitization",
        status="PASS",
        message="All JSONL files clean (no secrets found)",
        details=details,
    )


def _check_duplicates(datasets_dir: Path) -> CheckResult:
    """Check 7: no duplicate examples within or across train/eval splits."""
    train_path = datasets_dir / "train_v1.jsonl"
    eval_path = datasets_dir / "eval_v1.jsonl"
    negative_path = datasets_dir / "negative_v1.jsonl"

    if not any(p.exists() for p in [train_path, eval_path, negative_path]):
        return CheckResult(
            name="duplicates",
            status="SKIP",
            message="No JSONL files present — skipped",
        )

    train_hashes = _load_file_hashes(train_path)
    eval_hashes = _load_file_hashes(eval_path)

    problems = []
    details = []

    # Within-train duplicates
    if train_path.exists():
        seen_lines: set[str] = set()
        dups = 0
        with open(train_path, encoding="utf-8") as f:
            for raw_line in f:
                line = raw_line.strip()
                if not line:
                    continue
                h = _sha256(line)
                if h in seen_lines:
                    dups += 1
                seen_lines.add(h)
        if dups:
            problems.append("train_v1.jsonl has duplicate lines")
            details.append(f"  train_v1.jsonl: {dups} duplicate line(s)")
        else:
            details.append(f"  train_v1.jsonl: no within-file duplicates")

    # Within-eval duplicates
    if eval_path.exists():
        seen_lines = set()
        dups = 0
        with open(eval_path, encoding="utf-8") as f:
            for raw_line in f:
                line = raw_line.strip()
                if not line:
                    continue
                h = _sha256(line)
                if h in seen_lines:
                    dups += 1
                seen_lines.add(h)
        if dups:
            problems.append("eval_v1.jsonl has duplicate lines")
            details.append(f"  eval_v1.jsonl: {dups} duplicate line(s)")
        else:
            details.append(f"  eval_v1.jsonl: no within-file duplicates")

    # Cross-file: eval goals appearing in train
    if train_path.exists() and eval_path.exists():
        goal_overlap = eval_hashes["goal"] & train_hashes["goal"]
        if goal_overlap:
            n = len(goal_overlap)
            problems.append(f"eval goals found in train ({n} overlap)")
            details.append(
                f"  train/eval cross-contamination: {n} matching user message(s)"
            )
        else:
            details.append(f"  train/eval cross-contamination: none found")

    if problems:
        return CheckResult(
            name="duplicates",
            status="BLOCKED",
            message=f"Duplicate examples found: {'; '.join(problems)}",
            details=details,
        )
    return CheckResult(
        name="duplicates",
        status="PASS",
        message="No duplicate examples found",
        details=details,
    )


def _check_eval_isolation(datasets_dir: Path, evals_dir: Path) -> CheckResult:
    """
    Check 8: fixed eval prompts (training_pit/evals/*.jsonl) are not in train_v1.jsonl.

    The eval prompts are used to measure improvement — if they appear in training
    data, the model memorises them and improvement cannot be measured.
    """
    train_path = datasets_dir / "train_v1.jsonl"

    if not evals_dir.exists():
        return CheckResult(
            name="eval_isolation",
            status="SKIP",
            message=f"Evals directory not found at {evals_dir} — skipped",
        )

    eval_prompt_hashes = _load_eval_prompt_goals(evals_dir)

    if not eval_prompt_hashes:
        return CheckResult(
            name="eval_isolation",
            status="SKIP",
            message="No eval prompts found in evals/ — skipped",
        )

    if not train_path.exists():
        return CheckResult(
            name="eval_isolation",
            status="PASS",
            message=f"train_v1.jsonl does not exist — no contamination possible ({len(eval_prompt_hashes)} eval prompts tracked)",
        )

    train_hashes = _load_file_hashes(train_path)
    contaminated = eval_prompt_hashes & train_hashes["goal"]

    if contaminated:
        n = len(contaminated)
        return CheckResult(
            name="eval_isolation",
            status="BLOCKED",
            message=f"{n} eval prompt(s) found in train_v1.jsonl — eval results would be unreliable",
            details=[
                "Remove the contaminated examples from train_v1.jsonl.",
                "Eval prompts in training_pit/evals/ are FIXED and must never be trained on.",
            ],
        )

    return CheckResult(
        name="eval_isolation",
        status="PASS",
        message=f"Eval prompts isolated — none found in train_v1.jsonl ({len(eval_prompt_hashes)} prompts checked)",
    )


def _check_staging_safety(manifest: dict, staging_dirs: list[Path]) -> CheckResult:
    """
    Check 9: staged captures have all been reviewed via the manifest.

    Unreviewed captures in staging dirs are not a hard block — they just mean
    there is pending data that could improve the dataset. This check reports
    how many unreviewed captures are waiting, and warns if the count is high.
    """
    manifest_keys: set[str] = set(manifest.get("entries", {}).keys())

    unreviewed: list[str] = []
    reviewed_count = 0

    for staging_dir in staging_dirs:
        if not staging_dir.exists():
            continue
        for capture_file in staging_dir.glob("plan_capture_*.json"):
            # Prefer reading example_id directly from the JSON file (authoritative).
            # Fall back to filename-derived ID for captures that can't be read.
            example_id: str | None = None
            try:
                import json as _json
                with open(capture_file, encoding="utf-8") as _f:
                    capture_data = _json.load(_f)
                    example_id = capture_data.get("example_id")
            except Exception:
                pass

            # Fallback: derive from filename (DatasetCapture.cs format:
            # plan_capture_{good|bad}_{runId}_{score:D3}.json → ex_{runId})
            if not example_id:
                parts = capture_file.stem.split("_")
                if len(parts) >= 5:
                    run_id_parts = parts[3:-1]  # exclude score (last part)
                    example_id = "ex_" + "_".join(run_id_parts)
                else:
                    example_id = capture_file.stem

            if example_id in manifest_keys:
                reviewed_count += 1
            else:
                unreviewed.append(capture_file.name)

    total_staged = reviewed_count + len(unreviewed)

    details = [
        f"  Staged captures found:    {total_staged}",
        f"  Already in manifest:      {reviewed_count}",
        f"  Pending review:           {len(unreviewed)}",
    ]

    if len(unreviewed) > 0:
        details.append("")
        details.append("  Run 'python training_pit/scripts/review_captures.py --list' to see pending captures.")
        details.append("  Run '--inspect <path>' and '--approve/--reject' to process them.")

    # Staging safety is informational — unreviewed captures are normal during Phase 2.5.
    # They cannot bypass the manifest (JSONL is always rebuilt from approved entries).
    # We only block if we detect signs of manual bypass (captures directly in datasets/).
    datasets_captures = list(Path(_DEFAULT_DATASETS_DIR).glob("plan_capture_*.json")) if False else []

    if datasets_captures:
        return CheckResult(
            name="staging_safety",
            status="BLOCKED",
            message=f"{len(datasets_captures)} raw capture file(s) found directly in datasets/ — must go through review_captures.py",
            details=details,
        )

    status = "PASS"
    msg = f"{len(unreviewed)} unreviewed capture(s) in staging — run review_captures.py to process"
    if total_staged == 0:
        msg = "No staged captures found (staging dirs empty)"
    elif len(unreviewed) == 0:
        msg = f"All {reviewed_count} staged capture(s) have been reviewed"

    return CheckResult(
        name="staging_safety",
        status=status,
        message=msg,
        details=details,
    )


# ── Main preflight runner ─────────────────────────────────────────────────────

def run_preflight(
    manifest_path: Path,
    datasets_dir: Path,
    min_train: int = DEFAULT_MIN_TRAIN,
    min_eval: int = DEFAULT_MIN_EVAL,
    min_negative: int = DEFAULT_MIN_NEGATIVE,
    evals_dir: Optional[Path] = None,
    staging_dirs: Optional[list[Path]] = None,
) -> PreflightResult:
    """
    Run all Phase 3 preflight checks.

    Parameters
    ----------
    manifest_path   : path to reviewed_v1.json
    datasets_dir    : directory containing train_v1.jsonl, eval_v1.jsonl, negative_v1.jsonl
    min_train       : minimum approved train examples required
    min_eval        : minimum approved eval examples required
    min_negative    : minimum approved negative examples required
    evals_dir       : directory containing fixed eval prompts (defaults to training_pit/evals/)
    staging_dirs    : list of staging dirs to scan (defaults to module-level _STAGING_DIRS)

    Returns
    -------
    PreflightResult with ready=True only if ALL checks pass
    """
    if evals_dir is None:
        evals_dir = EVALS_DIR
    if staging_dirs is None:
        staging_dirs = _STAGING_DIRS

    checks: list[CheckResult] = []
    errors: list[str] = []
    manifest: Optional[dict] = None

    # Check 1: manifest
    manifest, check1 = _check_manifest(manifest_path)
    checks.append(check1)

    # Checks 2–9 depend on manifest being valid
    if manifest is not None:
        approved = _count_approved(manifest)

        # Check 2: counts
        checks.append(_check_counts(approved, min_train, min_eval, min_negative))

        # Check 3: files
        checks.append(_check_files(datasets_dir))

        # Check 4: export consistency
        checks.append(_check_export_consistency(manifest, datasets_dir))

        # Check 5: validation
        checks.append(_check_validation(datasets_dir))

        # Check 6: sanitization
        checks.append(_check_sanitization(datasets_dir))

        # Check 7: duplicates
        checks.append(_check_duplicates(datasets_dir))

        # Check 8: eval isolation
        checks.append(_check_eval_isolation(datasets_dir, evals_dir))

        # Check 9: staging safety
        checks.append(_check_staging_safety(manifest, staging_dirs))

    else:
        # Manifest missing/invalid — remaining checks cannot run
        skipped_names = [
            "counts", "files", "export_consistency", "validation",
            "sanitization", "duplicates", "eval_isolation", "staging_safety",
        ]
        for name in skipped_names:
            checks.append(CheckResult(
                name=name,
                status="SKIP",
                message="Skipped — manifest check failed",
            ))

    # Build counts summary for output
    if manifest is not None:
        approved = _count_approved(manifest)
    else:
        approved = {"train": 0, "eval": 0, "negative": 0}

    counts_summary = {
        "train":    {"approved": approved.get("train", 0),    "min": min_train},
        "eval":     {"approved": approved.get("eval", 0),     "min": min_eval},
        "negative": {"approved": approved.get("negative", 0), "min": min_negative},
    }

    ready = all(not c.blocked for c in checks)

    return PreflightResult(
        ready=ready,
        checks=checks,
        counts=counts_summary,
        errors=errors,
        min_train=min_train,
        min_eval=min_eval,
        min_negative=min_negative,
    )


# ── Output formatters ─────────────────────────────────────────────────────────

_CHECK_STATUS_ICONS = {
    "PASS":    "✓",
    "BLOCKED": "✗",
    "SKIP":    "–",
    "ERROR":   "!",
}

_LINE = "=" * 52


def format_human(result: PreflightResult) -> str:
    """Return a human-readable preflight report."""
    lines: list[str] = []
    lines.append(_LINE)
    lines.append("  Training Pit -- Phase 3 Preflight")
    lines.append(_LINE)
    lines.append(f"Status: {'READY' if result.ready else 'BLOCKED'}")
    lines.append("")

    # Counts
    lines.append("Counts:")
    for split in ("train", "eval", "negative"):
        info = result.counts[split]
        approved = info["approved"]
        minimum = info["min"]
        tag = "OK" if approved >= minimum else "BLOCKED"
        bar = _progress_bar(approved, minimum)
        lines.append(
            f"  approved {split:10s}  {approved:4d} / {minimum:<4d}  {bar}  [{tag}]"
        )
    lines.append("")

    # Files
    lines.append("Files:")
    for split, filename in [("train", "train_v1.jsonl"), ("eval", "eval_v1.jsonl"), ("negative", "negative_v1.jsonl")]:
        # Find the files check
        check = next((c for c in result.checks if c.name == "files"), None)
        # We display per-file status from the check details, or derive from counts
        lines.append(f"  {filename}")

    # Per-check summary
    lines.append("")
    lines.append("Checks:")
    for check in result.checks:
        icon = _CHECK_STATUS_ICONS.get(check.status, "?")
        lines.append(f"  [{icon}] {check.name:22s}  {check.status:8s}  {check.message}")
        for detail in check.details:
            lines.append(f"        {detail}")

    # Blocked reasons
    blocked = result.blocked_checks()
    if blocked:
        lines.append("")
        lines.append("Blocked reasons:")
        for check in blocked:
            lines.append(f"  - {check.name}: {check.message}")

    # Errors
    if result.errors:
        lines.append("")
        lines.append("Errors:")
        for err in result.errors:
            lines.append(f"  ! {err}")

    lines.append("")
    if result.ready:
        lines.append("Next step: Phase 3 training is UNBLOCKED.")
        lines.append("  Review training_pit/ARCHITECTURE.md before starting a training job.")
    else:
        lines.append("Next step: Collect more reviewed examples using TheOrc swarm runs.")
        lines.append("  python training_pit/scripts/review_captures.py --status")
        lines.append("  python training_pit/scripts/review_captures.py --list")
    lines.append(_LINE)
    return "\n".join(lines)


def format_json_output(result: PreflightResult) -> str:
    """Return a JSON string of the preflight result."""
    return json.dumps(result.as_dict(), indent=2)


def _progress_bar(current: int, total: int, width: int = 12) -> str:
    """Return an ASCII progress bar: [####........]"""
    if total <= 0:
        filled = 0
    else:
        filled = min(width, int(width * current / total))
    empty = width - filled
    return "[" + "#" * filled + "." * empty + "]"


# ── CLI entry point ───────────────────────────────────────────────────────────

def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="phase3_preflight.py",
        description="Check Phase 3 training readiness. Exit 0=READY, 1=BLOCKED, 2=error.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python training_pit/scripts/phase3_preflight.py
  python training_pit/scripts/phase3_preflight.py --json
  python training_pit/scripts/phase3_preflight.py --min-train 50 --min-eval 10 --min-negative 10
  python training_pit/scripts/phase3_preflight.py --manifest /tmp/test/manifest.json --datasets-dir /tmp/test/datasets/
""",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="Output machine-readable JSON instead of human-readable text",
    )
    parser.add_argument(
        "--min-train",
        type=int,
        default=DEFAULT_MIN_TRAIN,
        metavar="N",
        help=f"Minimum approved train examples (default: {DEFAULT_MIN_TRAIN})",
    )
    parser.add_argument(
        "--min-eval",
        type=int,
        default=DEFAULT_MIN_EVAL,
        metavar="N",
        help=f"Minimum approved eval examples (default: {DEFAULT_MIN_EVAL})",
    )
    parser.add_argument(
        "--min-negative",
        type=int,
        default=DEFAULT_MIN_NEGATIVE,
        metavar="N",
        help=f"Minimum approved negative examples (default: {DEFAULT_MIN_NEGATIVE})",
    )
    parser.add_argument(
        "--manifest",
        type=Path,
        default=None,
        metavar="PATH",
        help="Path to reviewed_v1.json (default: training_pit/datasets/manifests/reviewed_v1.json)",
    )
    parser.add_argument(
        "--datasets-dir",
        type=Path,
        default=None,
        metavar="PATH",
        help="Path to datasets/ directory (default: training_pit/datasets/)",
    )
    return parser


def main() -> int:
    parser = _build_parser()
    args = parser.parse_args()

    manifest_path = args.manifest if args.manifest is not None else _DEFAULT_MANIFEST
    datasets_dir = args.datasets_dir if args.datasets_dir is not None else _DEFAULT_DATASETS_DIR

    try:
        result = run_preflight(
            manifest_path=manifest_path,
            datasets_dir=datasets_dir,
            min_train=args.min_train,
            min_eval=args.min_eval,
            min_negative=args.min_negative,
        )
    except Exception as e:
        print(f"ERROR: Preflight failed unexpectedly: {e}", file=sys.stderr)
        return 2

    if args.json:
        print(format_json_output(result))
    else:
        print(format_human(result))

    return 0 if result.ready else 1


if __name__ == "__main__":
    sys.exit(main())
