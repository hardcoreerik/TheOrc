#!/usr/bin/env python3
"""Deterministic, standard-library checks for OrcEngine documentation drift.

Methodology ported from OrcSDR's `tools/check_documentation_truth.py`:
  * a fixed allow-list of CURRENT documents, separate from append-only
    historical evidence (DECISION_LOG.md, older dated sections);
  * every check compares a document claim against a machine-checkable
    source of truth (git tags, files on disk, the decision log, commit
    dates) -- never against prose;
  * ERROR fails CI, WARNING annotates only;
  * every check has unit tests with a passing and a failing fixture
    (`Tools/test_check_orcengine_docs_truth.py`).

Authority: `docs/OrcEngine/CURRENT_STATE.yaml` is the machine-readable
current-state file. This checker never rewrites documents; it only reports.
"""

from __future__ import annotations

import argparse
import dataclasses
import datetime as dt
import json
import os
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote

DOCS = "docs/OrcEngine"
STATE = f"{DOCS}/CURRENT_STATE.yaml"
DECISION_LOG = f"{DOCS}/DECISION_LOG.md"

# Documents that must describe the CURRENT state. PROJECT_TRUTH.md and
# DECISION_LOG.md are append-only evidence: dated sections are historical by
# design, so they are only checked for the header snapshot date and ADR
# integrity, never for stale-phrase or path drift.
CURRENT_DOCS = (f"{DOCS}/README.md", f"{DOCS}/ENGINEERING_ROADMAP.md", STATE)
CODE_GLOB_PREFIXES = ("Tools/OrcEngine",)

# Claims that were true once and are now known-obsolete. If one resurfaces
# in a current document it is an ERROR.
RESOLVED_CLAIMS = (
    (r"phase_5c_not_yet_started", "Phase 5C is frozen (orcengine-phase5c-freeze)"),
    (r"zero (?:C\+\+ )?engine code", "C++ engine code exists (Phases 1-5C frozen)"),
    (r"Phase 3 frozen; Phase 4 implemented and self-reviewed", "Phases 1-5C are frozen"),
    (r"FL-08 remains not started", "FL-08 is frozen (orcengine-fl08-freeze)"),
    (r"Phase 1 \([^)]*\) is the next unblocked phase", "Phase 1 is complete and frozen"),
)
PROMPT_PATTERNS = (
    re.compile(r"\byour job is to\b", re.I),
    re.compile(r"\byou are (?:codex|claude)\b", re.I),
    re.compile(r"<instructions?>", re.I),
)
WARN_LAG_DAYS = 14
ERROR_LAG_DAYS = 45
SNAPSHOT_SYNC_DAYS = 7


@dataclasses.dataclass(frozen=True)
class Diagnostic:
    level: str
    code: str
    path: str
    line: int
    message: str


@dataclasses.dataclass
class Report:
    errors: list[Diagnostic] = dataclasses.field(default_factory=list)
    warnings: list[Diagnostic] = dataclasses.field(default_factory=list)
    passes: list[str] = dataclasses.field(default_factory=list)

    def add(self, level: str, code: str, path: str, line: int, message: str) -> None:
        item = Diagnostic(level, code, path, line, message)
        (self.errors if level == "ERROR" else self.warnings).append(item)

    def has(self, code: str) -> bool:
        return any(item.code == code for item in self.errors + self.warnings)


def _text(root: Path, relative: str) -> str | None:
    path = root / relative
    return path.read_text(encoding="utf-8") if path.is_file() else None


def _line(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def _git(root: Path, *args: str) -> str | None:
    try:
        done = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.SubprocessError):
        return None
    return done.stdout.strip() if done.returncode == 0 else None


def _state_keys(state: str) -> dict[str, tuple[str, int]]:
    """Flat `key: value` scalars from CURRENT_STATE.yaml (stdlib only, no YAML dep)."""
    found: dict[str, tuple[str, int]] = {}
    for number, raw in enumerate(state.splitlines(), 1):
        match = re.match(r"^\s{0,4}([A-Za-z0-9_]+):\s*(\S.*?)\s*$", raw)
        if match and not raw.lstrip().startswith("#"):
            found.setdefault(match.group(1), (match.group(2).strip("\"'"), number))
    return found


def _as_of(state: str) -> dt.date | None:
    match = re.search(r"^as_of:\s*\n(?:\s+#.*\n|\s+\w[^\n]*\n)*?\s+date:\s*[\"']?(\d{4}-\d{2}-\d{2})", state, re.M)
    return dt.date.fromisoformat(match.group(1)) if match else None


def _current_markdown(root: Path) -> list[Path]:
    return [root / item for item in CURRENT_DOCS if item.endswith(".md") and (root / item).is_file()]


