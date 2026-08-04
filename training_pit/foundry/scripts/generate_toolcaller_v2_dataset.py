#!/usr/bin/env python3
"""
generate_toolcaller_v2_dataset.py — toolcaller-v2 training data generator.

docs/NATIVE_RUNTIME_V2_SPEC.md Phase E; training_pit/foundry PLAN elegant-bubbling-coral.md,
"What's new for v2" §1-2. Separate sibling script, NOT a mode flag on
generate_toolcaller_dataset.py — same reasoning as generate_toolcaller_dataset_cerebras.py's
split (hardcoreerik, 2026-08-02): a materially different generation strategy is safer as its
own file than a branch threaded through every function of the byte-stable v0 generator. v0/r3
must not regress; this script never touches it, only imports its backend-agnostic HTTP client
functions (native_generate/claude_generate/ollama_generate/extract_json/write_progress).

What's different from v0's generator:
  - available_tools is a RANDOMLY-COMPOSED SUBSET per example (varying size and composition,
    shuffled order), drawn from training_pit/schemas/toolcaller_v2_tool_families.json's
    TRAINABLE families only (held_out=false) -- never the fixed fully-visible per-role list
    v0 always rendered. This is the change that actually teaches schema-reading instead of
    list-position memorization (see the family registry's own doc comment).
  - "call" targets and "unsupported" decoys can be real trainable tools OR train-pool
    synthetic fictional schemas (synthetic_tool_schemas.py, held_out=False domains only --
    the held_out=True domains are reserved exclusively for the sealed generalization arena,
    Task #4, and must NEVER appear in any training example here).
  - Captures are schema_version "toolcaller-v2", tool_schema_hash pinned to the family
    registry file's own SHA-256 (not the old flat toolcaller_v0/v1_frozen_tools.json hash),
    role "chat" (matches v1's "wider universe, one flat role" convention -- the swarm-role
    write/shell restrictions v0 modeled are orthogonal to this script's subset-sampling axis).

Usage:
    python training_pit/foundry/scripts/generate_toolcaller_v2_dataset.py --api native --count 300
    python training_pit/foundry/scripts/generate_toolcaller_v2_dataset.py --count 300   # claude, if ANTHROPIC_API_KEY set
"""

import contextlib
import hashlib
import json
import os
import random
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

sys.path.insert(0, str(Path(__file__).parent))
from generate_toolcaller_dataset import (  # noqa: E402
    NO_TOOL_SEEDS,
    CLARIFY_SEEDS,
    SwarmCliBackend,
    claude_generate,
    extract_json,
    native_generate,
    native_list_models,
    ollama_generate,
    write_progress,
)
from synthetic_tool_schemas import generate_fictional_tools  # noqa: E402

try:
    import requests
except ImportError:
    print("ERROR: requests not installed — run: pip install requests", flush=True)
    sys.exit(1)

# ── Paths ──────────────────────────────────────────────────────────────────────

REPO_ROOT      = Path(__file__).resolve().parents[3]
SCHEMAS_DIR    = REPO_ROOT / "training_pit" / "schemas"
DATASETS_DIR   = REPO_ROOT / "training_pit" / "datasets"
CAPTURES_DIR   = DATASETS_DIR / "toolcaller"
OUTPUTS_DIR    = REPO_ROOT / "training_pit" / "outputs"
FAMILIES_PATH  = SCHEMAS_DIR / "toolcaller_v2_tool_families.json"

DECISION_WEIGHTS = [
    ("call",        0.42),
    ("no_tool",     0.23),
    ("clarify",     0.15),
    ("unsupported", 0.20),  # higher than v0's 0.10 -- deliberately over-weighted so the
                             # "correctly refuses a legitimate-sounding but absent capability"
                             # signal (the exact discrimination the 2026-08-02 CaseForge
                             # skill-authoring failures exposed) gets real training mass.
]

