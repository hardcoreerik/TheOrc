#!/usr/bin/env python3
"""Foundry pre-training gate — nothing trains unless every check passes.

Config-driven sibling of phase3_preflight.py for the Foundry specialist tracks
(training_pit/foundry/configs/*.json). Deterministic: same inputs, same verdict.

  python training_pit/foundry/scripts/foundry_preflight.py --config training_pit/foundry/configs/toolcaller_v0.json

Importable: train_foundry.py calls run_preflight() before allocating VRAM.
"""
import argparse, hashlib, json
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
FROZEN_TOOLS = REPO_ROOT / "training_pit" / "schemas" / "toolcaller_v0_frozen_tools.json"


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def sha256_file_lf(path: Path) -> str:
    """Line-ending-normalized hash — the canonical frozen-inventory hash is the
    LF (git blob) form; CRLF checkouts must not read as a changed inventory."""
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def load_config(config_path: Path) -> dict:
    cfg = json.loads(config_path.read_text(encoding="utf-8"))
    for key in ("foundry_track", "status", "job", "base_model", "adapter", "dataset", "hyperparams", "gates", "output"):
        if key not in cfg:
            raise ValueError(f"config missing required key '{key}': {config_path}")
    return cfg


def _load_jsonl(path: Path) -> list[dict]:
    rows = []
    with path.open(encoding="utf-8") as fh:
        for i, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError as ex:
                raise ValueError(f"{path.name}:{i} is not valid JSON ({ex})")
    return rows


