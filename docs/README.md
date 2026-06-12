# TheOrc — Documentation Index

> Start with [ARCHITECTURE.md](ARCHITECTURE.md) if you want the system-level picture, then use the guides below for operator workflow and subsystem detail.

---

## Foundation

- [ARCHITECTURE.md](ARCHITECTURE.md) explains how the WPF shell, agent runtime, GOBLIN MIND, swarm lifecycle, Training Pit, and planned HIVE MIND layer fit together.
- [GLOSSARY.md](GLOSSARY.md) defines the project vocabulary once: TheOrc, goblins, ORC ACADEMY, GOBLIN HARVEST, lanes, captures, manifest, and related terms.
- [ROADMAP.md](ROADMAP.md) separates what is already shipped from what is still planned.

---

## Getting Started

- [QUICK_START.md](QUICK_START.md) is the shortest path from install to first successful run.
- [INSTALLATION.md](INSTALLATION.md) covers installer, portable, source build, and Training Pit prerequisites.
- [USER_GUIDE.md](USER_GUIDE.md) explains the shell, modes, approvals, help system, status bar, and where the major features live.

---

## Execution Modes

- [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md) explains the plan-then-execute loop and how approvals, tool dispatch, and verification work in one-agent mode.
- [SWARM_GUIDE.md](SWARM_GUIDE.md) explains the boss plus four-role swarm, capability-aware steering, badges, metrics history, and co-work follow-ups.

---

## Models And Hardware

- [MODEL_GUIDE.md](MODEL_GUIDE.md) explains built-in profile scores, long-`write_file` caveats, and where model suitability comes from.
- [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) covers the in-app model intelligence surface, trends strip, comparison view, capability tests, and export.
- [HARDWARE_GUIDE.md](HARDWARE_GUIDE.md) maps hardware tiers to practical TheOrc behavior for both inference and training.

---

## Training Pit

- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) covers the end-to-end dataset and adapter pipeline, including ORC ACADEMY.
- [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) focuses on capture review, manifest decisions, exports, and preflight safety.

---

## Reliability And Support

- [TESTING_GUIDE.md](TESTING_GUIDE.md) covers FlaUI UI coverage, Training Pit script tests, and the automation surfaces the suite depends on.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) covers common failures in shell startup, models, swarm execution, and training.
- [FAQ.md](FAQ.md) answers recurring operator questions without duplicating the longer guides.

---

## Reference Paths

These are source-level references rather than end-user guides:

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
