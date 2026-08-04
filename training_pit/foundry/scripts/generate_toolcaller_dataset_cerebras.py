#!/usr/bin/env python3
"""
generate_toolcaller_dataset_cerebras.py — Cerebras-powered toolcaller dataset generator.

Standalone sibling to generate_toolcaller_dataset.py (native/claude/ollama backends),
mirroring generate_v4gold_cerebras.py's pattern: Cerebras is an external development tool
used to generate synthetic training data, kept deliberately separate from TheOrc's own
runtime/backends rather than added as another --api choice on the shared script
(hardcoreerik, 2026-08-02 — "Cerebras... is not meant to be wired into TheOrc. This is
meant as an external development tool.").

Reuses every scenario/prompt/validation helper from generate_toolcaller_dataset.py by
import (ROLE_TOOLS, plan_scenarios, build_generator_prompt, extract_json,
validate_and_build_capture, write_progress, ...) — this script supplies only the Cerebras
client and its own CLI entrypoint, so the two backends can never validate examples
differently by drifting out of sync.

Usage:
    $env:CEREBRAS_API_KEY = "csk-..."
    python training_pit/foundry/scripts/generate_toolcaller_dataset_cerebras.py --count 200
    python training_pit/foundry/scripts/generate_toolcaller_dataset_cerebras.py --count 500 --model gpt-oss-120b

Requires: pip install openai
"""

import sys
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

sys.path.insert(0, str(Path(__file__).parent))
from generate_toolcaller_dataset import (  # noqa: E402
    CAPTURES_DIR,
    FROZEN_TOOLS_PATH,
    OUTPUTS_DIR,
    build_generator_prompt,
    extract_json,
    plan_scenarios,
    validate_and_build_capture,
    write_progress,
    GENERATOR_SYSTEM,
)

import argparse
import json
import os
import random
import time
from datetime import datetime, timezone

CEREBRAS_BASE_URL = "https://api.cerebras.ai/v1"
DEFAULT_MODEL     = "gpt-oss-120b"


def cerebras_generate(client, model: str, system: str, prompt: str,
                       timeout: int = 60, max_retries: int = 5) -> str:
    for attempt in range(max_retries):
        try:
            resp = client.chat.completions.create(
                model=model,
                messages=[
                    {"role": "system", "content": system},
                    {"role": "user", "content": prompt},
                ],
                max_completion_tokens=512,
                temperature=0.8,
                timeout=timeout,
            )
            return (resp.choices[0].message.content or "").strip()
        except Exception as exc:
            msg = str(exc)
            if "rate" in msg.lower() or "429" in msg:
                wait = 2 ** attempt
                print(f"  [rate-limited] backing off {wait}s...", flush=True)
                time.sleep(wait)
                continue
            raise
    raise RuntimeError(f"Cerebras call failed after {max_retries} retries.")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--model", default=DEFAULT_MODEL, help=f"Cerebras model (default {DEFAULT_MODEL})")
    ap.add_argument("--count", type=int, default=200)
    ap.add_argument("--key",   default="toolcaller_cerebras")
    ap.add_argument("--seed",  type=int, default=42)
    args = ap.parse_args()

    try:
        from openai import OpenAI
    except ImportError:
        print("ERROR: openai SDK not installed — run: pip install openai", flush=True)
        sys.exit(1)

    api_key = os.environ.get("CEREBRAS_API_KEY", "").strip()
    if not api_key:
        print("ERROR: CEREBRAS_API_KEY env var not set.", flush=True)
        sys.exit(1)

    client = OpenAI(api_key=api_key, base_url=CEREBRAS_BASE_URL)

    rng = random.Random(args.seed)

    if not FROZEN_TOOLS_PATH.exists():
        print(f"ERROR: frozen tool schema not found: {FROZEN_TOOLS_PATH}", flush=True)
        sys.exit(1)
    raw_schemas: list[dict] = json.loads(FROZEN_TOOLS_PATH.read_text(encoding="utf-8"))
    tool_schemas = {s["name"]: s for s in raw_schemas}

    CAPTURES_DIR.mkdir(parents=True, exist_ok=True)
    out_dir = OUTPUTS_DIR / f"gen_{args.key}"
    out_dir.mkdir(parents=True, exist_ok=True)
    progress_path = out_dir / "gen_progress.json"

    existing = sorted(CAPTURES_DIR.glob("*.json"))
    counter  = len(existing) + 1

    print("=== toolcaller dataset generator (Cerebras) ===", flush=True)
    print(f"model       : {args.model}", flush=True)
    print(f"target count: {args.count}", flush=True)
    print(f"output dir  : {CAPTURES_DIR}", flush=True)
    print(f"progress    : {progress_path}", flush=True)
    print(flush=True)

    try:
        test = cerebras_generate(client, args.model, "Reply with the word OK.", "OK", timeout=15)
        if not test:
            raise ValueError("empty response")
    except Exception as e:
        print(f"ERROR: Cerebras API not reachable ({args.model}): {e}", flush=True)
        sys.exit(1)
    print(f"Cerebras API ready ({args.model})", flush=True)

    write_progress(progress_path, "running", 0, 0, args.count)

    scenarios = plan_scenarios(args.count, rng)
    generated = 0
    rejected  = 0

    for role, decision, tool, context_hint in scenarios:
        if generated >= args.count:
            break

        prompt = build_generator_prompt(role, decision, tool, context_hint, tool_schemas)

        try:
            raw_text = cerebras_generate(client, args.model, GENERATOR_SYSTEM, prompt)
        except Exception as e:
            print(f"  [warn] Cerebras error: {e}", flush=True)
            rejected += 1
            continue

        raw_dict = extract_json(raw_text)
        if raw_dict is None:
            print(f"  [reject] no JSON in response (decision={decision}, role={role})", flush=True)
            rejected += 1
            continue

        ts         = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
        example_id = f"tc_{ts}_{counter:04d}"
        lineage_id = f"tc_lg_{example_id}"

        capture = validate_and_build_capture(
            raw=raw_dict,
            role=role,
            tool_schemas=tool_schemas,
            lineage_id=lineage_id,
            example_id=example_id,
            model_name=args.model,
            teacher_model=args.model,
        )

        if capture is None:
            print(f"  [reject] validation failed (decision={decision}, role={role})", flush=True)
            rejected += 1
            continue

        out_file = CAPTURES_DIR / f"toolcaller_capture_{example_id}.json"
        out_file.write_text(json.dumps(capture, indent=2, ensure_ascii=False), encoding="utf-8")
        generated += 1
        counter   += 1

        req_preview = capture["request"][:60].replace("\n", " ")
        print(f"  [{generated:>4}/{args.count}] {decision:12s} {role:12s} "
              f"{(tool or '-'):12s} — {req_preview!r}", flush=True)

        write_progress(progress_path, "running", generated, rejected, args.count)
        time.sleep(0.05)  # Cerebras is fast — light breathing room only

    write_progress(progress_path, "done", generated, rejected, args.count)

    print(flush=True)
    print(f"=== done: {generated} valid, {rejected} rejected ===", flush=True)
    print("Next step: THE FOUNDRY → 'Validate captures' → 'Export dataset' → 'Train toolcaller'", flush=True)

    if generated < args.count:
        print(f"WARNING: only {generated} of {args.count} target reached "
              f"({rejected} rejected). Consider a higher-capacity model or re-run.", flush=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
