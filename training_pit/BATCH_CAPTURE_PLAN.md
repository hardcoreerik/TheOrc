# Training Pit — Batch Capture Plan v1

> **Purpose:** 20 live TheOrc swarm prompts designed to produce high-quality boss-planning captures.
> Run each goal in TheOrc, let DatasetCapture.cs auto-stage qualifying plans, then review with `review_captures.py`.
>
> **Current counts:** 3/150 train · 0/20 eval · 0/25 negative
> **Do not start Phase 3 training.** These runs are for data collection only.

---

## Command Checklist

### Before batch
```powershell
python training_pit/scripts/review_captures.py --status
python training_pit/scripts/phase3_preflight.py
```

### After each swarm run
```powershell
python training_pit/scripts/review_captures.py --list
```

### Review loop
```powershell
python training_pit/scripts/review_captures.py --inspect <capture>
python training_pit/scripts/review_captures.py --approve <capture> --split train --quality gold     --note "..."
python training_pit/scripts/review_captures.py --approve <capture> --split train --quality silver   --note "..."
python training_pit/scripts/review_captures.py --approve <capture> --split eval  --quality draft    --note "..."
python training_pit/scripts/review_captures.py --approve <capture> --split negative --quality rejected --note "..."
python training_pit/scripts/review_captures.py --reject  <capture> --note "..."
```

### After review batch
```powershell
python training_pit/scripts/review_captures.py --export-train
python training_pit/scripts/review_captures.py --export-eval
python training_pit/scripts/review_captures.py --export-negative
python training_pit/scripts/review_captures.py --status
python training_pit/scripts/phase3_preflight.py
```

---

## Gold/Silver/Rejected Criteria (universal)

| Rating | Criteria |
|--------|----------|
| **gold** | 3–4 tasks · named output files in every CODER/UIDEVELOPER title · API contracts explicit and consistent · descriptions ≥ 80 chars · no hallucinated names · rubric ≥ 90 |
| **silver** | 2–4 tasks · named output files · reasonable descriptions · minor contract gaps · rubric 70–89 |
| **rejected** | Single-task collapse · vague descriptions · hallucinated APIs · wrong roles · sensitive data · rubric < 70 |

---

## Prompts — Train Targets (12 prompts)

### CAPTURE-001
**Category:** `wpf_ui`
**Intent:** train/gold
**Goal prompt:**
> Add a collapsible sidebar panel to OrchestratorIDE that shows recent agent run history. Each entry shows the goal, model used, and exit status. Clicking an entry opens a detail popup with the full agent log.

**Expected tasks:** 3 (CODER × 2 + UIDEVELOPER)
**Expected roles:** CODER (RunHistoryViewModel.cs, RunHistoryService.cs) · UIDEVELOPER (RunHistorySidebar.xaml)
**Expected files:** `RunHistoryViewModel.cs`, `RunHistoryService.cs`, `RunHistorySidebar.xaml`
**Good training signal:** Multi-file WPF feature with clear CODER/UIDEVELOPER split and required API contract (ViewModel shared between backend and UI).
**Gold if:** ViewModel properties named consistently in both CODER and UIDEVELOPER tasks; popup trigger mechanism specified.
**Silver if:** Files named but API contract not fully explicit; description slightly thin.
**Rejected if:** Single task "implement sidebar," no filenames, vague description.

---

### CAPTURE-002
**Category:** `model_wiki`
**Intent:** train/gold
**Goal prompt:**
> Add a "Last Probed" timestamp column to the Model Wiki model list. When a model has been GOBLIN MIND probed, display the probe date in the list. If never probed, show "—". The data comes from the existing ToolCallProfile.TestedAt field, accessible via the model's probe profile in ModelWikiWindow.xaml.cs.

