---
name: grok-review
description: Run a second-opinion Grok (xAI) code review of TheOrc changes at the right cost tier — quick (default, cheap, latest commit), diff (uncommitted pre-commit check), full (PR-scope with repo reads and conventions), adversary (post-review red team that hunts what the prior reviewer missed). Use when asked for a "grok review", a second opinion, an external review, or before merging a PR.
---

# Grok review — second-opinion review at the right cost tier

One script, four modes: `Tools/grok-review.ps1 -Mode quick|diff|full|adversary`.
Grok tokens are budgeted — always pick the **cheapest mode that answers the question**,
and prefer `-DryRun` first when unsure what scope a run will cover.

## Mode selection

| Mode | When | Scope default | Repo reads | Diff cap / timeout |
|---|---|---|---|---|
| `quick` (default) | after each commit, iteration rounds | `HEAD~1..HEAD` | no (single-turn) | 64KB / 300s |
| `diff` | before committing | uncommitted (staged+unstaged) | no (single-turn) | 128KB / 300s |
| `full` | **once** before merging a PR | `-PR <n>` → `origin/<base>...HEAD` | yes | 512KB / 900s |
| `adversary` | after Claude review/fix rounds pass | same as full | yes | 512KB / 900s |

Token discipline:
- Iteration loops (fix → re-review) use `quick`, never `full`. Run `full` once when the branch is merge-ready.
- `adversary` is for when everything already looks CLEAN — it red-teams commit/PR claims and is told
  "a prior reviewer passed this; find what they missed." Always pass `-PriorReview <file>` pointing at
  the latest saved review (or a file of Claude's own findings) so it doesn't burn tokens re-reporting knowns.
- Reads are hard-blocked in quick/diff and writes/shell/web are hard-blocked in ALL modes via `--deny`
  rules (the CLI's `--disallowed-tools` flag silently does nothing — do not "simplify" back to it).

## Commands

```powershell
pwsh -NoProfile -File Tools/grok-review.ps1                          # quick, latest commit
pwsh -NoProfile -File Tools/grok-review.ps1 -Mode diff               # pre-commit check
pwsh -NoProfile -File Tools/grok-review.ps1 -Mode full -PR 64        # pre-merge, PR scope
pwsh -NoProfile -File Tools/grok-review.ps1 -Mode adversary -PR 64 -PriorReview .orc\reviews\grok_full_<ts>.md
pwsh -NoProfile -File Tools/grok-review.ps1 -Mode full -PR 64 -DryRun  # inspect scope/prompt, no tokens
```

Scope precedence: `-Staged` > `-Mode diff` > `-PR` > `-Range` (default `HEAD~1..HEAD`).
Always use `-PR <n>` for PR-scope reviews — it uses a three-dot merge-base diff; a hand-written
two-dot `origin/master..HEAD` wrongly includes reversed master-side commits.

## Reading results

- Exit codes: `0` CLEAN or MINORs only · `1` BLOCKER(s) · `2` timeout · `3` grok not installed · `5` git/gh/grok tool error.
- Findings print as `BLOCKER|MINOR <file>:<line> — <issue>` lines; the report is saved to
  `.orc/reviews/grok_<mode>_<timestamp>.md` (feed this file to a later `-PriorReview`).
- Grok occasionally hallucinates line numbers — verify a finding's location in the actual file
  before fixing "at" the reported line.
- On exit 1: fix the blockers, then re-run **quick** (not full) to confirm.

## Relationship to other review paths

- `/code-review` and `efficient-diff-reviewer` are Claude reviewing the code itself; this skill is the
  external second opinion. The highest-value sequence pre-merge: Claude review → fix → `grok full` →
  fix → `grok adversary -PriorReview <full report>`.
- `Tools/dual-review.ps1` runs codex + grok in parallel; this script keeps its exit-code contract.
