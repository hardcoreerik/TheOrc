# 🗺️ TheOrc — Full Project Roadmap

> **Primary constraint (permanent):**
> You are not supposed to write the prompt test code.
> The swarm is supposed to complete test prompts.
> The models are capable enough. We are focused on building the internal methods
> that allow TheOrc (boss) to steer and correct.

---

## Guiding Principles

| Principle | What it means in practice |
|---|---|
| **Local-first, always** | No cloud dependency. Everything runs on your GPU. |
| **Trust-first** | Every file write shows a diff. Every shell command asks. The agent works; the user decides. |
| **Capability-driven routing** | TheOrc routes tasks by what models can actually do, not by config. |
| **Self-improving** | TheOrc reads its own issues, clones its own source, and can propose fixes to itself. |
| **No test code by us** | Test prompt results are produced by the swarm. Our job is the steering, correction, and routing machinery. |

---

## Release History

### ✅ v1.0 — Foundation *(2026-06-05)*

The core engine, WPF scaffold, and trust loop.

- Native C# + WPF .NET 10 — single `.exe`, ~600MB RAM, 2ms input latency
- Ollama streaming client (OpenAI-compatible `/v1/chat/completions`)
- AgentLoop — Plan → Review → Execute split
- DiffViewer — visual diff + Approve/Reject before any file write
- ShellApprovalCard — inline command approval, no MessageBox
- Git auto-checkpoint before every Execute run (LibGit2Sharp)
- Multi-tab AvalonEdit code editor with syntax highlighting
- File explorer, workspace guard, session save/restore
- Model picker flyout — click status bar to switch model live
- Model profiles — auto-selects best model for GPU on startup
- Context progress bar (blue/amber/red at 70%/85%)
- Ctrl+K command palette with fuzzy search
- `.agent.md` rules file — injected into every Plan + Execute system prompt
- Unknown tool cards — agent self-corrects or user implements inline

---

### ✅ v1.1 — GOBLIN MIND *(2026-06-08)*

Runtime model capability discovery. TheOrc now knows what each goblin can do.

See [`GOBLIN_MIND_TODO.md`](GOBLIN_MIND_TODO.md) for full task breakdown.

| Phase | What it does | Status |
|---|---|---|
| 1 — Format Fingerprinting | Probes 5 serialization formats (OpenAI/Hermes/bare/Python/YAML) per model | ✅ Done |
| 2 — Category Boundary Mapping | 14-query capability taxonomy (7 categories × 2 tests) — feeds swarm routing | ✅ Done |
| 3 — Adaptive Schema Generation | Confirmed tool schemas per model; few-shot bootstrapping from probe outputs | ✅ Done |
| 4 — Schema Reduction Middleware | Transparent simplifier in AgentLoop; zero friction for users | ✅ Done |
| 6 — TheOrc Steering Integration | Boss reads capability profiles; routing is capability-driven not config-driven | ✅ Done |
| 5 — Evolutionary Schema Search | On-demand mutation engine — CLI `tool-probe evolve` available; GUI integration pending | 🔲 Partial |

---

### ✅ v1.1.1 — Settings Overhaul + Self-Improve + Status Bar *(2026-06-08)*

- Settings toggle alignment fixed (WPF DockPanel child ordering)
- INSTALL section: 📁 install folder + 🗂 data folder one-click open
- **SELF-IMPROVE**: ⬇ Grab Source (git clone/pull) → 📂 Open in Agent → 🔍 Scan GitHub (fetches issues + commits, builds analysis prompt, injects into Agent panel)
- `tool-probe.exe` full CLI: `dispatch`, `format`, `categories`, `full`, `evolve`, `list`
- Status bar: 30px height, 12–13pt text, 15pt screenshot button, 12pt trust pills

---

## Active Milestone

### 🔬 v1.2 — Swarm Tuning & Steering Verification *(In Progress)*

> TheOrc's routing machinery is built. Now we run it against real tasks and verify the boss actually steers and corrects.

**The core loop we're proving:**
```
User prompt
  → TheOrc decomposes into subtasks
  → reads capability profiles for available models
  → routes each subtask to the model best suited for that category
  → monitors worker output
  → detects failure / wrong category / stalled worker
  → reformulates and reroutes
  → synthesizes final result
```

