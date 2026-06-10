# TheOrc — Testing Guide

---

## Test Suite Overview

TheOrc uses **FlaUI (Windows UI Automation)** for black-box UI testing. The test
project is `OrchestratorIDE.UITests`. Tests drive the actual application as a user would —
clicking buttons, reading UI state, checking results.

There is no mock layer. Every test that touches agent behavior requires a live Ollama server.

---

## Requirements

- **Windows interactive desktop session** — FlaUI tests require an actual display.
  They cannot run headless, on a server, or in RDP with no display attached.
- **Ollama running** — tests that interact with agent features require `ollama serve`
- **Do not touch the mouse or keyboard** during FlaUI test runs — input events interfere
- **No other window should cover the TheOrc window** during tests
- Build the app first before running tests

---

## Running Tests

```powershell
# Run T07 and T08 (safe — do not require a capable model)
dotnet test OrchestratorIDE.UITests/OrchestratorIDE.UITests.csproj `
  --no-build `
  --filter "FullyQualifiedName~T07|FullyQualifiedName~T08"

# Run all tests except T06
dotnet test OrchestratorIDE.UITests/OrchestratorIDE.UITests.csproj `
  --filter "FullyQualifiedName!~T06"

# Run a specific test
dotnet test OrchestratorIDE.UITests/OrchestratorIDE.UITests.csproj `
  --filter "FullyQualifiedName~T01_LaunchTests.MainWindow_IsVisible"
```

> **Do NOT run T06 unless you have intentionally selected a capable model (≥12B).**
> T06 is a live end-to-end test that runs the agent autonomously and writes files.

---

## Test Suite Reference

### T01 — Launch Tests

**What it tests:** Application launch, main window visibility, AutomationId integrity,
basic status bar elements.

**Requirements:** No Ollama required. App must be built.

**Key assertions:**
- Main window is visible and has title containing "Orchestrator IDE"
- `AutomationId` of root window is `MainWindow`
- Status bar workspace label is present

### T02 — Activity Bar Tests

**What it tests:** Mode selector, activity bar navigation buttons, panel switching.

**Requirements:** No Ollama required.

**Key assertions:**
- Mode buttons are present and clickable (`Mode.Single`, `Mode.Swarm`, `Mode.Chat`)
- Switching modes shows/hides the correct panels

### T03 — Command Palette Tests

**What it tests:** `Ctrl+K` command palette open/close, search filtering, basic navigation.

**Requirements:** No Ollama required.

### T04 — Tool Editor Tests

**What it tests:** Tool editor panel opens, basic tool loading UI elements are present.

**Requirements:** No Ollama required.

### T05 — Agent Panel Tests

**What it tests:** Agent panel layout, chat input presence, model badge, trust level pill.

**Requirements:** No Ollama required for structural checks.

---

### T06 — Autonomous Build (End-to-End)

**What it tests:** Single-agent Execute mode — the agent must autonomously write 6 Python
files for OrcResearcher without human approval of each step.

**Requirements:**
- A capable model is currently selected (≥12B — `theorc-boss:gemma4`, `qwen2.5-coder:14b`)
- Ollama running with the model loaded
- Full Auto trust level or approved execution
- This test writes actual files to a test workspace

**Architecture:**
- File-based IPC: test writes the prompt to `<workspace>/.flaui_cmd`
- `MainWindow`'s `FileSystemWatcher` picks it up and calls `_agentPanel.AutoSend(prompt)`
- This bypasses `IValueProvider.SetValue` which truncates long prompts at ~383 chars
- Prompts embed full Python file contents so the model only needs to copy them

**Confirmed failures (small models):**
- `nemotron-3-nano:4b-q8_0` — zero files written across 3 passes (JSON truncation confirmed)
- Any model ≤4B — hard payload ceiling; will truncate long `write_file` JSON

**What failure means:**
If T06 fails with `opens > closes` in the agent log, the failure is a **model capability
issue** — not an app logic or Ollama configuration problem.

**Do not run T06 casually.** It is a live model test and takes several minutes.

---

### T07 — Swarm Board Tests

**What it tests:** Swarm Board idle-state smoke tests — panel loads, idle controls are
present and visible.