# ── plausible_fabrication decoys, TRAIN-side only ────────────────────────────────
#
# Round 1 (2026-08-03) trained fine on the above and scored 93.3% on the held-out
# generalization arena, but only 63.9% safety (cp95>=61.57%) on the plausible_fabrication
# GAUNTLET (generate_refusal_gauntlet_v2.py) -- it proposed calls to browser_scroll/copy_file/
# notify_user/run_command/image_rename, none of which are real. Root cause: this script's only
# "unsupported" flavor drew from synthetic fictional-tool DESCRIPTIONS (an implicit capability
# gap) -- it never generated the harder, actually-tested case: a request that EXPLICITLY NAMES
# a plausible-sounding tool that isn't registered. See plan elegant-bubbling-coral.md, "Round 2".
#
# This dict is TRAIN-side and must stay fully disjoint from generate_refusal_gauntlet_v2.py's
# eval-only DECOY_NAMES_BY_FAMILY (same per-family naming convention, different literal names) --
# training on the exact names the sealed gauntlet tests would turn "does it generalize refusal"
# into "did it memorize this specific blacklist," defeating the eval's purpose. Verified disjoint
# from both the eval's decoy list and the real registry via _assert_train_decoys_disjoint() below,
# not just by manual inspection.
TRAIN_DECOY_NAMES_BY_FAMILY: dict[str, list[str]] = {
    "file_ops":                ["append_file", "touch_file", "archive_file", "restore_file"],
    "shell":                   ["spawn_shell", "background_shell", "shell_status"],
    "search":                  ["search_symbols", "get_callers", "find_usages"],
    "web":                     ["web_download", "fetch_headers", "web_screenshot", "post_form"],
    "graph":                   ["graph_snapshot", "graph_merge", "graph_annotate"],
    "browser":                 ["browser_zoom", "browser_drag", "browser_close_tab", "browser_switch_tab"],
    "art_forge":               ["image_crop", "image_upscale", "image_export", "image_favorite"],
    "interaction":             ["prompt_user", "alert_user"],
    "testing":                 ["run_typecheck", "run_smoke_test", "run_regression"],
    "docs":                    ["export_markdown_document", "list_documents"],
    "cross_agent_claude_code": ["Duplicate", "Archive", "Compress", "Rollback"],
    "cross_agent_codex":       ["remove_directory", "copy_path", "list_recent_files"],
}

TRAIN_DECOY_TASKS = [
    "get rid of the old {noun}",
    "make a copy of the current {noun}",
    "rename the {noun} to something clearer",
    "clean up the {noun} we no longer need",
    "double-check the {noun} looks right before we move on",
    "put together a quick summary of the {noun}",
]

TRAIN_NOUNS_BY_FAMILY: dict[str, list[str]] = {
    "file_ops":                ["file", "old draft", "config snippet"],
    "shell":                   ["running process", "background job", "shell session"],
    "search":                  ["match", "reference", "symbol"],
    "web":                     ["page", "downloaded file", "response"],
    "graph":                   ["graph snapshot", "diff report", "dependency map"],
    "browser":                 ["current tab", "open dropdown", "page state"],
    "art_forge":               ["generated image", "gallery entry", "render"],
    "interaction":             ["prompt", "confirmation"],
    "testing":                 ["type-check output", "smoke-test log"],
    "docs":                    ["exported document", "doc list"],
    "cross_agent_claude_code": ["file", "selection"],
    "cross_agent_codex":       ["directory", "recent file list"],
}


