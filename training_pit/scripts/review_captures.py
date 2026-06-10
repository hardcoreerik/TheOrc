#!/usr/bin/env python3
"""
review_captures.py — Dataset Review / Approval Valve for TheOrc Training Pit.

Usage (run from repo root):
    python training_pit/scripts/review_captures.py --list
    python training_pit/scripts/review_captures.py --inspect .orc/swarm/dataset-staging/plan_capture_good_run001_085.json
    python training_pit/scripts/review_captures.py --approve .orc/swarm/dataset-staging/plan_capture_good_run001_085.json --split train --quality silver
    python training_pit/scripts/review_captures.py --reject .orc/swarm/dataset-staging/plan_capture_bad_run002_005.json --note "Collapse pattern"
    python training_pit/scripts/review_captures.py --export-train
    python training_pit/scripts/review_captures.py --export-eval
    python training_pit/scripts/review_captures.py --export-negative
    python training_pit/scripts/review_captures.py --status

Approval rules:
    train split    — quality must be gold or silver
    eval split     — quality may be gold, silver, draft, or rejected
    negative split — quality may be gold, silver, draft, or rejected

Export gate (fail closed):
    Converts approved captures to chat-JSONL → writes temp file →
    runs validate_dataset → runs sanitize_dataset →
    replaces final file only on success. Final file is unchanged on any failure.

Phase 3 gate thresholds:
    Train    >= 150 approved gold/silver examples
    Eval     >=  20 approved examples (any quality)
    Negative >=  25 approved examples

Requirements: none (stdlib only)
"""

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

# Force UTF-8 on Windows
if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

# ── Path resolution ───────────────────────────────────────────────────────────


def find_repo_root() -> Path:
    """
    Walk up from this script to find the repo root (dir containing training_pit/).
    Falls back to cwd if the walk fails.
    """
    # Start from the scripts/ directory (parent of this file)
    candidate = Path(__file__).resolve().parent
    for _ in range(6):
        if (candidate / "training_pit").is_dir():
            return candidate
        parent = candidate.parent
        if parent == candidate:
            break
        candidate = parent

    # Fallback: check cwd
    cwd = Path.cwd()
    if (cwd / "training_pit").is_dir():
        return cwd

    raise FileNotFoundError(
        "Cannot locate repo root. "
        "Run from the project root directory or ensure training_pit/ is a subdirectory."
    )


REPO_ROOT   = find_repo_root()
SCRIPTS_DIR = REPO_ROOT / "training_pit" / "scripts"

# Add scripts dir to sys.path so we can import sibling scripts
if str(SCRIPTS_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPTS_DIR))

from convert_plan_captures import capture_to_chat_jsonl  # noqa: E402
from validate_dataset      import validate_file          # noqa: E402
from sanitize_dataset      import sanitize_file          # noqa: E402

# ── Constants ─────────────────────────────────────────────────────────────────

MANIFEST_PATH = REPO_ROOT / "training_pit" / "datasets" / "manifests" / "reviewed_v1.json"

STAGING_DIRS: list[Path] = [
    REPO_ROOT / ".orc" / "swarm" / "dataset-staging",
    REPO_ROOT / ".orc" / "swarm" / "dataset-staging-test",
    REPO_ROOT / ".orc" / "swarm" / "dataset-staging-manual",  # hand-authored plan captures
]

OUTPUT_PATHS: dict[str, Path] = {
    "train":    REPO_ROOT / "training_pit" / "datasets" / "train_v1.jsonl",
    "eval":     REPO_ROOT / "training_pit" / "datasets" / "eval_v1.jsonl",
    "negative": REPO_ROOT / "training_pit" / "datasets" / "negative_v1.jsonl",
}

# Phase 3 gate thresholds
PHASE3_GATES: dict[str, int] = {
    "train":    150,
    "eval":     20,
    "negative": 25,
}

# Quality constraints per split
TRAIN_QUALITIES:    frozenset[str] = frozenset({"gold", "silver"})
EVAL_QUALITIES:     frozenset[str] = frozenset({"gold", "silver", "draft", "rejected"})
NEGATIVE_QUALITIES: frozenset[str] = frozenset({"gold", "silver", "draft", "rejected"})

SPLIT_QUALITY_MAP: dict[str, frozenset[str]] = {
    "train":    TRAIN_QUALITIES,
    "eval":     EVAL_QUALITIES,
    "negative": NEGATIVE_QUALITIES,
}

