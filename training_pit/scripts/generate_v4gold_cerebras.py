#!/usr/bin/env python3
"""
generate_v4gold_cerebras.py — Cerebras-powered v4gold boss-plan dataset generator.

Targets the files_named gap found in ORC ACADEMY v3 A/B eval (FT 65/87 vs base 74/87).
Every CODER/UIDEVELOPER task title MUST name an explicit output file — plans that omit
filenames are rejected automatically (same rule as generate_v4gold.py, but Cerebras-fast).

On completion, merges with any existing train_v4gold.jsonl + eval_v4gold.jsonl, shuffles,
and writes a clean 90/10 train/eval split.  Also writes gen_progress.json for UI polling.

Usage:
    $env:CEREBRAS_API_KEY = "csk-..."
    python training_pit/scripts/generate_v4gold_cerebras.py
    python training_pit/scripts/generate_v4gold_cerebras.py --batches 40 --per-batch 20
    python training_pit/scripts/generate_v4gold_cerebras.py --smoke
    python training_pit/scripts/generate_v4gold_cerebras.py --resume

Requires: pip install openai
"""

import argparse
import json
import os
import random
import re
import sys
import time
import uuid
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

# ── Paths ──────────────────────────────────────────────────────────────────────

REPO_ROOT     = Path(__file__).resolve().parent.parent.parent
DATASETS_DIR  = REPO_ROOT / "training_pit" / "datasets"
OUTPUTS_DIR   = REPO_ROOT / "training_pit" / "outputs"
GEN_OUT_DIR   = OUTPUTS_DIR / "gen_v4gold_cerebras"
WORK_FILE     = GEN_OUT_DIR / "work.jsonl"
PROGRESS_FILE = GEN_OUT_DIR / "gen_progress.json"
PROGRESS_TMP  = GEN_OUT_DIR / "gen_progress.tmp"

TRAIN_OUT = DATASETS_DIR / "train_v4gold.jsonl"
EVAL_OUT  = DATASETS_DIR / "eval_v4gold.jsonl"
META_OUT  = DATASETS_DIR / "v4gold.meta.json"

CEREBRAS_BASE_URL = "https://api.cerebras.ai/v1"
DEFAULT_MODEL     = "gpt-oss-120b"
EVAL_SPLIT        = 0.10

# ── BOSS_SYSTEM_PROMPT ─────────────────────────────────────────────────────────
# Keep in sync with SwarmSession.BossDecomposeSystemPrompt and generate_v4gold.py

