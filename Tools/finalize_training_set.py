#!/usr/bin/env python3
"""
finalize_training_set.py
Turns the Codex gold work file into the final train/eval split, ready for the Training Pit.

Steps:
  1. Load codex_gold.work.jsonl, re-validate every example (schema + rules).
  2. Rename to canonical: codex[api].synthetic.boss.{n}.jsonl
  3. Hold out EVAL_N examples (stratified by language) as the eval set.
  4. Merge the remaining codex gold with merged[mixed].normalized.boss.2244.jsonl,
     shuffle (seed 42), write the final training file.
  5. Print the final composition table.

Usage:
  python tools/finalize_training_set.py            # dry-run (counts only)
  python tools/finalize_training_set.py --apply     # write all output files
  python tools/finalize_training_set.py --eval-n 200
"""

import argparse
import json
import random
import re
from collections import Counter
from pathlib import Path

ROOT   = Path(__file__).resolve().parent.parent
DS_DIR = ROOT / "training_pit" / "datasets"

WORK     = DS_DIR / "codex_gold.work.jsonl"
EXISTING = DS_DIR / "merged[mixed].normalized.boss.2244.jsonl"

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
    """Final structural gate on a fully-wrapped example."""
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply",  action="store_true", help="Write output files (default: dry-run)")
    ap.add_argument("--eval-n", type=int, default=200, help="Eval holdout size")
    ap.add_argument("--seed",   type=int, default=42)
    args = ap.parse_args()
    rng = random.Random(args.seed)

    print("=" * 64)
    print("  TheOrc -- Finalize Training Set")
    print("=" * 64)

    raw = load_jsonl(WORK)
    print(f"  codex work file : {len(raw)} examples")
    gold = [e for e in raw if revalidate(e)]
    rejected = len(raw) - len(gold)
    print(f"  passed re-valid : {len(gold)}  (rejected {rejected})")

    existing = load_jsonl(EXISTING)
    print(f"  existing merged : {len(existing)} examples")

    # ── Stratified eval holdout by language ───────────────────────────────────
    by_lang: dict[str, list[dict]] = {}
    for e in gold:
        lang = e.get("metadata", {}).get("language", "unknown")
        by_lang.setdefault(lang, []).append(e)
    for lang in by_lang:
        rng.shuffle(by_lang[lang])

    eval_set, train_codex = [], []
    eval_n = min(args.eval_n, len(gold))
    # Proportional pick per language
    for lang, items in by_lang.items():
        share = round(eval_n * len(items) / max(1, len(gold)))
        eval_set.extend(items[:share])
        train_codex.extend(items[share:])
    # Trim eval to exact target; spill remainder back to train
    rng.shuffle(eval_set)
    if len(eval_set) > eval_n:
        train_codex.extend(eval_set[eval_n:])
        eval_set = eval_set[:eval_n]

    print(f"\n  eval holdout    : {len(eval_set)} (stratified by language)")
    print(f"  codex -> train  : {len(train_codex)}")

    # ── Merge codex-train with existing, shuffle ──────────────────────────────
    train = existing + train_codex
    rng.shuffle(train)

    codex_n   = len(gold)
    codex_name = f"codex[api].synthetic.boss.{codex_n}.jsonl"
    train_name = f"train[mixed].merged.boss.{len(train)}.jsonl"
    eval_name  = f"eval[mixed].holdout.boss.{len(eval_set)}.jsonl"

    # ── Composition table ─────────────────────────────────────────────────────
    def compose(rows: list[dict], key_path) -> Counter:
        c = Counter()
        for r in rows:
            c[key_path(r)] += 1
        return c

    print("\n  TRAIN composition by source:")
    for k, v in compose(train, lambda r: r.get("metadata", {}).get("source", "?")).most_common():
        print(f"    {k:<22} {v:5d}")
    print("\n  TRAIN composition by language (codex portion):")
    for k, v in compose(train_codex, lambda r: r.get("metadata", {}).get("language", "?")).most_common():
        print(f"    {k:<22} {v:5d}")

    print(f"\n  -> {codex_name}")
    print(f"  -> {train_name}   (existing {len(existing)} + codex {len(train_codex)})")
    print(f"  -> {eval_name}")

    if not args.apply:
        print("\n[DRY RUN] Nothing written. Pass --apply to write the files.")
        return

    save_jsonl(DS_DIR / codex_name, gold)
    save_jsonl(DS_DIR / train_name, train)
    save_jsonl(DS_DIR / eval_name,  eval_set)
    print("\nOK -- all files written.")
    print(f"\nTrain on: training_pit/datasets/{train_name}")
    print(f"Eval on : training_pit/datasets/{eval_name}")


if __name__ == "__main__":
    main()
