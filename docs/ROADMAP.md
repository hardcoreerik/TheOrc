# TheOrc — Roadmap

> Last updated: 2026-06-20 (WPF deleted — Avalonia-only + experimental native HIVE worker opt-in).
> This document is updated after every GitHub release. It reflects actual code state, not aspirations — features marked Shipped have been verified in the running app.

---

## Where we are

TheOrc is a production local AI orchestrator. The core swarm, model intelligence, distributed HIVE MIND, and self-training loop are all shipped and running. The v1 adapter scores **99.3%** on structured planning evals and remains the production adapter. The **v2 adapter regressed** — a post-hoc suitability audit found 51.3% of its 1,784 examples assigned write tasks to TESTER-lane roles, dropping the structured-plan pass rate to 77.8% (perfect plans 71% → 54%); v2 was retired and its data repurposed. **ORC ACADEMY v3** completed on a clean 906 train / 87 eval set and beat base in A/B (94.7% vs 85.3%), but did **not** beat the v1 99.3% production baseline because of the `files_named` gap. v1 stays production; v3 is not registered.

v1.8.0 ships the Avalonia MarkdownView (Phase 6), the full FlaUI + Avalonia test suite (Phase 7, 23 tests), and the Grok toolchain integration. CodeGraph v1 — a Roslyn + SQLite code knowledge graph that lets the agent query graph structure instead of grepping files — is fully implemented and committed, targeting v1.9.

**HIVE MIND node startup was broken on every normal-user install until 2026-06-20** — `HiveNodeServer.Start()` silently aborted (no error, no log line, nothing listening) on any non-elevated machine, because a failed wildcard `HttpListener` bind left the listener disposed internally and the fallback cleanup code's own property access threw a second, masking exception inside an unobserved `Task.Run`. Found via a pre-release smoke test specifically because nothing in automated test coverage exercises real socket binding. Fixed — verified `localhost:7078/hive/info` returns 200 and UDP 7077 beacon listens. This was a hard release blocker for any LAN/Tailscale HIVE MIND testing; not caught by `dotnet test` since the unit/headless suites mock or don't reach real listener startup.

The honest gaps: the Reviewer Quality Gate is advisory-only (can always be overridden), the Tool Editor hot-reload is a stub, and HIVE MIND multi-step tool calling on remote workers is Phase 3B (not yet built). **WPF is deleted (2026-06-20)** — `OrchestratorIDE/OrchestratorIDE.csproj` and every WPF-only file are gone from the repo; Avalonia is the only desktop shell. `ask_user`, `ModelCapabilityTestDialog`, and `ToolCallTestWindow` all have real Avalonia resolutions (the latter two retired as diagnostics, not ported); `ModelWikiWindow`/`ModelCompareWindow` were retired rather than ported (data layer kept, window itself dropped — a future from-scratch Avalonia rebuild is a real feature request, not a blocker). The UIA automation lane already targeted Avalonia exclusively. Native Runtime groundwork is now real in code (IModelRuntime, Ollama wrapper, llama.cpp server wrapper, LLamaSharp runtime, shared text tool-call parser, Chat/Swarm/HIVE worker/reviewer migration, ModelDepot, SessionManager, AdapterManager with per-role persistent LoRA contexts, RuntimeOrchestrator wiring all three together, `IRoleRuntime`/`NativeRoleRuntime`, Settings-panel telemetry/smoke surfaces with explicit Ollama fallback and evidence capture, and a first OrcScheduler VRAM-budget admission check). It is still **not** production/default: the first live path is an experimental HIVE worker opt-in only; main chat, research chat, and SwarmSession stay on the configured default runtime, with Ollama remaining default/fallback.

---

## Shipped

### v1.0 — Core shell, swarm, GOBLIN MIND

**Core shell (Single + Swarm + Chat modes)**
- Single-agent `AgentLoop`: plan-only review, execute mode, git checkpoints before execution
- Tool calls flow through approval-aware `ToolRegistry` handlers; refusal nudging and text-format tool-call fallback implemented
- Four trust tiers: per-tool approval chips persisted in the status bar; `ToolPolicyEngine` framework wired (UI config surface deferred — see below)
- Just Chat tab — research assistant with web search and tool use
- Co-work mode — `ask_user` tool pauses the LLM mid-run; `UserInputDialog` blocks until reply; steer message injection at each LLM step
- Per-mode model memory; first-run onboarding overhaul; persistent last-model and mode
- AI-assisted Agent Builder dialog
- Built-in screen recorder (F12 toggle, SharpAVI MJPEG AVI); FlaUI test auto-record

**Swarm runtime**
- Four worker roles: `RESEARCHER`, `CODER`, `UIDEVELOPER`, `TESTER`
- TESTER is read-only — no write access, exempt from retry loops
- Capability-aware fallback decisions in `SwarmSteering`
- File staging at `<runDir>/staging/` before apply; all-or-nothing commit
- Swarm Board capability badges and per-configuration metrics history
- Token cost estimator — live next-request estimate on the context badge

