# TheOrc — Roadmap

> Last updated: 2026-06-16 (v1.7.0 release).
> This document is updated after every GitHub release. It reflects actual code state, not aspirations — features marked Shipped have been verified in the running app.

---

## Where we are

TheOrc is a production local AI orchestrator. The core swarm, model intelligence, distributed HIVE MIND, and self-training loop are all shipped and running. The adapter trained by the v1 pipeline scores **99.3%** on structured planning evals. The v2 dataset pipeline is running now (Cerebras gold generation).

v1.7.0 ships the Avalonia 12 cross-platform UI migration. The WPF MainWindow (~2,589 lines) has been fully replaced by an Avalonia code-behind with identical service wiring. The build compiles, 121/121 unit tests pass headlessly. Native dialogs (Phase 4) are deferred to v1.8.

The honest gaps: the Reviewer Quality Gate is advisory-only (can always be overridden), the Tool Editor hot-reload is a stub, and HIVE MIND multi-step tool calling on remote workers is Phase 3B (not yet built). Native dialogs (`UserInputDialog`, `ToolEditorPanel` hot-load) are not yet ported to Avalonia. Everything else on the shipped list below is real, wired, and tested.

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
- Full execution pipeline: `PitBossService` kicks off dataset generation via Cerebras/Ollama/Claude API, hands off to ORC ACADEMY Forge
- Plan history landing page — every training run logged with status, target count, model, timestamp
- `generate_cerebras_gold.py` — Cerebras `gpt-oss-120b` synthetic generation (free tier, ~20 min for ~1,200–1,400 examples per 72-batch run); sleep paced to 5 req/min free-tier limit
- `generate_claude_gold.py` — Claude API fallback path

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

### ORC ACADEMY v2 dataset (finalized — 2026-06-16)
Cerebras pipeline complete: 72-batch run (1,458 examples, `gpt-oss-120b`, 19.8 min, zero API cost) + 217 Codex gold + 2,244 existing swarm captures = ~3,900 pre-review pool.

**Finalization done (`Tools/finalize_training_set.py`, rewritten for v2):**
- All 1,458 Cerebras + 217 Codex gold pass the structural + FILENAME-RULE gate (100%).
- Existing 2,244 captures **gate-filtered**: only 320 conform (1,924 dropped — they predate the FILENAME RULE, titles lack output filenames). This keeps ONE consistent convention across the set instead of diluting the rule v2 is meant to reinforce.
- Deduplicated by user-goal across the pool (11 collisions removed) → **1,984 conforming examples**.
- Stratified-by-language split: **1,784 train / 200 eval**, verified **zero goal overlap** (no train/eval leakage). Spot-check across languages passed.
- Outputs: `cerebras[api].synthetic.boss.1458.jsonl`, `codex[api].synthetic.boss.217.jsonl`, `train[mixed].merged.boss.1784.jsonl`, `eval[mixed].holdout.boss.200.jsonl`.

**Remaining:** Train v2 adapter on the 1,784-example train file (RTX 5070 Ti, QLoRA, ~148 min like v1); A/B eval against the 200-example holdout vs the v1 99.3% adapter.

### Documentation
Docs are being normalized around the current implementation. Key gaps:
- `ARCHITECTURE.md` pre-dates HIVE MIND Phase 3A and SQLite
- `TRAINING_PIT_GUIDE.md` does not cover the Pit Boss workflow end-to-end

---

## Planned — v1.8

### Avalonia Phase 4 — native modal dialogs
Port the remaining WPF-only dialogs to Avalonia: `UserInputDialog` (`ask_user` tool unblock), `AgentBuilderDialog`, `ModelWikiWindow`, `LabWindow`, `ToolEditorDialog` (Roslyn hot-reload). These were deferred from v1.7 because they require deeper WPF→Avalonia rewrites and don't block the core IDE flow.

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

## Deferred — with rationale

| Feature | Status | Rationale |
|---|---|---|
| **Skills / plugin system** | Not started — no framework exists | Was a prompt concept; static C# tool definitions cover all current needs; revisit when there is a concrete extensibility use case |
| **GOBLIN MIND Phase 5 evolution UI (main app)** | Test-only | Low demand; fitness data is useful for debugging but not a user-facing daily feature |
| **Tool Editor hot-reload (Phase 6/7)** | Stub only | Roslyn pipeline is complex; pay-off unclear until tool definitions are more dynamic |
| **HIVE MIND C2 (RPC model chain)** | Groundwork laid | llama.cpp RPC plumbing exists; full SwarmSession routing to RPC workers not wired; blocked on Phase 3B |
| **"Zero idle chatter" message discipline** | Not implemented | Good spec hygiene; no user-visible impact currently; revisit when HIVE worker verbosity becomes a real problem |
| **Cross-platform (Mac / Linux)** | Avalonia UI shipped v1.7; runtime testing on Mac/Linux pending | Avalonia 12 removes the WPF lock; actual cross-platform CI and packaging not yet wired; revisit after Phase 3B ships |
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
