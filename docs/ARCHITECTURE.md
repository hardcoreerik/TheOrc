# TheOrc — Architecture

> Implementation-grounded system view for the current branch. Read this with
> [GLOSSARY.md](GLOSSARY.md) for naming, [ROADMAP.md](ROADMAP.md) for phased
> status, and [`../.grok/PROJECT_TRUTH.md`](../.grok/PROJECT_TRUTH.md) for the
> stricter "what is actually true right now" ledger.

---

## What This System Is

TheOrc is a local-first AI orchestration shell with three connected loops:

- an operator shell, **Avalonia-only** — WPF (`OrchestratorIDE/OrchestratorIDE.csproj`)
  was deleted 2026-06-20; there is no compatibility surface left
- an execution layer that runs single-agent, chat, swarm, and distributed HIVE
  workflows against local model runtimes
- a learning loop that captures swarm behavior, curates datasets, trains
  adapters, and feeds the results back into future planning

The codebase is opinionated about trust and inspectability. The UI is not a thin
skin over a CLI. `AgentLoop`, `ToolRegistry`, `SwarmSession`, the approval flow,
the reviewer gate, dataset capture, and the runtime abstractions all participate
in keeping local execution reviewable.

---

## System Map

```text
+------------------ OPERATOR SHELL -------------------+
| Avalonia app (only desktop shell — WPF deleted)     |
|  - Single / Swarm / Chat / Pit / Hive / Settings    |
|  - docs/help viewer                                 |
|  - status: workspace / git / build / model/runtime  |
+-------------------------+---------------------------+
                          |
          +---------------+----------------+
          |                                |
          v                                v
+-----------------------+      +------------------------+
| Single + Chat         |      | Swarm + HIVE           |
| AgentLoop             |      | SwarmSession           |
| ChatEngine            |      | HiveTaskQueue          |
| ToolRegistry          |      | HiveWorkerAgent        |
| Approval queue        |      | HiveScheduler          |
+-----------+-----------+      +-----------+------------+
            |                              |
            v                              v
     +------+------------------------------+------+
     | files, shell, web, tests, git, research    |
     +--------------------+-----------------------+
                          |
                          v
               +----------+-----------+
               | Training Pit         |
               | capture/review       |
               | ORC ACADEMY          |
               | adapter registry     |
               +----------+-----------+
                          |
                          v
               +----------+-----------+
               | Runtime layer        |
               | OllamaRuntime        |
               | LlamaCppServerRuntime|
               | LLamaSharpRuntime    |
               | ModelDepot / Session |
               | Adapter / Scheduler  |
               +----------------------+
```

---

## UI Shell

The operator shell transition is complete:

- **Avalonia is the only desktop shell.** WPF (`OrchestratorIDE/OrchestratorIDE.csproj`)
  was deleted 2026-06-20 along with all 70 WPF-only window/dialog/panel/control
  files. `ModelWikiWindow`/`ModelCompareWindow` were retired rather than ported
  — their data layer (`ModelWikiService`, `ModelWikiExporter`) is still shared
  code; the window itself is gone, not yet rebuilt.
- Shared core/runtime logic was already kept outside any one desktop shell
  (`Core/`, `Services/`, `Models/`, `Trust/` under `OrchestratorIDE/` are pulled
  into Avalonia via explicit `<Compile Include>`, not duplicated) — this is
  exactly what made deleting WPF tractable without rewriting that logic.

The status bar remains an architectural trust surface:

- workspace badge shows tool root
- branch badge shows git context
- build stamp proves which binary is running
- model/runtime label surfaces the active inference path

That build stamp exists specifically to reduce stale-exe confusion during local
development and test runs.

---

## Documentation Surface

The in-app help/docs viewer is a first-class feature, not a browser link-out.
Markdown files under `docs/` are part of the product surface, which is why stale
architecture docs are more than cosmetic drift: they actively mislead operator
and developer decisions.

Practical rule for this repo: `README.md`, `ROADMAP.md`, `PROJECT_TRUTH.md`, and
the architecture docs need to move together whenever a phase closes or the
primary execution path changes.

---

## Single-Agent And Chat Execution

Single-agent mode is driven by `AgentLoop`. Chat mode shares the same model
runtime abstraction through `ChatEngine`.

At a high level:

1. Plan mode builds a plan-only prompt and avoids tools.
2. Execute mode loads the active tool profile from `ToolRegistry`.
3. Tool calls are parsed, executed, approval-gated when needed, and fed back to
   the model until it stops or hits limits.
4. Verification and checkpointing can run around write operations.

`ToolRegistry` owns which tools exist and how they are approval-gated.
`AgentLoop` owns conversation state, prompt construction, tool-call parsing, and
retry/nudge behavior.

---

## GOBLIN MIND / Tool-Call Adaptation

The model-adaptation layer still exists under the GOBLIN MIND name in code and
docs, though a broader rename inventory already exists.

