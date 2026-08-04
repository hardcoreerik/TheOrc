#!/usr/bin/env python3
"""
synthetic_tool_schemas.py — Procedural fictional-tool schema generator for toolcaller-v2.

docs/NATIVE_RUNTIME_V2_SPEC.md Phase E; training_pit/foundry PLAN elegant-bubbling-coral.md,
"What's new for v2" §1. This is the single most direct lever for "generalizes to genuinely
new tools": produces plausible, structurally realistic {name, description, parameters,
required} schemas — same flat-properties shape OllamaClient.ToOllamaSchema() emits — for
tools that were NEVER registered anywhere in TheOrc. Training examples where the correct
call target is one of these forces the model to condition on the in-context schema instead
of a memorized name, which is exactly the discrimination the closed-set v0/r3 pipeline never
exercised (verified: every real family in
training_pit/schemas/toolcaller_v2_tool_families.json is either always fully present or
absent per example — nothing in the pre-v2 pipeline varies which tools appear).

Deliberately in a DIFFERENT problem space from TheOrc's real tool families (file/shell/
search/web/graph/browser/art_forge/case_forge/keyhound_atlas/fabric/interaction/testing/
docs) — everyday utility domains (calendar, notes, spreadsheet, translation, weather,
contacts, timer, unit conversion) that a coding-agent tool roster would never plausibly
contain. This is intentional: these are "obviously legitimate, obviously new" tools the
model MUST learn to call correctly from schema alone, the opposite training signal from
generate_refusal_gauntlet.py's plausible_fabrication family (Task #3), which instead tests
REFUSING names that sound like they belong to a REAL family but don't.

Deterministic template-combinatorial generation, same philosophy as
generate_refusal_gauntlet.py: no teacher model, fully auditable, same seed -> same set.
Every produced name is checked against the real tool-family registry so a synthetic name can
never collide with (or be confused for) an actual TheOrc tool.
"""

import json
import random
import sys
from pathlib import Path

REPO_ROOT   = Path(__file__).resolve().parents[3]
SCHEMAS_DIR = REPO_ROOT / "training_pit" / "schemas"
FAMILIES_PATH = SCHEMAS_DIR / "toolcaller_v2_tool_families.json"

# ── Domain templates ────────────────────────────────────────────────────────────
# Each domain: a noun (the resource the tool acts on), a pool of verbs that make sense
# for that noun, and a parameter pool verbs can draw required/optional params from.
# Tool names are synthesized as f"{verb}_{noun}" or f"{verb}_{noun}s" (list-shaped verbs).

# Each verb tuple: (verb, description, is_mutating, required_param_names). Required params
# are explicit per verb, not heuristically guessed — a "create" tool requiring the resource's
# own name/content and an "update"/"cancel" tool requiring its id is what makes these
# structurally realistic; guessing (e.g. picking a random shuffled param) produced nonsense
# like create_calendar_event requiring "attendees" but not "title" (caught in review, 2026-08-02).

# held_out=true domains must NEVER be passed to generate_fictional_tools(..., held_out=False)
# -- same convention and purpose as toolcaller_v2_tool_families.json's held_out flag: these
# schemas exist ONLY to build the sealed held-out generalization arena (Task #4), so a
# passing score there means the model generalized to schemas it truly never saw, not ones it
# memorized during training under a different sampling call.

