# Training Pit — Batch Capture Plan v3 ("run to 150")

> **Purpose:** close the train gate (need 114 more train approvals; 36/150 at authoring).
> Eval (20/20) and negative (25/25) gates are already MET — v3 is train-only.
> **Do not start Phase 3 training.**

## What's different from v2

v3 is the first **automated farming tranche** (GOBLIN HARVEST first pass):

1. **Goals live in [batch_v3_goals.psv](batch_v3_goals.psv)** — 143 train goals,
   pipe-delimited `ID|domain|goal`, machine-consumable by the runner and pre-screen.
2. **Farming is scripted** — `scripts/farm_batch.ps1` loops `swarmcli --plan-only`
   over the goals file unattended, with resume support (`batch_v3_done.csv`).
3. **First-pass review is deterministic** — `scripts/prescreen_captures.py` auto-flags
   the four mechanical defect classes before a human reads anything:
   single-task plan · TESTER write-verbs · wrong-stack extensions · low rubric (bad capture).
   "Create <existing file>" is a WARN (needs eyes, high false-positive risk).
4. **Human review only sees survivors** — fabrication/invented-API judgment stays manual.

## Engineering rules (carried from v2 evidence)

- Every goal anchors its stack with explicit filenames.
- Single-stack goals only; no mixed-language prompts.
- Test-creation goals phrase the new test file as the CODER deliverable;
  TESTER phrasing is "run the new tests and report results".
- No docs-edit goals (they don't decompose).
- New-file goals use paths that do not exist yet; existing files use modify verbs.

## Domain mix (143 goals)

wpf_ui 25 · swarm 20 · ollama 12 · model_wiki 10 · csharp_core 15 · testing 10 ·
git 8 · python_utility 20 · powershell 15 · training_pit 8

> **Source of truth:** [batch_v3_goals.psv](batch_v3_goals.psv) is authoritative for
> goal text and counts — this doc is explanatory. The farm resumes by goal ID via
> `batch_v3_done.csv`; pre-screen matches captures to goals by **exact goal text**.
> Therefore: never edit the text of a goal whose ID already appears in the done
> file — its staged capture would no longer match and become invisible to review
> tooling. Unfarmed goal text may be edited. Capture filenames embed the run's
> local-time run_id (`yyyyMMdd_HHmmss`); manifest `decided_at` timestamps are UTC.

## Workflow

```powershell
# 1. Farm (resumable; logs per goal under .orc/swarm/farm_logs/)
powershell -File training_pit\scripts\farm_batch.ps1

# 2. Pre-screen (prints PASS/WARN/REJECT; --apply executes the rejects)
python training_pit\scripts\prescreen_captures.py --apply

# 3. Human review of PASS/WARN survivors, then approve + export
python training_pit\scripts\review_captures.py --approve <path> --split train --quality silver
python training_pit\scripts\review_captures.py --export-train
```

## Review bar (unchanged)

gold = 3–4 tasks, filenames in every CODER/UIDEV title, explicit contracts,
rubric ≥ 90, no hallucinated names · silver = rubric 70–89, named files, minor gaps ·
reject = wrong stack, role misuse, single-task, fabrication, rubric < 70.

---

*Authored 2026-06-11. Predecessors: BATCH_CAPTURE_PLAN.md, BATCH_CAPTURE_PLAN_V2.md (both fully dispositioned).*
