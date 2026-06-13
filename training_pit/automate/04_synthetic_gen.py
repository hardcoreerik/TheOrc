#!/usr/bin/env python3
"""Generate synthetic boss-plan SFT examples using a local small model.

The model is asked to produce a well-formed JSON plan for each goal.
Each passing example is saved as a chat-JSONL row ready for review
and eventual training.

Usage:
  python 04_synthetic_gen.py --count 50 --model qwen2.5-coder:7b
  python 04_synthetic_gen.py --count 100 --out my_synth.jsonl
"""
import argparse, json, random, re, sys, time
from datetime import datetime
from pathlib import Path
import urllib.request, urllib.error

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
FILE_RE = re.compile(r"[\w./\\-]+\.(cs|xaml|py|ps1|psm1|csproj|json|md|ts|js)\b", re.I)
WRITE_VERBS = re.compile(r"\b(create|write|implement|build|generate)\b", re.I)

BOSS_SYSTEM = """\
You are TheOrc, a senior software architect. When given a goal, you produce a JSON task plan
that decomposes the goal into 2-4 concrete tasks for your team.

STRICT RULES:
- Output ONLY a valid JSON object — no preamble, no markdown fences, no explanation
- Each task has exactly: role, title, description
- role must be one of: RESEARCHER, CODER, UIDEVELOPER, TESTER
- CODER and UIDEVELOPER tasks must name specific files (e.g., Services/UserService.cs)
- TESTER tasks must verify, not write code — no create/write/implement verbs
- Do not invent APIs, NuGet packages, or libraries that don't exist

FORMAT:
{"tasks": [{"role": "ROLE", "title": "...", "description": "..."}]}"""

GOAL_BANK = [
    "Add a dark mode toggle to the WPF settings panel that persists across sessions",
    "Implement a CSV export feature for the data grid that handles Unicode filenames",
    "Write PowerShell scripts to automate daily backup of the SQLite database to a network share",
    "Add retry logic with exponential backoff to the HTTP client service",
    "Create a progress bar component for long-running operations in the UI",
    "Implement input validation for the user registration form with inline error messages",
    "Add logging middleware that writes structured JSON logs to a rolling file",
    "Build a plugin discovery system that loads assemblies from a plugins/ folder at startup",
    "Write unit tests for the authentication service covering token expiry edge cases",
    "Refactor the database access layer to use the repository pattern",
    "Add a system tray icon that shows notification count and allows quick navigation",
    "Implement keyboard shortcuts for the top 5 most-used actions in the main window",
    "Create a configuration migration tool that upgrades settings from v1 to v2 format",
    "Add a search bar with debounced filtering to the main data list view",
    "Implement graceful shutdown that saves in-progress work on app close",
    "Write a CLI wrapper around the core service for headless batch processing",
    "Add telemetry to track which features users interact with most",
    "Implement a job queue that limits concurrent background tasks to 3",
    "Create a health check endpoint for the local API server",
    "Add automatic update checking that notifies users without forcing a restart",
]


def ollama_chat(host, model, messages):
    body = json.dumps({
        "model": model,
        "messages": messages,
        "stream": False,
        "options": {"temperature": 0.7, "num_predict": 800},
    }).encode()
    req = urllib.request.Request(
        f"{host}/api/chat",
        data=body,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=60) as resp:
        data = json.loads(resp.read())
    return data["message"]["content"]


def score_plan(text):
    m = re.search(r"\{.*\}", text, re.S)
    if not m:
        return 0, None
    try:
        plan = json.loads(m.group(0))
    except json.JSONDecodeError:
        return 0, None
    tasks = plan.get("tasks") if isinstance(plan, dict) else None
    if not isinstance(tasks, list) or not (2 <= len(tasks) <= 4):
        return 1, plan
    score = 2
    for t in tasks:
        if not isinstance(t, dict):
            continue
        role = str(t.get("role", "")).upper()
        if role not in VALID_ROLES:
            return score, plan
        blob = f"{t.get('title','')} {t.get('description','')}"
        if role in ("CODER", "UIDEVELOPER") and not FILE_RE.search(blob):
            return score, plan
        if role == "TESTER" and WRITE_VERBS.search(blob):
            return score, plan
    return 5, plan


def make_example(goal, raw_output):
    """Format as chat JSONL row."""
    m = re.search(r"\{.*\}", raw_output, re.S)
    assistant_content = m.group(0) if m else raw_output
    return {
        "messages": [
            {"role": "system", "content": BOSS_SYSTEM},
            {"role": "user",   "content": goal},
            {"role": "assistant", "content": assistant_content},
        ],
        "metadata": {
            "source": "synthetic_hardcorepc",
            "model": None,
            "generated": datetime.now().isoformat(timespec="seconds"),
            "score": None,
        },
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", type=int, default=50, help="Examples to generate")
    ap.add_argument("--model", default="qwen2.5-coder:7b")
    ap.add_argument("--host",  default="http://localhost:11434")
    ap.add_argument("--out",   default="synthetic_sft.jsonl")
    ap.add_argument("--goals", default="", help="Optional PSV goals file to draw from")
    args = ap.parse_args()

    # Load goals
    goals = []
    if args.goals and Path(args.goals).exists():
        for line in Path(args.goals).read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if "|" in line:
                goals.append(line.split("|", 1)[1].strip())
            elif line:
                goals.append(line)
        print(f"Loaded {len(goals)} goals from {args.goals}")
    else:
        goals = GOAL_BANK
        print(f"Using built-in goal bank ({len(goals)} goals)")

    out_path = Path(args.out)
    passed = 0
    failed = 0
    total  = 0

    print(f"\nGenerating {args.count} synthetic examples via {args.model}...\n")

    with out_path.open("w", encoding="utf-8") as fout:
        while passed < args.count:
            goal = random.choice(goals)
            total += 1

            try:
                raw = ollama_chat(args.host, args.model, [
                    {"role": "system",  "content": BOSS_SYSTEM},
                    {"role": "user",    "content": goal},
                ])
            except Exception as e:
                print(f"  [!] Ollama error: {e}")
                time.sleep(2)
                continue

            sc, plan = score_plan(raw)
            if sc < 5:
                failed += 1
                print(f"  [{total}] FAIL (score {sc}) — {goal[:60]}")
                continue

            example = make_example(goal, raw)
            example["metadata"]["model"] = args.model
            example["metadata"]["score"] = sc
            fout.write(json.dumps(example) + "\n")
            passed += 1
            print(f"  [{total}] PASS ({passed}/{args.count}) — {goal[:60]}")

    print(f"\nDone. {passed} examples saved to {out_path}")
    print(f"Pass rate: {passed}/{total} ({100*passed//max(total,1)}%)")
    print(f"Upload to main machine and review with review_captures.py")


if __name__ == "__main__":
    main()
