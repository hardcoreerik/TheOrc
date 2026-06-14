#!/usr/bin/env python3
"""
generate_codex_gold.py
Generates GOLD-STANDARD boss-decomposition training examples via Codex CLI.

Each example pairs a multi-sentence developer request (the user's natural voice,
realistic typos in the INPUT only) with a flawless boss decomposition produced by
Codex (GPT-5 class) -- distilling its planning ability into the local Gemma4 boss.

Pipeline per batch:
  1. Build a batch prompt (language + task-type theme).
  2. Invoke codex.exe exec (stdin closed -> no hang; --output-last-message).
  3. Parse compact {"goal","plan","tasks"} lines.
  4. Validate + fix priorities + wrap in canonical schema + dedupe.
  5. Append to the output JSONL (incremental / resumable).

Usage:
  python tools/generate_codex_gold.py --smoke          # 1 small batch, prints samples, no write
  python tools/generate_codex_gold.py                  # full run: 80 batches x 25 = 2000
  python tools/generate_codex_gold.py --batches 80 --per-batch 25
  python tools/generate_codex_gold.py --resume         # continue an interrupted run
"""

import argparse
import json
import os
import random
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

ROOT   = Path(__file__).resolve().parent.parent
DS_DIR = ROOT / "training_pit" / "datasets"
CANON  = DS_DIR / "mainpc[24gb].captured.boss.1384.jsonl"

OUT_WORK = DS_DIR / "codex_gold.work.jsonl"
PROGRESS = DS_DIR / "codex_gold.progress.json"

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
FILENAME_RE = re.compile(r"[\w\-]+\.[A-Za-z0-9]{1,6}\b")

# ── Balanced-realistic distribution (batches per language) ──────────────────────
LANG_BATCHES = {
    "Python":                 24,
    "C#":                     20,
    "JavaScript/TypeScript":  12,
    "SQL":                     8,
    "Rust":                    3,
    "Go":                      3,
    "PowerShell":              3,
    "Bash":                    2,
    "Java":                    2,
    "HTML/CSS/JavaScript":     3,
}  # sum = 80

TASK_TYPES = [
    ("feature",     0.40),
    ("refactor",    0.15),
    ("bugfix",      0.15),
    ("tests",       0.10),
    ("ui",          0.10),
    ("docs",        0.05),
    ("integration", 0.05),
]

# task types that make no sense for certain languages -> remap to feature
NO_UI_LANGS = {"SQL", "Bash", "PowerShell"}


def find_codex() -> str:
    appdata = os.environ.get("APPDATA", "")
    base = Path(appdata) / "npm" / "node_modules" / "@openai" / "codex"
    if base.exists():
        for p in base.rglob("codex.exe"):
            return str(p)
    from shutil import which
    exe = which("codex.exe") or which("codex")
    if exe:
        return exe
    print("ERROR: codex.exe not found. Install: npm i -g @openai/codex", file=sys.stderr)
    sys.exit(3)


def load_system_prompt() -> str:
    with open(CANON, encoding="utf-8") as f:
        first = json.loads(f.readline())
    sp = first["messages"][0]["content"]
    assert "plan" in sp and "priority" in sp, "Canonical system prompt looks wrong"
    return sp


def pick_task_type(rng: random.Random, language: str) -> str:
    r = rng.random()
    acc = 0.0
    for name, w in TASK_TYPES:
        acc += w
        if r <= acc:
            if name == "ui" and language in NO_UI_LANGS:
                return "feature"
            return name
    return "feature"


def build_batch_plan(rng: random.Random, batches: int, per_batch: int) -> list[dict]:
    """Returns a list of batch specs honoring the language distribution."""
    # Scale LANG_BATCHES to the requested batch count
    total = sum(LANG_BATCHES.values())
    langs = []
    for lang, count in LANG_BATCHES.items():
        scaled = max(1, round(count * batches / total))
        langs.extend([lang] * scaled)
    # Trim/pad to exactly `batches`
    langs = langs[:batches]
    while len(langs) < batches:
        langs.append("Python")
    rng.shuffle(langs)

    return [
        {"idx": i, "language": lang, "task_type": pick_task_type(rng, lang), "n": per_batch}
        for i, lang in enumerate(langs)
    ]