BOSS_SYSTEM_PROMPT = (
    "You are TheOrc — the Orchestrator of a multi-agent AI coding swarm.\n"
    "You direct four specialist minions:\n"
    "  • RESEARCHER  — investigates APIs, libraries, docs; does NOT write production code\n"
    "  • CODER       — writes full implementation code using the researcher's findings\n"
    "  • UIDEVELOPER — writes UI code (XAML, WPF, HTML/CSS) and styling\n"
    "  • TESTER      — runs existing code, executes tests, checks syntax, reports results; does NOT write or modify files\n"
    "\n"
    "Given a user's coding goal, break it into 2–4 concurrent subtasks.\n"
    "Assign each subtask to the best-fit minion role.\n"
    "\n"
    "Rules:\n"
    "- RESEARCHER tasks always get priority 1 (they run first, alone)\n"
    "- CODER, UIDEVELOPER, and TESTER tasks get priority 2 (run concurrently after research)\n"
    "- If no research is needed, skip RESEARCHER and assign CODER/UIDEVELOPER/TESTER tasks directly\n"
    "- TESTER tasks verify code that already exists in the workspace — they do NOT receive output from CODER tasks in the same run\n"
    "- Descriptions must be self-contained — minions cannot ask follow-up questions\n"
    "- Maximum 4 tasks total: up to 1 RESEARCHER + up to 3 CODER/UIDEVELOPER/TESTER\n"
    "- Prefer 3 priority-2 tasks when the goal has distinct implementation concerns\n"
    "\n"
    "FILENAME RULE — task titles MUST name the output file(s):\n"
    '- Good title: "Write scraper.py and ollama_client.py"\n'
    '- Good title: "Build main.py Tkinter UI"\n'
    '- Bad title:  "Implement article fetcher" (no filename — workers won\'t know what to name the file)\n'
    "\n"
    "API CONTRACT RULE — when worker A produces a module that worker B imports:\n"
    "- Decide the EXACT function/class names ONCE and use the same names in BOTH task descriptions.\n"
    "- Example: if CODER writes scraper.py with function fetch_article_text(url), then the UIDEVELOPER task MUST say "
    '"from scraper import fetch_article_text" — not a different name.\n'
    "- This is non-negotiable: mismatched names cause import errors at runtime.\n"
    "\n"
    "Respond with ONLY valid JSON — no markdown fences, no preamble, no trailing text.\n"
    "String values MUST NOT contain literal newlines — use \\\\n inside strings if needed.\n"
    "{\n"
    '  "plan": "one-sentence overall approach",\n'
    '  "tasks": [\n'
    "    {\n"
    '      "role": "RESEARCHER",\n'
    '      "priority": 1,\n'
    '      "title": "Short descriptive title",\n'
    '      "description": "Detailed, self-contained instructions."\n'
    "    }\n"
    "  ]\n"
    "}"
)

# ── Batch themes ───────────────────────────────────────────────────────────────
# 40 batches, 20 per batch = 800 target.  Balanced across Python, C#, Avalonia, multi-file.

BATCHES_SPEC = (
    # (language_tag, task_type, count)
    ("Python",    "feature",     8),
    ("Python",    "bugfix",      4),
    ("Python",    "refactor",    3),
    ("Python",    "integration", 3),
    ("C#",        "feature",     6),
    ("C#",        "bugfix",      4),
    ("C#",        "refactor",    3),
    ("C#",        "integration", 2),
    ("Avalonia",  "feature",     3),
    ("Avalonia",  "bugfix",      2),
    ("Multi",     "feature",     2),
)  # total = 40

def _expand_batches() -> list[dict]:
    items = []
    idx = 0
    for lang, ttype, cnt in BATCHES_SPEC:
        for _ in range(cnt):
            items.append({"idx": idx, "language": lang, "task_type": ttype})
            idx += 1
    return items


# ── Prompt builder ────────────────────────────────────────────────────────────

_TASK_GUIDANCE = {
    "feature": (
        "Goals add new functionality. Include RESEARCHER only for obscure third-party APIs. "
        "Always end with a TESTER task. Typical: 2–4 tasks."
    ),
    "bugfix": (
        "Goals describe a specific broken behaviour (crash, wrong output, edge-case failure). "
        "CODER diagnoses and patches the file(s); TESTER verifies the fix. "
        "Almost never needs RESEARCHER. Typical: 2 tasks (CODER + TESTER)."
    ),
    "refactor": (
        "Goals restructure existing code without changing external behaviour. "
        "CODER refactors; TESTER verifies behaviour is preserved. "
        "Typical: 2–3 tasks (CODER + optional CODER + TESTER)."
    ),
    "integration": (
        "Goals connect two or more systems: API client, webhook, message queue, DB connector. "
        "May need RESEARCHER for obscure third-party APIs. Typical: 2–3 tasks."
    ),
}

_LANG_CONTEXT = {
    "Python":   "All goals use Python (stdlib + popular packages like requests, click, sqlalchemy, pytest).",
    "C#":       "All goals use C# (.NET 10, ASP.NET Core, EF Core, xUnit). Files end in .cs or .csproj.",
    "Avalonia": "All goals use Avalonia UI (C# .axaml + .axaml.cs files). No WPF. No WinForms.",
    "Multi":    "Goals span two languages or involve a Python script calling a C# service (or vice versa).",
}


