#!/usr/bin/env python3
"""GOBLIN HARVEST first pass — deterministic pre-screen of staged captures.

Auto-flags the mechanical defect classes a human shouldn't waste time on:
  REJECT single_task      plan has fewer than 2 tasks
  REJECT invalid_role     a task role is not exactly one of the four (e.g. UIDEDEVELOPER)
  REJECT tester_write     a TESTER task uses file-writing verbs
  REJECT wrong_stack      plan task extensions conflict with the goal's stack
  REJECT low_rubric       capture staged as bad (score <= 39) for a train goal
  WARN   create_existing  a task says "create <file>" for a file already in the workspace
  WARN   fabricated_ref   a task updates/modifies a file that exists neither in the
                          repo nor as another task's deliverable in the same plan
  PASS                    survivor — needs human fabrication review

Regression anchors (caught by human review before these checks existed):
  ex_20260611_015650  invalid_role UIDEDEVELOPER
  ex_20260611_030431  create-existing MainWindow.xaml.cs
  ex_20260611_021318  fabricated_ref SettingsManager.cs (V3-T047)

Only captures whose goal text matches an entry in the goals file are considered
(everything else in staging is from earlier tranches and already reviewed).
Already-decided captures (present in the manifest by example_id) are skipped.

Usage:
  python prescreen_captures.py [--goals PSV] [--apply] [--workspace DIR]
  --apply runs review_captures.py --reject for every REJECT line.
"""
import argparse, json, re, subprocess, sys
from pathlib import Path

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
WRITE_VERBS = re.compile(r"\b(create|write|implement|build|generate|author|compose)\b", re.I)
UPDATE_NEAR_FILE = re.compile(
    r"\b(update|modif\w+|edit|extend|refactor)\b[^.]{0,60}?"
    r"([\w][\w./\\-]*\.(?:cs|xaml|py|ps1))", re.I)
FILE_RE = re.compile(r"[\w][\w./\\-]*\.(cs|xaml|py|ps1|psm1|csproj|json|md|ts|js)\b", re.I)
CREATE_NEAR_FILE = re.compile(
    r"\bcreat\w*\b(?![^.]{0,60}?\b(in|inside|within|under)\b)[^.]{0,60}?"
    r"([\w][\w./\\-]*\.(?:cs|xaml|py|ps1))", re.I)

LANG_EXT = {
    "cs": "csharp", "xaml": "csharp", "csproj": "csharp", "sln": "csharp", "slnx": "csharp",
    "py": "python",
    "ps1": "powershell", "psm1": "powershell", "psd1": "powershell",
    "ts": "typescript", "tsx": "typescript", "js": "javascript", "jsx": "javascript",
}
# js/ts in a plan for a C#/py/ps1 goal is the classic web-stack hallucination
CONFLICTS = {
    "csharp": {"python", "typescript", "javascript"},
    "python": {"csharp", "typescript", "javascript"},
    "powershell": {"csharp", "python", "typescript", "javascript"},
}


def norm(s: str) -> str:
    return re.sub(r"\s+", " ", s or "").strip().lower()


