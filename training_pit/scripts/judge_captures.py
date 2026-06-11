#!/usr/bin/env python3
"""GOBLIN HARVEST second pass — local judge-model triage of pre-screen survivors.

The deterministic pre-screen (prescreen_captures.py) catches mechanical defects.
This pass targets what the rubric cannot see: confident fabrication — invented
file paths, invented API/class names, filler tasks unrelated to the goal, and
plans whose tasks contradict each other.

The judge NEVER approves or rejects. It sorts survivors into a triage order
(high risk first) so the human reviewer spends attention where it matters.
Per ROADMAP.md the judge must not be the boss model: default qwen2.5-coder:14b.

Usage:
  python judge_captures.py [--goals PSV] [--model NAME] [--host URL] [--out TSV]
"""
import argparse, json, re, urllib.request
from pathlib import Path

SYSTEM = """You are a strict dataset reviewer for a multi-agent swarm planner.
You receive a user GOAL, the PLAN a boss model produced, and a FILE CHECK table
showing, for every file the plan mentions, whether it actually exists in the
repository. Use the table — do not guess about file existence.

HARD RULES — any violation is automatically "high" risk:
1. Task roles must be EXACTLY one of: RESEARCHER, CODER, UIDEVELOPER, TESTER.
   Any other string, including typos like UIDEDEVELOPER, is invalid.
2. A task that says create/write a NEW file which the FILE CHECK table marks
   as EXISTING is treating real code as greenfield (it would overwrite it).
3. A task that says update/modify a file the table marks MISSING (and which no
   sibling task creates) is fabricating knowledge of nonexistent code.
4. A plan that delivers a mock, demo, or "integration example" instead of the
   real integration the goal asked for.

Also flag (medium): invented API/class/property names asserted as already
existing, filler tasks unrelated to the goal, tasks contradicting each other
on a shared deliverable's name, or solving a different problem than asked.

Real failures previously missed by a reviewer like you (do not repeat them):
- role "UIDEDEVELOPER" rated low risk — it is an invalid role, high risk
- a task "Create a C# file MainWindow.xaml.cs" rated low — that file existed,
  the plan would overwrite the app's main window: high risk
- a task "write a mock class MainWindow that simulates..." rated low — mock
  substituted for the requested real integration: high risk

Do NOT penalize: new helper files the goal requests (table will show MISSING —
that is correct for new code a CREATE task introduces), standard framework
APIs, or a RESEARCHER task that investigates before coding.

Respond with ONLY a JSON object:
{"fabrication_risk": "low"|"medium"|"high",
 "issues": ["<short specific issue>", ...],
 "summary": "<one sentence>"}"""

FILE_RE = re.compile(r"[\w][\w./\\-]*\.(?:cs|xaml|py|ps1|psm1|csproj|json|md)\b", re.I)


def norm(s):
    return re.sub(r"\s+", " ", s or "").strip().lower()


def ask_judge(host, model, goal, tasks, existing_names):
    plan_txt = "\n".join(
        f"- [{t.get('role','?')}] {t.get('title','')}\n  {t.get('description','')}"
        for t in tasks)
    mentioned = sorted({m.group(0) for m in FILE_RE.finditer(
        goal + " " + plan_txt)})
    file_check = "\n".join(
        f"- {name}: {'EXISTS in repo' if name.replace(chr(92), '/').rsplit('/', 1)[-1].lower() in existing_names else 'MISSING from repo'}"
        for name in mentioned) or "- (no file references found)"
    body = json.dumps({
        "model": model,
        "stream": False,
        "format": "json",
        "options": {"temperature": 0, "num_ctx": 8192},
        "messages": [
            {"role": "system", "content": SYSTEM},
            {"role": "user", "content":
                f"GOAL:\n{goal}\n\nPLAN:\n{plan_txt}\n\nFILE CHECK:\n{file_check}"},
        ],
    }).encode()
    req = urllib.request.Request(f"{host}/api/chat", data=body,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as r:
        content = json.load(r)["message"]["content"]
    try:
        v = json.loads(content)
        risk = str(v.get("fabrication_risk", "medium")).lower()
        if risk not in ("low", "medium", "high"):
            risk = "medium"
        return risk, [str(i) for i in v.get("issues", [])][:6], str(v.get("summary", ""))
    except json.JSONDecodeError:
        return "medium", ["judge returned non-JSON"], content[:120]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--goals", default="training_pit/batch_v3_goals.psv")
    ap.add_argument("--model", default="qwen2.5-coder:14b")
    ap.add_argument("--host", default="http://localhost:11434")
    ap.add_argument("--workspace", default=".")
    ap.add_argument("--out", default="training_pit/batch_v3_triage.tsv")
    args = ap.parse_args()

    root = Path(args.workspace).resolve()
    staging = root / ".orc" / "swarm" / "dataset-staging"
    manifest_path = root / "training_pit" / "datasets" / "manifests" / "reviewed_v1.json"

    if "boss" in args.model or "gemma" in args.model:
        raise SystemExit("Refusing: judge must not be the boss model family (see ROADMAP.md).")

    goal_ids = {}
    for line in (root / args.goals).read_text(encoding="utf-8").splitlines():
        if "|" in line:
            gid, _d, goal = line.split("|", 2)
            goal_ids[norm(goal)] = gid.strip()

    decided = set()
    if manifest_path.exists():
        decided = set(json.loads(manifest_path.read_text(encoding="utf-8"))
                      .get("entries", {}).keys())

    # repo file index for the FILE CHECK table (skip transient dirs)
    skip_dirs = {"bin", "obj", ".git", ".orc", "node_modules", "publish"}
    existing_names = {p.name.lower() for p in root.rglob("*")
                      if p.is_file() and not (set(p.parts) & skip_dirs)}

    rows = []
    files = sorted(staging.glob("plan_capture_good_*.json"))  # judge only good captures
    for f in files:
        cap = json.loads(f.read_text(encoding="utf-8"))
        gid = goal_ids.get(norm(cap.get("goal", "")))
        if gid is None or cap.get("example_id") in decided:
            continue
        tasks = (cap.get("plan") or {}).get("tasks") or []
        m = re.search(r"_(\d+)\.json$", f.name)
        score = int(m.group(1)) if m else -1
        risk, issues, summary = ask_judge(args.host, args.model, cap["goal"], tasks, existing_names)
        rel = f.relative_to(root).as_posix()
        rows.append((risk, gid, score, rel, "; ".join(issues), summary))
        print(f"{risk.upper():6} {gid} score={score} {summary[:90]}")

    order = {"high": 0, "medium": 1, "low": 2}
    rows.sort(key=lambda r: (order[r[0]], -r[2]))
    out = root / args.out
    with out.open("w", encoding="utf-8") as fh:
        fh.write("risk\tid\tscore\tfile\tissues\tsummary\n")
        for r in rows:
            fh.write("\t".join(str(x) for x in r) + "\n")

    counts = {k: sum(1 for r in rows if r[0] == k) for k in ("high", "medium", "low")}
    print(f"\n# judged={len(rows)} high={counts['high']} medium={counts['medium']} "
          f"low={counts['low']} -> {out.relative_to(root)}")


if __name__ == "__main__":
    main()
