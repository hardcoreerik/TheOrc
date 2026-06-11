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

*Authored 2026-06-10 from v1 batch evidence. Predecessor: BATCH_CAPTURE_PLAN.md (fully dispositioned).*
