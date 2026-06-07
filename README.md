<div align="center">

![TheOrc Banner](Assets/banner.png)

<br/>

[![Platform](https://img.shields.io/badge/platform-Windows-blue?style=for-the-badge&logo=windows)](https://github.com/hardcoreerik/TheOrc/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License](https://img.shields.io/badge/license-MIT-brightgreen?style=for-the-badge)](LICENSE)
[![Backend](https://img.shields.io/badge/inference-llama.cpp-orange?style=for-the-badge)](https://github.com/ggml-org/llama.cpp)
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
| Auto-selects best model for your GPU | ✅ | ❌ | ❌ |
| Guided one-click installer | ✅ | ✅ | ❌ |
| Plan → Review → Execute safety loop | ✅ | ❌ | ❌ |
| Diff approval before any file write | ✅ | ❌ | ❌ |
| Git auto-checkpoint before each run | ✅ | ❌ | ❌ |
| Profile-tailored agent rules (.agent.md) | ✅ | ❌ | ❌ |
| Hot-load custom C# tools at runtime | ✅ | ❌ | ❌ |

---

## Features

### 🔒 Trust-First Agent Loop
Every file write surfaces a **visual diff** — Approve or Reject before anything on disk changes. Shell commands show a **ShellApprovalCard** with the exact command to be run. You stay in control; the agent does the heavy lifting.

### 📋 Plan → Execute Split
The agent proposes a step-by-step plan for your review before executing a single tool call. Reject the plan, edit it, or approve — your call.

### ⚡ Local Inference via llama.cpp
TheOrc bundles **llama-server.exe** directly — no Ollama required (though Ollama is supported as an alternative backend). The installer detects your GPU and downloads the right CUDA/Vulkan/AVX2 runtime automatically.

### 🎯 GPU-Aware Model Selection
The installer reads your VRAM and picks the best Qwen2.5-Coder model for your hardware — from 1.5B on a CPU-only machine all the way to 32B Q4 on a 24 GB card.

### 📝 Coding Profiles
Eight tailored `.agent.md` rule sets — the agent knows your domain before you type a word.

### 🔧 Hot-Loadable Tools
Write a C# class, hit **Compile & Load** — the agent picks up your new tool without restarting. Tools persist across sessions.

### 📸 Git Checkpoints
Auto-commits before every Execute run. Mess something up? `git log` shows every checkpoint. Roll back in one click.

---

## Supported Hardware

| GPU | Runtime | Recommended Model |
|---|---|---|
| NVIDIA RTX (CUDA 12) | cuda12 | Qwen2.5-Coder 14B Q4 |
| NVIDIA GTX (CUDA 11) | cuda11 | Qwen2.5-Coder 7B Q5 |
| AMD RX / Pro | Vulkan | Qwen2.5-Coder 7B Q5 |
| Intel Arc | Vulkan | Qwen2.5-Coder 7B Q5 |
| CPU (AVX2) | avx2 | Qwen2.5-Coder 3B Q8 |
| CPU (baseline) | cpu | Qwen2.5-Coder 1.5B Q8 |

---

## Model Tiers

| VRAM | Model | Quality | Notes |
|---|---|---|---|
| CPU only | Qwen2.5-Coder 1.5B Q8 | ★★ | Fast, simple edits |
| 3–4 GB | Qwen2.5-Coder 3B Q8 | ★★★ | Entry level |
| 5–6 GB | Qwen2.5-Coder 7B Q5 | ★★★★ | **Balanced pick** |
| 8–10 GB | Qwen2.5-Coder 7B Q8 | ★★★★ | Higher accuracy |
| 10–12 GB | Qwen2.5-Coder 14B Q4 | ★★★★★ | **Recommended** |
| 14–18 GB | Qwen2.5-Coder 32B Q3 | ★★★★★ | Flagship |
| 20–24 GB | Qwen2.5-Coder 32B Q4 | ★★★★★ | **Best available** |

Profile-specific alternatives auto-suggested by the installer:
- **Security/Pentest** → Hermes-4 14B (fewer restrictions, agentic)
- **Game Dev / Systems** → DeepSeek-Coder-V2-Lite (stronger C++/GLSL)
- **UI/UX / Mobile** → Phi-4 Mini (fast, structured output)

---

## Coding Profiles

The installer tailors the agent's `.agent.md` rules to your discipline:

| Profile | Stack |
|---|---|
| 🌐 Web / Full-Stack | TypeScript · React 18 · Node.js · REST/GraphQL |
| ⚙️ Systems / Embedded | C · C++ · Rust · RTOS · BSP drivers |
| 📊 Data / AI / ML | Python · PyTorch · scikit-learn · Polars · MLflow |
| 🔐 Security / Pentest | Recon · OWASP · Metasploit · Impacket · Flipper/RF |
| 🎨 UI / UX | Design tokens · WCAG 2.2 · React · Tailwind · Motion |
| 🎮 Game Development | Unity C# · Unreal C++ · HLSL/GLSL · Physics · AI |
| 📱 Android / Apple | Kotlin + Compose · Swift + SwiftUI · React Native |
| 📈 Finance / FinTech | Trading systems · Risk · Crypto · Decimal math · Audit |

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

## Support TheOrc

TheOrc is free, open source, and always will be.  
If it saves you a subscription bill, consider buying the orc a coffee ☕

<div align="center">

[![Ko-fi](https://img.shields.io/badge/Ko--fi-Support_the_Orc-FF5E5B?style=for-the-badge&logo=kofi&logoColor=white)](https://ko-fi.com/hardcoreerik)
[![PayPal](https://img.shields.io/badge/PayPal-Donate-00457C?style=for-the-badge&logo=paypal&logoColor=white)](https://paypal.me/hardcoreerik)
[![GitHub Sponsors](https://img.shields.io/badge/Sponsor-%E2%9D%A4-pink?style=for-the-badge&logo=github-sponsors&logoColor=white)](https://github.com/sponsors/hardcoreerik)

</div>

---

## Architecture

```
OrchestratorIDE/          — Main WPF application (.NET 10)
  Core/
    AgentOrchestrator     — Plan/Execute loop, tool dispatch, streaming
    OllamaClient          — OpenAI-compat /v1/chat/completions streaming
    LlamaServerManager    — Manages llama-server.exe process lifecycle
    AppSettings           — Settings persistence (%APPDATA%)
  UI/Panels/
    AgentPanel            — Chat interface, mode toggle, token meter
    ToolEditorPanel       — Hot-load custom C# tools via Roslyn
  UI/Controls/
    CommandPalette        — Ctrl+K fuzzy search
    DiffViewer            — File write approval (before/after diff)
    ShellApprovalCard     — Shell command approval

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

## Roadmap

- [x] Plan → Execute agent loop with approval gates
- [x] Command palette (Ctrl+K)
- [x] Git auto-checkpoint
- [x] Hot-loadable C# tools (Roslyn)
- [x] llama.cpp backend (no Ollama required)
- [x] One-click guided installer
- [x] GPU-aware model selection
- [x] 8 coding profiles
- [x] HTTP range-resume model downloads
- [x] **Phase F** — WMI hardware detection (real GPU/CUDA/VRAM query, registry fallback)
- [x] **Phase G** — Auto-update checker (GitHub Releases API, 24-hr throttle, Settings toggle)
- [x] FlaUI UI automation test suite (26/26 passing)
- [ ] Linux / macOS (Avalonia port, planned)

---

## License

MIT — use it, fork it, ship it.

```
Copyright (c) 2025 hardcoreerik
```

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

<img src="Assets/icon.png" width="80" alt="TheOrc"/>

</div>
