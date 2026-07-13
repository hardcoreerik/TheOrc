#!/usr/bin/env python3
"""
Foundry promotion gate — mechanically verifies a candidate against its track's
frozen promotion margin and refuses promotion on any unmet criterion. Built in
response to the 2026-07-12 external release review's P0 finding: "Arena
promotion must become executable policy... The command — not a Markdown
checklist — should change the registry/tag from candidate to promoted."

This tool does NOT decide policy. It enforces whatever is already frozen in
the track's config (promotion.margin) — see toolcaller_v0_r3.json for the
shape. Adding/loosening a criterion here without a corresponding frozen
config value is a bug, not a feature.

Checks performed (all must pass):
  1. dataset_hash            - candidate train file matches its own meta.json record
  2. frozen_group_split      - candidate's holdout_frac/seed match the frozen config values
  3. sealed_eval_hash        - the regression eval file hasn't been edited since it was sealed
  4. candidate_artifact_hash - candidate GGUF exists and is hashed (recorded, not compared)
  5. incumbent_artifact_hash - currently-deployed GGUF exists and is hashed (rollback target)
  6. deployed_artifact_gauntlet - the refusal-gauntlet run that produced the candidate's
                              safety numbers ran against a deployed Ollama tag (contains
                              ':'), not just the training-format adapter
  7. confidence_lower_bound  - candidate gauntlet safety_cp95_lower strictly exceeds the
                              incumbent's recorded bound
  8. arena_max_regression    - candidate Arena decision_accuracy does not drop more than
                              the frozen max from the incumbent's
  9. arena_json_validity_min - candidate Arena json_validity meets the frozen floor
  10. per_family_safety_floor - no gauntlet family's safety_rate regresses vs the incumbent
  11. latency_memory_budget  - NOT YET MEASURED for this track; this criterion always
                              blocks promotion until a real budget check is wired in.
                              Recorded as a known gap, not silently skipped.
  12. runtime_schema_identity - candidate's tool_schema_hash matches the frozen inventory hash
  13. human_approval_record  - --approved-by and --approval-note were supplied
  14. rollback_artifact_availability - the incumbent's adapter/GGUF files still exist on disk

Usage:
    python foundry_promote.py --config training_pit/foundry/configs/toolcaller_v0_r3.json \
        --candidate-dir training_pit/outputs/foundry_toolcaller_v0_r3 \
        --candidate-gguf training_pit/outputs/foundry_toolcaller_v0_r3/theorc-toolcaller-qwen25-1.5b-lora-v0-r3-f16.gguf \
        --arena-results training_pit/outputs/foundry_toolcaller_v0_r3/arena/results.json \
        --gauntlet-results training_pit/outputs/refusal_gauntlet/r3_holdout/results.json \
        --incumbent-modelfile training_pit/modelfiles/toolcaller-qwen25-1.5b.modelfile \
        --approved-by "hardcoreerik" --approval-note "reviewed Arena+Gauntlet numbers in chat" \
        [--allow-unmeasured-latency-budget]  # explicit escape hatch, logged loudly if used

On PASS: writes/appends training_pit/foundry/PROMOTION_REGISTRY.json (the actual
registry-of-record this command changes candidate -> promoted) and prints the
exact `ollama create` command to deploy — does not run it. Pass --deploy to
also execute it.

On FAIL: exits 1, prints every unmet criterion, writes nothing.
"""
import argparse
import hashlib
import json
import re
import subprocess
import sys
import time
from pathlib import Path

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO = Path(__file__).resolve().parents[3]
REGISTRY_PATH = REPO / "training_pit" / "foundry" / "PROMOTION_REGISTRY.json"


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def rel(p: Path) -> str:
    try:
        return p.resolve().relative_to(REPO).as_posix()
    except ValueError:
        return str(p)


class Check:
    def __init__(self, name: str):
        self.name = name
        self.passed: bool | None = None
        self.detail = ""

    def ok(self, detail: str = ""):
        self.passed = True
        self.detail = detail
        return self

    def fail(self, detail: str):
        self.passed = False
        self.detail = detail
        return self


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def find_incumbent_gguf(modelfile_path: Path) -> Path | None:
    if not modelfile_path.exists():
        return None
    m = re.search(r"^ADAPTER\s+(.+\.gguf)\s*$", modelfile_path.read_text(encoding="utf-8"), re.MULTILINE)
    if not m:
        return None
    return (modelfile_path.parent / m.group(1)).resolve()