def batch_prompt(spec: dict, per_batch: int) -> str:
    lang  = spec["language"]
    ttype = spec["task_type"]
    guidance = _TASK_GUIDANCE.get(ttype, _TASK_GUIDANCE["feature"])
    lang_ctx  = _LANG_CONTEXT.get(lang, "")
    return f"""\
Generate exactly {per_batch} JSONL training examples for TheOrc, a local AI orchestrator.

TheOrc takes a developer's coding goal and decomposes it into 2–4 concurrent subtasks.
Roles: RESEARCHER (priority 1, investigate only), CODER (priority 2, write implementation),
UIDEVELOPER (priority 2, write UI code), TESTER (priority 2, run tests — NO file writes).

OUTPUT FORMAT: one minified JSON object per line, no markdown, no preamble:
{{"goal":"<developer request>","plan":"<1-3 sentences>","tasks":[{{"role":"...","priority":1 or 2,"title":"...","description":"..."}}]}}

LANGUAGE: {lang}. {lang_ctx}
TASK TYPE: {ttype}. {guidance}

CRITICAL FILENAME RULE — every CODER and UIDEVELOPER task title MUST name the exact output
file(s) it creates/modifies, e.g. "Write cache_service.cs and ICacheService.cs" or
"Build monitor.py CLI". A title like "Implement the cache layer" is INVALID — it has no
filename. TESTER task titles do NOT need a filename (they run files, not create them).

GOAL FIELD: 2–5 sentences, casual developer voice. Reference concrete file names, function
names, library names. Vary the project domain widely across the {per_batch} examples.

DECOMPOSITION (plan + tasks): must be flawless gold-standard training labels.
- If one CODER writes a module another imports, use the EXACT same function/class names in both descriptions.
- Descriptions: self-contained, 2–4 sentences, no ambiguity.
- No invented external services. No RESEARCHER for well-known frameworks.

Output ONLY the {per_batch} JSONL lines. Nothing else."""


# ── Validation ────────────────────────────────────────────────────────────────

_VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
_IMPL_ROLES  = {"CODER", "UIDEVELOPER"}
_FILE_RE     = re.compile(
    r'\b[\w\-/]+\.(py|cs|axaml|xaml|ts|tsx|js|html|css|json|sql|md|sh|ps1|rs|go|java|yaml|yml|txt|log|csv)\b',
    re.IGNORECASE,
)
_TESTER_WRITE_KW = re.compile(r'\b(write|create|modify|implement|add|generate)\b', re.IGNORECASE)


def _parse_lines(raw: str) -> list[dict]:
    raw = re.sub(r"```(?:json)?", "", raw, flags=re.IGNORECASE).strip()
    # Try whole-blob JSON array first
    try:
        blob = json.loads(raw)
        if isinstance(blob, list):
            return [x for x in blob if isinstance(x, dict)]
    except Exception:
        pass
    results = []
    for line in raw.splitlines():
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


def _validate(obj: dict) -> tuple[dict | None, str]:
    goal  = (obj.get("goal")  or "").strip()
    plan  = (obj.get("plan")  or "").strip()
    tasks = obj.get("tasks")

    if not goal or len(goal) < 60:
        return None, "goal_too_short"
    if not plan:
        return None, "no_plan"
    if not isinstance(tasks, list) or not (1 <= len(tasks) <= 4):
        return None, f"task_count:{len(tasks) if isinstance(tasks, list) else '?'}"

    fixed = []
    for t in tasks:
        if not isinstance(t, dict):
            return None, "task_not_dict"
        role  = (t.get("role") or "").strip().upper()
        title = (t.get("title") or "").strip()
        desc  = (t.get("description") or "").strip()
        if role not in _VALID_ROLES:
            return None, f"bad_role:{role!r}"
        if not title or len(desc) < 20:
            return None, "title_or_desc_empty"

        # ── CORE CHECK: CODER/UIDEVELOPER title must name an output file ──
        if role in _IMPL_ROLES and not _FILE_RE.search(title):
            return None, f"files_named_missing:{title!r}"

        # tester-write poison: TESTER describing file creation
        if role == "TESTER" and _FILE_RE.search(desc) and _TESTER_WRITE_KW.search(desc):
            return None, "tester_write_poison"

        priority = 1 if role == "RESEARCHER" else 2
        fixed.append({"role": role, "priority": priority, "title": title, "description": desc})

    if sum(1 for t in fixed if t["role"] == "RESEARCHER") > 1:
        return None, "multiple_researchers"

    return {"goal": goal, "plan": plan, "tasks": fixed}, "ok"


