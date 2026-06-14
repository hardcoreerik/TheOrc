#!/usr/bin/env python3
"""
organize_datasets.py
Renames training datasets to the canonical naming convention and
auto-promotes silver-quality examples to gold.

Naming convention:
  {source}[{vram}].{type}.{role}.{n}.jsonl

  source : hardcorepc | mainpc | claude | ollama | merged
  vram   : 8gb | 24gb | api | mixed  (bracket, lowercase)
  type   : captured | synthetic | normalized | raw | merged
  role   : boss | worker | researcher | tester | mixed
  n      : exact example count

Usage:
  python tools/organize_datasets.py            # dry-run (prints plan)
  python tools/organize_datasets.py --apply    # rename + promote + write merged
  python tools/organize_datasets.py --promote-only  # only promote silver→gold in place
"""

import argparse
import json
import shutil
import sys
from pathlib import Path

ROOT    = Path(__file__).resolve().parent.parent
DS_DIR  = ROOT / "training_pit" / "datasets"

# ── Dataset map ────────────────────────────────────────────────────────────────
# old_name → (source, vram, type, role)
DATASET_MAP = {
    "train_v1.jsonl":                    ("mainpc",     "24gb",  "captured",   "boss"),
    "hardcorepc_raw_overnight.jsonl":    ("hardcorepc", "8gb",   "raw",        "boss"),
    "hardcorepc_raw_synthetic20.jsonl":  ("hardcorepc", "8gb",   "synthetic",  "boss"),
    "hardcorepc_boss_normalized.jsonl":  ("hardcorepc", "8gb",   "normalized", "boss"),
    "train_v2.jsonl":                    ("merged",     "mixed", "normalized", "boss"),
}

# Files that should NOT be auto-promoted (raw = unprocessed, keep as-is)
NO_PROMOTE = {"hardcorepc_raw_overnight.jsonl", "hardcorepc_raw_synthetic20.jsonl"}


def load_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def save_jsonl(path: Path, examples: list[dict]) -> None:
    with open(path, "w", encoding="utf-8") as f:
        for ex in examples:
            f.write(json.dumps(ex, ensure_ascii=False) + "\n")


def promote_to_gold(examples: list[dict], source_tag: str) -> tuple[list[dict], int]:
    """Set quality='gold' on every example. Returns (updated_list, promoted_count)."""
    promoted = 0
    result = []
    for ex in examples:
        meta = ex.get("metadata", {})
        if meta.get("quality") != "gold":
            meta["quality"] = "gold"
            meta["promoted_by"] = "organize_datasets.py"
            meta["promoted_source"] = source_tag
            ex["metadata"] = meta
            promoted += 1
        result.append(ex)
    return result, promoted


def canonical_name(source: str, vram: str, type_: str, role: str, n: int) -> str:
    return f"{source}[{vram}].{type_}.{role}.{n}.jsonl"


