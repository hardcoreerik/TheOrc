#!/usr/bin/env python3
"""suitability_gate.py — pre-training contamination check for boss datasets.

Scans train (and optionally eval) JSONL files for examples that would teach
the boss wrong behaviors, and for cross-set assistant-response hash leakage.

Contamination categories
  tester_poison   — plan assigns write/create work to a TESTER-lane role.
                    These examples are VALID tester-worker training data but
                    are WRONG for boss training. Route them to ORC ACADEMY v4.
  no_valid_json   — assistant message contains no parseable plan JSON at all.
  task_overflow   — plan has >4 tasks (out-of-spec for boss).
  hash_leak       — same assistant response appears in both train and eval
                    (checked only when --eval is supplied).

Exit codes
  0  PASS  — all checks within limits
  1  BLOCK — at least one check exceeded its limit

Usage
  python training_pit/scripts/suitability_gate.py training_pit/datasets/train_v2gold.jsonl
  python training_pit/scripts/suitability_gate.py \\
      --train training_pit/datasets/train_v2gold.jsonl \\
      --eval  training_pit/datasets/eval_v2gold.jsonl \\
      --poison-limit 0.15
  python training_pit/scripts/suitability_gate.py --json train.jsonl
"""
import argparse
import hashlib
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

# ── Role/lane resolution — must match eval_adapter._resolve_lane ──────────────

_WRITE_VERBS = re.compile(
    r"\b(create|write|implement|build|generate|author|compose)\b", re.I
)
_TESTER_ROLES   = {"TESTER", "QA", "QUALITY_ASSURANCE"}
_UI_ROLES       = {"UIDEVELOPER", "FRONTEND_DEVELOPER", "FRONTEND", "UI"}
_RESEARCH_ROLES = {"RESEARCHER", "ARCHITECT", "PLANNER", "REVIEWER", "ANALYST"}


def _resolve_lane(role: str) -> str:
    if role in _TESTER_ROLES:   return "TESTER"
    if role in _UI_ROLES:       return "UIDEVELOPER"
    if role in _RESEARCH_ROLES: return "RESEARCHER"
    return "CODER"


# ── Per-example classifier ────────────────────────────────────────────────────

def _get_assistant_content(messages: list) -> str:
    for msg in reversed(messages):
        if isinstance(msg, dict) and msg.get("role") == "assistant":
            return msg.get("content", "")
    return ""


