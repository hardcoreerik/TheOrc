# TheOrc — Documentation Index

New to TheOrc? Start with [QUICK_START.md](QUICK_START.md) for your first run, then [USER_GUIDE.md](USER_GUIDE.md) to understand the app. The guides below cover every feature in depth.

---

## Foundation

- [ARCHITECTURE.md](ARCHITECTURE.md) — how the WPF shell, agent runtime, GOBLIN MIND, swarm lifecycle, Training Pit, and HIVE MIND layer fit together (technical reference)
- [GLOSSARY.md](GLOSSARY.md) — definitions for all TheOrc terms: goblins, ORC ACADEMY, HIVE MIND, Warchief, Pit Boss, GOBLIN HARVEST, and more
- [ROADMAP.md](ROADMAP.md) — what is already shipped vs. what is still planned

---

## Getting Started

- [QUICK_START.md](QUICK_START.md) — the fastest path from install to first successful run (about 10 minutes)
- [INSTALLATION.md](INSTALLATION.md) — installer options, portable build, source build, and Training Pit prerequisites
- [USER_GUIDE.md](USER_GUIDE.md) — the full guide to modes (Single, Swarm, Chat, Pit, Hive, Update), the status bar, approvals, shortcuts, and Help

---

## Execution Modes

- [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md) — the plan-then-execute loop, approvals, tool dispatch, and verification in one-agent mode
- [SWARM_GUIDE.md](SWARM_GUIDE.md) — the boss plus four worker lanes, capability-aware routing, the Swarm Board, metrics history, and how to interact with workers mid-run

---

## HIVE MIND

- [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) — the HIVE MIND user guide: setting up multi-PC networking, the constellation panel, the Warchief, security, and fleet deploy (shipped v1.6)

---

## Models And Hardware

- [MODEL_GUIDE.md](MODEL_GUIDE.md) — built-in profile scores, write_file caveats, and where model suitability data comes from
- [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) — the in-app model intelligence surface: trends strip, comparison view, capability tests, and export
- [HARDWARE_GUIDE.md](HARDWARE_GUIDE.md) — hardware tiers and what they mean for inference and training

---

## Training Pit

- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) — the full capture-to-adapter pipeline, including Pit Boss (the setup wizard), ORC ACADEMY, NIGHT HARVEST, and the v1 adapter
- [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) — capture review, manifest decisions, exports, and the preflight safety gate

---

## Reliability And Support

- [TESTING_GUIDE.md](TESTING_GUIDE.md) — FlaUI UI tests, Training Pit script tests, and the 112 headless unit tests (runnable without a display)
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — common failures in shell startup, models, swarm runs, HIVE MIND, and training
- [FAQ.md](FAQ.md) — quick answers to common questions about TheOrc, HIVE MIND, the Update Center, Pit Boss, and more

---

## Reference Paths

Source-level references rather than end-user guides:

- `training_pit/ARCHITECTURE.md`
- `training_pit/PLAN_CAPTURE_SCHEMA.md`
- `training_pit/DATASET_SCHEMA.md`
- `training_pit/HARDWARE_GUIDE.md`
- `training_pit/README.md`

---

## Contributor Docs

These stay out of the in-app help list on purpose:

- [DOCUMENTATION_STANDARD.md](DOCUMENTATION_STANDARD.md)
- [SPONSOR_TEST_LAB.md](SPONSOR_TEST_LAB.md)