def main():
    parser = argparse.ArgumentParser(description="Organize and promote training datasets")
    parser.add_argument("--apply",        action="store_true", help="Actually rename and promote (default: dry-run)")
    parser.add_argument("--promote-only", action="store_true", help="Only promote silver→gold in existing files, no rename")
    args = parser.parse_args()

    dry_run = not args.apply and not args.promote_only

    print("=" * 60)
    print("  TheOrc — Dataset Organizer")
    print("=" * 60)
    if dry_run:
        print("  Mode: DRY RUN (pass --apply to execute)\n")
    elif args.promote_only:
        print("  Mode: PROMOTE ONLY (no rename)\n")
    else:
        print("  Mode: APPLY (rename + promote + rebuild merged)\n")

    rename_plan = []
    total_promoted = 0

    for old_name, (source, vram, type_, role) in DATASET_MAP.items():
        old_path = DS_DIR / old_name
        if not old_path.exists():
            print(f"  ⚠ Not found: {old_name} — skipping")
            continue

        examples = load_jsonl(old_path)
        n = len(examples)

        new_name = canonical_name(source, vram, type_, role, n)
        new_path = DS_DIR / new_name

        do_promote = old_name not in NO_PROMOTE
        source_tag = f"{source}[{vram}]"

        if do_promote:
            promoted_examples, promoted_count = promote_to_gold(examples, source_tag)
        else:
            promoted_examples, promoted_count = examples, 0

        total_promoted += promoted_count

        silver_before = sum(1 for e in examples         if e.get("metadata", {}).get("quality") == "silver")
        gold_before   = sum(1 for e in examples         if e.get("metadata", {}).get("quality") == "gold")
        gold_after    = sum(1 for e in promoted_examples if e.get("metadata", {}).get("quality") == "gold")

        print(f"  {old_name}")
        print(f"    -> {new_name}")
        print(f"    Examples : {n}")
        print(f"    Quality  : {gold_before} gold + {silver_before} silver -> {gold_after} gold")
        if not do_promote:
            print(f"    Promote  : skipped (raw file)")
        print()

        rename_plan.append((old_path, new_path, promoted_examples, do_promote))

    print(f"Total examples promoted: {total_promoted}")

    if dry_run:
        print("\n[DRY RUN] Nothing written. Pass --apply to execute.")
        return

    if args.promote_only:
        print("\nPromoting in place (no rename)...")
        for old_path, _, promoted_examples, do_promote in rename_plan:
            if do_promote:
                save_jsonl(old_path, promoted_examples)
                print(f"  OK Promoted: {old_path.name}")
        print(f"\nDone -- {total_promoted} examples promoted to gold")
        return

    # ── Full apply: promote + rename ──────────────────────────────────────────
    print("\nApplying...")
    for old_path, new_path, promoted_examples, do_promote in rename_plan:
        # Skip train_v2 (merged) — we'll rebuild it below
        if old_path.name == "train_v2.jsonl":
            continue

        save_jsonl(new_path, promoted_examples)
        print(f"  OK Written : {new_path.name}")

        # Keep originals as .bak so nothing is lost
        bak = old_path.with_suffix(".jsonl.bak")
        shutil.copy2(old_path, bak)
        print(f"  OK Backup  : {bak.name}")

    # ── Rebuild merged dataset from the two promoted canonical files ──────────
    print("\nRebuilding merged dataset…")
    import random

    v1_path   = next((DS_DIR / canonical_name("mainpc",     "24gb",  "captured",   "boss", 0)).parent.glob("mainpc[[]24gb[]].captured.boss.*.jsonl"), None)
    hpc_path  = next((DS_DIR / canonical_name("hardcorepc", "8gb",   "normalized", "boss", 0)).parent.glob("hardcorepc[[]8gb[]].normalized.boss.*.jsonl"), None)

    merged = []
    if v1_path and v1_path.exists():
        merged += load_jsonl(v1_path)
        print(f"  + {v1_path.name} ({len(load_jsonl(v1_path))} examples)")
    if hpc_path and hpc_path.exists():
        hpc_examples = load_jsonl(hpc_path)
        merged += hpc_examples
        print(f"  + {hpc_path.name} ({len(hpc_examples)} examples)")

    random.seed(42)
    random.shuffle(merged)

    gold   = sum(1 for e in merged if e.get("metadata", {}).get("quality") == "gold")
    silver = sum(1 for e in merged if e.get("metadata", {}).get("quality") == "silver")

    merged_name = canonical_name("merged", "mixed", "normalized", "boss", len(merged))
    merged_path = DS_DIR / merged_name
    save_jsonl(merged_path, merged)

    print(f"\nOK Merged -> {merged_name}")
    print(f"  Gold   : {gold}")
    print(f"  Silver : {silver}")
    print(f"  Total  : {len(merged)}")

    # Back up old train_v2
    old_v2 = DS_DIR / "train_v2.jsonl"
    if old_v2.exists():
        shutil.copy2(old_v2, old_v2.with_suffix(".jsonl.bak"))
        print(f"\n  Backup: train_v2.jsonl.bak")

    print("\nOrganization complete.")
    print(f"\nDataset naming convention: {{source}}[{{vram}}].{{type}}.{{role}}.{{n}}.jsonl")
    print("Examples:")
    print("  mainpc[24gb].captured.boss.1384.jsonl     -- live captured runs, main PC")
    print("  hardcorepc[8gb].normalized.boss.860.jsonl -- overnight harvest, normalized")
    print("  claude[api].synthetic.boss.2000.jsonl     -- future: Claude API generated")
    print("  merged[mixed].normalized.boss.2244.jsonl  -- merged training set")


if __name__ == "__main__":
    main()
