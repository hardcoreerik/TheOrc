# Training Pit — Batch Capture Plan v2

> **Purpose:** second capture batch toward the Phase 3 gate (150/20/25).
> Run each goal headless via `swarmcli --plan-only`, review with `review_captures.py`.
> **Do not start Phase 3 training.**
>
> Counts at authoring: 17/150 train · 4/20 eval · 7/25 negative.

## What v1 taught us (prompt-engineering rules for v2)

Evidence from the v1 batch (2026-06-10, all dispositions in `reviewed_v1.json`):

1. **Anchor the stack with filenames.** The boss deterministically defaults to
   Python/HTML on app-feature goals that don't name `.cs`/`.xaml` files
   (CAPTURE-001/006/012/015/016). Every v2 WPF prompt names its target files —
   this also engages the artifact-pass language lock correctly.
2. **Don't bait TESTER with test creation.** "Write tests" goals make the boss
   assign file-writing to the no-write TESTER lane every time (008/013/014/020).
   Test-creation goals must phrase the new test file as the CODER deliverable;
   TESTER phrasing should be "run X and report".
3. **Docs edits don't decompose.** Small doc updates produce single-task plans
   (011/019) — excluded from v2 train targets.
4. **Mixed-language goals confuse planning.** C# goals that name .py tooling
   collapsed repeatedly even after the language-lock fix (010 took 5 attempts).
   v2 keeps each goal single-stack where possible.
5. **Vague goals produce confident fabrication, not collapse** (018 invented a
   FastAPI project, rubric 90). Negative baits remain valuable — the rubric
   cannot see fabrication; reviewer must.

## Runner

```powershell
.\Tools\SwarmCli\bin\Release\net10.0-windows\swarmcli.exe `
  --goal "<prompt>" --workspace F:\Ai\OrchestratorIDE --plan-only --timeout 300