**GOBLIN MIND**
- Phase 1 — Format fingerprinting (`FormatProbeEngine`): 5 serialization variants (BareJson, OpenAiJson, HermesXml, PythonStyle, YamlBlock)
- Phase 2 — Category boundary mapping (`CategoryProbeEngine`): 7 categories (FileOps, Network, CodeExec, DataTransform, SystemInspect, StructuredOutput, TaskPlanning)
- Phase 3+6 — Schema library, adaptive schema generator, schema simplification middleware
- Phase 4 — Schema reduction middleware; schema applied to swarm routing at runtime
- Phase 5 — Evolutionary fitness storage (`FitnessMap`); UI surface is test-only (not in main app — see Needs Polish)
- Steering consumes capability data at runtime; `SwarmSteering` extracted and covered by T11 test suite
- In-app GOBLIN MIND CLI

**Model Wiki / Lab**
- Browseable model catalogue with capability testing
- Side-by-side comparison view
- Historical result trends strip
- Category filter chips (GOBLIN MIND category)
- Probe Now button — triggers live capability test from detail pane
- Export capability matrix to Markdown
- Local test results persisted and re-surfaced across sessions

**UI polish**
- Status bar: workspace, branch, build stamp (version + git commit + dirty flag), model, status text
- In-app Help window (F1) — embedded docs with search and cross-guide links; no browser required
- Dark VS Code-like menu theme; global implicit styles

---

### v1.2 — Training Pit, NIGHT HARVEST, GOBLIN HARVEST

**Training Pit**
- Boss-plan auto-capture after every swarm run; `DatasetCapture.cs` stages plans
- Manifest-driven review: `reviewed_v1.json` tracks every capture decision (keep / reject / hold)
- Pre-screen: rejects invalid roles, fabricated file references, truncated plans — deterministic, no LLM
- Judge triage: LLM judge receives per-capture FILE CHECK table and explicit role whitelist
- `review_captures.py`, `judge_captures.py`, `prescreen_captures.py` — all production implementations
- `validate_dataset.py`, `sanitize_dataset.py` — dataset quality gates
- Training Pit panel in app (Pit mode pill in title bar); gate progress cards in plain language
- Phase 3 gate met: 163/150 train / 20/20 eval / 25/25 negative; certified by three-reviewer audit

**NIGHT HARVEST / GOBLIN HARVEST**
- Autonomous overnight capture farming with fail-closed phase checks
- Stop-file kill switch; hourly cycle management; intelligent goal generator with linting
- `harvest_marker_watch.ps1` — auto-stops at the ~1,000 capture marker
- `tools/codex-review.ps1` — reliable scripted Codex CLI reviews (avoids stdin hang)
- Dataset review: manifest-backed review queue, one-click keep/reject in Training Pit panel

**swarmcli**
- Headless console swarm runner for CLI-driven orchestration
- `--plan-only` capture farming without GUI

---

### v1.3 — HIVE MIND Phase A + ORC ACADEMY training GUI + Model Wiki upgrades

**HIVE MIND Phase A**
- Named Ollama host store with persistent reachability probing (`HiveBeacon`, `TailscalePeers`)
- Tailscale-aware peer discovery — extends HIVE to any machine in a tailnet
- War-camp constellation panel (`HivePanel`) — live node cards with GPU load, VRAM, lane counts, pulse animations
- Title-bar HIVE pill — lights up when a remote node is active
- Installer HIVE MIND enrollment step — nodes enrolled during setup, no config file editing needed
- Node-aware model pickers (v1.3.2) — model dropdowns swap to remote node's models when "RUN ON" is selected

**ORC ACADEMY — training GUI (ORC ACADEMY, formerly WARCHIEF FORGE)**
- Production training interface: dry-run, VRAM cap, checkpoints, resume, progress heartbeat
- Live VRAM meter in Training Pit header during training
- `--vram-cap` flag: trainer coexists with Ollama workers on the same GPU
- Crash-safe: subprocess stdio file-redirected; app survives restarts mid-training, re-attaches to process
- Bulk review: dataset extended from 163 → **900 reviewed examples / 87 eval**; gate cards show professional targets
- `train_lora.py` — full QLoRA trainer with Unsloth/PEFT fallback (617-line test suite in `test_phase3_preflight.py`)

**ORC ACADEMY v1 adapter (shipped v1.3.3)**
- 900 reviewed boss plans, harvested overnight by GOBLIN HARVEST / NIGHT HARVEST
- LoRA trained locally in **148 minutes** on RTX 5070 Ti (Gemma 4 12B QAT base)
- A/B eval (blind, 87 cases): **99.3% structured planning pass rate** vs 94.5% base
- Deployed as `theorc-boss:gemma4-ft` — 125 MB GGUF LoRA; pull with `ollama pull theorc-boss:gemma4-ft`
- `tools/merge_lora.py`, `training_pit/adapters/registry.json`, `theorc-boss-gemma4-ft.Modelfile`

