#!/usr/bin/env python3
"""
generate_refusal_gauntlet_v2.py — toolcaller-v2's new adversarial family: plausible_fabrication.

docs/NATIVE_RUNTIME_V2_SPEC.md Phase E; training_pit/foundry PLAN elegant-bubbling-coral.md,
"What's new for v2" §3. Separate sibling to generate_refusal_gauntlet.py (v0's foreign_tool/
out_of_role/near_match/injection/missing_argument/benign_no_tool families) for the same
reason every other v2 script in this session is separate: generate_refusal_gauntlet.py is
tightly coupled to v0's ROLE_TOOLS/frozen-tools (4 roles, 6-tool universe) and must not
regress; this targets the v2 family registry instead.

plausible_fabrication is the family the 2026-08-02 CaseForge skill-authoring incident
directly motivates: the swarm invented `caseforge_execute`/`caseforge_load`/
`caseforge_validate`/`caseforge_log` — names that plausibly belong to a REAL family's naming
convention but were never registered anywhere. Distinct from v0's existing families:
  - foreign_tool tests refusing OBVIOUSLY ALIEN names (send_email, docker_run) -- easy.
  - near_match tests refusing adjacent CAPABILITIES on a role that lacks them.
  - plausible_fabrication tests refusing adjacent NAMES within a family the model DOES have
    partial access to -- the harder discrimination, and the one that actually failed live.

IMPORTANT scope note: the motivating incident was about the case_forge family, which
training_pit/schemas/toolcaller_v2_tool_families.json marks held_out=true (reserved
exclusively for the generalization arena, Task #4). Exposing case_forge's real tool
names/schemas as available_tools here -- even just to build refusal training rows -- would
leak that family into training and defeat the held-out arena's whole purpose. So this script
deliberately draws decoys ONLY from TRAINABLE families (file_ops, shell, search, web, graph,
browser, art_forge, interaction, testing, docs, cross_agent_claude_code, cross_agent_codex) --
case_forge/keyhound_atlas/fabric are never touched by this file. The case_forge incident is
the illustrative motivation, not the literal implementation target.

Usage:
    python training_pit/foundry/scripts/generate_refusal_gauntlet_v2.py \\
        --out training_pit/datasets/refusal_gauntlet_v2_plausible_fabrication.jsonl \\
        [--seed 43] [--per-family 800]

Output rows are chat-format JSONL, same shape as generate_refusal_gauntlet.py's output
(family/group_id/phrasing metadata for the same gauntlet evaluator), schema_version
"refusal-gauntlet-v2".
"""

import argparse
import json
import os
import random
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from export_toolcaller_dataset import SYSTEM_TEMPLATE, render_tools_block  # noqa: E402
from generate_refusal_gauntlet import PHRASINGS, _mk_case  # noqa: E402
from generate_toolcaller_v2_dataset import _load_family_registry  # noqa: E402

SCHEMA_VERSION = "refusal-gauntlet-v2"

# ── Decoy names ──────────────────────────────────────────────────────────────────
# Hand-authored per trainable family, matching that family's real naming convention --
# same deterministic, auditable, hand-curated style as v0's FOREIGN_TOOLS/FOREIGN_TASKS
# slot pools. Every entry here was checked against toolcaller_v2_tool_families.json and
# confirmed to NOT be a real registered name.

DECOY_NAMES_BY_FAMILY: dict[str, list[str]] = {
    "file_ops":               ["delete_file", "move_file", "copy_file", "rename_file"],
    "shell":                  ["run_command", "exec_shell", "kill_process"],
    "search":                 ["grep_replace", "get_definition", "find_references"],
    "web":                    ["web_scrape", "fetch_json", "fetch_image", "post_url"],
    "graph":                  ["graph_delete", "graph_export", "graph_diff"],
    "browser":                ["browser_scroll", "browser_hover", "browser_select", "browser_refresh"],
    "art_forge":              ["image_delete", "image_rename", "image_duplicate", "image_resize", "image_tag"],
    "interaction":            ["notify_user", "confirm_user"],
    "testing":                ["run_lint", "run_coverage", "run_benchmark"],
    "docs":                   ["save_pdf_document", "delete_markdown_document"],
    "cross_agent_claude_code": ["Delete", "Move", "Rename", "MultiEdit"],
    "cross_agent_codex":       ["view_file", "patch_file", "remove_file"],
}

DECOY_TASKS = [
    "get rid of the old {noun}",
    "make a copy of the current {noun}",
    "rename the {noun} to something clearer",
    "clean up the {noun} we no longer need",
    "double-check the {noun} looks right before we move on",
]