VALID_SPLITS:    frozenset[str] = frozenset(SPLIT_QUALITY_MAP.keys())
VALID_QUALITIES: frozenset[str] = frozenset({"gold", "silver", "draft", "rejected"})


# ── Manifest I/O ──────────────────────────────────────────────────────────────

def _now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def _empty_manifest() -> dict:
    return {
        "schema_version": "1.0",
        "created_at":     _now(),
        "last_modified":  _now(),
        "entries":        {},
    }


def load_manifest() -> dict:
    """Load reviewed_v1.json; return empty manifest if it doesn't exist."""
    if not MANIFEST_PATH.exists():
        return _empty_manifest()
    try:
        with open(MANIFEST_PATH, encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError) as e:
        print(f"ERROR: Cannot read manifest {MANIFEST_PATH}: {e}")
        sys.exit(1)


def save_manifest(manifest: dict) -> None:
    """
    Atomically save the manifest.
    Writes to a .tmp sibling, then replaces the final file.
    Leaves the manifest unchanged on any failure.
    """
    manifest["last_modified"] = _now()
    MANIFEST_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = MANIFEST_PATH.with_suffix(".tmp")
    try:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(manifest, f, indent=2, ensure_ascii=False)
            f.write("\n")
        tmp.replace(MANIFEST_PATH)
    except Exception as e:
        try:
            tmp.unlink()
        except OSError:
            pass
        print(f"ERROR: Cannot save manifest: {e}")
        sys.exit(1)


# ── Capture loading ───────────────────────────────────────────────────────────

def load_capture(path: Path) -> dict | None:
    """Load a plan capture JSON file. Returns None on parse or IO error."""
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError) as e:
        print(f"ERROR: Cannot load capture {path}: {e}")
        return None


def capture_key(capture: dict) -> str | None:
    """
    Return the stable manifest key for a capture.
    Primary key: example_id (set by DatasetCapture.cs as ex_{runId}).
    Fallback: sha256[:12] hash of the file's absolute path (for captures missing example_id).
    """
    eid = capture.get("example_id")
    return str(eid) if eid else None


def path_to_key(path: Path) -> str | None:
    """Load a capture from path and return its manifest key."""
    capture = load_capture(path)
    if capture is None:
        return None
    key = capture_key(capture)
    if not key:
        # Fallback: deterministic hash of path so the entry is still addressable
        key = "path_" + hashlib.sha256(str(path.resolve()).encode()).hexdigest()[:12]
    return key