**Expected tasks:** 3 (CODER × 2 + UIDEVELOPER)
**Expected roles:** CODER (ModelWikiWindow.xaml.cs — expose TestedAt via display property) · UIDEVELOPER (ModelWikiWindow.xaml — add column) · CODER (format TestedAt as short date string; null → "—")
**Expected files:** `ModelWikiWindow.xaml.cs`, `ModelWikiWindow.xaml`
**Good training signal:** Incremental WPF feature on existing window — tests ability to plan targeted changes without rewriting unrelated code.
**Gold if:** Tasks reference `ToolCallProfile.TestedAt` and `entry.ProbeProfile` by name; column binding is explicit; null-guard for no-probe case specified.
**Silver if:** Correct files but describes binding generically without field name.
**Rejected if:** Proposes rewriting the entire Model Wiki window, or invents a non-existent `LastProbed` field.

---

### CAPTURE-003
**Category:** `swarm`
**Intent:** train/gold
**Goal prompt:**
> After a swarm run that included a TESTER task completes, display a colored verdict summary card at the top of the Boss output tab in SwarmBoardPanel. The card should show PASS (green), PARTIAL (yellow), FAIL (red), or SKIPPED (gray) based on the existing TesterVerdict enum in SwarmSession.cs. Show the verdict label and the key verdict line from the TESTER output. Wire it to the existing OnSwarmComplete event in SwarmBoardPanel.xaml.cs.

**Expected tasks:** 3 (CODER × 2 + UIDEVELOPER)
**Expected roles:** CODER (SwarmSession.cs — expose TesterVerdict via OnSwarmComplete or a dedicated event) · UIDEVELOPER (SwarmBoardPanel.xaml — add verdict card Border with DataTrigger colors) · CODER (SwarmBoardPanel.xaml.cs — populate verdict card on OnSwarmComplete)
**Expected files:** `SwarmSession.cs`, `SwarmBoardPanel.xaml.cs`, `SwarmBoardPanel.xaml`
**Good training signal:** Cross-layer feature on existing infrastructure — tests planning the data-flow from SwarmSession event through ViewModel binding to XAML display. TESTER role is already wired; only the verdict display is missing.
**Gold if:** All three files named in titles; TesterVerdict enum values (Pass/Partial/Fail/Skipped) used as the branching condition in both CODER and UIDEVELOPER tasks; DataTrigger color values specified.
**Silver if:** Files named but verdict enum not referenced; color logic described generically.
**Rejected if:** Proposes re-implementing the TESTER lane itself (already exists); or uses a single CODER task with no UIDEVELOPER split.

---

### CAPTURE-004
**Category:** `training_pit`
**Intent:** train/gold
**Goal prompt:**
> Add a --dry-run flag to review_captures.py that shows what would be approved/rejected without writing to the manifest. Output should mirror the normal approve/reject output but prefix each line with [DRY RUN].

**Expected tasks:** 2 (CODER)
**Expected roles:** CODER (review_captures.py --dry-run logic)
**Expected files:** `training_pit/scripts/review_captures.py`
**Good training signal:** Python script extension with a clear, bounded scope — tests planning single-file additions without over-decomposing.
**Gold if:** Task description specifies the exact CLI argument name, prefix string, and which manifest write calls to skip.
**Silver if:** Correct scope but description says "add dry-run mode" without specifying the output format.
**Rejected if:** Proposes rewriting review_captures.py or adding a second script.

---

### CAPTURE-005
**Category:** `ollama`
**Intent:** train/gold
**Goal prompt:**
> Write a Python script ollama_benchmark.py that measures time-to-first-token and tokens-per-second for a given Ollama model. Accept --model and --prompt as CLI args. Run 3 warmup passes then 5 measured passes. Output a JSON summary.

**Expected tasks:** 3 (RESEARCHER + CODER × 2)
**Expected roles:** RESEARCHER (Ollama /api/generate streaming schema) · CODER (benchmark logic in ollama_benchmark.py) · CODER (JSON output formatter)
**Expected files:** `ollama_benchmark.py`
**Good training signal:** Python utility with a RESEARCHER-first pattern — validates that the boss knows when research is needed before coding.
**Gold if:** RESEARCHER task asks for exact streaming response fields; CODER tasks reference those field names.
**Silver if:** RESEARCHER task is vague; CODER implements with assumptions.
**Rejected if:** Skips RESEARCHER and hallucinates Ollama API details.

