# TheOrc — Project Truth Document
# Consolidated from all roadmap, architecture, and planning .md files
# Purpose: Give Grok a single-file reference to hold the project accountable
# Sources: README.md, docs/ROADMAP.md, docs/ARCHITECTURE.md,
#          training_pit/ARCHITECTURE.md, training_pit/DATASET_STRATEGY.md,
#          training_pit/ROLE_ARCHITECTURE.md, docs/sql-migration/00_ROADMAP.md
# Generated: 2026-06-17

---

## BLOCKER (2026-06-19, overnight autonomous session) — Codex CLI non-interactive task dispatch produced no output

Per the user's instructions, directed Codex CLI at ~04:00 to do research + in-app docs work
(separate track from the Native Runtime loop, deliberately scoped away from
`OrchestratorIDE/Core/Runtime/` to avoid collision). Built `Tools/codex-task.ps1` — a
write-enabled sibling to the existing read-only `Tools/codex-review.ps1`, using the same
proven exe-location + stdin-piping pattern, with `--sandbox workspace-write` (verified via
`codex exec --help`, not guessed — confirmed valid values are `read-only` / `workspace-write`
/ `danger-full-access`).

**Result: the dispatch completed with exit code 0, no error on stdout/stderr, but produced a
completely empty result and made zero file changes / zero commits.** Raw evidence (full
captured stdout, the script's own banner line is the only output produced):
```
dispatching codex task (sandbox=workspace-write, timeout=2400s)...



saved -> F:\Ai\OrchestratorIDE-dev\.orc\tasks\codex_20260619_035922.md
```
`git log --oneline -5` and `git status --short` immediately after showed the same 5 commits
from this session's own Native Runtime work and no new untracked/modified files from Codex —
i.e. nothing from Codex at all, not even a partial or malformed change. The same exe + stdin
pattern works correctly for `codex-review.ps1` (read-only review mode, used successfully many
times this session), so the difference is specifically in `workspace-write` mode behavior for a
multi-part, open-ended task prompt — not a wrong flag name or a broken stdin pipe (those would
typically surface as a hang or a visible error, not a clean empty exit).

**Per explicit instruction, not retried or guessed further.** Plausible causes, not verified:
non-interactive `workspace-write` mode may require an additional approval/config step that
differs from `read-only` review mode; the task prompt's two-part open-ended scope (research +
docs) may not suit a single non-interactive shot the way a bounded diff-review prompt does;
or there may be an auth/session state issue specific to unattended invocation. `Tools/codex-task.ps1`
is committed and reusable for a future attempt with a narrower, single-action prompt and closer
output inspection (e.g. checking `.orc/tasks/codex_*.md.last` before it's deleted, or running
foreground instead of backgrounded to see live output) — but that diagnosis is for the user or
a future session, not something to keep attempting unattended overnight.