---

### v1.4 — HIVE MIND Phase 3A (distributed swarm) + platform

**HIVE MIND Phase 3A — distributed swarm engine**
- `HiveTaskQueue` (port 7079) — distributed work queue with per-task claim tokens, 60s pending timeout (falls back to local), 45s heartbeat timeout (re-queues task), stale-worker 409 Conflict rejection
- `HiveWorkerAgent` — polling, claiming, executing, heartbeating; single-pass LLM call per task
- `HiveScheduler` — capability-aware routing: model presence, VRAM free, lane matching all wired
- `HiveEventBus` — in-memory ring buffer + REST API polling for task lifecycle events (the `/events` side-channel)
- Right-click context menus on constellation node cards (disconnect, probe, view models)
- T15 test suite covers queue state machine, node routing, timeout behaviour
- Codex full review: all blockers resolved before ship

**Platform**
- **Self-updater** — `SelfUpdater.cs` checks GitHub releases, downloads .NET 10 SDK if needed, clones/pulls source, runs `dotnet publish`, writes relaunch script; fully wired, not a dialog stub
- **Results Launch Pad** — wired in `SwarmBoardPanel`: Run / Open Folder / Apply buttons, staged file list, gate findings panel (BLOCKER/MINOR/CLEAN with colour coding), run-error bar with "Fix this" auto-retry
- **Uninstaller** — registers in Windows Add/Remove Programs; `UninstallService.cs` handles clean removal
- llama.cpp RPC layer groundwork for multi-machine model splitting

---

### v1.5 — Pit Boss, SQLite, worktree isolation, reviewer gate

**Pit Boss — AI training wizard**
- 8-question in-app interview generates a structured `TrainingPlan` (goal types, languages, target count, model)
- Full execution pipeline: `PitBossService` kicks off dataset generation via Cerebras/Ollama (local), hands off to ORC ACADEMY Forge
- Plan history landing page — every training run logged with status, target count, model, timestamp
- `generate_cerebras_gold.py` — Cerebras `gpt-oss-120b` synthetic generation (free tier, ~20 min for ~1,200–1,400 examples per 72-batch run); sleep paced to 5 req/min free-tier limit
- ~~`generate_claude_gold.py` — Claude API fallback path~~ **ARCHIVED 2026-06-17** (violated no-Anthropic-bulk-gen + no-secrets-in-repo rules; see `Tools/_archived/`). Use `generate_cerebras_gold.py`.

**SQLite metadata layer** (shipped ahead of v1.6 plan)
- Phases 0–3: captures, triage, plans, runs, datasets tables; migrations included
- `PlanRepository`, `RepositoryBase`, `DataRecords`, `MetadataImporter`
- Replaces JSON manifests + filesystem-walk counting; cross-restart session resume; queryable run history

**Worktree isolation**
- `FileOwnershipLedger` — tracks which swarm task owns which file; all-or-nothing `TryClaim` (no hold-and-wait deadlock); serialized integration merges
- `WorktreeManager` — per-task git worktrees; TESTER and RESEARCHER exempt (read-only roles)
- Conflict retry: every 500 ms for up to 5 minutes; T16 test suite covers ownership tracking
- Opt-in via `AppSettings.HiveWorktreeIsolation`

**Reviewer Quality Gate**
- `ReviewGateService` wraps `gate-review.ps1` (Codex CLI path)
- `OllamaReviewService` — local Ollama reviewer (qwen2.5-coder:14b) for offline mode
- Both return `GateResult` with `Verdict` (Clean / Minor / Blocker)
- Three modes: Off / Advisory / Gated (toggle in SwarmSession)
- **Current status: advisory only** — BLOCKER finding changes Apply button to "⚠ Apply anyway (BLOCKER found)" but user can always override; true blocking is not enforced (see Needs Polish)

**Other**
- Training Pit: dynamic dataset auto-detection for new naming convention
- `night_ollama_gen.ps1` — zero-dependency overnight Ollama gold generator
- Advisory reviewer toggle wired in swarm UI

---

### v1.6 — HIVE security hardening, Update Center, fleet deploy

**HIVE MIND security overhaul (20-pass Codex review)**
- `HiveIdentity` — P-256 ECDSA signing key + X25519/ECDH exchange key per node; `NodeId = hex(SHA-256(signing pubkey DER))`; DPAPI-protected on Windows
- `HivePeerStore` — trust store with DPAPI-wrapped per-peer HMAC secrets; enrollment sequence for election ordering; `CreateForTest()` for headless CI
- `HiveAuthMiddleware` — per-request HMAC-SHA256 (canonical: `METHOD\nPATH\nNONCE\nTS\nHEX(SHA256(body))`); 30s clock skew window; nonce replay cache; fail-closed when `GracePeriodActive = false`
- `HiveMeshHeartbeat` — 30s signed heartbeat loop; liveness table; dead-peer eviction at 3× interval
- `HiveElectionService` — Bully-style leader election; five message types (suspect / claim / recover / stepdown / ping); enrollment-order tie-break; `WarchiefNodeId` tracked on all nodes