def _wrap(obj: dict, spec: dict, model: str) -> dict:
    assistant = json.dumps(
        {"plan": obj["plan"], "tasks": obj["tasks"]},
        ensure_ascii=False, separators=(",", ":"),
    )
    return {
        "messages": [
            {"role": "system",    "content": BOSS_SYSTEM_PROMPT},
            {"role": "user",      "content": f"Goal: {obj['goal']}"},
            {"role": "assistant", "content": assistant},
        ],
        "metadata": {
            "category":                "boss_planning",
            "task_type":               spec["task_type"],
            "source":                  "cerebras_synthetic",
            "quality":                 "gold",
            "contains_sensitive_data": False,
            "base_model_target":       "gemma4:12b",
            "created_by":              "generate_v4gold_cerebras.py",
            "language":                spec["language"],
            "model":                   model,
            "example_id":              str(uuid.uuid4()),
            "notes":                   f"v4gold cerebras batch {spec['idx']} {spec['task_type']}/{spec['language']}",
        },
    }


# ── Progress ──────────────────────────────────────────────────────────────────

def _write_progress(status: str, generated: int, rejected: int, target: int,
                    last_goal: str = "") -> None:
    payload = json.dumps({
        "status":    status,
        "generated": generated,
        "rejected":  rejected,
        "target":    target,
        "pid":       os.getpid(),
        "last_goal": last_goal[:120],
    }, ensure_ascii=False)
    PROGRESS_TMP.write_text(payload, encoding="utf-8")
    PROGRESS_TMP.replace(PROGRESS_FILE)


# ── Cerebras client ───────────────────────────────────────────────────────────

def _get_client(model: str):
    try:
        from openai import OpenAI
    except ImportError:
        print("ERROR: openai SDK not installed — run: pip install openai", file=sys.stderr)
        sys.exit(3)
    api_key = os.environ.get("CEREBRAS_API_KEY", "").strip()
    if not api_key:
        print("ERROR: CEREBRAS_API_KEY env var not set.", file=sys.stderr)
        sys.exit(3)
    return OpenAI(api_key=api_key, base_url=CEREBRAS_BASE_URL)


def _call(client, model: str, prompt: str, max_retries: int = 5) -> str:
    for attempt in range(max_retries):
        try:
            resp = client.chat.completions.create(
                model=model,
                messages=[{"role": "user", "content": prompt}],
                max_completion_tokens=8192,
                temperature=0.85,
            )
            return resp.choices[0].message.content or ""
        except Exception as exc:
            msg = str(exc)
            if "rate" in msg.lower() or "429" in msg:
                wait = 20 * (2 ** attempt)
                print(f"  [rate-limit] sleeping {wait}s …", flush=True)
                time.sleep(wait)
            elif "503" in msg or "overload" in msg.lower():
                wait = 30 * (attempt + 1)
                print(f"  [overloaded] sleeping {wait}s …", flush=True)
                time.sleep(wait)
            else:
                print(f"  [api-error attempt {attempt+1}] {type(exc).__name__}: {msg[:120]}", flush=True)
                if attempt == max_retries - 1:
                    return ""
                time.sleep(8)
    return ""


