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

- [ ] **Steering test suite** — Test prompt suite verifying TheOrc correctly routes, retries,
  and corrects workers using capability profiles
- [ ] **Live capability badges** — Swarm Board shows Format | Categories | Schema Complexity |
  Last Probed per model slot, with "Probe Now" button
- [ ] **Fitness map GUI** — `tool-probe evolve` results in ToolCallTestWindow "Evolution" tab;
  high-fitness variants auto-promoted to SchemaLibrary
- [ ] **Self-improve loop** — GitHub issue scanner → Agent panel injection → TheOrc proposes
  and applies fixes to itself via source clone
- [ ] **Parallel slots live gate** — `OLLAMA_NUM_PARALLEL` detection; swarm start blocked if
  slots < worker count; settings panel shows live status
- [ ] **Wire `TotalVramGb`** in SwarmSession — currently hardcoded 0; call
  `SwarmConfigAdvisor.DetectHardwareAsync()` at swarm init

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
| Phase 2.5 | 🔵 Active | Dataset Accumulation — infrastructure complete; collecting captures (16/150 train · 4/20 eval · 7/25 negative) |
| Phase 3 | 🔴 Blocked | Training — LoRA fine-tune on Gemma 4 12B QAT |
| Phase 4 | 🔲 Future | Deployment — A/B path in SwarmSession |

**Phase 3 gate:** ≥150 reviewed positive examples + ≥25 negative examples + ≥20 eval.
Current count: **16/150 train, 7/25 negative, 4/20 eval.**
Run `python training_pit/scripts/review_captures.py --status` for live counters.
See `training_pit/BATCH_CAPTURE_PLAN.md` for the next planned capture batch (20 prompts).

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
