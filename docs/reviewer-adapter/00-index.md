# Reviewer Adapter — Documentation Index

The reviewer adapter is TheOrc's effort to train a local model that reviews
TheOrc's own code, eventually replacing Codex as the gold-standard reviewer.
This tree holds the **working artifacts** of that effort, organized by phase.

The **consolidated, user-facing guide** lives at
[`docs/REVIEWER_ADAPTER_GUIDE.md`](../REVIEWER_ADAPTER_GUIDE.md) — read that
first for the big picture. The numbered phases below are the primary-source
record behind it: raw experiment logs, research notes, and design artifacts.

This structure mirrors the SigmaLink docs lifecycle
(`01-investigation → … → 09-release`), where every claim in the canonical guide
traces back to a dated artifact here.

---

## Start here

1. [`../REVIEWER_ADAPTER_GUIDE.md`](../REVIEWER_ADAPTER_GUIDE.md) — the canonical
   strategy and current state.
2. [`01-investigation/B-3-results.md`](01-investigation/B-3-results.md) — the
   empirical result that set the project's direction: prompt engineering alone
   cannot make `qwen2.5-coder:14b` a useful reviewer.
3. [`02-research/external-prior-art.md`](02-research/external-prior-art.md) —
   the published work this strategy builds on.

---

## Phases

| Phase | Status | Contents |
|---|---|---|
| `01-investigation/` | **Active** | The B-3 baseline series — measured why the off-the-shelf model fails at review |
| `02-research/` | **Active** | External prior art: Meta semi-formal reasoning, MelcotCR, RARe, Adversarial Review, CodeReviewer |
| `03-plan/` | Canonical in guide | Two-stage training plan lives in [`../REVIEWER_ADAPTER_GUIDE.md`](../REVIEWER_ADAPTER_GUIDE.md) |
| `04-design/` | Pending | Dataset schema + validator design (when capture count nears 50) |
| `05-critique/` | Pending | Stress-test of the two-stage plan before training spend |
| `06-build/` | Pending | Stage 1 SFT + Stage 2 LoRA build logs |
| `07-test/` | Pending | Eval runs: agreement matrix vs Codex |
| `08-bugs/` | Pending | Defects found during build/eval |
| `09-release/` | Pending | `theorc-reviewer:v1` release notes + trust-tier promotion record |

---

## How this tree is used

- **Every experiment gets a dated artifact** under the relevant phase. The B-3
  series is the template: configuration, reproducibility command, raw result,
  interpretation.
- **The canonical guide cites these artifacts**, not the other way around. If
  the guide and an artifact disagree, the dated artifact is the primary source
  and the guide is updated to match.
- **Phases advance, they don't reset.** Once `06-build` starts, `01-investigation`
  stays as the historical record of why we built what we built.
