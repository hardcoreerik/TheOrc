# TheOrc — Roadmap

> This roadmap reflects what is shipped in the current codebase versus what is still only specified. For the current system shape, start with [ARCHITECTURE.md](ARCHITECTURE.md) and [GLOSSARY.md](GLOSSARY.md).

---

## Current State

TheOrc already ships more of the recent roadmap than the older docs implied. The codebase now includes:

- in-app help and embedded docs
- status-bar build stamp
- Swarm Board capability badges and per-configuration metrics history
- Model Wiki trends, comparison view, export, and capability test surfaces
- ORC ACADEMY training GUI with heartbeat, VRAM cap, resume, and re-attach
- Training Pit preflight gates with the current dataset thresholds already met
- scripted support tools such as `codex-review.ps1` and `harvest_marker_watch.ps1`

---

## Shipped

### Core Shell

- Single, Swarm, and Chat modes are implemented in the WPF shell.
- `F1` opens the in-app Help window.
- The status bar shows workspace, branch, build stamp, model, and status text.
- The token cost estimator is active in the main window context display.

### Single-Agent Runtime

- `AgentLoop` supports plan-only review and execute mode.
- Git checkpoints are created before execution.
- Tool calls flow through approval-aware `ToolRegistry` handlers.
- refusal nudging and text-format tool-call fallback are implemented.

### Swarm Runtime

- the four worker roles are implemented: `RESEARCHER`, `CODER`, `UIDEVELOPER`, `TESTER`
- capability-aware fallback decisions are implemented in `SwarmSteering`
- co-work pauses, steering, and worker continuation are implemented
- Swarm Board capability badges are live
- Swarm Board metrics history is live

### GOBLIN MIND

- format fingerprinting is implemented
- category boundary mapping is implemented
- adaptive schema generation is implemented
- schema reduction middleware is implemented
- evolutionary fitness storage and GUI surfacing are implemented
- steering consumes capability data at runtime

### Model Intelligence

- Model Wiki / Lab is implemented
- local capability test results are persisted
- trends strip is implemented
- model comparison window is implemented
- capability matrix export is implemented

### Training Pit

- boss-plan auto-capture is implemented
- manifest-driven review is implemented
- prescreen and judge triage tooling exist
- preflight gate checking exists
- ORC ACADEMY training GUI exists
- `train_lora.py` supports dry run, VRAM cap, checkpoints, resume, and progress heartbeat

### ORC ACADEMY v1 — Boss Adapter (Shipped 2026-06-12)

- 900 reviewed boss plans harvested via GOBLIN HARVEST / NIGHT HARVEST
- LoRA v1 trained locally on Gemma 4 12B (148 min, RTX 5070 Ti)
- A/B eval: 99.3% pass rate / 84 perfect plans vs 94.5% / 69 base (87 blind cases)
- Adapter deployed as `theorc-boss:gemma4-ft` in Ollama via GGUF LoRA + ADAPTER directive
- `tools/merge_lora.py` and `training_pit/adapters/registry.json` added
- `theorc-boss-gemma4-ft.Modelfile` is the production deployment spec

---

## Active Work

### Documentation

The docs are being normalized around the current implementation, especially:

- the architecture narrative
- glossary-backed terminology
- recent UI and Training Pit features

### Data Scale

The 1,000 train example target is met (1,009 harvested, 900 reviewed and used for v1 training). Next dataset milestone: extend to ~2,000 diverse examples covering edge-case goal types for a v2 adapter.

### Distributed Design

HIVE MIND is still a spec, not a shipped feature. The current work is architectural preparation rather than implementation.

---

## Planned

### HIVE MIND

Phase 3A (distributed swarm) **shipped in v1.4.0** (2026-06-12): HiveTaskQueue,
worker polling, model-aware scheduling, LAN beacon discovery. See
[HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md). Remaining planned work:

- capability-aware job placement refinements
- remote harvest and academy execution with artifact return
- `/events` lifecycle side-channel (monitor pane/task lifecycle without
  polluting task semantics — SigmaLink mailbox-back-channel pattern)
- "zero idle chatter" message discipline (HiveTask messages must carry progress
  or block info; no bare status pings)

### Orchestration Hardening (SigmaLink-inspired)

Patterns absorbed from the SigmaLink comparison (2026-06-13), in priority order.
Two are designed; two are deferred with rationale.

| Item | Target | Status | Design |
|---|---|---|---|
| **Worktree-per-task isolation** | v1.5 | **Shipped** | [WORKTREE_ISOLATION_DESIGN.md](WORKTREE_ISOLATION_DESIGN.md) |
| **Reviewer Quality Gate** | v1.5 | **Shipped** | [REVIEWER_QUALITY_GATE.md](REVIEWER_QUALITY_GATE.md) |
| **SQLite task board** | v1.6 | Deferred | — |
| **Skills system (Anthropic Skills spec)** | v1.5+ | Deferred | — |

**Worktree-per-task isolation** — shipped in v1.5. Each swarm task works in its
own git worktree with strict file ownership, making parallel runs conflict-free
by construction. Key guarantees: all-or-nothing `TryClaim` (no hold-and-wait
deadlock), serialized integration merges, per-task tool registry rooted at the
worktree path, TESTER/RESEARCHER exempt from ownership (read-only roles).
Opt-in via `AppSettings.HiveWorktreeIsolation`. Composes with HIVE (isolation
is Warchief-side; workers stay stateless).

**Reviewer Quality Gate** — swarm output is not authoritative until a Reviewer
passes it. Runs at the worktree merge step. The gate's judge follows a trust
ladder: human → Codex → TheOrc (once `theorc-reviewer:v1` earns it). Machinery
(`review-capture.ps1`) already exists; this formalizes its authority and home.

**SQLite task board** (deferred to v1.6) — replace the JSON manifests +
filesystem-walk counting with a SQLite system-of-record. Rationale for waiting:
the JSON path works today and a concurrent-write race (hit once in the night
harvest) is the only pressing motivation; not worth the migration cost until the
worktree + gate work lands and the schema stabilizes. Enables cross-restart
session resume and queryable run history.

**Skills system** (deferred to v1.5+) — drag-and-drop Anthropic Skills
frontmatter to load reviewer/domain skills (security, performance,
accessibility) instead of hard-coding prompts in scripts. Rationale for
waiting: needs a UI surface to be useful, and the reviewer prompt is still
churning (B-3 series, certificate template). Premature to externalize a prompt
we are actively redesigning.

### Broader Platform Expansion

The repository still contains the design direction for a cross-platform layer, but the current production experience remains the Windows WPF application.

### Further Model And Training Work

ORC ACADEMY v1 is complete. Open follow-up work:

- v2 adapter: broader goal coverage, edge-case plans, ~2,000 train examples
- automated eval harness integrated into the Training Pit UI
- explore on-platform adapter iteration (TheOrc writes its own training goals)

---

## Reading Order

If you are new to the project, read in this order:

1. [ARCHITECTURE.md](ARCHITECTURE.md)
2. [GLOSSARY.md](GLOSSARY.md)
3. [USER_GUIDE.md](USER_GUIDE.md)
4. [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md)
5. [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md)