def langs_of(text: str) -> set:
    return {LANG_EXT[m.group(1).lower()] for m in FILE_RE.finditer(text)
            if m.group(1).lower() in LANG_EXT}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--goals", default="training_pit/batch_v3_goals.psv")
    ap.add_argument("--workspace", default=".")
    ap.add_argument("--apply", action="store_true",
                    help="execute review_captures.py --reject for REJECT lines")
    args = ap.parse_args()

    root = Path(args.workspace).resolve()
    staging = root / ".orc" / "swarm" / "dataset-staging"
    manifest_path = root / "training_pit" / "datasets" / "manifests" / "reviewed_v1.json"

    goal_ids = {}
    for line in Path(root / args.goals).read_text(encoding="utf-8").splitlines():
        if "|" not in line:
            continue
        gid, _domain, goal = line.split("|", 2)
        goal_ids[norm(goal)] = gid.strip()

    decided = set()
    if manifest_path.exists():
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        decided = set(manifest.get("entries", {}).keys())

    # index of workspace filenames for the create-existing check
    skip_dirs = {"bin", "obj", ".git", ".orc", "node_modules", "publish"}
    existing = set()
    for p in root.rglob("*"):
        if p.is_file() and not (set(p.parts) & skip_dirs):
            existing.add(p.name.lower())

    results = {"PASS": [], "WARN": [], "REJECT": []}
    for f in sorted(staging.glob("plan_capture_*.json")):
        try:
            cap = json.loads(f.read_text(encoding="utf-8"))
        except Exception as e:
            print(f"SKIP\t{f.name}\tunreadable: {e}")
            continue
        gid = goal_ids.get(norm(cap.get("goal", "")))
        if gid is None or cap.get("example_id") in decided:
            continue

        m = re.search(r"_(\d+)\.json$", f.name)
        score = int(m.group(1)) if m else -1
        tasks = (cap.get("plan") or {}).get("tasks") or []
        reasons, warns = [], []

        if f.name.startswith("plan_capture_bad_") or (0 <= score <= 39):
            reasons.append("low_rubric")
        if len(tasks) < 2:
            reasons.append("single_task")
        # files this plan itself creates — legitimate update targets for sibling tasks
        plan_created = set()
        for t in tasks:
            blob = f"{t.get('title','')} {t.get('description','')}"
            for cm in CREATE_NEAR_FILE.finditer(blob):
                plan_created.add(Path(cm.group(2).replace("\\", "/")).name.lower())
        for t in tasks:
            role = t.get("role", "").upper().strip()
            if role not in VALID_ROLES:
                reasons.append(f"invalid_role:{role or 'EMPTY'}")
            blob = f"{t.get('title','')} {t.get('description','')}"
            if role == "TESTER" and WRITE_VERBS.search(blob):
                reasons.append("tester_write")
            for cm in CREATE_NEAR_FILE.finditer(blob):
                name = Path(cm.group(2).replace("\\", "/")).name.lower()
                if name in existing:
                    warns.append(f"create_existing:{name}")
            for um in UPDATE_NEAR_FILE.finditer(blob):
                name = Path(um.group(2).replace("\\", "/")).name.lower()
                if name not in existing and name not in plan_created:
                    warns.append(f"fabricated_ref:{name}")
        goal_langs = langs_of(cap.get("goal", ""))
        plan_langs = langs_of(" ".join(
            f"{t.get('title','')} {t.get('description','')}" for t in tasks))
        for gl in goal_langs:
            bad = plan_langs & CONFLICTS.get(gl, set()) - goal_langs
            if bad:
                reasons.append(f"wrong_stack:{'+'.join(sorted(bad))}")

        reasons = sorted(set(reasons))
        rel = f.relative_to(root).as_posix()
        titles = " // ".join(f"{t.get('role','?')[:5]}:{t.get('title','')[:48]}" for t in tasks)
        if reasons:
            results["REJECT"].append((gid, rel, ",".join(reasons)))
            print(f"REJECT\t{gid}\t{rel}\t{','.join(reasons)}")
        elif warns:
            results["WARN"].append((gid, rel, ",".join(sorted(set(warns)))))
            print(f"WARN\t{gid}\t{rel}\tscore={score}\t{','.join(sorted(set(warns)))}\t{titles}")
        else:
            results["PASS"].append((gid, rel, score))
            print(f"PASS\t{gid}\t{rel}\tscore={score}\t{titles}")

    print(f"\n# pass={len(results['PASS'])} warn={len(results['WARN'])} "
          f"reject={len(results['REJECT'])}")

    if args.apply and results["REJECT"]:
        review = root / "training_pit" / "scripts" / "review_captures.py"
        for gid, rel, why in results["REJECT"]:
            r = subprocess.run(
                [sys.executable, str(review), "--reject", rel,
                 "--note", f"prescreen auto-reject ({gid}): {why}"],
                cwd=root, capture_output=True, text=True)
            tag = "applied" if r.returncode == 0 else f"FAILED: {r.stderr.strip()[:120]}"
            print(f"# reject {rel} -> {tag}")


if __name__ == "__main__":
    main()
