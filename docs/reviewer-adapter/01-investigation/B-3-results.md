# Investigation — The B-3 Baseline Series

**Date:** 2026-06-13
**Question:** Can prompt engineering alone make `qwen2.5-coder:14b` a useful
code reviewer, or is fine-tuning required?
**Answer:** Fine-tuning is required. Four prompt-engineering configurations all
scored **0/3** against Codex gold findings on a real C# diff.

This is the primary-source record. The consolidated summary lives in
[`../../REVIEWER_ADAPTER_GUIDE.md`](../../REVIEWER_ADAPTER_GUIDE.md) §Baseline
Measurements.

---

## Setup

**Model under test:** `qwen2.5-coder:14b` (Ollama, 32K context, temp 0.1 base)

**Benchmark diff:** the captured pre-fix diff from
`review_capture_20260613_102048.json` — 79,516 chars, 7 files, the Training Pit
harvest/train picker + review-captures work before Codex's fixes were applied.

**Codex gold findings (3):**
- BLOCKER `TrainingPitPanel.xaml.cs:1103` — Forge picker gated on both datasets
  AND models loaded; if Ollama is down the dataset picker never fills and
  StartForge falls back to hardcoded `train_v1.jsonl`.
- BLOCKER `TrainingPitPanel.xaml.cs:1199` — `BtnReviewNow` launches review
  without `-Range`, so "Review current branch" silently reviews only staged
  changes.
- MINOR `REVIEWER_ADAPTER_GUIDE.md:191` — phase-gate table says certificate
  prompt is "NOT done" while the same diff deploys it (self-contradiction).

**Reproducibility:** all runs used the replay harness so the diff is fixed and
git-independent:

```powershell
# B-3   (certificate prompt, single shot)
pwsh tools\theorc-review.ps1 -DiffFile .orc\swarm\review-staging\review_capture_20260613_102048.json

# B-3b  (+ RAG top-1 anchor)
pwsh tools\theorc-review.ps1 -DiffFile <same> -UseRagAnchor

# B-3c  (+ self-consistency 5x, no RAG)
pwsh tools\theorc-review.ps1 -DiffFile <same> -SelfConsistencyN 5

# B-3d  (+ RAG + self-consistency 5x)
pwsh tools\theorc-review.ps1 -DiffFile <same> -UseRagAnchor -SelfConsistencyN 5
```

The hash-based RAG filter ensured the benchmark capture could not be selected
as its own anchor (no gold leakage) — confirmed in the B-3b/B-3d logs with the
line `RAG: skipping review_capture_20260613_102048.json — same diff as the one
under review`.

---

## Results

| ID | Config | Codex catches | FPs after vote | Output size | Failure mode |
|---|---|---|---|---|---|
| B-3 | certificate only | 0/3 | 2 | 3,904 chars | Hallucinated findings from doc-comments |
| B-3b | + RAG-1 | 0/3 | 3 | — | Verbatim copy-paste of anchor's findings |
| B-3c | + SC-5× | 0/3 | 1 | — | Right concept (mutual-exclusion), wrong line |
| B-3d | + RAG + SC-5× | 0/3 | **0** | — | Voting canceled the RAG copy-paste |

Prior baselines (before this series, free-form prompt):
- B-1: free-form, 16K ctx → `CLEAN` (94 chars), 0/3
- B-2: free-form, 32K ctx → `CLEAN` (94 chars), 0/3 — ruled out context as the cause

---

## Per-configuration detail

### B-3 — certificate prompt alone

The Meta-style semi-formal certificate (premises → imports → execution trace →
convention checks → findings → formal conclusion) transformed the output from a
94-char `CLEAN` shrug into a 3,904-char fully-structured certificate. Every
section was filled. But the two findings it produced were **hallucinated from
documentation comments** in the diff — the model read a doc-comment describing
the review-captures feature and reported it as a BLOCKER.

**Lesson:** the certificate adds structure and forces the model to reason, but
structure alone does not give it the skill to tell a real bug from a comment.

### B-3b — certificate + RAG top-1 anchor

RAG selected the most-similar prior capture (`review_capture_20260613_090559`,
cosine 1.000 after the same-diff was filtered out) and prepended its Codex
review as a calibration example, with an explicit "DO NOT copy this example's
findings" instruction.

The model **copy-pasted all three of the anchor's findings verbatim** into its
own output (lines 491, 773, 826 — the anchor's findings, not this diff's). The
RARe paper's finding (top-1 RAG lifts review quality on larger models) did
**not** transfer to the 14B coder tier: the model treats the anchor as a
template to fill in, not a standard to calibrate against.

**Lesson:** RAG-1 with verbatim findings backfires at this model size. A v2
would need to abstract the anchor into *categories of attention* rather than
showing literal findings.

### B-3c — certificate + self-consistency 5×

Five rollouts at ramped temperatures (0.10 → 0.70), majority vote (≥3 of 5) on
the `FINDINGS_SUMMARY` block, with line-proximity dedup (findings within ±5
lines treated as the same).

Individual rollouts each produced 2-3 hallucinated findings, but they
**hallucinated different things** at different temperatures, so the vote
filtered most of them out. One finding survived — a mutual-exclusion concern at
the wrong line number. The concept was approximately real; the location was not.

**Lesson:** self-consistency is a reliable noise filter. It cannot add signal
the model doesn't have, but it cleanly removes uncorrelated hallucinations.

### B-3d — the full stack

RAG anchor + 5× self-consistency. The voting mechanism **canceled the RAG
copy-paste**: at low temperature the model copied the anchor, but higher-temp
rollouts diverged, so no copied finding reached the 3-of-5 threshold. Result:
zero false positives — the best precision of the series — but still **0/3** real
catches.

**Lesson:** stacking the noise filter on top of the contaminating RAG gives
clean output, but "clean output that finds nothing" is not a useful reviewer.

---

## Conclusions

1. **There is a hard ceiling.** Across four orthogonal prompt-engineering
   techniques, the model never caught a single Codex gold finding on a real diff
   with cross-file API drift, gated init conditions, and doc/code
   self-contradiction.
2. **The bottleneck is reviewer-skill priors, not prompting.** The model can be
   forced to produce a well-structured review; it cannot be prompted into having
   the judgment to populate that structure correctly.
3. **Stage 1 SFT is necessary, not optional.** The strategy doc's "mid-phase
   exception" (defer SFT if prompting reaches ≥3/6) is retired.

---

## Infrastructure produced (survives the negative result)

- **Replay harness** (`theorc-review.ps1 -DiffFile`) — deterministic A/B of any
  prompt config against a fixed diff.
- **Hash-based RAG leak filter** — prevents a capture being its own anchor.
- **Temperature-ramped self-consistency vote** with line-proximity dedup — a
  reusable noise filter for post-SFT use.
- **Three benchmark captures** with Codex gold findings — the seed of the
  eventual eval set.

---

## Follow-up experiments (not yet run)

- **B-4** — `deepseek-coder-v2:16b` with the certificate prompt. Different
  training prior; the one base-model variable the B-3 series did not probe. This
  is the cheapest remaining experiment and the only one that could move the
  ceiling without training.
- **B-5** — RAG-v2 with category-abstracted anchors instead of verbatim
  findings. Re-test only after SFT, to see if calibration works once the model
  has baseline competence.
