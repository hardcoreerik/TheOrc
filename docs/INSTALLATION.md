# TheOrc — Installation

> This guide covers the verified installation paths for the current Windows application and the minimum setup needed for local training. For architecture context, see [ARCHITECTURE.md](ARCHITECTURE.md). For operator workflow after install, see [QUICK_START.md](QUICK_START.md).

---

## Platform Reality

The current app is Windows-only.

- UI: Avalonia on .NET 10
- test automation: Windows UI Automation via FlaUI
- training GUI: integrated into the Windows shell

The repository contains planned cross-platform direction, but this guide documents the current Windows implementation only.

---

## Option 1: Installer

Use the installer if you want the app to make the early hardware and runtime choices for you.

The installer path is the best fit when you want:

- hardware detection
- a guided profile choice
- model/runtime download help
- a clean first-run setup

After install, continue with [QUICK_START.md](QUICK_START.md).

---

## Option 2: Portable Build

The portable route is appropriate when you already have a working inference backend and want to place the app manually.

Expect to configure:

- Ollama host or llama.cpp runtime path
- model selection
- workspace and settings paths

If the app opens but cannot see models, use [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

---

## Option 3: Build From Source

Source build is the best choice if you are working on TheOrc itself.

Minimum requirements:

- Windows 10 or 11
- .NET 10 SDK
- a working local inference backend for full runtime testing

Typical build commands:

```powershell
dotnet build OrchestratorIDE.slnx
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj
```

If you are only checking compile health, you can build without a live model. If you are validating agent behavior, model surfaces, or swarm execution, you need a reachable backend.

---

## Inference Backend Setup

The current shell is designed around reachable local or LAN inference endpoints.

Common setup:

```powershell
ollama serve
ollama list
```

The default Ollama host is:

```text
http://localhost:11434
```

The app can also point at another host in Settings, which is useful when a second machine has the stronger GPU.

---

## Training Pit Setup

Training is separate from ordinary inference setup.

What the code expects:

- the repository contains `training_pit/`
- reviewed dataset exports exist under `training_pit/datasets/`
- Python can run the Training Pit scripts
- CUDA is available for the current `train_lora.py` path

Useful checks:

```powershell
python training_pit/scripts/phase3_preflight.py
python training_pit/scripts/review_captures.py --status
```

The current preflight gate is already met in this repository, but preflight still matters because it verifies manifest consistency, validation, sanitizer safety, duplicate safety, and eval isolation before training starts.

---

## ORC ACADEMY Prerequisites

The in-app training GUI expects:

- Python packages required by `training_pit/scripts/train_lora.py`
- CUDA-capable PyTorch for the current training path
- enough GPU memory for the chosen run mode

The GUI itself contributes:

- `dry run`
- `resume`
- `VRAM cap`
- heartbeat monitoring
- re-attach after app restart

Read [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) before launching a real run.

---

## First Post-Install Checks

After installation or build, verify:

- `F1` opens the in-app Help window
- the status bar shows a build stamp
- a model can be selected
- the Model Wiki / Lab window opens
- the Swarm Board shows capability badges

Those checks cover the shell, model services, and embedded docs in one pass.