**Port 7079 HMAC enforcement**
- All `HiveTaskQueue` endpoints now validate HMAC before processing; `GracePeriodActive = false` (fail-closed, no migration mode)
- Body pre-read with 1 MB cap at handler entry; POST handlers receive `byte[]` body to avoid double-read

**Warchief crown badge**
- Gold border (`#FFD700`), `👑` prefix, and `W A R C H I E F` label on the correct constellation node card
- Warchief resolution via `HivePeerStore.Default.Find(nodeId)` → IP match for remote nodes; direct NodeId compare for "This PC"

**Update Center** (new mode tab `⬆ Update`)
- Version card: installed vs latest, release name, `🔄 Check Now`, `⬆ Update This Node`
- Inline build log with 5-step progress strip; downloads pre-built `.exe` from GitHub release asset when available, falls back to build-from-source
- Gold dot indicator on mode button when update available
- `UpdateChecker.GetReleaseAssetUrlAsync()` — finds `.exe` in release asset list
- `SelfUpdater.DownloadReleaseAsync()` — streams release asset to staging dir

**Warchief fleet deploy**
- `GET /hive/update/version` (unauthenticated) — returns node's installed version
- `POST /hive/update/deploy` (HMAC-authenticated, Warchief role check) — triggers background self-update on remote node
- Update Center Fleet section probes all paired peers, shows per-node version + status, `⬆ Deploy to Fleet` button

**Test infrastructure**
- `OrchestratorIDE.UnitTests` project — T10-T17 compile without AppFixture; 112 tests pass headlessly in ~1s over SSH; single source of truth via `<Compile Include>` pointing to UITests folder
- T17: 49 unit tests for the full HIVE security layer (HiveIdentity, HivePeerStore, HiveAuthMiddleware, HiveMeshHeartbeat, HiveElectionService)
- T18: FlaUI smoke tests for HivePanel constellation canvas (AutomationId on inner TextBlock elements)

---

### v1.8 — MarkdownView, Phase 7 test suite, ORC ACADEMY v2, Grok toolchain

**Avalonia MarkdownView (Phase 6)**
- Native Avalonia `ContentControl`-based Markdown renderer replacing the WPF `FlowDocument` path
- `IsVisible` deferred-render guard: prevents per-token GC pressure during streaming; only parses when the panel is actually visible
- Unified rendering across WPF and Avalonia code paths

**Phase 7 — FlaUI + Avalonia test suite**
- 23-test matrix: 8 WPF FlaUI interactive tests (T01–T08) + T20 Avalonia smoke (UIA window assertion)
- 174/175 tests pass (T06 pre-existing model-capacity skip: 4B Nemotron cannot produce valid `write_file` JSON)
- `Avalonia_Migration.md` Phase 7 checklist certified complete

**ORC ACADEMY v2 dataset**
- 1,784 train / 200 eval, zero train/eval leakage; gate-filtered for FILENAME RULE conformance
- Sources: 1,458 Cerebras `gpt-oss-120b` + 217 Codex gold + 320 gate-passing swarm captures
- **v2 adapter trained overnight (2026-06-17)**: Gemma 4 12B, 669 steps × 3 epochs, 333 min, **eval loss 0.2595** (vs v1 0.32+)
- Adapter at `training_pit/outputs/lora_v2/adapter/`; A/B eval vs v1 99.3% pending

**Grok toolchain integration**
- Grok Build CLI (`~/.grok/bin/grok.exe`) wired as the project's code-review and implementation agent, replacing Codex
- `.grok/SKILL.md` + `config.toml`: project conventions, hard rules, architecture map injected into every Grok session
- `tools/grok-review.ps1`: incremental review on every CodeGraph step; pattern replaces `tools/codex-review.ps1`
- GitHub MCP enabled (`GITHUB_TOKEN` via `gh auth token`): `/review --pr` and `/pr-babysit` available

---

### v1.7 — Avalonia 12 cross-platform UI migration