**Requirements:** No model execution required. Ollama not required for idle-state checks.

**Key assertions:**
- `Panel.SwarmBoard` is present and on-screen
- `Swarm.GoalInput` and `Swarm.Launch` are present in idle state
- Model picker elements are accessible

**What it does NOT test:**
- Active-state swarm nodes (they are `Visibility.Collapsed` at idle)
- Live swarm runs (requires Ollama with `OLLAMA_NUM_PARALLEL`)

---

### T08 — Model Wiki Tests

**What it tests:** Model Wiki / Lab window — open, filter chips, model list, detail pane,
capability test dialog.

**Requirements:** Ollama running (for model list population). No model execution for
most tests. The RunCapabilityTest tests briefly open and close the dialog.

**Key AutomationIds used:**
- `Menu.Models` — Models menu
- `Menu.ModelWiki` — Model Wiki / Lab… menu item
- `ModelWiki.Root` — Model Wiki window root
- `ModelWiki.Search` — Search box
- `ModelWiki.ModelList` — Model list
- `ModelWiki.Detail` — Detail pane
- `ModelWiki.RunCapabilityTest` — Run capability test button
- `ModelCapTest.Root` — Capability test dialog root
- `ModelCapTest.Cancel` — Stop button
- `ModelCapTest.Close` — Close button

**What it tests:**
- Window opens from menu (Menu.Models → Menu.ModelWiki)
- Window is single-instance (second open activates existing window)
- Search box filters model list
- Filter chips are present (Installed, Boss, Coder, Researcher, Tester, etc.)
- Detail pane loads content on model selection
- Run Capability Test button opens the dialog
- Capability test dialog closes cleanly

---

## AutomationId Reference

Key AutomationIds used across the test suite:

| AutomationId | Element |
|---|---|
| `MainWindow` | Application root window |
| `Mode.Single` | Single mode button |
| `Mode.Swarm` | Swarm mode button |
| `Mode.Chat` | Chat mode button |
| `StatusBar.Workspace` | Workspace badge in status bar |
| `Panel.SwarmBoard` | Swarm Board UserControl |
| `Swarm.GoalInput` | Goal text input |
| `Swarm.Launch` | Launch Swarm button |
| `Menu.Models` | Models menu item |
| `Menu.ModelWiki` | Model Wiki / Lab… menu item |
| `Menu.ModelCapabilityTest` | Run Model Capability Test… menu item |
| `ModelWiki.Root` | Model Wiki window |
| `ModelWiki.Search` | Search box |
| `ModelWiki.ModelList` | Model list |
| `ModelWiki.Detail` | Detail ScrollViewer |
| `ModelWiki.RunCapabilityTest` | Run Capability Test button |
| `ModelCapTest.Root` | Capability Test dialog window |
| `ModelCapTest.Cancel` | Stop/cancel button |
| `ModelCapTest.Close` | Close button |

---

## Adding a New Test

1. Create `OrchestratorIDE.UITests/Tests/T<nn>_<Name>Tests.cs`
2. Inherit from `RecordingTestBase`
3. Use `AppFixture.FindById(automationId)` for element discovery
4. Use `AppFixture.RequireById(automationId)` when the element must exist (throws if not found)
5. Add AutomationIds to XAML elements before writing tests for them
6. Tests that involve live model execution: document the model requirement and add a skip guard

---

## Test Recordings

FlaUI test failures save screen recordings to:
```
%APPDATA%\OrchestratorIDE\Recordings\
```

These are AVI files named by test and timestamp. Review them when a test fails in CI or
on a machine where you can't observe the screen.

---

## Training Pit Python Tests

The Training Pit scripts have their own unittest suites under `training_pit/tests/`.
These run without FlaUI, Ollama, or a live desktop — pure Python unit tests.

```powershell
# Run from the repo root

# Phase 2.5 approval valve tests (31 tests)
python training_pit/tests/test_review_captures.py

# Phase 3 preflight guardrail tests (36 tests)
python training_pit/tests/test_phase3_preflight.py
```

Both suites use isolated temp directories and do not read or modify the live
`reviewed_v1.json` manifest or any staged captures.

**Run both after any change to Training Pit scripts.** All tests must pass before committing.
