#!/usr/bin/env python3
"""NIGHT HARVEST goal generator — a local model authors new capture goals.

Reads PROMPT_AUTHORING_GUIDE.md as the system spec, shows the model a sample of
existing goals as style anchors, and asks for fresh ones. Every candidate is
linted in code against the guide's rules and deduped against every goal ever
authored (psv tranches, plan-doc tables, staged captures). Survivors are
written as a new pipe-delimited tranche ready for farm_batch.ps1.

The generator must NOT be the boss model family: goals authored by the boss
would feed the boss its own distribution back during fine-tuning.

Usage:
  python generate_goals.py [--count 30] [--model qwen2.5-coder:14b]
                           [--host URL] [--out PSV] [--prefix NH]
"""
import argparse, datetime, json, re, urllib.request
from pathlib import Path

VALID_DOMAINS = {"wpf_ui", "swarm", "ollama", "model_wiki", "csharp_core",
                 "testing", "git", "python_utility", "powershell", "training_pit"}
VAGUE = re.compile(r"\b(improve|optimi[sz]e|clean\s*up|refactor everything|make .{0,20}(better|faster|nicer)|fix it)\b", re.I)
FILE_ANCHOR = re.compile(r"[\w][\w./\\-]*\.(cs|xaml|py|ps1|psm1|csproj|json)\b", re.I)
LANG_EXT = {"cs": "csharp", "xaml": "csharp", "csproj": "csharp",
            "py": "python", "ps1": "powershell", "psm1": "powershell",
            "ts": "web", "tsx": "web", "js": "web", "html": "web"}


def norm(s):
    return re.sub(r"[^a-z0-9 ]", "", re.sub(r"\s+", " ", (s or "").lower())).strip()


def stacks(goal):
    return {LANG_EXT[m.group(1).lower()] for m in FILE_ANCHOR.finditer(goal)
            if m.group(1).lower() in LANG_EXT}


def existing_goals(root):
    """Every goal text we have ever farmed or planned, normalized."""
    seen = set()
    for psv in (root / "training_pit").glob("batch_*_goals*.psv"):
        for line in psv.read_text(encoding="utf-8").splitlines():
            if "|" in line:
                seen.add(norm(line.split("|", 2)[2]))
    for md in (root / "training_pit").glob("BATCH_CAPTURE_PLAN*.md"):
        for line in md.read_text(encoding="utf-8").splitlines():
            m = re.match(r"^\|\s*V\d+-[TEN]\d+\s*\|(?:[^|]*\|)?\s*(.+?)\s*\|\s*$", line)
            if m:
                seen.add(norm(m.group(1)))
    staging = root / ".orc" / "swarm" / "dataset-staging"
    if staging.exists():
        for f in staging.glob("plan_capture_*.json"):
            try:
                seen.add(norm(json.loads(f.read_text(encoding="utf-8")).get("goal", "")))
            except Exception:
                pass
    seen.discard("")
    return seen


def lint(goal, domain, seen):
    """Return None if the goal passes every guide rule, else the failure name."""
    if domain not in VALID_DOMAINS:
        return "invalid_domain"
    if "|" in goal or '"' in goal:
        return "forbidden_char"
    if not (120 <= len(goal) <= 700):
        return "bad_length"
    if not FILE_ANCHOR.search(goal):
        return "no_filename_anchor"
    if VAGUE.search(goal):
        return "vague_verb"
    st = stacks(goal)
    if len(st) > 1:
        return "mixed_stack"
    if "web" in st:
        return "web_stack"
    if re.search(r"\b(test|tests)\b", goal, re.I) and not re.search(
            r"TESTER lane should run", goal, re.I):
        return "tester_phrasing"
    if re.search(r"\b(readme|docs/|documentation)\b", goal, re.I):
        return "docs_goal"
    if norm(goal) in seen:
        return "duplicate"
    return None


