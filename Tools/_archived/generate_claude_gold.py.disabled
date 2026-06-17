#!/usr/bin/env python3
"""
generate_claude_gold.py
Generates GOLD-STANDARD boss-decomposition training examples via Claude API.

Same goal-decomposition format as generate_codex_gold.py but uses the Anthropic
SDK directly — no Codex CLI dependency, no quota gap risk, and higher throughput
capped by Anthropic rate limits rather than local Ollama context slots.

Target: 72 batches x 25 = 1,800 examples (supplement codex_gold.work.jsonl)
Combined with existing 2,244 normalized + 217 Codex → ~4,261 total for lora_v2.

Usage:
  python tools/generate_claude_gold.py --smoke          # 1 small batch, no write
  python tools/generate_claude_gold.py                  # full run
  python tools/generate_claude_gold.py --batches 72 --per-batch 25
  python tools/generate_claude_gold.py --resume         # continue interrupted run
  python tools/generate_claude_gold.py --model claude-opus-4-8  # max quality

Requires: pip install anthropic
          ANTHROPIC_API_KEY env var (or .env file in repo root)
"""

import argparse
import json
import os
import random
import re
import sys
import time
from pathlib import Path

ROOT   = Path(__file__).resolve().parent.parent
DS_DIR = ROOT / "training_pit" / "datasets"
CANON  = DS_DIR / "mainpc[24gb].captured.boss.1384.jsonl"

OUT_WORK = DS_DIR / "claude_gold.work.jsonl"
PROGRESS = DS_DIR / "claude_gold.progress.json"

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
FILENAME_RE = re.compile(r"[\w\-]+\.[A-Za-z0-9]{1,6}\b")

DEFAULT_MODEL = "claude-sonnet-4-6"

# Balanced-realistic distribution — matches generate_codex_gold.py
LANG_BATCHES = {
    "Python":                 22,
    "C#":                     18,
    "JavaScript/TypeScript":  10,
    "SQL":                     6,
    "Rust":                    3,
    "Go":                      3,
    "PowerShell":              3,
    "Bash":                    2,
    "Java":                    2,
    "HTML/CSS/JavaScript":     3,
}  # sum = 72

TASK_TYPES = [
    ("feature",     0.40),
    ("refactor",    0.15),
    ("bugfix",      0.15),
    ("tests",       0.10),
    ("ui",          0.10),
    ("docs",        0.05),
    ("integration", 0.05),
]

NO_UI_LANGS = {"SQL", "Bash", "PowerShell"}


# ── API client ────────────────────────────────────────────────────────────────

def get_client(model: str):
    try:
        import anthropic
    except ImportError:
        print("ERROR: anthropic SDK not installed. Run: pip install anthropic", file=sys.stderr)
        sys.exit(3)

    api_key = os.environ.get("ANTHROPIC_API_KEY", "")
    if not api_key:
        env_file = ROOT / ".env"
        if env_file.exists():
            for line in env_file.read_text().splitlines():
                k, _, v = line.partition("=")
                if k.strip() == "ANTHROPIC_API_KEY":
                    api_key = v.strip().strip('"').strip("'")
                    break
    if not api_key:
        print("ERROR: ANTHROPIC_API_KEY not set.", file=sys.stderr)
        sys.exit(3)

    client = anthropic.Anthropic(api_key=api_key)
    # Smoke-test the connection with a lightweight call
    try:
        client.models.list()
    except Exception:
        pass  # Non-fatal; proceed and let the first generate call surface errors

    return client, anthropic


def call_claude(client, anthropic_mod, model: str, prompt: str, max_retries: int = 5) -> str:
    for attempt in range(max_retries):
        try:
            msg = client.messages.create(
                model=model,
                max_tokens=8192,
                temperature=0.9,
                messages=[{"role": "user", "content": prompt}],
            )
            return msg.content[0].text if msg.content else ""
        except Exception as e:
            name = type(e).__name__
            if "RateLimit" in name or "overloaded" in str(e).lower():
                wait = 30 * (2 ** attempt)
                print(f"  [rate-limit] waiting {wait}s (attempt {attempt+1}/{max_retries})...")
                time.sleep(wait)
            elif "APIStatusError" in name and "529" in str(e):
                wait = 60 * (attempt + 1)
                print(f"  [overloaded] waiting {wait}s...")
                time.sleep(wait)
            else:
                print(f"  [api-error] {e} — skipping batch")
                return ""
    return ""


# ── Prompt / validation (mirrors generate_codex_gold.py) ─────────────────────

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
    total = sum(LANG_BATCHES.values())
    langs = []
    for lang, count in LANG_BATCHES.items():
        scaled = max(1, round(count * batches / total))
        langs.extend([lang] * scaled)
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
nothing else. Do NOT explore any repository. Pure text generation only.

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


