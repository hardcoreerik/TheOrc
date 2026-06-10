# TheOrc — Documentation Index

> **Windows-first local AI coding and orchestration tool.**
> Runs 100% on your GPU. No cloud. No subscription.

---

## Getting Started

| Doc | What it covers |
|---|---|
| [QUICK_START.md](QUICK_START.md) | First run in 10 steps — Ollama, model, workspace, first agent run |
| [INSTALLATION.md](INSTALLATION.md) | Requirements, build from source, VS/VS Code notes, common setup problems |
| [USER_GUIDE.md](USER_GUIDE.md) | App concepts — modes, Plan/Execute, approvals, workspace, output |

---

## Features

| Doc | What it covers |
|---|---|
| [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md) | Single Agent mode, Plan vs Execute, write_file behavior, T06 |
| [SWARM_GUIDE.md](SWARM_GUIDE.md) | Goblin Swarm — Boss, Researcher, Coder, UIDeveloper, Tester roles |
| [MODEL_GUIDE.md](MODEL_GUIDE.md) | Model scores, role recommendations, local model behavior facts |
| [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) | Model Wiki / Lab window, capability test dialog, result schemas |

---

## Models and Hardware

| Doc | What it covers |
|---|---|
| [HARDWARE_GUIDE.md](HARDWARE_GUIDE.md) | VRAM tiers, expected model classes, NVIDIA/AMD/Intel notes |
| [SPONSOR_TEST_LAB.md](SPONSOR_TEST_LAB.md) | Hardware vendor and sponsor program — compatibility testing offer |

---

## Training Pit

| Doc | What it covers |
|---|---|
| [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) | What the Training Pit is, current phase status, Phase 3 gate |
| [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) | Capture → review → validate → promote workflow (Phase 2.5) |

---

## Development

| Doc | What it covers |
|---|---|
| [TESTING_GUIDE.md](TESTING_GUIDE.md) | Build commands, T07/T08 test filter, FlaUI requirements, diagnostics |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Real issues observed — Ollama, model, FlaUI, swarm, training |
| [DOCUMENTATION_STANDARD.md](DOCUMENTATION_STANDARD.md) | Required checklist for all future code and docs passes |

---

## Project

| Doc | What it covers |
|---|---|
| [ROADMAP.md](ROADMAP.md) | Current milestones, active work, backlog |
| [FAQ.md](FAQ.md) | Common questions about TheOrc, Ollama, Training Pit, models |

---

## Source Docs (training_pit/)

Detailed technical references live alongside the training pipeline:

| File | What it covers |
|---|---|
| `training_pit/README.md` | Training Pit phase status and file inventory |
| `training_pit/DATASET_STRATEGY.md` | Three-tier source strategy, Phase 3 gate conditions |
| `training_pit/ROLE_ARCHITECTURE.md` | Logical vs execution roles, alias map, dataset implications |
| `training_pit/MODEL_COMPATIBILITY.md` | Base model compat states, Gemma 4 12B architecture facts |
| `training_pit/HARDWARE_GUIDE.md` | Training-specific VRAM tiers, QLoRA config, WSL2 setup |
| `training_pit/ARCHITECTURE.md` | Full training system design |
| `training_pit/DATASET_SCHEMA.md` | Canonical chat-JSONL training format |
| `training_pit/EVAL_RUBRIC.md` | Plan quality and boss behavior rubrics |
| `training_pit/datasets/CONTRIBUTING.md` | How to add training examples |

---

*All docs describe the current implementation unless explicitly marked **Planned**.*
