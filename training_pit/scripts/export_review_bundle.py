#!/usr/bin/env python3
"""Export a self-contained external-review bundle from an exported split.

Produces one markdown file containing BOTH the reviewer instructions and a
random sample of examples, so an outside AI (Grok, DeepSeek, ChatGPT, ...)
can review it from a single URL or a single paste — no other context needed.

This is the ONLY dataset artifact intended for git/publication: the raw JSONL
splits stay local-only per training_pit rules. Refuses examples whose metadata
says contains_sensitive_data, and re-checks for obvious secrets before writing.

Usage:
  python export_review_bundle.py [--count 10] [--seed N]
                                 [--src training_pit/datasets/train_v1.jsonl]
                                 [--out training_pit/review/REVIEW_BUNDLE.md]
"""
import argparse, json, random, re, sys
from datetime import date
from pathlib import Path

# Match secrets in assignment context or known formats — bare words like
# "TokenEstimator" or "tokens per second" are legitimate in this dataset.
SECRET_RE = re.compile(
    r"((api[_-]?key|secret|password|token)\s*[:=]\s*['\"]?[A-Za-z0-9+/_-]{8,}"
    r"|BEGIN [A-Z ]*PRIVATE KEY|ghp_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{20,})", re.I)

INSTRUCTIONS = """# TheOrc Training Pit — external review bundle

You (the AI reading this) are an independent dataset reviewer. Below are
{count} randomly sampled training examples for fine-tuning a 12B "boss" model
whose only job is: given a user's coding goal, produce a JSON swarm plan.
Give a second human-quality opinion on whether each example deserves to be in
the training set. You have no access to the codebase — judge only what is
internally verifiable. Be strict: a reviewer who keeps everything is useless.

The quality bar each example must meet:

1. 2-4 tasks. Single-task plans and 5+ task plans are defects.
2. Roles are EXACTLY one of: RESEARCHER, CODER, UIDEVELOPER, TESTER.
   Any other role string is an instant fail.
3. TESTER never creates, writes, or edits files — it only runs things and
   reports. A TESTER task with create/write verbs is an instant fail.
4. CODER/UIDEVELOPER tasks name concrete output files with extensions.
5. Tasks must agree with each other: shared deliverables must use identical
   file/class/property names across tasks.
6. The plan must solve the goal that was asked — not a mock, demo, or
   substitute feature.
7. No invented certainty about existing code internals the planner cannot know.
8. One language stack per plan, matching the goal's stack.

For EACH example output exactly:

- ID: <example id>
- VERDICT: KEEP / BORDERLINE / PULL
- REASON: 1-2 sentences citing the specific task or name at fault
- BEST TASK / WORST TASK: one line each

After all examples: a pass rate, the most common weakness across the set, and
whether you would trust a model trained on examples like these to plan reliably.

---
*Generated {today} from {src} ({total} examples in split, sample seed {seed}).*

---
"""


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--count", type=int, default=10)
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--src", default="training_pit/datasets/train_v1.jsonl")
    ap.add_argument("--out", default="training_pit/review/REVIEW_BUNDLE.md")
    args = ap.parse_args()

    src = Path(args.src)
    if not src.exists():
        sys.exit(f"source not found: {src} (run review_captures.py --export-train first)")

    records = [json.loads(l) for l in src.read_text(encoding="utf-8").splitlines() if l.strip()]
    eligible = [r for r in records
                if not r.get("metadata", {}).get("contains_sensitive_data", False)]
    seed = args.seed if args.seed is not None else random.randrange(10_000)
    random.Random(seed).shuffle(eligible)
    sample = eligible[: args.count]

    parts = [INSTRUCTIONS.format(count=len(sample), today=date.today().isoformat(),
                                 src=src.as_posix(), total=len(records), seed=seed)]
    for i, rec in enumerate(sample, 1):
        meta = rec.get("metadata", {})
        m = re.search(r"(ex_[\w]+)", meta.get("notes", ""))
        ex_id = m.group(1) if m else f"sample_{i:02}"
        goal = next((msg["content"] for msg in rec["messages"] if msg["role"] == "user"), "")
        plan = next((msg["content"] for msg in rec["messages"] if msg["role"] == "assistant"), "")
        try:  # pretty-print the plan JSON for readability
            plan = json.dumps(json.loads(plan), indent=2)
        except json.JSONDecodeError:
            pass
        parts.append(f"## Example {i} — ID: {ex_id}  (quality: {meta.get('quality','?')})\n\n"
                     f"**Goal given to the planner:**\n\n> {goal}\n\n"
                     f"**Plan the planner produced:**\n\n```json\n{plan}\n```\n")

    text = "\n".join(parts)
    hits = sorted({m.group(0) for m in SECRET_RE.finditer(text)})
    if hits:
        sys.exit(f"REFUSING to write bundle — secret-like content found: {hits}")

    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {len(sample)} examples -> {out} (seed {seed})")
    print("note: this file is intended for commit/publication; raw JSONL stays local-only")


if __name__ == "__main__":
    main()