**WPF → Avalonia migration (Phases 1–5)**
- Full Avalonia 12.0.4 project (`OrchestratorIDE.Avalonia`): startup, app host, resources, styles
- All panels ported: `FileExplorerPanel`, `SettingsPanel`, `CheckpointPanel`, `SessionPanel`, `AgentPanel`, `ChatPanel`, `UpdatePanel`, `WarmUpEditorWindow`, `HivePanel`, `PitBossPanel`, `SwarmBoardPanel`, `TrainingPitPanel`
- `CodeEditorPanel` and `ToolEditorPanel` ported with AvaloniaEdit (Phase 2)
- `DiffViewer`, `ShellApprovalCard`, `UnknownToolCard`, approval flow (Phase 4 core panels — modal native dialogs deferred to v1.8)
- `MainWindow.axaml` — 4-row IDE layout (title bar, mode pills, editor/panel grid, status bar); full service wiring in `MainWindow.axaml.cs` (~850 lines replacing 2,589-line WPF code-behind)
- New Avalonia-native stubs: `CommandPalette.axaml/.cs`, `ModelPickerPopup.axaml/.cs`, `PaletteCommand.cs`, `HexToBrushConverter.cs`, `DialogHelper.cs`
- `OrchestratorIDE.UnitTests` — 121/121 tests pass headlessly (T10–T18 via shared Compile Include)
- All Codex BLOCKERs resolved before merge: `StringConverters` namespace fix, fail-closed `ask_user` stub, session workspace rebinding via `ConfirmWorkspace`, complete mode-toggle coloring

**Key Avalonia API substitutions applied**
- `Dispatcher.UIThread.InvokeAsync` (not WPF `Dispatcher.Invoke`)
- `IsVisible` (not WPF `Visibility` enum)
- `ToolTip.SetTip(element, value)` attached property
- `IsLightDismissEnabled="True"` on Popup (not `StaysOpen="False"`)
- `IClipboard` from `Avalonia.Input.Platform` with pattern-match
- `StorageProvider.SaveFilePickerAsync` for file save dialogs
- `PointerPressedEventArgs` (not `MouseButtonEventArgs`)

**Deferred to v1.8**
- Phase 4 native modal dialogs: `UserInputDialog`, `AgentBuilderDialog`, `ModelWikiWindow`, `LabWindow`, `ToolEditorDialog` hot-reload

---

## Needs Testing — real code, not yet battle-tested

These features are fully implemented but have not been stress-tested in the conditions that matter:

| Area | Gap | Risk |
|---|---|---|
| **HIVE MIND timeouts** | 45s heartbeat and 60s pending timeouts correct in unit tests but not tested with real multi-machine network latency/dropout | Worker tasks could silently re-queue without completing on flaky networks |
| **Unsloth trainer path** | `base_model_compat.json` notes Unsloth compatibility "not yet tested" — PEFT fallback is the proven path | Training may fail or be slower than expected on Unsloth-specific hardware |
| **Reviewer gate BLOCKER flow** | No automated test of BLOCKER finding → user override → apply sequence | Advisory gate could be clicked through without the user noticing |
| **Trust framework integration** | `ApprovalQueue`, `TrustLevel`, `ToolPolicyEngine` framework is wired; per-workspace config and end-to-end approval+policy+isolation flow has no integration test | Trust configuration could behave inconsistently across sessions |
| **HIVE MIND node dropout recovery** | Queue re-queues correctly in unit tests; actual TCP failure recovery on worker nodes not tested | Tasks could silently vanish on hard network drops |
| **EVAL_RUBRIC auto-scoring** | Defined in `training_pit/EVAL_RUBRIC.md` — "not yet wired to auto-scoring in SwarmSession" | Manual-only eval; no automated model regression testing |

---

## Needs Polish — wired but incomplete

These are intentional future phases, not bugs. They have placeholder UI or framework-without-surface.

### Reviewer Quality Gate — advisory only
The gate warns but does not block. "BLOCKER found" changes the Apply button label and colour but the user can proceed. Making it truly enforced requires a session-level override confirmation dialog and a way to bypass only with an explicit acknowledgement (not just a click). This is the primary quality-gate gap.

### Trust Tier config surface
`ApprovalQueue`, `TrustLevel`, and `ToolPolicyEngine` exist in `Trust/`. The four-tier trust system is wired at runtime but there is no Settings UI for configuring per-tool or per-workspace trust levels. Tool approval dialogs (`ShellApprovalCard`, `UnknownToolCard`) work; configuring which tools are always-trusted or always-blocked does not.

### GOBLIN MIND Phase 5 evolution UI
The evolutionary fitness map (`FitnessMap.cs`, `SchemaEvolution.cs`) is implemented and functional. The UI surface (`ToolCallTestWindow` Evolution tab) exists but is only accessible in the test harness window — it is not wired into the main app. This is test-facing only.

### Tool Editor — Phase 7 (stub)
`ToolEditorPanel` and `ToolCompiler` exist as a framework skeleton. The Roslyn hot-reload editing and compilation pipeline is **not implemented**. `UnknownToolCard.xaml:114` shows the placeholder: "The tool editor (Roslyn hot-load) is coming in Phase 6 — coming soon." No ETA. Static C# tool definitions work fine for everything current.

---

## Active Work

### ORC ACADEMY v3 — complete, not promoted (2026-06-17)

Training finished: Gemma 4 12B, 906 clean examples, 3 epochs, 156 min, rubric 99.17%. A/B eval complete.

