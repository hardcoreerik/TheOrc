# TheOrc — Reviewer Adapter Guide

> This guide explains how TheOrc learns to review its own code. Read the [Training Pit Guide](TRAINING_PIT_GUIDE.md) for the planning-adapter story; this one covers the **reviewer adapter** — a separate, parallel pipeline that turns Codex code reviews into training data for a local reviewer model. Read [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) for the manifest-driven approval flow used by both pipelines.

---

## What The Reviewer Adapter Is

A second, smaller adapter trained on `qwen2.5-coder:14b` whose only job is reading a git diff and producing a structured code review with `BLOCKER` / `MINOR` / `CLEAN` findings tied to specific `file:line` locations.

It is **not** the boss adapter. The boss (`theorc-boss:gemma4-ft`) plans swarm work; the reviewer reads diffs and finds bugs. They are different models trained for different jobs on different data.

The reviewer's eventual purpose is to become the gold standard for code review of TheOrc itself — replacing Codex as the reference reviewer once it's trustworthy. Until then, **Codex is gold, TheOrc is the apprentice**, and we capture every paired review as a training example.

---

## The Current End-To-End Flow

```text
git diff (staged or branch range)
  -> tools/review-capture.ps1
       -> tools/codex-review.ps1     -> Codex verdict (GOLD)
       -> tools/theorc-review.ps1    -> TheOrc verdict (apprentice)
  -> paired capture in .orc/swarm/review-staging/review_capture_*.json
  -> (future) review_dataset.py exports review_v1.jsonl
  -> (future) phase 2 trains theorc-reviewer:v1
  -> registered alongside theorc-boss in the Adapter Registry
```

The first three steps are live today. Capture conversion, training, and registration are planned for Phase 2 once enough captures accumulate.

---

## Capture State Today

Tracked in the Training Pit panel under **🔍 Review captures**:

- Counts files in `.orc/swarm/review-staging/`
- Live updates as new captures land (no refresh needed)
- Progress bar to the next milestone: 50 captures = experimental adapter, 200 = production adapter

Baseline reading from 2026-06-13:

- TheOrc caught **0 of 6** real findings on a 60 KB diff using the free-form prompt
- Doubling context (16K → 32K) changed nothing
- The bias toward "CLEAN" is in the model's training, not its memory budget

This is the gap the adapter exists to close.

---

## Two-Stage Training Architecture

Unlike the boss adapter, the reviewer is built in two stages because reviewing is a well-studied problem with **public datasets and a known recipe**.

### Stage 1 — Public corpus SFT

**Purpose:** teach the base model that its job is to find and explain issues, not just generate code.

**Candidate corpora:**

| Corpus | Size | Notes |
|---|---|---|
| **MelcotCR** | 12,881 examples | Cleaned chain-of-thought reviews; matches our target output format |
| **CodeReviewer** (Microsoft) | ~642 K raw | Multi-language; use the cleaned variant (85% valid vs 64% raw) |

Stage 1 needs an H100-class GPU and is run off-machine (rented compute or a downloaded checkpoint). Stage 2 — the part that learns TheOrc — runs locally.

### Stage 2 — TheOrc captures LoRA

**Purpose:** adapt the base reviewer to TheOrc's conventions: WPF/.NET 10 patterns, `AutomationProperties.AutomationId` enforcement, NUnit `T##_*.cs` naming, FlaUI testability, our file layout.

Runs on the same trainer as the boss adapter (`train_lora.py`) with the base swapped to the Stage 1 checkpoint.

---

## Three Sources of Training Data

| Tier | Target % | Source | Notes |
|---|---|---|---|
| **1. Paired captures** | ~70% | `tools/review-capture.ps1` runs during normal dev | Codex verdict is the gold label |
| **2. Hand-authored** | ~20% | A human writes a review for a representative diff | Covers failure modes we haven't hit yet (cross-file API drift, async-void traps, missing AutomationIds) |
| **3. Public C# subset** | ~10% | Cleaned CodeReviewer filtered to C#/C++/PowerShell | Regularization to prevent forgetting general C# review |

---

## Why Public Datasets Are Used Here (Different From The Boss)

