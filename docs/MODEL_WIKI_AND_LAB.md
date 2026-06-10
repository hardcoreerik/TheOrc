# TheOrc — Model Wiki / Lab

---

## Overview

The Model Wiki / Lab is a non-modal window that aggregates everything TheOrc knows
about each model into a single browseable, searchable catalogue. It merges data from:

- **ModelProfiles** — built-in capability scores and role recommendations
- **Ollama installed list** — which models are currently on your Ollama server
- **GOBLIN MIND probe results** — format fingerprint, category boundary scores
- **Swarm run history** — historical swarm session metrics
- **Built-in observations** — local test results baked into the app
- **User capability test results** — results from **Run Model Capability Test…**

---

## How to Open

**Menu:** `Models → Model Wiki / Lab…`

This opens a non-modal window (`AutomationId: ModelWiki.Root`, window title: "Model Wiki / Lab — TheOrc").

The window is **single-instance** — if it's already open, opening the menu item will activate
and bring it to focus rather than opening a second window.

---

## Window Layout

### Left Pane — Model List

- **Search box** (`AutomationId: ModelWiki.Search`) — type to filter by model name in real-time
- **Filter chips** — filter by Installed, Boss, Coder, Researcher, Tester, Long write_file, Fast/Small
- **Model list** (`AutomationId: ModelWiki.ModelList`) — shows model display name, speed tier, VRAM, installed badge

Click a model to load its detail in the right pane.

### Right Pane — Model Detail

The detail ScrollViewer (`AutomationId: ModelWiki.Detail`) shows per-model sections:

- **Section A — Summary** — display name, installed status, VRAM, speed tier
- **Section B — Role Scores** — BossScore, CoderScore, ResearcherScore, TesterScore with colored bars
- **Section C — Observations** — recorded observations from all sources with result badges
- **Section D — Tool Call Reliability** — GOBLIN MIND probe data (format preference, category scores)
- **Section E — Swarm History** — recent swarm runs involving this model
- **Section F — Routing Recommendation** — derived routing advice: Boss / Coder / Researcher / Tester / SingleAgent / LongWriteFile

### Header — Run Capability Test Button

`AutomationId: ModelWiki.RunCapabilityTest`

Opens the Model Capability Test dialog for the currently selected model.

---

## Built-in Observations File

Built-in observations are loaded from:
```
OrchestratorIDE/Resources/model-wiki-observations.json
```

This file is embedded in the assembly as an EmbeddedResource. It contains observations
for models that have been locally tested and cannot be derived from Ollama metadata alone.

### Observation Schema

Each entry in the JSON array has these fields:

| Field | Type | Description |
|---|---|---|
| `ModelId` | string | Exact Ollama model tag (e.g. `nemotron-3-nano:4b-q8_0`) |
| `Source` | string | Source type: `built_in`, `local_system_test`, `local_probe`, `user_run_test`, `local_observation` |
| `TestId` | string | Test identifier (e.g. `T06_BuildResearchTool`, `swarm_benchmark_multiple_runs`) |
| `Date` | string | ISO date string (YYYY-MM-DD) |
| `Result` | string | `pass`, `fail`, `partial`, `not_tested`, or `observed` |
| `Summary` | string | Human-readable description of what was observed |
| `Classification` | string | Short classification tag (e.g. `not_recommended_for_long_write_file`, `recommended_boss_model`) |
| `RecommendedUses` | string[] | List of suitable use cases |
| `NotRecommendedUses` | string[] | List of unsuitable use cases |
| `Confidence` | string | Confidence level (e.g. `observed_local_single_run`, `observed_local_multiple_runs`, `inferred_from_q8_result`) |

---

## Model Capability Test Dialog

### How to Open

**Menu:** `Models → Run Model Capability Test…`

Or click **Run Capability Test** in the Model Wiki header (uses the currently selected model).

This opens a modal dialog (`AutomationId: ModelCapTest.Root`, title: "Run Model Capability Test — TheOrc").

### Tests

Three payload sizes test `write_file` JSON reliability:

| Test ID | Description | Payload |
|---|---|---|
| `FileWriteSmall` | Write a 1-line text file | ~30 chars |
| `FileWriteMedium` | Write a ~50-line Python script | ~1.5 KB |
| `FileWriteLarge` | Write a ~150-line Python application | ~5 KB |

