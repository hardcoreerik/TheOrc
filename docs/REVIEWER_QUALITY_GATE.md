# TheOrc — The Reviewer Quality Gate

> **Status:** Formalized concept. The gate machinery (`codex-review.ps1`,
> `theorc-review.ps1`, `review-capture.ps1`) exists and runs manually. This
> document makes the Reviewer an **authoritative role** in TheOrc's
> architecture and defines where it sits in the run lifecycle.

> Related: [REVIEWER_ADAPTER_GUIDE.md](REVIEWER_ADAPTER_GUIDE.md) (the local
> reviewer model), [WORKTREE_ISOLATION_DESIGN.md](WORKTREE_ISOLATION_DESIGN.md)
> (where the gate runs), [ROLE_ARCHITECTURE.md](../training_pit/ROLE_ARCHITECTURE.md)
> (the swarm execution lanes the gate is distinct from).

---

## Two Things Named "Reviewer" — Disambiguated

The word "reviewer" appears in two unrelated places in TheOrc. Conflating them
causes confusion, so this section nails the distinction first.

| | **REVIEWER execution lane** | **Reviewer Quality Gate** |
|---|---|---|
| What it is | A task *within* a swarm run | A gate that runs on the *output of* a swarm run |
| Who creates it | The boss, during decomposition | The orchestration layer, automatically |
| Current handling | Aliased to RESEARCHER (read-only investigation) | Runs the review pipeline (Codex / TheOrc) |
| Example | "Review the proposed auth design and report risks" | "Has every file this run produced passed review before we apply it?" |
| Scope | One task's slice of the goal | The whole run's diff |
| Authority | None special — just another task | **Blocks merge/apply if it fails** |

This document is exclusively about the **second** one: the Quality Gate. The
REVIEWER execution lane stays exactly as documented in
[ROLE_ARCHITECTURE.md](../training_pit/ROLE_ARCHITECTURE.md) — aliased to
RESEARCHER until a dedicated lane is built.

---

## The Principle (from SigmaLink)

SigmaLink makes Reviewer one of four canonical roles with a hard rule:

> "Every piece of work passes through a comprehensive review. Reviewer must
> pass before merge."

Their Reviewer is a Principal-Engineer-persona quality gate checking
correctness, security, and consistency, with the authority to **block
substandard work from merging**. It is not advisory — it is a gate.

TheOrc adopts this as an architectural commitment: **swarm output is not
authoritative until a Reviewer has passed it.** Today the human is that gate.
The path below replaces the human with Codex, then eventually with TheOrc's own
reviewer adapter — earning that authority through measured agreement, never
assuming it.

---

## Where the Gate Runs

The Reviewer Quality Gate has a precise home in the run lifecycle: the **merge
step** of the worktree isolation design.

```
Swarm run produces per-task worktrees
        │
        ▼
Each task verified + merged into the integration tree   ← per-task diffs exist here
        │
        ▼
┌─────────────────────────────────────────────┐
│   REVIEWER QUALITY GATE                       │
│   Runs the review pipeline on the run's diff  │
│   (integration tree vs workspace baseline)    │
│                                               │
│   PASS → run is applied to the workspace      │
│   FAIL → run is held; findings surfaced        │
│          to the user for decision              │
└─────────────────────────────────────────────┘
        │
        ▼
Workspace updated (only after gate passes)
```

Before the worktree design, there was no clean diff to review — flat staging
mixed every task's output together. The worktree integration tree gives the
gate exactly what it needs: a coherent, attributable diff of the whole run.

This is why the two designs compose: **worktree isolation produces the
reviewable artifact; the Quality Gate reviews it.**

---

## The Judge — A Trust Ladder, Not a Fixed Assignment

Who holds the gate is not fixed. It is earned, following the same trust tiers
the [Reviewer Adapter Guide](REVIEWER_ADAPTER_GUIDE.md) defines for the local
model.

