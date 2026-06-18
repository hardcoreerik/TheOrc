#!/usr/bin/env python3
"""split_v2gold.py — route v2gold examples into boss-v3 and tester-v1 buckets.

Reads train_v2gold.jsonl + eval_v2gold.jsonl, classifies each example with the
suitability_gate logic, then writes four output files:

  BOSS (ORC ACADEMY v3 — clean boss training)
    datasets/train_v3gold.jsonl
    datasets/eval_v3gold.jsonl

  TESTER (ORC ACADEMY v4 seed — tester worker training)
    datasets/train_tester_v1.jsonl
    datasets/eval_tester_v1.jsonl

Examples with no_valid_json are quarantined to a separate log and excluded from
both buckets (they cannot teach either model anything useful).

Usage
  python training_pit/scripts/split_v2gold.py
  python training_pit/scripts/split_v2gold.py --dry-run   # counts only, no files written
  python training_pit/scripts/split_v2gold.py --out-dir /tmp/test
"""
import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

# ── Role/lane resolution — mirrors suitability_gate and eval_adapter ──────────

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


def _get_assistant_content(messages: list) -> str:
    for msg in reversed(messages):
        if isinstance(msg, dict) and msg.get("role") == "assistant":
            return msg.get("content", "")
    return ""


def _classify(content: str) -> str:
    """Return 'boss_clean', 'tester_poison', or 'invalid'."""
    m = re.search(r"\{.*\}", content, re.S)
    if not m:
        return "invalid"
    try:
        plan = json.loads(m.group(0))
    except json.JSONDecodeError:
        return "invalid"

    tasks = plan.get("tasks") if isinstance(plan, dict) else None
    if not isinstance(tasks, list) or not tasks:
        return "invalid"

    if len(tasks) > 4:
        return "invalid"  # out-of-spec plan; useless for boss training

    for t in tasks:
        if not isinstance(t, dict):
            continue
        role = str(t.get("role", "")).upper().strip()
        lane = _resolve_lane(role)
        blob = f"{t.get('title', '')} {t.get('description', '')}"
        if lane == "TESTER" and _WRITE_VERBS.search(blob):
            return "tester_poison"

    return "boss_clean"


# ── Per-split routing ─────────────────────────────────────────────────────────

@dataclass
class SplitResult:
    source: str
    total: int = 0
    boss_clean: int = 0
    tester_poison: int = 0
    invalid: int = 0
    invalid_lines: list[int] = field(default_factory=list)


def _route_file(src: Path, boss_out: Path, tester_out: Path,
                invalid_log: Path, dry_run: bool) -> SplitResult:
    result = SplitResult(source=str(src))

    lines = [l for l in src.read_text(encoding="utf-8").splitlines() if l.strip()]
    result.total = len(lines)

    boss_rows:   list[str] = []
    tester_rows: list[str] = []
    invalid_rows: list[dict] = []

    for i, raw in enumerate(lines, 1):
        try:
            row = json.loads(raw)
        except json.JSONDecodeError:
            result.invalid += 1
            result.invalid_lines.append(i)
            invalid_rows.append({"line": i, "raw": raw[:120], "reason": "json_parse_error"})
            continue

        content  = _get_assistant_content(row.get("messages", []))
        category = _classify(content)

        if category == "boss_clean":
            result.boss_clean += 1
            boss_rows.append(raw)
        elif category == "tester_poison":
            result.tester_poison += 1
            tester_rows.append(raw)
        else:
            result.invalid += 1
            result.invalid_lines.append(i)
            invalid_rows.append({"line": i, "reason": "no_valid_json"})

    if not dry_run:
        boss_out.write_text("\n".join(boss_rows) + ("\n" if boss_rows else ""),
                            encoding="utf-8")
        tester_out.write_text("\n".join(tester_rows) + ("\n" if tester_rows else ""),
                              encoding="utf-8")
        if invalid_rows:
            invalid_log.write_text(
                json.dumps(invalid_rows, indent=2), encoding="utf-8"
            )

    return result


# ── Summary output ────────────────────────────────────────────────────────────

_BOSS_MIN_TRAIN  = 150
_BOSS_MIN_EVAL   = 20
_TESTER_MIN_TRAIN = 100


def _bar(n: int, total: int, width: int = 14) -> str:
    filled = min(width, int(width * n / total)) if total else 0
    return "[" + "#" * filled + "." * (width - filled) + "]"


