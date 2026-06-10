#!/usr/bin/env python3
"""
convert_plan_captures.py — Convert plan capture JSON files to chat-JSONL training examples.

Usage:
    python training_pit/scripts/convert_plan_captures.py
    python training_pit/scripts/convert_plan_captures.py --staging-dir .orc/swarm/dataset-staging
    python training_pit/scripts/convert_plan_captures.py --output datasets/staging/converted.jsonl
    python training_pit/scripts/convert_plan_captures.py --dry-run

Reads:   .orc/swarm/dataset-staging/plan_capture_good_*.json
Writes:  training_pit/datasets/staging/converted_<timestamp>.jsonl  (default)

Only converts positive examples (quality_score >= 70).
Negative examples (score <= 39) are logged but not converted —
they require manual annotation before use as training negatives.

The output chat-JSONL uses the TheOrc BossDecomposeSystemPrompt as the system message,
the goal as the user message, and the boss's raw JSON plan as the assistant message.

After conversion, run:
    python training_pit/scripts/validate_dataset.py <output_file>
    python training_pit/scripts/sanitize_dataset.py <output_file>

Requirements: none (stdlib only)
"""

import json
import sys
import argparse
from pathlib import Path
from datetime import datetime

# Force UTF-8 on Windows
if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

# ── TheOrc BossDecomposeSystemPrompt ──────────────────────────────────────────
# Keep this in sync with SwarmSession.BossDecomposeSystemPrompt in
# OrchestratorIDE/Agents/SwarmSession.cs
BOSS_SYSTEM_PROMPT = """You are TheOrc — the Orchestrator of a multi-agent AI coding swarm.
You direct four specialist minions:
  • RESEARCHER  — investigates APIs, libraries, docs; does NOT write production code
  • CODER       — writes full implementation code using the researcher's findings
  • UIDEVELOPER — writes UI code (XAML, WPF, HTML/CSS) and styling
  • TESTER      — runs existing code, executes tests, checks syntax, reports results; does NOT write or modify files

Given a user's coding goal, break it into 2–4 concurrent subtasks.
Assign each subtask to the best-fit minion role.

Rules:
- RESEARCHER tasks always get priority 1 (they run first, alone)
- CODER, UIDEVELOPER, and TESTER tasks get priority 2 (run concurrently after research)
- If no research is needed, skip RESEARCHER and assign CODER/UIDEVELOPER/TESTER tasks directly
- TESTER tasks verify code that already exists in the workspace — they do NOT receive output from CODER tasks in the same run
- Descriptions must be self-contained — minions cannot ask follow-up questions
- Maximum 4 tasks total: up to 1 RESEARCHER + up to 3 CODER/UIDEVELOPER/TESTER
- Prefer 3 priority-2 tasks when the goal has distinct implementation concerns

FILENAME RULE — task titles MUST name the output file(s):
- Good title: "Write scraper.py and ollama_client.py"
- Good title: "Build main.py Tkinter UI"
- Bad title:  "Implement article fetcher" (no filename — workers won't know what to name the file)

API CONTRACT RULE — when worker A produces a module that worker B imports:
- Decide the EXACT function/class names ONCE and use the same names in BOTH task descriptions.
- Example: if CODER writes scraper.py with function fetch_article_text(url), then the UIDEVELOPER task MUST say "from scraper import fetch_article_text" — not a different name.
- This is non-negotiable: mismatched names cause import errors at runtime.

Respond with ONLY valid JSON — no markdown fences, no preamble, no trailing text.
String values MUST NOT contain literal newlines — use \\n inside strings if needed.
{
  "plan": "one-sentence overall approach",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Short descriptive title",
      "description": "Detailed, self-contained instructions for this minion. Use \\n for line breaks inside this string."
    }
  ]
}"""


def load_capture(path: Path) -> dict | None:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (json.JSONDecodeError, OSError) as e:
        print(f"  SKIP {path.name}: {e}")
        return None


