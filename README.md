<div align="center">

![TheOrc Banner](Assets/banner.png)

<br/>

[![Platform](https://img.shields.io/badge/platform-Windows-blue?style=for-the-badge&logo=windows)](https://github.com/hardcoreerik/TheOrc/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/license-MIT-brightgreen?style=for-the-badge)](LICENSE)
[![Backend](https://img.shields.io/badge/inference-llama.cpp%20%2F%20Ollama-orange?style=for-the-badge)](https://github.com/ggml-org/llama.cpp)
[![Models](https://img.shields.io/badge/models-HuggingFace_GGUF-yellow?style=for-the-badge)](https://huggingface.co/bartowski)
[![Sponsor](https://img.shields.io/badge/Sponsor-%E2%9D%A4-pink?style=for-the-badge&logo=github-sponsors)](https://github.com/sponsors/hardcoreerik)

<br/>

**TheOrc** is a native AI coding assistant that runs 100% on your hardware —
no cloud, no subscriptions, no data leaving your machine.
Point it at your GPU, pick a coding profile, and let it rip.

[**Download Setup**](https://github.com/hardcoreerik/The-Orchestrator/releases) · [**Documentation**](#getting-started) · [**Profiles**](#coding-profiles) · [**Supported Hardware**](#supported-hardware)

</div>

---

## What Makes TheOrc Different

| | TheOrc | Cursor / Copilot | Ollama + VS Code |
|---|---|---|---|
| Runs 100% locally | ✅ | ❌ | ✅ |
| No subscription | ✅ | ❌ | ✅ |
| Multi-agent Goblin Swarm | ✅ | ❌ | ❌ |
| Auto-selects best model for your GPU | ✅ | ❌ | ❌ |
| Guided one-click installer | ✅ | ✅ | ❌ |
| Plan → Review → Execute safety loop | ✅ | ❌ | ❌ |
| Diff approval before any file write | ✅ | ❌ | ❌ |
| Git auto-checkpoint before each run | ✅ | ❌ | ❌ |
| Profile-tailored agent rules (.agent.md) | ✅ | ❌ | ❌ |
| Hot-load custom C# tools at runtime | ✅ | ❌ | ❌ |
| Runtime tool-call compatibility probing | ✅ | ❌ | ❌ |

---

## Goblin Swarm — Multi-Agent Mode

<div align="center">

![Goblin Swarm](Assets/goblin%20swarm.png)

</div>

TheOrc doesn't work alone. Activate the **Goblin Swarm** and deploy a coordinated squad:

| Role | Responsibility |
|---|---|
| **TheOrc (Boss)** | Reads your goal, decomposes it into tasks, steers and corrects the swarm |
| **Coder Goblin** `</>` | Writes code, creates files, runs builds |
| **Researcher Goblin** `>_` | Searches the web, reads docs, answers context questions |
| **Ghost Researchers** | Parallel sub-agents spawned for deep research tasks |

Each goblin runs on a separate model you choose. TheOrc orchestrates — issuing tasks, reviewing outputs, retrying failures, and synthesizing the final result. You watch it happen in real time on the Swarm Board.

**Hardware-aware auto-config:** TheOrc reads your GPU, VRAM, and benchmark history to recommend the optimal model for each role — no guessing required.

---

## Features

### 🐉 Goblin Swarm — Multi-Agent Orchestration
Deploy a full squad: TheOrc (boss) breaks down your goal and routes tasks to specialized goblin agents in parallel. Automatic retry loops, quality scoring, and a real-time Swarm Board give you full visibility into what each agent is doing.

### 🔒 Trust-First Agent Loop
Every file write surfaces a **visual diff** — Approve or Reject before anything on disk changes. Shell commands show a **ShellApprovalCard** with the exact command to be run. You stay in control; the agent does the heavy lifting.

### 📋 Plan → Execute Split
The agent proposes a step-by-step plan for your review before executing a single tool call. Reject the plan, edit it, or approve — your call.

### ⚡ Local Inference via llama.cpp + Ollama
TheOrc bundles **llama-server.exe** directly — no Ollama required (though Ollama is fully supported). The installer detects your GPU and downloads the right CUDA/Vulkan/AVX2 runtime automatically.

### 🎯 GPU-Aware Model Selection
The installer reads your VRAM and picks the best model for your hardware — from 1.5B on a CPU-only machine all the way to 32B Q4 on a 24 GB card. The Swarm auto-config goes further: it recommends the optimal model *per role* based on your VRAM budget and observed run history.

### 🔬 Tool Call Compatibility Probing
Every model gets behaviorally tested before it's deployed. The **Tool Call Probe Engine** runs 5 deterministic tests × 2 dispatch modes, stores the results, and automatically routes tool calls through the correct path (native API or text-JSON). No more silent failures from model format mismatches.

### 📝 Coding Profiles
Eight tailored `.agent.md` rule sets — the agent knows your domain before you type a word.

### 🔧 Hot-Loadable Tools
Write a C# class, hit **Compile & Load** — the agent picks up your new tool without restarting. Tools persist across sessions.

### 📸 Git Checkpoints
Auto-commits before every Execute run. Mess something up? `git log` shows every checkpoint. Roll back in one click.

### 💬 Just Chat — Research Assistant
A dedicated research mode with web search and tool use — for when you need answers, not code. Runs alongside the main agent loop.

---

## Supported Hardware

| GPU | Inference Backend | VRAM | Performance |
|---|---|---|---|
| NVIDIA RTX 40/50 (CUDA 12) | llama.cpp cuda12 | Up to 24 GB | Maximum throughput |
| NVIDIA RTX 30 / GTX (CUDA 11) | llama.cpp cuda11 | Up to 16 GB | High throughput |
| AMD RX 7000 / 6000 | llama.cpp Vulkan | Up to 16 GB | Good throughput |
| Intel Arc | llama.cpp Vulkan | Up to 12 GB | Moderate throughput |
| CPU with AVX2 | llama.cpp avx2 | System RAM | Low, small models only |
| CPU baseline | llama.cpp cpu | System RAM | Minimal — 1.5B/3B only |

TheOrc auto-detects your GPU and CUDA version at install time. Ollama is also supported as an alternative backend — point the Settings panel at any Ollama host.

---

## Model Catalog

TheOrc ships with a curated catalog of tested GGUF models spanning 8 model families. The installer auto-selects based on your VRAM; you can switch any model at any time from the Model Library.

**Publisher legend:**

![Qwen](https://img.shields.io/badge/Qwen-Alibaba-FF6A00?style=flat-square&logoColor=white)
![Meta](https://img.shields.io/badge/Llama-Meta_AI-0668E1?style=flat-square&logoColor=white)
![Microsoft](https://img.shields.io/badge/Phi-Microsoft-0078D4?style=flat-square&logoColor=white)
![DeepSeek](https://img.shields.io/badge/R1%2FCoder-DeepSeek-6148FF?style=flat-square&logoColor=white)
![Nous](https://img.shields.io/badge/Hermes-Nous_Research-C41E3A?style=flat-square&logoColor=white)
![Google](https://img.shields.io/badge/Gemma-Google_DeepMind-4285F4?style=flat-square&logoColor=white)
![Mistral](https://img.shields.io/badge/Mistral%2FCodestral-Mistral_AI-F54800?style=flat-square&logoColor=white)
![NVIDIA](https://img.shields.io/badge/Nemotron-NVIDIA-76B900?style=flat-square&logoColor=white)

**Swarm role icons:** 🧠 Boss (TheOrc) · ⚙️ Worker (Coder) · 🔍 Researcher

---

### Tiny Tier — CPU / 2–4 GB VRAM

| Model | Publisher | Roles | Quality | Notes |
|---|---|---|---|---|
| Qwen 2.5 Coder 1.5B Q8 | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | ⚙️ 🔍 | ★★★ | CPU-ok. Fastest response on any hardware. |
| Qwen 2.5 Coder 3B Q8 | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | ⚙️ 🔍 | ★★★ | Entry coder. Runs well on 4 GB cards. |
| Llama 3.2 3B Q5 | ![Meta](https://img.shields.io/badge/-Meta_AI-0668E1?style=flat-square) | 🔍 | ★★★ | Fast researcher. Good for quick lookups. |
| Phi-3.5 Mini Q4 | ![Microsoft](https://img.shields.io/badge/-Microsoft-0078D4?style=flat-square) | 🔍 | ★★★ | 128K context. Best long-doc reader at 4 GB. |

---

### Small Tier — 4–8 GB VRAM

| Model | Publisher | Roles | Quality | Notes |
|---|---|---|---|---|
| **Phi-4 Mini Q8** | ![Microsoft](https://img.shields.io/badge/-Microsoft-0078D4?style=flat-square) | 🧠 🔍 | ★★★★ | **Best Boss for 8 GB cards.** Strong reasoning in 3.8B params. |
| Nemotron Mini 4B Q5 | ![NVIDIA](https://img.shields.io/badge/-NVIDIA-76B900?style=flat-square) | ⚙️ | ★★★★ | NVIDIA-optimised. Fast tool use on RTX cards. |
| **Qwen 2.5 Coder 7B Q5** | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | ⚙️ | ★★★★ | **Default worker.** Beats many 14B models on HumanEval. |
| Llama 3.1 8B Q5 | ![Meta](https://img.shields.io/badge/-Meta_AI-0668E1?style=flat-square) | 🧠 ⚙️ 🔍 | ★★★★ | Most versatile 8B model. Runs in all 3 roles. |
| Hermes 3 8B Q5 | ![Nous](https://img.shields.io/badge/-Nous_Research-C41E3A?style=flat-square) | 🧠 🔍 | ★★★★ | Tool-use specialist. Best Boss at 8 GB if planning is the bottleneck. |
| DeepSeek R1 Distill 7B Q5 | ![DeepSeek](https://img.shields.io/badge/-DeepSeek_AI-6148FF?style=flat-square) | 🧠 | ★★★★ | Visible chain-of-thought reasoning. Strong Boss candidate. |
| Mistral 7B v0.3 Q5 | ![Mistral](https://img.shields.io/badge/-Mistral_AI-F54800?style=flat-square) | ⚙️ 🔍 | ★★★ | Solid baseline. Good for mixed code + research tasks. |

---

### Mid Tier — 8–14 GB VRAM

| Model | Publisher | Roles | Quality | Notes |
|---|---|---|---|---|
| Qwen 2.5 Coder 7B Q8 | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | ⚙️ | ★★★★ | Near-lossless Q8. Higher accuracy on edge-case tool calls. |
| Gemma 3 12B Q4 | ![Google](https://img.shields.io/badge/-Google_DeepMind-4285F4?style=flat-square) | ⚙️ | ★★★★ | Google's latest. Strong multilingual + doc understanding. |
| Mistral Nemo 12B Q4 | ![Mistral](https://img.shields.io/badge/-Mistral_AI-F54800?style=flat-square) | 🧠 🔍 | ★★★★ | 128K context. Best for long-document research sessions. |
| **Qwen 2.5 Coder 14B Q4** | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | 🧠 ⚙️ | ★★★★★ | **Recommended default.** Best code quality under 14 GB. |
| Phi-4 14B Q4 | ![Microsoft](https://img.shields.io/badge/-Microsoft-0078D4?style=flat-square) | 🧠 | ★★★★★ | Microsoft's flagship reasoning model. Exceptional Boss. |
| **Hermes 4 14B Q5** | ![Nous](https://img.shields.io/badge/-Nous_Research-C41E3A?style=flat-square) | 🧠 ⚙️ | ★★★★★ | **Best Boss for pentest/security profiles.** Elite tool use. |
| DeepSeek R1 Distill 14B Q4 | ![DeepSeek](https://img.shields.io/badge/-DeepSeek_AI-6148FF?style=flat-square) | 🧠 | ★★★★★ | Full chain-of-thought at 14B. Best reasoning Boss in this tier. |
| DeepSeek Coder V2 Lite Q5 | ![DeepSeek](https://img.shields.io/badge/-DeepSeek_AI-6148FF?style=flat-square) | ⚙️ | ★★★★ | MoE architecture. Fast C/C++/Rust specialist worker. |
| Qwen 2.5 14B Instruct Q4 | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | 🧠 | ★★★★★ | General reasoning Boss. Better task decomposition than Coder variant. |

---

### Large Tier — 14–24 GB VRAM

| Model | Publisher | Roles | Quality | Notes |
|---|---|---|---|---|
| **Codestral 22B Q4** | ![Mistral](https://img.shields.io/badge/-Mistral_AI-F54800?style=flat-square) | ⚙️ | ★★★★★ | **Best pure code worker.** 80+ languages, FIM-optimised. |
| Qwen 2.5 Coder 32B Q3 | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | 🧠 ⚙️ | ★★★★★ | 32B quality at 16–20 GB. Minor quality trade from Q3 quant. |
| **Qwen 2.5 Coder 32B Q4** | ![Qwen](https://img.shields.io/badge/-Qwen%2FAlibaba-FF6A00?style=flat-square) | 🧠 ⚙️ | ★★★★★ | **Best available at 24 GB.** Approaches GPT-4 quality locally. |

---

### Flagship Tier — 40+ GB VRAM (multi-GPU)

| Model | Publisher | Roles | Quality | Notes |
|---|---|---|---|---|
| Llama 3.3 70B Q4 | ![Meta](https://img.shields.io/badge/-Meta_AI-0668E1?style=flat-square) | 🧠 | ★★★★★ | Near-GPT-4 quality. Best Boss for extreme VRAM. |

---

## Coding Profiles

The installer tailors the agent's `.agent.md` rules to your discipline and auto-suggests the best model family for each profile:

| Profile | Stack | Suggested Model |
|---|---|---|
| 🌐 Web / Full-Stack | TypeScript · React 18 · Node.js · REST/GraphQL | Qwen 2.5 Coder |
| ⚙️ Systems / Embedded | C · C++ · Rust · RTOS · BSP drivers | DeepSeek Coder V2 · Qwen 2.5 Coder |
| 📊 Data / AI / ML | Python · PyTorch · scikit-learn · Polars · MLflow | Qwen 2.5 Coder · Llama 3.1 8B |
| 🔐 Security / Pentest | Recon · OWASP · Metasploit · Impacket · Flipper/RF | Hermes 4 (fewer restrictions, agentic) |
| 🎨 UI / UX | Design tokens · WCAG 2.2 · React · Tailwind · Motion | Phi-4 Mini (fast structured output) |
| 🎮 Game Development | Unity C# · Unreal C++ · HLSL/GLSL · Physics · AI | DeepSeek Coder V2 · Codestral 22B |
| 📱 Android / Apple | Kotlin + Compose · Swift + SwiftUI · React Native | Qwen 2.5 Coder · Phi-4 Mini |
| 📈 Finance / FinTech | Trading systems · Risk · Crypto · Decimal math · Audit | DeepSeek R1 Distill (strong reasoning) |

---

## Agent Tools

| Tool | Approval? | Description |
|---|---|---|
| `read_file` | No | Read any file with line numbers |
| `write_file` | **Yes — diff shown** | Write a file, shows diff before applying |
| `list_files` | No | Recursive directory listing |
| `grep_code` | No | Ripgrep search across the codebase |
| `get_outline` | No | Extract functions/classes from a file |
| `run_shell` | **Yes — command shown** | Run PowerShell command |
| `run_tests` | No | Auto-detect and run tests |
| `fetch_url` | No | Fetch and strip a public URL |
| `ask_user` | Prompt shown | Ask a clarifying question inline |
| *Custom tools* | Configurable | Hot-loaded from your C# DLL |

---

## Getting Started

### Option 1 — One-Click Installer (recommended)

1. Go to [**Releases**](https://github.com/hardcoreerik/The-Orchestrator/releases) and download **OrchestratorSetup.exe**
2. Run it — Windows may show a SmartScreen warning; click **More info → Run anyway** (the app is unsigned but open source)
3. The wizard auto-detects your GPU, VRAM, and CUDA version, then downloads the right llama.cpp runtime and model
4. Pick your coding profile (Web, Systems, Security, etc.) and let the installer finish
5. Launch **TheOrc** from the Desktop shortcut

**First launch:**
- Click the 📁 workspace badge (top of the chat bar) to open your project folder
- Type your request — the agent proposes a plan first, you approve before anything runs
- Hit **Execute** to let it write files and run commands (with approval gates)

The installer handles everything: llama.cpp runtime, GGUF model download, `.agent.md` profile, and `settings.json`.

### Option 2 — Portable (no installer)

Download **TheOrc-x.x.x-win-x64-portable.zip** from [Releases](https://github.com/hardcoreerik/The-Orchestrator/releases), unzip anywhere, and run `OrchestratorIDE.exe`.
You'll need an existing [Ollama](https://ollama.ai) or llama-server instance — point Settings at it.

### Option 3 — Build from Source

```powershell
# Clone
git clone https://github.com/hardcoreerik/The-Orchestrator.git
cd The-Orchestrator

# Run the main app (requires an existing Ollama or llama-server running)
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj

# Or publish a single self-contained exe
dotnet publish OrchestratorIDE/OrchestratorIDE.csproj `
  -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -o publish/
```

**Requirements (build from source):** Windows 10/11 · [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) · Ollama or llama-server.exe

---

## Configuration

Settings are stored at `%APPDATA%\OrchestratorIDE\settings.json`.

```json
{
  "backend": "LlamaCpp",
  "llamaCppRuntimePath": "C:/OrchestratorIDE/Runtime/llama",
  "llamaCppModelPath":   "C:/Users/you/OrchestratorIDE/Models/Qwen2.5-Coder-14B-Instruct-Q4_K_M.gguf",
  "llamaCppPort": 8080,
  "llamaCppGpuLayers": -1,
  "llamaCppContextSize": 8192,
  "defaultModel": "Qwen2.5-Coder-14B-Instruct-Q4_K_M"
}
```

Alternatively, use the **Settings** panel in the app (gear icon in the activity bar).

---

## Architecture

```
OrchestratorIDE/          — Main WPF application (.NET 10)
  Core/
    AgentLoop             — Plan/Execute loop, tool dispatch, format-aware routing
    OllamaClient          — OpenAI-compat /v1/chat/completions streaming
    LlamaServerManager    — Manages llama-server.exe process lifecycle
    AppSettings           — Settings persistence (%APPDATA%)
  Agents/
    SwarmSession          — Multi-agent orchestration: boss + coder + researcher
    AgentWorker           — Individual goblin worker lifecycle
  Services/
    Swarm/
      SwarmConfigAdvisor  — Hardware-aware model recommender (nvidia-smi + benchmarks)
      SwarmMetricsStore   — JSONL run history, ConfigStats quality scoring
    ToolCalls/
      ToolCallProbeEngine — 5-test × 2-mode behavioral compatibility prober
      ToolCallProfileStore— Per-model probe results, dispatch mode selection
  UI/Panels/
    AgentPanel            — Chat interface, mode toggle, token meter
    SwarmBoardPanel       — Swarm Board: model pickers, goal input, live activity
    ToolEditorPanel       — Hot-load custom C# tools via Roslyn
  Tests/
    ToolCallTestWindow    — WPF probe results grid, live log
  Tools/
    ToolCallTester/       — tool-probe.exe standalone CLI

OrchestratorSetup/        — Guided installer WPF app
  Services/
    DownloadService       — HTTP Range-resume + SHA-256 verify
    ZipExtractService     — Runtime zip extraction
    InstallOrchestrator   — Full install sequence coordinator
    ProfileMerger         — .agent.md + settings.json writer

Setup/
  model-manifest.json     — Live-updateable model + runtime catalogue
  Profiles/*.agent.md     — 8 coding profile rule templates
```

---

## Shipped Features (v1.0)

Everything below is complete and shipping:

- [x] Plan → Execute agent loop with approval gates
- [x] Command palette (Ctrl+K)
- [x] Git auto-checkpoint before every run
- [x] Hot-loadable C# tools (Roslyn)
- [x] llama.cpp backend (no Ollama required)
- [x] One-click guided installer with GPU/CUDA auto-detection
- [x] GPU-aware model selection (VRAM-tiered)
- [x] 8 coding profiles (.agent.md rule sets)
- [x] HTTP range-resume model downloads with SHA-256 verify
- [x] WMI hardware detection (real GPU/CUDA/VRAM, registry fallback)
- [x] Auto-update checker (GitHub Releases API, 24-hr throttle)
- [x] FlaUI UI automation test suite (26/26 passing)
- [x] **Goblin Swarm** — boss + coder + researcher multi-agent mode
- [x] **SwarmBoard UI** — real-time task visibility panel
- [x] **SwarmConfigAdvisor** — hardware-aware role-based model recommender
- [x] **SwarmRunMetrics** — JSONL run history + quality scoring per config
- [x] **Co-Work mode** — human + agent working side-by-side in the same session
- [x] **Just Chat tab** — research assistant with web search and tool use
- [x] **4-tier trust system** — granular approval controls per tool class
- [x] **Per-mode model memory** — separate model choice per mode (plan/execute/swarm)
- [x] **Tool Call Probe Engine** — 5-test × 2-mode behavioral compatibility testing
- [x] **ToolCallProfileStore** — per-model dispatch profiles persisted across sessions
- [x] **tool-probe.exe** — standalone CLI tool for headless model probing
- [x] **Models menu** — Run Tool Call Tests, Manage Library, probe integration

---

## Roadmap

### 🧠 v1.1 — GOBLIN MIND (Active)

The Goblin Mind initiative teaches the swarm to understand itself at runtime.
See [`GOBLIN_MIND_TODO.md`](GOBLIN_MIND_TODO.md) for full task breakdown.

- [ ] **Phase 1: Behavioral Format Fingerprinting** — Probe each model's preferred tool-call serialization format (OpenAI JSON / Hermes XML / bare JSON / Python-style / YAML). Store as `FormatFingerprint` in the model profile. `AgentLoop` shapes tool schemas to the model's native format.
- [ ] **Phase 2: Category Boundary Mapping** — 14-query capability taxonomy per model (7 categories × 2 tests). TheOrc reads the map to gate swarm task routing — no coder goblin gets a network task if it fails network probes.
- [ ] **Phase 3: Adaptive Schema Generation** — Generate and persist confirmed tool schemas per model. Few-shot bootstrapping from successful probe outputs grows the schema library automatically.
- [ ] **Phase 4: Schema Reduction Middleware** — Transparent `AgentLoop` middleware that simplifies tool schemas for models that fail on complexity. Zero friction for users.
- [ ] **Phase 5: Evolutionary Schema Search** — On-demand mutation engine. Systematically explores schema space to find each model's highest-fitness calling convention.
- [ ] **Phase 6: TheOrc Steering Integration** — Boss model reads capability profiles to steer the swarm. Task routing becomes capability-driven, not config-driven.

### 🍎 v1.2 — Mac / Linux Port

- [ ] Avalonia UI port (Windows WPF → cross-platform)
- [ ] macOS llama.cpp Metal backend integration
- [ ] Linux AppImage build

### 🔮 v1.3 — Backlog

- [ ] Inline diff editing (edit proposed diff before approving)
- [ ] Background agent (fire task, get notified when done)
- [ ] Token cost estimator
- [ ] Multi-workspace support
- [ ] SwarmBoard metrics history tab (ConfigStats per configuration)

---

## Support TheOrc

TheOrc is free, open source, and always will be.
If it saves you a subscription bill, consider buying the orc a coffee ☕

<div align="center">

[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support_the_Orc-FF5E5B?style=for-the-badge&logo=kofi&logoColor=white)](https://ko-fi.com/hardcoreerik)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://paypal.me/hardcoreerik)
[![GitHub Sponsors](https://img.shields.io/badge/Sponsor-%E2%9D%A4-pink?style=for-the-badge&logo=github-sponsors&logoColor=white)](https://github.com/sponsors/hardcoreerik)

</div>

---

<div align="center">

## ❤️ Like TheOrc? Support the Project

TheOrc replaces a **$20–$40/month** subscription with a one-time setup and your own hardware.
If it's saving you money, a small contribution goes a long way.

<br/>

**Every coffee keeps the orc coding** — GPU testing, new model profiles, and faster updates.

<br/>

[![Ko-fi](https://img.shields.io/badge/Ko--fi-%E2%98%95_Buy_Me_a_Coffee-FF5E5B?style=for-the-badge&logo=kofi&logoColor=white&labelColor=FF5E5B)](https://ko-fi.com/hardcoreerik)

[![PayPal](https://img.shields.io/badge/PayPal-Send_a_Tip-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://paypal.me/hardcoreerik)

[![GitHub Sponsors](https://img.shields.io/badge/GitHub_Sponsors-Monthly_Support-%23EA4AAA?style=for-the-badge&logo=github-sponsors&logoColor=white)](https://github.com/sponsors/hardcoreerik)

<br/>

| Your support funds | |
|---|---|
| 🖥️ GPU time | Testing across RTX, AMD, and CPU-only hardware |
| 📦 Model updates | Keeping the manifest current as better models drop |
| ⚡ New features | More profiles, tools, and quality-of-life improvements |
| 🐛 Bug fixes | Fast turnaround on issues you report |

<br/>

---

**CODE. BUILD. AUTONOMIZE. REPEAT.**

*Built for developers. Made for freedom. Free forever.*

<img src="Assets/goblin%20swarm%20badge.png" width="160" alt="TheOrc Goblin Swarm"/>

</div>
