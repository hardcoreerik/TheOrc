# TheOrc — Roadmap

> Last updated: 2026-07-13 (TheOrc Foundry's first specialist model, theorc-toolcaller r3, trained/promoted/deployed; see below).
> This document is updated after every GitHub release. It reflects actual code state, not aspirations — features marked Shipped have been verified in the running app.

---

## Where we are

TheOrc is a production local AI orchestrator. The core swarm, model intelligence, distributed HIVE MIND, and self-training loop are all shipped and running. The v1 adapter scores **99.3%** on structured planning evals and remains the production adapter. The **v2 adapter regressed** — a post-hoc suitability audit found 51.3% of its 1,784 examples assigned write tasks to TESTER-lane roles, dropping the structured-plan pass rate to 77.8% (perfect plans 71% → 54%); v2 was retired and its data repurposed. **ORC ACADEMY v3** completed on a clean 906 train / 87 eval set and beat base in A/B (94.7% vs 85.3%), but did **not** beat the v1 99.3% production baseline because of the `files_named` gap. v1 stays production; v3 is not registered.

**Foundry status:** the first specialist model shipped in v1.12.0. `theorc-toolcaller`
(Qwen2.5-1.5B + LoRA, round r3) is trained, promoted, deployed via Ollama, and wired
into the Swarm worker loop as an **opt-in** repair lane (off by default) — see
[FOUNDRY_ARENA.md](FOUNDRY_ARENA.md) for the promotion policy and
[THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md) /
[TOOLCALLER_REFUSAL_GAUNTLET.md](../training_pit/../docs/TOOLCALLER_REFUSAL_GAUNTLET.md)
for the evaluation contract and adversarial-safety results. r3 measured 98.5% sealed
decision accuracy and 98.3% held-out gauntlet safety, but has a known remaining
regression on ordinary (non-tool) conversation (89.3% strict, all misses are
over-eager calls, not fabricated arguments) — the repair lane stays opt-in until an
r4 round closes that gap. The promotion margin that will judge r4+ is frozen in
`training_pit/foundry/configs/toolcaller_v0_r3.json`; r3 itself was promoted before
that margin existed and is not retroactively judged by it.

v1.8.0 ships the Avalonia MarkdownView (Phase 6), the full FlaUI + Avalonia test suite (Phase 7, 23 tests), and the Grok toolchain integration. CodeGraph v1 — a Roslyn + SQLite code knowledge graph that lets the agent query graph structure instead of grepping files — is fully implemented and committed, targeting v1.9.

**HIVE MIND node startup was broken on every normal-user install until 2026-06-20** — `HiveNodeServer.Start()` silently aborted (no error, no log line, nothing listening) on any non-elevated machine, because a failed wildcard `HttpListener` bind left the listener disposed internally and the fallback cleanup code's own property access threw a second, masking exception inside an unobserved `Task.Run`. Found via a pre-release smoke test specifically because nothing in automated test coverage exercises real socket binding. Fixed — verified `localhost:7078/hive/info` returns 200 and UDP 7077 beacon listens. This was a hard release blocker for any LAN/Tailscale HIVE MIND testing; not caught by `dotnet test` since the unit/headless suites mock or don't reach real listener startup.

The honest gaps: the Reviewer Quality Gate is advisory-only (can always be overridden), the Tool Editor hot-reload is a stub, and Phase 3B is implementation groundwork rather than a completed distributed-computing release. Native campaign contracts, capability-aware atomic leasing, persistence, content-addressed transfer, independent verification, the shared headless loop, fail-closed native-agent execution, staged campaign dependencies, native input-artifact materialization, and CF-6 distributed HIVE Context Fabric execution now exist in code. **WPF is deleted (2026-06-20)** — `OrchestratorIDE/OrchestratorIDE.csproj` and every WPF-only file are gone from the repo; Avalonia is the only desktop shell. `ask_user`, `ModelCapabilityTestDialog`, and `ToolCallTestWindow` all have real Avalonia resolutions (the latter two retired as diagnostics, not ported); `ModelWikiWindow`/`ModelCompareWindow` were retired rather than ported (data layer kept, window itself dropped — a future from-scratch Avalonia rebuild is a real feature request, not a blocker). The UIA automation lane already targeted Avalonia exclusively. Native Runtime groundwork is now real in code (IModelRuntime, Ollama wrapper, llama.cpp server wrapper, LLamaSharp runtime, shared text tool-call parser, Chat/Swarm/HIVE worker/reviewer migration, ModelDepot, SessionManager, AdapterManager with per-role persistent LoRA contexts, RuntimeOrchestrator wiring all three together, `IRoleRuntime`/`NativeRoleRuntime`, Settings-panel telemetry/smoke surfaces with explicit Ollama fallback and evidence capture, OrcScheduler VRAM-budget admission checks, workload-aware native model-admission, and CUDA backend-selection diagnostics for driver-only GPU nodes). It is still **not** production/default: native main chat and native HIVE workers are opt-in; legacy paths may retain configured fallback behavior, while native campaign jobs fail closed and do not fall back to Ollama. Context Fabric CF-0 through CF-8 have landed their focused framework/distribution/benchmark/hard-ingestion gates; unattended million-token/LongBench runs and full multimodal page understanding remain future benchmark/product work.

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

### OrcChat — uncensored multi-backend chat (branch `feat/uncensored-chat-models`, unpushed)

A SillyTavern-pattern chat experience built from scratch in C#, not a clone. Branch is
local-only (not pushed). Shipped on the branch so far:
- **Phase A** (separately released): 3 uncensored Dolphin-line models added to the model
  catalogs with an UNCENSORED badge — opt-in only, never auto-recommended.
- **`ChatEngine` dual-mode generalization**: one engine carries Research and (former) Open
  behavior instead of forking a second engine; `OllamaClient.GetContextLengthAsync` +
  `ChatEngine.OnUsage` feed a context-window usage indicator; opt-in `IncludeDateTimeContext`
  date/time grounding; `OpenChatMemory` persists a chosen system prompt across restarts.
- **OrcChat merge** (2026-06-23): the former Research/Open mode toggle was collapsed into a
  single surface — web search/fetch tools are **always** available, no system prompt is
  injected by default, user controls system prompt/temperature/top-p, with HIVE node routing
  to run a chat on a paired machine's Ollama.
- **Images in chat output** (2026-06-23): `MarkdownView` renders `![alt](src)` (http(s)/
  data:/local-file), background-thread decode, broken-image placeholder, tooltip + right-click
  "Copy image link".

Pending on the branch (the remaining handoff items):
- **Image/file ingestion with vision** — `AgentMessage` has no image/attachment field and
  `OllamaClient`'s payload has no `images` array yet; needs the field, the Ollama vision
  payload, a chat-UI attach affordance, and a "model can't see images" cue for non-vision models.
- **Full FlaUI UI-test pass** for the chat surface (viable on the interactive dev machine).

Review note: while grok/codex are unavailable, branch commits are gated through
`theorc-review.ps1` against NEWCOREPC's `qwen2.5-coder:14b` plus a human/Opus judgment pass,
not the usual `grok-review.ps1`.

### Native runtime / OrcChat-off-Ollama (branch `feat/native-runtime-orcchat`, off the above, unpushed)

Picked up `.grok/RUNTIME_SWITCH_PLAN.md`'s long-open "Stage 3" question by verifying actual code
state rather than trusting the doc's "Not started" framing (it was stale — see that file for the
correction). Shipped on this branch, all real-machine-verified, not just built:
- **Settings UI for the llama.cpp backend** — `OllamaClient`/`MainWindow`'s `LlamaServerManager`
  lifecycle/`ChatPanel`'s shared-client inheritance already fully supported running OrcChat off a
  local `llama-server` with zero Ollama involvement; there was simply no UI control to reach it.
  Added the missing toggle + path/port fields.
