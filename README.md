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

## What is this thing?

GitHub Copilot helps you write the next line. Cursor rewrites the current file. ChatGPT gives you code to paste.

TheOrc receives a **goal** — *"build a Python CSV cleaner with a GUI"* — breaks it into parallel tasks, and sends each one to a specialist AI agent. While you wait, a Researcher is reading the pandas docs, two Coders are writing separate files, and a UIDeveloper is setting up the README. When they're done, your workspace has the whole project.

Everything runs on your machine. No API key. No subscription. No code leaves your network.

It's basically a tiny software company that lives in your PC and does what you tell it. The staff are goblins. This is intentional.

---

## Meet the Warband

<div align="center">

![Goblin Swarm](Assets/goblin%20swarm.png)

</div>

TheOrc is the boss. He reads your goal, writes the plan, and keeps everyone pointed in the right direction. The rest of the swarm handles execution — in parallel, surprisingly fast, and with a work ethic that would shame most interns.

| Role | What they do |
|---|---|
| **TheOrc** | Reads your goal, writes the plan, routes each task to the right goblin |
| **Researcher** | Digs through docs, APIs, and libraries — never touches production code |
| **Coder** | Writes the actual implementation using whatever the Researcher found |
| **UIDeveloper** | Handles all the UI work — XAML, WPF, HTML/CSS, styles |
| **Tester** | Runs tests and reads logs — read-only, no write access, very trustworthy |

The boss model is a **fine-tuned local Gemma 4 12B** (`theorc-boss:gemma4-ft`) — trained by TheOrc's own pipeline on 900 reviewed swarm plans. It scores **99.3%** on structured planning evals. We made the AI smarter by feeding it examples of itself doing a good job. Yes, really.

---

## How it fits your day

<div align="center">

![TheOrc at work](Assets/badge1.png)

</div>

TheOrc runs **beside your IDE**, not inside it. Keep VS Code, Visual Studio, or whatever you're used to — TheOrc doesn't care. It just needs a folder to work in.

```
1. Point TheOrc at a workspace folder
2. Describe what you want built
3. Watch the swarm plan and execute in real time
4. Review every file and command before it lands — approve, reject, or redirect
5. Commit the result from your normal editor like nothing happened
```

Nothing gets written, no shell command runs, and no git operation executes without going through the approval flow you configure. You're always in the loop. The goblins are enthusiastic but not unsupervised.

---

## vs the tools you're already paying for

| | GitHub Copilot | Cursor | ChatGPT | **TheOrc** |
|---|:---:|:---:|:---:|:---:|
| Runs locally | ❌ | ❌ | ❌ | ✅ |
| Your code stays on your machine | ❌ | ❌ | ❌ | ✅ |
| Multi-agent parallel execution | ❌ | ❌ | ❌ | ✅ |
| Writes files autonomously | ❌ | Partial | Copy-paste | ✅ |
| Monthly cost | $10–19 | $20 | $20 | **$0** |
| Can train its own boss model | ❌ | ❌ | ❌ | ✅ |

TheOrc is not trying to replace your editor. It's the AI **project runner** that sits next to it and does the parts that were never fun to do yourself.

---

## ORC ACADEMY — the swarm teaches itself

Here's the part that gets genuinely weird in the best way.

Every good swarm run captures the boss's plan. Those captures go through a review pipeline. When you have enough reviewed examples, **ORC ACADEMY** trains a LoRA adapter on your own GPU. The new boss model is better at planning the next run. Which produces better captures. Which trains a better adapter. You get the idea.

**v1 shipped — June 2026:**
- 900 reviewed boss plans, harvested overnight by GOBLIN HARVEST while the PC sat idle
- LoRA trained locally in **148 minutes** on an RTX 5070 Ti
- Result: **99.3% structured planning pass rate**, up from 94.5% on the base model
- Shipped as `theorc-boss:gemma4-ft` — a 125 MB GGUF LoRA you can pull right now

The loop — *run → capture → review → train → deploy* — is part of the product. TheOrc is designed to get better the more you use it, entirely on your own hardware, with no data leaving your machine.

---

## Quick Start

### One-click installer