def batch_prompt(spec: dict) -> str:
    n, language, task_type = spec["n"], spec["language"], spec["task_type"]
    return f"""\
You are generating GOLD-STANDARD training data for "TheOrc", a local AI orchestrator \
that decomposes a developer's coding request into 2-4 concurrent subtasks for \
specialist agents: RESEARCHER, CODER, UIDEVELOPER, TESTER.

TASK: Output exactly {n} examples as JSONL -- one compact JSON object per line, \
nothing else. Do NOT explore the repository. Do NOT use tools. Pure text generation.

Each line MUST be exactly this minified shape (one physical line):
{{"goal":"<developer request>","plan":"<1-3 sentences>","tasks":[{{"role":"...","priority":1,"title":"...","description":"..."}}]}}

THIS BATCH: language = {language}; primary task type = {task_type}.
All {n} examples target {language}. Vary the specific projects/domains widely -- \
no two goals about the same thing.

THE "goal" FIELD (the developer's request) -- match this voice EXACTLY:
- 2-6 sentences, conversational, like a real dev briefing an assistant in chat.
- Lead with context ("Im building...", "We've got an existing module that...").
- Lay out multiple steps/requirements in sequence.
- Reference concrete files, functions, libraries, endpoints by name.
- State constraints inline (encoding, target framework, perf, error handling).
- REALISTIC TYPOS REQUIRED: include occasional typos, missing apostrophes, \
lowercase "i", a run-on -- like hurried chat input. ~1-2 small errors per goal. \
This is intentional; do NOT write perfect prose in the goal.

THE DECOMPOSITION (plan + tasks) -- must be FLAWLESS (this is the gold label):
- 2-4 tasks. RESEARCHER => priority 1; CODER/UIDEVELOPER/TESTER => priority 2.
- Include a RESEARCHER ONLY when the goal genuinely needs investigation \
(unfamiliar API/library); otherwise go straight to CODER/UIDEVELOPER/TESTER.
- Every task title MUST name the output file(s), e.g. "Write scraper.py and client.py".
- If one task produces a module another imports, use the EXACT same function/class \
names in BOTH task descriptions.
- Descriptions are self-contained, 2-4 sentences, specify {language} explicitly.
- "plan": 1-3 sentences -- overall approach + the key dependency between tasks.
- NO typos anywhere in plan/tasks/roles. Valid minified JSON. Use \\n not literal \
newlines inside strings.

Output ONLY the {n} JSONL lines. No preamble, no markdown fences, no commentary."""


def run_codex(exe: str, prompt: str, timeout: int) -> str:
    with tempfile.NamedTemporaryFile("r", suffix=".last", delete=False, encoding="utf-8") as tf:
        last_path = tf.name
    try:
        args = [exe, "exec", "--sandbox", "read-only", "-C", str(ROOT),
                "--output-last-message", last_path, prompt]
        proc = subprocess.run(
            args, stdin=subprocess.DEVNULL,
            capture_output=True, text=True, timeout=timeout, cwd=str(ROOT),
        )
        out = ""
        try:
            out = Path(last_path).read_text(encoding="utf-8")
        except Exception:
            pass
        return out.strip() or (proc.stdout or "").strip()
    finally:
        try: os.unlink(last_path)
        except Exception: pass