#### Swarm Steering Verification
- [ ] Run all 5 benchmark prompts (CleanCSV, LogAnalyzer, BugDex, GuardScan, MP3Player) across the 5 model combos in the test matrix below
- [ ] Observe: does TheOrc actually read category profiles before routing?
- [ ] Observe: does the boss reformulate when a worker fails its required category?
- [ ] Log: which combos succeed end-to-end vs stall vs produce wrong output
- [ ] Document failure patterns — is the failure in routing logic, worker prompt, or result synthesis?

#### Model Test Matrix (v1.2 target)

| Combo | Boss | Coder | Researcher | Notes |
|---|---|---|---|---|
| **A** | `qwen2.5-coder:14b` | `nemotron-3-nano:4b-q8_0` | `nemotron-3-nano:4b-q4_k_m` | Baseline — best proven combo |
| **B** | `gemma4:12b` | `nemotron-3-nano:4b-q8_0` | `nemotron-3-nano:4b-q4_k_m` | Gemma as boss — tests planning quality |
| **C** | `qwen2.5-coder:14b` | `qwen2.5-coder:7b` | `nemotron-3-nano:4b-q8_0` | Qwen worker — pure coding quality test |
| **D** | `gemma4:12b` | `qwen2.5-coder:7b` | `nemotron-3-nano:4b-q4_k_m` | Mixed: Gemma boss + Qwen coder |
| **E** | `qwen2.5-coder:14b` | `gemma4:12b` | `gemma4:12b` | Gemma as worker — quality vs speed |

#### Live Capability Badges (SwarmBoard UI)
- [ ] Per-slot capability display: Format | Categories | Schema Complexity | Last Probed
- [ ] "Probe Now" button per slot — runs GOBLIN MIND probe on selected model inline
- [ ] Colored dots per category (✅/⚠️/❌) with tooltip
- [ ] Badge persists after probing — shown on next launch

#### Fitness Map GUI (Phase 5 completion)
- [ ] "Evolution" tab in ToolCallTestWindow
- [ ] Shows mutation variants vs fitness scores per model
- [ ] High-fitness variants get "Promote" button → writes to SchemaLibrary
- [ ] "Run Schema Evolution…" in Models menu — dialog: pick model + seed tool + generations

#### Plumbing Fixes
- [ ] **Wire `TotalVramGb` in SwarmSession** — currently hardcoded 0; call `SwarmConfigAdvisor.DetectHardwareAsync()` at swarm init so auto-config recommendations are accurate
- [ ] **`OLLAMA_NUM_PARALLEL` live gate** — detect actual slot count before swarm start; block launch if slots < worker count with clear message + settings link
- [ ] **Model eviction verification** — confirm researcher model is actually evicted from VRAM before coder phase loads (serial confirmation from Ollama `/api/ps`)
- [ ] **`MODEL_PROFILES.md` auto-gen** — generate per-model capability summary doc from probe results; accessible from Models menu

#### Self-Improve Round-Trip (first real run)
- [ ] Clone TheOrc source into local working folder via Settings → Grab Source
- [ ] Open source clone as workspace in Agent panel
- [ ] Scan GitHub issues → review injected prompt → let model propose a fix → approve diff
- [ ] Verify the fix actually applies to the cloned source, builds clean
- [ ] Document: what issue did the model pick? Was the fix reasonable?

---

## Backlog Milestones

### 🎯 v1.3 — Agent Quality

Features that make individual agent sessions better, regardless of swarm.

- [ ] **Inline diff editing** — edit the proposed diff before approving it. Agent proposes, you tweak, then approve. Kills the "approve then manually fix" loop.
- [ ] **Background agent** — fire a task and get a notification when it's done. Don't wait at the keyboard. Results appear in a new session tab.
- [ ] **Token cost estimator** — show estimated token count + time before long runs. Prevents accidentally burning 60 minutes on a prompt that needs more context.
- [ ] **Session branching** — fork from any checkpoint. Try two different approaches from the same state without losing either.
- [ ] **Agent memory** — per-workspace memory file (`.orc/memory.md`) that the agent reads and updates across sessions. Knows the codebase after the first run.
- [ ] **Multi-file search tool** — `search_codebase(query)` using ripgrep. Agent can search its own workspace without reading every file.
- [ ] **`fetch_url` tool** — HTTP GET for documentation lookup. Agent can read a library's docs mid-session.