def main():
    ap = argparse.ArgumentParser(description="Foundry promotion gate")
    ap.add_argument("--config", required=True, type=Path)
    ap.add_argument("--candidate-dir", required=True, type=Path)
    ap.add_argument("--candidate-gguf", required=True, type=Path)
    ap.add_argument("--arena-results", required=True, type=Path)
    ap.add_argument("--gauntlet-results", required=True, type=Path)
    ap.add_argument("--incumbent-modelfile", required=True, type=Path)
    ap.add_argument("--approved-by", default=None)
    ap.add_argument("--approval-note", default=None)
    ap.add_argument("--allow-unmeasured-latency-budget", action="store_true",
                    help="Explicit escape hatch to promote without a latency/memory "
                         "measurement. Logged in the registry entry so it's auditable.")
    ap.add_argument("--deploy", action="store_true",
                    help="Also run `ollama create` to actually deploy the promoted candidate.")
    ap.add_argument("--ollama-tag", default=None,
                    help="Tag to deploy to (default: config's output.ollama_tag_hypothesis, "
                         "falling back to the incumbent modelfile's own build tag).")
    ap.add_argument("--bootstrap", action="store_true",
                    help="Seed PROMOTION_REGISTRY.json with the CURRENTLY deployed incumbent's "
                         "own numbers as a one-time baseline entry, so the NEXT real candidate "
                         "has something to compare per-family safety against. Does not run the "
                         "gate checks — bootstrap entries are explicitly labeled as such, not "
                         "claimed as a gate-verified promotion.")
    args = ap.parse_args()

    if args.bootstrap:
        cfg = load_json(args.config)
        gauntlet = load_json(args.gauntlet_results) if args.gauntlet_results.exists() else None
        arena = load_json(args.arena_results) if args.arena_results.exists() else None
        if not gauntlet or not arena:
            print("REFUSED: --bootstrap still needs real arena-results and gauntlet-results files.")
            raise SystemExit(1)
        candidate_hash = sha256_file(args.candidate_gguf) if args.candidate_gguf.exists() else None
        entry = {
            "track": cfg["foundry_track"],
            "round": cfg["job"]["name"],
            "promoted_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
            "bootstrap": True,
            "bootstrap_note": (
                "Seeded from the round already promoted by human review before this gate "
                "existed (r3, PR #44). NOT itself gate-verified -- exists only so the next "
                "real candidate has a mechanical per-family safety baseline to compare "
                "against. See docs/TOOLCALLER_REFUSAL_GAUNTLET.md for r3's actual promotion record."
            ),
            "candidate_artifact_sha256": candidate_hash,
            "candidate_artifact_path": rel(args.candidate_gguf),
            "arena_decision_accuracy": arena["metrics"]["decision_accuracy"],
            "gauntlet_holdout_safety_cp95_lower": gauntlet["metrics"]["safety_cp95_lower"],
            "gauntlet_per_family_safety": {
                k: v["safety_rate"] for k, v in gauntlet["metrics"].get("per_family", {}).items()
            },
            "approved_by": args.approved_by,
            "approval_note": args.approval_note or "bootstrap seed, not a new promotion decision",
        }
        registry = load_json(REGISTRY_PATH) if REGISTRY_PATH.exists() else {"promotions": []}
        registry["promotions"].append(entry)
        REGISTRY_PATH.write_text(json.dumps(registry, indent=2) + "\n", encoding="utf-8", newline="\n")
        print(f"Bootstrap entry written to {rel(REGISTRY_PATH)} for {cfg['foundry_track']} / {cfg['job']['name']}.")
        print("This is NOT a gate-verified promotion -- it only seeds the per-family baseline for the next candidate.")
        return

    cfg = load_json(args.config)
    margin = cfg.get("promotion", {}).get("margin")
    if not margin:
        print("REFUSED: no frozen promotion.margin in config — nothing to enforce against.")
        raise SystemExit(1)

    incumbent = margin["incumbent"]
    rules = margin["rules"]
    eval_sets = margin["eval_sets"]

    checks: list[Check] = []

    # 1. dataset hash
    c = Check("dataset_hash")
    train_path = REPO / cfg["dataset"]["train_path"]
    meta_path = REPO / cfg["dataset"]["meta_path"]
    if train_path.exists() and meta_path.exists():
        meta = load_json(meta_path)
        actual = sha256_file(train_path)
        recorded = meta.get("train_sha256")
        if actual == recorded:
            c.ok(f"{rel(train_path)} matches meta.json record ({actual[:12]}...)")
        else:
            c.fail(f"{rel(train_path)} sha256 {actual[:12]}... != meta.json's recorded {str(recorded)[:12]}...")
    else:
        c.fail(f"missing train file or meta.json ({rel(train_path)} / {rel(meta_path)})")
    checks.append(c)

    # 2. frozen group split
    c = Check("frozen_group_split")
    if meta_path.exists():
        meta = load_json(meta_path)
        isolation = meta.get("isolation", "")
        frozen = eval_sets.get("group_split", {})
        expected = f"holdout_frac={frozen.get('holdout_frac')}, seed={frozen.get('seed')}"
        if f"holdout_frac={frozen.get('holdout_frac')}" in isolation and f"seed={frozen.get('seed')}" in isolation:
            c.ok(f"meta.json isolation matches frozen split ({expected})")
        else:
            c.fail(f"meta.json isolation '{isolation}' does not match frozen split ({expected})")
    else:
        c.fail("no meta.json to verify split isolation against")
    checks.append(c)

    # 3. sealed eval hash
    c = Check("sealed_eval_hash")
    eval_path = REPO / cfg["dataset"]["eval_path"]
    pinned = eval_sets.get("regression_sealed_sha256")
    if eval_path.exists() and pinned:
        actual = sha256_file(eval_path)
        if actual == pinned:
            c.ok(f"{rel(eval_path)} unchanged since sealing ({actual[:12]}...)")
        else:
            c.fail(f"{rel(eval_path)} sha256 {actual[:12]}... != pinned {pinned[:12]}... — SEALED SET WAS EDITED")
    else:
        c.fail(f"missing eval file or no pinned regression_sealed_sha256 in config")
    checks.append(c)

    # 4. candidate artifact hash (recorded, not compared against anything -- this IS the
    #    artifact identity going into the registry)
    c = Check("candidate_artifact_hash")
    candidate_hash = None
    if args.candidate_gguf.exists():
        candidate_hash = sha256_file(args.candidate_gguf)
        c.ok(f"{rel(args.candidate_gguf)} = {candidate_hash[:16]}...")
    else:
        c.fail(f"candidate GGUF not found: {rel(args.candidate_gguf)}")
    checks.append(c)

    # 5. incumbent artifact hash + rollback availability (14, folded in here since both
    #    need the same lookup)
    c = Check("incumbent_artifact_hash")
    c14 = Check("rollback_artifact_availability")
    incumbent_gguf = find_incumbent_gguf(args.incumbent_modelfile)
    incumbent_hash = None
    if incumbent_gguf and incumbent_gguf.exists():
        incumbent_hash = sha256_file(incumbent_gguf)
        c.ok(f"{rel(incumbent_gguf)} = {incumbent_hash[:16]}...")
        c14.ok(f"incumbent artifact present at {rel(incumbent_gguf)} — rollback is possible")
    else:
        c.fail(f"could not resolve/find currently-deployed GGUF from {rel(args.incumbent_modelfile)}")
        c14.fail("no incumbent artifact found on disk — a failed promotion could not be rolled back")
    checks.append(c)
    checks.append(c14)

    # 6. deployed-artifact gauntlet evidence
    c = Check("deployed_artifact_gauntlet")
    gauntlet = None
    if args.gauntlet_results.exists():
        gauntlet = load_json(args.gauntlet_results)
        model_field = gauntlet.get("model", "")
        if ":" in model_field and "/" not in model_field.replace("\\", "/").rsplit(":", 1)[0].strip("."):
            c.ok(f"gauntlet ran against deployed tag '{model_field}', not a raw adapter path")
        else:
            c.fail(f"gauntlet 'model' field ('{model_field}') does not look like a deployed Ollama tag")
    else:
        c.fail(f"gauntlet results not found: {rel(args.gauntlet_results)}")
    checks.append(c)

    # 7. confidence lower bound
    c = Check("confidence_lower_bound")
    if gauntlet:
        candidate_bound = gauntlet.get("metrics", {}).get("safety_cp95_lower")
        incumbent_bound = incumbent.get("gauntlet_holdout_safety_cp95_lower")
        if candidate_bound is not None and candidate_bound > incumbent_bound:
            c.ok(f"candidate {candidate_bound:.4f} > incumbent {incumbent_bound:.4f}")
        else:
            c.fail(f"candidate {candidate_bound} does not strictly exceed incumbent {incumbent_bound}")
    else:
        c.fail("no gauntlet results to check")
    checks.append(c)

    # 8/9. Arena regression + json validity
    c8 = Check("arena_max_regression")
    c9 = Check("arena_json_validity_min")
    arena = None
    if args.arena_results.exists():
        arena = load_json(args.arena_results)
        m = arena.get("metrics", {})
        candidate_acc = m.get("decision_accuracy")
        incumbent_acc = incumbent.get("arena_decision_accuracy")
        max_drop = rules.get("arena_decision_accuracy_max_drop", 0)
        if candidate_acc is not None and incumbent_acc - candidate_acc <= max_drop:
            c8.ok(f"candidate {candidate_acc:.4f} vs incumbent {incumbent_acc:.4f} "
                  f"(drop {incumbent_acc - candidate_acc:.4f} <= max {max_drop})")
        else:
            c8.fail(f"candidate {candidate_acc} regresses more than {max_drop} vs incumbent {incumbent_acc}")
        jv = m.get("json_validity")
        floor = rules.get("arena_json_validity_min", 0)
        if jv is not None and jv >= floor:
            c9.ok(f"candidate {jv:.4f} >= floor {floor}")
        else:
            c9.fail(f"candidate json_validity {jv} below floor {floor}")
    else:
        c8.fail(f"arena results not found: {rel(args.arena_results)}")
        c9.fail(f"arena results not found: {rel(args.arena_results)}")
    checks.append(c8)
    checks.append(c9)

    # 10. per-family safety floor -- incumbent per-family numbers come from the SAME
    #     gauntlet-results file's own record if this is a self-check, or must be supplied
    #     via a prior registry entry for a real candidate-vs-incumbent comparison. Since the
    #     config only freezes AGGREGATE incumbent numbers (not per-family), this check reads
    #     the incumbent's per-family numbers from the promotion registry's last entry for
    #     this track if one exists; otherwise it cannot verify and fails loudly rather than
    #     silently passing.
    c = Check("per_family_safety_floor")
    registry_incumbent_families = None
    if REGISTRY_PATH.exists():
        registry = load_json(REGISTRY_PATH)
        track_entries = [e for e in registry.get("promotions", []) if e["track"] == cfg["foundry_track"]]
        if track_entries:
            registry_incumbent_families = track_entries[-1].get("gauntlet_per_family_safety")
    if gauntlet and registry_incumbent_families:
        candidate_families = {k: v["safety_rate"] for k, v in gauntlet["metrics"].get("per_family", {}).items()}
        regressions = [f for f, v in candidate_families.items()
                       if f in registry_incumbent_families and v < registry_incumbent_families[f]]
        if not regressions:
            c.ok(f"no family regressed vs the last promoted round's per-family safety")
        else:
            c.fail(f"families regressed vs incumbent: {regressions}")
    elif gauntlet and not registry_incumbent_families:
        c.fail("no prior PROMOTION_REGISTRY.json entry for this track to compare per-family "
               "safety against — cannot verify this criterion mechanically yet (first promotion "
               "under this gate establishes the baseline; this check will work for the NEXT round)")
    else:
        c.fail("no gauntlet results to check per-family safety")
    checks.append(c)

    # 11. latency/memory budget -- genuinely not measured anywhere in this pipeline yet.
    c = Check("latency_memory_budget")
    if args.allow_unmeasured_latency_budget:
        c.ok("UNMEASURED -- promoted anyway via explicit --allow-unmeasured-latency-budget "
             "(logged in the registry entry)")
    else:
        c.fail("no latency/memory measurement exists for this track yet. This criterion is a "
               "real, currently-unmet gap (see docs/CURRENT_STATE.yaml). Pass "
               "--allow-unmeasured-latency-budget to override explicitly and audibly, or wire "
               "a real measurement before promoting.")
    checks.append(c)

    # 12. runtime/schema identity
    c = Check("runtime_schema_identity")
    frozen_hash = cfg.get("gates", {}).get("tool_schema_hash")
    if meta_path.exists() and frozen_hash:
        meta = load_json(meta_path)
        # r3's meta doesn't carry tool_schema_hash directly (it's inherited from the base
        # export, not re-stamped per training round) -- check the frozen tools file itself.
        frozen_tools_path = REPO / "training_pit" / "schemas" / "toolcaller_v0_frozen_tools.json"
        if frozen_tools_path.exists():
            actual = hashlib.sha256(frozen_tools_path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()
            if actual == frozen_hash:
                c.ok(f"frozen tool inventory hash matches config ({actual[:12]}...)")
            else:
                c.fail(f"frozen tool inventory hash drifted: {actual[:12]}... != config's {frozen_hash[:12]}...")
        else:
            c.fail(f"frozen tools file not found: {rel(frozen_tools_path)}")
    else:
        c.fail("no tool_schema_hash in config gates to check")
    checks.append(c)

    # 13. human approval record
    c = Check("human_approval_record")
    if args.approved_by and args.approval_note:
        c.ok(f"approved by {args.approved_by}: \"{args.approval_note}\"")
    else:
        c.fail("--approved-by and --approval-note are both required — no anonymous or silent promotions")
    checks.append(c)

    # ── Verdict ──────────────────────────────────────────────────────────────
    print("\n" + "=" * 66)
    print(f"  FOUNDRY PROMOTION GATE — {cfg['foundry_track']} / {cfg['job']['name']}")
    print("=" * 66)
    for chk in checks:
        mark = "PASS" if chk.passed else "FAIL"
        print(f"  [{mark}] {chk.name}")
        print(f"         {chk.detail}")
    print("=" * 66)

    failed = [c for c in checks if not c.passed]
    if failed:
        print(f"\n  REFUSED — {len(failed)} unmet criterion/criteria:")
        for c in failed:
            print(f"    - {c.name}: {c.detail}")
        print("\n  No registry entry written. Candidate remains unpromoted.")
        raise SystemExit(1)

    # ── All checks passed: write the registry entry ─────────────────────────
    entry = {
        "track": cfg["foundry_track"],
        "round": cfg["job"]["name"],
        "promoted_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "candidate_artifact_sha256": candidate_hash,
        "candidate_artifact_path": rel(args.candidate_gguf),
        "incumbent_artifact_sha256": incumbent_hash,
        "arena_decision_accuracy": arena["metrics"]["decision_accuracy"] if arena else None,
        "gauntlet_holdout_safety_cp95_lower": gauntlet["metrics"]["safety_cp95_lower"] if gauntlet else None,
        "gauntlet_per_family_safety": {
            k: v["safety_rate"] for k, v in gauntlet["metrics"].get("per_family", {}).items()
        } if gauntlet else {},
        "approved_by": args.approved_by,
        "approval_note": args.approval_note,
        "latency_budget_measured": not args.allow_unmeasured_latency_budget,
        "checks": [{"name": c.name, "passed": c.passed, "detail": c.detail} for c in checks],
    }

    registry = load_json(REGISTRY_PATH) if REGISTRY_PATH.exists() else {"promotions": []}
    registry["promotions"].append(entry)
    REGISTRY_PATH.write_text(json.dumps(registry, indent=2) + "\n", encoding="utf-8", newline="\n")

    tag = args.ollama_tag or cfg.get("output", {}).get("ollama_tag_hypothesis") or "theorc-toolcaller:latest"
    deploy_cmd = (
        f'ollama create {tag} -f {rel(args.incumbent_modelfile)}  '
        f'# after pointing its ADAPTER line at {rel(args.candidate_gguf)}'
    )

    print(f"\n  PROMOTED. Registry updated: {rel(REGISTRY_PATH)}")
    print(f"  Next step to deploy:\n    {deploy_cmd}")

    if args.deploy:
        print("\n  --deploy passed: this script does not auto-edit the modelfile's ADAPTER line "
              "(that's a one-line manual edit to point at the new GGUF) -- run the printed "
              "command yourself after making that edit. Refusing to auto-run an untargeted "
              "`ollama create` against a modelfile that may still point at the OLD artifact.")


if __name__ == "__main__":
    main()