# ── Finalize: merge + split ───────────────────────────────────────────────────

def _finalize(rng: random.Random, new_examples: list[dict]) -> tuple[int, int]:
    """Merge work examples with any existing v4gold JSONL, shuffle, split 90/10."""
    all_examples: list[dict] = []

    # Load existing train + eval if present (keep deduplication by example_id)
    seen_ids: set[str] = set()
    for path in (TRAIN_OUT, EVAL_OUT):
        if not path.exists():
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                ex = json.loads(line)
                eid = ex.get("metadata", {}).get("example_id", "")
                if eid and eid in seen_ids:
                    continue
                if eid:
                    seen_ids.add(eid)
                all_examples.append(ex)
            except Exception:
                pass

    # Merge new examples (skip dupes by example_id)
    added = 0
    for ex in new_examples:
        eid = ex.get("metadata", {}).get("example_id", "")
        if eid and eid in seen_ids:
            continue
        if eid:
            seen_ids.add(eid)
        all_examples.append(ex)
        added += 1

    rng.shuffle(all_examples)
    n_eval  = max(1, int(len(all_examples) * EVAL_SPLIT))
    eval_ex  = all_examples[:n_eval]
    train_ex = all_examples[n_eval:]

    DATASETS_DIR.mkdir(parents=True, exist_ok=True)
    TRAIN_OUT.write_text(
        "\n".join(json.dumps(e, ensure_ascii=False) for e in train_ex) + "\n",
        encoding="utf-8",
    )
    EVAL_OUT.write_text(
        "\n".join(json.dumps(e, ensure_ascii=False) for e in eval_ex) + "\n",
        encoding="utf-8",
    )
    return len(train_ex), len(eval_ex)


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--smoke",     action="store_true", help="1 batch, print samples, no write")
    ap.add_argument("--resume",    action="store_true", help="Continue interrupted run")
    ap.add_argument("--batches",   type=int, default=40,            help="Number of batches (default 40)")
    ap.add_argument("--per-batch", type=int, default=20,            help="Examples per batch (default 20)")
    ap.add_argument("--model",     default=DEFAULT_MODEL,           help=f"Cerebras model (default {DEFAULT_MODEL})")
    ap.add_argument("--seed",      type=int, default=42)
    args = ap.parse_args()

    rng    = random.Random(args.seed)
    client = _get_client(args.model)
    target = args.batches * args.per_batch

    GEN_OUT_DIR.mkdir(parents=True, exist_ok=True)

    all_batches = _expand_batches()
    # Scale to requested batch count
    while len(all_batches) < args.batches:
        all_batches.extend(_expand_batches())
    all_batches = all_batches[:args.batches]
    rng.shuffle(all_batches)

    # ── Smoke test ────────────────────────────────────────────────────────────
    if args.smoke:
        spec  = {"idx": 0, "language": "Python", "task_type": "feature"}
        raw   = _call(client, args.model, batch_prompt(spec, 4))
        lines = _parse_lines(raw)
        valid = [(v, r) for v, r in (_validate(o) for o in lines)]
        good  = [(v, r) for v, r in valid if v is not None]
        print(f"API returned {len(lines)} lines; {len(good)} passed validation.\n")
        for i, (v, _) in enumerate(good, 1):
            print(f"--- SAMPLE {i} " + "-" * 48)
            print(f"GOAL : {v['goal'][:120]}")
            print(f"PLAN : {v['plan']}")
            for t in v["tasks"]:
                print(f"  [{t['role']} p{t['priority']}] {t['title']}")
            print()
        if not good:
            print("RAW (first 2000 chars):\n", raw[:2000])
        print("Smoke done. Nothing written.")
        return

    # ── Resume ────────────────────────────────────────────────────────────────
    done_set: set[int] = set()
    new_examples: list[dict] = []

    if args.resume and WORK_FILE.exists():
        for line in WORK_FILE.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                ex = json.loads(line)
                bidx = ex.get("metadata", {}).get("batch_idx")
                if bidx is not None:
                    done_set.add(int(bidx))
                new_examples.append(ex)
            except Exception:
                pass
        print(f"Resume: {len(done_set)} batches done, {len(new_examples)} examples kept.")

    # Tag batch_idx in metadata for resume tracking
    for b in all_batches:
        b["batch_idx"] = b["idx"]

    total_gen = len(new_examples)
    total_rej = 0
    t0 = time.time()

    print("=" * 64)
    print("  TheOrc — Cerebras v4gold Generator")
    print("=" * 64)
    print(f"  model    : {args.model}")
    print(f"  batches  : {args.batches} × {args.per_batch} = {target} target")
    print(f"  key      : v4gold")
    print(f"  out      : {TRAIN_OUT.name} + {EVAL_OUT.name}")
    est_min = int(args.batches * 12 / 60)
    print(f"  est.     : ~{est_min} min (5 req/min rate limit)\n")

    _write_progress("starting", total_gen, total_rej, target)

    with WORK_FILE.open("a", encoding="utf-8") as work_fh:
        for spec in all_batches:
            if spec["idx"] in done_set:
                continue

            _write_progress("generating", total_gen, total_rej, target,
                            f"{spec['language']}/{spec['task_type']} batch {spec['idx']}")

            tb  = time.time()
            raw = _call(client, args.model, batch_prompt(spec, args.per_batch))
            lines = _parse_lines(raw)

            kept = 0
            for obj in lines:
                validated, reason = _validate(obj)
                if validated is None:
                    total_rej += 1
                    continue
                ex = _wrap(validated, spec, args.model)
                ex["metadata"]["batch_idx"] = spec["idx"]
                work_fh.write(json.dumps(ex, ensure_ascii=False) + "\n")
                new_examples.append(ex)
                total_gen += 1
                kept += 1

            work_fh.flush()
            done_set.add(spec["idx"])

            dt = time.time() - tb
            print(f"  [batch {spec['idx']:3d}/{args.batches}] {spec['language']:<10} "
                  f"{spec['task_type']:<12} +{kept:2d}/{len(lines):2d}  "
                  f"total={total_gen:4d}  {dt:4.0f}s", flush=True)

            _write_progress("generating", total_gen, total_rej, target)

            # Stay under 5 req/min
            elapsed = time.time() - tb
            if elapsed < 12:
                time.sleep(12 - elapsed)

    # ── Finalize ──────────────────────────────────────────────────────────────
    print(f"\nFinalizing … {len(new_examples)} new examples, merging with existing v4gold …")
    n_train, n_eval = _finalize(rng, new_examples)

    _write_progress("done", total_gen, total_rej, target)

    reject_rate = round(total_rej / max(1, total_gen + total_rej) * 100, 1)
    meta = {
        "description": f"v4gold — files_named gap fix. Cerebras {args.model}. {n_train} train / {n_eval} eval.",
        "purpose":     "boss_planning",
        "generator":   "generate_v4gold_cerebras.py",
        "model":       args.model,
        "created":     time.strftime("%Y-%m-%d"),
        "generated":   total_gen,
        "rejected":    total_rej,
        "reject_rate_pct": reject_rate,
        "train_count": n_train,
        "eval_count":  n_eval,
        "elapsed_min": round((time.time() - t0) / 60, 1),
    }
    META_OUT.write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")

    print(f"\n✓ Done in {meta['elapsed_min']:.1f} min")
    print(f"  train → {TRAIN_OUT}  ({n_train})")
    print(f"  eval  → {EVAL_OUT}   ({n_eval})")
    print(f"  meta  → {META_OUT}")
    print(f"  reject rate: {reject_rate}%  ({total_rej} rejected)")


if __name__ == "__main__":
    main()