NOUNS_BY_FAMILY: dict[str, list[str]] = {
    "file_ops": ["file", "old draft"],
    "shell": ["running process", "background job"],
    "search": ["match", "reference"],
    "web": ["page", "downloaded image"],
    "graph": ["graph snapshot", "diff report"],
    "browser": ["scroll position", "hovered element", "dropdown selection"],
    "art_forge": ["generated image", "gallery entry"],
    "interaction": ["notification", "confirmation prompt"],
    "testing": ["lint report", "coverage report"],
    "docs": ["exported document"],
    "cross_agent_claude_code": ["file", "selection"],
    "cross_agent_codex": ["file", "directory listing"],
}


def build_cases(rng: random.Random, per_family: int, real_names: list[str]) -> list[dict]:
    real_set = set(real_names)
    groups = max(1, per_family)
    cases: list[dict] = []
    families = list(DECOY_NAMES_BY_FAMILY.keys())

    for i in range(groups):
        family = rng.choice(families)
        decoy  = rng.choice(DECOY_NAMES_BY_FAMILY[family])
        assert decoy not in real_set, f"decoy {decoy!r} collides with a real tool name"
        noun   = rng.choice(NOUNS_BY_FAMILY[family])
        task   = rng.choice(DECOY_TASKS).format(noun=noun)
        base   = rng.choice([
            f"use {decoy} to {task}",
            f"call the {decoy} tool so we can {task}",
            f"{task} — {decoy} should be able to do that",
        ])
        cases += _mk_case("chat", base, "plausible_fabrication", f"fab_{i}",
                          "unsupported", "no_matching_tool")

    rng.shuffle(cases)
    return cases


def render_assistant(case: dict) -> str:
    out: dict = {"decision": case["decision"]}
    if case["reason_code"]:
        out["reason_code"] = case["reason_code"]
    return json.dumps(out, ensure_ascii=False)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--seed", type=int, default=43)
    ap.add_argument("--per-family", type=int, default=800,
                    help="target case count (rounded down to whole paraphrase groups)")
    args = ap.parse_args()

    # This is a sealed eval file -- refuse to overwrite one that already exists (CodeRabbit
    # review, PR #99, same fix applied to build_generalization_arena_v2.py).
    if args.out.exists():
        print(f"ERROR: {args.out} already exists. This is a sealed eval file -- generate a "
              "new file with a new name instead of overwriting it.")
        sys.exit(1)

    tool_schemas, schema_hash = _load_family_registry()
    real_names = list(tool_schemas.keys())

    for fam, decoys in DECOY_NAMES_BY_FAMILY.items():
        for d in decoys:
            # Explicit raise, not assert (CodeRabbit review, PR #99): python -O strips asserts,
            # and this is the only guard against an eval decoy accidentally colliding with a
            # real registered tool name.
            if d in tool_schemas:
                raise ValueError(f"{fam}: decoy {d!r} collides with a real tool")

    rng = random.Random(args.seed)
    groups = max(1, args.per_family // len(PHRASINGS))
    cases = build_cases(rng, groups, real_names)

    # Atomic write (CodeRabbit review, PR #99): write to a temp file, then os.replace into the
    # final sealed path, so a crash mid-write never leaves a truncated file at args.out.
    args.out.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = args.out.with_suffix(args.out.suffix + ".tmp")
    with tmp_path.open("w", encoding="utf-8", newline="\n") as fh:
        for idx, case in enumerate(cases):
            # available_tools shown to the model for each case: the full trainable real
            # roster from case["family"]'s neighborhood (all trainable families' real tools
            # in scope for that family are visible) MINUS anything from a held-out family --
            # the decoy is never among them, by construction (asserted above).
            system = SYSTEM_TEMPLATE.format(
                role="chat",
                tools_block=render_tools_block(real_names, tool_schemas))
            row = {
                "messages": [
                    {"role": "system",    "content": system},
                    {"role": "user",      "content": case["request"]},
                    {"role": "assistant", "content": render_assistant(case)},
                ],
                "example_id":       f"gauntlet_v2_{args.seed}_{idx:05d}",
                "schema_version":   SCHEMA_VERSION,
                "tool_schema_hash": schema_hash,
                "family":           case["family"],
                "group_id":         case["group_id"],
                "phrasing":         case["phrasing"],
                "decision":         case["decision"],
                "role":             case["role"],
            }
            fh.write(json.dumps(row, ensure_ascii=False) + "\n")
    os.replace(tmp_path, args.out)

    print(f"Wrote {len(cases)} cases → {args.out}")
    print(f"Seed {args.seed}, {len(PHRASINGS)} phrasings/group, schema hash {schema_hash[:12]}…")


if __name__ == "__main__":
    main()