- **Settings UI for the experimental native main-chat (Agent panel) toggle** — same pattern: a
  fully-wired in-process `NativeRoleRuntime` path for the Agent panel's chat loop
  (`AppSettings.ExperimentalNativeMainChatEnabled`) existed with zero UI exposure. Note: this one
  needs an app restart to take effect (`BuildAgentLoopRuntime()` is only called once, at
  construction) — disclosed in the hint text rather than silently shipped.
- **Real bug found and fixed**: `ChatEngine` passed raw `ToolDefinition` objects into
  `StreamCompletionAsync` without mapping them through `.ToOllamaSchema()` — Ollama's
  `/v1/chat/completions` tolerates the malformed shape silently, llama.cpp's stricter OpenAI-compat
  parser rejects it with a `500`. Found on the very first real attempt to drive OrcChat through
  llama.cpp via the actual Settings UI (not just direct HTTP probing) — fixed, then verified both
  single-turn and multi-turn conversations work correctly end-to-end against a live server.
- **Model downloader hardening**: auto-retry-with-resume on transient download failures; real
  SHA-256 verification wired in via HF's `tree/main` API (`lfs.oid`) — the in-app downloader had
  never verified integrity before, despite the verification method existing as dead code.
- **UI-test AutomationId drift fixed**: `HivePanel`/`AgentPanel`/`CommandPalette`/
  `SwarmBoardPanel`/`ChatPanel`/`SettingsPanel` all had zero `AutomationProperties.AutomationId`
  anywhere (WPF→Avalonia port drift, not a runtime bug) — added what each panel's tests or this
  branch's own end-to-end verification needed. Full UI suite went from 169/201 to 198/201; the
  remaining 3 are pre-existing, unrelated `ToolEditor` failures present before this branch existed.