DOMAINS: list[dict] = [
    {
        "domain": "calendar",
        "held_out": False,
        "noun": "calendar_event",
        "verbs": [
            ("create", "Create a new calendar event.", True, ["title", "start_time"]),
            ("list",   "List upcoming calendar events.", False, []),
            ("cancel", "Cancel an existing calendar event.", True, ["event_id"]),
            ("update", "Update the time or details of a calendar event.", True, ["event_id"]),
        ],
        "params": {
            "title":       ("string",  "Event title."),
            "start_time":  ("string",  "Event start time, ISO 8601."),
            "end_time":    ("string",  "Event end time, ISO 8601."),
            "attendees":   ("string",  "Comma-separated attendee list."),
            "event_id":    ("string",  "Identifier of an existing event."),
            "reminder_minutes": ("integer", "Minutes before the event to remind."),
        },
    },
    {
        "domain": "notes",
        "held_out": False,
        "noun": "note",
        "verbs": [
            ("create",  "Create a new note.", True, ["title", "body"]),
            ("search",  "Search existing notes by keyword.", False, ["query"]),
            ("archive", "Archive an existing note.", True, ["note_id"]),
            ("tag",     "Add a tag to an existing note.", True, ["note_id", "tag"]),
        ],
        "params": {
            "title":   ("string", "Note title."),
            "body":    ("string", "Note body text."),
            "note_id": ("string", "Identifier of an existing note."),
            "tag":     ("string", "Tag to apply."),
            "query":   ("string", "Search keyword."),
        },
    },
    {
        "domain": "spreadsheet",
        "held_out": False,
        "noun": "spreadsheet_row",
        "verbs": [
            ("append", "Append a row to a spreadsheet.", True, ["sheet_id", "values"]),
            ("read",   "Read rows from a spreadsheet range.", False, ["sheet_id", "range"]),
            ("delete", "Delete a row from a spreadsheet.", True, ["sheet_id", "row_number"]),
        ],
        "params": {
            "sheet_id":   ("string", "Spreadsheet identifier."),
            "range":      ("string", "Cell range, e.g. A1:C10."),
            "values":     ("string", "Comma-separated row values."),
            "row_number": ("integer", "Row number to act on."),
        },
    },
    {
        "domain": "translation",
        "held_out": False,
        "noun": "translation",
        "verbs": [
            ("translate", "Translate text from one language to another.", False, ["text", "target_language"]),
            ("detect",    "Detect the language of a piece of text.", False, ["text"]),
        ],
        "params": {
            "text":            ("string", "Text to translate or detect."),
            "source_language": ("string", "Optional source language code."),
            "target_language": ("string", "Target language code."),
        },
    },
    {
        "domain": "weather",
        "held_out": False,
        "noun": "weather_forecast",
        "verbs": [
            ("get", "Get the current weather forecast for a location.", False, ["location"]),
        ],
        "params": {
            "location":   ("string",  "City name or coordinates."),
            "days_ahead": ("integer", "How many days ahead to forecast."),
            "units":      ("string",  "metric or imperial."),
        },
    },
    {
        "domain": "contacts",
        "held_out": False,
        "noun": "contact",
        "verbs": [
            ("create", "Create a new contact.", True, ["name"]),
            ("search", "Search contacts by name or company.", False, ["query"]),
            ("delete", "Delete an existing contact.", True, ["contact_id"]),
        ],
        "params": {
            "name":       ("string", "Contact full name."),
            "email":      ("string", "Contact email address."),
            "phone":      ("string", "Contact phone number."),
            "company":    ("string", "Contact's company."),
            "contact_id": ("string", "Identifier of an existing contact."),
            "query":      ("string", "Search term."),
        },
    },
    {
        "domain": "timer",
        "held_out": True,
        "noun": "timer",
        "verbs": [
            ("start",  "Start a countdown timer.", False, ["duration_seconds"]),
            ("cancel", "Cancel a running timer.", True, ["timer_id"]),
        ],
        "params": {
            "duration_seconds": ("integer", "How long the timer should run, in seconds."),
            "label":            ("string",  "Optional label for the timer."),
            "timer_id":         ("string",  "Identifier of a running timer."),
        },
    },
    {
        "domain": "unit_conversion",
        "held_out": True,
        "noun": "unit_conversion",
        "verbs": [
            ("convert", "Convert a value from one unit to another.", False, ["value", "from_unit", "to_unit"]),
        ],
        "params": {
            "value":     ("number", "The numeric value to convert."),
            "from_unit": ("string", "Unit to convert from, e.g. miles."),
            "to_unit":   ("string", "Unit to convert to, e.g. kilometers."),
        },
    },
]


