# The Orchestrator

> A native AI coding assistant built for Windows. No browser. No subscription. Your machine, your models.

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-10.0-purple)
![License](https://img.shields.io/badge/license-MIT-green)

---

## What it is

The Orchestrator is a **standalone WPF desktop IDE** that wraps local Ollama models in a trust-first agent loop. It's designed for developers who want AI coding assistance without sending code to the cloud, without browser overhead, and without per-token billing.

Think: Cursor + Cline, but native Windows, 2ms input latency, and completely offline.

---

## Features

- **Trust-first**: every file write shows a diff — Approve or Reject before anything changes
- **Plan → Execute split**: agent proposes a plan for review before running any tools
- **Local models via Ollama**: works with qwen2.5-coder, Llama 3, Gemma, Phi-4, Hermes, and any model you have installed
- **Model picker**: click the status bar to switch models — toolset and system prompt auto-adapt
- **Command palette** (Ctrl+K): fuzzy-search commands, switch models, change workspace
- **Activity log**: real-time streaming of every tool call the agent makes
- **Git checkpoints**: auto-commits before every agent run so you can always roll back
- **Context meter**: color-coded token usage bar (blue → amber → red at 70%/85%)
- **Session persistence**: crashes don't lose your conversation — auto-recovered on restart
- **fetch_url tool**: agent can look up documentation and GitHub issues directly

---

## Tech Stack

| Layer | Technology |
|---|---|
| UI | WPF (.NET 10, XAML) |
| Code editor | AvalonEditB 1.2.0 |
| Diff engine | DiffPlex 1.9.0 |
| Git | LibGit2Sharp 0.31.0 |
| AI backend | Ollama (OpenAI-compat API) |
| Streaming | IAsyncEnumerable + SSE |
| Approval gate | TaskCompletionSource |

---

## Requirements

- Windows 10/11
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or use the self-contained publish)
- [Ollama](https://ollama.ai) running locally or on a network machine
- At least one model pulled in Ollama (e.g. `ollama pull qwen2.5-coder:7b`)

---

## Getting Started

```powershell
# Clone the repo
git clone https://github.com/hardcoreerik/The-Orchestrator.git
cd The-Orchestrator

# Build and run
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj

# Or build a single .exe
dotnet publish OrchestratorIDE/OrchestratorIDE.csproj `
  -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -o publish/
```

**Configure Ollama host** — edit `MainWindow.xaml.cs` line with `OllamaClient(...)` to point to your Ollama instance:
```csharp
_ollama = new OllamaClient("http://localhost:11434");
```

---

## Recommended Models

| Model | Best for | VRAM |
|---|---|---|
| `qwen2.5-coder:14b` | Coding tasks ★ | ~10 GB |
| `qwen2.5-coder:7b` | Fast coding | ~5 GB |
| `qwen2.5:14b-instruct` | General reasoning | ~10 GB |
| `llama3.1:8b` | Quick chat/questions | ~5 GB |
| `phi4-mini` | Lightweight / fast | ~2 GB |

---

## Tools Available to the Agent

| Tool | Description | Approval needed? |
|---|---|---|
| `read_file` | Read any file with line numbers | No |
| `write_file` | Write a file — shows diff first | **Yes** |
| `list_files` | Recursive directory listing | No |
| `grep_code` | Ripgrep search with fallback | No |
| `get_outline` | Extract functions/classes from file | No |
| `run_shell` | Run PowerShell command | **Yes** |
| `run_tests` | Auto-detect and run tests | No |
| `fetch_url` | Fetch public URL, strip HTML | No |

---

## Roadmap

- [ ] AvalonEdit code viewer (open files from explorer)
- [ ] Command palette: file open, recent sessions
- [ ] Checkpoint browser (browse/restore git history)
- [ ] Settings panel (Ollama host, workspace, rules)
- [ ] Single-exe publish + installer
- [ ] Background agent tasks

---

## License

MIT — use it, fork it, ship it.
