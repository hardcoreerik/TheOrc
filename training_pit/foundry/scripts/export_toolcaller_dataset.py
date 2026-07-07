#!/usr/bin/env python3
"""Export reviewed toolcaller-v0 captures into training-ready chat JSONL.

Reads capture JSONs (training_pit/TOOLCALLER_CAPTURE_SCHEMA.md), keeps
review_status == "accepted", runs the mechanical validator
(Tools/ToolcallerBench) over the selected set, assigns a lineage-safe
train/eval split, renders each capture into the canonical chat format
({"messages":[system,user,assistant]}), and writes:

  training_pit/datasets/train_{key}.jsonl
  training_pit/datasets/eval_{key}.jsonl
  training_pit/datasets/{key}.meta.json     (provenance sidecar; preflight reads it)

  python training_pit/foundry/scripts/export_toolcaller_dataset.py
  python export_toolcaller_dataset.py --allow-pending      # experiment only; meta is marked
  python export_toolcaller_dataset.py --skip-validator     # meta records verdict "skipped"
"""
import argparse, hashlib, json, shutil, subprocess, sys, tempfile, time
from collections import Counter
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
FROZEN_TOOLS = REPO_ROOT / "training_pit" / "schemas" / "toolcaller_v0_frozen_tools.json"
DATASETS_DIR = REPO_ROOT / "training_pit" / "datasets"
DEFAULT_SOURCES = [
    REPO_ROOT / ".orc" / "swarm" / "dataset-staging" / "toolcaller",
    REPO_ROOT / "training_pit" / "datasets" / "toolcaller",
]
BENCH_CANDIDATES = [
    REPO_ROOT / "Tools" / "ToolcallerBench" / "bin" / "Release" / "net10.0" / "toolcaller-bench.exe",
    REPO_ROOT / "Tools" / "ToolcallerBench" / "bin" / "Debug" / "net10.0" / "toolcaller-bench.exe",
]

SYSTEM_TEMPLATE = """You are theorc-toolcaller, TheOrc's tool-proposal specialist.
Your only job: given a worker role, its available tools, and a request, propose the single correct next action as JSON.

Role: {role}

Available tools:
{tools_block}

Respond with EXACTLY one JSON object, no prose, in one of these shapes:
  {{"decision": "call", "tool": "<name from available tools>", "arguments": {{...exact schema fields only...}}}}
  {{"decision": "no_tool"}}
  {{"decision": "clarify", "reason_code": "<missing_required_argument|ambiguous_target|ambiguous_intent>"}}
  {{"decision": "unsupported", "reason_code": "<no_matching_tool|tool_outside_role>"}}

Rules:
- Never invent tools or argument fields. Arguments must match the tool's schema exactly.
- If required information is missing or ambiguous, choose "clarify" — never fabricate arguments.
- You propose; you never execute. Deterministic policy and human approval decide whether the call runs."""


def sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def sha256_file_lf(path: Path) -> str:
    """Line-ending-normalized hash. The canonical frozen-inventory hash is the LF
    (git blob) form; core.autocrlf checkouts materialize CRLF on disk, so hashing
    raw bytes would wrongly mark every capture stale on Windows."""
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def load_frozen_tools() -> tuple[list[dict], str]:
    tools = json.loads(FROZEN_TOOLS.read_text(encoding="utf-8"))
    return tools, sha256_file_lf(FROZEN_TOOLS)


def render_tools_block(names: list[str], frozen: dict[str, dict]) -> str:
    lines = []
    for name in names:
        tool = frozen.get(name)
        if tool is None:
            raise ValueError(f"available_tools contains '{name}' which is not in the frozen inventory")
        params = tool.get("parameters", {}) or {}
        required = set(tool.get("required", []) or [])
        plines = [f"    - {p} ({spec.get('type', '?')}{', required' if p in required else ''}): {spec.get('description', '')}"
                  for p, spec in params.items()]
        lines.append(f"- {name}: {tool.get('description', '')}")
        lines.extend(plines if plines else ["    (no parameters)"])
    return "\n".join(lines)


def render_assistant(expected: dict) -> str:
    decision = expected.get("decision")
    out: dict = {"decision": decision}
    if decision == "call":
        out["tool"] = expected["tool"]
        out["arguments"] = expected["arguments"]
    elif decision in ("clarify", "unsupported"):
        out["reason_code"] = expected["reason_code"]
    return json.dumps(out, ensure_ascii=False)