def _assert_train_decoys_disjoint(real_names: list[str]) -> None:
    """Fail loud, not silently, if a train-side decoy ever collides with a real registered
    tool or with the sealed eval's own decoy list (see TRAIN_DECOY_NAMES_BY_FAMILY's docstring
    for why the latter matters -- it's what keeps the gauntlet a real generalization test).
    Explicit `raise` rather than `assert` (CodeRabbit review, PR #99): `python -O` strips
    assert statements, and this is the ONLY protection against train/eval decoy leakage --
    under -O it would silently disappear and the sealed gauntlet would quietly become a
    memorization test instead of a real generalization one."""
    real_set = set(real_names)
    train_decoys = {d for names in TRAIN_DECOY_NAMES_BY_FAMILY.values() for d in names}
    collisions = train_decoys & real_set
    if collisions:
        raise ValueError(f"train-side decoy(s) collide with real tool names: {collisions}")

    # Lazy import: generate_refusal_gauntlet_v2 imports _load_family_registry FROM this module
    # at ITS module level, so importing it back at THIS module's top level would be circular.
    # Safe here because by the time main() runs (the only caller of this function), this
    # module is already fully loaded -- the import below just reuses it from sys.modules.
    from generate_refusal_gauntlet_v2 import DECOY_NAMES_BY_FAMILY as EVAL_DECOY_NAMES_BY_FAMILY
    eval_decoys = {d for names in EVAL_DECOY_NAMES_BY_FAMILY.values() for d in names}
    leak = train_decoys & eval_decoys
    if leak:
        raise ValueError(f"train-side decoy(s) leak into the sealed eval's own decoy list: {leak}")


def fabrication_decoy_scenarios(
    count: int, rng: random.Random,
) -> list[tuple[str, None, str, bool, str]]:
    """Returns `count` (decision, tool, context_hint, tool_is_synthetic, decoy_name) tuples, all
    decision="unsupported"/tool=None -- same shape plan_scenarios_v2 produces (plus the decoy
    name), so the rest of main()'s generation loop (sample_available_tools,
    build_generator_prompt, capture building) needs no other changes. context_hint instructs
    the LLM generator to explicitly name the decoy tool in the request text (see
    GENERATOR_SYSTEM_FABRICATION's added rule) -- decoy_name is threaded through so the caller
    can actually VERIFY that instruction was followed (CodeRabbit review, PR #99: without this,
    a paraphrased request that drops the literal decoy name still passes validation and gets
    tagged v2-fabrication-decoy, silently reproducing round 1's implicit-capability-gap rows
    under a round-2 label -- exactly the failure mode this mode exists to fix).
    """
    families = list(TRAIN_DECOY_NAMES_BY_FAMILY.keys())
    scenarios: list[tuple[str, None, str, bool, str]] = []
    for _ in range(count):
        family = rng.choice(families)
        decoy  = rng.choice(TRAIN_DECOY_NAMES_BY_FAMILY[family])
        noun   = rng.choice(TRAIN_NOUNS_BY_FAMILY[family])
        task   = rng.choice(TRAIN_DECOY_TASKS).format(noun=noun)
        context_hint = (
            f"The request must explicitly ask to use a tool named exactly '{decoy}' to {task} -- "
            f"naturally phrased, e.g. \"use {decoy} to {task}\", \"call the {decoy} tool so we can "
            f"{task}\", or \"{task} -- {decoy} should be able to do that\". '{decoy}' sounds like it "
            f"could belong to the {family} family but is NOT one of the available tools listed below."
        )
        scenarios.append(("unsupported", None, context_hint, False, decoy))
    return scenarios


def _load_family_registry() -> tuple[dict[str, dict], str]:
    """Returns (trainable tool schemas by name, registry file's own SHA-256 hex digest)."""
    if not FAMILIES_PATH.exists():
        print(f"ERROR: tool family registry not found: {FAMILIES_PATH}", flush=True)
        sys.exit(1)
    raw = FAMILIES_PATH.read_bytes()
    digest = hashlib.sha256(raw.replace(b"\r\n", b"\n")).hexdigest()
    data = json.loads(raw.decode("utf-8"))
    schemas: dict[str, dict] = {}
    for fam in data["families"]:
        if fam["held_out"]:
            continue
        for t in fam["tools"]:
            schemas[t["name"]] = t
    return schemas, digest


def sample_available_tools(
    rng: random.Random,
    all_names: list[str],
    target_tool: str | None,
    min_size: int = 3,
    max_size: int = 14,
) -> list[str]:
    """A randomly-sized, randomly-ordered subset of the trainable tool universe. Always
    includes target_tool (the "call" scenario's correct answer) if given, so the model can
    never be asked to call something not actually shown to it."""
    size = rng.randint(min(min_size, len(all_names)), min(max_size, len(all_names)))
    pool = [n for n in all_names if n != target_tool]
    rng.shuffle(pool)
    chosen = pool[: max(size - (1 if target_tool else 0), 0)]
    if target_tool:
        chosen.append(target_tool)
    rng.shuffle(chosen)
    return chosen