def _sha256(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _classify(content: str) -> set[str]:
    """Return contamination flag(s) for one assistant plan string."""
    flags: set[str] = set()
    m = re.search(r"\{.*\}", content, re.S)
    if not m:
        flags.add("no_valid_json")
        return flags
    try:
        plan = json.loads(m.group(0))
    except json.JSONDecodeError:
        flags.add("no_valid_json")
        return flags

    tasks = plan.get("tasks") if isinstance(plan, dict) else None
    if not isinstance(tasks, list) or not tasks:
        flags.add("no_valid_json")
        return flags

    if len(tasks) > 4:
        flags.add("task_overflow")

    for t in tasks:
        if not isinstance(t, dict):
            continue
        role = str(t.get("role", "")).upper().strip()
        lane = _resolve_lane(role)
        blob = f"{t.get('title', '')} {t.get('description', '')}"
        if lane == "TESTER" and _WRITE_VERBS.search(blob):
            flags.add("tester_poison")
            break  # one poisoned task is enough to flag the example

    return flags


# ── File-level scanner ────────────────────────────────────────────────────────

@dataclass
class FileReport:
    file: str
    total: int
    tester_poison: int = 0
    no_valid_json: int = 0
    task_overflow: int = 0
    tester_poison_rate: float = 0.0
    passed: bool = True
    poisoned_lines: list[int] = field(default_factory=list)
    assistant_hashes: set[str] = field(default_factory=set)

    def as_dict(self, include_hashes: bool = False) -> dict:
        d = {
            "file": self.file,
            "total": self.total,
            "tester_poison": self.tester_poison,
            "no_valid_json": self.no_valid_json,
            "task_overflow": self.task_overflow,
            "tester_poison_rate": self.tester_poison_rate,
            "passed": self.passed,
            "poisoned_lines": self.poisoned_lines[:20],
        }
        return d


def check_file(
    path: Path,
    poison_limit: float = 0.25,
    invalid_limit: float = 0.10,
    overflow_limit: float = 0.05,
) -> FileReport:
    """
    Scan a train JSONL and classify each example.

    Parameters
    ----------
    path           : JSONL file to scan
    poison_limit   : max allowed fraction of tester_poison examples (default 0.25)
    invalid_limit  : max allowed fraction of no_valid_json examples (default 0.10)
    overflow_limit : max allowed fraction of task_overflow (>4 tasks) examples (default 0.05)

    Returns a FileReport; report.passed is False if any limit is exceeded.
    """
    lines = [l for l in path.read_text(encoding="utf-8").splitlines() if l.strip()]
    n = len(lines)
    report = FileReport(file=str(path), total=n)

    if n == 0:
        return report

    for i, raw in enumerate(lines, 1):
        try:
            row = json.loads(raw)
        except json.JSONDecodeError:
            report.no_valid_json += 1
            continue

        content = _get_assistant_content(row.get("messages", []))
        report.assistant_hashes.add(_sha256(content))
        flags = _classify(content)

        if "tester_poison" in flags:
            report.tester_poison += 1
            if len(report.poisoned_lines) < 50:
                report.poisoned_lines.append(i)
        if "no_valid_json" in flags:
            report.no_valid_json += 1
        if "task_overflow" in flags:
            report.task_overflow += 1

    report.tester_poison_rate = report.tester_poison / n
    report.passed = (
        report.tester_poison_rate <= poison_limit
        and (report.no_valid_json / n) <= invalid_limit
        and (report.task_overflow / n) <= overflow_limit
    )
    return report


# ── Cross-set leakage check ───────────────────────────────────────────────────

@dataclass
class LeakageReport:
    train_file: str
    eval_file: str
    overlapping_responses: int
    passed: bool

    def as_dict(self) -> dict:
        return {
            "train_file": self.train_file,
            "eval_file": self.eval_file,
            "overlapping_responses": self.overlapping_responses,
            "passed": self.passed,
        }


def check_leakage(train_report: FileReport, eval_report: FileReport) -> LeakageReport:
    """Check whether the same assistant response appears in both train and eval."""
    overlap = len(train_report.assistant_hashes & eval_report.assistant_hashes)
    return LeakageReport(
        train_file=train_report.file,
        eval_file=eval_report.file,
        overlapping_responses=overlap,
        passed=(overlap == 0),
    )


# ── CLI ───────────────────────────────────────────────────────────────────────

def _fmt_rate(n: int, total: int) -> str:
    pct = 100 * n / total if total else 0
    return f"{n:4d} / {total}  ({pct:.1f}%)"


def _print_report(
    train_report: FileReport,
    eval_report: FileReport | None,
    leakage: LeakageReport | None,
) -> None:
    LINE = "=" * 56
    print(LINE)
    print("  Suitability Gate")
    print(LINE)

    def _section(r: FileReport, label: str) -> None:
        ok = "PASS" if r.passed else "BLOCK"
        print(f"\n[{ok}] {label}: {r.file}")
        print(f"  Total examples    : {r.total}")
        print(f"  tester_poison     : {_fmt_rate(r.tester_poison, r.total)}", end="")
        if r.tester_poison > 0:
            print(f"  ← route to TESTER worker training (ORC ACADEMY v4)", end="")
        print()
        print(f"  no_valid_json     : {_fmt_rate(r.no_valid_json, r.total)}")
        print(f"  task_overflow     : {_fmt_rate(r.task_overflow, r.total)}")
        if r.poisoned_lines:
            shown = r.poisoned_lines[:10]
            more  = len(r.poisoned_lines) - len(shown)
            tail  = f" … +{more} more" if more > 0 else ""
            print(f"  poisoned at lines : {shown}{tail}")

    _section(train_report, "TRAIN")
    if eval_report is not None:
        _section(eval_report, "EVAL")

    if leakage is not None:
        ok = "PASS" if leakage.passed else "BLOCK"
        print(f"\n[{ok}] Hash leakage (train↔eval assistant responses)")
        print(f"  Overlapping responses: {leakage.overlapping_responses}")
        if not leakage.passed:
            print("  Remove duplicates before training — eval results will be inflated.")

    all_pass = train_report.passed
    if eval_report:  all_pass = all_pass and eval_report.passed
    if leakage:      all_pass = all_pass and leakage.passed

    print()
    print(LINE)
    print(f"  Result: {'PASS — proceed to training' if all_pass else 'BLOCK — fix contamination first'}")
    print(LINE)


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Pre-training suitability check for boss datasets.",
    )
    ap.add_argument("train", nargs="?", help="Train JSONL to scan (positional shorthand)")
    ap.add_argument("--train", dest="train_flag", metavar="PATH",
                    help="Train JSONL (overrides positional)")
    ap.add_argument("--eval", metavar="PATH",
                    help="Eval JSONL — enables cross-set leakage check")
    ap.add_argument("--poison-limit", type=float, default=0.25, metavar="FRAC",
                    help="Max tester_poison fraction before BLOCK (default 0.25)")
    ap.add_argument("--invalid-limit", type=float, default=0.10, metavar="FRAC",
                    help="Max no_valid_json fraction before BLOCK (default 0.10)")
    ap.add_argument("--json", action="store_true",
                    help="Output machine-readable JSON")
    args = ap.parse_args()

    train_path_str = args.train_flag or args.train
    if not train_path_str:
        ap.error("Provide a train JSONL path (positional or --train).")

    train_path = Path(train_path_str)
    if not train_path.exists():
        print(f"ERROR: train file not found: {train_path}", file=sys.stderr)
        return 2

    train_report = check_file(train_path, args.poison_limit, args.invalid_limit)

    eval_report: FileReport | None = None
    leakage: LeakageReport | None = None

    if args.eval:
        eval_path = Path(args.eval)
        if not eval_path.exists():
            print(f"ERROR: eval file not found: {eval_path}", file=sys.stderr)
            return 2
        eval_report = check_file(eval_path, args.poison_limit, args.invalid_limit)
        leakage = check_leakage(train_report, eval_report)

    all_pass = train_report.passed
    if eval_report: all_pass = all_pass and eval_report.passed
    if leakage:     all_pass = all_pass and leakage.passed

    if args.json:
        out: dict = {"train": train_report.as_dict(), "passed": all_pass}
        if eval_report: out["eval"] = eval_report.as_dict()
        if leakage:     out["leakage"] = leakage.as_dict()
        print(json.dumps(out, indent=2))
    else:
        _print_report(train_report, eval_report, leakage)

    return 0 if all_pass else 1


if __name__ == "__main__":
    sys.exit(main())
