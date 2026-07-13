#!/usr/bin/env python3
"""
Build the toolcaller r3 training round from Refusal Gauntlet findings.

Inputs:
  - train_toolcaller_v0.jsonl          (r2 training set, kept intact)
  - refusal_gauntlet_v0.jsonl          (4,788 adversarial cases)
  - <gauntlet run>/failures.jsonl      (what the deployed r2 artifact got wrong)

Contamination guards (the whole point):
  1. GROUP-level split: every paraphrase group is deterministically assigned
     train-eligible (70%) or HELD OUT (30%) by hash. Training rows come only
     from train-eligible groups; the held-out groups become
     refusal_gauntlet_v0_holdout.jsonl — the post-train re-eval set. No group
     ever appears on both sides, so template phrasings seen in training are
     never scored.
  2. The sealed Arena set (eval_toolcaller_v0.jsonl) is not touched. It stays
     the training eval_loss set AND the regression gate.
  3. Distribution cap: at most --max-added gauntlet rows (default 900) so
     refusal examples don't swamp the original call distribution and cost us
     the 97.3% Arena decision accuracy. Failing phrasings are sampled first,
     then siblings of failing groups (consistency signal), up to 3 rows/group.

Outputs:
  - train_toolcaller_v0_r3.jsonl
  - refusal_gauntlet_v0_holdout.jsonl
  - toolcaller_v0_r3.meta.json  (honest sidecar: sha256s, group lists, and an
    explicit validator note — this dataset is synthetic-derived and does NOT
    carry a ToolcallerBench PASS; train with --skip-gates and document it)
"""
import argparse
import hashlib
import json
import random
from collections import defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_jsonl(path: Path) -> list[dict]:
    return [json.loads(l) for l in path.open(encoding="utf-8") if l.strip()]


def group_key(row: dict) -> str:
    return f"{row['family']}/{row['group_id']}"


def is_holdout(key: str, holdout_frac: float) -> bool:
    bucket = int(hashlib.sha256(key.encode("utf-8")).hexdigest()[:8], 16) % 100
    return bucket < int(holdout_frac * 100)