def _load_real_tool_names() -> set[str]:
    # Fail loud, not silently (CodeRabbit review, PR #99): returning an empty set when the
    # registry is missing would make every synthetic name look collision-free by construction,
    # silently disabling the one check that keeps fictional tools from colliding with real
    # TheOrc tools -- a configuration problem should stop the run, not be swallowed.
    if not FAMILIES_PATH.exists():
        raise FileNotFoundError(
            f"tool family registry not found: {FAMILIES_PATH} -- cannot verify synthetic tool "
            "names are collision-free without it")
    data = json.loads(FAMILIES_PATH.read_text(encoding="utf-8"))
    return {t["name"] for fam in data["families"] for t in fam["tools"]}


def generate_fictional_tools(rng: random.Random, count: int, held_out: bool = False) -> list[dict]:
    """Return up to `count` distinct fictional tool schemas, each guaranteed to not
    collide with a real TheOrc tool name. Schema shape matches
    OllamaClient.ToOllamaSchema()'s flat properties/required convention exactly:
    {"name": ..., "description": ..., "parameters": {param: {"type", "description"}},
    "required": [...]}.

    held_out=False (default): draws only from DOMAINS marked held_out=False -- the pool
    dataset generation (Task #2) may use as decoys/call-targets in TRAINING examples.
    held_out=True: draws EXCLUSIVELY from DOMAINS marked held_out=True -- reserved for the
    sealed generalization arena (Task #4). The two pools never overlap, by construction --
    a training example must never contain a schema the eval later "tests" as unseen.
    """
    real_names = _load_real_tool_names()
    candidates: list[dict] = []

    for dom in DOMAINS:
        if dom["held_out"] != held_out:
            continue
        noun = dom["noun"]
        param_pool = dom["params"]
        for verb, desc, is_mutating, required in dom["verbs"]:
            plural = verb in ("list", "read", "search") and not noun.endswith("s")
            name = f"{verb}_{noun}{'s' if plural else ''}"
            if name in real_names:
                continue
            if not set(required).issubset(param_pool.keys()):
                # Explicit raise, not assert (CodeRabbit review, PR #99): `python -O` strips
                # asserts, and this is the only guard against DOMAINS declaring a required
                # param that was never added to that domain's param_pool.
                raise ValueError(
                    f"{name}: required {required} not a subset of declared params {list(param_pool)}")

            # Every verb previously declared the domain's ENTIRE param_pool, so e.g.
            # cancel_calendar_event declared title/start_time/end_time/attendees/event_id/
            # reminder_minutes even though only event_id is semantically relevant to
            # cancelling something (CodeRabbit review, PR #99) -- this both undercut the
            # "structurally realistic" goal and weakened the downstream argument-key check
            # (generate_toolcaller_v2_dataset.py's schema_params validation), since nearly any
            # domain-plausible key would pass regardless of which verb it was checked against.
            # Fix: required params always included, plus up to 2 more pool params (deterministic
            # by pool declaration order, not random, so the same seed still reproduces the same
            # schemas) -- narrows the declared surface per verb without needing to hand-curate
            # an explicit optional-param list for every one of DOMAINS' ~20 verb entries.
            optional_extra = [p for p in param_pool if p not in required][:2]
            verb_params = list(required) + optional_extra
            parameters = {p: {"type": param_pool[p][0], "description": param_pool[p][1]} for p in verb_params}

            candidates.append({
                "name": name,
                "description": desc,
                "parameters": parameters,
                "required": list(required),
                "_domain": dom["domain"],
                "_is_mutating": is_mutating,
            })

    rng.shuffle(candidates)
    return candidates[:count]


def main():
    import argparse
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--count", type=int, default=20)
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--held-out", action="store_true",
                     help="Draw from the held-out domain pool (eval-only) instead of the train pool.")
    args = ap.parse_args()

    rng = random.Random(args.seed)
    tools = generate_fictional_tools(rng, args.count, held_out=args.held_out)
    for t in tools:
        print(json.dumps(t, indent=2, ensure_ascii=False))
    pool = "held-out" if args.held_out else "train"
    print(f"\n=== {len(tools)} fictional tool schemas generated from the {pool} pool "
          f"(requested {args.count}) ===", file=sys.stderr)


if __name__ == "__main__":
    main()