| Dimension | Base | FT v3 |
|---|---|---|
| valid_json | 85/87 | **87/87** ✅ |
| task_count_ok | 78/87 | **87/87** ✅ |
| roles_valid | 85/87 | **87/87** ✅ |
| files_named | 74/87 | **65/87** ⚠️ FT *worse* than base |
| no_tester_write | 49/87 | **86/87** ✅ |
| **Overall** | **85.3%** | **94.7%** |
| Perfect plans | 37/87 | 64/87 |

**v3 does not beat v1's 99.3%.** Root cause: `files_named` — CODER tasks aren't naming output files explicitly enough. Every other dimension is perfect or near-perfect. This is a dataset coverage gap, not a model failure.

**Decision:** v1 stays production. v3 adapter at `training_pit/outputs/lora_v3/` — results in `ab_eval.json`. Not registered.

**v4 fix:** add targeted golden examples where CODER tasks explicitly name output files, rerun suitability gate, retrain.

### Documentation
- `ARCHITECTURE.md` pre-dates HIVE MIND Phase 3A and SQLite
- `TRAINING_PIT_GUIDE.md` does not cover the Pit Boss workflow end-to-end

---

## Planned — v1.9

### CodeGraph v1 — code knowledge graph (SHIPPED on master, targeting v1.9 release)

**Fully implemented in commit `f7fbe28`** (2026-06-17). Built 100% native and in-process — no external binary, no network egress — reusing Roslyn + SQLite already in the stack.

**What it does:** turns the open workspace into a queryable graph of code structure so the agent queries the graph instead of grepping/reading files one-by-one.
- **Level 1 payoff**: agent spends token budget on reasoning, not exploration (fewer grep/read cycles). Serves the five-nines tool-calling goal.
- **Level 2 payoff**: graph-grounded trajectories are higher-signal → cleaner LoRA datasets; graph tools become a new trainable tool schema through the existing capture → `ToolCallProbeEngine` pipeline.

**Architecture:**
```
OrchestratorIDE/Services/CodeGraph/
  GraphModels.cs           CodeNode / CodeEdge record types
  RoslynIndexer.cs         MSBuildWorkspace → nodes/edges; complexity wired
  ComplexityAnalyzer.cs    Roslyn ControlFlowGraph → cyclomatic, cognitive, loop nesting,
                           linear_scan_in_loop, is_recursive, transitive_loop_depth
  Data/GraphRepository.cs  SQLite read/write; Migration v5/v6; TraceCallers/Callees; ADR CRUD
OrchestratorIDE/Tools/
  GraphTools.cs            5 registered tools (see below)
```

**5 tools registered in `AgentLoop`:**

| Tool | Purpose |
|---|---|
| `graph_search` | BM25 full-text search over qualified names + structural boost (Function > Route > Class > File) |
| `trace_path` | Caller/callee tree to configurable depth via CALLS or DATA_FLOWS edges |
| `get_architecture` | Top-10 hub nodes by degree, namespace list, Route labels, file/edge totals |
| `detect_changes` | LibGit2Sharp diff → changed symbols + blast-radius (callers within depth 2) |
| `graph_adr` | Persistent Architecture Decision Records: list / get / add across sessions |

**Storage:** Migration v5 (`graph_nodes`, `graph_edges`, `graph_fts` FTS5, `graph_adr`) + Migration v6 (ADR schema evolution) in the existing `.orc/theorc.db`. No second DB file.

**Test coverage:** 132 unit tests + 11 T19 graph tests (RoslynIndexer roundtrip, FTS search, ADR CRUD, trace, complexity) all green. grok-review CLEAN on all 5 increments.

**Remaining for v1.9:**
- `CodeGraphService.cs` lifecycle façade (background re-index on workspace open + git-change signal)
- System prompt nudge in `AgentLoop`: "prefer graph_search/trace_path over grep_code for structural questions"
- Graph panel UI (optional nicety — not blocking)
- Tag captures that use graph tools to measure trajectory-quality lift (Level 2)
- Measure token-budget reduction on real swarm runs vs grep baseline

### ORC ACADEMY v4 Boss — fix files_named gap
v3 scored 94.7% overall but dropped `files_named` to 65/87 (worse than base 74/87). Fix: audit v3gold examples for file-naming coverage, author targeted golden examples where CODER tasks name explicit output files (`.cs`, `.xaml`, etc.), pass suitability gate, retrain. Target: ≥99% overall, ≥85/87 on files_named.

### Avalonia remaining modal dialogs — ✅ CLOSED (2026-06-20, WPF deleted)
`AgentBuilderDialog`'s functionality was already replaced by Avalonia's `AgentRulesWindow` before this closed. `ModelWikiWindow`/`ModelCompareWindow` were retired (not ported) as part of deleting WPF outright. ("`LabWindow`" never existed as an actual file in this repo — a stale planning reference, removed here.) `UserInputDialog` and `ToolEditorDialog` shipped in v1.8.

