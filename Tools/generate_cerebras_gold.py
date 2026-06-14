#!/usr/bin/env python3
"""
generate_cerebras_gold.py
Generates GOLD-STANDARD boss-decomposition training examples via Cerebras API.

Uses the OpenAI-compatible Cerebras endpoint with qwen-3-32b — a 32B model
running at ~2,600 tokens/sec on Cerebras hardware. 72 batches x 25 examples
completes in ~10-15 minutes (not overnight). Free tier: 1M tokens/day.

Usage:
  # Set API key first:
  $env:CEREBRAS_API_KEY = "your-key"

  python tools/generate_cerebras_gold.py --smoke          # 1 batch, no write
  python tools/generate_cerebras_gold.py                  # full run (72x25=1800)
  python tools/generate_cerebras_gold.py --resume         # continue interrupted run
  python tools/generate_cerebras_gold.py --model qwen-3-32b --batches 72

Available Cerebras models (as of 2026-06):
  gpt-oss-120b   (best quality, recommended — 120B params)
  zai-glm-4.7    (fastest, lower quality — 4.7B params, not recommended)

Requires: pip install openai
          CEREBRAS_API_KEY env var
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

OUT_WORK = DS_DIR / "cerebras_gold.work.jsonl"
PROGRESS = DS_DIR / "cerebras_gold.progress.json"

CEREBRAS_BASE_URL = "https://api.cerebras.ai/v1"
DEFAULT_MODEL     = "gpt-oss-120b"

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
FILENAME_RE = re.compile(r"[\w\-]+\.[A-Za-z0-9]{1,6}\b")

# Language distribution — heavily diversified vs captured data (which was ~100% C# TheOrc self-dev)
LANG_BATCHES = {
    "Python":                 14,
    "C#":                     10,
    "JavaScript/TypeScript":  10,
    "TypeScript (React/Next.js)": 6,
    "SQL":                     6,
    "Rust":                    6,
    "Go":                      6,
    "Java":                    5,
    "PowerShell":              3,
    "Bash":                    3,
    "HTML/CSS/JavaScript":     3,
}  # sum = 72

# Task-type distribution — fixes the critical gap: 2,244 examples are ALL feature_plan,
# zero bugfix/refactor/tests/docs. These types train the boss to handle real-world requests.
TASK_TYPES = [
    ("bugfix",      0.28),   # was 0% — boss has NEVER seen a bug report
    ("refactor",    0.22),   # was 0% — boss has NEVER seen a refactor request
    ("tests",       0.18),   # was 0% — trains boss to decompose test-writing goals
    ("feature",     0.15),   # already well-covered; keep some for diversity
    ("integration", 0.08),   # 0% in existing data
    ("docs",        0.05),   # 0% in existing data
    ("ui",          0.04),   # keep small, UIDEVELOPER already over-represented
]

NO_UI_LANGS = {"SQL", "Bash", "PowerShell"}


# ── API client ────────────────────────────────────────────────────────────────

def get_client(model: str):
    try:
        from openai import OpenAI
    except ImportError:
        print("ERROR: openai SDK not installed.  Run: pip install openai", file=sys.stderr)
        sys.exit(3)

    api_key = os.environ.get("CEREBRAS_API_KEY", "").strip()
    if not api_key:
        # Try .env in repo root
        env_file = ROOT / ".env"
        if env_file.exists():
            for line in env_file.read_text().splitlines():
                k, _, v = line.partition("=")
                if k.strip() == "CEREBRAS_API_KEY":
                    api_key = v.strip().strip('"').strip("'")
                    break
    if not api_key:
        print("ERROR: CEREBRAS_API_KEY not set.\n"
              "  PowerShell: $env:CEREBRAS_API_KEY = \"your-key\"", file=sys.stderr)
        sys.exit(3)

    client = OpenAI(api_key=api_key, base_url=CEREBRAS_BASE_URL)
    return client


def call_cerebras(client, model: str, prompt: str, max_retries: int = 5) -> str:
    for attempt in range(max_retries):
        try:
            resp = client.chat.completions.create(
                model=model,
                messages=[{"role": "user", "content": prompt}],
                max_completion_tokens=8192,
                temperature=0.9,
            )
            return resp.choices[0].message.content or ""
        except Exception as e:
            name  = type(e).__name__
            emsg  = str(e)
            if "rate" in emsg.lower() or "429" in emsg or "RateLimit" in name:
                wait = 15 * (2 ** attempt)
                print(f"  [rate-limit] waiting {wait}s (attempt {attempt+1}/{max_retries})...")
                time.sleep(wait)
            elif "503" in emsg or "overload" in emsg.lower():
                wait = 30 * (attempt + 1)
                print(f"  [overloaded] waiting {wait}s...")
                time.sleep(wait)
            elif "404" in emsg or "not_found" in emsg.lower():
                print(f"  [model-not-found] {emsg[:200]}")
                print(f"  Available models: run python -c \"from openai import OpenAI; import os; "
                      f"[print(m.id) for m in OpenAI(api_key=os.environ['CEREBRAS_API_KEY'], "
                      f"base_url='{CEREBRAS_BASE_URL}').models.list().data]\"")
                sys.exit(3)
            else:
                print(f"  [api-error attempt {attempt+1}] {name}: {emsg[:120]}")
                if attempt == max_retries - 1:
                    return ""
                time.sleep(5)
    return ""


# ── Prompt / validation (identical to generate_codex_gold.py) ─────────────────

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


def _task_type_guidance(task_type: str) -> str:
    return {
        "bugfix": (
            "Goals describe a specific broken behaviour (crash, wrong output, edge-case failure). "
            "Decomposition: CODER diagnoses and patches the affected file(s); TESTER writes a "
            "regression test that would have caught the bug. No RESEARCHER unless the fix requires "
            "an unfamiliar external API. Typical pattern: 2 tasks (CODER + TESTER)."
        ),
        "refactor": (
            "Goals describe restructuring existing code without changing external behaviour: "
            "extract method, rename, split module, reduce duplication, improve types. "
            "Decomposition: CODER refactors the target file(s); TESTER verifies behaviour is "
            "preserved (run existing tests or write new ones). Almost never needs RESEARCHER. "
            "Typical pattern: 2-3 tasks (CODER + TESTER, or CODER + CODER + TESTER for large refactors)."
        ),
        "tests": (
            "Goals are purely about adding test coverage to existing code — unit tests, integration "
            "tests, property-based tests. Decomposition: CODER writes the test file(s); optionally "
            "a second CODER writes test fixtures/helpers. No RESEARCHER. No UIDEVELOPER. "
            "Typical pattern: 2 tasks (CODER writes tests + TESTER runs and validates coverage)."
        ),
        "integration": (
            "Goals connect two or more existing systems: webhook handler, API client for a "
            "third-party service, message queue consumer, database connector. May warrant a "
            "RESEARCHER if the third-party API is obscure. CODER implements; TESTER writes "
            "integration tests with mocked external calls. Typical pattern: 2-3 tasks."
        ),
        "docs": (
            "Goals produce documentation: README, API reference, architecture diagrams, docstrings, "
            "migration guide. Decomposition: RESEARCHER gathers current behaviour from source; "
            "CODER writes the docs file(s). No TESTER (docs don't need testing). "
            "Typical pattern: 2 tasks (RESEARCHER + CODER)."
        ),
        "feature": (
            "Goals add new functionality to an existing codebase. Only include RESEARCHER if the "
            "feature requires a third-party library the team has never used. Always end with "
            "a TESTER task. Typical pattern: 2-4 tasks."
        ),
        "ui": (
            "Goals build or update user-facing components: forms, dashboards, modals, responsive "
            "layouts. UIDEVELOPER implements the component file(s); TESTER writes interaction/snapshot "
            "tests. No RESEARCHER for standard frameworks. Typical pattern: 2-3 tasks."
        ),
    }.get(task_type, "Follow the general rules above.")


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

TASK TYPE GUIDANCE for "{task_type}":
{_task_type_guidance(task_type)}

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
- RESEARCHER RULE (critical): include RESEARCHER ONLY for truly unfamiliar third-party \
APIs or libraries the team has not used before. Standard library calls, well-known \
frameworks (React, Django, ASP.NET, Spring, etc.), bugfixes in existing code, \
refactors, and test-writing NEVER need a RESEARCHER. Most examples (70%+) should \
have NO RESEARCHER — go straight to CODER/UIDEVELOPER/TESTER.
- TESTER RULE: every example involving new or changed code MUST include a TESTER \
task. Only docs-only or research-only tasks may omit TESTER.
- Task count: use 2 tasks for simple single-component work; 3 for typical features; \
4 only for large multi-component features with genuine parallelism.
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

    if len([t for t in fixed_tasks if t["role"] == "RESEARCHER"]) > 1:
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
            "source":                  "cerebras_synthetic",
            "quality":                 "gold",
            "contains_sensitive_data": False,
            "base_model_target":       "theorc-boss:gemma4",
            "created_by":              "generate_cerebras_gold.py",
            "language":                spec["language"],
            "style":                   "multi_sentence_typos",
            "model":                   model,
            "notes": f"Cerebras gold, batch {spec['idx']}, theme {spec['task_type']}/{spec['language']}.",
        },
    }


def norm_goal(goal: str) -> str:
    return re.sub(r"\s+", " ", goal.lower().strip())


# ── Main ──────────────────────────────────────────────────────────────────────

def _plan_to_lang_batches(plan: dict, total_batches: int) -> dict[str, int]:
    """Convert a TrainingPlan task_mix + languages into a LANG_BATCHES dict."""
    langs = plan.get("languages") or []
    if not langs:
        langs = list(LANG_BATCHES.keys())

    # Spread batches evenly across specified languages
    per_lang = max(1, total_batches // len(langs))
    result   = {lang: per_lang for lang in langs}

    # Top up to exactly total_batches using the first language
    shortfall = total_batches - sum(result.values())
    if shortfall > 0 and langs:
        result[langs[0]] = result[langs[0]] + shortfall
    return result


def _plan_to_task_types(plan: dict) -> list[tuple[str, float]]:
    """Convert a TrainingPlan task_mix dict into TASK_TYPES weight list."""
    mix = plan.get("task_mix") or {}
    if not mix:
        return TASK_TYPES
    total = sum(mix.values())
    if total <= 0:
        return TASK_TYPES
    return [(k, v / total) for k, v in mix.items()]


def main():
    ap = argparse.ArgumentParser(description="Generate Cerebras gold boss-decomposition data")
    ap.add_argument("--smoke",      action="store_true", help="1 small batch, print samples, no write")
    ap.add_argument("--batches",    type=int, default=72,  help="Number of batches (default 72)")
    ap.add_argument("--per-batch",  type=int, default=25,  help="Examples per batch (default 25)")
    ap.add_argument("--model",      default=DEFAULT_MODEL, help=f"Cerebras model (default {DEFAULT_MODEL})")
    ap.add_argument("--resume",     action="store_true",   help="Continue an interrupted run")
    ap.add_argument("--seed",       type=int, default=77)
    ap.add_argument("--plan-file",  default="",  help="Path to a TrainingPlan JSON — overrides LANG_BATCHES and TASK_TYPES")
    ap.add_argument("--out-file",   default="",  help="Override output .work.jsonl path (used by Pit Boss executor)")
    args = ap.parse_args()

    # ── Apply plan overrides ──────────────────────────────────────────────────
    global LANG_BATCHES, TASK_TYPES, OUT_WORK, PROGRESS

    if args.plan_file:
        plan_path = Path(args.plan_file)
        if not plan_path.exists():
            print(f"ERROR: --plan-file not found: {plan_path}", file=sys.stderr)
            sys.exit(3)
        plan_data = json.loads(plan_path.read_text(encoding="utf-8"))

        # Override target count from plan
        dataset_target = plan_data.get("dataset_target", args.batches * args.per_batch)
        # Recalculate batches to hit the target (keep per_batch default)
        args.batches  = max(1, round(dataset_target / args.per_batch))

        LANG_BATCHES  = _plan_to_lang_batches(plan_data, args.batches)
        TASK_TYPES    = _plan_to_task_types(plan_data)

        # Override model if plan specifies one
        if plan_data.get("dataset_gen_model"):
            args.model = plan_data["dataset_gen_model"]

        print(f"  plan-file : {plan_path.name}")
        print(f"  goal      : {plan_data.get('goal', '?')}")
        print(f"  target    : {dataset_target} examples ({args.batches} batches × {args.per_batch})")

    if args.out_file:
        OUT_WORK = Path(args.out_file)
        PROGRESS = OUT_WORK.with_suffix("").with_suffix(".progress.json")

    client        = get_client(args.model)
    system_prompt = load_system_prompt()
    rng           = random.Random(args.seed)

    print("=" * 64)
    print("  TheOrc -- Cerebras Gold Generator")
    print("=" * 64)
    print(f"  model     : {args.model}")
    print(f"  endpoint  : {CEREBRAS_BASE_URL}")
    print(f"  sys prompt: {len(system_prompt)} chars")

    if args.smoke:
        spec = {"idx": 0, "language": "C#", "task_type": "feature", "n": 4}
        print(f"  SMOKE: 1 batch, {spec['n']} examples, {spec['language']}/{spec['task_type']}\n")
        raw    = call_cerebras(client, args.model, batch_prompt(spec))
        parsed = parse_lines(raw)
        valid  = [v for v in (validate_and_fix(o) for o in parsed) if v]
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
            print("  RAW (first 2000 chars):")
            print(raw[:2000])
        print("Smoke test done. Nothing written.")
        return

    # ── Full run ──────────────────────────────────────────────────────────────
    plan = build_batch_plan(rng, args.batches, args.per_batch)

    done_batches: set[int] = set()
    seen:         set[str] = set()

    if args.resume and PROGRESS.exists():
        prog         = json.loads(PROGRESS.read_text(encoding="utf-8"))
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

    target = args.batches * args.per_batch
    print(f"  Target : {args.batches} batches x {args.per_batch} = {target}")
    print(f"  Est.   : ~{args.batches * 17 // 60}–{args.batches * 22 // 60} min at 5 RPM (12s/batch)\n")

    total_written = len(seen)
    t0 = time.time()

    with open(OUT_WORK, "a", encoding="utf-8") as out_f:
        for spec in plan:
            if spec["idx"] in done_batches:
                continue
            spec["n"] = args.per_batch
            tb = time.time()

            raw    = call_cerebras(client, args.model, batch_prompt(spec))
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

            # Free tier is 5 req/min — 12s between requests keeps us just under it
            time.sleep(12)

    elapsed = time.time() - t0
    print(f"\nDone. {total_written} gold examples -> {OUT_WORK.name}")
    print(f"Elapsed: {elapsed / 60:.1f} min")
    print(f"\nNext: python tools/finalize_training_set.py")
    print(f"      rename to cerebras[api].synthetic.boss.{total_written}.jsonl")


if __name__ == "__main__":
    main()
