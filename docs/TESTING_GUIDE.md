# TheOrc — Testing Guide

> This guide covers the current test surfaces that protect the shell, model tooling, and Training Pit scripts. For shell behavior, see [USER_GUIDE.md](USER_GUIDE.md). For swarm internals, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## What Is Under Test

The current repository has two main testing layers:

- Windows UI tests under `OrchestratorIDE.UITests`
- Python tests for Training Pit scripts under `training_pit/tests`

There are also focused logic tests, including steering-related coverage, that protect capability-aware swarm behavior.

---

## UI Tests

The UI test project drives the real application through FlaUI and Windows UI Automation.

That means:

- a real interactive Windows desktop session is required
- the app must be buildable
- model-dependent flows need a reachable inference backend

This is why AutomationIds matter so much in the WPF shell.

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

The model tooling layer has dedicated UI and runtime surfaces:

- capability test dialog
- tool-call probe window
- Evolution tab for stored schema fitness

These surfaces matter because TheOrc actively changes runtime behavior based on probe results and local model evidence.

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
