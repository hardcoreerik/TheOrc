#!/usr/bin/env python3
"""Bulk review sweep over every UNDECIDED staged capture (goals-file-free).

Unlike prescreen_captures.py this audits captures purely from their own
embedded goal+plan, so it works even when tranche goals files were lost or
overwritten (2026-06-11 prefix-collision fallout). Buckets every undecided
plan_capture_good_* capture:

  REJECT  mechanical defect (invalid role, tester-write, single-task,
          wrong-stack vs the capture's own goal, >4 tasks)
  FLAG    needs human eyes (create-existing file, update of a file that exists
          neither in repo nor plan, mock/demo substitution wording)
  OK      no defect signal — candidate for sampled bulk approval

Usage:
  python review_sweep.py                 # write sweep TSV + summary
  python review_sweep.py --apply-rejects # also reject REJECT bucket via review_captures.py
"""
import argparse, json, re, subprocess, sys
from pathlib import Path

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
WRITE_VERBS = re.compile(r"\b(create|write|implement|build|generate|author|compose)\b", re.I)
FILE_RE = re.compile(r"[\w][\w./\\-]*\.(cs|xaml|py|ps1|psm1|csproj|json|md|ts|js)\b", re.I)
CREATE_NEAR_FILE = re.compile(
    r"\bcreat\w*\b(?![^.]{0,60}?\b(in|inside|within|under)\b)[^.]{0,60}?"
    r"([\w][\w./\\-]*\.(?:cs|xaml|py|ps1))", re.I)
# Any authoring verb counts as "this plan creates that file" for the
# fabricated-ref check (implement/write/build/add miss = false fabrications)
AUTHOR_NEAR_FILE = re.compile(
    r"\b(creat\w*|implement\w*|write|build|add|author|generate)\b[^.]{0,60}?"
    r"([\w][\w./\\-]*\.(?:cs|xaml|py|ps1))", re.I)
UPDATE_NEAR_FILE = re.compile(
    r"\b(update|modif\w+|edit|extend|refactor)\b[^.]{0,60}?"
    r"([\w][\w./\\-]*\.(?:cs|xaml|py|ps1))", re.I)
MOCK_RE = re.compile(r"\b(mock|simulate[sd]?|placeholder|dummy)\b", re.I)
LANG_EXT = {"cs": "csharp", "xaml": "csharp", "csproj": "csharp",
            "py": "python", "ps1": "powershell", "psm1": "powershell",
            "ts": "web", "tsx": "web", "js": "web"}
CONFLICTS = {"csharp": {"python", "web"}, "python": {"csharp", "web"},
             "powershell": {"csharp", "python", "web"}}


def langs(text):
    return {LANG_EXT[m.group(1).lower()] for m in FILE_RE.finditer(text)
            if m.group(1).lower() in LANG_EXT}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workspace", default=".")
    ap.add_argument("--apply-rejects", action="store_true")
    ap.add_argument("--out", default="training_pit/review_sweep.tsv")
    args = ap.parse_args()

    root = Path(args.workspace).resolve()
    staging = root / ".orc" / "swarm" / "dataset-staging"
    manifest = json.loads((root / "training_pit/datasets/manifests/reviewed_v1.json")
                          .read_text(encoding="utf-8"))
    decided = set(manifest.get("entries", {}).keys())

    skip_dirs = {"bin", "obj", ".git", ".orc", "node_modules", "publish"}
    existing = {p.name.lower() for p in root.rglob("*")
                if p.is_file() and not (set(p.parts) & skip_dirs)}

    rows, counts = [], {"REJECT": 0, "FLAG": 0, "OK": 0}
    for f in sorted(staging.glob("plan_capture_good_*.json")):
        try:
            cap = json.loads(f.read_text(encoding="utf-8"))
        except Exception:
            continue
        if cap.get("example_id") in decided:
            continue

        goal  = cap.get("goal", "")
        tasks = (cap.get("plan") or {}).get("tasks") or []
        rejects, flags = [], []

        if not 2 <= len(tasks) <= 4:
            rejects.append(f"task_count:{len(tasks)}")
        snippets = []
        def snip(text, m):
            s = max(0, m.start() - 25); e = min(len(text), m.end() + 35)
            snippets.append(re.sub(r"\s+", " ", text[s:e]).strip())

        plan_created = set()
        for t in tasks:
            blob = f"{t.get('title','')} {t.get('description','')}"
            for cm in AUTHOR_NEAR_FILE.finditer(blob):
                plan_created.add(Path(cm.group(2).replace("\\", "/")).name.lower())
        # the goal's own authored files are legitimate plan targets too
        for cm in AUTHOR_NEAR_FILE.finditer(goal):
            plan_created.add(Path(cm.group(2).replace("\\", "/")).name.lower())
        plan_text = " ".join(f"{t.get('title','')} {t.get('description','')}" for t in tasks)
        for t in tasks:
            role = t.get("role", "").upper().strip()
            blob = f"{t.get('title','')} {t.get('description','')}"
            if role not in VALID_ROLES:
                rejects.append(f"invalid_role:{role or 'EMPTY'}")
            if role == "TESTER" and WRITE_VERBS.search(blob):
                rejects.append("tester_write")
            for cm in CREATE_NEAR_FILE.finditer(blob):
                name = Path(cm.group(2).replace("\\", "/")).name.lower()
                if name in existing:
                    flags.append(f"create_existing:{name}"); snip(blob, cm)
            for um in UPDATE_NEAR_FILE.finditer(blob):
                name = Path(um.group(2).replace("\\", "/")).name.lower()
                if name not in existing and name not in plan_created:
                    flags.append(f"fabricated_ref:{name}"); snip(blob, um)
            # mock/demo substitution only matters in builder lanes
            if role in ("CODER", "UIDEVELOPER"):
                mm = MOCK_RE.search(blob)
                if mm and not MOCK_RE.search(goal):
                    flags.append("mock_wording"); snip(blob, mm)
        bad = langs(plan_text) & CONFLICTS.get(next(iter(langs(goal)), ""), set())
        if langs(goal) and bad:
            rejects.append(f"wrong_stack:{'+'.join(sorted(bad))}")

        bucket = "REJECT" if rejects else "FLAG" if flags else "OK"
        counts[bucket] += 1
        roles = "/".join(t.get("role", "?")[:5] for t in tasks)
        rows.append((bucket, ",".join(rejects + flags) or "-",
                     f.relative_to(root).as_posix(), cap.get("example_id", "?"),
                     roles, re.sub(r"\s+", " ", goal)[:110],
                     " | ".join(snippets)[:200]))

    out = root / args.out
    with out.open("w", encoding="utf-8") as fh:
        fh.write("bucket\treasons\tfile\texample_id\troles\tgoal\tsnippet\n")
        for r in sorted(rows):
            fh.write("\t".join(r) + "\n")
    print(f"undecided={len(rows)}  REJECT={counts['REJECT']}  FLAG={counts['FLAG']}  "
          f"OK={counts['OK']}  -> {out.relative_to(root)}")

    if args.apply_rejects:
        review = root / "training_pit/scripts/review_captures.py"
        done = 0
        for bucket, reasons, rel, _exid, _roles, _goal, _snip in rows:
            if bucket != "REJECT":
                continue
            r = subprocess.run([sys.executable, str(review), "--reject", rel,
                                "--note", f"review_sweep auto-reject: {reasons}"],
                               cwd=root, capture_output=True, text=True)
            done += 1 if r.returncode == 0 else 0
        print(f"# applied {done} rejects")


if __name__ == "__main__":
    main()