def _rel(path: Path) -> str:
    """Return path relative to REPO_ROOT, or the absolute path string if outside."""
    try:
        return str(path.relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


# ── Staging scan ─────────────────────────────────────────────────────────────

def scan_staging() -> list[tuple[Path, str]]:
    """
    Scan all staging directories for plan capture files.
    Returns a list of (path, class_hint) where class_hint is 'positive' or 'negative'.
    Skips staging directories that don't exist yet.
    """
    results: list[tuple[Path, str]] = []
    for staging_dir in STAGING_DIRS:
        if not staging_dir.exists():
            continue
        for pattern, class_hint in [
            ("plan_capture_good_*.json", "positive"),
            ("plan_capture_bad_*.json",  "negative"),
        ]:
            for path in sorted(staging_dir.glob(pattern)):
                results.append((path, class_hint))
    return results


# ── Conversion helpers ────────────────────────────────────────────────────────

def _convert_for_export(capture: dict, entry: dict) -> dict | None:
    """
    Convert a capture to chat-JSONL using the reviewer's quality/split decision.
    Works for both positive and negative captures.

    Overrides:
    - metadata.quality  → reviewer's explicit quality tier
    - metadata.notes    → appended with review provenance
    """
    example = capture_to_chat_jsonl(capture)
    if example is None:
        return None

    # Apply reviewer's quality decision (overrides auto-derived tier from score)
    example["metadata"]["quality"] = entry["quality"]

    # Append review provenance
    ts              = entry.get("decided_at", "unknown")
    reviewer_note   = entry.get("note", "").strip()
    existing_notes  = example["metadata"].get("notes", "")
    provenance      = f"Reviewed {ts}."
    if reviewer_note:
        provenance += f" Reviewer note: {reviewer_note}"
    example["metadata"]["notes"] = (existing_notes.rstrip() + " " + provenance).strip()

    return example


# ── Command: --list ───────────────────────────────────────────────────────────

def cmd_list(_args) -> int:
    manifest = load_manifest()
    entries  = manifest.get("entries", {})
    captures = scan_staging()

    if not captures:
        print("No plan captures found in staging directories:")
        for d in STAGING_DIRS:
            print(f"  {d}")
        print()
        print("Run a swarm to generate captures, or check that the staging dirs exist.")
        return 0

    col_file  = 44
    col_score = 5
    col_class = 9
    col_dec   = 9
    col_split = 9
    col_qual  = 9

    def row(fname, score, cls, dec, split, qual) -> str:
        fname_s = fname[:col_file - 1] + "~" if len(fname) > col_file else fname
        return (
            f"{fname_s:<{col_file}} {str(score):>{col_score}} {cls:<{col_class}}"
            f" {dec:<{col_dec}} {split:<{col_split}} {qual:<{col_qual}}"
        )

    header = row("FILE", "SCORE", "CLASS", "STATUS", "SPLIT", "QUALITY")
    print(header)
    print("-" * len(header))

    for path, class_hint in captures:
        capture = load_capture(path)
        if capture is None:
            print(f"  [unreadable] {path.name}")
            continue

        key     = capture_key(capture)
        entry   = entries.get(key, {}) if key else {}
        score   = capture.get("quality_score", "?")
        cls     = capture.get("example_class", class_hint)
        dec     = entry.get("decision", "pending")
        split   = entry.get("split") or "-"
        quality = entry.get("quality") or "-"

        print(row(path.name, score, cls, dec, split, quality))

    approved_count = sum(1 for e in entries.values() if e.get("decision") == "approved")
    pending_count  = sum(1 for _, _ in captures) - sum(
        1 for (path, _) in captures
        for key in [capture_key(load_capture(path) or {})]
        if key and entries.get(key, {}).get("decision") in ("approved", "rejected")
    )

    print()
    print(f"Total: {len(captures)} capture(s) in staging  |  "
          f"Approved: {approved_count}  |  Manifest: {_rel(MANIFEST_PATH)}")
    print("Run --status for Phase 3 gate counters.")
    return 0


# ── Command: --inspect ────────────────────────────────────────────────────────

def cmd_inspect(args) -> int:
    path = Path(args.inspect)
    if not path.is_absolute():
        path = REPO_ROOT / path
    if not path.exists():
        print(f"ERROR: File not found: {path}")
        return 1

    capture = load_capture(path)
    if capture is None:
        return 1

    manifest = load_manifest()
    key      = capture_key(capture)
    entry    = manifest.get("entries", {}).get(key) if key else None

    sep = "─" * 68

    print("=" * 70)
    print(f"FILE:        {_rel(path)}")
    print(f"EXAMPLE ID:  {capture.get('example_id', '(none)')}")
    print(f"RUN ID:      {capture.get('run_id', '(none)')}")
    print(f"SCORE:       {capture.get('quality_score', '(none)')} / 100")
    print(f"CLASS:       {capture.get('example_class', '(none)')}")
    goal_preview = (capture.get("goal") or "")[:120]
    print(f"GOAL:        {goal_preview}")
    print()

    print(f"── CAPTURE JSON {sep[:53]}")
    print(json.dumps(capture, indent=2, ensure_ascii=False))
    print()

    print(f"── MANIFEST STATUS {sep[:50]}")
    if entry:
        print(json.dumps(entry, indent=2, ensure_ascii=False))
    else:
        print("(not yet reviewed — status: pending)")
    print()

    print(f"── CONVERSION PREVIEW {sep[:47]}")
    example = capture_to_chat_jsonl(capture)
    if example is None:
        print("WARNING: capture_to_chat_jsonl returned None (missing goal or plan field)")
        print("         This capture cannot be exported. Check goal/plan fields.")
        print("=" * 70)
        return 0

    print(json.dumps(example, indent=2, ensure_ascii=False))
    print()

    print(f"── VALIDATION {sep[:55]}")
    fd, tmp_path = tempfile.mkstemp(suffix=".jsonl")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            f.write(json.dumps(example, ensure_ascii=False) + "\n")

        val_rc = validate_file(tmp_path)
        print()
        print(f"── SANITIZER {sep[:56]}")
        san_rc = sanitize_file(tmp_path)
        print()

        if val_rc == 0 and san_rc == 0:
            print("RESULT: passes validation and sanitizer — safe to approve.")
        elif val_rc != 0:
            print("RESULT: validation errors found. Fix before approving.")
        else:
            print("RESULT: sanitizer REJECT patterns found. Redact before approving.")
    finally:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass

    print("=" * 70)
    return 0


# ── Command: --approve ────────────────────────────────────────────────────────

def cmd_approve(args) -> int:
    path = Path(args.approve)
    if not path.is_absolute():
        path = REPO_ROOT / path
    if not path.exists():
        print(f"ERROR: File not found: {path}")
        return 1

    split   = args.split
    quality = args.quality
    note    = args.note or ""

    # Validate split
    if split not in VALID_SPLITS:
        print(f"ERROR: Invalid split '{split}'. Must be one of: {sorted(VALID_SPLITS)}")
        return 1

    # Validate quality for this split
    allowed_qualities = SPLIT_QUALITY_MAP[split]
    if quality not in allowed_qualities:
        print(
            f"ERROR: Quality '{quality}' is not allowed for split '{split}'. "
            f"Allowed: {sorted(allowed_qualities)}"
        )
        return 1

    capture = load_capture(path)
    if capture is None:
        return 1

    key = capture_key(capture)
    if not key:
        print("ERROR: Capture has no example_id field. Cannot add to manifest.")
        return 1

    # Quick sanity check: capture must be convertible
    if capture_to_chat_jsonl(capture) is None:
        print(
            "ERROR: This capture cannot be converted to chat-JSONL "
            "(missing goal or plan field). Fix the capture before approving."
        )
        return 1

    manifest = load_manifest()
    entries  = manifest.setdefault("entries", {})

    existing = entries.get(key)
    if existing and existing.get("decision") == "approved":
        print(
            f"WARNING: {key} is already approved "
            f"(split={existing.get('split')}, quality={existing.get('quality')}). "
            f"Overwriting."
        )

    entries[key] = {
        "file":          _rel(path),
        "example_id":    capture.get("example_id", key),
        "run_id":        capture.get("run_id", ""),
        "quality_score": capture.get("quality_score", 0),
        "example_class": capture.get("example_class", "unknown"),
        "decision":      "approved",
        "split":         split,
        "quality":       quality,
        "note":          note,
        "decided_at":    _now(),
        "decided_by":    "human",
    }

    save_manifest(manifest)

    print(f"Approved: {key}")
    print(f"  split={split}  quality={quality}" + (f"  note=\"{note}\"" if note else ""))
    print(f"  Manifest: {_rel(MANIFEST_PATH)}")
    print()
    print("NOTE: Approval is recorded in the manifest only.")
    print("      Run --export-train / --export-eval / --export-negative to write JSONL.")
    return 0


# ── Command: --reject ─────────────────────────────────────────────────────────

def cmd_reject(args) -> int:
    path = Path(args.reject)
    if not path.is_absolute():
        path = REPO_ROOT / path
    if not path.exists():
        print(f"ERROR: File not found: {path}")
        return 1

    note = args.note or ""

    capture = load_capture(path)
    if capture is None:
        return 1

    key = capture_key(capture)
    if not key:
        print("ERROR: Capture has no example_id field. Cannot add to manifest.")
        return 1

    manifest = load_manifest()
    entries  = manifest.setdefault("entries", {})

    entries[key] = {
        "file":          _rel(path),
        "example_id":    capture.get("example_id", key),
        "run_id":        capture.get("run_id", ""),
        "quality_score": capture.get("quality_score", 0),
        "example_class": capture.get("example_class", "unknown"),
        "decision":      "rejected",
        "split":         None,
        "quality":       None,
        "note":          note,
        "decided_at":    _now(),
        "decided_by":    "human",
    }

    save_manifest(manifest)

    print(f"Rejected: {key}" + (f"\n  note=\"{note}\"" if note else ""))
    print(f"  Manifest: {_rel(MANIFEST_PATH)}")
    return 0


# ── Command: --export-* ───────────────────────────────────────────────────────

def cmd_export(split: str) -> int:
    """
    Atomic export for a split.

    Steps:
      1. Collect all manifest-approved entries for this split.
      2. Convert each capture to chat-JSONL (with reviewer quality override).
      3. Write lines to a .tmp file next to the final output file.
      4. Run validate_dataset on .tmp  — abort (leaving final file unchanged) on error.
      5. Run sanitize_dataset on .tmp  — abort on REJECT patterns.
      6. Atomically replace the final file with .tmp.

    The final file is never touched on failure.
    """
    manifest = load_manifest()
    entries  = manifest.get("entries", {})

    approved = [
        entry for entry in entries.values()
        if entry.get("decision") == "approved" and entry.get("split") == split
    ]

    if not approved:
        print(f"No approved entries for split '{split}'.")
        print(f"Use --approve <file> --split {split} --quality <q> to approve captures first.")
        return 0

    final_path = OUTPUT_PATHS[split]
    tmp_path   = final_path.with_suffix(".tmp")
    final_path.parent.mkdir(parents=True, exist_ok=True)

    print(f"Exporting {len(approved)} approved '{split}' example(s) -> {_rel(final_path)}")
    print()

    lines:  list[str] = []
    failed: list[str] = []

    for entry in approved:
        file_path = REPO_ROOT / entry["file"]

        if not file_path.exists():
            print(f"  MISSING  {entry['file']}")
            failed.append(entry.get("example_id", entry["file"]))
            continue

        capture = load_capture(file_path)
        if capture is None:
            failed.append(entry.get("example_id", entry["file"]))
            continue

        example = _convert_for_export(capture, entry)
        if example is None:
            print(
                f"  SKIP     {entry['file']} "
                f"(conversion returned None — missing goal or plan field)"
            )
            failed.append(entry.get("example_id", entry["file"]))
            continue

        lines.append(json.dumps(example, ensure_ascii=False))
        print(f"  OK       {entry['file']} (quality={entry['quality']})")

    print()

    if failed:
        print(f"EXPORT ABORTED: {len(failed)} capture(s) could not be converted:")
        for eid in failed:
            print(f"  - {eid}")
        print("Fix the issues above and retry. Final file is unchanged.")
        return 1

    # ── Write temp file ───────────────────────────────────────────────────────
    try:
        with open(tmp_path, "w", encoding="utf-8") as f:
            for line in lines:
                f.write(line + "\n")
    except OSError as e:
        print(f"EXPORT ABORTED: Cannot write temp file {tmp_path}: {e}")
        return 1

    # ── Validation gate ───────────────────────────────────────────────────────
    print(f"-- Validating {tmp_path.name} --")
    val_rc = validate_file(str(tmp_path))
    print()

    if val_rc != 0:
        _cleanup_tmp(tmp_path)
        print("EXPORT ABORTED: validation failed. Final file is unchanged.")
        return 1

    # ── Sanitizer gate ────────────────────────────────────────────────────────
    print(f"-- Sanitizing {tmp_path.name} --")
    san_rc = sanitize_file(str(tmp_path))
    print()

    if san_rc != 0:
        _cleanup_tmp(tmp_path)
        print("EXPORT ABORTED: sanitizer found REJECT pattern(s). Final file is unchanged.")
        print("Review and redact the flagged captures, then retry.")
        return 1

    # ── Atomic replace ────────────────────────────────────────────────────────
    try:
        tmp_path.replace(final_path)
    except Exception as e:
        _cleanup_tmp(tmp_path)
        print(f"EXPORT ABORTED: Cannot replace final file: {e}")
        return 1

    print(f"OK Exported {len(lines)} example(s) -> {_rel(final_path)}")
    return 0


def _cleanup_tmp(tmp_path: Path) -> None:
    try:
        tmp_path.unlink()
    except OSError:
        pass


# ── Command: --status ─────────────────────────────────────────────────────────

def cmd_status(_args) -> int:
    manifest = load_manifest()
    entries  = manifest.get("entries", {})

    # Count decisions
    counts: dict[str, int] = {split: 0 for split in VALID_SPLITS}
    counts["pending"]  = 0
    counts["rejected_decision"] = 0

    for entry in entries.values():
        decision = entry.get("decision", "pending")
        if decision == "approved":
            split = entry.get("split")
            if split in counts:
                counts[split] += 1
        elif decision == "rejected":
            counts["rejected_decision"] += 1
        else:
            counts["pending"] += 1

    # Count unreviewed captures in staging
    staged_captures = scan_staging()
    reviewed_ids = {
        e.get("example_id")
        for e in entries.values()
        if e.get("decision") in ("approved", "rejected")
    }
    unreviewed = 0
    for path, _ in staged_captures:
        cap = load_capture(path)
        if cap is None:
            continue
        key = capture_key(cap)
        if key not in reviewed_ids:
            unreviewed += 1

    divider = "=" * 52

    print(divider)
    print("  PHASE 2.5 — DATASET REVIEW STATUS")
    print(divider)
    print()
    print(f"  Staging captures found:   {len(staged_captures)}")
    print(f"  Awaiting review:          {unreviewed}")
    print(f"  Reviewer-rejected:        {counts['rejected_decision']}")
    print()
    print("  -- Phase 3 Gate " + "-" * 34)
    print()

    all_met = True
    for split, threshold in PHASE3_GATES.items():
        count    = counts.get(split, 0)
        met      = count >= threshold
        bar      = _progress_bar(count, threshold)
        status   = "GATE MET" if met else "open"
        if not met:
            all_met = False
        print(f"  {split.upper():<10} {bar} {count:>4}/{threshold:<4}  {status}")

    print()
    if all_met:
        print("  ALL GATES MET — Phase 3 training is authorized.")
    else:
        missing = []
        for split, threshold in PHASE3_GATES.items():
            need = threshold - counts.get(split, 0)
            if need > 0:
                missing.append(f"{need} more {split} example(s)")
        print(f"  Phase 3 blocked. Need: {', '.join(missing)}.")

    print()
    print(f"  Manifest: {_rel(MANIFEST_PATH)}")
    print(divider)
    return 0


def _progress_bar(current: int, total: int, width: int = 20) -> str:
    fraction = min(current / total, 1.0) if total > 0 else 0.0
    filled   = round(fraction * width)
    return "[" + "#" * filled + "." * (width - filled) + "]"


# ── Main / argparse ───────────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(
        description="Dataset Review / Approval Valve for TheOrc Training Pit",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
examples:
  python training_pit/scripts/review_captures.py --list
  python training_pit/scripts/review_captures.py --status
  python training_pit/scripts/review_captures.py --inspect .orc/swarm/dataset-staging/plan_capture_good_20260601_085.json
  python training_pit/scripts/review_captures.py --approve .orc/swarm/dataset-staging/plan_capture_good_run001_085.json \\
      --split train --quality silver --note "Good decomposition, correct filenames"
  python training_pit/scripts/review_captures.py --reject .orc/swarm/dataset-staging/plan_capture_bad_run002_005.json \\
      --note "Collapse pattern — single empty task"
  python training_pit/scripts/review_captures.py --export-train
  python training_pit/scripts/review_captures.py --export-eval
  python training_pit/scripts/review_captures.py --export-negative
""",
    )

    parser.add_argument(
        "--list", action="store_true",
        help="List all captures in staging dirs with their manifest status",
    )
    parser.add_argument(
        "--inspect", metavar="PATH",
        help="Show full detail, conversion preview, and validation results for a capture",
    )
    parser.add_argument(
        "--approve", metavar="PATH",
        help="Approve a capture for a training split (requires --split and --quality)",
    )
    parser.add_argument(
        "--reject", metavar="PATH",
        help="Mark a capture as rejected in the manifest",
    )
    parser.add_argument(
        "--export-train", action="store_true",
        help="Atomic export: write/replace training_pit/datasets/train_v1.jsonl",
    )
    parser.add_argument(
        "--export-eval", action="store_true",
        help="Atomic export: write/replace training_pit/datasets/eval_v1.jsonl",
    )
    parser.add_argument(
        "--export-negative", action="store_true",
        help="Atomic export: write/replace training_pit/datasets/negative_v1.jsonl",
    )
    parser.add_argument(
        "--status", action="store_true",
        help="Show Phase 3 gate counters and unreviewed capture count",
    )

    # Options used with --approve
    parser.add_argument(
        "--split", choices=sorted(VALID_SPLITS),
        help="Target split for --approve (train | eval | negative)",
    )
    parser.add_argument(
        "--quality", choices=sorted(VALID_QUALITIES),
        help="Quality tier for --approve (gold | silver | draft | rejected)",
    )
    parser.add_argument(
        "--note", metavar="TEXT",
        help="Optional reviewer note for --approve or --reject",
    )

    args = parser.parse_args()

    # Route to command handler
    if args.list:
        return cmd_list(args)

    if args.inspect:
        return cmd_inspect(args)

    if args.approve:
        if not args.split:
            parser.error("--approve requires --split (train | eval | negative)")
        if not args.quality:
            parser.error("--approve requires --quality (gold | silver | draft | rejected)")
        return cmd_approve(args)

    if args.reject:
        return cmd_reject(args)

    if args.export_train:
        return cmd_export("train")

    if args.export_eval:
        return cmd_export("eval")

    if args.export_negative:
        return cmd_export("negative")

    if args.status:
        return cmd_status(args)

    parser.print_help()
    return 0


if __name__ == "__main__":
    sys.exit(main())
