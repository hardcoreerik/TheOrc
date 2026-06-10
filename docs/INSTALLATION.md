# TheOrc — Installation

---

## Platform Status

**TheOrc is Windows-first.** The UI is built on WPF (.NET 10) and uses Windows-native
UI automation (FlaUI / UIA3). There is no macOS or Linux build at this time.

A cross-platform Docker + Blazor port is on the roadmap (v1.4). The WPF app will
remain the primary Windows experience indefinitely.

---

## Requirements

| Requirement | Version / Notes |
|---|---|
| Windows | 10 or 11 (64-bit) |
| .NET Runtime | 10.0+ (included in self-contained portable builds) |
| GPU | Any NVIDIA, AMD, or Intel GPU; CPU inference supported but slow |
| Inference backend | **Ollama** (recommended) or llama.cpp via `OrchestratorIDE.exe` bundled runtime |

---

## Option A — Portable ZIP (no installer)

1. Download `TheOrc-x.x.x-win-x64-portable.zip` from [Releases](https://github.com/hardcoreerik/The-Orchestrator/releases)
2. Unzip to any folder (e.g. `C:\Tools\TheOrc\`)
3. Run `OrchestratorIDE.exe`

You will need an existing Ollama instance running. Point **Settings → Ollama Host** at it.

---

## Option B — Build from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Git](https://git-scm.com/)
- [Ollama](https://ollama.ai) running (or llama.cpp)

### Steps

```powershell
# Clone the repo
git clone https://github.com/hardcoreerik/The-Orchestrator.git
cd The-Orchestrator

# Build the main application
dotnet build OrchestratorIDE/OrchestratorIDE.csproj --configuration Debug

# Run directly
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj

# Or publish a self-contained single executable
dotnet publish OrchestratorIDE/OrchestratorIDE.csproj `
  -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -o publish/
```

The `publish/` folder will contain `OrchestratorIDE.exe` and supporting assets.

---

## Visual Studio / VS Code Notes

### Visual Studio 2022 (recommended for full WPF dev)

1. Open `OrchestratorIDE.slnx` (Visual Studio solution file)
2. Set `OrchestratorIDE` as the startup project
3. Run with F5

The solution includes `OrchestratorIDE.UITests` — these are FlaUI automation tests
that launch the actual app. Do not run them from the IDE Test Explorer unless you
understand the desktop requirement (see below).

### VS Code

VS Code can build and run TheOrc via the `dotnet` CLI. XAML editing is functional
but without the WPF Designer. Install the C# Dev Kit extension for best experience.

---

## Ollama Setup

TheOrc communicates with Ollama via the OpenAI-compatible `/v1/chat/completions` endpoint.

```powershell
# Start Ollama (required before launching TheOrc)
ollama serve

# Default host (TheOrc will discover models automatically)
# http://localhost:11434

# To use a remote Ollama server, set in TheOrc Settings:
# Ollama Host = http://192.168.1.x:11434
```

Settings are persisted at `%APPDATA%\OrchestratorIDE\settings.json`.

**Multi-agent (Swarm) requirement:**
```powershell
# Allow multiple models to run in parallel
$env:OLLAMA_NUM_PARALLEL = "4"
ollama serve
```
Without `OLLAMA_NUM_PARALLEL` set, Swarm workers will queue instead of running concurrently.

---

## Common Setup Problems

### "OrchestratorIDE.exe not found"
Build the main project first:
```powershell
dotnet build OrchestratorIDE/OrchestratorIDE.csproj
```
Or set the `ORCHESTRATOR_EXE` environment variable to the full path of the binary
(useful in CI where the output path differs from the default).

### Red Ollama indicator in status bar
- Check that `ollama serve` is running
- Check **Settings → Ollama Host** matches the address Ollama is listening on
- Check Windows Firewall if Ollama is on a different machine

### Model list is empty
- The Ollama host must be reachable before TheOrc starts
- Pull at least one model: `ollama pull qwen2.5-coder:7b`
- Restart TheOrc after pulling if the model list was already showing empty

### "SmartScreen prevented an unrecognized app"
The executable is unsigned. Click **More info → Run anyway**. This is normal for
open-source apps that are not code-signed. The source is available at the repo for inspection.

### dotnet build fails: "SDK not found"
Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
Make sure `dotnet --version` shows `10.x.x` after installation.

---

## FlaUI UI Tests — Special Requirement

The `OrchestratorIDE.UITests` project uses FlaUI (Windows UI Automation) to drive the
actual application as a black-box UI test suite.

**FlaUI tests require:**
- An **interactive Windows desktop session** (not a headless server, not RDP with no display)
- No other application should be covering the TheOrc window during test execution
- Do not move the mouse or type during FlaUI test runs — keyboard/mouse events interfere

Run the tests from the command line:
```powershell
dotnet test OrchestratorIDE.UITests/OrchestratorIDE.UITests.csproj `
  --no-build `
  --filter "FullyQualifiedName~T07|FullyQualifiedName~T08"
```

Do not run T06 unless you have a capable model installed (≥7B recommended).

See [TESTING_GUIDE.md](TESTING_GUIDE.md) for the full test reference.
