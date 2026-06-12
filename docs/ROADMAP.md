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

---

## Active Work

### Documentation

The docs are being normalized around the current implementation, especially:

- the architecture narrative
- glossary-backed terminology
- recent UI and Training Pit features

### Data Scale

The v1 Phase 3 minimum gate is already satisfied, but the longer-term dataset target still matters. The Training Pit panel and docs now treat roughly 1,000 train and 200 eval examples as the next quality target after minimum readiness.

### Distributed Design

HIVE MIND is still a spec, not a shipped feature. The current work is architectural preparation rather than implementation.

---

## Planned

### HIVE MIND

Planned in [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md):

- multi-node discovery
- remote Ollama host routing
- capability-aware job placement
- remote harvest and academy execution with artifact return

### Broader Platform Expansion

The repository still contains the design direction for a cross-platform layer, but the current production experience remains the Windows WPF application.

### Further Model And Training Work

Still-open follow-up opportunities visible from the current code shape include:

- broader automated probe coverage
- more reviewed data for future adapters
- additional operator tooling around remote or distributed training

---

## Reading Order

If you are new to the project, read in this order:

1. [ARCHITECTURE.md](ARCHITECTURE.md)
2. [GLOSSARY.md](GLOSSARY.md)
3. [USER_GUIDE.md](USER_GUIDE.md)
4. [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md)
5. [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md)
