#!/usr/bin/env python3
"""
finalize_training_set.py
Builds the v2 train/eval split for the ORC ACADEMY boss adapter.

Pipeline (v2):
  1. Load the synthetic gold work files and re-validate every example against
     the structural + filename rules:
       - cerebras_gold.work.jsonl  (Cerebras gpt-oss-120b)
       - codex_gold.work.jsonl     (Codex API)
  2. Load the existing swarm captures (merged[mixed].normalized.boss.2244.jsonl)
     and GATE-FILTER them through the SAME rules. The captures predate the
     FILENAME RULE, so most fail (no filename in task title) and are dropped.
     This keeps ONE consistent convention across the whole training set rather
     than diluting the rule the v2 round is meant to reinforce.
  3. Deduplicate by user-goal text across the whole conforming pool. This both
     removes repeated goals and guarantees no goal can land in BOTH train and
     eval (leakage). Gold wins ties over captures; Cerebras wins over Codex.
  4. Hold out EVAL_N examples (stratified by language) as the eval set.
  5. Shuffle (seed 42) the remainder into the final training file.
  6. Rename the gold work files to canonical names and write everything.

Usage:
  python Tools/finalize_training_set.py                # dry-run (counts only)
  python Tools/finalize_training_set.py --apply        # write all output files
  python Tools/finalize_training_set.py --eval-n 200
"""

import argparse
import json
import random
import re
from collections import Counter
from pathlib import Path

ROOT   = Path(__file__).resolve().parent.parent
DS_DIR = ROOT / "training_pit" / "datasets"

# Synthetic gold work files (source, path) — order sets dedup tie-break priority.
GOLD_SOURCES = [
    ("cerebras", DS_DIR / "cerebras_gold.work.jsonl"),
    ("codex",    DS_DIR / "codex_gold.work.jsonl"),
]
CAPTURES = DS_DIR / "merged[mixed].normalized.boss.2244.jsonl"

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
FILENAME_RE = re.compile(r"[\w\-]+\.[A-Za-z0-9]{1,6}\b")