def parse_lines(raw: str) -> list[dict]:
    """Extract compact example objects from Codex output (JSONL or array)."""
    raw = re.sub(r"```(?:json)?", "", raw, flags=re.IGNORECASE).strip()
    results = []

    # Try whole-blob as a JSON array first
    try:
        blob = json.loads(raw)
        if isinstance(blob, list):
            return [x for x in blob if isinstance(x, dict)]
    except Exception:
        pass

    for line in raw.split("\n"):
        line = line.strip().rstrip(",")
        if not (line.startswith("{") and line.endswith("}")):
            continue
        try:
            obj = json.loads(line)
            if isinstance(obj, dict):
                results.append(obj)
        except Exception:
            continue
    return results


def sentence_count(text: str) -> int:
    return len(re.findall(r"[.!?]", text))


def validate_and_fix(obj: dict) -> dict | None:
    goal  = (obj.get("goal")  or "").strip()
    plan  = (obj.get("plan")  or "").strip()
    tasks = obj.get("tasks")

    if not goal or not plan or not isinstance(tasks, list):
        return None
    # Multi-sentence goal gate (lenient for run-ons)
    if len(goal) < 80 or (sentence_count(goal) < 2 and len(goal) < 160):
        return None
    if not (2 <= len(tasks) <= 4):
        return None

    fixed_tasks = []
    for t in tasks:
        if not isinstance(t, dict):
            return None
        role = (t.get("role") or "").strip().upper()
        if role not in VALID_ROLES:
            return None
        title = (t.get("title") or "").strip()
        desc  = (t.get("description") or "").strip()
        if not title or len(desc) < 30:
            return None
        # Title must name a file (skip this check for TESTER, which verifies existing files)
        if role != "TESTER" and not FILENAME_RE.search(title):
            return None
        # Enforce priority rule regardless of what Codex emitted
        priority = 1 if role == "RESEARCHER" else 2
        fixed_tasks.append({"role": role, "priority": priority,
                            "title": title, "description": desc})

    # At most one researcher, and it must be priority 1
    researchers = [t for t in fixed_tasks if t["role"] == "RESEARCHER"]
    if len(researchers) > 1:
        return None

    return {"goal": goal, "plan": plan, "tasks": fixed_tasks}


def wrap(example: dict, system_prompt: str, spec: dict) -> dict:
    assistant = json.dumps({"plan": example["plan"], "tasks": example["tasks"]},
                           ensure_ascii=False, separators=(",", ":"))
    return {
        "messages": [
            {"role": "system",    "content": system_prompt},
            {"role": "user",      "content": f"Goal: {example['goal']}"},
            {"role": "assistant", "content": assistant},
        ],
        "metadata": {
            "category":                "boss_planning",
            "task_type":               spec["task_type"],
            "source":                  "codex_synthetic",
            "quality":                 "gold",
            "contains_sensitive_data": False,
            "base_model_target":       "theorc-boss:gemma4",
            "created_by":              "generate_codex_gold.py",
            "language":                spec["language"],
            "style":                   "multi_sentence_typos",
            "notes": f"Codex gold, batch {spec['idx']}, theme {spec['task_type']}/{spec['language']}.",
        },
    }


def norm_goal(goal: str) -> str:
    return re.sub(r"\s+", " ", goal.lower().strip())