---

### CAPTURE-006
**Category:** `goblin_mind`
**Intent:** train/gold
**Goal prompt:**
> Add a "🧠 Probe Now" button to the Model Wiki detail pane. When clicked, run FormatProbeEngine and CategoryProbeEngine on the currently selected model, save the result via ToolCallProfileStore, and refresh the detail pane to show updated probe data. Show a progress ring in the button while the probe runs. The probe engines already exist in OrchestratorIDE/Services/ToolCalls/.

**Expected tasks:** 3 (CODER × 2 + UIDEVELOPER)
**Expected roles:** CODER (ModelWikiWindow.xaml.cs — BtnProbeNow_Click, call FormatProbeEngine + CategoryProbeEngine, save via ToolCallProfileStore, call existing RefreshDetailPane) · UIDEVELOPER (ModelWikiWindow.xaml — add "🧠 Probe Now" button + ProgressRing to detail pane) · CODER (disable button + show spinner during probe; re-enable and refresh on completion)
**Expected files:** `ModelWikiWindow.xaml.cs`, `ModelWikiWindow.xaml`
**Good training signal:** Feature from v1.2 roadmap — tests CODER/UIDEVELOPER split on an incremental addition to an existing window. Uses real service class names (`FormatProbeEngine`, `CategoryProbeEngine`, `ToolCallProfileStore`).
**Gold if:** Both CODER tasks and UIDEVELOPER task use the same button name (`BtnProbeNow`); probe engine class names explicitly stated; async/await pattern noted.
**Silver if:** Files named and approach correct but engine class names not specified.
**Rejected if:** Proposes a new service class when the probe engines already exist; or single task "add probe button."

---

### CAPTURE-007
**Category:** `powershell`
**Intent:** train/gold
**Goal prompt:**
> Write a PowerShell script health_check.ps1 that verifies the local TheOrc dev environment: checks Ollama is running, dotnet SDK is present and correct version, git is clean, and the OrchestratorIDE.exe can be built. Output a colored status table.

**Expected tasks:** 2 (RESEARCHER + CODER)
**Expected roles:** RESEARCHER (PowerShell version check cmdlets, Ollama service check pattern) · CODER (health_check.ps1 implementation)
**Expected files:** `health_check.ps1`
**Good training signal:** Windows-specific scripting goal — tests that the boss plans PS-native solutions, not bash substitutes.
**Gold if:** CODER task specifies PS cmdlets (Get-Command, Invoke-RestMethod for Ollama, dotnet --version parsing).
**Silver if:** Correct structure but cmdlets not specified; uses generic "check" language.
**Rejected if:** Proposes bash/WSL solution for a Windows-native task.

---

### CAPTURE-008
**Category:** `python_utility`
**Intent:** train/silver
**Goal prompt:**
> Write a Python script diff_jsonl.py that takes two JSONL files and reports: lines only in file A, lines only in file B, and lines in both. Match lines by the messages[1].content field (user goal). Output a summary and optionally --dump-diff to show full diffs.

**Expected tasks:** 2 (CODER)
**Expected roles:** CODER (diff_jsonl.py implementation)
**Expected files:** `diff_jsonl.py`
**Good training signal:** Training Pit utility — a real tool we'll want. Tests 2-task planning for single-module Python scripts.
**Silver target (not gold):** 2 tasks is appropriate; goal is well-scoped but doesn't require RESEARCHER.
**Gold if:** Splits into parser module + CLI module; names both files.
**Silver if:** Single CODER task with specific field path and --dump-diff flag.
**Rejected if:** Proposes generic file diff (ignores JSONL structure).

---

### CAPTURE-009
**Category:** `wpf_ui`
**Intent:** train/gold
**Goal prompt:**
> Add keyboard shortcut Ctrl+Shift+S to OrchestratorIDE that opens the Swarm Board panel and focuses the goal input. If the Swarm Board is already visible, just focus the goal input. Register the shortcut in MainWindow.xaml.cs.