- **Real cross-machine GPU verification**: fetched a genuine CUDA-enabled `llama-server` build
  onto hardcorelaptopmsi (RTX 4060) and confirmed real in-process/server-side native generation —
  67.7 tok/s with 3147 MiB VRAM in use, vs. ~6 tok/s CPU-only earlier in the same session.

Not done, flagged honestly rather than guessed at:
- **HARDCOREPI untested** — SSH key not authorized from this branch's working session; the
  "native runtime works on any OS" goal still has zero non-Windows data points.
- **Remote HIVE-node chat always uses Backend=Ollama regardless of local Backend setting** —
  checked and ruled out as a bug: `HiveHost.Url` is explicitly modeled as an Ollama endpoint only,
  with no per-node llama.cpp concept at all today. A real "remote llama.cpp node" would be new
  scope, not a fix.
- **Branch not pushed** — getting it onto HARDCOREPC (which has its own separate clone, currently
  on `master`) for a real cross-machine *build* test would need either pushing to GitHub or
  ad-hoc file copying outside git; flagged as a decision for whoever picks this up next rather
  than acted on unilaterally.

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

**Follow-through state:** `CodeGraphService` now owns background indexing on workspace open, and `AgentLoop` explicitly prefers graph tools for structural questions. Remaining measurement and UX work is to tag graph-backed captures, measure token-budget reduction against the grep baseline, and decide whether a dedicated graph panel earns its cost.

### ORC ACADEMY v4 Boss — fix files_named gap
v3 scored 94.7% overall but dropped `files_named` to 65/87 (worse than base 74/87). Fix: audit v3gold examples for file-naming coverage, author targeted golden examples where CODER tasks name explicit output files (`.cs`, `.xaml`, etc.), pass suitability gate, retrain. Target: ≥99% overall, ≥85/87 on files_named.

### Avalonia remaining modal dialogs — ✅ CLOSED (2026-06-20, WPF deleted)
`AgentBuilderDialog`'s functionality was already replaced by Avalonia's `AgentRulesWindow` before this closed. `ModelWikiWindow`/`ModelCompareWindow` were retired (not ported) as part of deleting WPF outright. ("`LabWindow`" never existed as an actual file in this repo — a stale planning reference, removed here.) `UserInputDialog` and `ToolEditorDialog` shipped in v1.8.

### HIVE MIND Phase 3B — native campaign engine (FOUNDATION LANDED)

Commit `22db2ccf` landed the shared headless native loop, `native_agent` and `container_pack` campaign contracts, worker capability declarations, atomic capability-aware leasing, campaign persistence, resumable content-addressed model/artifact transfer, independent verification, retries, stale-token rejection, and fail-closed native execution.

This is not yet a finished Phase 3B product. The remaining release gates are dependency-aware campaign stages, native-agent input-artifact materialization, hostile-input hardening, complete campaign-control UX, and the planned multi-node acceptance run with worker loss, re-lease, stale-result rejection, pause/resume/cancel, artifact verification, and measured speedup.

### The Orc Context Fabric — effectively unbounded source memory (CF-0 through CF-8 landed)

Full design: [The Orc Context Fabric.md](The%20Orc%20Context%20Fabric.md).

Context Fabric is the next major OrcChat/native-runtime direction. It treats the model context as a bounded working set over a disk-backed, source-addressable memory system rather than trying to place an entire book or library into one prompt. Every source is parsed and segmented; native readers create citation-bearing evidence cards; section/chapter/document reducers create hierarchical memory; a document graph connects claims, entities, conflicts, and source ranges; and query-time "cognitive page faults" reopen original text whenever compressed memory is insufficient.