def main():
    ap = argparse.ArgumentParser(description="Generate Codex gold boss-decomposition data")
    ap.add_argument("--smoke",     action="store_true", help="1 small batch, print samples, no write")
    ap.add_argument("--batches",   type=int, default=80, help="Number of batches (default 80)")
    ap.add_argument("--per-batch", type=int, default=25, help="Examples per batch (default 25)")
    ap.add_argument("--timeout",   type=int, default=300, help="Per-batch Codex timeout seconds")
    ap.add_argument("--resume",    action="store_true", help="Continue an interrupted run")
    ap.add_argument("--seed",      type=int, default=42)
    args = ap.parse_args()

    exe = find_codex()
    system_prompt = load_system_prompt()
    rng = random.Random(args.seed)

    print("=" * 64)
    print("  TheOrc -- Codex Gold Generator")
    print("=" * 64)
    print(f"  codex.exe : {exe}")
    print(f"  sys prompt: {len(system_prompt)} chars")

    if args.smoke:
        spec = {"idx": 0, "language": "Python", "task_type": "feature", "n": 6}
        print(f"  SMOKE: 1 batch, {spec['n']} examples, {spec['language']}/{spec['task_type']}\n")
        raw = run_codex(exe, batch_prompt(spec), args.timeout)
        parsed = parse_lines(raw)
        valid = [validate_and_fix(o) for o in parsed]
        valid = [v for v in valid if v]
        print(f"  Codex returned {len(parsed)} line(s); {len(valid)} passed validation.\n")
        for i, v in enumerate(valid, 1):
            print(f"--- SAMPLE {i} " + "-" * 48)
            print(f"USER : Goal: {v['goal']}")
            print(f"PLAN : {v['plan']}")
            for t in v["tasks"]:
                print(f"  [{t['role']} p{t['priority']}] {t['title']}")
                print(f"      {t['description'][:150]}")
            print()
        if not valid:
            print("  RAW OUTPUT (first 1500 chars for debugging):")
            print(raw[:1500])
        print("Smoke test complete. Nothing written. Re-run without --smoke for the full set.")
        return

    # ── Full run ──────────────────────────────────────────────────────────────
    plan = build_batch_plan(rng, args.batches, args.per_batch)

    done_batches = set()
    seen = set()
    if args.resume and PROGRESS.exists():
        prog = json.loads(PROGRESS.read_text(encoding="utf-8"))
        done_batches = set(prog.get("done", []))
        if OUT_WORK.exists():
            with open(OUT_WORK, encoding="utf-8") as f:
                for line in f:
                    if line.strip():
                        ex = json.loads(line)
                        seen.add(norm_goal(ex["messages"][1]["content"][6:]))
        print(f"  RESUME: {len(done_batches)} batches done, {len(seen)} examples kept\n")
    elif OUT_WORK.exists():
        OUT_WORK.unlink()

    print(f"  Target: {args.batches} batches x {args.per_batch} = {args.batches*args.per_batch}\n")

    total_written = len(seen)
    t0 = time.time()
    out_f = open(OUT_WORK, "a", encoding="utf-8")

    for spec in plan:
        if spec["idx"] in done_batches:
            continue
        spec["n"] = args.per_batch
        tb = time.time()
        try:
            raw = run_codex(exe, batch_prompt(spec), args.timeout)
        except subprocess.TimeoutExpired:
            print(f"  [batch {spec['idx']:3d}] TIMEOUT ({args.timeout}s) -- skipping")
            continue

        parsed = parse_lines(raw)
        kept = 0
        for o in parsed:
            v = validate_and_fix(o)
            if not v:
                continue
            ng = norm_goal(v["goal"])
            if ng in seen:
                continue
            seen.add(ng)
            out_f.write(json.dumps(wrap(v, system_prompt, spec), ensure_ascii=False) + "\n")
            kept += 1
        out_f.flush()
        total_written += kept
        done_batches.add(spec["idx"])
        PROGRESS.write_text(json.dumps({"done": sorted(done_batches)}), encoding="utf-8")

        dt = time.time() - tb
        print(f"  [batch {spec['idx']:3d}/{args.batches}] {spec['language']:<22} "
              f"{spec['task_type']:<12} +{kept:2d}/{len(parsed):2d}  "
              f"total={total_written:4d}  {dt:4.0f}s")

    out_f.close()
    elapsed = time.time() - t0
    print(f"\nDone. {total_written} gold examples -> {OUT_WORK.name}")
    print(f"Elapsed: {elapsed/60:.1f} min")
    print(f"\nNext: validate count, then rename to codex[api].synthetic.boss.{total_written}.jsonl")
    print("      and merge with merged[mixed].normalized.boss.2244.jsonl for training.")


if __name__ == "__main__":
    main()
