#!/usr/bin/env python3
"""Dataset health dashboard — shows counts, coverage, and gaps.

Works on both the main machine and HARDCOREPC (no GPU needed).

Usage:
  python 07_dataset_stats.py
  python 07_dataset_stats.py --workspace \\HARDCORERIK\F$\Ai\OrchestratorIDE
"""
import argparse, json, re, sys
from collections import Counter
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}


def count_manifest(manifest_path):
    if not manifest_path.exists():
        return {"approved": 0, "rejected": 0, "train": 0, "eval": 0, "negative": 0}
    data = json.loads(manifest_path.read_text(encoding="utf-8"))
    entries = data if isinstance(data, list) else data.get("entries", [])
    counts = Counter()
    for e in entries:
        decision = e.get("decision", "")
        split    = e.get("split", "")
        counts[decision] += 1
        if decision == "approved":
            counts[split] += 1
    return dict(counts)


def scan_staging(staging_dir):
    if not staging_dir.exists():
        return {"good": 0, "bad": 0, "total": 0}
    good = len(list(staging_dir.glob("plan_capture_good_*.json")))
    bad  = len(list(staging_dir.glob("plan_capture_bad_*.json")))
    return {"good": good, "bad": bad, "total": good + bad}


def scan_jsonl(jsonl_path):
    if not jsonl_path.exists():
        return {"count": 0, "roles": {}, "avg_tasks": 0}
    rows = [json.loads(l) for l in jsonl_path.read_text(encoding="utf-8").splitlines() if l.strip()]
    role_counts = Counter()
    task_totals = []
    for row in rows:
        msgs = row.get("messages", [])
        asst = next((m["content"] for m in msgs if m["role"] == "assistant"), "")
        m = re.search(r"\{.*\}", asst, re.S)
        if m:
            try:
                plan = json.loads(m.group(0))
                tasks = plan.get("tasks", [])
                task_totals.append(len(tasks))
                for t in tasks:
                    if isinstance(t, dict):
                        role_counts[t.get("role", "?").upper()] += 1
            except Exception:
                pass
    return {
        "count": len(rows),
        "roles": dict(role_counts),
        "avg_tasks": round(sum(task_totals) / len(task_totals), 1) if task_totals else 0,
    }


def scan_goals(pit_dir):
    batches = list(pit_dir.glob("batch_*_goals.psv"))
    total_goals = 0
    for f in batches:
        lines = [l for l in f.read_text(encoding="utf-8").splitlines() if l.strip()]
        total_goals += len(lines)
    return {"batch_files": len(batches), "total_goals": total_goals}


def bar(n, total, width=30):
    if total == 0:
        return "[" + " " * width + "]"
    filled = int(width * n / total)
    return "[" + "#" * filled + "-" * (width - filled) + "]"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workspace", default="F:\\Ai\\OrchestratorIDE")
    args = ap.parse_args()

    ws       = Path(args.workspace)
    pit      = ws / "training_pit"
    staging  = ws / ".orc" / "swarm" / "dataset-staging"
    manifest = pit / "datasets" / "manifests" / "reviewed_v1.json"
    train_f  = pit / "datasets" / "train_v1.jsonl"
    eval_f   = pit / "datasets" / "eval_v1.jsonl"

    manifest_counts = count_manifest(manifest)
    staging_counts  = scan_staging(staging)
    goals           = scan_goals(pit)
    train_stats     = scan_jsonl(train_f)
    eval_stats      = scan_jsonl(eval_f)

    approved = manifest_counts.get("approved", 0)
    rejected = manifest_counts.get("rejected", 0)
    train    = manifest_counts.get("train", 0)
    eval_n   = manifest_counts.get("eval", 0)

    TRAIN_TARGET = 1000
    EVAL_TARGET  = 200

    print()
    print("=" * 55)
    print("  ORC ACADEMY Dataset Health Dashboard")
    print("=" * 55)
    print()
    print("  STAGING (awaiting review)")
    print(f"    Good captures:  {staging_counts['good']}")
    print(f"    Bad captures:   {staging_counts['bad']}")
    print(f"    Total:          {staging_counts['total']}")
    print()
    print("  MANIFEST (reviewed decisions)")
    print(f"    Approved:       {approved}")
    print(f"    Rejected:       {rejected}")
    print(f"    Train split:    {train} / {TRAIN_TARGET}  {bar(train, TRAIN_TARGET)}")
    print(f"    Eval split:     {eval_n} / {EVAL_TARGET}  {bar(eval_n, EVAL_TARGET)}")
    print()
    print("  EXPORTED DATASETS")
    if train_stats["count"] > 0:
        print(f"    train_v1.jsonl: {train_stats['count']} rows  avg {train_stats['avg_tasks']} tasks")
        print(f"    Roles:          {dict(sorted(train_stats['roles'].items(), key=lambda x:-x[1]))}")
    else:
        print(f"    train_v1.jsonl: not exported yet")
    if eval_stats["count"] > 0:
        print(f"    eval_v1.jsonl:  {eval_stats['count']} rows")
    else:
        print(f"    eval_v1.jsonl:  not exported yet")
    print()
    print("  GOALS GENERATED")
    print(f"    Batch files:    {goals['batch_files']}")
    print(f"    Total goals:    {goals['total_goals']}")
    print()

    # Phase gate check
    print("  PHASE GATES")
    p3_gate = train >= 150
    p3_full = train >= TRAIN_TARGET
    print(f"    Phase 3 v1 gate (150 train):   {'PASS' if p3_gate else 'FAIL'} ({train}/150)")
    print(f"    Phase 3 full goal (1000 train): {'PASS' if p3_full else f'need {TRAIN_TARGET - train} more'}")

    # Coverage gaps
    if train_stats["roles"]:
        print()
        print("  ROLE COVERAGE (train set)")
        for role in ["RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"]:
            count = train_stats["roles"].get(role, 0)
            print(f"    {role:<15} {count:>4} tasks")

    stop_flag = ws / ".orc" / "swarm" / "HARVEST_STOP"
    if stop_flag.exists():
        print()
        print("  [!] HARVEST_STOP exists — harvest is halted")

    print()
    print("=" * 55)


if __name__ == "__main__":
    main()