The implementation deliberately builds beside CodeGraph on the same `SqliteStore`, migration runner, WAL database, FTS5 pattern, transactional repository layer, and lifecycle model. It does not put document concepts into `graph_nodes`; a dedicated `DocumentGraphRepository` preserves corpus, edition, page, confidence, coverage, and citation semantics while optional typed links connect document evidence to code symbols.

Delivery order:

1. CF-0 contracts, evidence schema, deterministic corpus, and 16-segment native feasibility spike. **Passed:** the scripted lane remains green; the pinned Hermes 3 Llama 3.1 8B native lane passed 16/16 segment cards, 5/5 questions, 100% citation precision, all nine gates, and an 11.50x source-to-working-context ratio inside the 8K limit. A second verified native lane now passes on Gemma 4 12B through the runtime's `GemmaNativeFallback` prompt path with 16/16 segment cards, 5/5 questions, 100% citation precision, and an 11.48x ratio. Quote anchoring and the 2/2 native boundary-stitch lane also pass.
2. CF-1 deterministic ingestion, structural segmentation, and content-addressed source storage. **Passed in focused tests:** migrations v8-v9, strict UTF-8 text/Markdown parsing, stable structural segmentation, SHA-256 source/normalized storage, transactional repository replacement, FTS5 search, rebuild/delete paths, Darwin text/PDF reproducibility, pinned Constitution and Federalist fixtures, artifact GC, and focused failure tests are implemented. Product-surface work remains follow-on rather than a CF-1 blocker.
3. CF-2 document graph, SQLite migrations, FTS, source tools, and local retrieval. **Passed in focused tests:** migration v10, `DocumentGraphRepository`, `FabricEvidenceGraphImporter`, claim FTS, provenance-carrying `FabricSearchService`, and read-only library graph/search/open/list tools are implemented, with in-memory and on-disk repository tests plus unchanged CodeGraph tests.
4. CF-3 native readers, boundary stitching, schema validation, and source verification. **Framework exit gate passed in focused no-fallback tests.**
5. CF-4 hierarchical reducers, context budgeting, source rehydration, Quick and Study modes. **Framework exit gate passed in focused tests.**
6. CF-5 OrcChat Library, corpus attachment, citations, coverage, and persistent cited notebook. **Merged in PR #29:** cited answers use short source labels mapped back to verified segment IDs, citation preview opens as a popup, original source files can be staged/opened from citations, and focused ask/headless tests plus Windows CI and CodeRabbit passed.
7. CF-6 HIVE stage dependencies, native input staging, distributed readers/reducers/verifiers. Merged in PR #15 with passing Windows/CodeRabbit checks and recorded live 3-machine worker-death recovery evidence: heartbeat-loss requeue, different-node reclaim, stale completion rejected with HTTP 409, token rotation, and exactly one accepted completion.
8. CF-7 Exhaustive mode and frozen B0-B4 benchmark gate. Merged in PR #16 with the benchmark-gate report contract, CLI suite, tests, and CodeRabbit/Windows checks green.
9. CF-8 multimodal documents, optional vector acceleration, and cross-CodeGraph links. **Closed in focused tests/docs:** parser block provenance, DOCX/EPUB structure, table/figure blocks, optional OCR contracts, immutable document versions, cache policy, vector fallback, and cross-corpus/CodeGraph links are landed; full multimodal understanding and unattended million-token benchmarks remain future work.

The go/no-go benchmark compares closed-book, truncated prompt, conventional RAG, single-node Context Fabric, and HIVE Context Fabric with the same native model. It requires exact synthetic ground truth, a pinned public-domain Darwin corpus, standardized long-context subsets, citation precision, multi-hop and exhaustive recall, correct abstention, an 8K final-context ceiling, normalized HIVE scaling, and worker-failure recovery. The project must report a failed hypothesis if these gates do not beat ordinary RAG; impressive demos are not acceptance evidence.

### Reviewer Gate hardening — true blocking mode
Add a session-level confirmation dialog for BLOCKER findings. The user must explicitly acknowledge ("I understand this output has a BLOCKER finding and I am proceeding anyway") rather than just clicking Apply. Optional: log all BLOCKER overrides to the SQLite runs table for audit.

### Trust Tier config UI
Surface the existing `ToolPolicyEngine` framework in Settings. Allow per-workspace "always trust" / "always block" lists for specific tools. Target: one Settings tab, no new service layer needed.

### Automated eval harness in Training Pit
Wire `EVAL_RUBRIC.md` into a UI-driven automated model regression test. After each ORC ACADEMY training run, auto-run the eval set and surface pass-rate change vs prior adapter. Currently manual-only.

