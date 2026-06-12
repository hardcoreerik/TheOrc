# TheOrc — Architecture

> This is the implementation-grounded architecture paper for the current Windows build of TheOrc. Read it with [GLOSSARY.md](GLOSSARY.md) for naming and [USER_GUIDE.md](USER_GUIDE.md) for operator workflow.

---

## What This System Is

TheOrc is a local-first orchestration shell around three tightly connected loops:

- a WPF desktop shell that hosts the operator experience
- an execution layer that turns user goals into single-agent or swarm tool runs
- a learning loop that turns successful and failed swarm plans into reviewed training data and, eventually, LoRA adapters

The codebase is opinionated about trust boundaries. The UI is not a thin skin over a CLI. `AgentLoop`, `ToolRegistry`, the approval queue, the sandbox rules, and `SwarmSession` all participate directly in keeping local execution inspectable and reviewable.

---

## System Map

```text
+-------------------- WPF SHELL --------------------+
| MainWindow                                        |
|  - mode switch: Single / Swarm / Chat             |
|  - status bar: workspace, branch, build, model    |
|  - activity bar + sidebar panels                  |
|  - embedded docs/help viewer (F1)                 |
+-------------------------+-------------------------+
                          |
          +---------------+----------------+
          |                                |
          v                                v
+-----------------------+      +------------------------+
| Single-Agent Runtime  |      | Swarm Runtime          |
| AgentLoop             |      | SwarmSession           |
| ToolRegistry          |      | SwarmSteering          |
| ApprovalQueue         |      | SwarmRunMetrics        |
+-----------+-----------+      +-----------+------------+
            |                              |
            v                              v
     +------+------------------------------+------+
     | Tools, files, shell, web, tests, research  |
     +--------------------+-----------------------+
                          |
                          v
               +----------+-----------+
               | Training Pit         |
               | DatasetCapture       |
               | review/prescreen     |
               | judge/preflight      |
               | train_lora.py        |
               +----------------------+
```

---

## WPF Shell And Panel Architecture

`MainWindow` is the composition root for the operator UI. It instantiates the major panels in code-behind and swaps them into the main content and sidebar presenters instead of relying on a large MVVM shell registry.

The main shell pieces verified in `MainWindow.xaml` and `MainWindow.xaml.cs` are:

- mode selector buttons for `Single`, `Swarm`, and `Chat`
- a sidebar presenter used for the file explorer, sessions, checkpoints, and settings
- panel instances for `AgentPanel`, `SwarmBoardPanel`, `ChatPanel`, `TrainingPitPanel`, `CodeEditorPanel`, `ToolEditorPanel`, `SettingsPanel`, `SessionBrowserPanel`, and `CheckpointBrowserPanel`
- a status bar that surfaces workspace, git branch, build stamp, active model, and current status
- help entry points from both the Help menu and the `F1` shortcut

The status bar matters architecturally because it is the fastest trust surface in the app:

- workspace badge shows where file tools are rooted
- branch badge reflects git context
- build stamp proves exactly which binary is running
- model label reflects the active model or role selection

That build stamp is sourced from `AssemblyInformationalVersion` and is shown specifically to prevent "stale exe" confusion.

---

## Documentation And Research Surfaces

The in-app help system is a first-class shell feature, not a browser link-out.

- `HelpWindow` loads `docs/*.md` from disk when present and falls back to embedded resources in published builds
- relative guide links such as `[User Guide](USER_GUIDE.md)` are rewritten to an internal `orcdoc://` scheme so navigation stays inside the viewer
- `MarkdownFlowDocument` renders headings, lists, fenced code blocks, blockquotes, emphasis, inline code, links, and horizontal rules

This explains why the docs must avoid raw HTML-heavy layouts and why simple Markdown cross-links are the stable documentation contract.

---

## Single-Agent Execution Path

Single-agent mode is driven by `AgentLoop`.

At a high level:

1. `PlanAsync` builds a plan-only prompt, injects workspace rules, and asks the model for a plan without tools.
2. The approved plan is stored in session history.
3. `ExecuteAsync` loads the active tool set from `ToolRegistry`, adds project rules, creates a git checkpoint, and enters a tool-calling loop.
4. Tool calls are executed through `ToolRegistry`, which routes approval-gated calls through the approval queue.
5. Tool results are fed back into the model until the model stops issuing calls or hits its step limit.

The tool runtime is deliberately defensive:

- unknown tools return rich self-correction messages instead of generic failures
- write operations and shell commands can require explicit approval
- `AgentLoop` can detect refusal-style answers and push the model back toward real tool use
- automatic post-write verification can run `run_tests`