**Expected tasks:** 2 (CODER + UIDEVELOPER)
**Expected roles:** CODER (MainWindow.xaml.cs KeyBinding handler) · UIDEVELOPER (MainWindow.xaml KeyBinding declaration)
**Expected files:** `MainWindow.xaml.cs`, `MainWindow.xaml`
**Good training signal:** Targeted WPF change — tests planning minimal edits to existing files, not rewrites.
**Gold if:** Both tasks reference the AutomationId `Swarm.GoalInput` and the specific KeyBinding syntax.
**Silver if:** Correct files but binding syntax not specified.
**Rejected if:** Proposes adding a menu item instead of a keyboard shortcut.

---

### CAPTURE-010
**Category:** `testing`
**Intent:** train/gold
**Goal prompt:**
> Write an NUnit test class T09_TrainingPitTests.cs that verifies: review_captures.py --status exits 0, phase3_preflight.py exits 1 (BLOCKED), and that reviewed_v1.json is valid JSON. Use Process.Start to invoke the scripts.

**Expected tasks:** 2 (RESEARCHER + CODER)
**Expected roles:** RESEARCHER (NUnit Process.Start pattern, Python script exit code capture) · CODER (T09_TrainingPitTests.cs implementation)
**Expected files:** `OrchestratorIDE.UITests/Tests/T09_TrainingPitTests.cs`
**Good training signal:** Tests knowledge of the project's NUnit test conventions (RecordingTestBase, T0n naming, file location).
**Gold if:** CODER task names the test class, file path, and specific assertions (exit code 1 for BLOCKED preflight).
**Silver if:** Correct approach but doesn't specify exit code assertions.
**Rejected if:** Proposes Python unittest for a C# test project.

---

### CAPTURE-011
**Category:** `docs`
**Intent:** train/silver
**Goal prompt:**
> Update INSTALLATION.md to add a "Training Pit Setup" section explaining: how DatasetCapture.cs auto-stages captures, how to run review_captures.py --list after swarm runs, and how to check Phase 3 gate status. Keep it under 30 lines.

**Expected tasks:** 2 (RESEARCHER + CODER)
**Expected roles:** RESEARCHER (read existing INSTALLATION.md, current Training Pit README state) · CODER (write the new section)
**Expected files:** `docs/INSTALLATION.md`
**Good training signal:** Docs-update planning — tests RESEARCHER-reads-first then CODER-writes pattern for documentation changes.
**Silver target:** 2 tasks, scoped, appropriate.
**Gold if:** RESEARCHER task explicitly reads INSTALLATION.md to find insertion point; CODER task names the section header and bullet structure.
**Rejected if:** Proposes rewriting all of INSTALLATION.md.

---

### CAPTURE-012
**Category:** `ollama`
**Intent:** train/gold
**Goal prompt:**
> Add a ModelProfile.ContextWindow field to OrchestratorIDE that stores the model's context length. Populate it by calling ollama show <model> --json and parsing the num_ctx field. Display the value in the Model Wiki detail pane.

**Expected tasks:** 3 (RESEARCHER + CODER × 2)
**Expected roles:** RESEARCHER (ollama show --json schema, num_ctx field location) · CODER (ModelProfile.cs field + OllamaService population) · CODER (ModelWikiViewModel binding + XAML label)
**Expected files:** `ModelProfile.cs`, `OllamaService.cs`, `ModelWikiWindow.xaml`
**Good training signal:** Cross-layer feature — model layer, service layer, UI layer, all named explicitly.
**Gold if:** All three files named in titles; RESEARCHER establishes field name used in both CODERs.
**Silver if:** Correct approach but field name not explicitly contracted.
**Rejected if:** Invents a different Ollama API endpoint that doesn't exist.

---

## Prompts — Eval Targets (4 prompts)

> These prompts are held out to measure model improvement. Run them, let them stage, then approve as eval split.
> **Never approve eval captures as train.** They exist to test the fine-tuned model, not teach it.