---

### 🍎 v1.4 — Mac / Linux Port

TheOrc is currently Windows-only (WPF). This milestone ports it to cross-platform.

- [ ] **Avalonia UI** — WPF → Avalonia 11 (same XAML dialect, minimal rewrite)
- [ ] **macOS Metal backend** — llama.cpp Metal integration; test on M-series Mac
- [ ] **Linux AppImage** — single binary, works on Ubuntu 22.04+ and Arch
- [ ] **Installer** — macOS `.dmg`, Linux `.AppImage` / `.deb`
- [ ] **Platform capability matrix** — verify all features work across Windows/macOS/Linux; document gaps

---

### ⚡ v1.5 — Cloud + Hybrid Backends

Optional cloud fallback for when local GPU isn't enough.

- [ ] **Cloud model support** — Anthropic Claude / OpenAI GPT-4 / Groq as selectable backends (user provides API key; stored locally, never sent anywhere else)
- [ ] **Hybrid routing** — use local model for cheap tasks (file ops, short coding), cloud for complex reasoning (architecture, long context)
- [ ] **Cost tracking** — real-time token cost display for cloud backends; budget limit setting
- [ ] **Context offload** — when local context window fills, offload summary to cloud model and continue locally
- [ ] **Bring-your-own-endpoint** — any OpenAI-compatible endpoint works (LM Studio, vLLM, Together.ai, etc.)

---

### 🔐 v1.6 — Pentest & Security Profile

TheOrc as a security-focused assistant.

- [ ] **Auto pentest mode** — detects pentest workspaces (`.pentest/` marker) and loads security-focused model + system prompt
- [ ] **Security tool registry** — `nmap_scan`, `nuclei_run`, `burp_request`, `subdomain_enum` tools
- [ ] **Report generator** — structured pentest report from session findings (CVSS scoring, remediation suggestions)
- [ ] **Reverse shell / payload safety gate** — explicit confirmation before generating any payload
- [ ] **RFID / Flipper Zero integration** — `.flipper` workspace support, Flipper script generation tools

---

### 🧬 v1.7 — Full Self-Improvement Loop

TheOrc improves itself, guided by you.

- [ ] **Issue triage agent** — reads GitHub issues, scores them by impact + effort, produces a prioritized fix list
- [ ] **Roslyn hot-load tool editor** — agent writes a new C# tool in the editor, "Register & Resume" compiles it at runtime and drops it into ToolRegistry. Zero restart.
- [ ] **Automated regression suite** — TheOrc runs its own swarm benchmark suite against a PR branch and reports regressions before merge
- [ ] **Schema evolution nightly job** — scheduled overnight run of `tool-probe evolve` on all installed models; results auto-promote to SchemaLibrary
- [ ] **TheOrc rates TheOrc** — boss model scores the swarm's output against the deliverable contract using the scoring rubric from the benchmark pack; result stored in RunHistory

---

## Things to Review / Debug (Immediate)

These are known issues or unverified assumptions from the current build:

| # | Area | Issue | Priority |
|---|---|---|---|
| 1 | SwarmSession.cs | `TotalVramGb` hardcoded to 0 — auto-config recommendations inaccurate | 🔴 High |
| 2 | Swarm routing | Category-aware routing written but **never tested with real prompts** — unknown if it actually routes correctly | 🔴 High |
| 3 | Model eviction | Researcher model eviction from VRAM is assumed, not confirmed. No `/api/ps` verification after eviction. | 🟡 Medium |
| 4 | GOBLIN MIND Phase 5 | `SchemaEvolution.cs` / `FitnessMap.cs` exist as CLI only — no GUI integration, no auto-promotion to SchemaLibrary | 🟡 Medium |
| 5 | SwarmBoard badges | Format/Categories/LastProbed per model slot not displayed — user has no visibility into what was probed | 🟡 Medium |
| 6 | Self-improve loop | Clone + scan + inject works. Round-trip (apply fix → build → verify) has never been exercised end-to-end | 🟡 Medium |
| 7 | OLLAMA_NUM_PARALLEL | Not validated at swarm start. If slots < workers, swarm silently degrades (workers queue) rather than blocking with a clear error | 🟠 Low-med |
| 8 | Activity log | CS8602 null-ref warnings in SwarmSession.cs lines 275, 341, 382 — non-fatal but should be cleaned up | 🟢 Low |
| 9 | Desktop shortcut | Points to old installed binary, not Release build. Users don't see new changes unless launched from bin/Release | 🟢 Low |

