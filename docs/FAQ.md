# TheOrc — FAQ

> This FAQ answers recurring operator questions briefly and points back to the deeper guides where needed. For the big picture, start with [ARCHITECTURE.md](ARCHITECTURE.md). For terms, see [GLOSSARY.md](GLOSSARY.md).

---

## What is TheOrc?

TheOrc is a Windows desktop AI coding shell that keeps inference, orchestration, model evidence, and training data workflows local. It supports both single-agent execution and a boss-plus-workers swarm.

---

## Does it send my code to a cloud service?

Not by default. The product is designed around local or operator-chosen hosts. The exact privacy boundary still depends on where you point the inference backend.

---

## What are goblins?

Goblins are the named swarm worker lanes: `RESEARCHER`, `CODER`, `UIDEVELOPER`, and `TESTER`. The boss orchestrator is separate.

---

## Is GOBLIN MIND marketing or runtime logic?

Runtime logic. It directly affects tool-call format instructions, schema simplification, and capability-aware swarm steering.

---

## Why do docs keep separating short tool-call support from long `write_file` support?

Because the codebase does too. A model can look fine on small structured calls and still fail on large JSON payloads used for real file generation.

---

## What is ORC ACADEMY?

ORC ACADEMY is the current operator-facing training GUI for launching and supervising `train_lora.py`. Some older code comments still use the previous WARCHIEF FORGE naming.

---

## Is the Training Pit already usable?

Yes. The current repository passes preflight and has verified reviewed counts of 900 train, 87 eval, and 25 negative examples.

---

## What is HIVE MIND?

HIVE MIND is a planned distributed TheOrc layer described in [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md). It is not a shipped runtime feature yet.
