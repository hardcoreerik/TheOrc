#!/usr/bin/env python3
"""Interactive batch review helper — works over a network workspace.

Shows captures one by one: goal, plan, rubric score.
Key commands: a=approve  r=reject  s=skip  q=quit

Usage:
  python 08_review_batch.py
  python 08_review_batch.py --workspace \\HARDCORERIK\F$\Ai\OrchestratorIDE --limit 20
"""
import argparse, json, os, re, sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
WRITE_VERBS = re.compile(r"\b(create|write|implement|build|generate)\b", re.I)
FILE_RE     = re.compile(r"[\w./\\-]+\.(cs|xaml|py|ps1|csproj|json|md|ts|js)\b", re.I)


def score_plan(text):
    checks = {}
    m = re.search(r"\{.*\}", text, re.S)
    checks["valid_json"] = bool(m)
    if not m:
        return checks, None
    try:
        plan = json.loads(m.group(0))
    except json.JSONDecodeError:
        checks["valid_json"] = False
        return checks, None

    tasks = plan.get("tasks") if isinstance(plan, dict) else None
    checks["task_count_ok"] = isinstance(tasks, list) and 2 <= len(tasks) <= 4
    if not isinstance(tasks, list):
        return checks, plan

    roles_ok = files_ok = tester_ok = True
    for t in tasks:
        if not isinstance(t, dict):
            roles_ok = False
            continue
        role = str(t.get("role", "")).upper()
        if role not in VALID_ROLES:
            roles_ok = False
        blob = f"{t.get('title','')} {t.get('description','')}"
        if role in ("CODER", "UIDEVELOPER") and not FILE_RE.search(blob):
            files_ok = False
        if role == "TESTER" and WRITE_VERBS.search(blob):
            tester_ok = False
    checks["roles_valid"]     = roles_ok
    checks["files_named"]     = files_ok
    checks["no_tester_write"] = tester_ok
    return checks, plan


def load_manifest(manifest_path):
    if manifest_path.exists():
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
        entries = data if isinstance(data, list) else data.get("entries", [])
        return {e.get("capture_id") or e.get("file"): e.get("decision") for e in entries}
    return {}


def save_decision(manifest_path, capture_id, decision):
    if manifest_path.exists():
        data = json.loads(manifest_path.read_text(encoding="utf-8"))
        if isinstance(data, list):
            data = {"entries": data}
        entries = data.get("entries", [])
    else:
        data = {"entries": []}
        entries = data["entries"]

    existing = next((e for e in entries if (e.get("capture_id") or e.get("file")) == capture_id), None)
    if existing:
        existing["decision"] = decision
    else:
        entries.append({"capture_id": capture_id, "file": capture_id, "decision": decision})
    data["entries"] = entries
    manifest_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def display_capture(path, idx, total):
    os.system("cls" if os.name == "nt" else "clear")
    try:
        cap = json.loads(path.read_text(encoding="utf-8"))
    except Exception as e:
        print(f"  ERROR reading {path.name}: {e}")
        return None, None

    goal = cap.get("goal", cap.get("user_goal", "?"))
    raw  = cap.get("boss_response", cap.get("raw_plan", ""))
    score_field = cap.get("score", cap.get("quality_score", "?"))

    checks, plan = score_plan(raw)

    print(f"\n{'='*60}")
    print(f"  [{idx}/{total}]  {path.name}")
    print(f"{'='*60}")
    print(f"\n  GOAL:\n    {goal[:200]}")
    print(f"\n  RUBRIC SCORE: {score_field}")
    print("  CHECKS:")
    for k, v in checks.items():
        mark = "PASS" if v else "FAIL"
        print(f"    [{mark}] {k}")

    if plan and isinstance(plan.get("tasks"), list):
        print(f"\n  PLAN ({len(plan['tasks'])} tasks):")
        for i, t in enumerate(plan["tasks"], 1):
            role = t.get("role", "?")
            title = t.get("title", "?")
            desc = t.get("description", "")[:100]
            print(f"    {i}. [{role}] {title}")
            print(f"       {desc}")
    else:
        print(f"\n  RAW OUTPUT (first 400 chars):\n{raw[:400]}")

    return goal, plan


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workspace", default="F:\\Ai\\OrchestratorIDE")
    ap.add_argument("--limit",     type=int, default=0, help="Max captures to review")
    ap.add_argument("--unreviewed-only", action="store_true", default=True)
    args = ap.parse_args()

    ws       = Path(args.workspace)
    staging  = ws / ".orc" / "swarm" / "dataset-staging"
    manifest = ws / "training_pit" / "datasets" / "manifests" / "reviewed_v1.json"

    if not staging.exists():
        print(f"Staging directory not found: {staging}")
        sys.exit(1)

    captures = sorted(staging.glob("plan_capture_*.json"))
    if not captures:
        print("No captures found in staging directory.")
        sys.exit(0)

    decisions = load_manifest(manifest)

    if args.unreviewed_only:
        captures = [c for c in captures if c.name not in decisions]

    if args.limit:
        captures = captures[:args.limit]

    print(f"\nReviewing {len(captures)} captures (unreviewed only: {args.unreviewed_only})")
    print("Commands: a=approve  r=reject  s=skip  q=quit\n")

    approved = rejected = skipped = 0

    for idx, path in enumerate(captures, 1):
        goal, plan = display_capture(path, idx, len(captures))
        if goal is None:
            skipped += 1
            continue

        print(f"\n  [a] Approve  [r] Reject  [s] Skip  [q] Quit")
        try:
            choice = input("  Decision: ").strip().lower()
        except (KeyboardInterrupt, EOFError):
            print("\nInterrupted.")
            break

        if choice == "q":
            break
        elif choice == "a":
            save_decision(manifest, path.name, "approved")
            approved += 1
            print("  -> Approved")
        elif choice == "r":
            save_decision(manifest, path.name, "rejected")
            rejected += 1
            print("  -> Rejected")
        else:
            skipped += 1
            print("  -> Skipped")

    print(f"\nSession done: {approved} approved | {rejected} rejected | {skipped} skipped")
    print(f"Manifest: {manifest}")


if __name__ == "__main__":
    main()