### HIVE MIND Phase 3B — multi-step tool calling on remote workers
`HiveWorkerAgent` currently executes single-pass LLM calls. Phase 3B adds full `AgentLoop`-style multi-step tool execution on remote nodes (file writes, shell commands, web search — all running on the worker machine). This is the primary remaining HIVE gap.

### Reviewer Gate hardening — true blocking mode
Add a session-level confirmation dialog for BLOCKER findings. The user must explicitly acknowledge ("I understand this output has a BLOCKER finding and I am proceeding anyway") rather than just clicking Apply. Optional: log all BLOCKER overrides to the SQLite runs table for audit.

### Trust Tier config UI
Surface the existing `ToolPolicyEngine` framework in Settings. Allow per-workspace "always trust" / "always block" lists for specific tools. Target: one Settings tab, no new service layer needed.

### Automated eval harness in Training Pit
Wire `EVAL_RUBRIC.md` into a UI-driven automated model regression test. After each ORC ACADEMY training run, auto-run the eval set and surface pass-rate change vs prior adapter. Currently manual-only.

### HIVE MIND: remote harvest and academy execution
Allow a HIVE worker node to run GOBLIN HARVEST overnight and return captures to the boss node's Training Pit. Remote adapter training (via HIVE) is further out — needs Phase 3B first.

---

## WARBANDS — Cloud & Headless Deployment

> Full spec: [`.grok/WARBANDS.md`](../.grok/WARBANDS.md). The daemon is the Warband. Binary rename pending: `theorc-daemon` → `theorc-warband`.

A **Warband** is a deployed headless HIVE node — the `OrchestratorIDE.Daemon` binary running on any machine that isn't your main desktop. Your GUI app (the Warchief) stays home. Warbands run in the cloud, on a home-lab machine, or in Docker — headless, no GUI, pulling tasks from your Warchief's queue.

```
Your machine         Cloud / LAN
──────────────       ─────────────────────────────────
Warchief (GUI)  ──→  Warband 1 (linux-x64, Vast.ai GPU)
                ──→  Warband 2 (win-x64, home lab)
                ──→  Warband 3 (osx-arm64, MacBook)
```

**Current shape (now):** each Warband in Docker needs an Ollama sidecar — two containers per deployment.

**Post-Native-Runtime target shape:** LLamaSharp in-process eliminates the sidecar. One container, GGUF mounted as a volume, ORCISH TONGUE GBNF constraints work on any model. See `RUNTIME_PHASE0_SPEC.md` §11. The initial runtime code exists now, but this deployment shape is not shipped yet.

**Mac/Linux Warband binaries** are one CI job away — `net10.0`, AES-256-GCM secrets (no DPAPI), cross-platform Tailscale path detection all shipped in v1.6.2. GitHub Actions publish matrix and release artifacts are the remaining gap.

| Pending | Status |
|---|---|
| Binary rename: `theorc-daemon` → `theorc-warband` | ⬜ Next commit |
| CI publish matrix for `linux-x64` + `osx-arm64` Warband binaries | ⬜ v1.9 / v2.0 |
| `warband.compose.yml` Docker template | ⬜ v1.9 / v2.0 |
| GHCR/Docker Hub publish on release | ⬜ v2.0 |

---

## TheOrc Native Runtime — v2.0 direction

> Full spec: [`.grok/RUNTIME_PHASE0_SPEC.md`](../.grok/RUNTIME_PHASE0_SPEC.md). Status changed after ORC ACADEMY v3 completed: early runtime groundwork has landed, but native runtime is still pre-production and not the default.

**What it is:** an orchestration / swarm-aware layer *on top of* LLamaSharp (llama.cpp bindings) — **not** a from-scratch inference engine. llama.cpp owns the kernels; TheOrc owns scheduling, session management, adapter hot-swap, VRAM-aware dispatch, and direct Avalonia streaming. The moat is making the warband feel like one cohesive mind on the GPU instead of a series of independent HTTP calls.

**Why:** removes the Ollama install/management burden, the per-call model-reload penalty, the HTTP round-trip, and the `ollama create` merge step in ORC ACADEMY deploy. Also advances the cross-platform goal (LLamaSharp runs on Mac/Linux).

