# TheOrc — Architecture

> Implementation-grounded system view for the current branch. Read this with
> [GLOSSARY.md](GLOSSARY.md) for naming, [ROADMAP.md](ROADMAP.md) for phased
> status, [The Orc Context Fabric.md](The%20Orc%20Context%20Fabric.md) for the
> proposed large-corpus memory architecture, and
> [`../.grok/PROJECT_TRUTH.md`](../.grok/PROJECT_TRUTH.md) for the stricter
> "what is actually true right now" ledger.

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
+-------------------- OPERATOR SHELL ---------------------+
| Avalonia: Single / Swarm / Chat / Pit / Hive / Settings |
+---------------------------+-----------------------------+
                            |
          +-----------------+------------------+
          |                                    |
          v                                    v
+-------------------------+        +-------------------------+
| Single + OrcChat        |        | Swarm + HIVE            |
| AgentLoop / ChatEngine  |        | SwarmSession             |
| ToolRegistry / approval |        | TaskQueue / WorkerAgent  |
+------------+------------+        | Campaign engine          |
             |                     +------------+------------+
             +------------------+---------------+
                                |
                +---------------+----------------+
                | Knowledge and tool layer       |
                | CodeGraph (shipped)             |
                | Context Fabric (proposed)       |
                | files / web / shell / artifacts|
                +---------------+----------------+
                                |
          +---------------------+---------------------+
          |                                           |
          v                                           v
+-------------------------+               +-------------------------+
| Runtime layer           |               | Persistence             |
| IModelRuntime           |               | .orc/theorc.db (WAL)    |
| NativeRoleRuntime       |               | content-addressed stores|
| Model/Session/Adapter   |               | run/campaign artifacts  |
+------------+------------+               +-------------------------+
             |
             v
+-------------------------+
| Training Pit / Academy  |
| capture / review / LoRA |
+-------------------------+
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

The next layer now exists in scaffold form for the native runtime as well:

- `ModelAdmissionGate` fingerprints local GGUF names into model families
- native workloads are classified (`OrcChat`, `ToolCalling`,
  `StrictStructuredOutput`, `ContextFabricReader`, `ContextFabricReviewer`,
  `AgenticCoding`, `VisionReasoning`)
- each model/workload pair is labeled `Admitted`, `Provisional`, or `Rejected`

This is intentionally separate from tool-call probing. A model can be broadly
chat-capable and still be rejected for strict JSON or evidence-grade work.

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
- native campaign/work-unit contracts and atomic capability-aware leases
- campaign persistence and lifecycle controls
- resumable SHA-256 content-addressed model/artifact transfer
- shared `HeadlessAgentLoop` execution through `NativeRoleRuntime`
- independent verification runs, retries, and stale-result rejection

Still missing:

- dependency-aware campaign stages and fan-in barriers
- input-artifact materialization for native-agent jobs
- complete campaign-control UX and hostile-input hardening
- the planned multi-node hardware acceptance and scaling report

Phase 3B native campaign jobs fail closed when native admission or execution
fails. They do not use an Ollama fallback. Legacy non-campaign execution remains
a separate compatibility path.

---

## Knowledge And Context Layer

### CodeGraph

CodeGraph is a shipped, code-specialized knowledge index:

- `RoslynIndexer` creates symbol nodes and structural edges
- `ComplexityAnalyzer` adds code metrics
- `GraphRepository` persists nodes, edges, FTS5 search data, and ADRs
- `CodeGraphService` manages background indexing when a workspace opens
- `GraphTools` exposes search, path tracing, architecture, change impact, and ADR operations
- `AgentLoop` prefers graph tools over repeated grep/read exploration for structural questions

CodeGraph stores its relational graph in the shared `.orc/theorc.db`. Its node
contract is intentionally specific to code: qualified names, source files,
line ranges, labels, degree, and complexity metrics.

### The Orc Context Fabric (proposed)

Context Fabric extends the same architectural pattern to books, manuals, and
large document collections without pretending that all source tokens fit in
the live prompt.

Its core loop is:

```text
immutable source
  -> deterministic parse and overlapping segments
  -> native evidence-card readers
  -> boundary stitching
  -> section/chapter/document/corpus reduction
  -> document graph + FTS + optional embeddings
  -> query planning and source rehydration
  -> token-bounded answer synthesis
  -> independent citation verification
```