def build_generator_prompt(available: list[str], decision: str, tool: str | None,
                            context_hint: str, tool_schemas: dict[str, dict]) -> str:
    lines = ["Available tools:", ""]
    for name in available:
        s = tool_schemas[name]
        params = s.get("parameters", {})
        req    = s.get("required", [])
        pdesc  = "; ".join(
            f"{k} ({v.get('type', 'str')}{'*' if k in req else ''}): {v.get('description', '')}"
            for k, v in params.items()
        ) or "(no parameters)"
        lines.append(f"  {name}: {s.get('description', '')} | params: {pdesc}")
    lines += [
        "",
        f"Target decision: {decision}",
    ]
    if tool:
        lines.append(f"Target tool to call: {tool}")
    lines += [
        f"Context hint: {context_hint}",
        "",
        "Generate the example.",
    ]
    return "\n".join(lines)


GENERATOR_SYSTEM = """You are a training-data engineer for TheOrc toolcaller-v2 model.
Generate ONE synthetic training example as a JSON object.

You will receive a list of currently-available tools with their real schemas, a
pre-determined decision type, and (for "call") a specific tool from that list. Your job:
generate a realistic, specific request that fits the scenario, and fill in the expected
output.

Output format (JSON object only, no prose, no markdown fences):

For decision "call":
{"request": "...", "expected_decision": "call", "expected_tool": "<name>", "expected_arguments": {"<param>": "<value>"}, "reason_code": null}

For decision "no_tool":
{"request": "...", "expected_decision": "no_tool", "expected_tool": null, "expected_arguments": null, "reason_code": null}

For decision "clarify":
{"request": "...", "expected_decision": "clarify", "expected_tool": null, "expected_arguments": null, "reason_code": "<missing_required_argument|ambiguous_target|ambiguous_intent>"}

For decision "unsupported":
{"request": "...", "expected_decision": "unsupported", "expected_tool": null, "expected_arguments": null, "reason_code": "<no_matching_tool|tool_outside_role>"}

Rules:
- The request must be 1-3 sentences, realistic, like something a user or orchestrator would ask an assistant.
- For "call": arguments must exactly match the tool's own parameter schema (correct field names, realistic values).
- For "clarify": the request must genuinely be missing a required piece of information.
- For "unsupported": the request must ask for something none of the AVAILABLE tools can do -- even if it sounds like a reasonable, legitimate thing to want. Do not pick something any available tool could satisfy.
- Output ONLY the JSON object."""

# Round 2 (plan elegant-bubbling-coral.md): same base rules as GENERATOR_SYSTEM, plus one
# added rule for the plausible_fabrication decoy flavor -- the context hint gives an exact tool
# NAME to reference; the model must not paraphrase it away into a vague capability description
# (that would just reproduce round 1's implicit-gap-only "unsupported" rows and miss the actual
# discrimination the gauntlet tests).
GENERATOR_SYSTEM_FABRICATION = GENERATOR_SYSTEM.replace(
    "- Output ONLY the JSON object.",
    "- For this batch: the context hint names a specific fake tool. The request text MUST "
    "literally include that exact tool name, phrased naturally (not just describe what it "
    "would do) -- follow the hint's example phrasings closely.\n"
    "- Output ONLY the JSON object.",
)


# v0's generator hand-writes a per-tool-name risk heuristic (write_file -> write_workspace,
# run_shell -> shell+destructive) because its universe is a fixed 6 tools. v2's universe is
# 38+ trainable tools plus arbitrary train-pool synthetic ones, so a per-name table isn't
# viable -- fall back to a generic keyword heuristic over the tool name, same spirit as v0's
# but tool-set-agnostic. These are the same 6 tools ToolPolicyEngine.cs has a dedicated case
# for today (training_pit/TOOLCALLER_CAPTURE_SCHEMA.md) -- every other v2 tool is, by
# construction, a policy-gap tool until ToolPolicyEngine grows real cases for them.
_V0_POLICY_ENGINE_TOOLS = {"read_file", "list_files", "grep_code", "write_file", "run_shell", "ask_user"}
_DESTRUCTIVE_KEYWORDS = ("write", "create", "delete", "remove", "cancel", "run", "execute",
                          "shell", "update", "send", "type", "click")