All tests run in an **isolated temp workspace** at `%TEMP%\TheOrc\ModelTests\{timestamp}\`.
They do NOT touch your active project workspace.

### Test Selection

Use the **Test Level** ComboBox to select:
- Small only
- Medium only
- Large only
- All three ★ (runs all three in sequence)

### Visual Proof-of-Life Indicators

The dialog is designed to show exactly what the model is doing:

**Phase strip** — shows current execution stage:
```
○ Idle  →  📡 Sending  →  ⏳ Model thinking  →  📥 Received  →  🔍 Analyzing  →  Done
```

**Live test cards** — one card per test, updated in real time:
- Badge: QUEUED / RUNNING / PASS / FAIL / PARTIAL
- Card color changes (green = pass, red = fail, orange = partial)
- Shows response time once received

**Activity feed** — timestamped colored log:
- Green: pass events
- Red: fail events, errors
- Orange: truncation warnings, partial results
- White: informational messages

**Truncation / incomplete JSON warnings** — if `OpenBraceCount ≠ CloseBraceCount`, the
activity feed explicitly flags this as a truncated JSON payload with brace counts shown.

**Stop / Close:**
- **■ Stop** (`AutomationId: ModelCapTest.Cancel`) — cancels a running test
- **Close** (`AutomationId: ModelCapTest.Close`) — closes the dialog (enabled at all times)

### Capability Test Result Schema

Results are saved to `%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl`. Each line:

| Field | Type | Description |
|---|---|---|
| `ModelId` | string | Model being tested |
| `TestId` | string | `FileWriteSmall`, `FileWriteMedium`, or `FileWriteLarge` |
| `TestName` | string | Human-readable test name |
| `Timestamp` | DateTime | When the test ran |
| `Result` | string | `pass`, `fail`, or `partial` |
| `FileWritten` | bool | Whether the model made a file write attempt |
| `ExpectedFile` | string | The filename the model was asked to create |
| `ActualFileSizeBytes` | int | Size of the written file (0 if not written) |
| `ValidJson` | bool | Whether the tool call JSON was fully valid |
| `Truncated` | bool | Whether the response was likely truncated |
| `OpenBraceCount` | int | Number of `{` in the response |
| `CloseBraceCount` | int | Number of `}` in the response |
| `Notes` | string | Additional detail about what happened |

### Interpreting Results

| Result | Meaning |
|---|---|
| **PASS** | File was written, JSON was valid, no truncation detected |
| **PARTIAL** | File was written but content was shorter than expected, or JSON had minor issues |
| **FAIL** | File was not written, or JSON was truncated / invalid |

A model that passes `FileWriteSmall` but fails `FileWriteLarge` has a **payload size ceiling**.
This is expected for models under ~7B parameters. Use the VRAM tier table in
[MODEL_GUIDE.md](MODEL_GUIDE.md) to pick a model appropriate for your task complexity.

---

## Structured Progress Tokens

`ModelCapabilityTestService` emits structured tokens in the progress stream that the dialog parses:

```
[PHASE:sending]              — HTTP request sent to Ollama
[PHASE:received:N]           — Response received, N chars
[PHASE:analyzing]            — Analyzing tool call JSON
[RESULT:pass]                — Test passed
[RESULT:fail]                — Test failed
[RESULT:partial]             — Test partially passed
```

These tokens drive the phase strip and test card state updates in real time.

---

## Implemented vs Planned

### Currently Implemented ✅

- Model Wiki window with search, filter chips, model list, detail pane
- Built-in observations from `model-wiki-observations.json`
- Capability test dialog with phase strip, live cards, activity feed
- FileWriteSmall / FileWriteMedium / FileWriteLarge test suite
- Result persistence to `%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl`
- GOBLIN MIND probe data displayed in detail pane (Section D)
- Single-instance window with activate-on-reopen behavior

### Planned 🔲

- Model comparison view (side-by-side two models)
- Historical result trends (improvement over time chart)
- Export capability matrix to Markdown
- Filter chips for GOBLIN MIND category scores
- "Probe Now" button in the Model Wiki detail pane (currently only available in ToolCallTestWindow)
