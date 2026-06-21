# Reviewer Adapter — Documentation Index

> **Status: PARKED (since 2026-06-13).** Investigation (B-3/B-4) reached a
> clear, useful conclusion — see below — and then work paused for other
> priorities (ORC ACADEMY v1/v2, HIVE security, Avalonia migration). This is
> **not abandoned**: the goal (a local reviewer adapter to eventually replace
> Codex) is still considered valuable. It's parked at a clean phase boundary,
> ready to resume. See "Why parked / how to resume" below.

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
| `01-investigation/` | **Concluded** | The B-3/B-4 baseline series — measured why the off-the-shelf model fails at review; reached a clean, conclusive answer |
| `02-research/` | **Concluded for now** | External prior art: Meta semi-formal reasoning, MelcotCR, RARe, Adversarial Review, CodeReviewer |
| `03-plan/` | Canonical in guide, PARKED | Two-stage training plan lives in [`../REVIEWER_ADAPTER_GUIDE.md`](../REVIEWER_ADAPTER_GUIDE.md) |
| `04-design/` | PARKED | Dataset schema + validator design (when capture count nears 50) |
| `05-critique/` | PARKED | Stress-test of the two-stage plan before training spend |
| `06-build/` | PARKED | Stage 1 SFT + Stage 2 LoRA build logs |
| `07-test/` | PARKED | Eval runs: agreement matrix vs Codex |
| `08-bugs/` | PARKED | Defects found during build/eval |
| `09-release/` | PARKED | `theorc-reviewer:v1` release notes + trust-tier promotion record |

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

---

## Why parked / how to resume

The B-3/B-4 series (2026-06-13) gave a clean, conclusive answer: prompt
engineering on `qwen2.5-coder:14b` has a hard ceiling (0/3 Codex catches
across every technique tried), so a Stage 1 SFT base is *necessary*, not a
maybe. That's a good stopping point, not a stall — there was nothing left to
learn cheaply before committing real training spend.

What actually paused it: Stage 1 SFT needs off-machine compute (H100-class,
rented or a downloaded checkpoint) and the local machine's GPU time was
committed to ORC ACADEMY v1/v2 boss training instead. Capture volume also
never grew past 4 (phase gate needs ≥50) because nobody was running
`tools/review-capture.ps1` day-to-day once attention moved elsewhere.

**To resume:** either (a) keep using `tools/review-capture.ps1` during normal
dev to passively grow Tier-1 captures toward 50, or (b) deliberately schedule
GPU/rented-compute time for Stage 1 SFT once a public corpus (MelcotCR or
cleaned CodeReviewer) is sourced. Either is a valid restart point — see the
phase gate table in [`REVIEWER_ADAPTER_GUIDE.md`](../REVIEWER_ADAPTER_GUIDE.md)
for the full unblock checklist.