| Stage | Gate held by | Condition |
|---|---|---|
| **Now** | Human | Default. The operator reviews and applies. |
| **Soon** | Codex | Run `review-capture.ps1` automatically at the gate. Codex findings shown; human still applies. |
| **Experimental** | TheOrc (advisory) | `theorc-reviewer:v1` runs *alongside* Codex at the gate. Its findings are recorded, not yet authoritative. |
| **Promoted** | TheOrc (primary) + Codex (audit) | TheOrc holds the gate; Codex spot-audits. ≥ 80% agreement on 50+ runs. |
| **Trusted** | TheOrc | TheOrc is the gate. Codex retired from the loop. ≥ 90% agreement on 200+ runs, human overrides ≤ 5%. |

This is the concrete realization of "TheOrc as gold standard for its own
development." The gate is the seat of authority; the trust ladder is how TheOrc
earns that seat.

Critically: **TheOrc reviewing TheOrc's swarm output, gated by Codex, is itself
a training-capture event.** Every gated run produces a paired
(Codex verdict, TheOrc verdict) capture — the exact training data the reviewer
adapter needs. The gate is both a quality mechanism and a data flywheel.

---

## Gate Policy — Fail Loud, Human Decides

The gate follows TheOrc's established "fail loud, never silent" philosophy
(the same one behind the HiveTaskQueue stale-worker guards and the worktree
conflict escalation):

- **A BLOCKER finding holds the run.** The integration tree is not applied to
  the workspace. The user sees the findings and decides: apply anyway, discard,
  or send specific tasks back for rework.
- **MINOR findings annotate but do not hold.** They are surfaced with the run
  result; the user can address them now or later.
- **CLEAN applies the run** (in the assisted modes; the human still confirms
  until the Trusted tier).
- **The gate never auto-discards work.** It holds and surfaces. Destroying a
  run's output is always a human decision.

This mirrors the worktree design's rule: convert what used to be a silent
failure (bad output applied without scrutiny) into a loud, attributable,
reviewable event.

---

## What This Changes Right Now

This is primarily a **formalization** — naming and positioning a gate whose
machinery already exists. The immediate, concrete changes are small:

1. **Doc authority.** The Reviewer Quality Gate is now a named architectural
   role with defined authority (blocks merge) and a defined home (the worktree
   merge step). Future work references this contract.
2. **The review pipeline gains a purpose beyond manual use.** `review-capture.ps1`
   was built for manual dataset collection; it is now also the gate's executor.
   Same script, elevated role.
3. **The trust ladder is unified.** The reviewer-adapter trust tiers and the
   gate-authority tiers are the same ladder, so "promote the adapter" and "give
   the gate to TheOrc" become one decision, not two.

What it does **not** change yet (deliberately deferred):

- No automatic gate execution wired into the swarm run completion path. That
  waits on the worktree integration tree existing (Phase 2 of the worktree
  design). Until then the gate runs manually via `review-capture.ps1`.
- No new `SwarmWorkerRole` enum value. The gate is not a swarm execution lane;
  adding an enum value would pollute boss planning (see
  [ROLE_ARCHITECTURE.md](../training_pit/ROLE_ARCHITECTURE.md) role-evolution
  policy).

---

## Open Questions

1. **Gate granularity** — does the gate review the whole run's diff at once, or
   per-task worktree diffs individually? Per-task gives finer attribution but
   N review calls per run (cost). Whole-run is one call but coarser. The B-3
   measurements used whole-diff; start there.
2. **Gate on greenfield runs** — a from-scratch project has no baseline to diff
   against. Does the gate review the full generated tree, or skip on greenfield
   until there's a prior version to compare?
3. **Where do gate verdicts persist** — alongside the run manifest, or in the
   SQLite task board (v1.6)? In-manifest is enough for now; SQLite matters when
   we want to query "show me every run TheOrc gated and how it agreed with
   Codex."

---

## Version History

| Version | Date | Notes |
|---|---|---|
| 0.1 | 2026-06-13 | Formalized the Reviewer Quality Gate as an authoritative role distinct from the REVIEWER execution lane. Positioned at the worktree merge step. Unified the gate-authority ladder with the reviewer-adapter trust tiers. |