### HIVE MIND: remote harvest and academy execution
Allow a HIVE worker node to run GOBLIN HARVEST overnight and return captures to the boss node's Training Pit. Remote adapter training (via HIVE) is further out — needs Phase 3B first.

### TheOrc Foundry — custom local model program

> **SHIPPED in v1.12.0** (this entry originally listed the strategy under v1.9
> planning; it is left in place chronologically but the status below is current
> as of v1.12.0, not v1.9).

Canonical strategy: [THEORC_FOUNDRY.md](THEORC_FOUNDRY.md). Evaluation and
promotion policy: [FOUNDRY_ARENA.md](FOUNDRY_ARENA.md). First proof, now
promoted: [THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md) /
[TOOLCALLER_REFUSAL_GAUNTLET.md](TOOLCALLER_REFUSAL_GAUNTLET.md).

TheOrc Foundry is the program for creating TheOrc-native specialists on locally
controlled consumer hardware. It joins the Training Pit and ORC ACADEMY
data/training foundation to Native Runtime/Ollama deployment. Candidate models
must beat the current baseline on a frozen rubric without safety regressions
before promotion.

**Current status:** `theorc-toolcaller` (Qwen2.5-1.5B + LoRA) is trained,
promoted (round r3), deployed as `theorc-toolcaller:qwen25-1.5b` via Ollama, and
wired into the Swarm worker loop as an opt-in repair lane. The Training Pit ships
a Stage 4 **ARENA** benchmark panel (sealed 260-example eval) and a **Refusal
Gauntlet** (4,788-case adversarial refusal-safety suite with exact confidence
bounds). Organic training-data capture runs from two sources — Swarm and
OrcChat — both off by default under one settings toggle.

**Still planned / open:**
- An r4 round to close the known ordinary-conversation over-eager-call
  regression (89.3% strict on benign no_tool cases; see
  [TOOLCALLER_REFUSAL_GAUNTLET.md](TOOLCALLER_REFUSAL_GAUNTLET.md)) — repair
  lane stays opt-in until this closes.
- Promotion is still judged by review of the metrics in
  `training_pit/foundry/configs/toolcaller_v0_r3.json`, not yet by a single
  mechanical gate command that refuses promotion on any unmet criterion
  (dataset/eval hashes, confidence bound, per-family safety floor, latency
  budget, explicit human-approval record, rollback artifact) — see the
  Foundry promotion gate work item below.
- Universal producer-provenance/contamination fields (candidate-vs-incumbent
  flag, teacher identity, repair lineage, redaction state) are enforced for the
  toolcaller repair lane specifically, not yet as a schema-level gate every
  Foundry/Training Pit capture path must satisfy.
- ORCISH TONGUE remains the planned universal tool-caller rename/runtime
  direction; existing prompt-layer adaptation remains under current code names.

### HIVE MIND: hive identity, membership certs, auto-promotion — v1.9.4 (all 4 phases shipped 2026-06-21)
Spec: [`HIVE_MEMBERSHIP_SPEC.md`](HIVE_MEMBERSHIP_SPEC.md). Adds a hive-wide `HiveId` (survives Warchief elections, unlike per-node identity), membership certificates so a node can prove hive membership to a peer it never directly paired with (avoids O(n²) manual-approval pairing at "100s of nodes" scale), an authenticated `/hive/mesh/role-assign` RPC + "👑 Declare this machine Warchief" UI action (first real consumer of the long-dormant `HiveAcceptControlPolicy` enum), and a first-run/repair discovery wizard (`HiveDiscoveryWizard`: scan LAN → join existing hive or found a new one, with three trigger sites). All four phases landed same day, each build+test+grok-review-CLEAN before commit; full swarmcli parity (`--list-peers`, `--declare-warchief`, `--set-accept-control`). Also fixed a naming collision: the pre-existing "🎯 Set as Warchief" menu item was an unrelated swarm-task-routing preference, renamed to "📤 Route my swarm tasks here". One deferred remainder: presenting a membership cert at the request-time auth gate needs its own subject-proves-key signature scheme (issuance + verification shipped; wire-gate consumption intentionally not bolted on).

### HIVE MIND: duplicate node-entry merging + Warchief/Worker topology layout
Deferred "next version" items from live pairing testing (2026-06-21): merge duplicate node cards for the same machine (e.g. one via LAN, one via Tailscale IP, one via Tailscale MagicDNS) into a single card showing all reachable paths; change the constellation's visual layout once roles are assigned (Warchief vs Worker) to reflect hive topology, not just a flat node list. Both visual-only, unrelated to the trust-model work in `HIVE_MEMBERSHIP_SPEC.md`.