def capture_to_chat(cap: dict, frozen: dict[str, dict]) -> dict:
    system = SYSTEM_TEMPLATE.format(
        role=cap["role"],
        tools_block=render_tools_block(cap["available_tools"], frozen))
    user = cap["request"]
    approval = cap.get("approval_state")
    if approval and approval != "n/a":
        user += f"\n\n[approval context: {approval}]"
    return {
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
            {"role": "assistant", "content": render_assistant(cap["expected"])},
        ],
        "example_id": cap.get("example_id", ""),
        "lineage_group_id": cap.get("lineage_group_id") or cap.get("example_id", ""),
        "decision": cap["expected"].get("decision", ""),
        "role": cap.get("role", ""),
        "source_type": (cap.get("provenance") or {}).get("source_type", ""),
    }


def assign_split(cap: dict, eval_frac: float) -> str:
    explicit = cap.get("split")
    if explicit in ("train", "eval"):
        return explicit
    # Deterministic lineage-group split: every sibling of a group lands on the
    # same side, and re-running the exporter never reshuffles history.
    group = cap.get("lineage_group_id") or cap.get("example_id", "")
    bucket = int(hashlib.sha256(group.encode("utf-8")).hexdigest()[:8], 16) % 100
    return "eval" if bucket < int(eval_frac * 100) else "train"


