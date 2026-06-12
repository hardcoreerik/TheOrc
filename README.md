# TheOrc

TheOrc is a Windows WPF AI coding shell built for fully local operation: single-agent execution, a multi-lane goblin swarm, evidence-driven model steering, and an integrated Training Pit that turns reviewed swarm plans into adapter training data.

This repository is not just an app UI. It contains the execution runtime, the GOBLIN MIND model-profiling layer, the swarm orchestration loop, the embedded docs system, and the ORC ACADEMY training path.

## Architecture

```text
user goal
  -> MainWindow shell
  -> Single Agent or Swarm runtime
  -> ToolRegistry + approvals + sandbox rules
  -> GOBLIN MIND adapts tool-call format, schema, and routing
  -> run artifacts + metrics + plan captures
  -> Training Pit review/export/preflight
  -> ORC ACADEMY adapter training
```

Read the full paper in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Feature Matrix

| Area | Current state |
|---|---|
| Windows WPF shell | Implemented |
| Single-agent plan and execute loop | Implemented |
| Swarm boss plus four worker roles | Implemented |
| Capability-aware swarm steering | Implemented |
| GOBLIN MIND probe stack | Implemented |
| Swarm Board capability badges | Implemented |
| Swarm metrics history | Implemented |
| In-app Help window (`F1`) | Implemented |
| Model Wiki / Lab | Implemented |
| Model comparison view | Implemented |
| Trends strip and local capability history | Implemented |
| Token cost estimator | Implemented |
| Training Pit review flow | Implemented |
| ORC ACADEMY GUI with VRAM cap, heartbeat, resume, re-attach | Implemented |
| HIVE MIND distributed layer | Planned in spec |

## Quick Start

1. Start your inference backend, usually:

```powershell
ollama serve
```

2. Pull a model that fits your hardware, for example:

```powershell
ollama pull qwen2.5-coder:14b
```

3. Build or launch the app:

```powershell
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj
```

4. Open a workspace, press `F1` once to confirm the embedded docs viewer works, then run a small task in `Single` mode before moving to `Swarm`.

For the fuller path, read [docs/QUICK_START.md](docs/QUICK_START.md).

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
- [docs/GLOSSARY.md](docs/GLOSSARY.md)
- [docs/USER_GUIDE.md](docs/USER_GUIDE.md)
- [docs/SWARM_GUIDE.md](docs/SWARM_GUIDE.md)
- [docs/MODEL_WIKI_AND_LAB.md](docs/MODEL_WIKI_AND_LAB.md)
- [docs/TRAINING_PIT_GUIDE.md](docs/TRAINING_PIT_GUIDE.md)
- [docs/README.md](docs/README.md)

## Notes

- The current production shell is Windows-only.
- The Training Pit in this repository currently passes preflight with 900 approved train examples, 87 eval examples, and 25 negative examples.
- `docs/HIVE_MIND_SPEC.md` is a forward-looking spec, not a shipped subsystem.