def parse_lines(raw: str) -> list[dict]:
    raw = re.sub(r"```(?:json)?", "", raw, flags=re.IGNORECASE).strip()
    results = []
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
        if role != "TESTER" and not FILENAME_RE.search(title):
            return None
        priority = 1 if role == "RESEARCHER" else 2
        fixed_tasks.append({"role": role, "priority": priority,
                            "title": title, "description": desc})

    researchers = [t for t in fixed_tasks if t["role"] == "RESEARCHER"]
    if len(researchers) > 1:
        return None

    return {"goal": goal, "plan": plan, "tasks": fixed_tasks}


def wrap(example: dict, system_prompt: str, spec: dict, model: str) -> dict:
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
            "source":                  "claude_synthetic",
            "quality":                 "gold",
            "contains_sensitive_data": False,
            "base_model_target":       "theorc-boss:gemma4",
            "created_by":              "generate_claude_gold.py",
            "language":                spec["language"],
            "style":                   "multi_sentence_typos",
            "model":                   model,
            "notes": f"Claude gold, batch {spec['idx']}, theme {spec['task_type']}/{spec['language']}.",
        },
    }


def norm_goal(goal: str) -> str:
    return re.sub(r"\s+", " ", goal.lower().strip())


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Generate Claude API gold boss-decomposition data")
    ap.add_argument("--smoke",     action="store_true", help="1 small batch, print samples, no write")
    ap.add_argument("--batches",   type=int, default=72,  help="Number of batches (default 72)")
    ap.add_argument("--per-batch", type=int, default=25,  help="Examples per batch (default 25)")
    ap.add_argument("--model",     default=DEFAULT_MODEL, help=f"Anthropic model (default {DEFAULT_MODEL})")
    ap.add_argument("--resume",    action="store_true", help="Continue an interrupted run")
    ap.add_argument("--seed",      type=int, default=99)
    args = ap.parse_args()

    client, anthropic_mod = get_client(args.model)
    system_prompt = load_system_prompt()
    rng = random.Random(args.seed)

    print("=" * 64)
    print("  TheOrc -- Claude Gold Generator")
    print("=" * 64)
    print(f"  model     : {args.model}")
    print(f"  sys prompt: {len(system_prompt)} chars")

    if args.smoke:
        spec = {"idx": 0, "language": "Python", "task_type": "feature", "n": 4}
        print(f"  SMOKE: 1 batch, {spec['n']} examples, {spec['language']}/{spec['task_type']}\n")
        raw  = call_claude(client, anthropic_mod, args.model, batch_prompt(spec))
        parsed = parse_lines(raw)
        valid = [v for v in (validate_and_fix(o) for o in parsed) if v]
        print(f"  API returned {len(parsed)} line(s); {len(valid)} passed validation.\n")
        for i, v in enumerate(valid, 1):
            print(f"--- SAMPLE {i} " + "-" * 48)
            print(f"USER : Goal: {v['goal']}")
            print(f"PLAN : {v['plan']}")
            for t in v["tasks"]:
                print(f"  [{t['role']} p{t['priority']}] {t['title']}")
                print(f"      {t['description'][:150]}")
            print()
        if not valid:
            print("  RAW (first 1500 chars):")
            print(raw[:1500])
        print("Smoke test done. Nothing written.")
        return

    # ── Full run ──────────────────────────────────────────────────────────────
    plan = build_batch_plan(rng, args.batches, args.per_batch)

    done_batches: set[int] = set()
    seen: set[str] = set()

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

    print(f"  Target: {args.batches} batches x {args.per_batch} = {args.batches * args.per_batch}\n")

    total_written = len(seen)
    t0 = time.time()

    with open(OUT_WORK, "a", encoding="utf-8") as out_f:
        for spec in plan:
            if spec["idx"] in done_batches:
                print(f"  [batch {spec['idx']:3d}/{args.batches}] skip (already done)")
                continue
            spec["n"] = args.per_batch
            tb = time.time()

            raw    = call_claude(client, anthropic_mod, args.model, batch_prompt(spec))
            parsed = parse_lines(raw)
            kept   = 0

            for o in parsed:
                v = validate_and_fix(o)
                if not v:
                    continue
                ng = norm_goal(v["goal"])
                if ng in seen:
                    continue
                seen.add(ng)
                out_f.write(json.dumps(wrap(v, system_prompt, spec, args.model),
                                       ensure_ascii=False) + "\n")
                kept += 1

            out_f.flush()
            total_written += kept
            done_batches.add(spec["idx"])
            PROGRESS.write_text(json.dumps({"done": sorted(done_batches)}), encoding="utf-8")

            dt = time.time() - tb
            print(f"  [batch {spec['idx']:3d}/{args.batches}] {spec['language']:<22} "
                  f"{spec['task_type']:<12} +{kept:2d}/{len(parsed):2d}  "
                  f"total={total_written:4d}  {dt:4.0f}s")

            # Polite pause between batches to avoid rate-limit bursts
            if kept > 0:
                time.sleep(2)

    elapsed = time.time() - t0
    print(f"\nDone. {total_written} gold examples -> {OUT_WORK.name}")
    print(f"Elapsed: {elapsed / 60:.1f} min")
    print(f"\nNext: python tools/finalize_training_set.py")
    print(f"      then rename to claude[api].synthetic.boss.{total_written}.jsonl")


if __name__ == "__main__":
    main()