The [Training Pit Guide](TRAINING_PIT_GUIDE.md) and the developer strategy doc both refuse public datasets for the boss adapter. The reviewer adapter is the opposite:

1. **The boss's prompt is TheOrc-specific.** Public datasets use general chat templates — wrong shape.
2. **The reviewer's prompt is generic.** Diff in, structured review out. Public datasets transfer cleanly.
3. **Bootstrap reality.** The 0/6 baseline means we cannot reach competent reviewing from TheOrc captures alone — we'd need thousands before the model figures out what a review looks like. Public data gives us that base layer for free.

This reversal is intentional and documented per-adapter, not a system-wide policy change.

---

## The Prompt Architecture — Semi-Formal Reasoning

Meta researchers showed in 2026 that forcing a model to fill in a structured "certificate" template lifts review accuracy from 78% to 88-93% **without any fine-tuning**. The mechanism: the model can't shortcut to "CLEAN" because the template requires evidence first.

We adopt the same template for both training and inference:

```text
DEFINITIONS:
  D1: A change is CORRECT iff [executable semantics + repo conventions hold]
  D2: An issue is BLOCKER iff [runtime crash, correctness regression, data loss]
  D3: An issue is MINOR iff [convention violation, doc/code drift]

PREMISES:
  P1: Files modified — [list with line ranges]
  P2: New functions / API surface — [list with signatures]
  P3: Repo conventions in scope — [AutomationId, NUnit T##_*, async void, ...]

IMPORTS AND DEPENDENCIES:
  For each modified file: cross-references, shadowed names, public surface changes

EXECUTION TRACE (per substantive change):
  Pre-change behavior: [traced]
  Post-change behavior: [traced]
  Divergence: [SAME / DIFFERENT — for what input class]

CONVENTION CHECKS:
  - AutomationId on new interactive controls?
  - NUnit tests follow T##_*.cs pattern?
  - Commit-message behavior claims supported by diff?

FINDINGS:
  BLOCKER <file>:<line> — <issue>, justified by [premise/trace ref]
  MINOR   <file>:<line> — <issue>, justified by [premise/trace ref]

FORMAL CONCLUSION:
  By D1 above, [N BLOCKERs + M MINORs found | CLEAN — all traced paths preserve correctness].
```

CLEAN is reachable, but only after the certificate is filled in. The one-word shrug is structurally blocked.

---

## What A Capture Looks Like

Every paired capture in `.orc/swarm/review-staging/` is a JSON file shaped like this:

```json
{
  "example_id": "review_20260613_090559",
  "captured_at": "2026-06-13T09:05:59-07:00",
  "scope": "staged",
  "range": "",
  "stats": "5 files changed, 964 insertions(+), 37 deletions(-)",
  "diff": "<the actual diff text>",
  "verdicts": {
    "codex":  "<Codex output — gold>",
    "theorc": "<TheOrc output — apprentice>",
    "claude": null
  },
  "gold": "codex",
  "review_model": "qwen2.5-coder:14b",
  "versions": {
    "theorc_app": "1.4.0",
    "git_head": "59733dd",
    "git_branch": "master"
  }
}
```

The `claude` slot is reserved so a future UI integration with the Claude API can fill it without breaking the format.

---

## Capture Hygiene

- **Default flow** (`tools/review-capture.ps1` with no flags) — both verdicts captured, balanced training row
- **Debug flow** (`-SkipCodex` or `-SkipTheOrc`) — produces single-side captures that are **not** valid training rows and will be filtered out by the export script
- **Failed runs** (one reviewer timed out or errored) — partial capture is saved but flagged via exit code 2; export script discards these

The capture saved on 2026-06-13 09:05 is the first valid Tier-1 row.

---

## Phase Gate — When Training Can Start

Phase 2 training is **blocked** until all of these are true:

