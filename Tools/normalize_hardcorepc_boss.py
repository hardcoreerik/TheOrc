#!/usr/bin/env python3
"""
normalize_hardcorepc_boss.py
Normalizes HARDCOREPC overnight boss-plan data to canonical TheOrc format.

Schema gaps fixed:
  - System prompt  → replaced with canonical BossDecomposeSystemPrompt
  - User message   → "Goal: " prefix added
  - priority       → RESEARCHER=1 / others=2 (rule-based, no LLM needed)
  - plan           → generated via Ollama (one-sentence approach summary)
  - metadata       → updated to canonical structure, quality: "silver"

Usage:
  python tools/normalize_hardcorepc_boss.py
  python tools/normalize_hardcorepc_boss.py --merge     # also writes train_v2.jsonl
  python tools/normalize_hardcorepc_boss.py --no-plan   # skip Ollama plan gen (plan="")
  python tools/normalize_hardcorepc_boss.py --model qwen2.5-coder:14b
"""

import argparse
import json
import random
import sys
import time
import urllib.request
from pathlib import Path

# ── Paths ──────────────────────────────────────────────────────────────────────

ROOT      = Path(__file__).resolve().parent.parent
RAW_FILE  = ROOT / "training_pit" / "datasets" / "hardcorepc_raw_overnight.jsonl"
RAW_20    = ROOT / "training_pit" / "datasets" / "hardcorepc_raw_synthetic20.jsonl"
CANON     = ROOT / "training_pit" / "datasets" / "train_v1.jsonl"
OUT_FILE  = ROOT / "training_pit" / "datasets" / "hardcorepc_boss_normalized.jsonl"
MERGE_OUT = ROOT / "training_pit" / "datasets" / "train_v2.jsonl"

OLLAMA_URL  = "http://localhost:11434/api/generate"
DEFAULT_MODEL = "qwen2.5-coder:7b"

# ── Canonical system prompt — loaded from train_v1 so it's always in sync ─────

def load_canonical_system_prompt() -> str:
    with open(CANON, encoding="utf-8") as f:
        first = json.loads(f.readline())
    prompt = first["messages"][0]["content"]
    assert "plan" in prompt and "priority" in prompt, \
        "Canonical prompt looks wrong — check train_v1.jsonl"
    return prompt

# ── Ollama plan generation ─────────────────────────────────────────────────────

PLAN_PROMPT = """\
Given the coding goal and subtasks below, write ONE concise sentence (≤20 words) \
describing the overall technical approach. Output ONLY the sentence — no markdown, \
no preamble, no trailing punctuation.

Goal: {goal}
Tasks:
{tasks}

One-sentence approach:"""

def generate_plan(goal: str, tasks: list[dict], model: str) -> str:
    task_lines = "\n".join(
        f"  [{t.get('role','?')}] {t.get('title','')}" for t in tasks
    )
    prompt = PLAN_PROMPT.format(goal=goal, tasks=task_lines)

    payload = json.dumps({
        "model":   model,
        "prompt":  prompt,
        "stream":  False,
        "options": {"temperature": 0.1, "num_predict": 64},
    }).encode()

    try:
        req = urllib.request.Request(
            OLLAMA_URL, data=payload,
            headers={"Content-Type": "application/json"}, method="POST",
        )
        with urllib.request.urlopen(req, timeout=45) as resp:
            data = json.loads(resp.read())
        text = data.get("response", "").strip().split("\n")[0].strip(" .")
        return text if text else _fallback_plan(tasks)
    except Exception as e:
        print(f"\n    ⚠ Ollama plan gen failed ({e}) — using fallback", flush=True)
        return _fallback_plan(tasks)

def _fallback_plan(tasks: list[dict]) -> str:
    roles = list(dict.fromkeys(t.get("role", "?") for t in tasks))
    return f"Use {', '.join(r.lower() for r in roles)} pipeline to implement the goal."

# ── Priority assignment ────────────────────────────────────────────────────────

def assign_priority(role: str) -> int:
    return 1 if role.strip().upper() == "RESEARCHER" else 2

# ── Normalize one example ─────────────────────────────────────────────────────