---

## AgentLoop And ToolRegistry

`AgentLoop` and `ToolRegistry` split responsibility cleanly:

- `AgentLoop` owns prompt construction, conversation state, context pressure, tool-call parsing, and retry/nudge behavior
- `ToolRegistry` owns which tools exist, which profiles can see them, unknown-tool recovery, and approval-aware execution

That separation is important because GOBLIN MIND augments `AgentLoop` without requiring the tool handlers themselves to understand model quirks.

---

## GOBLIN MIND

GOBLIN MIND is the model-adaptation layer under `Services/ToolCalls/`.

Its implemented phases are:

- Phase 1: format fingerprinting via `FormatProbeEngine`
- Phase 2: category boundary mapping via `CategoryProbeEngine`
- Phase 3: adaptive schema generation via `SchemaGenerator`
- Phase 4: schema reduction middleware via `SchemaSimplifier`
- Phase 5: evolutionary schema fitness tracking via `FitnessMap`
- Phase 6: capability-aware swarm steering via `SwarmSteering`

The key idea is simple: local models do not all fail in the same way, so TheOrc profiles them and changes how it talks to them.

### Format fingerprinting

`FormatProbeEngine` tests five output conventions:

- bare JSON
- OpenAI-style JSON
- Hermes XML wrapper
- Python-style function syntax
- YAML block syntax

`AgentLoop` then injects the preferred format instructions for that model instead of hardcoding one universal tool-call format.

### Category boundary mapping

`CategoryProbeEngine` scores whether a model can reliably handle seven categories:

- `FileOps`
- `Network`
- `CodeExec`
- `DataTransform`
- `SystemInspect`
- `StructuredOutput`
- `TaskPlanning`

Those scores are not informational only. `SwarmSteering` uses them to decide whether a requested goblin should keep its assigned model or fall back.

### Schema reduction

`SchemaGenerator` first looks for confirmed schemas in `SchemaLibrary`. If none exist, it builds them dynamically. `SchemaSimplifier` can then reduce complexity by flattening nested objects, dropping optional fields, shortening descriptions, and similar transformations.

This means schema complexity is treated as a runtime compatibility variable, not as a fixed tool definition.

### Evolutionary search

`FitnessMap` stores tested schema variants and tracks winners. The GUI surface for this is the Evolution tab in `ToolCallTestWindow`, and winning variants can be promoted into `SchemaLibrary`.

### Steering integration

`SwarmSteering` converts capability maps into routing decisions and warning text. The boss prompt also receives a compact "Goblin Capability Map" summary so decomposition is informed by model capability, not just role labels.

---

## Swarm Session Lifecycle

`SwarmSession` is the orchestration engine for swarm mode. The current implementation supports one boss and up to four worker lanes: `RESEARCHER`, `CODER`, `UIDEVELOPER`, and `TESTER`.

The lifecycle is:

```text
user goal
  -> boss decomposition
  -> optional dataset capture
  -> researcher phase
  -> implementation phase (coder + uideveloper + planned tester)
  -> auto tester verification phase
  -> optional fix task
  -> boss merge
  -> staged outputs + run artifacts
```

More concretely:

1. The boss model decomposes the goal into JSON tasks.
2. The boss plan is parsed into `SwarmTask` records.
3. `DatasetCapture.StageAsync` may write a plan capture if the rubric score is high enough or low enough.
4. Researcher tasks run first.
5. If the researcher model differs from the coder model, the researcher model can be evicted from VRAM before the coder phase.
6. Coder, UI, and planned tester tasks run concurrently.
7. Empty or ghost researcher output is filtered before it contaminates coder prompts.
8. Completed implementation tasks can be retried when they produce no files.
9. Tester results can spawn a targeted fix task.
10. The boss merges worker results into the final report and emits a staged-files event.

The swarm keeps durable run artifacts under `.orc/swarm/runs/<runId>/`, including plan JSON, trace data, task files, and staged output.

---

## Co-Work, Steering, And Continuations

Swarm mode is not fire-and-forget only.

`SwarmSession` supports:

- worker pauses for user input via `ask_user`
- user steering messages queued into the next worker iteration
- follow-up continuation on a finished worker thread using preserved conversation history

This is why the swarm board is built around live per-lane streams and task-state transitions instead of only a final report.

---

## Metrics, Capability Badges, And Model Intelligence

Three subsystems turn swarm runs into operational feedback:

- `SwarmMetricsStore` appends a JSONL record for each run and aggregates per-configuration quality
- `SwarmConfigAdvisor` detects hardware with `nvidia-smi` and recommends boss/coder/researcher/tester assignments
- `ToolCallProfileStore` provides the data behind capability badges

