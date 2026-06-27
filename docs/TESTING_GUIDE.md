# TheOrc — Testing Guide

> This guide covers the current test surfaces that protect the shell, model tooling, and Training Pit scripts. For shell behavior, see [USER_GUIDE.md](USER_GUIDE.md). For swarm internals, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## What Is Under Test

The current repository has three main testing layers:

- Headless unit + Avalonia tests under `OrchestratorIDE.UnitTests` and
  `OrchestratorIDE.Avalonia.HeadlessTests` — run without a display, the primary
  CI-runnable suites (logic, HIVE security, ChatPanel/MarkdownView behavior)
- Windows UI tests under `OrchestratorIDE.UITests` — drive the real app via FlaUI
- Python tests for Training Pit scripts under `training_pit/tests`

There are also focused logic tests, including steering-related coverage, that protect capability-aware swarm behavior.

---

## UI Tests

The UI test project drives the real application through FlaUI and Windows UI Automation.

That means:

- a real interactive Windows desktop session is required
- the app must be buildable
- model-dependent flows need a reachable inference backend

This is why AutomationIds matter so much in the Avalonia shell.

---

## Why AutomationIds Matter

The docs should only mention AutomationIds that actually exist in the XAML because the UI suite depends on exact values.

Examples visible in the current shell include:

- `Mode.Single`
- `Mode.Swarm`
- `Mode.Chat`
- `StatusBar.Workspace`
- `StatusBar.Branch`
- `StatusBar.Build`
- `StatusBar.Model`

If you document or test a made-up AutomationId, you are testing fiction.

---

## Model And Probe Testing

The GUI diagnostic surfaces that used to front this layer — the capability test
dialog, the tool-call probe window, and its Evolution tab — were **retired with
WPF (2026-06-20)** and not ported to Avalonia. What remains and still matters:

- live capability testing during normal runs (`ModelCapabilityTestService`)
- the GOBLIN MIND probe stack and stored schema-fitness data
- the `model-wiki/results.jsonl` evidence the swarm and AgentLoop consume

These still drive runtime behavior; only the standalone WPF diagnostic windows are gone.

---

## Training Pit Script Tests

The Training Pit has Python tests under `training_pit/tests`.

These are important because the data pipeline is designed to fail closed. Script regressions can corrupt trust in the dataset even if the shell still looks healthy.

Core checks include:

- review workflow behavior
- preflight behavior
- dataset safety invariants

---

## Good Testing Order

When making shell or model-surface changes:

1. build the app
2. run targeted logic tests where possible
3. run the relevant UI tests
4. if Training Pit scripts changed, run the Python tests too

This gives you the shortest path to catching both UI breakage and dataset-pipeline regressions.

---

## Repo-Local Test Profile

Some environments deny writes to the normal OS temp or per-user AppData paths
even when the repo itself is writable. That can surface as false negatives in
tests that touch git scratch repos, HIVE identity persistence, or other local
state.

For that case, use:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\run-local-testenv.ps1
```

What it does:

- redirects `TEMP`, `TMP`, `APPDATA`, `LOCALAPPDATA`, and `HOME` into
  `F:\Ai\OrchestratorIDE-dev\.codex-testenv`
- runs the `OrchestratorIDE.UnitTests` and
  `OrchestratorIDE.Avalonia.HeadlessTests` projects under that profile
- falls back to `dotnet vstest` against existing built test assemblies if
  `dotnet test` is blocked by restore/build issues

Useful variants:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\run-local-testenv.ps1 -Suite Unit
powershell -ExecutionPolicy Bypass -File Tools\run-local-testenv.ps1 -Suite Headless
powershell -ExecutionPolicy Bypass -File Tools\run-local-testenv.ps1 -UseScratchBuildPaths
powershell -ExecutionPolicy Bypass -File Tools\run-local-testenv.ps1 -UseVsTestOnly
powershell -ExecutionPolicy Bypass -File Tools\run-local-testenv.ps1 -NoRestore
```

`-UseScratchBuildPaths` is the "build somewhere else" escape hatch. It tells
MSBuild to place project `obj` / output files under a repo-local
`.codex-scratch-build` tree, with one isolated subfolder per referenced project.
Use it when the normal `obj` tree is locked or denied but the repo itself is
still writable.