def _check_state_file(root: Path, report: Report) -> dt.date | None:
    state = _text(root, STATE)
    if state is None:
        report.add("ERROR", "state-file", STATE, 1, "CURRENT_STATE.yaml is missing")
        return None
    as_of = _as_of(state)
    if as_of is None:
        report.add("ERROR", "state-file", STATE, 1, "as_of.date not found")
    if "lifecycle:" not in state:
        report.add("ERROR", "state-file", STATE, 1, "status.lifecycle not found")
    if not report.has("state-file"):
        report.passes.append(f"CURRENT_STATE.yaml present (as_of {as_of})")
    return as_of


def _check_freeze_tags(root: Path, report: Report) -> None:
    state = _text(root, STATE)
    if state is None:
        return
    keys = _state_keys(state)
    tags = {k: v for k, v in keys.items() if re.fullmatch(r"(?:phase\w+|fl\d+)_freeze_tag", k)}
    if not tags:
        return
    listing = _git(root, "tag", "--list")
    if listing is None:
        report.add("WARNING", "freeze-tag", STATE, 1, "not a git checkout; freeze tags not verified")
        return
    existing = set(listing.split())
    verified = 0
    for key, (value, line) in tags.items():
        if value in ("not_created", "none", "null"):
            continue
        if value not in existing:
            level = "WARNING" if not existing else "ERROR"
            report.add(level, "freeze-tag", STATE, line, f"{key}={value} is not a git tag in this checkout"
                       + ("" if existing else " (no tags fetched; use fetch-tags)"))
            continue
        commit_key = key.replace("_tag", "_commit")
        if commit_key in keys:
            documented, cline = keys[commit_key]
            peeled = _git(root, "rev-parse", f"{value}^{{commit}}")
            if peeled and peeled != documented:
                report.add("ERROR", "freeze-tag", STATE, cline, f"{commit_key}={documented} but {value} peels to {peeled}")
                continue
        verified += 1
    if not report.has("freeze-tag"):
        report.passes.append(f"{verified} documented freeze tags exist and peel to the documented commits")


def _check_spec_references(root: Path, report: Report) -> None:
    state = _text(root, STATE)
    if state is None:
        return
    for key, (value, line) in _state_keys(state).items():
        if re.fullmatch(r"phase\w+_spec", key) and value.endswith(".md"):
            if not (root / DOCS / value).is_file():
                report.add("ERROR", "spec-reference", STATE, line, f"{key} points to missing {DOCS}/{value}")
    if not report.has("spec-reference"):
        report.passes.append("Phase spec files named in CURRENT_STATE.yaml exist")


def _check_adr_integrity(root: Path, report: Report) -> None:
    log = _text(root, DECISION_LOG)
    if log is None:
        report.add("ERROR", "adr", DECISION_LOG, 1, "DECISION_LOG.md is missing")
        return
    defined: dict[int, int] = {}
    for match in re.finditer(r"^##\s+OE-ADR-(\d{3})\b", log, re.M):
        number = int(match.group(1))
        if number in defined:
            report.add("ERROR", "adr", DECISION_LOG, _line(log, match.start()), f"OE-ADR-{number:03d} defined twice")
        defined[number] = _line(log, match.start())
    highest = max(defined, default=0)
    for relative in CURRENT_DOCS + (f"{DOCS}/PROJECT_TRUTH.md",):
        text = _text(root, relative)
        if text is None:
            continue
        for match in re.finditer(r"OE[-_]ADR[-_](\d{3})\b", text):
            number = int(match.group(1))
            if number > highest:
                report.add("ERROR", "adr", relative, _line(text, match.start()),
                           f"references OE-ADR-{number:03d} but the newest logged ADR is {highest:03d}")
    if not report.has("adr"):
        report.passes.append(f"ADR log consistent ({len(defined)} entries, newest OE-ADR-{highest:03d}, no dangling references)")


def _check_snapshot_sync(root: Path, as_of: dt.date | None, report: Report) -> None:
    truth = _text(root, f"{DOCS}/PROJECT_TRUTH.md")
    if truth is None or as_of is None:
        return
    match = re.search(r"Snapshot date:\s*(\d{4}-\d{2}-\d{2})", truth[:2000])
    if not match:
        report.add("ERROR", "snapshot-sync", f"{DOCS}/PROJECT_TRUTH.md", 1, "header 'Snapshot date:' not found")
        return
    snapshot = dt.date.fromisoformat(match.group(1))
    gap = abs((as_of - snapshot).days)
    if gap > SNAPSHOT_SYNC_DAYS:
        report.add("ERROR", "snapshot-sync", f"{DOCS}/PROJECT_TRUTH.md", 1,
                   f"snapshot {snapshot} differs from CURRENT_STATE as_of {as_of} by {gap} days (> {SNAPSHOT_SYNC_DAYS})")
    else:
        report.passes.append(f"PROJECT_TRUTH snapshot ({snapshot}) matches CURRENT_STATE as_of ({as_of})")