def run_validator(files: list[Path], out_dir: Path) -> str:
    bench = next((p for p in BENCH_CANDIDATES if p.exists()), None)
    tmp = Path(tempfile.mkdtemp(prefix="toolcaller_export_"))
    try:
        # Index-prefix the copies: same-named captures from different source dirs
        # must not collapse into one file, or the validator sees fewer examples
        # than the exporter writes.
        for idx, f in enumerate(files):
            shutil.copy2(f, tmp / f"{idx:06d}_{f.name}")
        if bench is not None:
            cmd = [str(bench)]
        else:
            print("[validator] no built toolcaller-bench.exe found — using 'dotnet run' (slower)")
            cmd = ["dotnet", "run", "--project",
                   str(REPO_ROOT / "Tools" / "ToolcallerBench"), "--"]
        cmd += ["--suite", "validate", "--captures", str(tmp),
                "--tools", str(FROZEN_TOOLS), "--output", str(out_dir / "bench")]
        proc = subprocess.run(cmd, capture_output=True, text=True, cwd=REPO_ROOT)
        sys.stdout.write(proc.stdout)
        sys.stderr.write(proc.stderr)
        if proc.returncode == 0:
            return "PASS"
        if proc.returncode == 2:
            return "FAIL"
        raise RuntimeError(f"toolcaller-bench errored (exit {proc.returncode})")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--captures", action="append", type=Path,
                    help="capture directory (repeatable). Default: staging + datasets/toolcaller")
    ap.add_argument("--out-key", default="toolcaller_v0",
                    help="dataset key — writes train_{key}.jsonl / eval_{key}.jsonl / {key}.meta.json")
    ap.add_argument("--eval-frac", type=float, default=0.2,
                    help="eval fraction for captures without a preassigned split (default 0.2)")
    ap.add_argument("--allow-pending", action="store_true",
                    help="include review_status=pending captures (meta is marked; preflight will reject "
                         "the export for a real training run)")
    ap.add_argument("--skip-validator", action="store_true",
                    help="skip the ToolcallerBench mechanical gate (meta records verdict 'skipped')")
    args = ap.parse_args()

    sources = args.captures or [d for d in DEFAULT_SOURCES if d.exists()]
    if not sources:
        print("No capture directories found. Expected captures under:")
        for d in DEFAULT_SOURCES:
            print(f"  {d}")
        print("Run swarm sessions to stage organic captures (ToolcallerDatasetCapture is on by "
              "default), or author examples per training_pit/TOOLCALLER_CAPTURE_SCHEMA.md.")
        raise SystemExit(1)

    frozen_list, tools_hash = load_frozen_tools()
    frozen = {t["name"]: t for t in frozen_list}

    captures, files, skipped = [], [], Counter()
    for src in sources:
        for f in sorted(Path(src).glob("*.json")):
            try:
                cap = json.loads(f.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                skipped["unparseable"] += 1
                continue
            if cap.get("schema_version") != "toolcaller-v0":
                skipped["not_toolcaller"] += 1
                continue
            status = cap.get("review_status", "pending")
            if status == "rejected":
                skipped["rejected"] += 1
                continue
            if status != "accepted" and not args.allow_pending:
                skipped["pending"] += 1
                continue
            if cap.get("tool_schema_hash") != tools_hash:
                skipped["stale_schema_hash"] += 1
                continue
            captures.append((cap, status))
            files.append(f)

    print(f"Selected {len(captures)} capture(s) from {len(sources)} source dir(s); skipped: "
          f"{dict(skipped) or 'none'}")
    if not captures:
        print("Nothing to export. Accepted captures are required "
              "(or pass --allow-pending for a marked experimental export).")
        raise SystemExit(1)

    out_dir = REPO_ROOT / "training_pit" / "outputs" / f"foundry_{args.out_key}"
    out_dir.mkdir(parents=True, exist_ok=True)
    DATASETS_DIR.mkdir(parents=True, exist_ok=True)

    if args.skip_validator:
        verdict = "skipped"
        print("[validator] SKIPPED by flag — this export cannot pass training preflight gates")
    else:
        verdict = run_validator(files, out_dir)
        if verdict != "PASS":
            print(f"[validator] {verdict} — fix the findings (report under {out_dir / 'bench'}) "
                  "and re-export. Nothing was written.")
            raise SystemExit(2)

    rows = {"train": [], "eval": []}
    group_split: dict[str, str] = {}
    for cap, _status in captures:
        split = assign_split(cap, args.eval_frac)
        group = cap.get("lineage_group_id") or cap.get("example_id", "")
        prior = group_split.setdefault(group, split)
        if prior != split:
            print(f"[split] lineage group '{group}' straddles train/eval via preassigned splits — "
                  "hard admission failure (TOOLCALLER_CAPTURE_SCHEMA.md). Nothing was written.")
            raise SystemExit(2)
        rows[prior].append(capture_to_chat(cap, frozen))

    train_path = DATASETS_DIR / f"train_{args.out_key}.jsonl"
    eval_path = DATASETS_DIR / f"eval_{args.out_key}.jsonl"
    for path, split in ((train_path, "train"), (eval_path, "eval")):
        with path.open("w", encoding="utf-8") as fh:
            for row in rows[split]:
                fh.write(json.dumps(row, ensure_ascii=False) + "\n")

    balance = {split: dict(Counter(r["decision"] for r in rows[split])) for split in rows}
    contains_pending = any(status != "accepted" for _cap, status in captures)
    meta = {
        "key": args.out_key,
        "schema_version": "toolcaller-v0",
        "created": time.strftime("%Y-%m-%d %H:%M:%S"),
        "notes": "Foundry toolcaller v0 export — bounded tool-proposal decisions "
                 "(training_pit/TOOLCALLER_CAPTURE_SCHEMA.md)",
        "role": "toolcaller",
        "data_type": "chat-sft",
        "source": "+".join(str(s) for s in sources),
        "tool_schema_hash": tools_hash,
        "train_count": len(rows["train"]),
        "eval_count": len(rows["eval"]),
        "decision_balance": balance,
        "contains_pending": contains_pending,
        "validator": {"suite": "toolcaller-bench validate", "verdict": verdict},
        "lineage_groups": {
            split: sorted({r["lineage_group_id"] for r in rows[split]}) for split in rows
        },
        "train_sha256": sha256_file(train_path),
        "eval_sha256": sha256_file(eval_path),
    }
    meta_path = DATASETS_DIR / f"{args.out_key}.meta.json"
    meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")

    print(f"\nWrote {len(rows['train'])} train -> {train_path}")
    print(f"Wrote {len(rows['eval'])} eval  -> {eval_path}")
    print(f"Meta sidecar -> {meta_path}")
    print(f"Decision balance: {json.dumps(balance)}")
    missing = [d for d in ("call", "no_tool", "clarify", "unsupported")
               if not balance.get("eval", {}).get(d)]
    if missing:
        print(f"[balance] eval set has NO examples for: {', '.join(missing)} — "
              "THEORC_TOOLCALLER_V0.md requires a balanced held-out set; author or "
              "bootstrap these categories before treating eval numbers as meaningful.")
    if contains_pending:
        print("[review] export contains PENDING captures — marked in meta; training preflight "
              "will reject it until the captures are accepted and re-exported.")


if __name__ == "__main__":
    main()