### CAPTURE-013
**Category:** `wpf_ui`
**Intent:** eval/draft
**Goal prompt:**
> Add a dark mode toggle to OrchestratorIDE. When toggled, switch the app's ResourceDictionary theme between Light.xaml and Dark.xaml. Persist the user's choice in app settings.

**Why eval:** Classic WPF theming task — measures whether the model can plan ResourceDictionary switching + settings persistence without hallucinating WPF theme APIs.
**Approve as:** `--split eval --quality draft`

---

### CAPTURE-014
**Category:** `python_utility`
**Intent:** eval/draft
**Goal prompt:**
> Write a Python script export_manifest_report.py that reads training_pit/datasets/manifests/reviewed_v1.json and outputs a Markdown table of all approved entries: example_id, split, quality, score, and first 60 chars of the note.

**Why eval:** Training Pit domain — tests whether the model correctly decomposes a Python utility task that touches the project's own files.
**Approve as:** `--split eval --quality draft`

---

### CAPTURE-015
**Category:** `swarm`
**Intent:** eval/draft
**Goal prompt:**
> When a swarm run finishes, automatically save the boss plan and worker outputs to a JSON session file in .orc/swarm/sessions/. File should be named session_<runId>_<timestamp>.json. Add a "View Session" button to the SwarmBoard that opens the latest session file.

**Why eval:** Multi-component swarm feature — tests decomposition of a file-persistence + UI wiring task.
**Approve as:** `--split eval --quality draft`

---

### CAPTURE-016
**Category:** `goblin_mind`
**Intent:** eval/draft
**Goal prompt:**
> Add a capability badge row to the Model Wiki detail pane showing the model's GOBLIN MIND category scores as colored chips: green (≥7), yellow (4–6), red (<4). Scores come from ModelProfile.CategoryScores.

**Why eval:** GOBLIN MIND + WPF display — tests planning a UI-only read-from-ViewModel feature without introducing unnecessary service calls.
**Approve as:** `--split eval --quality draft`

---

## Prompts — Negative/Collapse Targets (4 prompts)

> These prompts are designed to expose specific boss-planning failure modes.
> **Do not approve as train.** Approve as negative split for regression eval.
> Run them in TheOrc with a weak model configuration if available, or just use the captures as-is if the boss collapses.

### CAPTURE-017
**Category:** `python_utility`
**Intent:** negative — one-task collapse bait
**Goal prompt:**
> Fix the bug.

**Expected failure mode:** Single empty task "Fix the bug" with no description, or refusal to plan. Score ≤ 39.
**Why useful:** Documents the model's behavior on zero-context goals — the clearest collapse case.
**Approve as:** `--split negative --quality rejected --note "zero-context collapse bait: single vague task or refusal"`
**Do NOT approve as train.**

---

### CAPTURE-018
**Category:** `wpf_ui`
**Intent:** negative — ambiguous UI/backend boundary
**Goal prompt:**
> Make the app better.

**Expected failure mode:** Collapse to 1–2 vague tasks ("Improve the app," "Fix UI issues") with no filenames or meaningful scope.
**Why useful:** Documents the model's behavior on maximally vague goals — no decomposition possible.
**Approve as:** `--split negative --quality rejected --note "vague-goal collapse: no actionable decomposition possible"`
**Do NOT approve as train.**

---

### CAPTURE-019
**Category:** `docs`
**Intent:** negative — over-decomposition bait
**Goal prompt:**
> Write a single-sentence description of what GOBLIN MIND does for the README.

**Expected failure mode:** Boss over-decomposes a trivially small task into 3–4 tasks (RESEARCHER to "research GOBLIN MIND," CODER to "write the sentence," TESTER to "verify the sentence"). Score penalized for unnecessary decomposition.
**Why useful:** Documents the opposite failure mode — over-decomposition of a task that is smaller than the minimum useful swarm scope.
**Approve as:** `--split negative --quality rejected --note "over-decomposition: single-sentence doc task decomposed into multi-agent plan"`
**Do NOT approve as train.**