def _check_code_lag(root: Path, as_of: dt.date | None, report: Report) -> None:
    if as_of is None:
        return
    stamp = _git(root, "log", "-1", "--format=%cs", "--", *[f"{p}*" for p in CODE_GLOB_PREFIXES])
    if not stamp:
        report.add("WARNING", "code-lag", STATE, 1, "no OrcEngine code history available; staleness vs code not checked")
        return
    newest = dt.date.fromisoformat(stamp)
    lag = (newest - as_of).days
    detail = f"newest OrcEngine code commit {newest} is {lag} days after CURRENT_STATE as_of {as_of}"
    if lag > ERROR_LAG_DAYS:
        report.add("ERROR", "code-lag", STATE, 1, detail + f" (> {ERROR_LAG_DAYS}); update the state docs")
    elif lag > WARN_LAG_DAYS:
        report.add("WARNING", "code-lag", STATE, 1, detail + f" (> {WARN_LAG_DAYS})")
    else:
        report.passes.append(f"State docs current with code ({detail})")


def _check_links(root: Path, report: Report) -> None:
    link_re = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
    for path in _current_markdown(root):
        text = path.read_text(encoding="utf-8")
        for match in link_re.finditer(text):
            raw = match.group(1).strip().split()[0].strip("<>")
            if not raw or raw.startswith(("#", "http://", "https://", "mailto:")):
                continue
            target = unquote(raw.split("#", 1)[0].split("?", 1)[0])
            resolved = (root / target.lstrip("/")) if target.startswith("/") else (path.parent / target)
            if not resolved.exists():
                report.add("ERROR", "local-link", path.relative_to(root).as_posix(), _line(text, match.start()),
                           f"local link target does not exist: {raw}")
    if not report.has("local-link"):
        report.passes.append("Markdown links in current docs resolve")


def _check_file_references(root: Path, report: Report) -> None:
    pattern = re.compile(r"`((?:Tools|docs|tools|\.github)/[^`*{}<>\s]+\.[A-Za-z0-9]+)`")
    for path in _current_markdown(root):
        text = path.read_text(encoding="utf-8")
        for match in pattern.finditer(text):
            if not (root / match.group(1)).exists():
                report.add("ERROR", "file-reference", path.relative_to(root).as_posix(), _line(text, match.start()),
                           f"referenced repository file does not exist: {match.group(1)}")
    if not report.has("file-reference"):
        report.passes.append("Backticked repository file references in current docs resolve")


def _check_resolved_claims(root: Path, report: Report) -> None:
    for relative in CURRENT_DOCS:
        text = _text(root, relative)
        if text is None:
            continue
        for pattern, truth in RESOLVED_CLAIMS:
            match = re.search(pattern, text, re.I)
            if match:
                report.add("ERROR", "resolved-claim", relative, _line(text, match.start()),
                           f"known-obsolete claim resurfaced (truth: {truth})")
    if not report.has("resolved-claim"):
        report.passes.append("Known-obsolete claims are absent from current docs")


def _check_prompt_residue(root: Path, report: Report) -> None:
    for path in _current_markdown(root):
        text = path.read_text(encoding="utf-8")
        for pattern in PROMPT_PATTERNS:
            match = pattern.search(text)
            if match:
                report.add("WARNING", "prompt-residue", path.relative_to(root).as_posix(), _line(text, match.start()),
                           f"possible prompt residue: {match.group(0)}")


def run_checks(root: Path) -> Report:
    root = root.resolve()
    report = Report()
    as_of = _check_state_file(root, report)
    _check_freeze_tags(root, report)
    _check_spec_references(root, report)
    _check_adr_integrity(root, report)
    _check_snapshot_sync(root, as_of, report)
    _check_code_lag(root, as_of, report)
    _check_links(root, report)
    _check_file_references(root, report)
    _check_resolved_claims(root, report)
    _check_prompt_residue(root, report)
    return report


def _render(report: Report) -> str:
    lines = ["OrcEngine Documentation Truth Check", "==================================="]
    lines.extend(f"PASS  {message}" for message in report.passes)
    lines.extend(f"WARN  {i.path}:{i.line}: {i.message}" for i in report.warnings)
    lines.extend(f"ERROR {i.path}:{i.line}: {i.message}" for i in report.errors)
    lines.extend(("", f"{len(report.errors)} errors", f"{len(report.warnings)} warnings"))
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    report = run_checks(args.root)
    rendered = _render(report)
    print(json.dumps(dataclasses.asdict(report), indent=2) if args.json else rendered)
    if os.environ.get("GITHUB_ACTIONS") == "true":
        for item in report.errors + report.warnings:
            level = "error" if item.level == "ERROR" else "warning"
            print(f"::{level} file={item.path},line={item.line}::{item.message}")
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as handle:
                handle.write("```text\n" + rendered + "\n```\n")
    return 1 if report.errors else 0


if __name__ == "__main__":
    sys.exit(main())