def _print_summary(
    train: SplitResult,
    eval_: SplitResult,
    out_dir: Path,
    dry_run: bool,
) -> bool:
    LINE = "=" * 62
    print(LINE)
    print("  v2gold → v3 boss + v4 tester split")
    print(LINE)

    def _row(label: str, r: SplitResult) -> None:
        print(f"\n  {label}  ({r.source})")
        print(f"    Total         : {r.total}")
        bar_b = _bar(r.boss_clean, r.total)
        bar_t = _bar(r.tester_poison, r.total)
        print(f"    boss_clean    : {r.boss_clean:4d}  {bar_b}  "
              f"({100*r.boss_clean/r.total:.1f}%  →  {'train' if 'train' in r.source else 'eval'}_v3gold)")
        print(f"    tester_poison : {r.tester_poison:4d}  {bar_t}  "
              f"({100*r.tester_poison/r.total:.1f}%  →  {'train' if 'train' in r.source else 'eval'}_tester_v1)")
        if r.invalid:
            print(f"    invalid       : {r.invalid:4d}  (quarantined, excluded from both)")

    _row("TRAIN", train)
    _row("EVAL ", eval_)

    print()
    print(LINE)

    # Gate status
    boss_train_ok   = train.boss_clean  >= _BOSS_MIN_TRAIN
    boss_eval_ok    = eval_.boss_clean  >= _BOSS_MIN_EVAL
    tester_train_ok = train.tester_poison >= _TESTER_MIN_TRAIN

    def _gate(label: str, n: int, minimum: int) -> str:
        ok = n >= minimum
        return f"  {'✓' if ok else '✗'} {label:35s}  {n:4d} / {minimum}  {'OK' if ok else 'SHORT'}"

    print("  Gate check:")
    print(_gate("boss    train_v3gold ≥ 150",      train.boss_clean,    _BOSS_MIN_TRAIN))
    print(_gate("boss    eval_v3gold  ≥ 20",        eval_.boss_clean,    _BOSS_MIN_EVAL))
    print(_gate("tester  train_tester_v1 ≥ 100",   train.tester_poison, _TESTER_MIN_TRAIN))
    print()

    boss_ready   = boss_train_ok and boss_eval_ok
    tester_ready = tester_train_ok

    if not dry_run:
        print("  Output files written to:", out_dir)
        print(f"    train_v3gold.jsonl      ({train.boss_clean} examples)")
        print(f"    eval_v3gold.jsonl       ({eval_.boss_clean} examples)")
        print(f"    train_tester_v1.jsonl   ({train.tester_poison} examples)")
        print(f"    eval_tester_v1.jsonl    ({eval_.tester_poison} examples)")
        if train.invalid or eval_.invalid:
            print(f"    split_invalid.json      ({train.invalid + eval_.invalid} quarantined)")
    else:
        print("  [DRY RUN] No files written.")

    print()
    print(LINE)
    if boss_ready:
        print("  ORC ACADEMY v3 (Boss): UNBLOCKED — run train_lora.py with train_v3gold")
    else:
        short = _BOSS_MIN_TRAIN - train.boss_clean
        print(f"  ORC ACADEMY v3 (Boss): BLOCKED — need {short} more clean train examples")
        print("     Generate more via Cerebras (generate_cerebras_gold.py)")

    if tester_ready:
        print("  ORC ACADEMY v4 (Tester): seed dataset READY — start data review pass")
    else:
        short = _TESTER_MIN_TRAIN - train.tester_poison
        print(f"  ORC ACADEMY v4 (Tester): need {short} more tester examples to reach gate")

    print(LINE)
    return boss_ready


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> int:
    DATASETS = Path(__file__).resolve().parent.parent / "datasets"

    ap = argparse.ArgumentParser(
        description="Split v2gold datasets into boss-v3 and tester-v1 buckets."
    )
    ap.add_argument("--train-in",  default=str(DATASETS / "train_v2gold.jsonl"),
                    help="Source train JSONL (default: train_v2gold.jsonl)")
    ap.add_argument("--eval-in",   default=str(DATASETS / "eval_v2gold.jsonl"),
                    help="Source eval JSONL  (default: eval_v2gold.jsonl)")
    ap.add_argument("--out-dir",   default=str(DATASETS),
                    help="Directory for output JSONL files (default: datasets/)")
    ap.add_argument("--dry-run", action="store_true",
                    help="Print counts only; write no files")
    args = ap.parse_args()

    train_src = Path(args.train_in)
    eval_src  = Path(args.eval_in)
    out_dir   = Path(args.out_dir)

    for p in (train_src, eval_src):
        if not p.exists():
            print(f"ERROR: not found: {p}", file=sys.stderr)
            return 2

    if not args.dry_run:
        out_dir.mkdir(parents=True, exist_ok=True)

    train_result = _route_file(
        src         = train_src,
        boss_out    = out_dir / "train_v3gold.jsonl",
        tester_out  = out_dir / "train_tester_v1.jsonl",
        invalid_log = out_dir / "split_invalid.json",
        dry_run     = args.dry_run,
    )
    eval_result = _route_file(
        src         = eval_src,
        boss_out    = out_dir / "eval_v3gold.jsonl",
        tester_out  = out_dir / "eval_tester_v1.jsonl",
        invalid_log = out_dir / "split_invalid.json",
        dry_run     = args.dry_run,
    )

    boss_ready = _print_summary(train_result, eval_result, out_dir, args.dry_run)
    return 0 if boss_ready else 1


if __name__ == "__main__":
    sys.exit(main())
