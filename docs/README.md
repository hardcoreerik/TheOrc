# TheOrc — Documentation Index

New to TheOrc? Start with [QUICK_START.md](QUICK_START.md) for your first run, then
[USER_GUIDE.md](USER_GUIDE.md) to understand the app. The guides below cover every feature in depth.

For the authoritative public "what is shipped vs planned" status, use
[ROADMAP.md](ROADMAP.md); when a guide and the roadmap disagree, the roadmap
wins. [`../.grok/PROJECT_TRUTH.md`](../.grok/PROJECT_TRUTH.md) is retained as
adversarial-review context and may contain deeper implementation notes.

---

## Foundation

- [ARCHITECTURE.md](ARCHITECTURE.md) — how the Avalonia shell, agent runtime, GOBLIN MIND, swarm
  lifecycle, Training Pit, and HIVE MIND layer fit together (technical reference)
- [GLOSSARY.md](GLOSSARY.md) — definitions for all TheOrc terms: goblins, ORC ACADEMY, HIVE MIND,
  Warchief, Pit Boss, GOBLIN HARVEST, and more
- [ROADMAP.md](ROADMAP.md) — what is already shipped vs. what is still planned

---

## Getting Started

- [QUICK_START.md](QUICK_START.md) — the fastest path from install to first successful run
- [INSTALLATION.md](INSTALLATION.md) — installer options, portable build, source build, prerequisites
- [USER_GUIDE.md](USER_GUIDE.md) — the full guide to modes, the status bar, approvals, shortcuts, Help

---

## Execution Modes

- [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md) — the plan-then-execute loop, approvals, tool dispatch
- [SWARM_GUIDE.md](SWARM_GUIDE.md) — the boss plus four worker lanes, capability-aware routing, the Swarm Board

---

## HIVE MIND

- [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) — the HIVE MIND user guide: multi-PC networking, the
  constellation panel, the Warchief, security, fleet deploy (shipped v1.6)
- [HIVE_PAIRING_SPEC.md](HIVE_PAIRING_SPEC.md) — secure pairing & node auth spec: first-contact
  pairing, request signing, mesh heartbeat, leader election (core crypto shipped v1.6)
- [HIVE_MEMBERSHIP_SPEC.md](HIVE_MEMBERSHIP_SPEC.md) — hive-wide identity, membership certificates,
  auto-promotion (phases 1–4 shipped v1.9.4)

---

## Models And Hardware

- [MODEL_GUIDE.md](MODEL_GUIDE.md) — built-in profile scores, write_file caveats, suitability data
- [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) — in-app model intelligence: trends, comparison,
  capability tests, export
- [HARDWARE_GUIDE.md](HARDWARE_GUIDE.md) — hardware tiers and what they mean for inference and training

---

## Training Pit

- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) — the full capture-to-adapter pipeline (Pit Boss,
  ORC ACADEMY, NIGHT HARVEST, the v1 adapter)
- [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) — capture review, manifest decisions,
  exports, the preflight safety gate

---

## TheOrc Foundry

- [THEORC_FOUNDRY.md](THEORC_FOUNDRY.md) — planned custom-model strategy, model family, local
  self-improvement boundaries, hardware hypotheses, and delivery phases
- [FOUNDRY_ARENA.md](FOUNDRY_ARENA.md) — planned dataset admission, baseline comparison, promotion,
  quarantine, and rollback policy
- [THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md) — documentation-only contract for the first
  proposed Foundry proof model
- [TOOLCALLER_V0_FROZEN_INVENTORY.md](TOOLCALLER_V0_FROZEN_INVENTORY.md) — F-1: the toolcaller-v0
  tool universe, verified against live code and frozen with a checked-in schema hash
- [`../training_pit/TOOLCALLER_CAPTURE_SCHEMA.md`](../training_pit/TOOLCALLER_CAPTURE_SCHEMA.md) —
  F-1: the dataset capture schema and mechanical admission gates for toolcaller-v0 examples

---

## Quality And Review

- [REVIEWER_QUALITY_GATE.md](REVIEWER_QUALITY_GATE.md) — the Reviewer as an authoritative role: where
  the gate sits in the run lifecycle and how Off/Advisory/Gated modes behave
- [REVIEWER_ADAPTER_GUIDE.md](REVIEWER_ADAPTER_GUIDE.md) — plan to train a local reviewer model
  (**parked** since 2026-06-13; see [reviewer-adapter/00-index.md](reviewer-adapter/00-index.md))

---

## Reliability And Support

- [TESTING_GUIDE.md](TESTING_GUIDE.md) — FlaUI UI tests, Training Pit script tests, and the headless
  unit-test suite (runnable without a display)
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — common failures in shell startup, models, swarm, HIVE, training
- [FAQ.md](FAQ.md) — quick answers about TheOrc, HIVE MIND, the Update Center, Pit Boss, and more

---

## Design Specs

Deeper design/implementation specs. Each carries its own status banner; [ROADMAP.md](ROADMAP.md) is
the authoritative ship-state.

- [INSTALLER_REVAMP_SPEC.md](INSTALLER_REVAMP_SPEC.md) — cross-platform installer; UI port + platform
  impls shipped 2026-06-21, release pipeline still Windows-only
- [MULTI_OS_RELEASE_SPEC.md](MULTI_OS_RELEASE_SPEC.md) — multi-OS release pipeline; macOS leg shipped
  2026-06-21, Linux leg + real-hardware verification not started
- [WARBAND_MODULE_SPEC.md](WARBAND_MODULE_SPEC.md) — daemon-first distributed-computing module (research draft)
- [WORKTREE_ISOLATION_DESIGN.md](WORKTREE_ISOLATION_DESIGN.md) — worktree-per-task file-ownership
  isolation (shipped v1.5)

---

## Sub-Areas

- [research/](research/) — design research inputs (uncensored chat, RLHF, SillyTavern patterns, model tiers)
- [reviewer-adapter/00-index.md](reviewer-adapter/00-index.md) — the parked reviewer-adapter investigation (phased 00–09)
- [sql-migration/README.md](sql-migration/README.md) — the SQLite metadata-layer migration design + roadmap

---

## Reference Paths

Source-level references rather than end-user guides:

- `training_pit/README.md` — the Training Pit subsystem index
- `training_pit/ARCHITECTURE.md`, `training_pit/PLAN_CAPTURE_SCHEMA.md`, `training_pit/DATASET_SCHEMA.md`

---

## Contributor Docs

These stay out of the in-app help list on purpose:

- [DOCUMENTATION_STANDARD.md](DOCUMENTATION_STANDARD.md)
- [SPONSOR_TEST_LAB.md](SPONSOR_TEST_LAB.md)

---

## Historical

Completed-task records, kept for history (scheduled to move to `_archive/` in a later batch):

- [WPF_RETIREMENT_CHECKLIST.md](_archive/WPF_RETIREMENT_CHECKLIST.md) — the WPF→Avalonia deletion checklist (✅ complete 2026-06-20)