def load_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def save_jsonl(path: Path, rows: list[dict]) -> None:
    with open(path, "w", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")


def revalidate(ex: dict) -> bool:
    """Final structural + filename gate on a fully-wrapped example."""
    try:
        msgs = ex["messages"]
        if len(msgs) != 3:
            return False
        if msgs[0]["role"] != "system" or msgs[1]["role"] != "user" or msgs[2]["role"] != "assistant":
            return False
        if not msgs[1]["content"].startswith("Goal: "):
            return False
        payload = json.loads(msgs[2]["content"])
        plan, tasks = payload.get("plan"), payload.get("tasks")
        if not plan or not isinstance(tasks, list) or not (2 <= len(tasks) <= 4):
            return False
        researchers = 0
        for t in tasks:
            role = t.get("role", "")
            if role not in VALID_ROLES:
                return False
            if role == "RESEARCHER":
                researchers += 1
                if t.get("priority") != 1:
                    return False
            elif t.get("priority") != 2:
                return False
            if role != "TESTER" and not FILENAME_RE.search(t.get("title", "")):
                return False
        return researchers <= 1
    except Exception:
        return False


def goal_text(ex: dict) -> str | None:
    try:
        return ex["messages"][1]["content"].strip()
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply",  action="store_true", help="Write output files (default: dry-run)")
    ap.add_argument("--eval-n", type=int, default=200, help="Eval holdout size")
    ap.add_argument("--seed",   type=int, default=42)
    args = ap.parse_args()
    rng = random.Random(args.seed)

    print("=" * 64)
    print("  TheOrc -- Finalize Training Set (v2)")
    print("=" * 64)

    # ── 1. Load + validate synthetic gold ─────────────────────────────────────
    pool: list[dict] = []          # conforming examples, in dedup-priority order
    gold_counts: dict[str, int] = {}
    for src, path in GOLD_SOURCES:
        rows   = load_jsonl(path)
        passed = [e for e in rows if revalidate(e)]
        gold_counts[src] = len(passed)
        print(f"  gold:{src:<9} loaded {len(rows):4}  passed {len(passed):4}  "
              f"(rejected {len(rows) - len(passed)})")
        pool.extend(passed)

    # ── 2. Gate-filter the existing captures ──────────────────────────────────
    caps        = load_jsonl(CAPTURES)
    caps_passed = [e for e in caps if revalidate(e)]
    print(f"  captures    loaded {len(caps):4}  passed {len(caps_passed):4}  "
          f"(dropped {len(caps) - len(caps_passed)} — mostly pre-FILENAME-RULE)")
    pool.extend(caps_passed)

    # ── 3. Dedup by user-goal (first occurrence wins → gold beats captures) ────
    seen: set[str] = set()
    deduped: list[dict] = []
    collisions = 0
    for e in pool:
        g = goal_text(e)
        if g is None:
            continue
        if g in seen:
            collisions += 1
            continue
        seen.add(g)
        deduped.append(e)
    print(f"\n  conforming pool : {len(pool)}  ->  deduped {len(deduped)}  "
          f"(removed {collisions} duplicate goals)")

    # ── 4. Stratified eval holdout by language ────────────────────────────────
    by_lang: dict[str, list[dict]] = {}
    for e in deduped:
        lang = e.get("metadata", {}).get("language", "unknown") or "unknown"
        by_lang.setdefault(lang, []).append(e)
    for lang in by_lang:
        rng.shuffle(by_lang[lang])

    eval_n = min(args.eval_n, len(deduped))
    eval_set, train = [], []
    for lang, items in by_lang.items():
        share = round(eval_n * len(items) / max(1, len(deduped)))
        eval_set.extend(items[:share])
        train.extend(items[share:])
    # Trim eval to exact target; spill remainder back to train.
    rng.shuffle(eval_set)
    if len(eval_set) > eval_n:
        train.extend(eval_set[eval_n:])
        eval_set = eval_set[:eval_n]

    rng.shuffle(train)

    print(f"\n  eval holdout    : {len(eval_set)} (stratified by language)")
    print(f"  train           : {len(train)}")

    # ── Composition tables ────────────────────────────────────────────────────
    def compose(rows, key):
        c = Counter(key(r) for r in rows)
        return c

    print("\n  TRAIN by source:")
    for k, v in compose(train, lambda r: r.get("metadata", {}).get("source", "?")).most_common():
        print(f"    {k:<24} {v:5d}")
    print("\n  TRAIN by language:")
    for k, v in compose(train, lambda r: r.get("metadata", {}).get("language", "?") or "?").most_common():
        print(f"    {k:<24} {v:5d}")
    print("\n  EVAL by language:")
    for k, v in compose(eval_set, lambda r: r.get("metadata", {}).get("language", "?") or "?").most_common():
        print(f"    {k:<24} {v:5d}")

    cer_n   = gold_counts.get("cerebras", 0)
    cod_n   = gold_counts.get("codex", 0)
    cer_name = f"cerebras[api].synthetic.boss.{cer_n}.jsonl"
    cod_name = f"codex[api].synthetic.boss.{cod_n}.jsonl"
    train_name = f"train[mixed].merged.boss.{len(train)}.jsonl"
    eval_name  = f"eval[mixed].holdout.boss.{len(eval_set)}.jsonl"

    print(f"\n  -> {cer_name}")
    print(f"  -> {cod_name}")
    print(f"  -> {train_name}")
    print(f"  -> {eval_name}")

    if not args.apply:
        print("\n[DRY RUN] Nothing written. Pass --apply to write the files.")
        return

    # Rename gold work files to canonical (re-validated, full set).
    save_jsonl(DS_DIR / cer_name, [e for e in load_jsonl(GOLD_SOURCES[0][1]) if revalidate(e)])
    save_jsonl(DS_DIR / cod_name, [e for e in load_jsonl(GOLD_SOURCES[1][1]) if revalidate(e)])
    save_jsonl(DS_DIR / train_name, train)
    save_jsonl(DS_DIR / eval_name,  eval_set)
    print("\nOK -- all files written.")
    print(f"\nTrain on: training_pit/datasets/{train_name}")
    print(f"Eval on : training_pit/datasets/{eval_name}")


if __name__ == "__main__":
    main()
