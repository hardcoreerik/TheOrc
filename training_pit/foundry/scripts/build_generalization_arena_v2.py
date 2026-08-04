#!/usr/bin/env python3
"""
build_generalization_arena_v2.py — sealed held-out-family generalization arena.

docs/NATIVE_RUNTIME_V2_SPEC.md Phase E; training_pit/foundry PLAN elegant-bubbling-coral.md,
"What's new for v2" §4. This is the eval that answers the question nothing in the pre-v2
pipeline could ask: "can theorc-toolcaller correctly CALL a tool it has never seen during
training, from its in-context schema alone?" Every prior eval (the sealed Arena, the refusal
gauntlet, even v2's own plausible_fabrication family) tests REFUSING unfamiliar names or
choosing correctly among FAMILIAR ones -- none test successfully using something genuinely new.

Draws EXCLUSIVELY from:
  - the three held_out=true real families in toolcaller_v2_tool_families.json
    (case_forge, keyhound_atlas, fabric -- 13 real tools, never in any training example), and
  - the held-out synthetic domains in synthetic_tool_schemas.py (timer, unit_conversion --
    3 fictional tools, never in any training example either).
Every "call" target here is therefore something the candidate model has structurally never
seen a fixed name/example for. available_tools mixes the held-out target with OTHER held-out
tools AND a sample of ordinary TRAINABLE tools -- mirrors a real deployment (a freshly
SkillLoader-registered tool sits in the SAME registry as everything else, not in isolation),
so the model must pick the right one out of a realistic, mixed, partly-familiar roster.

SEALED: like eval_toolcaller_v0.jsonl, this file is written once and never touched by any
training-set composition step (Task #6) or promotion run -- if a future round needs a bigger
arena, generate a NEW file with a new name; never append to or regenerate this one in place
once it has been used to score a promoted candidate (same discipline the v0 Arena already
documents).

Usage:
    python training_pit/foundry/scripts/build_generalization_arena_v2.py \\
        --api native --count 150 \\
        --out training_pit/datasets/eval_toolcaller_v2_generalization_arena.jsonl
"""

import contextlib
import json
import os
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from export_toolcaller_dataset import SYSTEM_TEMPLATE, render_tools_block  # noqa: E402
from generate_toolcaller_dataset import (  # noqa: E402
    SwarmCliBackend,
    claude_generate,
    extract_json,
    native_generate,
    native_list_models,
    ollama_generate,
)
from generate_toolcaller_v2_dataset import _load_family_registry, sample_available_tools  # noqa: E402
from synthetic_tool_schemas import FAMILIES_PATH, generate_fictional_tools  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[3]

GENERATOR_SYSTEM = """You are a training-data engineer building a SEALED EVALUATION set for
TheOrc toolcaller-v2 model. Generate ONE example as a JSON object.

You will receive a list of currently-available tools with their real schemas, and a specific
target tool you must write a request for. Your job: generate a realistic, specific request
that a user would plausibly make, that this target tool -- and only this target tool -- would
correctly satisfy, with correct arguments matching its schema exactly.

Output format (JSON object only, no prose, no markdown fences):
{"request": "...", "expected_arguments": {"<param>": "<value>"}}

Rules:
- The request must be 1-3 sentences, realistic.
- expected_arguments must exactly match the target tool's own parameter schema (correct field
  names, realistic values) and include every required field.
- Do not mention the tool's internal name in the request -- describe the user's intent."""


def _load_held_out_real_tools() -> dict[str, dict]:
    data = json.loads(FAMILIES_PATH.read_text(encoding="utf-8"))
    return {
        t["name"]: t
        for fam in data["families"] if fam["held_out"]
        for t in fam["tools"]
    }