---

### CAPTURE-020
**Category:** `testing`
**Intent:** negative — invalid role use bait
**Goal prompt:**
> Write a new Python function and then run the tests to make sure it works, all in one plan.

**Expected failure mode:** Boss assigns both writing and testing to CODER (ignoring TESTER role), or assigns TESTER to write files (violating TESTER's no-write constraint). Score penalized for role misuse.
**Why useful:** Documents role confusion — the model treating TESTER as "run tests after writing" rather than a separate no-write lane.
**Approve as:** `--split negative --quality rejected --note "role misuse: TESTER assigned to write files, or CODER given both write+test duties"`
**Do NOT approve as train.**

---

## Summary

| ID | Category | Intent | Tasks | Key Signal |
|----|----------|--------|-------|-----------|
| CAPTURE-001 | wpf_ui | train/gold | 3 | CODER/UIDEVELOPER split, ViewModel contract |
| CAPTURE-002 | model_wiki | train/gold | 3 | Incremental WPF, ToolCallProfile.TestedAt column |
| CAPTURE-003 | swarm | train/gold | 3 | TESTER verdict card (display gap, not lane wiring) |
| CAPTURE-004 | training_pit | train/gold | 2 | Single-file extension, bounded scope |
| CAPTURE-005 | ollama | train/gold | 3 | RESEARCHER-first, API contract from research |
| CAPTURE-006 | goblin_mind | train/gold | 3 | "Probe Now" in Model Wiki detail pane (roadmap v1.2) |
| CAPTURE-007 | powershell | train/gold | 2 | PS-native tooling, not bash |
| CAPTURE-008 | python_utility | train/silver | 2 | 2-task Python utility, scoped |
| CAPTURE-009 | wpf_ui | train/gold | 2 | Targeted XAML/cs edit, minimal scope |
| CAPTURE-010 | testing | train/gold | 2 | NUnit conventions, exit code assertions |
| CAPTURE-011 | docs | train/silver | 2 | Docs-update RESEARCHER-then-CODER |
| CAPTURE-012 | ollama | train/gold | 3 | Cross-layer, 3 named files, field contract |
| CAPTURE-013 | wpf_ui | eval/draft | 3 | WPF theming + settings persistence |
| CAPTURE-014 | python_utility | eval/draft | 2 | Training Pit domain utility |
| CAPTURE-015 | swarm | eval/draft | 3 | Session persistence + UI wiring |
| CAPTURE-016 | goblin_mind | eval/draft | 2 | UI-only read-from-ViewModel |
| CAPTURE-017 | python_utility | negative | 1 | Zero-context collapse bait |
| CAPTURE-018 | wpf_ui | negative | 1 | Maximally vague goal |
| CAPTURE-019 | docs | negative | 4+ | Over-decomposition bait |
| CAPTURE-020 | testing | negative | 2 | Role misuse bait |

**Targets if all 20 run and qualify:**
- Train: +12 (running total: ~15/150)
- Eval: +4 (running total: ~4/20)
- Negative: +4 (running total: ~4/25)

---

## Notes

- Run positive captures (001–012) with `theorc-boss:gemma4` at normal settings.
- Run negative captures (017–020) last. If the boss produces a good plan despite the bait, reject the capture or note the unexpected behavior.
- Eval captures (013–016) are held out — approve them as `eval` split only.
- After this batch, run `python training_pit/scripts/review_captures.py --status` and `phase3_preflight.py` to see updated gate counts.
- DatasetCapture.cs will only stage scores ≥70 or ≤39. Negative bait prompts that produce marginal scores (40–69) will be silently skipped.

---

*Last updated: 2026-06-09 — v1.1, repo-verified revision. CAPTURE-002 field name corrected (ToolCallProfile.TestedAt); CAPTURE-003 replaced (TESTER lane already exists — replaced with verdict card); CAPTURE-006 replaced (GoblinMindPanel/Service don't exist — replaced with "Probe Now" button in ModelWikiWindow using real engine class names).*