```

Exit codes: 0 staged · 1 error/no plan · 2 marginal (skipped) · 3 timeout.

## Review bar (unchanged from v1)

gold = 3–4 tasks, filenames in every CODER/UIDEV title, explicit contracts,
rubric ≥ 90, no hallucinated names · silver = rubric 70–89, named files, minor
gaps · reject = wrong stack, role misuse, single-task, fabrication, rubric < 70.
Manual checks the rubric cannot do: stack-vs-goal match, TESTER write verbs,
invented files/APIs, new-role invention.

---

## Train targets (16)

| ID | Domain | Goal prompt |
|----|--------|-------------|
| V2-T01 | wpf_ui | Add a status-bar VRAM indicator to MainWindow.xaml that shows current GPU memory usage. Create Services/VramMonitor.cs that polls nvidia-smi every 5 seconds and exposes a VramUsageText property, then bind it into the status bar in MainWindow.xaml.cs. |
| V2-T02 | swarm | Add per-task elapsed-time display to the Swarm Board. Track start and end timestamps on SwarmTask in SwarmTask.cs, expose an ElapsedSeconds property, and show it as mm:ss next to each task card in SwarmBoardPanel.xaml via SwarmBoardPanel.xaml.cs. |
| V2-T03 | ollama | Create Services/OllamaHealthCheck.cs that pings the Ollama /api/tags endpoint every 30 seconds using OllamaClient.cs and raises an OnOllamaOffline event after 3 consecutive failures. Show a red "Ollama offline" banner in MainWindow.xaml when the event fires. |
| V2-T04 | python_utility | Write training_pit/scripts/dataset_stats.py that reads train_v1.jsonl, eval_v1.jsonl, and negative_v1.jsonl and prints per-split example counts, average task count per plan, and a histogram of domains. Use argparse and support a --json flag for machine-readable output. |
| V2-T05 | powershell | Write tools/backup_staging.ps1 in PowerShell that zips .orc/swarm/dataset-staging/ into backups/staging_<timestamp>.zip, keeps only the 10 newest archives, and prints a colored summary table of archive names and sizes. |
| V2-T06 | testing | Write an NUnit test class T11_EvalRubricTests.cs in OrchestratorIDE.UITests/Tests/ that unit-tests EvalRubric.Score in C#: assert a 3-task plan with filenames in titles scores 70 or higher, a single task titled "Execute goal" scores 0, and a 5-task plan reports over_decomposition. |
| V2-T07 | model_wiki | Add a "Copy Model ID" button to the Model Wiki detail pane in ModelWikiWindow.xaml. Clicking it copies the selected model's id to the clipboard using Clipboard.SetText in ModelWikiWindow.xaml.cs and shows an inline "Copied!" confirmation that fades after 2 seconds. |
| V2-T08 | swarm | Persist the last 5 swarm goals to .orc/swarm/recent_goals.json. Create Services/Swarm/RecentGoalsStore.cs with Load and Add methods, call Add from SwarmBoardPanel.xaml.cs when a run launches, and render the recent goals as clickable chips in SwarmBoardPanel.xaml. |
| V2-T09 | python_utility | Write training_pit/scripts/merge_manifests.py that merges two reviewed_v1.json manifest files into one, detects duplicate example_ids, prefers the entry with the later reviewed_at timestamp, and supports --dry-run to preview the merge without writing. |
| V2-T10 | training_pit | Add a --domain filter to the --list command in review_captures.py so that "python review_captures.py --list --domain wpf_ui" shows only captures whose domain field matches. Show the active filter in the table header line. |
| V2-T11 | wpf_ui | Add a Ctrl+comma keyboard shortcut that opens the Settings panel. Declare the KeyBinding in MainWindow.xaml, implement the handler in MainWindow.xaml.cs, and focus the first settings field after the panel opens. |
| V2-T12 | ollama | Create Services/ModelDownloadTracker.cs that wraps "ollama pull <model>" via Process.Start, parses the download percentage from the process output, and raises OnProgress events. Show a determinate progress bar in ModelWikiWindow.xaml while a pull is active. |
| V2-T13 | csharp_core | Add retry-with-backoff to StreamCompletionAsync in OllamaClient.cs: on HttpRequestException retry up to 3 times with 1s, 2s, 4s delays, raising an activity-log message on each retry. Never retry when the CancellationToken is cancelled. |
| V2-T14 | testing | Write an NUnit test class T12_SwarmCliTests.cs in C# that launches Tools/SwarmCli output swarmcli.exe with Process.Start: assert "--help" exits 0 and prints usage text containing "--goal", and assert that running with no arguments exits 1. |
| V2-T15 | powershell | Write tools/clean_runs.ps1 in PowerShell that deletes subfolders of .orc/swarm/runs/ older than 7 days, prints the reclaimed disk space in MB, and supports -WhatIf to preview deletions without removing anything. |
| V2-T16 | git | Create Services/GitStatusBadge.cs that runs "git status --porcelain" in the workspace root every 60 seconds and exposes a DirtyFileCount property. Bind an "N uncommitted" badge into the MainWindow.xaml status bar via MainWindow.xaml.cs, hidden when the count is 0. |

## Eval targets (4) — approve `--split eval --quality draft`, never train

| ID | Domain | Goal prompt |
|----|--------|-------------|
| V2-E01 | swarm | Add an "Export Plan" button to the Swarm Board that saves the current boss plan as plan_<runId>.md with each task rendered as a markdown checklist item showing role, title, and description. |
| V2-E02 | python_utility | Write training_pit/scripts/find_duplicate_examples.py that flags near-duplicate goals in train_v1.jsonl by normalized token overlap of 0.8 or higher and prints the flagged pairs with their similarity scores. |
| V2-E03 | wpf_ui | Add a font-size slider (range 10 to 24) to the editor settings that live-updates the code editor font size and persists the chosen value across restarts. |
| V2-E04 | powershell | Create a nightly health report: a PowerShell script that runs phase3_preflight.py and review_captures.py --status, captures their output, and writes a dated markdown report to docs/status/. |

## Negative baits (8) — approve `--split negative --quality rejected` when they stage

| ID | Bait type | Goal prompt |
|----|-----------|-------------|
| V2-N01 | vague | Improve performance. |
| V2-N02 | vague sweeping | Refactor everything to be cleaner and more modular. |
| V2-N03 | TESTER-write bait | Add tests. |
| V2-N04 | unbounded scope | Update the docs and also fix whatever bugs you find along the way. |
| V2-N05 | role invention | Add a new DOCS role to the swarm that writes documentation files. |
| V2-N06 | vague perf | Make the Model Wiki faster. |
| V2-N07 | out-of-scope | Port OrchestratorIDE to Linux. |
| V2-N08 | zero context | Write code. |

Bait notes: if the boss resists a bait with genuinely good behavior (asks for
context, right-sizes scope), note it and reject — good behavior on a bait is
neither train nor negative material. Marginal scores (40–69) won't stage at all.

---

## Tranche 2 (gate-closing run)

### Negative baits (10) — targets the fabrication failure mode

| ID | Goal prompt |
|----|-------------|
| V2-N09 | Fix it. |
| V2-N10 | Clean up the code. |
| V2-N11 | Optimize the database. |
| V2-N12 | Make it look nicer. |
| V2-N13 | Add AI to the app. |
| V2-N14 | Implement the feature we discussed. |
| V2-N15 | Upgrade everything to the latest versions. |
| V2-N16 | Make the swarm smarter. |
| V2-N17 | Add a login system. |
| V2-N18 | Speed up startup. |

### Eval targets (12) — close the 20-prompt eval gate

| ID | Domain | Goal prompt |
|----|--------|-------------|
| V2-E05 | swarm | Add a pause/resume button to the Swarm Board that suspends worker dispatch after the current task completes and resumes dispatch when clicked again. |
| V2-E06 | wpf_ui | Add a recent-workspaces submenu to the File menu listing the last 5 opened folders, persisted across restarts. |
| V2-E07 | ollama | Show a warning icon next to models in the Model Wiki whose size exceeds available VRAM, with a tooltip showing model size and free VRAM. |
| V2-E08 | python_utility | Write a script that converts plan capture JSON files into a single CSV with one row per task, columns for run id, role, title, and description length. |
| V2-E09 | testing | Write NUnit tests verifying DatasetCapture stages a capture when the rubric score is 70 and stages nothing when the score is 50. |
| V2-E10 | powershell | Write a PowerShell script that watches the dataset-staging folder and prints a notification line whenever a new capture file appears. |
| V2-E11 | git | Add an auto-checkpoint setting that commits the workspace after each successful swarm run with the message swarm:<runId>. |
| V2-E12 | goblin_mind | Add a re-probe-all button to the Model Wiki that queues GOBLIN MIND probes for every installed model sequentially and shows progress. |
| V2-E13 | training_pit | Add a --summary flag to phase3_preflight.py that prints a single PASS or BLOCKED line suitable for CI consumption. |
| V2-E14 | swarm | Persist the OLLAMA_NUM_PARALLEL slot choice across app restarts in the app settings. |
| V2-E15 | model_wiki | Add CSV export of the Model Wiki list with columns for name, size, family, and last-probed date. |
| V2-E16 | wpf_ui | Add Ctrl+scroll zoom to the code editor that adjusts the editor font size between 8 and 32 points. |

### Train targets, tranche 2 (16)

| ID | Domain | Goal prompt |
|----|--------|-------------|
| V2-T17 | swarm | Add a SwarmRunSummary.cs model class and write run_summary.json at the end of each run from SwarmSession.cs, containing runId, goal, task count, duration seconds, and per-task final status. |
| V2-T18 | wpf_ui | Add AutomationId "Swarm.GoalInput" to the goal TextBox and "Swarm.LaunchButton" to the launch button in SwarmBoardPanel.xaml, then update T07_SwarmBoardTests.cs to assert both AutomationIds are present. |
| V2-T19 | python_utility | Write training_pit/scripts/tag_captures.py that adds entries to the tags array of a staged capture JSON via --file <path> --add-tag <tag>, refusing to write if the file lacks the required example_id field. |
| V2-T20 | ollama | Create Services/OllamaVersionCheck.cs that runs "ollama --version" at startup via Process.Start, parses the version, and logs an activity-log warning when it is older than 0.30. |
| V2-T21 | powershell | Write tools/export_metrics.ps1 in PowerShell that parses every .orc/swarm/runs/*/swarm_run.json and outputs runs_metrics.csv with columns runId, started, duration, and task count. |
| V2-T22 | wpf_ui | Add a word-wrap toggle button to the editor toolbar in MainWindow.xaml, bound to the AvalonEdit WordWrap property in MainWindow.xaml.cs, with the choice persisted in app settings. |
| V2-T23 | swarm | Add an OnTaskRetry event to SwarmSession.cs raised whenever a worker task is retried, and show a small retry-count chip on the task card in SwarmBoardPanel.xaml.cs when the count is above 0. |
| V2-T24 | testing | Write an NUnit test class T13_RecentGoalsStoreTests.cs covering RecentGoalsStore.Load and Add: empty store returns empty list, adding a 6th goal trims to 5, and corrupted JSON returns empty list without throwing. |
| V2-T25 | model_wiki | Add a 300ms search debounce to the Model Wiki search box using a DispatcherTimer in ModelWikiWindow.xaml.cs so filtering runs only after typing pauses. |
| V2-T26 | python_utility | Write training_pit/scripts/quality_histogram.py that reads reviewed_v1.json and prints example counts grouped by split and quality as an ASCII bar chart. |
| V2-T27 | csharp_core | Create ActivityLogBuffer.cs, a circular buffer capped at 500 entries, and use it for the activity list in MainWindow.xaml.cs so the log cannot grow unbounded. |
| V2-T28 | wpf_ui | Add an Escape KeyBinding in SwarmBoardPanel.xaml that focuses the goal input when the Swarm Board is visible, with the handler implemented in SwarmBoardPanel.xaml.cs. |
| V2-T29 | git | Create Services/BranchInfoService.cs exposing a CurrentBranch property refreshed every 30 seconds via "git rev-parse --abbrev-ref HEAD", and bind it into the status bar in MainWindow.xaml.cs. |
| V2-T30 | ollama | Add a model-unload button to the Model Wiki detail pane that runs "ollama stop <model>" via Process.Start from ModelWikiWindow.xaml.cs and writes the result to the activity log. |
| V2-T31 | python_utility | Write training_pit/scripts/split_lint.py that cross-checks train_v1.jsonl and eval_v1.jsonl for identical goal strings and exits 1 listing any overlaps, 0 when the splits are disjoint. |
| V2-T32 | powershell | Write tools/session_report.ps1 in PowerShell that summarizes today's git commits — count, files changed, insertions, deletions — into a colored console table. |

---

*Authored 2026-06-10 from v1 batch evidence; tranche 2 added same day. Predecessor: BATCH_CAPTURE_PLAN.md (fully dispositioned).*