def normalize(raw: dict, system_prompt: str, idx: int,
              model: str, gen_plan: bool) -> dict | None:
    try:
        msgs = raw.get("messages", [])
        user_msg = next((m for m in msgs if m["role"] == "user"), None)
        asst_msg = next((m for m in msgs if m["role"] == "assistant"), None)
        if not user_msg or not asst_msg:
            print(f"  ⚠ [{idx}] missing user or assistant — skipped")
            return None

        goal = user_msg["content"].strip()
        if goal.lower().startswith("goal:"):
            goal = goal[5:].strip()

        try:
            raw_plan = json.loads(asst_msg["content"])
        except json.JSONDecodeError:
            print(f"  ⚠ [{idx}] invalid JSON in assistant — skipped")
            return None

        tasks = raw_plan.get("tasks", [])
        if not tasks:
            print(f"  ⚠ [{idx}] no tasks — skipped")
            return None

        # ── Fix tasks ────────────────────────────────────────────────────────
        for t in tasks:
            t["priority"] = assign_priority(t.get("role", ""))
            # Ensure role is upper-case
            if "role" in t:
                t["role"] = t["role"].upper()

        # Re-order keys: role, priority, title, description
        ordered_tasks = [
            {k: t[k] for k in ("role", "priority", "title", "description") if k in t}
            for t in tasks
        ]

        # ── Plan sentence ─────────────────────────────────────────────────────
        if gen_plan:
            plan_text = generate_plan(goal, ordered_tasks, model)
        else:
            plan_text = ""

        canonical_output = json.dumps(
            {"plan": plan_text, "tasks": ordered_tasks},
            ensure_ascii=False, separators=(",", ":"),
        )

        src_meta = raw.get("metadata", {})
        return {
            "messages": [
                {"role": "system",    "content": system_prompt},
                {"role": "user",      "content": f"Goal: {goal}"},
                {"role": "assistant", "content": canonical_output},
            ],
            "metadata": {
                "category":              "boss_planning",
                "task_type":             "feature_plan",
                "source":                "hardcorepc_normalized",
                "quality":               "silver",
                "contains_sensitive_data": False,
                "base_model_target":     "theorc-boss:gemma4",
                "created_by":            "normalize_hardcorepc_boss.py",
                "original_model":        src_meta.get("model", "qwen2.5-coder:7b"),
                "original_generated":    src_meta.get("generated", ""),
                "notes": (
                    f"Normalized from hardcorepc_raw_overnight.jsonl example {idx}. "
                    + (f"Plan generated by {model}." if gen_plan else "Plan field empty (--no-plan).")
                ),
            },
        }
    except Exception as e:
        print(f"  ⚠ [{idx}] unexpected error: {e} — skipped")
        return None

# ── Load raw files ─────────────────────────────────────────────────────────────

def load_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]

# ── Main ───────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Normalize HARDCOREPC boss dataset")
    parser.add_argument("--merge",    action="store_true", help="Also produce train_v2.jsonl")
    parser.add_argument("--no-plan",  action="store_true", help="Skip Ollama plan generation (faster)")
    parser.add_argument("--model",    default=DEFAULT_MODEL, help="Ollama model for plan generation")
    args = parser.parse_args()

    print("=" * 60)
    print("  TheOrc -- HARDCOREPC Boss Dataset Normalizer")
    print("=" * 60)
    print(f"  Raw overnight : {RAW_FILE}")
    print(f"  Raw synthetic : {RAW_20}")
    print(f"  Canon source  : {CANON}")
    print(f"  Output        : {OUT_FILE}")
    if args.merge:
        print(f"  Merge output  : {MERGE_OUT}")
    print(f"  Plan gen      : {'OFF (--no-plan)' if args.no_plan else f'ON ({args.model})'}")
    print()

    system_prompt = load_canonical_system_prompt()
    print(f"✓ Canonical system prompt loaded ({len(system_prompt)} chars)\n")

    # Combine both raw files
    raw_examples = load_jsonl(RAW_FILE) + load_jsonl(RAW_20)
    print(f"✓ {len(raw_examples)} raw examples to process\n")

    if not raw_examples:
        print("✗ No raw examples found — check paths.")
        sys.exit(1)

    results   = []
    skipped   = 0
    t_start   = time.time()

    for i, raw in enumerate(raw_examples, 1):
        print(f"  [{i:4d}/{len(raw_examples)}] ", end="", flush=True)
        ex = normalize(raw, system_prompt, i, args.model, not args.no_plan)
        if ex:
            results.append(ex)
            plan_preview = ex["messages"][2]["content"][:70]
            print(f"✓  {plan_preview}…")
        else:
            skipped += 1

    elapsed = time.time() - t_start
    print(f"\n✓ Normalized : {len(results)}")
    print(f"✗ Skipped    : {skipped}")
    print(f"⏱ Elapsed    : {elapsed:.1f}s")

    # Write normalized output
    with open(OUT_FILE, "w", encoding="utf-8") as f:
        for ex in results:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")
    print(f"\n✓ Written → {OUT_FILE}")

    # ── Merge ─────────────────────────────────────────────────────────────────
    if args.merge:
        canon_examples = load_jsonl(CANON)
        merged = canon_examples + results
        random.seed(42)
        random.shuffle(merged)

        with open(MERGE_OUT, "w", encoding="utf-8") as f:
            for ex in merged:
                f.write(json.dumps(ex, ensure_ascii=False) + "\n")

        gold   = sum(1 for e in merged if e.get("metadata", {}).get("quality") == "gold")
        silver = sum(1 for e in merged if e.get("metadata", {}).get("quality") == "silver")
        print(f"\n✓ Merged train_v2.jsonl")
        print(f"  Gold   (reviewed) : {gold}")
        print(f"  Silver (normalized): {silver}")
        print(f"  Total             : {len(merged)}")
        print(f"✓ Written → {MERGE_OUT}")

if __name__ == "__main__":
    main()