User-visible surfaces backed by those services include:

- Swarm Board capability badges: dispatch mode, format, categories, schema reduction, probe age
- Swarm Board metrics history: best-known configurations and quality score
- Model Wiki / Lab trends strip and local result history
- model comparison window

---

## Training Pit Pipeline

The Training Pit is the data and adapter loop that sits downstream of swarm planning.

The code path today is:

```text
swarm run
  -> DatasetCapture stages qualifying boss plans
  -> human/judge review tooling triages captures
  -> reviewed_v1.json manifest records decisions
  -> export builds train/eval/negative JSONL files
  -> phase3_preflight verifies readiness
  -> ORC ACADEMY launches train_lora.py
  -> adapter + checkpoints + summary land in outputs/lora_v1
```

### 1. Capture

`DatasetCapture.cs` scores the parsed boss plan with `EvalRubric`.

- score `>= 70`: stage as `plan_capture_good_*`
- score `<= 39`: stage as `plan_capture_bad_*`
- score `40-69`: skip silently

The staged file is a plan-capture JSON document, not a trainable chat row yet.

### 2. Prescreen

`prescreen_captures.py` is the deterministic first pass used by GOBLIN HARVEST. It auto-flags mechanical problems such as:

- too few tasks
- invalid role strings
- TESTER tasks that use write verbs
- wrong-stack file extensions
- obvious greenfield/fabricated file references

### 3. Judge

`judge_captures.py` is the second pass. It uses a local judge model to assign `low`, `medium`, or `high` fabrication risk, but it does not approve or reject captures by itself.

### 4. Human review and manifest

`review_captures.py` is the approval valve.

- approvals and rejections are written to `training_pit/datasets/manifests/reviewed_v1.json`
- exports are rebuilt from the manifest, not from raw staging files
- export is fail-closed: convert -> validate -> sanitize -> replace final file only on success

As of the current manifest and preflight output, the reviewed dataset stands at:

- 900 approved train examples
- 87 approved eval examples
- 25 approved negative examples

### 5. Preflight

`phase3_preflight.py` verifies readiness before training. Its checks include:

- manifest validity
- split counts
- export consistency
- validation and sanitization
- duplicate detection
- eval isolation
- staging safety

### 6. ORC ACADEMY training

The current trainer is `training_pit/scripts/train_lora.py`.

Verified properties from the script and GUI:

- target workflow is QLoRA on `google/gemma-4-12b-it` by default
- training and eval data come from `train_v1.jsonl` and `eval_v1.jsonl`
- progress heartbeat is written to `training_pit/outputs/lora_v1/progress.json`
- resume uses the latest checkpoint when available
- a VRAM cap can be passed from the Training Pit GUI
- the GUI can re-attach to a surviving trainer process after an app restart
- final artifacts include adapter files, checkpoints, `training_summary.json`, and `forge.log`

The UI label is now ORC ACADEMY, while some code comments and backing field names still use the older WARCHIEF FORGE naming.

---

## NIGHT HARVEST And Marker Watching

The harvest loop is intentionally separate from training:

- `night_harvest.ps1` farms plans
- `TrainingPitPanel` watches staging activity and surfaces live collection state
- `harvest_marker_watch.ps1` can stop harvest automatically when the train-data marker is reached

That keeps capture farming, review, and training decoupled enough that operators can collect aggressively without automatically promoting raw data into training.

---

## HIVE MIND, Summarized

[HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) describes a planned distributed layer. The important architectural idea is not "remote shelling into another PC." It is a capability-aware roster of TheOrc nodes that can host the right workload on the right machine.

The planned phases in the spec are:

- remote Ollama host routing as the first useful slice
- discovery and roster management
- remote jobs and artifact return for harvest and academy workflows
- trust and confirmation for first contact

The current codebase does not implement HIVE MIND yet, but the spec is consistent with existing local primitives:

- capability profiles
- heartbeat files
- model and VRAM awareness
- artifact directories and resumable jobs

---

## Architectural Through-Line

The best way to understand TheOrc is as a closed local loop:

```text
goal
  -> plan
  -> verified execution
  -> scored swarm trace
  -> reviewed capture
  -> curated dataset
  -> adapter training
  -> better future planning
```

That loop is why the shell, the swarm, GOBLIN MIND, and the Training Pit belong in one product. TheOrc is not only an agent runner. It is an agent runner that records evidence about how its own models behave and then feeds that evidence back into the next generation of behavior.