| Phase | Scope | Status | Risk |
|---|---|---|---|
| **0** | `IModelRuntime` abstraction + `OllamaRuntime` wrapper. | ✅ Landed — local generation paths can depend on the runtime interface; Ollama remains default. | Low |
| **1** | `LlamaCppServerRuntime` — wraps the **existing** `LlamaServerManager` + `InferenceBackend.LlamaCpp`. | ✅ Landed — server lifecycle and HTTP routing exist behind the runtime interface. | Low |
| **2** | `LLamaSharpRuntime` — in-process GGUF streaming, embedded-template probing, stats, shared text tool-call parsing. | ⚠️ Prototype landed — useful for validation, not production/default. LoRA hot-swap, backend install flow, and full runtime selection are not complete. | Med |
| **2.5** | Close abstraction leaks from the first migration. | ✅ Closed for `HiveWorkerAgent` and reviewer inference — both use `IModelRuntime`; SwarmSession's Ollama-specific eviction escape hatch and remote HIVE task-queue/node HTTP remain separate follow-up plumbing. | Med |
| **3** | `ModelDepot` (local registry first; downloader later), `SessionManager` (persistent base model), `AdapterManager` (boss/worker/reviewer LoRAs), telemetry. | 🔶 Live opt-in proof path landed — ModelDepot, SessionManager, AdapterManager (per-role persistent executors, adapter attached once at creation per the §7 verdict), and `RuntimeOrchestrator` all landed; `IRoleRuntime`/`NativeRoleRuntime` now stream that role stack, `THEORC_TEST_GGUF` has an opt-in role-runtime smoke lane, Avalonia Settings exposes manual native smoke/fallback/evidence capture, and `HiveWorkerAgent` can opt into native role execution with logged fallback to the configured model runtime. Remaining: keep proving the real-model path, surface SessionManager/AdapterManager-backed telemetry, wire OrcScheduler into AdapterManager, and do not make native the main chat/swarm default yet. | Med-High |
| **4** | `OrcScheduler` — capability + VRAM + lane-aware dispatch; pipeline boss→workers. | 🔶 Started — interface + data model + a real VRAM-budget admission check landed; `RuntimeOrchestrator` now tracks active per-role reservations (generation-tagged, serialized through one admission gate) instead of a static zero-reserved snapshot, closing the over-admission gap the static check left open. Still not wired into AdapterManager beyond `RuntimeOrchestrator`'s own gate, no live GPU dispatch or pipeline queueing. | High |
| **5** | *(Research, non-blocking)* prefix KV cache for the shared warband prompt; multi-LoRA cache experiments. | ✅ Research closed — `Conversation.Fork()`/`MemorySequenceCopy` is a real, cheap shared-prefix mechanism, confirmed via LLamaSharp's own XML docs; blocked for cross-role sharing because `SetLoraAdapters` is context-scoped, not per-sequence — same-role prefix forking remains a viable future win, see `.grok/PREFIX_KV_CACHE_RESEARCH.md`. | Research |

**Caveat (permanent, now mechanically confirmed — not just inferred):** shared KV cache across *different* LoRA-specialized agents is not guaranteed safe — adapters change activations, and LLamaSharp 0.27.0's `SetLoraAdapters` applies per-context, not per-sequence, so forked sequences sharing a context cannot run different adapters. Start with simple prefix caching of the common system prompt only **within one role**; cross-role sharing is research, never a promised deliverable. LoRA hot-swap requires a verification spike before it joins a committed phase — that spike ran (§7), and the verdict (separate persistent contexts per role) is implemented in `AdapterManager`.

Ollama stays the **default and fallback** until the ModelDepot + installer first-run story is bulletproof and the reviewer/Swarm abstraction leaks are closed.

---

## Deferred — with rationale

| Feature | Status | Rationale |
|---|---|---|
| **Skills / plugin system** | Not started — no framework exists | Was a prompt concept; static C# tool definitions cover all current needs; revisit when there is a concrete extensibility use case |
| **GOBLIN MIND Phase 5 evolution UI (main app)** | Test-only | Low demand; fitness data is useful for debugging but not a user-facing daily feature |
| **Tool Editor hot-reload (Phase 6/7)** | Stub only | Roslyn pipeline is complex; pay-off unclear until tool definitions are more dynamic |
| **HIVE MIND C2 (RPC model chain)** | Groundwork laid | llama.cpp RPC plumbing exists; full SwarmSession routing to RPC workers not wired; blocked on Phase 3B |
| **"Zero idle chatter" message discipline** | Not implemented | Good spec hygiene; no user-visible impact currently; revisit when HIVE worker verbosity becomes a real problem |
| **Cross-platform desktop (Mac / Linux)** | WPF deleted 2026-06-20, Avalonia is the only desktop shell | `dotnet publish -r osx-arm64` should work — not yet verified on real macOS/Linux hardware. ScreenRecorder degrades gracefully (SharpAVI Windows-only). Warband (daemon) is already cross-platform — see WARBANDS above. |
| **On-platform self-improvement (TheOrc trains itself)** | Partial | Pit Boss + Cerebras pipeline makes the dataset side nearly free; the gap is auto-generating and auto-judging training goals without human input; deferred until v2 adapter proves out the data quality |

---

## Reading Order

New to the project — read in this order:

1. [ARCHITECTURE.md](ARCHITECTURE.md)
2. [GLOSSARY.md](GLOSSARY.md)
3. [USER_GUIDE.md](USER_GUIDE.md)
4. [SWARM_GUIDE.md](SWARM_GUIDE.md)
5. [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md)
6. [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md)