def main():
    ap = argparse.ArgumentParser(description="Build toolcaller r3 dataset from gauntlet findings")
    ap.add_argument("--train", type=Path, default=REPO / "training_pit/datasets/train_toolcaller_v0.jsonl")
    ap.add_argument("--gauntlet", type=Path, default=REPO / "training_pit/datasets/refusal_gauntlet_v0.jsonl")
    ap.add_argument("--failures", type=Path,
                    default=REPO / "training_pit/outputs/refusal_gauntlet/theorc-toolcaller_qwen25-1.5b/failures.jsonl")
    ap.add_argument("--out-train", type=Path, default=REPO / "training_pit/datasets/train_toolcaller_v0_r3.jsonl")
    ap.add_argument("--out-holdout", type=Path, default=REPO / "training_pit/datasets/refusal_gauntlet_v0_holdout.jsonl")
    ap.add_argument("--out-meta", type=Path, default=REPO / "training_pit/datasets/toolcaller_v0_r3.meta.json")
    ap.add_argument("--holdout-frac", type=float, default=0.30)
    ap.add_argument("--max-added", type=int, default=900)
    ap.add_argument("--max-per-group", type=int, default=3)
    ap.add_argument("--seed", type=int, default=7)
    args = ap.parse_args()

    # The gauntlet group->side assignment is FROZEN with the promotion margin
    # (toolcaller_v0_r3.json promotion.margin.eval_sets.group_split). Changing
    # either value silently migrates held-out groups train-side and invalidates
    # every cross-round comparison — hard-stop rather than trust review to catch it.
    FROZEN_HOLDOUT_FRAC, FROZEN_SPLIT_SEED = 0.30, 7
    if args.holdout_frac != FROZEN_HOLDOUT_FRAC or args.seed != FROZEN_SPLIT_SEED:
        raise SystemExit(
            f"REFUSED: group split is frozen at holdout_frac={FROZEN_HOLDOUT_FRAC}, "
            f"seed={FROZEN_SPLIT_SEED} (got {args.holdout_frac}, {args.seed}). "
            "See promotion.margin.eval_sets.group_split in toolcaller_v0_r3.json.")

    base_train = load_jsonl(args.train)
    gauntlet   = load_jsonl(args.gauntlet)
    failures   = load_jsonl(args.failures)
    rng = random.Random(args.seed)

    by_group: dict[str, list[dict]] = defaultdict(list)
    for row in gauntlet:
        by_group[group_key(row)].append(row)

    failed_phrasings: dict[str, set[str]] = defaultdict(set)
    for f in failures:
        failed_phrasings[f"{f['family']}/{f['group_id']}"].add(f["phrasing"])

    train_groups   = sorted(k for k in by_group if not is_holdout(k, args.holdout_frac))
    holdout_groups = sorted(k for k in by_group if is_holdout(k, args.holdout_frac))

    # ── Held-out gauntlet eval: ALL rows of held-out groups (pass and fail
    #    alike — an unbiased slice, not a failures-only slice) ────────────────
    holdout_rows = [r for k in holdout_groups for r in by_group[k]]

    # ── Training additions: failing groups from the train side only ──────────
    failing_train_groups = [k for k in train_groups if failed_phrasings.get(k)]
    rng.shuffle(failing_train_groups)

    added: list[dict] = []
    for k in failing_train_groups:
        if len(added) >= args.max_added:
            break
        rows = by_group[k]
        failed = [r for r in rows if r["phrasing"] in failed_phrasings[k]]
        passed = [r for r in rows if r["phrasing"] not in failed_phrasings[k]]
        rng.shuffle(failed)
        rng.shuffle(passed)
        take = (failed + passed)[: args.max_per_group]
        added.extend(take[: args.max_added - len(added)])

    # r3 train = r2 train + gauntlet-derived rows, shuffled deterministically.
    r3_rows = list(base_train) + [
        {"messages": r["messages"],
         "example_id": r["example_id"],
         "lineage_group_id": group_key(r),
         "decision": r["decision"],
         "role": r["role"],
         "source_type": "gauntlet-derived"} for r in added
    ]
    rng.shuffle(r3_rows)

    for path, rows in ((args.out_train, r3_rows), (args.out_holdout, holdout_rows)):
        with path.open("w", encoding="utf-8", newline="\n") as fh:
            for r in rows:
                fh.write(json.dumps(r, ensure_ascii=False) + "\n")

    # Decision-distribution report (the cap's job is to keep 'call' healthy).
    def dist(rows: list[dict]) -> dict:
        c: dict[str, int] = defaultdict(int)
        for r in rows:
            c[r.get("decision") or "?"] += 1
        return dict(sorted(c.items()))

    meta = {
        "round": "r3",
        "built": __import__("time").strftime("%Y-%m-%d %H:%M"),
        "sources": {
            "base_train": str(args.train), "gauntlet": str(args.gauntlet),
            "failures": str(args.failures),
        },
        "train_sha256":   sha256_file(args.out_train),
        "holdout_sha256": sha256_file(args.out_holdout),
        "counts": {
            "base_train": len(base_train), "gauntlet_added": len(added),
            "r3_train_total": len(r3_rows), "holdout": len(holdout_rows),
        },
        "decision_distribution": {"r3_train": dist(r3_rows), "holdout": dist(holdout_rows)},
        "lineage_groups": {
            "train": failing_train_groups[: len({group_key(r) for r in added})],
            "eval": holdout_groups,
        },
        "isolation": f"group-hash split, holdout_frac={args.holdout_frac}, seed={args.seed}; "
                      "no paraphrase group appears in both train and holdout",
        "validator": {
            "verdict": "N/A",
            "note": "gauntlet-derived synthetic rows carry no ToolcallerBench capture PASS; "
                    "train with --skip-gates and record the reason in the run manifest",
        },
    }
    args.out_meta.write_text(json.dumps(meta, indent=2), encoding="utf-8", newline="\n")

    print(f"r3 train:  {len(r3_rows)} rows ({len(base_train)} base + {len(added)} gauntlet-derived) -> {args.out_train}")
    print(f"holdout:   {len(holdout_rows)} rows across {len(holdout_groups)} held-out groups -> {args.out_holdout}")
    print(f"train dist:   {dist(r3_rows)}")
    print(f"holdout dist: {dist(holdout_rows)}")
    print(f"failing groups: {len(failing_train_groups)} train-side (sampled) / "
          f"{sum(1 for k in holdout_groups if failed_phrasings.get(k))} holdout-side (untouched)")


if __name__ == "__main__":
    main()