| Condition | Required | Current (2026-06-13) |
|---|---|---|
| Tier 1 paired captures (both verdicts non-null) | ≥ 50 | 1 |
| Tier 2 hand-authored examples | ≥ 20 | 0 |
| Tier 3 public C# subset prepared | ≥ 100 cleaned examples | not started |
| Semi-formal certificate prompt deployed in `tools/theorc-review.ps1` | Done | **Done** (2026-06-13) |
| Baseline measurement: prompt-engineering-only accuracy on a held-out eval | ≥ 1 published number | **B-3 measured 2026-06-13: 0/3 catches, but structured output produced (3,904 chars vs 94). Next step: top-1 RAG anchor.** |
| Stage 1 SFT base downloaded or reproduced | Done | not started |
| Validator script (`validate_review_dataset.py`) | Exists | not written |

**Mid-phase exception:** if the semi-formal certificate prompt alone gets TheOrc to ≥ 3/6 findings on the 2026-06-13 benchmark diff without any training, that is publishable evidence that prompt engineering may be enough — Stage 1 SFT can be deferred.

---

## Eval Metric — Agreement Matrix vs Codex

Eval captures live in `training_pit/datasets/review_eval_v1.jsonl` and are never used for training. Each eval entry is a diff with a human-curated gold finding list.

For each evaluated diff:
- **True positives:** findings TheOrc and Codex both report on the same `file:line ± 3`
- **False positives:** TheOrc-only findings the human reviewer rejects
- **False negatives:** Codex findings TheOrc missed
- Codex-only findings the human rejects: dropped (Codex hallucinations don't penalize us)

The adapter is "production ready" when:

- BLOCKER recall ≥ 90% on a 30-diff held-out set
- BLOCKER precision ≥ 80%
- MINOR F1 ≥ 0.5

---

## Trust Promotion Ladder

The Adapter Registry tracks three trust tiers; the reviewer adapter walks them like any other:

1. **Experimental** — adapter exists, no production use
2. **Promoted** — A/B agreement with Codex ≥ 80% on 50+ diffs, human-confirmed
3. **Trusted** — A/B agreement ≥ 90% on 200+ diffs, human reviewer overrides Codex ≤ 5% of the time

Only at **Trusted** does TheOrc become the gold label in new captures. This matches the HIVE MIND "give/take trust, trust-first" principle: trust is earned by measurable agreement, not assumed.

---

## Why qwen2.5-coder:14b

| Candidate | VRAM | Code understanding | Status |
|---|---|---|---|
| **qwen2.5-coder:14b** ✓ | ~8 GB | Excellent | **Chosen.** Native 32K context, public HF source, matches the MelcotCR recipe |
| deepseek-coder-v2:16b | ~8 GB | Excellent | Viable alternative; worth a baseline comparison |
| gemma4:12b | ~7 GB | Good | Already tuned for boss planning. Mixing review work would degrade the boss |
| nemotron-3-nano:4b | ~3 GB | Weak | Too small to hold a full diff + reasoning trace |

---

## Open Questions

1. Does the certificate prompt alone make qwen2.5-coder:14b useful, or do we still need Stage 1 SFT?
2. Is the MelcotCR dataset publicly downloadable, or do we have to reproduce it from the paper?
3. Should we capture Claude reviews too? Schema slot is reserved — adds value but adds API cost.
4. Is cross-file reasoning (caller/callee API drift) solved by the certificate format, or does it need a separate "reference graph" preprocessing step?

---

## References

External research that shaped this strategy:

- **Agentic Code Reasoning** (Ugare & Chandra, Meta, 2026) — semi-formal reasoning lifts patch accuracy 78% → 93% with no training
- **MelcotCR** (2025, arxiv 2509.21170) — Qwen2.5-14B + 12,881 examples matched DeepSeek-R1 671B at 1/47 the size
- **Too Noisy To Learn** (2025, arxiv 2502.02757) — cleaned CodeReviewer from 64% to 85% validity
- **Qwen2.5-Coder Technical Report** — confirms PR/review data was in pretraining; explains why the base "knows" reviews but won't produce them under default prompting

Related TheOrc docs:

- [Training Pit Guide](TRAINING_PIT_GUIDE.md) — the planning-adapter pipeline (boss)
- [Dataset Review Workflow](DATASET_REVIEW_WORKFLOW.md) — manifest-driven approval flow
- [Architecture](ARCHITECTURE.md) — system view
- [Hardware Guide](HARDWARE_GUIDE.md) — what runs where