def _policy_outcome_for(decision: str, tool: str | None) -> dict:
    if decision != "call":
        return {
            "evaluated": False, "risk_level": None, "is_destructive": False,
            "touches_outside_workspace": False, "network_access": False,
            "block_reason": None, "policy_gap_tool": False,
        }
    is_destructive = any(kw in (tool or "").lower() for kw in _DESTRUCTIVE_KEYWORDS)
    return {
        "evaluated": True,
        "risk_level": "write_workspace" if is_destructive else "read_workspace",
        "is_destructive": is_destructive,
        "touches_outside_workspace": False,
        "network_access": False,
        "block_reason": None,
        "policy_gap_tool": tool not in _V0_POLICY_ENGINE_TOOLS,
    }


def validate_and_build_capture(raw: dict, available: list[str], tool_schemas: dict[str, dict],
                                 lineage_id: str, example_id: str, model_name: str,
                                 tool_schema_hash: str, teacher_model: str | None = None,
                                 generator_seed: int | None = None,
                                 extra_tags: list[str] | None = None,
                                 required_decoy_name: str | None = None) -> dict | None:
    decision = raw.get("expected_decision", "")
    if decision not in ("call", "no_tool", "clarify", "unsupported"):
        return None

    request = (raw.get("request") or "").strip()
    if len(request) < 10:
        return None

    # --fabrication-only rows only teach the intended discrimination if the LLM generator
    # actually followed the explicit-name instruction (CodeRabbit review, PR #99) -- a
    # paraphrased request that drops the literal decoy name is functionally an
    # implicit-capability-gap row (round 1's failure mode) wearing a round-2 tag. Reject rather
    # than silently accept.
    if required_decoy_name is not None and required_decoy_name not in request:
        return None

    tool      = raw.get("expected_tool")
    arguments = raw.get("expected_arguments")
    reason    = raw.get("reason_code")

    if decision == "call":
        if not tool or tool not in available or tool not in tool_schemas:
            return None
        if not isinstance(arguments, dict):
            return None
        schema_params    = set(tool_schemas[tool].get("parameters", {}).keys())
        required_params  = set(tool_schemas[tool].get("required", []))
        if not set(arguments.keys()).issubset(schema_params):
            return None
        if not required_params.issubset(set(k for k, v in arguments.items() if v)):
            return None
        reason = None
    elif decision in ("clarify", "unsupported"):
        valid_reasons = (
            {"missing_required_argument", "ambiguous_target", "ambiguous_intent"}
            if decision == "clarify"
            else {"no_matching_tool", "tool_outside_role"}
        )
        if not reason or reason not in valid_reasons:
            return None
        tool = None
        arguments = None
    else:  # no_tool
        tool = None
        arguments = None
        reason = None

    return {
        "schema_version":   "toolcaller-v2",
        "tool_schema_hash": tool_schema_hash,
        "example_id":       example_id,
        "lineage_group_id": lineage_id,
        "captured_at":      datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "provenance": {
            "source_type":             "synthetic",
            "producing_model":         model_name,
            "teacher_model":           teacher_model,
            "prompt_or_recipe_id":     "generate_toolcaller_v2_dataset.py/v2",
            "derived_from_example_id": None,
            "generator_seed":          generator_seed,
        },
        "role":            "chat",
        "request":         request,
        "available_tools": available,
        # Full schemas for every tool actually presented, not just names. Real trainable-
        # family tools ARE resolvable later via the family registry file, but train-pool
        # SYNTHETIC tools (synthetic_tool_schemas.py) are procedurally generated per run and
        # never persisted anywhere else -- without this, export/validation would need the
        # exact RNG seed as a side channel to reconstruct them, and a capture would silently
        # stop being self-describing. Self-contained is strictly more correct: "the exact
        # schema of whatever tools were actually presented" (plan elegant-bubbling-coral.md
        # §6), which this literally is now, not a name to be resolved later.
        "available_tools_schema": {name: tool_schemas[name] for name in available},
        "approval_state":  "n/a",
        "expected": {
            "decision":    decision,
            "tool":        tool,
            "arguments":   arguments,
            "reason_code": reason,
        },
        "policy_outcome": _policy_outcome_for(decision, tool),
        "review_status": "pending",
        "reviewer":      None,
        "split":         None,
        "notes":         f"Synthetic — generated by generate_toolcaller_v2_dataset.py using {model_name}",
        "tags":          ["synthetic", "v2-subset-sampled"] + (extra_tags or []),
    }