Context Fabric shares `SqliteStore`, migrations, `RepositoryBase`, WAL,
transaction patterns, FTS5 conventions, lifecycle events, native runtime, and
HIVE campaigns with the current architecture. It uses a separate
`DocumentGraphRepository`; document claims, entities, editions, page ranges,
coverage, and citations do not belong in CodeGraph's `graph_nodes` table.

The two graphs may later connect through typed external links. For example, a
manual requirement may link to the CodeGraph method that implements it, while
both repositories retain their own invariants and rebuild rules.

Context Fabric treats the context window as working memory over a persistent
address space. Summaries are navigation caches, not source truth. When evidence
is missing or disputed, a query triggers a "cognitive page fault" and reloads
the exact source segment. Quick, Study, and Exhaustive modes trade latency for
coverage. Exhaustive mode maps the question across all selected leaves, reports
code-computed coverage, and should be treated as a premium verification path
rather than the default interactive chat mode for large corpora.

The first Context Fabric bench now also depends on native model admission. Orc
should reject obviously unfit models before spending time on a benchmark run,
rather than discovering only after execution that a tiny chat model cannot
produce valid evidence cards.

The critique-triage pass also locks in a host-trusted citation boundary:
models produce draft quotes, while the host computes canonical offsets and
digests, rejects ambiguous anchors, and records the benchmark environment with
the resolved model-admission verdicts. The real native CF-0 lane now passes on
the pinned Hermes 3 Llama 3.1 8B model and the verified Gemma 4 12B native
fallback path: both cleared 16/16 accepted segments, 5/5 verified questions,
100% citation precision, all nine frozen gates, and roughly an 11.5x
source-to-working-context ratio inside the 8K limit. Prompt-path telemetry
confirmed the embedded template on Hermes and `GemmaNativeFallback` on Gemma 4;
exhaustive enumeration is intentionally a host-deterministic aggregation of
grounded per-segment claims. Planned follow-up benchmarks should measure
hierarchy recall loss, embedding impact, graph noise, and SQLite traversal cost
as CF-1 and CF-2 mature.

The full schema, HIVE execution model, benchmark, security policy, and phased
implementation are specified in
[The Orc Context Fabric.md](The%20Orc%20Context%20Fabric.md). CF-0 now has a
native feasibility harness, deterministic corpus, strict host-side verifier,
and report generator, and its real-model quality gate has passed. CF-1 is now
underway: migrations v8-v9 plus deterministic text/Markdown parsing, structural
segmentation, content-addressed artifacts, transactional document replacement,
and segment FTS are implemented. PDF parsing, the Darwin acceptance fixture,
artifact garbage collection, the document graph, HIVE execution, and the
OrcChat product surface remain proposed rather than shipped.

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
- native main chat and native HIVE workers are explicit opt-ins; other paths
  may still use the configured default runtime
- Phase 3B `native_agent` jobs require the native role runtime and fail closed
  rather than silently substituting Ollama
- Session/Adapter telemetry is only partially surfaced
- prefix KV cache is research, not a promised feature
- cross-role shared KV cache is specifically unsafe with different adapters

That last point matters because architecture docs should not imply "one giant
shared cached brain" when the actual branch truth is more constrained.

---

## Persistence And Truth Sources

`SqliteStore` owns one operational metadata database at
`<workspace>/.orc/theorc.db`:

- WAL lets readers continue while the owner process writes
- each repository operation uses its own pooled connection
- foreign keys and a busy timeout are applied per connection
- `MigrationRunner` applies ordered migrations transactionally
- `RepositoryBase` centralizes parameterized commands and transaction helpers
- remote HIVE workers never open the database directly

Current persisted domains include captures, plans/runs, dataset indexes, HIVE
tasks/events, CodeGraph nodes/edges/FTS/ADRs, and Phase 3B
campaign/work-unit/artifact metadata.

Large model and campaign objects use `ContentAddressedStore`: SHA-256 names,
bounded one-megabyte chunks, resumable partial files, object/store quotas,
atomic completion, and final digest verification. Context Fabric will reuse
that split: SQLite for searchable metadata, graph structure, provenance, and
metrics; content-addressed storage for original documents, normalized source,
evidence payloads, images, and reports.

The Warchief/local app remains the single database writer. Completed HIVE
artifacts are verified first, then imported through repositories in bounded
transactions. This prevents remote-node SQLite corruption and write stampedes.

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

*Last updated: 2026-06-27 — architecture refreshed for Phase 3B native campaign
groundwork, CodeGraph lifecycle, and the proposed Orc Context Fabric.*
