![TheOrc Banner](Assets/banner.png)

**TheOrc is a 100% local, multi-agent AI coding assistant for Windows**: a guarded coding shell, a coordinated "goblin swarm," and a built-in training pipeline that stays on your own hardware. It runs free local models, helps you plan before you act, and can turn reviewed swarm output into fine-tuning data for its own next adapter. If you want AI-assisted coding without giving up your repo, prompts, or workflow to a cloud subscription, that is the lane TheOrc is built for.

<div align="center">

[![Platform](https://img.shields.io/badge/platform-Windows-0B6DFF?style=for-the-badge&logo=windows)](https://github.com/hardcoreerik/TheOrc/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-6B38FB?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Mode](https://img.shields.io/badge/local-first-100%25_local-21C55D?style=for-the-badge)](#what-theorc-is)
[![Swarm](https://img.shields.io/badge/multi--agent-goblin_swarm-39FF6A?style=for-the-badge)](#whats-new-since-v120)
[![Training](https://img.shields.io/badge/training-ORC_ACADEMY-13E9B4?style=for-the-badge)](#training-pit--orc-academy)
[![Docs](https://img.shields.io/badge/docs-help_%2B_guides-8AFD66?style=for-the-badge)](#documentation)

[**Download Setup**](https://github.com/hardcoreerik/The-Orchestrator/releases) · [**Architecture**](docs/ARCHITECTURE.md) · [**User Guide**](docs/USER_GUIDE.md) · [**Training Pit**](docs/TRAINING_PIT_GUIDE.md) · [**Roadmap**](docs/ROADMAP.md)

</div>

## What TheOrc Is

The current product combines several layers in one repo:

- a WPF coding shell with approval-aware file and shell operations
- a local-first agent runtime that can stay single-agent or switch into swarm mode
- GOBLIN MIND model steering, so tool-call and routing behavior is based on observed capability instead of guesswork
- a Training Pit pipeline that captures good swarm plans, routes them through review, and feeds ORC ACADEMY for local QLoRA training

![Architecture hero slot](Assets/hero-architecture.png)
*Image slot: landing-page architecture overview for shell, swarm, GOBLIN MIND, and training flow.*

## What's New Since v1.2.0

The `v1.2.3` README was a strong base, but current `master` has moved beyond it in several visible areas.

### Training Pit Dataset Pipeline

The Training Pit is no longer just an idea or a notes section. Current source shows:

- a dedicated Training Pit panel in the app
- boss-plan capture and review workflow
- deterministic pre-screening via `prescreen_captures.py`
- second-pass judge triage via `judge_captures.py`
- human manifest decisions as the source of truth
- export and preflight tooling around the reviewed dataset

I am intentionally **not** putting a hard exported-dataset number in this README, because this merged worktree proves the reviewed manifest counts but does not currently contain the exported JSONL files.

### ORC ACADEMY In-App QLoRA Training

ORC ACADEMY is now the operator-facing Phase 3 training surface in the app. Verified in current source:

- the Training Pit UI labels the training area as `ORC ACADEMY`
- `train_lora.py` is the local QLoRA trainer entrypoint
- the panel exposes a VRAM cap
- training progress is heartbeat-driven
- the app can resume and re-attach after restart
- the panel includes a hang watchdog instead of assuming the trainer is healthy forever

### GOBLIN HARVEST And NIGHT HARVEST

Autonomous data farming is now part of the story:

- `prescreen_captures.py` is explicitly the first pass of **GOBLIN HARVEST**
- `judge_captures.py` is explicitly the second pass of **GOBLIN HARVEST**
- `night_harvest.ps1` runs autonomous overnight farming loops
- `Tools/harvest_marker_watch.ps1` can plant the stop marker once the target threshold is approached

### White-Paper Docs And In-App Help

The documentation layer is much stronger than it was at `v1.2.0`:

- the repo now includes [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) as the white-paper-style system overview
- [docs/GLOSSARY.md](docs/GLOSSARY.md) centralizes the project language
- the Help window is in the app and opens on `F1`
- guide links are routed inside the embedded Markdown help flow

### Capability Badges, Build Stamp, And Operator Clarity

Current `master` also adds several operator-facing quality-of-life surfaces:

- status-bar build stamp showing the running build and git suffix
- GOBLIN MIND capability badges under Swarm Board model pickers
- Model Wiki compare and export surfaces
- trends rendering in the Model Wiki detail view
- next-request token-cost estimation on the context badge

### HIVE MIND On Master

This is the biggest change versus the earlier README drafts: HIVE MIND is no longer just a distant spec item.

Verified in current `master`:

- installer-side **HIVE MIND enrollment** is present in `OrchestratorSetup/Services/HiveEnroller.cs`
- the installer flow includes a **Join HIVE MIND** option in `OrchestratorSetup/Pages/OllamaCheckPage.xaml`
- the main app now has a **HIVE panel** wired in `MainWindow`
- `OrchestratorIDE/UI/Panels/HivePanel.xaml.cs` implements the war-camp / constellation visualizer panel

What I am **not** claiming yet: full distributed remote-job execution as a finished shipped subsystem. The verified current state is enrollment, host groundwork, and a HIVE panel/visualizer on master, with broader distributed behavior still in active buildout.

![Pipeline hero slot](Assets/hero-pipeline.png)
*Image slot: landing-page view of capture, review, manifest, preflight, and ORC ACADEMY training.*

## Goblin Swarm

TheOrc can stay in a single-agent loop, but the product identity is the **Goblin Swarm**:

| Role | What it does |
|---|---|
| **TheOrc (Boss)** | Breaks down goals, routes work, retries weak outputs, and synthesizes the final result. |
| **Coder Goblin** | Handles implementation-heavy coding tasks. |
| **UIDeveloper Goblin** | Focuses on WPF, XAML, styles, and presentation layers. |
| **Researcher Goblin** | Investigates docs and APIs without writing production files. |
| **Tester Goblin** | Runs tests and inspects logs without `write_file` access. |

Current source also verifies that swarm model routing is no longer purely manual: `SwarmSteering` and the capability-badge plumbing use persisted GOBLIN MIND results to make those decisions more explicit.

## Training Pit & ORC ACADEMY

TheOrc is trying to close the loop between "used the product" and "improved the product."

That loop now looks like this:

1. Swarm runs capture boss plans.
2. The Training Pit pipeline triages and reviews those captures.
3. The reviewed manifest becomes the source of truth for exports.
4. ORC ACADEMY launches the Phase 3 local QLoRA training path.

What I can verify directly from current source:

- the manifest-driven review pipeline is real
- the in-app training surface is real
- the restart/re-attach behavior is real
- the trainer heartbeat and watchdog behavior are real

## HIVE MIND

HIVE MIND is now the `v1.3` headline priority for the landing page, because parts of it are already on `master` and the rest has a concrete shape.

Current verified state:

- installer enrollment exists
- the main app includes a HIVE button and HIVE panel
- the panel renders a war-camp / constellation style view for local and peer nodes

Still planned or in progress:

- broader discovery and roster maturity
- richer lane/job data from remote nodes
- distributed remote work such as farm, judge, and academy jobs across machines

![HIVE hero slot](Assets/hero-hive.png)
*Image slot: landing-page visual for HIVE MIND enrollment, peer nodes, and the war-camp panel.*

## Quick Start

### Option 1: One-click installer

1. Download `OrchestratorSetup.exe` from [Releases](https://github.com/hardcoreerik/The-Orchestrator/releases).
2. Let the installer detect your GPU and backend path.
3. Pick a coding profile and model fit.
4. Launch TheOrc and open a workspace.
5. Press `F1` once to confirm the embedded Help flow is available.

### Option 2: Build from source

```powershell
git clone https://github.com/hardcoreerik/The-Orchestrator.git
cd The-Orchestrator
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj
```

### First-run checklist

- start your local inference backend first
- try a small task in `Single` mode before moving to `Swarm`
- glance at the status bar for the active model and build stamp
- open the Model Wiki / Lab if you want to compare or probe models
- open the Training Pit if you are reviewing captures or running ORC ACADEMY

## Documentation

The docs suite is now part of the product, not an afterthought.

| Start here | Why |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | White-paper overview of the shell, runtime, GOBLIN MIND, swarm lifecycle, Training Pit, and HIVE direction. |
| [docs/GLOSSARY.md](docs/GLOSSARY.md) | Central vocabulary for TheOrc, goblins, ORC ACADEMY, GOBLIN HARVEST, captures, manifests, and HIVE terms. |
| [docs/USER_GUIDE.md](docs/USER_GUIDE.md) | Best day-one operator guide. |
| [docs/SWARM_GUIDE.md](docs/SWARM_GUIDE.md) | How goals become swarm work. |
| [docs/TRAINING_PIT_GUIDE.md](docs/TRAINING_PIT_GUIDE.md) | How swarm work becomes reviewed data and then a training artifact. |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Repo-verified shipped work, active work, and planned work. |

## Roadmap Snapshot

### v1.2: Landed Or Largely Landed

- Training Pit review pipeline
- ORC ACADEMY in-app Phase 3 training surface
- GOBLIN HARVEST / NIGHT HARVEST farming tooling
- white-paper docs suite and glossary
- Help window with `F1`
- capability badges and better operator status surfaces

### v1.3: HIVE MIND Priority

- installer enrollment and private-network setup
- HIVE war-camp visualizer panel in the app
- node discovery, roster maturity, and richer lane data
- distributed execution across multiple TheOrc machines

### Beyond

- cross-platform backend and UI path
- more model-intelligence surfaces
- deeper quality-of-life work for long-running autonomous use

## Support TheOrc

TheOrc is free, open source, and local-first. If it saves you a subscription bill, consider supporting the project:

- [Ko-fi](https://ko-fi.com/hardcoreerik)
- [PayPal](https://paypal.me/hardcoreerik)
- [GitHub Sponsors](https://github.com/sponsors/hardcoreerik)

Hardware vendors and test-lab contributors should also see [docs/SPONSOR_TEST_LAB.md](docs/SPONSOR_TEST_LAB.md).
