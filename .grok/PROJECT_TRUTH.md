# TheOrc — Project Truth Document
# Consolidated from all roadmap, architecture, and planning .md files
# Purpose: Give Grok a single-file reference to hold the project accountable
# Sources: README.md, docs/ROADMAP.md, docs/ARCHITECTURE.md,
#          training_pit/ARCHITECTURE.md, training_pit/DATASET_STRATEGY.md,
#          training_pit/ROLE_ARCHITECTURE.md, docs/sql-migration/00_ROADMAP.md
# Generated: 2026-06-17

---

## 1. CORE IDENTITY — What This Project Claims to Be

**TheOrc** is a 100% local AI coding orchestrator for Windows. It receives a
user goal, decomposes it into parallel tasks, and dispatches each to a
specialist AI agent (goblin worker). The user approves every file write and
shell command before it executes. No API key. No subscription. No code leaves
the machine.

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

### Current Work (2026-06-17, post-v1.8)
- Suitability gate (suitability_gate.py) — pre-training contamination check; SHIPPED
- split_v2gold.py — routes v2gold into v3 boss and v4 tester buckets; SHIPPED
- split output verified by Grok + Codex; all 3 Codex findings fixed
- **ORC ACADEMY v3 TRAINING IN PROGRESS** (launched 2026-06-17 17:31):
  - train_v3gold.jsonl: 906 boss-clean examples (0% tester_poison)
  - eval_v3gold.jsonl: 87 boss-clean examples
  - Suitability gate passed (0/906 poison, 0 leakage)
  - Gemma 4 12B, seed=42, lr=1e-4, 3 epochs, rubric-in-the-loop checkpointing
  - Step 10 logged: loss=3.679 — running normally

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
| ORC ACADEMY v3 A/B eval + adapter registration | This session | Training in progress |
| HIVE MIND Phase 3B (multi-step tool calling on remote workers) | ROADMAP | Not started |
| Reviewer gate hardening — true blocking mode | ROADMAP | Advisory only today |
| Trust Tier config UI | ROADMAP | Framework exists; no Settings surface |
| Automated eval harness in Training Pit | ROADMAP | Manual only today |
| HIVE remote harvest + academy execution | ROADMAP | Needs Phase 3B first |
| train_lora.py defaults updated to v3gold/lora_v3 | This session | ✅ Done (commit 1735762) |

### TheOrc Native Runtime — v2.0 Direction (planning only)

Full spec: `.grok/RUNTIME_PHASE0_SPEC.md`. An orchestration/swarm-aware layer **on top of LLamaSharp** (llama.cpp bindings) — NOT a from-scratch inference engine. Goal: drop the Ollama dependency, kill per-call reload + HTTP overhead + the `ollama create` merge step, and make the warband behave as one cohesive GPU mind.

| Phase | Scope | Status |
|---|---|---|
| 0 | `IModelRuntime` + `OllamaRuntime` (wrap existing `OllamaClient`); migrate one call site; zero behavior change | ⬜ Not started (after v3) |
| 1 | `LlamaCppServerRuntime` — wraps **existing** `LlamaServerManager` | ⬜ Planned |
| 2 | `LLamaSharpRuntime` — in-process GGUF + LoRA; the "no Ollama" win | ⬜ Planned |
| 3 | ModelDepot + SessionManager + AdapterManager (boss/worker/reviewer) + telemetry | ⬜ Planned |
| 4 | `OrcScheduler` — VRAM + lane-aware dispatch, pipeline boss→workers | ⬜ Planned |
| 5 | Prefix KV cache (research, non-blocking) | ⬜ Research |

Key corrections vs the ChatGPT/Grok sketches (both written blind to the code): interface must carry **message history + tools + tool-call callback** (not single-prompt/no-tools); **there is no DI** — its introduction is a deliberate decision, not a Phase 0 assumption; the llama.cpp server bridge **already exists** as `LlamaServerManager`; LoRA hot-swap needs a verification spike before roadmapping. Ollama stays default/fallback until ModelDepot + installer are solid.

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
| v3 training run (906 clean boss examples, rubric-in-loop) | 🔄 IN PROGRESS |
| v3 A/B eval vs v1 99.3% baseline | ⬜ Pending |
| v3 adapter registration + Ollama deploy | ⬜ Pending |
| Gumroad marketplace: upload v3 adapter zip | ⬜ Pending (user must upload) |
| ORC ACADEMY v4 (Tester Worker, 955+112 examples) | ⬜ Planned |
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
| train_lora.py defaults | Still point to train_v2gold.jsonl; should be v3gold | Low |

---

## 6. DEFERRED — Explicitly Deprioritized (With Rationale)

| Feature | Rationale |
|---|---|
| Skills / plugin system | Static C# tool definitions cover all current needs |
| GOBLIN MIND Phase 5 evolution UI in main app | Low demand; data useful for debugging only |
| Tool Editor hot-reload (Roslyn pipeline) | Complex; payoff unclear until tool defs more dynamic |
| HIVE MIND C2 (RPC model chain) | Groundwork laid; blocked on Phase 3B |
| "Zero idle chatter" message discipline | No user-visible impact currently |
| Cross-platform CI and packaging | Avalonia UI shipped v1.7; runtime testing on Mac/Linux pending |
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
[WPF/Avalonia Shell]
  └─ AgentLoop (single agent) / SwarmSession (multi-agent)
       └─ OllamaClient → local Ollama server
            └─ Tool calls: write_file, run_shell, read_file, fetch_url, etc.

[Learning Loop — The Training Pit]
  run → DatasetCapture → review_captures.py → train_lora.py → adapter → Ollama

[HIVE MIND — Distributed Swarm]
  Boss node (Warchief) → HiveTaskQueue (7079) → Worker nodes (polling)
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
   (Check ORC ACADEMY section especially — v2 regressed, v3 in progress.)
9. **Deferred creep** — Are deferred items getting tacitly built without proper gating?
10. **New commitments** — Any new promises made in code comments, commit messages, or docs
    that aren't tracked in this document?