def run_preflight(cfg: dict, repo_root: Path = REPO_ROOT) -> list[str]:
    """Returns a list of blocking findings; empty list means GO."""
    findings: list[str] = []
    gates = cfg["gates"]

    # ── Track authorization ──────────────────────────────────────────────
    if cfg["status"] != "active":
        reason = gates.get("blocked_reason", "track status is not 'active'")
        findings.append(f"TRACK BLOCKED [{cfg['foundry_track']}]: {reason}")
        return findings  # nothing else is meaningful for a template track

    # ── Dataset presence + counts ────────────────────────────────────────
    ds = cfg["dataset"]
    paths = {}
    for name in ("train_path", "eval_path"):
        raw = ds.get(name)
        if not raw:
            findings.append(f"DATASET: {name} is not set — run the exporter first ({ds.get('exporter')})")
            continue
        p = repo_root / raw
        if not p.exists():
            findings.append(f"DATASET: {p} does not exist — run the exporter first ({ds.get('exporter')})")
            continue
        paths[name] = p
    if len(paths) < 2:
        return findings

    train_rows = _load_jsonl(paths["train_path"])
    eval_rows = _load_jsonl(paths["eval_path"])
    if len(train_rows) < gates["min_train_examples"]:
        findings.append(f"COUNT: train has {len(train_rows)} examples, gate requires "
                        f">= {gates['min_train_examples']}")
    if len(eval_rows) < gates["min_eval_examples"]:
        findings.append(f"COUNT: eval has {len(eval_rows)} examples, gate requires "
                        f">= {gates['min_eval_examples']}")
    def _valid_row(r):
        msgs = r.get("messages")
        return (isinstance(msgs, list) and len(msgs) >= 2 and
                all(isinstance(m, dict) and isinstance(m.get("role"), str) and
                    isinstance(m.get("content"), str) for m in msgs))

    for name, rows in (("train", train_rows), ("eval", eval_rows)):
        bad = sum(1 for r in rows if not _valid_row(r))
        if bad:
            findings.append(f"FORMAT: {bad} {name} row(s) missing a valid 'messages' array "
                            "(each message must be an object with string role/content)")

    # ── Meta sidecar: review state, validator verdict, dataset drift ─────
    meta = None
    meta_raw = ds.get("meta_path")
    meta_required = bool(gates.get("reject_pending_review") or
                         gates.get("require_validator_pass") or
                         gates.get("require_lineage_split_isolation") or
                         gates.get("tool_schema_hash"))
    if not meta_raw and meta_required:
        findings.append("META: this recipe's gates need the export meta sidecar, but "
                        "dataset.meta_path is not set — the gates cannot be verified")
    if meta_raw:
        meta_path = repo_root / meta_raw
        if not meta_path.exists():
            findings.append(f"META: sidecar {meta_path} missing — re-run the exporter")
        else:
            meta = json.loads(meta_path.read_text(encoding="utf-8"))
            if gates.get("reject_pending_review") and meta.get("contains_pending"):
                findings.append("REVIEW: export contains pending (unreviewed) captures — "
                                "accept them and re-export before training")
            if gates.get("require_validator_pass"):
                verdict = (meta.get("validator") or {}).get("verdict")
                if verdict != "PASS":
                    findings.append(f"VALIDATOR: meta records verdict '{verdict}' — a PASS from the "
                                    "mechanical validator is required")
            for name, path in paths.items():
                key = ("train_sha256" if name == "train_path" else "eval_sha256")
                recorded = meta.get(key)
                if recorded and recorded != sha256_file(path):
                    findings.append(f"DRIFT: {path.name} changed since export "
                                    f"(meta {key} mismatch) — re-run the exporter")

    # ── Frozen tool inventory hash (toolcaller-specific) ─────────────────
    expected_hash = gates.get("tool_schema_hash")
    if expected_hash:
        if not FROZEN_TOOLS.exists():
            findings.append(f"SCHEMA: frozen tool inventory missing: {FROZEN_TOOLS}")
        elif sha256_file_lf(FROZEN_TOOLS) != expected_hash:
            findings.append("SCHEMA: frozen tool inventory hash changed since this recipe was "
                            "frozen — regenerate/revalidate the dataset and update gates.tool_schema_hash")
        if meta is not None and meta.get("tool_schema_hash") != expected_hash:
            findings.append("SCHEMA: dataset was exported under a different (or unrecorded) "
                            "frozen inventory hash")

    # ── Leakage: lineage groups and exact assistant responses ────────────
    if gates.get("require_lineage_split_isolation"):
        if meta and isinstance(meta.get("lineage_groups"), dict):
            overlap = set(meta["lineage_groups"].get("train", [])) & \
                      set(meta["lineage_groups"].get("eval", []))
            if overlap:
                findings.append(f"LEAKAGE: {len(overlap)} lineage group(s) present in both splits: "
                                f"{sorted(overlap)[:5]}")
        def _answers(rows):
            return {m["content"] for r in rows if _valid_row(r)
                    for m in r["messages"] if m.get("role") == "assistant"}
        exact = _answers(train_rows) & _answers(eval_rows)
        # Identical short decisions ({"decision": "no_tool"}) legitimately recur;
        # only flag identical CALL payloads, where duplication means a shared case.
        exact = {a for a in exact if '"call"' in a}
        if exact:
            findings.append(f"LEAKAGE: {len(exact)} identical 'call' response(s) appear in both "
                            "train and eval — eval numbers would be inflated")

    # ── Baseline report (warn-level: required for promotion, not experiment) ──
    baseline = gates.get("baseline_report")
    if baseline and not (repo_root / baseline).exists():
        print(f"[WARN] baseline report missing ({baseline}) — an adapter trained now cannot be "
              "PROMOTED until the baseline comparison exists (docs/THEORC_TOOLCALLER_V0.md is a "
              "kill gate). Fine for a first experiment; do not skip it before Arena comparison.")

    return findings


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True, type=Path)
    args = ap.parse_args()

    cfg = load_config(args.config)
    findings = run_preflight(cfg)
    if findings:
        print(f"PREFLIGHT: BLOCKED — {len(findings)} finding(s) for {cfg['foundry_track']}:")
        for f in findings:
            print(f"  [x] {f}")
        raise SystemExit(1)
    print(f"PREFLIGHT: GO — {cfg['foundry_track']} passed every gate.")


if __name__ == "__main__":
    main()