def main():
    import argparse
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--api",          choices=["swarmcli", "native", "claude", "ollama"], default=None,
                     help="swarmcli (real in-process Native Runtime v2, no server/Ollama) is preferred; "
                          "native talks to llama-server.exe (third-party server binary).")
    ap.add_argument("--native-host",  default="http://127.0.0.1:8080")
    ap.add_argument("--swarmcli-exe", type=Path,
                     default=Path(__file__).resolve().parents[3] / "Tools" / "SwarmCli" / "bin" / "Debug"
                             / "net10.0-windows" / "swarmcli.exe")
    ap.add_argument("--swarmcli-gguf", type=Path, default=None)
    ap.add_argument("--claude-model", default="claude-haiku-4-5-20251001")
    ap.add_argument("--model",        default="qwen2.5-coder:14b")
    ap.add_argument("--ollama-host",  default="http://localhost:11434")
    ap.add_argument("--count",        type=int, default=150)
    ap.add_argument("--seed",         type=int, default=99)
    ap.add_argument("--out", type=Path,
                     default=REPO_ROOT / "training_pit" / "datasets" / "eval_toolcaller_v2_generalization_arena.jsonl")
    args = ap.parse_args()

    import os
    api_key = os.environ.get("ANTHROPIC_API_KEY")
    # No auto-fallback to Ollama (memory no-ollama-orc-development) -- must be explicit.
    if args.api is None:
        if api_key:
            args.api = "claude"
        elif args.swarmcli_gguf is not None and args.swarmcli_exe.exists() and args.swarmcli_gguf.exists():
            args.api = "swarmcli"
        else:
            print("ERROR: no backend auto-selected. Pass --api swarmcli --swarmcli-gguf <path>, "
                  "--api claude (needs ANTHROPIC_API_KEY), or --api native explicitly.", flush=True)
            sys.exit(1)

    # This is a sealed output file (see module docstring) -- refuse to overwrite one that
    # already exists rather than silently clobber a real, already-scored eval set (CodeRabbit
    # review, PR #99).
    if args.out.exists():
        print(f"ERROR: {args.out} already exists. This is a SEALED file (see module docstring) "
              "-- generate a new file with a new name instead of overwriting it.", flush=True)
        sys.exit(1)

    # Same `with`-block pattern as generate_toolcaller_dataset.py / generate_toolcaller_v2_dataset.py
    # (CodeRabbit review, PR #99): guarantees SwarmCliBackend.__exit__ runs even on an exception.
    swarmcli_backend_cm: SwarmCliBackend | contextlib.AbstractContextManager
    if args.api == "swarmcli":
        if args.swarmcli_gguf is None:
            print("ERROR: --api swarmcli requires --swarmcli-gguf <path-to-gguf>.", flush=True)
            sys.exit(1)
        active_model = args.swarmcli_gguf.stem
        swarmcli_backend_cm = SwarmCliBackend(args.swarmcli_exe, args.swarmcli_gguf)
    else:
        swarmcli_backend_cm = contextlib.nullcontext()

    if args.api == "swarmcli":
        pass  # readiness printed once the `with` block below actually starts the subprocess
    elif args.api == "native":
        active_model = args.model if args.model != "qwen2.5-coder:14b" else (
            (native_list_models(args.native_host) or ["native-unknown"])[0])
    elif args.api == "claude":
        active_model = args.claude_model
    else:
        active_model = args.model

    rng = random.Random(args.seed)

    trainable_schemas, _ = _load_family_registry()
    trainable_names = list(trainable_schemas.keys())

    held_out_real = _load_held_out_real_tools()
    held_out_synth = {t["name"]: {k: v for k, v in t.items() if not k.startswith("_")}
                       for t in generate_fictional_tools(random.Random(args.seed), 999, held_out=True)}
    held_out_schemas = {**held_out_real, **held_out_synth}
    held_out_names = list(held_out_schemas.keys())
    print(f"held-out targets: {len(held_out_real)} real ({sorted({n.split('_')[0] for n in held_out_real})}) "
          f"+ {len(held_out_synth)} synthetic ({sorted(held_out_synth)})", flush=True)

    all_visible_schemas = {**trainable_schemas, **held_out_schemas}

    import requests
    if args.api == "swarmcli":
        pass  # readiness already confirmed when the subprocess was opened above
    elif args.api == "native":
        r = requests.get(f"{args.native_host.rstrip('/')}/health", timeout=10)
        r.raise_for_status()
        print(f"Native runtime ready · model: {active_model}", flush=True)
    elif args.api == "claude":
        if not api_key:
            print("ERROR: ANTHROPIC_API_KEY not set.", flush=True)
            sys.exit(1)
        print(f"Claude API ready ({active_model})", flush=True)
    else:
        r = requests.get(f"{args.ollama_host.rstrip('/')}/api/tags", timeout=10)
        r.raise_for_status()
        print(f"Ollama ready at {args.ollama_host}", flush=True)

    rows: list[dict] = []
    generated = 0
    rejected = 0

    try:
        with swarmcli_backend_cm as swarmcli_backend:
            if args.api == "swarmcli":
                print(f"swarmcli --native-complete ready · model: {active_model}", flush=True)

            attempt = 0
            while generated < args.count and attempt < args.count * 4:
                attempt += 1
                target = rng.choice(held_out_names)
                available = sample_available_tools(rng, trainable_names, target_tool=None, min_size=4, max_size=10)
                # target must be visible; other held-out tools may also appear as realistic distractors
                other_held_out = [n for n in held_out_names if n != target]
                rng.shuffle(other_held_out)
                available = available + [target] + other_held_out[: rng.randint(0, 2)]
                rng.shuffle(available)

                # Reuse render_tools_block (CodeRabbit review, PR #99) instead of hand-formatting
                # descriptions/param names for the generator prompt -- the prior manual version
                # only showed param NAMES via .keys(), dropping type/required info that
                # render_tools_block (already used for the SYSTEM prompt below) includes, so the
                # generator model saw a weaker schema than the eval row it was building actually
                # carries.
                prompt = (
                    "Available tools:\n\n"
                    + render_tools_block(available, all_visible_schemas)
                    + f"\n\nTarget tool: {target} — {held_out_schemas[target]['description']}\n"
                    + "Generate the example."
                )

                try:
                    if args.api == "swarmcli":
                        raw_text = swarmcli_backend.generate(GENERATOR_SYSTEM, prompt)
                    elif args.api == "native":
                        raw_text = native_generate(args.native_host, active_model, GENERATOR_SYSTEM, prompt)
                    elif args.api == "claude":
                        raw_text = claude_generate(api_key, active_model, GENERATOR_SYSTEM, prompt)
                    else:
                        raw_text = ollama_generate(args.ollama_host, active_model, GENERATOR_SYSTEM, prompt)
                except Exception as e:
                    print(f"  [warn] backend error: {e}", flush=True)
                    rejected += 1
                    continue

                raw = extract_json(raw_text)
                if raw is None:
                    rejected += 1
                    continue
                request   = (raw.get("request") or "").strip()
                arguments = raw.get("expected_arguments")
                if len(request) < 10 or not isinstance(arguments, dict):
                    rejected += 1
                    continue

                schema_params   = set(held_out_schemas[target]["parameters"].keys())
                required_params = set(held_out_schemas[target]["required"])
                if not set(arguments.keys()).issubset(schema_params):
                    rejected += 1
                    continue
                if not required_params.issubset(set(k for k, v in arguments.items() if v)):
                    rejected += 1
                    continue

                system = SYSTEM_TEMPLATE.format(role="chat", tools_block=render_tools_block(available, all_visible_schemas))
                assistant = json.dumps({"decision": "call", "tool": target, "arguments": arguments}, ensure_ascii=False)
                rows.append({
                    "messages": [
                        {"role": "system",    "content": system},
                        {"role": "user",      "content": request},
                        {"role": "assistant", "content": assistant},
                    ],
                    "example_id": f"genarena_v2_{args.seed}_{generated:05d}",
                    "schema_version": "generalization-arena-v2",
                    "target_tool": target,
                    "target_is_synthetic": target in held_out_synth,
                })
                generated += 1
                if generated % 10 == 0:
                    print(f"  [{generated}/{args.count}] target={target}", flush=True)
    except (FileNotFoundError, RuntimeError, TimeoutError) as e:
        print(f"ERROR: swarmcli --native-complete failed to start: {e}", flush=True)
        sys.exit(1)

    print(flush=True)
    print(f"=== done: {generated} valid, {rejected} rejected ===", flush=True)
    if generated < args.count:
        print(f"WARNING: only {generated}/{args.count} reached -- NOT writing {args.out} "
              "(CodeRabbit review, PR #99: a sealed eval file must never be a silent partial "
              "artifact). Re-run once the shortfall is understood.", flush=True)
        sys.exit(1)

    # Atomic write (CodeRabbit review, PR #99): write to a temp file in the same directory, then
    # os.replace into the final sealed path -- a crash or Ctrl-C mid-write can never leave a
    # truncated/corrupt file at args.out, only at the temp path (which is never read by anything).
    args.out.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = args.out.with_suffix(args.out.suffix + ".tmp")
    with tmp_path.open("w", encoding="utf-8", newline="\n") as fh:
        for row in rows:
            fh.write(json.dumps(row, ensure_ascii=False) + "\n")
    os.replace(tmp_path, args.out)
    print(f"Wrote {generated} rows → {args.out}", flush=True)


if __name__ == "__main__":
    main()