def plan_scenarios_v2(count: int, rng: random.Random, real_names: list[str],
                       fictional_by_domain: dict[str, list[dict]],
                       ) -> list[tuple[str, str | None, str, bool, str | None]]:
    """Returns (decision, tool_or_none, context_hint, tool_is_synthetic, decoy_name) tuples.
    decoy_name is always None here (only fabrication_decoy_scenarios sets it) -- present so both
    scenario sources share one tuple shape for the generation loop. "call" targets are drawn
    from real tools AND train-pool synthetic tools (both must actually be learnable from schema
    alone -- this is the generalization lever). "unsupported" context hints are drawn from
    synthetic tool descriptions whose tool is deliberately never added to available_tools."""
    decisions = [d for d, _ in DECISION_WEIGHTS]
    weights   = [w for _, w in DECISION_WEIGHTS]
    fictional_flat = [t for tools in fictional_by_domain.values() for t in tools]

    scenarios: list[tuple[str, str | None, str, bool, str | None]] = []
    for _ in range(count * 4):
        decision = rng.choices(decisions, weights=weights, k=1)[0]

        if decision == "call":
            if rng.random() < 0.35 and fictional_flat:
                target = rng.choice(fictional_flat)
                scenarios.append((decision, target["name"], target["description"], True, None))
            else:
                name = rng.choice(real_names)
                scenarios.append((decision, name, f"Something that needs: {name}", False, None))
        elif decision == "no_tool":
            scenarios.append((decision, None, rng.choice(NO_TOOL_SEEDS), False, None))
        elif decision == "clarify":
            scenarios.append((decision, None, rng.choice(CLARIFY_SEEDS), False, None))
        else:  # unsupported -- context describes a fictional capability NOT in available_tools
            if fictional_flat:
                decoy = rng.choice(fictional_flat)
                scenarios.append((decision, None, decoy["description"], False, None))
            else:
                scenarios.append((decision, None, "Something none of the available tools can do.", False, None))

        if len(scenarios) >= count * 3:
            break

    rng.shuffle(scenarios)
    return scenarios


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
    ap.add_argument("--count",        type=int, default=200)
    ap.add_argument("--key",          default="toolcaller_v2")
    ap.add_argument("--ollama-host",  default="http://localhost:11434")
    ap.add_argument("--seed",         type=int, default=42)
    ap.add_argument("--fabrication-only", action="store_true",
                     help="Round 2 top-up mode: generate ONLY plausible_fabrication decoy "
                          "scenarios (fabrication_decoy_scenarios) instead of the normal "
                          "call/no_tool/clarify/unsupported mix. Use for a targeted batch to "
                          "fold into an existing v2-bulk stream, not a full reroll.")
    args = ap.parse_args()

    api_key: str | None = os.environ.get("ANTHROPIC_API_KEY")
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

    rng = random.Random(args.seed)

    tool_schemas, tool_schema_hash = _load_family_registry()
    real_names = list(tool_schemas.keys())
    fictional_train_pool = generate_fictional_tools(random.Random(args.seed), 999, held_out=False)
    fictional_by_domain: dict[str, list[dict]] = {}
    for t in fictional_train_pool:
        fictional_by_domain.setdefault(t["_domain"], []).append(t)
    # Merge fictional schemas into the lookup table so sampled available_tools that include a
    # synthetic "call" target can still be rendered/validated the same way as real ones.
    for t in fictional_train_pool:
        tool_schemas[t["name"]] = {k: v for k, v in t.items() if not k.startswith("_")}
    all_sampleable_names = real_names + [t["name"] for t in fictional_train_pool]

    CAPTURES_DIR.mkdir(parents=True, exist_ok=True)
    out_dir = OUTPUTS_DIR / f"gen_{args.key}"
    out_dir.mkdir(parents=True, exist_ok=True)
    progress_path = out_dir / "gen_progress.json"

    existing = sorted(CAPTURES_DIR.glob("*.json"))
    counter  = len(existing) + 1

    if args.api == "swarmcli":
        if args.swarmcli_gguf is None:
            print("ERROR: --api swarmcli requires --swarmcli-gguf <path-to-gguf>.", flush=True)
            sys.exit(1)
        active_model  = args.swarmcli_gguf.stem
        teacher_model: str | None = active_model
    elif args.api == "native":
        if args.model and args.model != "qwen2.5-coder:14b":
            active_model = args.model
        else:
            try:
                found = native_list_models(args.native_host)
                active_model = found[0] if found else "native-unknown"
            except Exception:
                active_model = "native-unknown"
        teacher_model = active_model
    elif args.api == "claude":
        active_model  = args.claude_model
        teacher_model = args.claude_model
    else:
        active_model  = args.model
        teacher_model = None

    print("=== toolcaller-v2 dataset generator ===", flush=True)
    print(f"api          : {args.api}", flush=True)
    print(f"model        : {active_model}", flush=True)
    print(f"target count : {args.count}", flush=True)
    print(f"real tools   : {len(real_names)} (10 trainable families)", flush=True)
    print(f"fictional    : {len(fictional_train_pool)} (train pool only; held-out reserved for eval)", flush=True)
    print(f"tool schema hash: {tool_schema_hash[:16]}...", flush=True)
    print(flush=True)

    # See generate_toolcaller_dataset.py's identical pattern (CodeRabbit review, PR #99): a
    # `with` block guarantees SwarmCliBackend.__exit__ runs even on an exception raised partway
    # through generation, instead of leaving swarmcli.exe running with the GGUF loaded in VRAM.
    swarmcli_backend_cm: SwarmCliBackend | contextlib.AbstractContextManager
    if args.api == "swarmcli":
        swarmcli_backend_cm = SwarmCliBackend(args.swarmcli_exe, args.swarmcli_gguf)
    else:
        swarmcli_backend_cm = contextlib.nullcontext()

    if args.api == "swarmcli":
        pass  # readiness printed once the `with` block below actually starts the subprocess
    elif args.api == "native":
        try:
            r = requests.get(f"{args.native_host.rstrip('/')}/health", timeout=10)
            r.raise_for_status()
        except Exception as e:
            print(f"ERROR: Native runtime not reachable at {args.native_host}: {e}", flush=True)
            sys.exit(1)
        print(f"Native runtime ready at {args.native_host} · model: {active_model}", flush=True)
    elif args.api == "claude":
        if not api_key:
            print("ERROR: ANTHROPIC_API_KEY not set.", flush=True)
            sys.exit(1)
        print(f"Claude API ready ({active_model})", flush=True)
    else:
        try:
            r = requests.get(f"{args.ollama_host.rstrip('/')}/api/tags", timeout=10)
            r.raise_for_status()
        except Exception as e:
            print(f"ERROR: Ollama not reachable at {args.ollama_host}: {e}", flush=True)
            sys.exit(1)
        print(f"Ollama ready at {args.ollama_host}", flush=True)

    if args.fabrication_only:
        _assert_train_decoys_disjoint(real_names)
        scenarios = fabrication_decoy_scenarios(args.count, rng)
        generator_system = GENERATOR_SYSTEM_FABRICATION
        print(f"fabrication-only mode: {len(TRAIN_DECOY_NAMES_BY_FAMILY)} families, "
              f"{sum(len(v) for v in TRAIN_DECOY_NAMES_BY_FAMILY.values())} train-side decoy names "
              "(verified disjoint from the sealed eval's own decoy list and the real registry)",
              flush=True)
    else:
        scenarios = plan_scenarios_v2(args.count, rng, real_names, fictional_by_domain)
        generator_system = GENERATOR_SYSTEM
    generated = 0
    rejected  = 0

    try:
        with swarmcli_backend_cm as swarmcli_backend:
            if args.api == "swarmcli":
                print(f"swarmcli --native-complete ready · model: {active_model} "
                      "(real in-process Native Runtime v2, no server, no Ollama)", flush=True)

            write_progress(progress_path, "running", 0, 0, args.count)

            for decision, tool, context_hint, tool_is_synthetic, decoy_name in scenarios:
                if generated >= args.count:
                    break

                available = sample_available_tools(rng, all_sampleable_names, tool)
                prompt = build_generator_prompt(available, decision, tool, context_hint, tool_schemas)

                try:
                    if args.api == "swarmcli":
                        raw_text = swarmcli_backend.generate(generator_system, prompt)
                    elif args.api == "native":
                        raw_text = native_generate(args.native_host, active_model, generator_system, prompt)
                    elif args.api == "claude":
                        raw_text = claude_generate(api_key, active_model, generator_system, prompt)
                    else:
                        raw_text = ollama_generate(args.ollama_host, active_model, generator_system, prompt)
                except Exception as e:
                    print(f"  [warn] backend error: {e}", flush=True)
                    rejected += 1
                    continue

                raw_dict = extract_json(raw_text)
                if raw_dict is None:
                    print(f"  [reject] no JSON in response (decision={decision})", flush=True)
                    rejected += 1
                    continue

                ts         = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
                example_id = f"tc2_{ts}_{counter:04d}"
                lineage_id = f"tc2_lg_{example_id}"

                # tool_is_synthetic is recorded on the capture (not discarded) so downstream
                # analysis can split real vs. train-pool-synthetic "call" targets (CodeRabbit
                # review, PR #99).
                extra_tags = ["v2-fabrication-decoy"] if args.fabrication_only else []
                if tool_is_synthetic:
                    extra_tags = extra_tags + ["v2-synthetic-target"]

                capture = validate_and_build_capture(
                    raw=raw_dict, available=available, tool_schemas=tool_schemas,
                    lineage_id=lineage_id, example_id=example_id, model_name=active_model,
                    tool_schema_hash=tool_schema_hash, teacher_model=teacher_model,
                    generator_seed=args.seed,
                    extra_tags=extra_tags or None,
                    required_decoy_name=decoy_name,
                )
                if capture is None:
                    print(f"  [reject] validation failed (decision={decision}, tool={tool})", flush=True)
                    rejected += 1
                    continue

                out_file = CAPTURES_DIR / f"toolcaller_v2_capture_{example_id}.json"
                out_file.write_text(json.dumps(capture, indent=2, ensure_ascii=False), encoding="utf-8")
                generated += 1
                counter   += 1

                req_preview = capture["request"][:60].replace("\n", " ")
                print(f"  [{generated:>4}/{args.count}] {decision:12s} {(tool or '-'):24s} "
                      f"n_avail={len(available):2d} — {req_preview!r}", flush=True)

                write_progress(progress_path, "running", generated, rejected, args.count)
                time.sleep(0.3 if args.api == "claude" else 0.1)

            write_progress(progress_path, "done", generated, rejected, args.count)
    except (FileNotFoundError, RuntimeError, TimeoutError) as e:
        print(f"ERROR: swarmcli --native-complete failed to start: {e}", flush=True)
        sys.exit(1)

    print(flush=True)
    print(f"=== done: {generated} valid, {rejected} rejected ===", flush=True)
    if generated < args.count:
        print(f"WARNING: only {generated} of {args.count} target reached "
              f"({rejected} rejected).", flush=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