def ask(host, model, guide, anchors, need, rejected_feedback):
    user = (f"Here are {len(anchors)} existing goals as style anchors:\n"
            + "\n".join(f"- {a}" for a in anchors)
            + f"\n\nAuthor {need} NEW goal prompts following the guide. Spread them "
            "across the listed domains. Do not restate or trivially vary the anchors "
            "or anything you already proposed."
            + (f"\nYour previous batch had rejects: {rejected_feedback}. Fix those patterns."
               if rejected_feedback else "")
            + '\n\nRespond with ONLY JSON: {"goals": [{"domain": "...", "goal": "..."}, ...]}')
    body = json.dumps({
        "model": model, "stream": False, "format": "json",
        "options": {"temperature": 0.8, "num_ctx": 16384},
        "messages": [{"role": "system",
                      "content": "You author dataset capture prompts. Follow this guide exactly:\n\n" + guide},
                     {"role": "user", "content": user}],
    }).encode()
    req = urllib.request.Request(f"{host}/api/chat", data=body,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        content = json.load(r)["message"]["content"]
    try:
        items = json.loads(content).get("goals", [])
        return [(str(i.get("domain", "general")).strip(),
                 re.sub(r"\s+", " ", str(i.get("goal", ""))).strip())
                for i in items if i.get("goal")]
    except json.JSONDecodeError:
        return []


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", type=int, default=30)
    ap.add_argument("--model", default="qwen2.5-coder:14b")
    ap.add_argument("--host", default="http://localhost:11434")
    ap.add_argument("--workspace", default=".")
    ap.add_argument("--prefix", default=None, help="tranche id prefix, default NH<yyMMdd>")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    if "boss" in args.model or "gemma" in args.model:
        raise SystemExit("Refusing: goal generator must not be the boss model family.")

    root = Path(args.workspace).resolve()
    guide = (root / "training_pit" / "PROMPT_AUTHORING_GUIDE.md").read_text(encoding="utf-8")
    seen = existing_goals(root)
    prefix = args.prefix or ("NH" + datetime.date.today().strftime("%y%m%d"))
    out = Path(args.out) if args.out else root / "training_pit" / f"batch_{prefix}_goals.psv"

    # style anchors: spread across an existing tranche
    anchors = []
    v3 = root / "training_pit" / "batch_v3_goals.psv"
    if v3.exists():
        lines = [l for l in v3.read_text(encoding="utf-8").splitlines() if "|" in l]
        anchors = [l.split("|", 2)[2] for l in lines[:: max(1, len(lines) // 12)]][:12]

    accepted, feedback, rounds = [], "", 0
    while len(accepted) < args.count and rounds < 6:
        rounds += 1
        need = min(args.count - len(accepted) + 5, 40)
        rejects = {}
        for domain, goal in ask(args.host, args.model, guide, anchors, need, feedback):
            why = lint(goal, domain, seen)
            if why:
                rejects[why] = rejects.get(why, 0) + 1
                continue
            seen.add(norm(goal))
            accepted.append((domain, goal))
            if len(accepted) >= args.count:
                break
        feedback = ", ".join(f"{k} x{v}" for k, v in sorted(rejects.items()))
        print(f"round {rounds}: accepted={len(accepted)}/{args.count}"
              + (f" rejects: {feedback}" if feedback else ""))

    # Atomic write: never leave a partial tranche a farm run could pick up.
    tmp = out.with_suffix(".psv.tmp")
    with tmp.open("w", encoding="utf-8", newline="\n") as fh:
        for i, (domain, goal) in enumerate(accepted, 1):
            fh.write(f"{prefix}-T{i:03}|{domain}|{goal}\n")
    tmp.replace(out)
    print(f"\nwrote {len(accepted)} goals -> {out}")
    if len(accepted) < args.count:
        print(f"SHORTFALL: {len(accepted)}/{args.count} after {rounds} rounds")
        sys.exit(2)   # nonzero so night_harvest fails closed on a thin tranche


if __name__ == "__main__":
    main()
