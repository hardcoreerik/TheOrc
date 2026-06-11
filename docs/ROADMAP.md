# TheOrc — Roadmap

> This roadmap reflects the current development state. Items move from Planned → In Progress
> → Stable as work completes. See the [GitHub issues](https://github.com/hardcoreerik/The-Orchestrator/issues)
> for the active backlog.

---

## v1.1 — GOBLIN MIND ✅ Complete

The Goblin Mind initiative teaches the swarm to understand itself at runtime.

- [x] **Phase 1: Behavioral Format Fingerprinting** — Probe each model's preferred tool-call
  format (OpenAI JSON / Hermes XML / bare JSON / Python-style / YAML). Store as `FormatFingerprint`.
  `AgentLoop` shapes tool schemas to the model's native format.
- [x] **Phase 2: Category Boundary Mapping** — 14-query capability taxonomy per model
  (7 categories × 2 tests). Task routing is gated on actual model capability.
- [x] **Phase 3: Adaptive Schema Generation** — Confirmed tool schemas per model.
  Few-shot bootstrapping from successful probe outputs.
- [x] **Phase 4: Schema Reduction Middleware** — Transparent `AgentLoop` middleware that
  simplifies schemas for models that fail on complexity.
- [x] **Phase 6: Steering Integration** — Boss reads capability profiles to steer the swarm.
  Task routing is capability-driven, not config-driven.
- [ ] **Phase 5: Evolutionary Schema Search** — On-demand mutation engine exploring schema
  space per model. CLI `tool-probe evolve` available; GUI integration pending.

---

## v1.2 — Swarm Tuning & Self-Improvement (Active)

Steering and correction are working. This milestone makes the swarm smarter through live
feedback loops and self-directed improvement.

- [x] **Steering test suite** — T11_SteeringTests (15 pure-logic NUnit tests) pins the
  capability-routing contract: unprobed models trusted, deficient primaries fall back
  with exact missing-category reporting, role requirements fixed (TESTER never needs
  FileOps), boss-prompt capability map verified. Routing extracted to SwarmSteering
  for testability. Built 2026-06-11
- [x] **Live capability badges** — Swarm Board shows mode | format | categories | schema |
  probe-age per model slot, with "Probe Now" button opening the probe window. Built 2026-06-10
- [x] **Fitness map GUI** — ToolCallTestWindow "Evolution" tab lists fitness records
  (variants, win rate, best mutation); Promote Winners saves to SchemaLibrary. Built 2026-06-11
- [ ] **Self-improve loop** — GitHub issue scanner → Agent panel injection → TheOrc proposes
  and applies fixes to itself via source clone
- [x] **GOBLIN HARVEST — autonomous dataset farming** — swarmcli batch runner
  (`farm_batch.ps1`) → deterministic rubric rejections (`prescreen_captures.py`:
  wrong-stack, TESTER-write, single-task, low-rubric) → local judge-model triage
  (`judge_captures.py`, qwen2.5-coder:14b — never the boss model judging itself) →
  human approves only final train candidates. Built 2026-06-11.
- [x] **NIGHT HARVEST — train till dawn** — `night_harvest.ps1` loops the full GOBLIN
  HARVEST pipeline overnight: a local model authors fresh goal tranches from
  PROMPT_AUTHORING_GUIDE.md (`generate_goals.py`, linted + deduped in code), farms,
  pre-screens, judge-triages; ends at dawn, after -Hours, -UntilStopped, or via the
  .orc/swarm/HARVEST_STOP file. Never approves, never trains. First live run
  2026-06-10→11: 34 cycles · 850 goals farmed · 608 survivors · 162 auto-rejected,
  zero crashes over ~9 h unattended
- [x] **Parallel slots live gate** — `OllamaParallelHelper` detects user/machine/process
  env; swarm start blocked below 3 slots with fix-it message + slot picker; settings
  panel writes and reports the value (was already complete; roadmap entry was stale)
- [x] **Wire `TotalVramGb`** in SwarmSession — DetectHardwareAsync() runs at swarm
  start and feeds run metrics (was already complete; roadmap entry was stale)
- [x] **In-app Help window** — F1 / Help menu opens an embedded documentation
  browser: all 17 guides ship inside the exe (works on published installs with
  no docs/ folder), full-text search, cross-guide link navigation. Built 2026-06-10

---

## v1.3 — Cross-Platform (Docker + Blazor) (Planned)

> The Avalonia port is parked. Docker + Blazor Server ships cross-platform faster and avoids
> porting 15+ WPF panels.

- [ ] ASP.NET Core API backend wrapping AgentLoop + ToolRegistry
- [ ] Blazor Server UI — same feature set as WPF app
- [ ] Docker image: llama.cpp + backend server in one container
- [ ] macOS Metal build of llama.cpp bundled
- [ ] Linux AppImage / `.deb` packaging

The WPF app remains the primary Windows-native experience indefinitely.

---

## v1.4 — Backlog (Future)

- [ ] Inline diff editing (edit proposed diff before approving)
- [ ] Background agent (fire task, get notified when done)
- [ ] Token cost estimator
- [ ] Multi-workspace support
- [ ] SwarmBoard metrics history tab (ConfigStats per configuration)
- [ ] Model Wiki: model comparison view (side-by-side)
- [ ] Model Wiki: historical result trends chart
- [ ] Model Wiki: export capability matrix to Markdown
- [ ] Model Wiki: filter chips for GOBLIN MIND category scores
- [ ] Model Wiki: "Probe Now" button in detail pane

---

## Training Pit Phases (Separate Track)

The Training Pit is on its own milestone track, not tied to app version numbers.

| Phase | Status | Description |
|---|---|---|
| Phase 1 | ✅ Done | Scaffolding — schemas, rubrics, configs, scripts |
| Phase 2 | ✅ Done | Data collection — auto-capture via DatasetCapture.cs |
| Phase 2.5 | ✅ Gate met | Dataset Accumulation — ALL GATES MET 2026-06-11 (163/150 train ✅ · 20/20 eval ✅ · 25/25 negative ✅); farming continues toward ~1,000 |
| Phase 3 | 🟢 Authorized | Training — LoRA fine-tune on Gemma 4 12B QAT; preflight exits 0, not yet started |
| Phase 4 | 🔲 Future | Deployment — A/B path in SwarmSession |

**Phase 3 gate:** ≥150 reviewed positive examples + ≥25 negative examples + ≥20 eval.
**Long-term dataset goal (agreed 2026-06-10):** ~1,000 train / ~200 eval for a
production-quality adapter; 150 is the proof-of-concept starting line. Five-nines
reliability comes from the system (rubric + validation + retry), not dataset size.
See TRAINING_PIT_GUIDE.md "Dataset Size Targets".
Current count: **163/150 train ✅, 25/25 negative ✅, 20/20 eval ✅ — ALL GATES MET 2026-06-11.**
Run `python training_pit/scripts/review_captures.py --status` for live counters.
See `training_pit/BATCH_CAPTURE_PLAN_V2.md` for the active capture batch
(v1 plan is fully dispositioned).

See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for the full roadmap.

---

## Current Stable Features

| Feature | Status |
|---|---|
| WPF shell, file explorer, code editor | ✅ Stable |
| Ollama + llama.cpp inference | ✅ Stable |
| Model selection and profiles | ✅ Stable |
| Single-agent Plan → Execute + approval gates | ✅ Stable |
| Git auto-checkpoint | ✅ Stable |
| GOBLIN MIND tool-call probing (CLI + GUI) | ⚠️ Beta |
| Goblin Swarm multi-agent routing | ⚠️ Beta |
| Self-improve / Scan GitHub loop | 🔬 Experimental |
| Hot-load C# tools (Roslyn) | 🔬 Experimental |
| llama.cpp direct backend | 🔬 Experimental |
| FlaUI UI automation suite | 🔬 In progress |
| CI / release automation | 🔲 Planned |

---

## How to Contribute

See [INSTALLATION.md](INSTALLATION.md) for build instructions.

For feature requests and bug reports, open a GitHub issue.
For hardware sponsor opportunities, see [SPONSOR_TEST_LAB.md](SPONSOR_TEST_LAB.md).
