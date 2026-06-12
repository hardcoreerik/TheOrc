# TheOrc — Glossary

> This glossary defines project codenames once so the rest of the docs can link here instead of re-explaining them. For architecture, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Product Names

### TheOrc

The Windows desktop application in this repository. In swarm mode, "TheOrc" also refers to the boss/orchestrator model that decomposes goals and merges worker output.

### goblins

The specialized worker lanes used by swarm mode. They are real execution roles with different prompts and tool access, not just UI labels.

### boss

The orchestration model in swarm mode. The boss plans, routes, steers, and merges. It is not counted as one of the four worker roles.

### Warchief

An older naming pattern still visible in some comments and backing field names. The operator-facing training name is now ORC ACADEMY.

---

## Swarm Roles

### RESEARCHER

The read-and-investigate lane. It is meant for file reading, search, web fetches, and summarization. It does not write production files.

### CODER

The implementation lane for code and file creation. It can write files and run shell commands.

### UIDEVELOPER

The implementation lane for XAML, layout, styling, and UI-focused work. It has the same broad tool access as CODER but a UI-oriented prompt.

### TESTER

The verification lane. It runs tests, inspects output, and reports verdicts. It is intentionally a no-`write_file` lane.

### lane

One execution column or role track in a swarm run. The Swarm Board shows one lane per role plus the boss stream.

---

## GOBLIN MIND Terms

### GOBLIN MIND

The runtime model-profiling system under `Services/ToolCalls/`. It fingerprints tool-call format, maps category boundaries, simplifies schemas for weaker models, stores successful schemas, and feeds capability data back into steering.

### format fingerprinting

Phase 1 of GOBLIN MIND. The process of discovering which tool-call syntax a model actually produces reliably.

### category boundary mapping

Phase 2 of GOBLIN MIND. The process of scoring whether a model can reliably handle task classes such as file ops, code execution, network access, structured output, and planning.

### schema reduction

Phase 4 of GOBLIN MIND. The middleware step that simplifies tool schemas for models that fail on complex parameter structures.

### Evolution tab

The GUI surface in `ToolCallTestWindow` that shows saved schema-fitness records from evolutionary search and can promote winning variants into the schema library.

### capability badges

The Swarm Board status line under each model picker showing dispatch mode, preferred format, category summary, schema reduction state, and probe age.

---

## Training Pit Terms

### Training Pit

The full data and adapter pipeline for turning swarm behavior into reviewed datasets and LoRA adapters.

### capture

A saved boss-planning example staged by `DatasetCapture.cs` as a plan-capture JSON file.

### manifest

The git-tracked decision ledger at `training_pit/datasets/manifests/reviewed_v1.json`. It records which captures were approved or rejected and how approved captures were split.

### review valve

The approval step enforced by `review_captures.py`. Nothing is exported into training JSONL unless it is represented as an approved manifest entry.

### GOBLIN HARVEST

The first-pass automation around staged captures. In code and scripts, this refers to deterministic prescreening and judge-assisted triage of harvested plans before human review.

### NIGHT HARVEST

The unattended overnight plan-farming loop. It gathers more staged captures for later review.

### harvest marker watcher

`Tools/harvest_marker_watch.ps1`. A detached monitor that stops NIGHT HARVEST once the train-data marker is reached and writes a morning note.

### ORC ACADEMY

The current operator-facing name for the in-app training workflow and GUI. It launches `training_pit/scripts/train_lora.py`, tracks heartbeat/progress, supports VRAM caps, and can re-attach to a surviving trainer process.

### WARCHIEF FORGE

The older internal name still visible in Training Pit panel variable names such as `ForgeStatus`, `ForgeBar`, and related comments.

### preflight

The readiness gate implemented by `phase3_preflight.py`. It validates counts, export consistency, sanitizer results, duplicate safety, eval isolation, and staging safety before training is considered ready.

### adapter

The LoRA or QLoRA output produced by training. In the current pipeline it lands under `training_pit/outputs/lora_v1/adapter`.

---

## Model And Ops Terms

### Model Wiki / Lab

The non-modal model intelligence window that merges catalog scores, installed state, GOBLIN MIND probe data, swarm history, built-in observations, and local capability test results.

### trends strip

The chronological visual strip in Model Wiki detail that summarizes local capability tests and swarm outcomes over time.

### model comparison view

The side-by-side `ModelCompareWindow` for comparing two wiki entries across identity, role scores, probe data, and routing recommendations.

### token cost estimator

The context-aware estimate shown in the main window for the next request. It combines used context, pending input, and an assumed response budget into a rough token and time estimate.

### build stamp

The version-and-commit indicator shown in the status bar. It is sourced from assembly informational version metadata.

### codex-review

`Tools/codex-review.ps1`. A scripted wrapper for Codex CLI reviews of staged changes or commit ranges that closes stdin correctly, applies a timeout, and saves findings under `.orc/reviews/`.

---

## Planned Distributed Terms

### HIVE MIND

The planned distributed TheOrc layer described in [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md). The idea is to treat multiple TheOrc machines as a capability-aware roster for inference, harvest, judging, and training jobs.

### Scout lane

A HIVE MIND concept from the spec for lighter-weight nodes that cannot host the full current academy target but can still contribute smaller-model or support workloads.