### Installer: cross-platform Avalonia rewrite — Phases 1, 2, 4, 5 shipped 2026-06-21; macOS release artifacts shipped in v1.11.2
Full spec: [`INSTALLER_REVAMP_SPEC.md`](INSTALLER_REVAMP_SPEC.md). `OrchestratorSetup` was a Windows-only WPF wizard (`net10.0-windows`, WMI, `netsh`, `.lnk`, registry) built before the Avalonia migration, capping the whole product to Windows even though the app + daemon are cross-platform. **Phase 1 (UI port + page restructure)**: `OrchestratorSetup` is now `net10.0` Avalonia, ported directly to the final 8-page flow. **Phase 2 (`IPlatformInstaller`)**: every OS-coupled action (hardware detection, firewall/HIVE, launchers, uninstall, launch path) now goes through one interface; `WindowsPlatformInstaller` is pure delegation, no rewrite. **Phase 4 (`LinuxPlatformInstaller`)**: `/proc`+`nvidia-smi`/`lspci` detection, XDG paths, `ufw`/`firewalld` via `pkexec`/`sudo`, `.desktop` launchers, manifest-file uninstall registration. **Phase 5 (`MacPlatformInstaller`)**: `system_profiler`+`uname`+`sysctl` detection, Application Support paths, the per-app Application Firewall via `osascript`-elevated `socketfilterfw` (the GUI-native equivalent of UAC/`pkexec` — raw `sudo` has no TTY to prompt in from a windowed app), a `~/Applications` symlink launcher standing in for a real `.app` bundle. All Windows-only service logic carries over unchanged (it was already plain C#, not WPF). **Current caveat**: macOS app/setup artifacts now publish, but Linux desktop app artifacts and real cross-OS hardware soak remain future work.

### Multi-OS release pipeline — macOS app/setup and Linux/macOS Warband artifacts shipped
Full spec: [`MULTI_OS_RELEASE_SPEC.md`](MULTI_OS_RELEASE_SPEC.md). This closed the first release-engineering gap the installer entry above left open. v1.11.2 publishes Windows app/setup artifacts, macOS `osx-arm64` app/setup artifacts, and raw Warband archives for `linux-x64` and `osx-arm64`. The manifest's `app.download_url` is OS-keyed; `InstallerState`'s exe/install paths go through `PlatformInstaller.Current`; `LlamaCppResolver` is OS+arch-aware with a real `metal` variant; `ZipExtractService` supports `.tar.gz`; binary placement sets the Unix executable bit; and `UpdateChecker`, `SelfUpdater`, and `LlamaServerManager` no longer assume Windows binary names only. **Remaining gaps**: full Linux desktop app publishing, Linux/macOS Ollama installer automation, real cross-OS hardware verification, AppImage/`.dmg` packaging, and macOS notarization.

---

## WARBANDS — Cloud & Headless Deployment

> Full spec: [`.grok/WARBANDS.md`](../.grok/WARBANDS.md). The daemon is the Warband. Binary rename complete: `theorc-daemon` → `theorc-warband`.

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

**Mac/Linux Warband binaries** now ship as release artifacts (`linux-x64` and `osx-arm64`). The remaining deployment-product gap is container registry publishing and deeper real-host soak, not raw daemon artifact production.

| Pending | Status |
|---|---|
| Binary rename: `theorc-daemon` → `theorc-warband` | ✅ Done |
| CI publish matrix for `linux-x64` + `osx-arm64` Warband binaries | ✅ Shipped 2026-06-24 (`.github/workflows/release.yml` `warband` job) — local cross-compile + YAML syntax validated only, no real GitHub Actions run yet |
| `warband.compose.yml` Docker template | ✅ Shipped 2026-06-24 (`docker/Dockerfile.warband` + `docker/warband.compose.yml`) — YAML/binary-placement validated only; a real `docker build`/`docker run` was attempted on HARDCOREPI but blocked by the safety classifier as too risky to that live production node, and local Docker Desktop's daemon never came up either. Still genuinely untested end-to-end. |
| GHCR/Docker Hub publish on release | ⬜ v2.0 |

### Daemon-centric HIVE — the v2.5 architectural changeover (vision, not started)

> Captured 2026-06-20 as a deliberate future direction. **Not** to be started as a
> refactor now — the current dual-path setup works and must not be broken first.
> Target window: ~v2.5, after the v1.9 multi-machine HIVE validation and the v2.0
> native-runtime-default work settle.

**The vision:** the Warband/Daemon becomes the *one canonical HIVE node*, running
on **every** machine — not just remote headless boxes, but the local desktop too —
as an always-on background service (Windows Service / systemd) that starts at boot
and survives the GUI being closed. The daemon is the privileged layer that owns the
machine-level handshakes, install/enrollment, peer trust, and (eventually)
hardware-level "control this PC" capabilities that the whole distributed-computing
vision depends on. **TheOrc's GUI becomes a client of its own local daemon** — a
dashboard that connects to manage/monitor HIVE, rather than building and running its
own separate in-process HIVE stack.

**Why this is the right end-state (not just convenient):**
- A machine can be a fully-participating HIVE worker with the GUI never opened —
  exactly what persistent worker nodes (HARDCOREPC, the laptop) need.
- It collapses today's **duplication**: right now the GUI (`MainWindow`) constructs
  its *own* `HiveNodeServer`/`HiveTaskQueue`/`HiveWorkerAgent`/`HiveBeacon`
  in-process, and `OrchestratorIDE.Daemon` has a *second, independent* copy of the
  same stack. Two non-cooperating implementations of "be a HIVE node," built from
  the same classes. The changeover makes the daemon the single implementation.
- It makes the daemon a genuinely **separable, shippable module** — the GUI depends
  on the daemon's interface, not the reverse.

**The hard requirement this forces (and the open design question):** once HIVE lives
in a separate always-on process (which is the whole point), the GUI — a different
process — can't share live objects with it (peer store, secrets, election state all
live in the daemon's memory). So the GUI must talk to the local daemon across the
process boundary. The **mesh/peer transport is already HTTP and stays HTTP** (it's
genuinely talking to other machines). The **GUI↔local-daemon *control* channel**
(settings, model selection, start/stop, peer enroll/revoke — operations a remote
peer must never reach) should almost certainly be a **strictly-local mechanism
(named pipe / localhost-only control socket), not the network-exposed HTTP port.**
That split is the core design decision to settle when this is scoped for real.

**Current integration gap (today):** the daemon has *zero* install or launch
wiring — no installer step deploys it, no GUI button starts/manages it. It's
complete, working code that nothing currently turns on. A near-term, low-risk
stepping stone (does NOT require the full refactor) is an "Install Warband (daemon)
as a service" action + the CI binaries above, so the daemon can at least be deployed
and run as a service before the GUI-as-client changeover happens.

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

### Native runtime function priorities

The next meaningful jump is not "more generation." It is giving the native runtime
the same high-value local function surface that makes modern chat tools useful in
practice, while keeping everything local-first and runtime-native.

Ranked by likely operator value:

| Rank | Function pack | Why it matters | Native-runtime direction |
|---|---|---|---|
| **1** | **Browser automation + page understanding** | Modern chat usage constantly crosses from text into websites, forms, docs, screenshots, and repeatable UI flows. Playwright already gives cross-browser automation, isolation, parallelization, screenshots, and trace/report tooling across Windows, Linux, and macOS. | Make browser control a first-class native tool surface for OrcChat and headless loops: navigate, click, type, extract DOM/text, capture screenshots, download files, and return structured evidence. |
| **2** | **Image intake, OCR, and multimodal message handling** | A chat surface that cannot see screenshots, scans, whiteboards, or charts is behind current operator expectations. Tesseract provides local OCR by CLI/API, and LLamaSharp is explicitly positioned for local LLM/LLaVA-style execution. | Add attachment routing that can choose OCR-only, multimodal-native, or OCR+reasoning pipelines based on the selected model's capabilities. |
| **3** | **Workspace intelligence primitives** | Code and docs work live or die on fast local search, safe file reads, outlines, diffs, and precise edits. ripgrep remains the strongest cross-platform baseline for repository search because it is fast, recursive, and respects `gitignore`. | Promote workspace browse/search/read/outline/diff/apply operations into the shared native function pack rather than leaving them chat-surface-specific. |
| **4** | **Bounded shell / build / test execution** | Native chat does not replace real software work unless it can run builds, tests, formatters, and short diagnostics with approval and limits. This is the bridge from "assistant" to "project operator." | Standardize one runtime-owned execution surface with budgets, cancellation, streaming logs, exit codes, and replayable attestations. |
| **5** | **Document and artifact generation/export** | Real chat sessions need to hand back durable outputs: Markdown plans, HTML previews, docx/PDF exports, release notes, and handoff docs. Pandoc remains the strongest local format-conversion backbone for this. | Treat artifact generation as a native function family: create markdown, export to docx/pdf/html, and return clickable artifact refs instead of giant chat blobs. |
| **6** | **Structured extraction and typed result channels** | Tool use gets much more reliable when the runtime can return typed tables, schemas, metrics, and artifact refs instead of forcing everything through prose. | Expand shared tool/result contracts so browser, OCR, shell, and workspace tools all emit typed payloads plus compact human summaries. |
| **7** | **Capability-aware attachment and tool routing** | Operators should not need to memorize which model can see images, which worker has Playwright, or which node has the right runtime backend. | Let the native runtime advertise model/tool capabilities and route work accordingly, with explicit "cannot satisfy" outcomes instead of silent fallbacks. |

Recommended sequencing for v2.x:

1. Browser automation
2. Image/OCR ingestion
3. Workspace intelligence
4. Bounded shell/test execution
5. Artifact export and typed result polish

Why this order: it closes the biggest OrcChat parity gaps first, reuses the same
headless execution loop needed by Phase 3B campaign work, and keeps the focus on
functions that are hard to replace with a single SSH command.

Reference points: [Playwright docs](https://playwright.dev/docs/intro),
[Playwright screenshots](https://playwright.dev/docs/screenshots),
[Tesseract user manual](https://tesseract-ocr.github.io/tessdoc/),
[LLamaSharp](https://github.com/SciSharp/LLamaSharp),
[Pandoc](https://pandoc.org/),
[ripgrep](https://github.com/BurntSushi/ripgrep).

---

## Deferred — with rationale

| Feature | Status | Rationale |
|---|---|---|
| **Skills / plugin system** | Not started — no framework exists | Was a prompt concept; static C# tool definitions cover all current needs; revisit when there is a concrete extensibility use case |
| **GOBLIN MIND Phase 5 evolution UI (main app)** | Test-only | Low demand; fitness data is useful for debugging but not a user-facing daily feature |
| **Tool Editor hot-reload (Phase 6/7)** | Stub only | Roslyn pipeline is complex; pay-off unclear until tool definitions are more dynamic |
| **HIVE MIND C2 (RPC model chain)** | Groundwork laid | llama.cpp RPC plumbing exists; full SwarmSession routing to RPC workers not wired; blocked on Phase 3B |
| **"Zero idle chatter" message discipline** | Not implemented | Good spec hygiene; no user-visible impact currently; revisit when HIVE worker verbosity becomes a real problem |
| **Cross-platform desktop (Mac / Linux)** | WPF deleted 2026-06-20, Avalonia is the only desktop shell | macOS `osx-arm64` app/setup artifacts ship in v1.11.2; full Linux desktop publishing and real cross-OS hardware soak remain open. ScreenRecorder degrades gracefully (SharpAVI Windows-only). Warband (daemon) ships Linux/macOS artifacts — see WARBANDS above. |
| **On-platform self-improvement (TheOrc trains itself)** | **Shipped for the first Foundry track (v1.12.0)** | `theorc-toolcaller` r3 was trained on organic data captured from TheOrc's own real Swarm/OrcChat usage, adversarially evaluated, and promoted — see [THEORC_FOUNDRY.md](THEORC_FOUNDRY.md). Still gated by human review at every stage; no autonomous promotion. |
| **Reviewer adapter (local model trained to review TheOrc's own code, replacing Codex)** | PARKED 2026-06-13 — investigation concluded, build not started | B-3/B-4 baseline series proved prompt-engineering alone has a hard ceiling on `qwen2.5-coder:14b` (0/3 Codex catches); Stage 1 SFT needs off-machine compute that went to ORC ACADEMY training instead. Still considered valuable — not cancelled. See [`reviewer-adapter/00-index.md`](reviewer-adapter/00-index.md) for the resume plan and phase gate. |

---

## Reading Order

New to the project — read in this order:

1. [ARCHITECTURE.md](ARCHITECTURE.md)
2. [GLOSSARY.md](GLOSSARY.md)
3. [USER_GUIDE.md](USER_GUIDE.md)
4. [SWARM_GUIDE.md](SWARM_GUIDE.md)
5. [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md)
6. [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md)
7. [The Orc Context Fabric.md](The%20Orc%20Context%20Fabric.md)