Implemented capability areas:

- format fingerprinting
- category boundary mapping
- schema library and adaptive schema generation
- schema simplification / reduction
- evolutionary fitness storage
- steering surfaces that feed capability data back into routing decisions

The important architectural point is unchanged: TheOrc does not assume one
universal tool-call format works for every local model. It probes and adapts.

---

## Swarm And HIVE Execution

`SwarmSession` is the core swarm orchestrator. It runs one boss plus up to four
 worker lanes: `RESEARCHER`, `CODER`, `UIDEVELOPER`, and `TESTER`.

Current swarm lifecycle:

```text
user goal
  -> boss decomposition
  -> optional dataset capture
  -> researcher pass
  -> coder/ui/tester execution
  -> tester verification / optional fix task
  -> boss merge
  -> staged outputs + run artifacts
```

Important current properties:

- dataset capture is live in the swarm path
- tester is a read-only lane
- run artifacts are written under `.orc/swarm/runs/<runId>/`
- reviewer verdicts are advisory, not hard-enforced
- swarm/runtime code now depends on `IModelRuntime` rather than hard-wiring
  inference calls to raw Ollama APIs

### HIVE MIND state

HIVE is no longer just a spec.

Shipped groundwork:

- named host store and reachability probing
- Tailscale-aware peer discovery
- distributed task queue and worker claiming
- capability-aware scheduler
- remote worker execution through `HiveWorkerAgent`

Still missing:

- Phase 3B multi-step tool calling on remote workers
- fuller runtime-native routing across remote nodes
- some recovery and polishing around the distributed path

---

## Training Pit / ORC ACADEMY

The Training Pit is live and no longer a future placeholder. It captures good
and bad boss plans, routes them through review, exports curated datasets, and
supports real LoRA training runs.

High-level pipeline:

```text
swarm run
  -> DatasetCapture
  -> review / prescreen / judge tooling
  -> curated JSONL lanes
  -> suitability / preflight checks
  -> ORC ACADEMY train_lora.py
  -> adapter eval + registry decision
```

Important current truth:

- production `lora_v1` is registered
- later datasets and adapters exist locally beyond the original v1 lane
- v2 was retired after suitability findings
- v3 completed and beat base, but did not beat the production v1 baseline
- training artifacts and dataset JSONL lanes are local-only and intentionally
  gitignored; the committed public contract is the registry plus the scripts/docs

See [../training_pit/ARCHITECTURE.md](../training_pit/ARCHITECTURE.md) for the
training-loop-specific view.

---

## Runtime Layer

This is the biggest recent architectural shift.

The app used to depend directly on Ollama-oriented call sites. The current branch
has a real runtime abstraction:

- `IModelRuntime` is the common generation surface
- `OllamaRuntime` wraps the current default backend
- `LlamaCppServerRuntime` wraps the existing llama.cpp server bridge
- `LLamaSharpRuntime` is the in-process native runtime prototype

Phase 3 runtime orchestration pieces now exist:

- `ModelDepot` scans local model/adaptor assets
- `SessionManager` owns persistent base-model load logic
- `AdapterManager` owns per-role persistent LoRA-backed executors
- `RuntimeOrchestrator` wires the managers together from one shared runtime
- `IRoleRuntime` / `NativeRoleRuntime` expose that stack as a role-aware
  streaming surface for opt-in call sites
- `OrcScheduler` has started with a real VRAM-budget admission check

Important caveats:

- Native Runtime is **not** the default path yet
- the first live path is limited to experimental HIVE worker opt-in; main
  chat, research chat, and SwarmSession still stay on the configured default
  runtime for this release
- Session/Adapter telemetry is only partially surfaced
- prefix KV cache is research, not a promised feature
- cross-role shared KV cache is specifically unsafe with different adapters

That last point matters because architecture docs should not imply "one giant
shared cached brain" when the actual branch truth is more constrained.

---

## Persistence And Truth Sources

The repo has several classes of truth now:

- code truth: what the app actually does
- operator truth: `README.md`, `docs/ROADMAP.md`, and the help guides
- engineering truth: `.grok/PROJECT_TRUTH.md` and runtime-phase docs
- local artifact truth: dataset JSONL, checkpoints, adapters, eval outputs

A recurring docs failure mode here is letting architecture docs describe either:

- an older shipped state after the code has moved on, or
- a desired future shape as if it already exists

For this branch, `PROJECT_TRUTH.md` is the safest anchor when docs disagree.

---

## Architectural Through-Line

TheOrc is best understood as a closed local loop:

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

Native Runtime extends that loop instead of replacing it. The point is not to
become an inference engine from scratch; it is to own more of the scheduling,
session, adapter, and feedback path locally while keeping the operator in
control.

---

*Last updated: 2026-06-19 — architecture refreshed for Avalonia-primary UI,
shipped HIVE/Training Pit state, and Native Runtime branch work.*