The Native Runtime loop (tasks #8-#17, this same session) was unaffected and continued normally.

---

## 1. CORE IDENTITY — What This Project Claims to Be

**TheOrc** is a local-first, cloud-optional AI coding orchestrator for Windows.
It receives a user goal, decomposes it into parallel tasks, and dispatches each
to a specialist AI agent (goblin worker). The user approves every file write
and shell command before it executes. Core inference, models, data stores,
training, and orchestration run entirely on infrastructure you control — no
API key or subscription is required for any of that. Optional, explicit,
operator-controlled network features exist (web search, URL fetch, Cerebras
dataset generation, GitHub update checks, HIVE/Tailscale remote nodes); none
of them are silent defaults, and using none of them keeps the whole app fully
offline.

**The warband:**
| Role | What they do |
|---|---|
| TheOrc (Boss) | Reads goal → writes structured JSON plan → routes tasks to workers |
| Researcher | Investigates APIs/docs/libs — NO write access |
| Coder | Writes implementation code |
| UIDeveloper | Writes UI code (XAML, HTML/CSS) |
| Tester | Runs tests and reads logs — NO write access |

**Core promise from README:**
> "TheOrc receives a goal, breaks it into parallel tasks, and sends each one to a
> specialist AI agent. Everything runs on your machine."

**Monetization commitments made:**
- AGPL-3.0 open source + commercial dual-license option
- Ko-fi / PayPal / GitHub Sponsors support links published
- Gumroad adapter marketplace: "TheOrc Boss: Planner Edition" at $9
  (`hardcoreerik.gumroad.com/l/bqlzrm`) — v1 adapter (125 MB GGUF LoRA)

---

## 2. WHAT HAS SHIPPED — Version History

### v1.0 — Core shell, swarm, GOBLIN MIND
- Single-agent AgentLoop (plan-only review, execute mode, git checkpoints)
- Tool calls via ToolRegistry with approval flow; four trust tiers framework
- Four worker roles: RESEARCHER, CODER, UIDEVELOPER, TESTER
- Swarm Board with capability badges; token cost estimator
- GOBLIN MIND: format fingerprinting (5 variants), category mapping (7 categories),
  schema library, adaptive schema generator, evolutionary fitness storage
- Model Wiki / Lab: catalogue, comparison, probe-now, historical trends
- Built-in screen recorder (F12, SharpAVI MJPEG); FlaUI test auto-record

### v1.2 — Training Pit, NIGHT HARVEST, GOBLIN HARVEST
- DatasetCapture.cs auto-captures boss plans after every swarm run
- Manifest-driven review pipeline (reviewed_v1.json)
- phase3_preflight.py — programmatic gate (9 checks)
- Training Pit panel in app; gate progress cards
- Phase 3 gate met: 163/150 train / 20/20 eval / 25/25 negative
- Autonomous overnight capture farming (NIGHT HARVEST / GOBLIN HARVEST)
- tools/codex-review.ps1 — reliable Codex CLI review (avoids stdin hang)
- swarmcli — headless console swarm runner

### v1.3 — HIVE MIND Phase A + ORC ACADEMY GUI + Model Wiki upgrades
- HIVE MIND Phase A: named Ollama host store, Tailscale-aware peer discovery,
  war-camp constellation panel, title-bar HIVE pill, installer enrollment step,
  node-aware model pickers
- ORC ACADEMY training GUI (Forge): dry-run, VRAM cap, checkpoints, resume,
  progress heartbeat, live VRAM meter
- **ORC ACADEMY v1 adapter SHIPPED (v1.3.3):**
  - 900 reviewed boss plans, 148-minute train, RTX 5070 Ti
  - **99.3% structured planning pass rate** vs 94.5% base model
  - Deployed as `theorc-boss:gemma4-ft` (125 MB GGUF LoRA)

### v1.4 — HIVE MIND Phase 3A (distributed swarm)
- HiveTaskQueue (port 7079): distributed work queue, 60s pending timeout, 45s heartbeat
- HiveWorkerAgent: polling, claiming, executing, heartbeating
- HiveScheduler: capability-aware routing (model presence, VRAM free, lane matching)
- Self-updater: checks GitHub releases, downloads .NET 10 SDK, clones/pulls, publishes
- Results Launch Pad: Run/Open Folder/Apply buttons, gate findings panel
- Uninstaller: registers in Windows Add/Remove Programs

### v1.5 — Pit Boss, SQLite, worktree isolation, reviewer gate
- **Pit Boss**: 8-question in-app interview → TrainingPlan → Cerebras/Ollama gen → Forge
- generate_cerebras_gold.py — Cerebras gpt-oss-120b synthetic generation (free tier)
- ~~generate_claude_gold.py~~ **ARCHIVED 2026-06-17** — violated no-Anthropic-bulk-gen
  and no-secrets-in-repo rules; use generate_cerebras_gold.py instead
- SQLite metadata layer: phases 0–3 (captures, triage, plans, runs, datasets tables)
- WorktreeManager: per-task git worktrees; TESTER/RESEARCHER exempt (read-only)
- FileOwnershipLedger: all-or-nothing TryClaim, no hold-and-wait deadlock
- Reviewer Quality Gate: Codex + local Ollama paths; Clean/Minor/Blocker verdicts
  **Current status: advisory only — user can always override BLOCKER**

### v1.6 — HIVE MIND security hardening, Update Center, fleet deploy
- HiveIdentity: P-256 ECDSA + ECDH per node; DPAPI-protected; NodeId = SHA-256(pubkey)
- HivePeerStore: DPAPI-wrapped HMAC secrets; trust store; enrollment ordering
- HiveAuthMiddleware: HMAC-SHA256 per-request; 30s clock skew; nonce replay cache; fail-closed
- HiveMeshHeartbeat: 30s signed heartbeat; dead-peer eviction at 3× interval
- HiveElectionService: Bully-style election; 5 message types; enrollment-order tie-break
- All 7 security findings from v1.6.1 audit fixed (election forgery, fail-closed auth,
  canonical injection, replay after restart, revocation race, liveness integrity, task-queue races)
- Update Center: version card, inline build log, GitHub release download, fleet deploy
- 51/51 HIVE security tests green; 112 headless unit tests pass

### v1.7 — Avalonia 12 cross-platform UI migration (Phases 0–5)
- Full Avalonia 12 project (net10.0, no -windows suffix) running side-by-side with WPF
- All panels ported: FileExplorer, Settings, Checkpoint, Session, Agent, Chat, Update,
  WarmUpEditor, Hive, PitBoss, SwarmBoard, TrainingPit, CodeEditor, ToolEditor (AvaloniaEdit)
- DiffViewer, ShellApprovalCard, UnknownToolCard, approval flow (Phase 4)
- MainWindow.axaml: full 4-row IDE layout; ~850 lines replacing 2,589-line WPF code-behind
- 121/121 unit tests green; Grok review CLEAN
- **Deferred to v1.8:** AgentBuilderDialog, ModelWikiWindow, LabWindow, ToolEditorDialog

### v1.8 — MarkdownView, Phase 7 tests, CodeGraph v1, Grok toolchain
- Avalonia MarkdownView (Phase 6): native renderer, zero new NuGet deps, streaming-safe
- Phase 7 test suite: 23 tests (8 WPF FlaUI + 10 Avalonia headless + T20 smoke)
- 142 automated tests green
- Pit Boss hardening: 10+ rounds, Hermes-3-Llama-3.2-3B default, injection prevention
- **CodeGraph v1 IMPLEMENTED** (commit f7fbe28) — Roslyn + SQLite code knowledge graph:
  - RoslynIndexer, ComplexityAnalyzer, GraphRepository (migration v5/v6)
  - 5 tools: graph_search, trace_path, get_architecture, detect_changes, graph_adr
  - 11 T19 graph tests green; targeting v1.9 release
- Grok toolchain integration: .grok/SKILL.md + config.toml + grok-review.ps1
- ORC ACADEMY v2 dataset: 1,784 train / 200 eval finalized
- **v2 adapter trained overnight**: 669 steps, 3 epochs, 333 min, eval loss 0.2595
- **ORC ACADEMY v2 A/B eval: REGRESSED** — v2 scored lower than v1 base
  - Root cause: 51% tester_poison in training data (write tasks assigned to TESTER lane)
  - 955 train + 112 eval examples routed to ORC ACADEMY v4 (Tester Worker) seed

### Current Work (2026-06-24, Linux/HARDCOREPI real-hardware verification)
- **Warband (`theorc-warband`) verified running for real on HARDCOREPI** (Raspberry Pi 4,
  ARM64 Linux, 1.8 GiB RAM, ssh alias `hardcorepi`) -- refreshed to current master/v1.10.0,
  paired, serving `/hive/info` correctly, ~82.5 MB RSS (the daemon itself is not what strains
  this box's RAM).
- **Real bug found+fixed**: `HiveWorkerAgent` had no way to configure its coder/researcher
  Ollama model on a headless box -- it only ever read the GUI's `settings.json`, which a
  Warband never has, so any dispatched task would throw `"Worker: no model configured"`.
  Added `DaemonConfig.CoderModel`/`ResearcherModel` (`Hive:CoderModel` /
  `HIVE__CODERMODEL` env var). Verified live via systemd `Environment=` + log line.
- **Real bug found+fixed**: `LlamaCppResolver`'s "cpu"/"avx2" variant never matched any real
  Linux asset -- Linux's baseline llama.cpp build carries no "cpu" label in its filename
  (unlike Windows), so the CPU-fallback runtime download was silently broken for Linux on any
  architecture since this resolver shipped. Fixed with OS-conditional matching; verified live
  against the real GitHub API on real ARM64 hardware.
- **Real bug found+fixed**: `Setup/model-manifest.json`'s static llama.cpp fallback table was
  a flat, non-OS-keyed map -- a Linux box whose live GitHub API call failed would have
  silently received a Windows `.zip` filename as its "fallback." Restructured to be OS-keyed
  (windows/macos/linux, mirroring the `app` key's already-shipped pattern); verified live on
  real ARM64 hardware via a throwaway probe replicating the exact lookup logic.
- **Real, previously-unconfirmed positive finding**: the full `OrchestratorIDE.Avalonia` app
  (not just the daemon) cross-compiles clean for `linux-arm64`, INCLUDING a genuine
  LLamaSharp CPU-based native backend (`libggml*/libllama.so` resolve correctly after a
  proper RID-specific restore). Native Runtime's packaging/build side works on Linux ARM64 --
  only HARDCOREPI's own 1.8 GiB RAM ceiling blocks actually running it there; that's a
  hardware limit on this specific box, not a code gap. The app/installer have still never
  actually been launched on real Linux hardware, only build-verified.
- **Verified clean (no bugs found)**: `LinuxPlatformInstaller.DetectHardwareAsync` and its
  firewall-detection fallback both behave correctly on real ARM64 hardware (AVX2 correctly
  reports false, `RuntimeVariant` correctly resolves to `cpu`, XDG paths resolve correctly,
  firewall absence correctly detected and handled with no elevation prompt attempted).
- **Real architectural gap found, deliberately NOT worked around (a real open design
  question, not decided today)**: there is no way, at all, to dispatch a real task to a
  Warband's worker queue from anywhere -- `HiveTaskQueue` has no remote task-submission
  endpoint, `HiveWorkerAgent.WarchiefUrl` hardcodes to itself, and `swarmcli` (the tool that
  normally decomposes a goal and enqueues tasks) is Windows-only by design. A Warband can
  pair, report health, and (per the model-config fix above) is now correctly configured to
  execute a task if one ever reached it -- but nothing can make that happen yet.
- CI publish matrix + Docker template for the Warband binary also shipped today, both
  validated locally only (no real GitHub Actions run, no live container boot yet) -- see
  WARBANDS section below for full detail and the specific reason the live-Docker test is
  still blocked.
- Full iteration-by-iteration detail: `.grok/LINUX_TEST_PLAN_2026-06-24.md` (local-only, not
  git-tracked, same default-deny `.grok/` rule as everything except this file).

### Current Work (2026-06-17, post-v1.8)
- Suitability gate (suitability_gate.py) — pre-training contamination check; SHIPPED
- split_v2gold.py — routes v2gold into v3 boss and v4 tester buckets; SHIPPED
- **ORC ACADEMY v3 COMPLETE** (2026-06-17):
  - train_v3gold.jsonl: 906 boss-clean examples (0% tester_poison)
  - Gemma 4 12B, seed=42, lr=1e-4, 3 epochs, 156 min
  - rubric_pass_pct: 99.17% (checkpoint-best)
  - A/B eval: **FT 94.7% / Base 85.3%** — does NOT beat v1 99.3%
  - Root cause: `files_named` dimension — FT 65/87 vs base 74/87 (only failing dim)
  - All other dimensions perfect: valid_json 87/87, task_count_ok 87/87, roles_valid 87/87, no_tester_write 86/87
  - **Decision: v1 stays production. v3 not registered. v4 targets files_named gap.**
  - Results: `training_pit/outputs/lora_v3/ab_eval.json`

---

## 3. HARD RULES — Non-Negotiable Project Constraints

These rules were established and documented during the project. Grok must flag
any code or plan that violates them.

1. **NEVER use Anthropic API for bulk data generation.** Cerebras or Grok only.
   (generate_claude_gold.py archived 2026-06-17 for violating this.)

2. **NEVER put secrets in the repo.** No .env files, no API keys committed.
   CEREBRAS_API_KEY must be an environment variable only.

3. **Pit Boss model must be LOCAL ONLY.** No cloud models, no glm-4.6:cloud,
   no API calls from Pit Boss. Prefer ≤7B to spare VRAM for training.

4. **HIVE security: standard cryptographic primitives only.** No custom crypto.
   ECDSA P-256, ECDH X25519, HMAC-SHA256, AES-256-GCM only.

5. **Training data must pass the suitability gate** before training starts.
   suitability_gate.py runs automatically in train_lora.py before VRAM is allocated.

6. **Training Pit GUI is THE training workflow.** Datasets must follow the
   train_{KEY}/eval_{KEY} pairing convention for the Forge registry.

7. **Reviewer gate BLOCKER findings must not be silently bypassed.** (Currently
   advisory — hardening to true blocking is a v1.9 commitment.)

8. **SQL: parameterized queries only.** No string concatenation into SQL. Ever.
   (See sql-migration guardrail #3.)

9. **Training corpora stay as files.** JSONL datasets are never put in SQL.
   SQL is for operational metadata only.

---

## 4. ACTIVE / PLANNED — What Has Been Promised

### v1.9 Committed Scope

| Item | Source | Status |
|---|---|---|
| CodeGraph v1 release (already implemented) | ROADMAP | Code done, targeting v1.9 release |
| CodeGraphService lifecycle façade (background re-index) | ROADMAP | Not yet built |
| System prompt nudge for graph tools | ROADMAP | Not yet built |
| Avalonia remaining modal dialogs (AgentBuilderDialog, ModelWikiWindow, LabWindow) | ROADMAP | Deferred from v1.7 |
| ORC ACADEMY v3 A/B eval + adapter registration | This session | A/B done; registration blocked by files_named gap |
| HIVE MIND Phase 3B (multi-step tool calling on remote workers) | ROADMAP | Not started |
| Reviewer gate hardening — true blocking mode | ROADMAP | Advisory only today |
| Trust Tier config UI | ROADMAP | Framework exists; no Settings surface |
| Automated eval harness in Training Pit | ROADMAP | Manual only today |
| HIVE remote harvest + academy execution | ROADMAP | Needs Phase 3B first |
| train_lora.py defaults updated to v3gold/lora_v3 | This session | ✅ Done (commit 1735762) |

### WARBANDS — Cloud & Headless Deployment (naming formalized 2026-06-17)

Full spec: `.grok/WARBANDS.md`. The `OrchestratorIDE.Daemon` project IS the Warband. Binary rename: `theorc-daemon` → `theorc-warband` (done). One Warband per deployed headless node. The Warchief (GUI) stays home; Warbands run in Docker, on cloud VMs, or on LAN machines.

- Daemon is already `net10.0` + AES-256-GCM — cross-platform today
- Current Docker: Warband container + Ollama sidecar (2 containers)
- Post-Native-Runtime (Phase 2): Warband loads GGUF in-process, no sidecar (1 container)
- ORCISH TONGUE GBNF tool-call constraints work in-process on any model (post-Phase 2)
- **2026-06-24**: CI publish matrix and Docker template both shipped (see below) -- neither
  has had a real GitHub Actions run or a live `docker build`/`docker run` yet; both were
  validated locally only (cross-compile + YAML syntax checking for the CI job; YAML parsing +
  binary-placement check for the Docker template -- a real container boot on HARDCOREPI was
  attempted but blocked by the safety classifier as too risky to that live production node,
  and local Docker Desktop's daemon never came up to test there either). First real
  verification of either happens whenever someone with working Docker access, or the next
  real tag push, gets to it.

| Pending | Target |
|---|---|
| Binary rename (`theorc-warband`) | ✅ Done |
| CI linux-x64 / osx-arm64 Warband artifacts | ✅ Shipped 2026-06-24 (release.yml `warband` job) -- not yet run for real |
| `warband.compose.yml` template | ✅ Shipped 2026-06-24 (`docker/warband.compose.yml`) -- not yet run for real |
| GHCR/Docker Hub publish on release | v2.0 |

### TheOrc Native Runtime — v2.0 Direction (Phase 0-3 groundwork landed; first live path is opt-in)

Full spec: `docs/RUNTIME_PHASE0_SPEC.md`. An orchestration/swarm-aware layer **on top of LLamaSharp** (llama.cpp bindings) — NOT a from-scratch inference engine. Goal: drop the Ollama dependency, kill per-call reload + HTTP overhead + the `ollama create` merge step, and make the warband behave as one cohesive GPU mind.

| Phase | Scope | Status |
|---|---|---|
| 0 | `IModelRuntime` + `OllamaRuntime` (wrap existing `OllamaClient`); migrate one call site; zero behavior change | ✅ Landed |
| 1 | `LlamaCppServerRuntime` — wraps **existing** `LlamaServerManager` | ✅ Landed |
| 2 | `LLamaSharpRuntime` — in-process GGUF + LoRA; the "no Ollama" win | ✅ Prototype landed (LoRA apply still deferred) |
| 2.5 | Close abstraction leaks: `HiveWorkerAgent` + reviewer gate now use `IModelRuntime`; remote HIVE task-queue/node HTTP remains separate plumbing, not LLM inference | ✅ Closed |
| 3 | ModelDepot + SessionManager + AdapterManager (boss/worker/reviewer) + telemetry | 🔶 Live opt-in proof path landed — ModelDepot, SessionManager, AdapterManager, and `RuntimeOrchestrator` (wires all three from one shared runtime instance — review caught and fixed a mismatched-runtime risk in the first draft) landed, with the wiring logic itself Grok-CLEAN. `IRoleRuntime`/`NativeRoleRuntime` now expose the stack as a role-aware streaming surface; `THEORC_TEST_GGUF` drives both the existing `LLamaSharpRuntime` load/generate/dispose/repeat smoke lane and a new opt-in role-runtime smoke lane. Avalonia Settings exposes manual native smoke with explicit Ollama fallback plus local `.orc/runtime-fallback/` evidence capture, and `HiveWorkerAgent` has the first live native path: experimental opt-in only, role-mapped researcher/worker execution, logged fallback to configured `IModelRuntime`. §7 hot-swap spike closed empirically across 2 LoRA samples. Remaining: keep proving the real-model path, SessionManager/AdapterManager-backed telemetry, OrcScheduler wiring into AdapterManager, and no native default for main chat/research/SwarmSession yet |
| 4 | `OrcScheduler` — VRAM + lane-aware dispatch, pipeline boss→workers | ✅ Wired in (corrected 2026-06-24, verified by reading `RuntimeOrchestrator.EnsureAdmitted` directly, not assumed) — `TryAdmit` IS called on every role admission with generation-tagged per-role VRAM reservation accounting; the previous "not yet wired into AdapterManager/RuntimeOrchestrator" claim here was false, left stale across multiple sessions. Still no live GPU dispatch or pipeline queueing — admission is a pure decision function, not an executor. |
| 5 | Prefix KV cache (research, non-blocking) | ✅ Research closed — `Conversation.Fork()` is a real, cheap shared-prefix mechanism (confirmed via LLamaSharp's shipped XML docs), blocked for cross-role sharing since `SetLoraAdapters` is context-scoped not per-sequence; same-role prefix forking is a viable future win, see `.grok/PREFIX_KV_CACHE_RESEARCH.md` |

Key corrections vs the ChatGPT/Grok sketches (both written blind to the code): interface must carry **message history + tools + tool-call callback** (not single-prompt/no-tools); **there is no DI** — its introduction is a deliberate decision, not a Phase 0 assumption; the llama.cpp server bridge **already exists** as `LlamaServerManager`; LoRA hot-swap needs a verification spike before roadmapping. Ollama stays default/fallback until ModelDepot + installer are solid.

**Routing a live call site through the stack:** Stage 1 (`LLamaSharpRuntime` smoke
test against a real model) and Stage 2 (opt-in Settings test surface with explicit
Ollama fallback and evidence capture) landed. **Update 2026-06-24, corrected after
verifying actual code state (this paragraph's "do not generalize" instruction was
stale -- both things it said not to do are already shipped):** `HiveWorkerAgent`
opting into `NativeRoleRuntime` (the original Stage 3 decision) is still in place
unchanged. Main chat (`AgentLoop`) now ALSO has its own opt-in native path
(`AppSettings.ExperimentalNativeMainChatEnabled`, exposed via Settings UI on
`feat/native-runtime-orcchat`, GPU-verified for real: 67.7 tok/s on an RTX 4060) --
a separate toggle, separate `NativeRoleRuntime` instance, same `NativeWithFallbackRuntime`
fallback mechanism. Research/OrcChat took a different, complementary route to the same
"no Ollama" goal: it inherits the shared `OllamaClient`'s live `Backend` switch
(out-of-process `llama-server`, not in-process `LLamaSharp`), also now exposed via
Settings UI and verified end-to-end (single-turn and multi-turn). `SwarmSession`
remains on the configured default runtime -- genuinely not touched, not just
forgotten to update here.

**ORCISH TONGUE** (universal tool caller, formerly GOBLIN MIND — renamed to end the GOBLIN MIND / HIVE MIND collision; inventory in `.grok/RENAME_GOBLIN_MIND.md`, not yet applied to code). Native runtime is the substrate that upgrades it from prompt-layer format adaptation (probe + parse defensively) to **decoder-layer grammar-constrained tool calls (GBNF)** — valid by construction, works on any model even untrained-for-tools. This is the real "why native" capability, not just dropping the Ollama install. See `docs/RUNTIME_PHASE0_SPEC.md` §11.

### Promised from README "What's coming" section
| Item | Source |
|---|---|
| ORC ACADEMY v2 — smarter boss, broader goals | README |
| HIVE MIND distributed swarm across whole network | README |
| On-platform self-improvement (TheOrc trains itself) | README |
| Cross-platform (Mac / Linux) | README |
| Multi-GPU Windows rig support | README |
| AMD GPU (RX 7000 / RX 9000) compatibility | README |

### ORC ACADEMY Pipeline — Full Roadmap

| Milestone | Status |
|---|---|
| v1 adapter (boss planning, 900 examples, 99.3%) | ✅ SHIPPED |
| v2 adapter (1,784 examples) | ✅ TRAINED — REGRESSED (51% tester_poison data) |
| v2 regression root-cause identified | ✅ DONE |
| Suitability gate (blocks contaminated training) | ✅ SHIPPED |
| v2gold split into v3gold (clean) + tester_v1 (tester seed) | ✅ SHIPPED |
| v3 training run (906 clean boss examples, rubric-in-loop) | ✅ DONE — 156 min, rubric 99.17% |
| v3 A/B eval vs v1 99.3% baseline | ✅ DONE — **v3 94.7% / base 85.3%** — did not beat v1 |
| v3 adapter registration + Ollama deploy | ❌ BLOCKED — files_named gap (see below) |
| Gumroad marketplace: upload v3 adapter zip | ❌ BLOCKED — v3 not production |
| ORC ACADEMY v4 Boss (fix files_named gap, target ≥99%) | ⬜ Planned — add file-naming golden examples, retrain |
| ORC ACADEMY v4 Tester Worker (955+112 examples) | ⬜ Planned |
| Automated self-improvement loop (TheOrc trains itself) | ⬜ Future |

---

## 5. KNOWN GAPS / NEEDS POLISH — Committed But Incomplete

These are from the ROADMAP "Needs Testing" and "Needs Polish" sections.
They were shipped but have documented limitations.

| Area | Gap | Risk level |
|---|---|---|
| Reviewer Quality Gate | Advisory only — BLOCKER can always be clicked through | Medium |
| Trust Tier config surface | Framework wired, no Settings UI to configure it | Low |
| GOBLIN MIND Phase 5 evolution UI | Test-only, not in main app | Low |
| Tool Editor hot-reload | Stub only — Roslyn pipeline not implemented | Low |
| HIVE MIND timeouts | 45s/60s correct in unit tests but not tested with real network dropout | Medium |
| Unsloth trainer path | Not tested — PEFT fallback is the proven path | Low |
| HIVE MIND node dropout recovery | Queue re-queues correctly in tests; hard network failure untested | Medium |
| EVAL_RUBRIC auto-scoring | Defined but not wired to auto-scoring in SwarmSession | Medium |
| ARCHITECTURE.md | Pre-dates HIVE MIND Phase 3A and SQLite — stale | Low |
| TRAINING_PIT_GUIDE.md | Does not cover Pit Boss workflow end-to-end | Low |
| GOBLIN MIND → ORCISH TONGUE rename | Inventory in `.grok/RENAME_GOBLIN_MIND.md`; ~4 code symbols + ~50 display strings; not yet applied | Low |

---

## 6. DEFERRED — Explicitly Deprioritized (With Rationale)

| Feature | Rationale |
|---|---|
| Skills / plugin system | Static C# tool definitions cover all current needs |
| GOBLIN MIND Phase 5 evolution UI in main app | Low demand; data useful for debugging only |
| Tool Editor hot-reload (Roslyn pipeline) | Complex; payoff unclear until tool defs more dynamic |
| HIVE MIND C2 (RPC model chain) | Groundwork laid; blocked on Phase 3B |
| "Zero idle chatter" message discipline | No user-visible impact currently |
| Cross-platform desktop (Mac/Linux) | Avalonia shipped v1.7; WPF deleted 2026-06-20 (Avalonia-only now). **Linux update 2026-06-24**: Warband (daemon) verified actually running on real Linux ARM64 hardware (HARDCOREPI); full GUI app + installer are cross-compile-verified clean for linux-arm64 (including a real LLamaSharp native backend) but have never been launched on real Linux hardware -- HARDCOREPI's 1.8 GB RAM can't run the full Avalonia/Skia stack, a hardware limit on that box, not a code gap. macOS: still nothing verified on real hardware, no Mac available to test. |
| On-platform self-improvement | Gap is auto-generating and auto-judging training goals without human input |
| Per-role model differentiation within execution lane | Planned for Phase 4 — not yet scheduled |

---

## 7. TRAINING PIT — Documented Rules and Strategy

### Dataset source rules (from DATASET_STRATEGY.md)
- Tier 1 (target ~80%): Real TheOrc captures reviewed via review_captures.py
- Tier 2 (target ~15%): Hand-authored golden examples using exact BOSS_SYSTEM_PROMPT
- Tier 3 (~5%): Synthetic edge-cases for eval/negative only — NEVER in train
- Public datasets: **explicitly excluded** from boss training (wrong format, wrong distribution)
- Synthetic data: **never in train_v1.jsonl** — eval/negative only

### Phase 3 gate conditions (programmatic, in phase3_preflight.py)
- ≥150 reviewed positive train examples
- ≥25 negative/collapse examples
- ≥20 fixed eval prompts (never trained on)
- All data passes validate_dataset.py + sanitize_dataset.py
- suitability_gate: tester_poison ≤25%, no_valid_json ≤10%, task_overflow ≤5%
- Zero train/eval hash leakage

### Adapter activation gate (from training_pit/ARCHITECTURE.md)
An adapter is NEVER activated in TheOrc until:
1. Registered in adapters/registry.json
2. eval_status is "approved"
3. Corresponding entry exists in ModelProfiles.cs

### Role expansion policy (from ROLE_ARCHITECTURE.md)
Never add a role to BOSS_SYSTEM_PROMPT before the execution lane is ready.
Never remove a role from the alias map (additive only).
Add a new role to training data only after: lane implemented + role in BOSS_SYSTEM_PROMPT
+ ≥5–10 hand-authored golden examples reviewed.

### Current execution lanes
| Lane | Write access | Tools |
|---|---|---|
| RESEARCHER | NO | fetch_url, grep_code, get_outline, read_file, list_files |
| CODER | YES | write_file, read_file, run_shell, list_files, grep_code, fetch_url |
| UIDEVELOPER | YES | write_file, read_file, run_shell, list_files, fetch_url |
| TESTER | NO | run_shell, read_file, list_files |

### Future execution lanes (planned but not implemented)
- DOCS lane (logical: DOCS, DOCUMENTATION, TECHNICAL_WRITER)
- DEVOPS lane (logical: DEVOPS, RELEASE_MANAGER, INFRASTRUCTURE)
- REVIEWER lane (logical: REVIEWER, AUDITOR, SECURITY)
Gate: boss fine-tune reliably generates correct tasks + worker system prompt defined +
scheduling rules confirmed + ≥20 eval prompts exist

---

## 8. SQL MIGRATION STATUS

| Phase | Description | Status |
|---|---|---|
| 0 | Foundation (SqliteStore, migrations, repository base) | ✅ SHIPPED v1.5 |
| 1 | Captures + Triage tables; dual-write from DatasetCapture | ✅ SHIPPED v1.5 |
| 2 | Plans + Runs tables; plan history panel | ✅ SHIPPED v1.5 |
| 3 | Datasets registry index (*.jsonl stay canonical) | ✅ SHIPPED v1.5 |
| 4 | HIVE persistence (hive_tasks, hive_events, migration v4) | ✅ SHIPPED v1.6.2 |
| 5 | Cutover (flip canonical per-table, optional) | ⬜ Optional / future |

**Guardrail reminders:**
- One DB file, one owner process — remote nodes go through HTTP layer only
- Files canonical during transition — never delete a JSON file until SQL verified
- Parameterized queries only — no exceptions, ever
- JSONL training corpora stay as files — SQL is for metadata only

---

## 9. ARCHITECTURE SUMMARY

### Three core loops
```
[Avalonia Shell — Warchief] (WPF deleted 2026-06-20)
  └─ AgentLoop (single agent) / SwarmSession (multi-agent)
       └─ OllamaClient → local Ollama server
            └─ Tool calls: write_file, run_shell, read_file, fetch_url, etc.

[Learning Loop — The Training Pit]
  run → DatasetCapture → review_captures.py → train_lora.py → adapter → Ollama

[HIVE MIND — Distributed Swarm]
  Warchief (GUI node) → HiveTaskQueue (7079) → Warbands (headless nodes, polling)
  Each Warband = theorc-warband binary (formerly theorc-daemon); runs on LAN/cloud/Docker
  All requests HMAC-SHA256 signed; fail-closed; Bully election for Warchief
```

### Trust boundary
Everything that leaves Ollama (tool calls) passes through the ToolRegistry approval flow.
The user sees and approves every file write and shell command. This is non-negotiable.

### CodeGraph v1 (v1.9 target)
- RoslynIndexer + SQLite (migration v5/v6) + FTS5 in existing .orc/theorc.db
- 5 tools: graph_search, trace_path, get_architecture, detect_changes, graph_adr
- Goal: agent queries graph structure instead of grepping files; fewer exploration tokens

---

## 10. REVIEWER INSTRUCTIONS FOR GROK

When reviewing this project against this document, check:

1. **Promise vs delivery** — Is something in "planned" still unbuilt after 2+ versions?
2. **Hard rule violations** — Any code calling Anthropic API for bulk gen? Any secret in repo?
   Any Pit Boss model that's not local? Any custom crypto in HIVE?
3. **Stale documentation** — Does ROADMAP still claim features "in progress" that have shipped
   or regressed? Are "needs testing" gaps getting worse or better?
4. **Training data quality** — Is tester_poison rate tracked? Is the suitability gate running?
   Are synthetic examples going into train_v* sets (they should not)?
5. **Adapter activation gate** — Is any adapter in use without registry.json + eval_status=approved?
6. **SQL guardrails** — Any string concatenation into SQL? Any second process writing to theorc.db?
7. **Role expansion policy** — Any new role added to BOSS_SYSTEM_PROMPT without lane implementation?
8. **README accuracy** — Does the README still accurately describe the current state?
   (Check ORC ACADEMY section especially — v2 regressed, v3 complete but not promoted, v4 planned.)
9. **Deferred creep** — Are deferred items getting tacitly built without proper gating?
10. **New commitments** — Any new promises made in code comments, commit messages, or docs
    that aren't tracked in this document?

---

## 11. RESEARCH REVIEW — "Compiling Agentic Workflows into LLM Weights" (2026-06-19)

Reviewed `docs/research/no-orchestration-layer2605.22502v1.pdf` (Dennis et al., arXiv:2605.22502,
single unreviewed preprint, LLM-as-judge methodology with a documented judge-dependent ranking
shift in their own Appendix C — treat the headline numbers as suggestive, not settled). Three
external models (Grok, ChatGPT, DeepSeek) were asked how it applies to TheOrc; the analysis below
is the corrected/independent read, not a restatement of theirs.

**Core finding:** for *bounded, fixed-flowchart* procedures (14–55 nodes), fine-tuning a small model
on synthetic conversations generated by traversing the flowchart ("compiling the procedure into
weights") beats both an in-context frontier baseline and an external orchestrator (LangGraph) on
cost (128–462× cheaper) and reaches 87–98% of frontier quality with zero runtime orchestrator.

**What this validates (no architecture change needed):** ORC ACADEMY fine-tuning the boss to
produce structured plans is already this technique applied to one bounded step — goal → JSON plan
is a single-shot generation task, the closest TheOrc has to the paper's "compile the procedure"
move. The 30–50 min recompile cycle they measure on an 8×H200 cluster validates treating retraining
as routine, consistent with the project's existing "Training Pit is the workflow" philosophy
(§7 above).

**What this does NOT validate, despite two of the three model reviews recommending it:**
"merge the boss and worker swarm into one compiled model that outputs code/tool-calls directly,
skip the plan step." Rejected. TheOrc's plan → approve → execute split is not orchestration
overhead the paper's evidence speaks to — it is the mechanism behind the project's stated
non-negotiable trust boundary ("the user sees and approves every file write and shell command,"
§9 Trust boundary above). The paper's three domains (travel booking, Zoom support, insurance
claims) have no analogous "this action needs human sign-off before it executes" requirement, so a
model internalizing the whole procedure well enough to skip the plan step is, by construction, a
model that wants to skip the approval gate too. Do not revisit this without a separate, explicit
decision on how a compiled end-to-end agent would preserve per-action approval.

**Also rejected:** a recurring-subscription monetization pitch (one reviewer's "$20–50/month")
floated off the paper's cost numbers. Contradicts the README's stated core promise ("No API key.
No subscription.") and the actual monetization already in place — AGPL open source + the one-time
Gumroad adapter sale (§1). The paper's $/conversation savings are a self-hosted-vs-cloud-API
comparison; TheOrc has been self-hosted since v1.0 via Ollama, so most of that delta was already
captured before this paper existed — it is not new leverage unlocked by Native Runtime.

**Real, narrower opportunity flagged for later (not started, not committed):** Pit Boss's 8-question
interview flow is a genuinely bounded, fixed-shape procedure — structurally much closer to the
paper's travel-booking flowchart than the open-ended swarm is. If a real subterranean-agent
experiment is ever worth running here, that is the better-scoped candidate, not the boss-planning
step (already compiled, narrowly) and not the swarm (open-ended, trust-gated). Not scheduled.

**Worth tracking before any future deep multi-turn compilation attempt:** the paper's companion work
[Dennis et al., 2026b] found LoRA fails to internalize multi-step procedures — only full-parameter
fine-tuning closed the gap in their experiments. ORC ACADEMY v1 hit 99.3% using LoRA, which is not
a contradiction (boss-plan generation is single-turn structured output, shallower than tracking
state across dozens of turns) but is a flag: a future target with real multi-turn state-tracking
depth (e.g. a compiled Pit Boss interview agent) may need full fine-tuning, with the VRAM/training-
time cost that implies — plan hardware accordingly if that work is ever scheduled.
