<div align="center">

![TheOrc Banner](Assets/banner.png)

[![Platform](https://img.shields.io/badge/platform-Windows-0B6DFF?style=for-the-badge&logo=windows)](https://github.com/hardcoreerik/TheOrc/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-6B38FB?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Local](https://img.shields.io/badge/100%25-local-21C55D?style=for-the-badge)](#quick-start)
[![License](https://img.shields.io/badge/license-MIT-39FF6A?style=for-the-badge)](LICENSE)
[![Release](https://img.shields.io/github/v/release/hardcoreerik/TheOrc?style=for-the-badge&color=13E9B4)](https://github.com/hardcoreerik/TheOrc/releases)

**You already use AI to write code. TheOrc is what happens when you let it run.**

[**Download**](https://github.com/hardcoreerik/TheOrc/releases) · [**Docs**](docs/ARCHITECTURE.md) · [**User Guide**](docs/USER_GUIDE.md) · [**Roadmap**](docs/ROADMAP.md)

</div>

---

## What it is

GitHub Copilot helps you write the next line. Cursor rewrites the current file. ChatGPT gives you code to paste.

TheOrc receives a **goal** — *"build a Python CSV cleaner with a GUI"* — breaks it into parallel tasks, and sends each one to a specialist AI agent. While you wait, a Researcher is reading the pandas docs, two Coders are writing separate files, and a UIDeveloper is writing the README. When they're done, your workspace has the project.

Everything runs on your machine. No API key. No subscription. No code leaves your network.

---

## The Goblin Swarm

<div align="center">

![Goblin Swarm](Assets/goblin%20swarm.png)

</div>

TheOrc is the boss. It decomposes your goal, assigns work, and keeps the agents honest. The swarm handles the rest in parallel.

| Role | What it does |
|---|---|
| **TheOrc** | Reads your goal, writes the plan, routes each task to the right specialist |
| **Researcher** | Reads docs, APIs, and libraries — never writes production code |
| **Coder** | Writes implementation files based on the Researcher's findings |
| **UIDeveloper** | Handles UI code, XAML, WPF, HTML/CSS, and styling |
| **Tester** | Runs tests and reads logs — no write access to your project files |

The boss model is now a **fine-tuned local Gemma 4 12B** (`theorc-boss:gemma4-ft`), trained by TheOrc's own pipeline on 900 reviewed swarm plans. It scores 99.3% on structured planning evals vs 94.5% for the base model.

---

## How it fits your workflow

<div align="center">

![TheOrc at work](Assets/badge1.png)

</div>

TheOrc runs **beside your IDE**, not inside it. You keep using VS Code, Visual Studio, or whatever editor you prefer.

```
1. Open a workspace folder in TheOrc
2. Describe what you want to build
3. Watch the swarm plan and execute
4. Review the output — approve, reject, or steer
5. Commit the result in your normal editor
```

You stay in control at every step. The approval-aware tool system means no file gets written, no shell command runs, and no git operation executes without going through the review flow you configure.

---

## vs the tools you're already using

| | GitHub Copilot | Cursor | ChatGPT | **TheOrc** |
|---|:---:|:---:|:---:|:---:|
| Runs locally | ❌ | ❌ | ❌ | ✅ |
| Your code stays on your machine | ❌ | ❌ | ❌ | ✅ |
| Multi-agent parallel execution | ❌ | ❌ | ❌ | ✅ |
| Writes files autonomously | ❌ | Partial | Copy-paste | ✅ |
| Monthly subscription | $10–19 | $20 | $20 | **Free** |
| Can train its own boss model | ❌ | ❌ | ❌ | ✅ |

TheOrc is not trying to replace your editor. It's the AI **project runner** that sits next to it.

---

## ORC ACADEMY — TheOrc trains itself

TheOrc closes the loop between using the product and improving it.

Every good swarm run captures the boss's plan. Those captures go through a review pipeline. When you have enough reviewed examples, ORC ACADEMY trains a LoRA adapter on your own GPU. The new boss model is better at planning the next run.

**v1 shipped (June 2026):**
- 900 reviewed boss plans harvested overnight via GOBLIN HARVEST
- LoRA trained locally in 148 minutes on an RTX 5070 Ti
- Result: **99.3% structured planning pass rate** — up from 94.5% on the base model
- Deployed as `theorc-boss:gemma4-ft` and distributed as a 125 MB GGUF LoRA

This loop — run → capture → review → train → deploy — is the core of what makes TheOrc different from a model wrapper. The pipeline is part of the product.

---

## Quick Start

### One-click installer

1. Download `OrchestratorSetup.exe` from [Releases](https://github.com/hardcoreerik/TheOrc/releases)
2. The installer detects your GPU and walks you through Ollama setup
3. Launch TheOrc, open a workspace folder, and run your first goal

### Build from source

```powershell
git clone https://github.com/hardcoreerik/TheOrc.git
cd TheOrc
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj
```

**Requirements:** Windows 10/11 · .NET 10 · [Ollama](https://ollama.com) · 8 GB VRAM minimum (16 GB recommended for swarm)

### Grab a model and go

```powershell
# Recommended starting stack
ollama pull theorc-boss:gemma4-ft   # fine-tuned boss (125 MB LoRA over Gemma 4 12B QAT)
ollama pull qwen2.5-coder:14b       # coder workers
```

> Don't have a GPU? TheOrc can run with CPU-only Ollama, just slower. 7B coder models work fine at CPU speeds for most tasks.

---

## Documentation

| | |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the shell, swarm, GOBLIN MIND, and Training Pit connect |
| [USER_GUIDE.md](docs/USER_GUIDE.md) | Day-one operator guide — modes, approvals, workspaces |
| [SWARM_GUIDE.md](docs/SWARM_GUIDE.md) | How goals become swarm plans and how to steer them |
| [TRAINING_PIT_GUIDE.md](docs/TRAINING_PIT_GUIDE.md) | Capture → review → ORC ACADEMY training walkthrough |
| [GLOSSARY.md](docs/GLOSSARY.md) | Every TheOrc term defined in one place |
| [ROADMAP.md](docs/ROADMAP.md) | What's shipped, what's active, what's next |

---

## Releasing

<div align="center">

![Build Complete](Assets/release.png)

</div>

---

## Support the project

TheOrc is free, open source, and local-first. If it saves you a subscription:

<div align="center">

[![Ko-fi](https://img.shields.io/badge/Ko--fi-support-FF5E5B?style=for-the-badge&logo=ko-fi)](https://ko-fi.com/hardcoreerik)
[![PayPal](https://img.shields.io/badge/PayPal-donate-003087?style=for-the-badge&logo=paypal)](https://paypal.me/hardcoreerik)
[![GitHub Sponsors](https://img.shields.io/badge/GitHub-sponsor-EA4AAA?style=for-the-badge&logo=githubsponsors)](https://github.com/sponsors/hardcoreerik)

</div>

Hardware vendors and test-lab contributors: [docs/SPONSOR_TEST_LAB.md](docs/SPONSOR_TEST_LAB.md)