---

## Benchmark Test Matrix

Five model combos × five benchmark prompts. Run in this order; use BenchmarkRunner to log results.

### Model Combos

| ID | Boss | Coder | Researcher | Character |
|---|---|---|---|---|
| A | `qwen2.5-coder:14b` | `nemotron-3-nano:4b-q8_0` | `nemotron-3-nano:4b-q4_k_m` | Proven baseline — strong boss, fast workers |
| B | `gemma4:12b` | `nemotron-3-nano:4b-q8_0` | `nemotron-3-nano:4b-q4_k_m` | Gemma boss — tests creative planning vs Qwen |
| C | `qwen2.5-coder:14b` | `qwen2.5-coder:7b` | `nemotron-3-nano:4b-q8_0` | Qwen all-in — pure coding quality ceiling |
| D | `gemma4:12b` | `qwen2.5-coder:7b` | `nemotron-3-nano:4b-q4_k_m` | Mixed — Gemma plans, Qwen codes |
| E | `qwen2.5-coder:14b` | `gemma4:12b` | `gemma4:12b` | Inverted — Gemma as worker under Qwen boss |

### Benchmark Prompts

| ID | Name | Complexity | Tests |
|---|---|---|---|
| B1 | CleanCSV | ⭐ Easy | Delegation count, clear role split, file write |
| B2 | Log Analyzer | ⭐⭐ Medium | File I/O, pattern matching, CLI output |
| B3 | BugDex | ⭐⭐⭐ Hard | Data model, GUI, multi-agent coordination |
| B4 | GuardScan Security | ⭐⭐⭐⭐ Very Hard | Domain knowledge, report structure, tool use |
| B5 | Portable MP3 Player | ⭐⭐⭐⭐ Very Hard | Platform libs, GUI, audio pipeline |

### Scoring (per run)

| Dimension | Max | What to look for |
|---|---|---|
| Task decomposition | 20 | Did TheOrc split the task into logical roles? Were agents given distinct, non-overlapping work? |
| Tool call quality | 20 | Correct tool names? Right args? No hallucinated tools? |
| Steering & correction | 20 | Did TheOrc retry a failing worker? Reformulate a bad task? Catch a wrong output? |
| Output correctness | 25 | Does the produced code/file actually do what was asked? Does it compile/run? |
| Result synthesis | 15 | Did TheOrc assemble worker outputs into a coherent final deliverable? |
| **Total** | **100** | |

---

## Key Files

| File | Purpose |
|---|---|
| `ROADMAP.md` | This file — full roadmap and debug list |
| `PROJECT_LOG.md` | Session history, bugs fixed, design decisions |
| `dev-notes/GOBLIN_MIND_TODO.md` | Detailed phase breakdown for v1.1 capability discovery |
| `dev-notes/GOBLIN_MIND_UPDATES.md` | Research log and milestone tracking |
| `dev-notes/THE_ORC_HANDOFF.md` | Handoff notes for multi-machine setup |
| `dev-notes/RESEARCH_TOOL_TODO.md` | Research tool backlog |
| `Assets/theorc_swarm_complete_benchmark_pack.md` | Full benchmark pack — prompts, scoring rubric, deliverable contracts |
| `Assets/theorc_theswarm_repeatable_test_prompts.md` | Quick-start test prompts |
| `Tools/ToolCallTester/Program.cs` | `tool-probe.exe` — GOBLIN MIND CLI backend |
| `Tools/BenchmarkRunner/` | BenchmarkRunner GUI — logs and scores swarm test runs |

---

*TheOrc Roadmap — Updated 2026-06-09*