def capture_to_chat_jsonl(capture: dict) -> dict | None:
    """Convert a plan capture to a chat-JSONL training example.

    Works for both positive (score >= 70) and negative (score <= 39) captures.
    Negative captures are exported to negative_v1.jsonl for DPO/regression eval.
    """
    goal = capture.get("goal", "")
    plan = capture.get("plan")
    score = capture.get("quality_score", 0)
    boss_model = capture.get("boss_model", "unknown")
    run_id = capture.get("run_id", "unknown")

    if not goal or plan is None:
        return None

    # Serialize the plan back to compact JSON (the assistant's response)
    if isinstance(plan, str):
        assistant_content = plan.strip()
    else:
        assistant_content = json.dumps(plan, separators=(",", ":"), ensure_ascii=False)

    if not assistant_content:
        return None

    # Determine quality tier from score (reviewer override applied later in review_captures.py)
    quality = "gold" if score >= 90 else "silver"

    # Preserve source from capture; map to a valid VALID_SOURCES value
    _VALID_SOURCES = {
        "manual", "corrected_model_output", "terminal_log", "repo_issue",
        "swarm_capture", "eval_failure", "synthetic", "imported",
    }
    raw_source = capture.get("source", "swarm_capture")
    source = raw_source if raw_source in _VALID_SOURCES else "swarm_capture"
    created_by = "user" if source == "manual" else "auto"

    return {
        "messages": [
            {"role": "system", "content": BOSS_SYSTEM_PROMPT},
            {"role": "user", "content": f"Goal: {goal}"},
            {"role": "assistant", "content": assistant_content},
        ],
        "metadata": {
            "category": "boss_planning",
            "task_type": "feature_plan",
            "source": source,
            "quality": quality,
            "contains_sensitive_data": False,
            "base_model_target": boss_model,
            "created_by": created_by,
            "notes": (
                f"Converted from plan capture {capture.get('example_id', run_id)}. "
                f"Quality score: {score}/100. "
                f"Rubric: {capture.get('rubric_scores', {})}."
            ),
        },
    }


def main():
    parser = argparse.ArgumentParser(description="Convert plan captures to chat-JSONL")
    parser.add_argument(
        "--staging-dir",
        default=".orc/swarm/dataset-staging",
        help="Directory containing plan_capture_good_*.json files",
    )
    parser.add_argument(
        "--output",
        default=None,
        help="Output JSONL file path (default: training_pit/datasets/staging/converted_<ts>.jsonl)",
    )
    parser.add_argument(
        "--min-score",
        type=int,
        default=70,
        help="Minimum quality_score to include (default: 70)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print what would be converted without writing any files",
    )
    args = parser.parse_args()

    staging_dir = Path(args.staging_dir)
    if not staging_dir.exists():
        print(f"Staging directory not found: {staging_dir}")
        print("Run at least one swarm to generate plan captures, then try again.")
        sys.exit(1)

    # Find positive captures
    captures = sorted(staging_dir.glob("plan_capture_good_*.json"))
    if not captures:
        print(f"No plan_capture_good_*.json files found in {staging_dir}")
        sys.exit(0)

    print(f"Found {len(captures)} positive capture(s) in {staging_dir}")

    converted = []
    skipped = []

    for path in captures:
        capture = load_capture(path)
        if capture is None:
            continue

        score = capture.get("quality_score", 0)
        goal_preview = capture.get("goal", "")[:60]

        if score < args.min_score:
            skipped.append((path.name, score, "below min-score"))
            print(f"  SKIP  {path.name} (score={score} < {args.min_score}): {goal_preview}...")
            continue

        example = capture_to_chat_jsonl(capture)
        if example is None:
            skipped.append((path.name, score, "missing goal or plan"))
            print(f"  SKIP  {path.name}: missing goal or plan field")
            continue

        converted.append((path.name, score, example))
        tier = "GOLD" if score >= 90 else "SILVER"
        print(f"  OK    {path.name} (score={score}, {tier}): {goal_preview}...")

    print()
    print(f"Converted: {len(converted)}  |  Skipped: {len(skipped)}")

    if not converted:
        print("Nothing to write.")
        sys.exit(0)

    if args.dry_run:
        print("[dry-run] No files written.")
        sys.exit(0)

    # Determine output path
    if args.output:
        output_path = Path(args.output)
    else:
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        output_path = Path("training_pit/datasets/staging") / f"converted_{ts}.jsonl"

    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Write chat-JSONL
    with open(output_path, "w", encoding="utf-8") as f:
        for _name, _score, example in converted:
            f.write(json.dumps(example, ensure_ascii=False) + "\n")

    print(f"Written: {output_path}  ({len(converted)} examples)")
    print()
    print("Next steps:")
    print(f"  python training_pit/scripts/validate_dataset.py {output_path}")
    print(f"  python training_pit/scripts/sanitize_dataset.py {output_path}")
    print("  Review manually, then append to training_pit/datasets/train_v1.jsonl")


if __name__ == "__main__":
    main()