1. Grab `OrchestratorSetup.exe` from [Releases](https://github.com/hardcoreerik/TheOrc/releases)
2. The installer detects your GPU and walks through Ollama setup — it's pretty painless
3. Launch TheOrc, open a workspace folder, describe something you want built

### Build from source

```powershell
git clone https://github.com/hardcoreerik/TheOrc.git
cd TheOrc
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj
```

**Requirements:** Windows 10/11 · .NET 10 · [Ollama](https://ollama.com) · 8 GB VRAM minimum (16 GB recommended for running a full swarm)

### Grab a model and go

```powershell
# Recommended starting stack
ollama pull theorc-boss:gemma4-ft   # fine-tuned boss — 125 MB LoRA over Gemma 4 12B QAT
ollama pull qwen2.5-coder:14b       # coder workers — great speed/quality balance
```

> **No dedicated GPU?** TheOrc works with CPU-only Ollama, just slower. 7B coder models run fine at CPU speeds for most tasks. Give it a shot.

---

## Documentation

| | |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the shell, swarm, GOBLIN MIND, and Training Pit all connect |
| [USER_GUIDE.md](docs/USER_GUIDE.md) | Best place to start on day one — modes, approvals, workspaces |
| [SWARM_GUIDE.md](docs/SWARM_GUIDE.md) | How goals become plans and how to steer the swarm mid-run |
| [TRAINING_PIT_GUIDE.md](docs/TRAINING_PIT_GUIDE.md) | Capture → review → ORC ACADEMY training, step by step |
| [GLOSSARY.md](docs/GLOSSARY.md) | Every TheOrc term in one place — goblins, captures, manifests, all of it |
| [ROADMAP.md](docs/ROADMAP.md) | What's shipped, what's cooking, what's next |

---

<div align="center">

![Build Complete](Assets/release.png)

</div>

---

## Support the project + what's coming

TheOrc is free, open source, and always will be. If it saves you a subscription or two, consider throwing something in the jar:

<div align="center">

[![Ko-fi](https://img.shields.io/badge/Ko--fi-buy_a_coffee-FF5E5B?style=for-the-badge&logo=ko-fi)](https://ko-fi.com/hardcoreerik)
[![PayPal](https://img.shields.io/badge/PayPal-donate-003087?style=for-the-badge&logo=paypal)](https://paypal.me/hardcoreerik)
[![GitHub Sponsors](https://img.shields.io/badge/GitHub-sponsor-EA4AAA?style=for-the-badge&logo=githubsponsors)](https://github.com/sponsors/hardcoreerik)

</div>

Here's what's on the workbench — this is where support goes:

### 🧠 ORC ACADEMY v2
The v1 adapter was trained on 900 plans. v2 targets 2,000+ with broader goal coverage and trickier edge cases. Better data, smarter boss, faster swarm. The pipeline is already built — it just needs more runs and more review time.

### 🌐 HIVE MIND — distributed swarm across your whole network
HIVE MIND lets multiple TheOrc machines coordinate over your local network (via Tailscale). One machine runs the boss and hands off worker tasks to others. Your gaming rig does the planning, your NAS runs a coder, the old workstation in the corner finally earns its keep. The groundwork is in — Phase A is shipped. Phase B is distributed task execution across nodes.

### 🎓 On-platform self-improvement
The long game: TheOrc writes its own training goals, runs them through the swarm, and reviews the output as part of ORC ACADEMY. The pipeline already exists. The next step is closing the loop so the swarm can improve itself with minimal human input.

### 💻 Cross-platform
TheOrc is Windows-first right now (WPF/.NET). A cross-platform path — Mac and Linux — is on the roadmap once the core is mature. Ollama runs everywhere. The UI is the hold-out.

---

### 🖥️ We want your hardware

Seriously. HIVE MIND needs real multi-machine testing and TheOrc needs to prove it runs well on hardware beyond the dev rig. If you have any of the following gathering dust, get in touch — you'd be doing the warband a real favour:

| Hardware | What we'd test |
|---|---|
| **Multi-GPU Windows rig** | Distributed swarm with workers on separate GPUs |
| **AMD GPU (RX 7000 / RX 9000)** | ROCm + Ollama compatibility, full swarm on AMD |
| **High VRAM card (24 GB+)** | Larger model support, bigger context, faster worker throughput |
| **Low-spec machine (4–8 GB VRAM / CPU-only)** | Minimum viable swarm, small model combinations |
| **Second Windows machine (any spec)** | HIVE MIND Phase B — multi-node job routing |
| **Mac (Apple Silicon)** | Groundwork for the cross-platform path |

Drop a note in [Issues](https://github.com/hardcoreerik/TheOrc/issues) with the tag `test-lab` or reach out directly. Hardware contributors are credited in [docs/SPONSOR_TEST_LAB.md](docs/SPONSOR_TEST_LAB.md).

The goblins are grateful. They work for free but they do appreciate the compute.
