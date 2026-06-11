# TheOrc Training Pit — external review bundle

You (the AI reading this) are an independent dataset reviewer. Below are
12 randomly sampled training examples for fine-tuning a 12B "boss" model
whose only job is: given a user's coding goal, produce a JSON swarm plan.
Give a second human-quality opinion on whether each example deserves to be in
the training set. You have no access to the codebase — judge only what is
internally verifiable. Be strict: a reviewer who keeps everything is useless.

The quality bar each example must meet:

1. 2-4 tasks. Single-task plans and 5+ task plans are defects.
2. Roles are EXACTLY one of: RESEARCHER, CODER, UIDEVELOPER, TESTER.
   Any other role string is an instant fail.
3. TESTER never creates, writes, or edits files — it only runs things and
   reports. A TESTER task with create/write verbs is an instant fail.
4. CODER/UIDEVELOPER tasks name concrete output files with extensions.
5. Tasks must agree with each other: shared deliverables must use identical
   file/class/property names across tasks.
6. The plan must solve the goal that was asked — not a mock, demo, or
   substitute feature.
7. No invented certainty about existing code internals the planner cannot know.
8. One language stack per plan, matching the goal's stack.

For EACH example output exactly:

- ID: <example id>
- VERDICT: KEEP / BORDERLINE / PULL
- REASON: 1-2 sentences citing the specific task or name at fault
- BEST TASK / WORST TASK: one line each

After all examples: a pass rate, the most common weakness across the set, and
whether you would trust a model trained on examples like these to plan reliably.

---
*Generated 2026-06-10 from training_pit/datasets/train_v1.jsonl (163 examples in split, sample seed 1337).*

---

## Example 1 — ID: ex_20260611_015817  (quality: silver)

**Goal given to the planner:**

> Goal: Add a star/favorite toggle to each Model Wiki row in ModelWikiWindow.xaml: starred model ids persist to .orc/model_favorites.json via a small Services/ModelFavoritesStore.cs, and starred models sort to the top of the list.

**Plan the planner produced:**

```json
{
  "plan": "Implement a favorite system by creating a JSON-based persistence service and updating the WPF UI with sorting logic.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research C# JSON persistence and WPF list sorting",
      "description": "Investigate: (1) Using System.Text.Json in C# to read/write a List<string> to a file path like '.orc/model_favorites.json', including directory creation logic. (2) Best practices for toggling a boolean property in a WPF collection that triggers a re-sort of the items (e.g., using ICollectionView or manual Sort). Return a 1-page summary with specific C# snippets for JSON serialization and sorting."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write Services/ModelFavoritesStore.cs",
      "description": "Create Services/ModelFavoritesStore.cs in C#. Implement a class that manages a list of favorited model IDs stored in '.orc/model_favorites.json'. Include two public methods: 'bool IsFavorite(string modelId)' which returns true if the ID exists in the loaded list, and 'void ToggleFavorite(string modelId)' which adds or removes the ID from the list and saves the updated list to the file. Use System.Text.Json for all operations. Ensure the .orc directory is created if it doesn't exist."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update ModelWikiWindow.xaml and associated C# logic",
      "description": "Modify ModelWikiWindow.xaml to add a Star button (or icon) to each row in the Model Wiki list. Update the associated C# code-behind or ViewModel to: (1) Call 'ModelFavoritesStore.IsFavorite(id)' when loading rows to set the initial state of the star button, (2) call 'ModelFavoritesStore.ToggleFavorite(id)' when the star is clicked, and (3) ensure that the collection of models is sorted so that all items where 'IsFavorite' is true appear at the top of the list. Use XAML for the UI layout."
    }
  ]
}
```

## Example 2 — ID: ex_20260611_024049  (quality: silver)

**Goal given to the planner:**

> Goal: Add a --json flag to phase3_preflight.py that emits the nine check results as a JSON object with name, passed, and detail fields plus an overall ready boolean, keeping the human-readable output as the default.

**Plan the planner produced:**

```json
{
  "plan": "Update phase3_preflight.py to support a --json flag using argparse and the json module while preserving existing text output.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research Python argparse and json formatting",
      "description": "Investigate: (1) Using `argparse` in Python to add a boolean flag like `--json`. (2) Best practices for structuring a list of dictionaries to represent multiple status checks (e.g., name, passed, detail). (3) Efficiently calculating an aggregate 'ready' boolean from a list of results (e.g., using `all()`). Return a summary with example code for parsing the flag and dumping the final dictionary to JSON."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Modify phase3_preflight.py for JSON output",
      "description": "Update phase3_preflight.py using Python. Implement the following: (1) Add a `--json` argument using `argparse` that defaults to False. (2) Refactor the logic so that the nine check results are collected into a list of dictionaries, where each dictionary contains 'name', 'passed' (boolean), and 'detail' (string). (3) Calculate an overall 'ready' boolean based on whether all checks passed. (4) If the `--json` flag is True, print a single JSON object containing the list of check results and the 'ready' boolean. (5) If the `--json` flag is False, ensure the script continues to print the original human-readable output exactly as it did before."
    },
    {
      "role": "TESTER",
      "priority": 2,
      "title": "Verify phase3_preflight.py outputs",
      "description": "Run tests on phase3_preflight.py using Python. Test 1: Execute the script without any flags; verify that the output remains in its original human-readable format. Test 2: Execute the script with the --json flag; verify that the output is valid JSON, contains a list of nine objects with the correct keys ('name', 'passed', 'detail'), and includes an overall 'ready' boolean field."
    }
  ]
}
```

## Example 3 — ID: ex_20260611_013148  (quality: silver)

**Goal given to the planner:**

> Goal: Add a keyboard shortcut cheat-sheet window opened with Ctrl+F1: create Dialogs/ShortcutSheetWindow.xaml listing all registered KeyBindings in a two-column table, populated from MainWindow.xaml.cs.

**Plan the planner produced:**

```json
{
  "plan": "Research WPF input bindings and data sharing, then create a modal dialog with a two-column layout populated by a shared collection of keybindings.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research WPF InputBindings and Window Data Sharing",
      "description": "Investigate (1) how to register a Ctrl+F1 hotkey in WPF using <InputBindings> or KeyDown events. (2) Best practices for sharing a list of objects (KeyBinding models) between MainWindow.xaml.cs and a new Dialog window in C# without tight coupling (e.g., static properties vs. Dependency Injection). (3) How to populate a WPF Grid/Table dynamically from a List<T>. Return specific XAML snippets for the shortcut and the C# model definition."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Build Dialogs/ShortcutSheetWindow.xaml",
      "description": "Create a WPF Window named ShortcutSheetWindow in the Dialogs folder. Use a two-column layout (Grid or DataGrid) where Column 0 is 'Key' and Column 1 is 'Description'. Style it as a modal dialog with a title 'Keyboard Shortcuts'. Ensure it uses a BindingContext that expects a collection of objects with 'Key' and 'Description' properties. All styling must be in XAML."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write MainWindow.xaml updates and ShortcutSheetWindow.xaml.cs",
      "description": "Implement the following in C#: (1) Define a public class `ShortcutBinding` with properties `string Key` and `string Description`. (2) In MainWindow.xaml.cs, create a static List<ShortcutBinding> called `AvailableShortcuts` populated with current app shortcuts. (3) Update MainWindow.xaml to include an <InputBindings> section that maps Ctrl+F1 to a Command or Event that opens ShortcutSheetWindow. (4) Write the code-behind for ShortcutSheetWindow.xaml.cs to fetch the data from MainWindow.AvailableShortcuts and assign it to the UI's BindingContext."
    }
  ]
}
```

## Example 4 — ID: ex_20260611_025410  (quality: gold)

**Goal given to the planner:**

> Goal: Add a dry-run toggle to the Swarm Board in SwarmBoardPanel.xaml: when enabled, SwarmSession.cs stops after the boss plan is produced and the board shows the plan with a banner saying no workers were dispatched.

**Plan the planner produced:**

```json
{
  "plan": "Modify SwarmSession.cs to intercept execution after plan generation and update SwarmBoardPanel.xaml to include a dry-run toggle and status banner.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research SwarmSession.cs flow and UI bindings",
      "description": "Investigate the existing code in SwarmSession.cs to identify the specific method where the 'boss plan' is generated and where worker tasks are dispatched. Identify the current data binding between SwarmSession.cs (or its associated ViewModel) and SwarmBoardPanel.xaml. Determine if there is an existing property for session status or if a new one needs to be created to trigger the 'no workers dispatched' banner. Return the exact method names and proposed property names for dry-run state."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update SwarmBoardPanel.xaml with Dry Run UI",
      "description": "Modify SwarmBoardPanel.xaml to include: (1) A CheckBox named 'DryRunToggle' for enabling dry-run mode, styled to match the existing theme. (2) A Banner element (e.g., a Border or StackPanel with a distinct background color) named 'NoWorkersBanner' that is hidden by default. The banner should contain the text 'No workers were dispatched'. Ensure both elements are bound to properties in the underlying ViewModel/Session class as identified by the researcher."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Implement Dry Run logic in SwarmSession.cs",
      "description": "Modify SwarmSession.cs using C# to implement the dry-run feature: (1) Add a boolean property 'IsDryRun' (bound to 'DryRunToggle'). (2) In the execution flow, check if 'IsDryRun' is true immediately after the boss plan is produced. (3) If true, set a status flag or message that triggers the visibility of 'NoWorkersBanner' and terminate the session before any worker tasks are dispatched. Ensure all logic follows existing C# patterns in the project."
    }
  ]
}
```

## Example 5 — ID: ex_20260611_003032  (quality: silver)

**Goal given to the planner:**

> Goal: Create Services/BranchInfoService.cs exposing a CurrentBranch property refreshed every 30 seconds via "git rev-parse --abbrev-ref HEAD", and bind it into the status bar in MainWindow.xaml.cs.

**Plan the planner produced:**

```json
{
  "plan": "Implement a C# WPF service that periodically executes a git command and binds the result to the UI.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research C# shell execution and WPF binding patterns",
      "description": "Investigate (1) how to use System.Diagnostics.Process in C# to execute 'git rev-parse --abbrev-ref HEAD' and capture stdout. (2) How to implement INotifyPropertyChanged in a C# service class for WPF data binding. (3) Best practices for using DispatcherTimer vs System.Timers.Timer in a C# WPF application to update properties on the UI thread safely. Return a summary of specific classes and method signatures."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write Services/BranchInfoService.cs",
      "description": "Create Services/BranchInfoService.cs in C# based on research findings. The class must implement INotifyPropertyChanged. It should expose a public string property 'CurrentBranch' that is updated every 30 seconds. Use a timer (DispatcherTimer or System.Timers.Timer) to execute 'git rev-parse --abbrev-ref HEAD' via System.Diagnostics.Process and update the CurrentBranch property. Ensure thread safety for UI updates."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update MainWindow.xaml and MainWindow.xaml.cs",
      "description": "Modify MainWindow.xaml and MainWindow.xaml.cs in C#. In MainWindow.xaml, add a TextBlock at the bottom of the window to act as a status bar. In MainWindow.xaml.cs, instantiate BranchInfoService and bind its 'CurrentBranch' property to the TextBlock's Text property using WPF DataBinding. Ensure the layout is clean and follows standard WPF conventions."
    }
  ]
}
```

## Example 6 — ID: ex_20260611_014846  (quality: silver)

**Goal given to the planner:**

> Goal: Add auto-scroll to the swarm activity feed in SwarmBoardPanel.xaml: keep the newest entry visible as entries arrive, with a pause-on-hover behavior and a toggle button to disable auto-scroll, implemented in SwarmBoardPanel.xaml.cs.

**Plan the planner produced:**

```json
{
  "plan": "Research WPF auto-scroll and hover detection patterns, then update the XAML layout and implement the C# logic for smooth scrolling with a toggle state.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research WPF C# ScrollViewer auto-scroll and hover detection",
      "description": "Investigate: (1) How to programmatically scroll a WPF ScrollViewer to its maximum vertical offset using C# (e.g., setting VerticalOffset vs. ScrollIntoView). (2) The best way to implement a 'smooth' auto-scroll effect using DispatcherTimer. (3) How to detect mouse hover on a ScrollViewer or ItemsControl container to set a boolean flag (MouseEnter/MouseLeave events). Return a summary of the specific properties and event handlers needed for these three features."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update SwarmBoardPanel.xaml with ToggleButton and Feed naming",
      "description": "Modify SwarmBoardPanel.xaml to include: (1) A ToggleButton named 'AutoScrollToggle' to enable/disable the auto-scroll feature, styled to match the existing UI. (2) Ensure the activity feed container is explicitly defined as a ScrollViewer with x:Name='ActivityFeed'. Use C# in all logic descriptions but only write XAML here."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write SwarmBoardPanel.xaml.cs auto-scroll logic",
      "description": "Implement the following in SwarmBoardPanel.xaml.cs using C#: (1) A private boolean _isHovered and a DispatcherTimer instance. (2) In the constructor or Loaded event, initialize the timer to fire every 100ms. (3) In the timer's Tick event, if AutoScrollToggle.IsChecked is true AND _isHovered is false, set ActivityFeed.VerticalOffset to its maximum value (or use the method found by the researcher). (4) Add MouseEnter and MouseLeave event handlers for ActivityFeed to toggle the _isHovered flag."
    }
  ]
}
```

## Example 7 — ID: ex_20260611_022139  (quality: silver)

**Goal given to the planner:**

> Goal: Add a recent-commits panel: create Services/GitLogReader.cs that parses git log -10 --pretty into commit records, and render hash, date, and subject rows in a collapsible panel in MainWindow.xaml with a copy-hash context menu.

**Plan the planner produced:**

```json
{
  "plan": "Implement a git log parsing service and integrate a collapsible UI panel into the WPF main window using C#.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research C# git execution and WPF collapsible patterns",
      "description": "Investigate: (1) The best way to execute shell commands in C# using System.Diagnostics.Process to run 'git log -10 --pretty=format:%H|%ad|%s'. (2) Parsing techniques for splitting multi-pipe delimited strings into objects. (3) WPF XAML patterns for creating a collapsible list of items, specifically looking at the Expander control vs custom ToggleButton layouts. Return a summary of recommended C# classes and XAML structures."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write Services/GitLogReader.cs",
      "description": "Write Services/GitLogReader.cs in C#. Define a class CommitRecord with properties: Hash (string), Date (DateTime), and Subject (string). Implement a static method GetRecentCommits() that executes 'git log -10 --pretty=format:%H|%ad|%s' via System.Diagnostics.Process. Parse each line into a List<CommitRecord> using the pipe delimiter. Ensure it handles cases where git is not installed or the directory is not a repository by returning an empty list."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update MainWindow.xaml and MainWindow.xaml.cs",
      "description": "Update MainWindow.xaml and MainWindow.xaml.cs in C# / XAML. Add a new UI section containing an Expander control for 'Recent Commits'. Inside the Expander, use an ItemsControl (or DataGrid) to display the CommitRecord objects from GitLogReader. Each row should show the Hash, Date, and Subject. Implement a ContextMenu on the Hash column with a 'Copy' button that uses Clipboard.SetText() to copy the hash string. In the code-behind, call GitLogReader.GetRecentCommits() on initialization to populate the UI."
    }
  ]
}
```

## Example 8 — ID: ex_20260611_025728  (quality: silver)

**Goal given to the planner:**

> Goal: Add a details-pane copy button in ModelWikiWindow.xaml that copies the selected model's full metadata block as formatted plain text via ModelWikiWindow.xaml.cs, with a brief copied confirmation.

**Plan the planner produced:**

```json
{
  "plan": "Update ModelWikiWindow to include a metadata copy button and clipboard functionality using C#.",
  "tasks": [
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write CopyMetadata logic in ModelWikiWindow.xaml.cs",
      "description": "Using C#, update ModelWikiWindow.xaml.cs to implement the following:\n1. Create a public method `CopyMetadata()` that identifies the currently selected model (assume a property `SelectedModel` exists on the class).\n2. Format the properties of `SelectedModel` into a plain text string where each property is represented as 'PropertyName: Value' on a new line.\n3. Use `System.Windows.Clipboard.SetText()` to copy this formatted string to the system clipboard.\n4. Update a UI element named `StatusLabel` (e.g., a TextBlock or Label) to display 'Copied!' for 2 seconds, then clear it. \n5. Ensure all logic is written in C#."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Add Copy Button to ModelWikiWindow.xaml",
      "description": "Update ModelWikiWindow.xaml to include the new functionality:\n1. Add a Button in the details pane area with the content 'Copy Metadata'.\n2. Style the button using standard WPF styles to ensure it matches the existing UI theme.\n3. Set the Button's `Click` attribute to `CopyMetadata_Click`.\n4. Ensure there is a TextBlock or Label named `StatusLabel` positioned near the button to display the confirmation message.\n5. All XAML and associated C# logic must strictly follow the project requirements."
    }
  ]
}
```

## Example 9 — ID: ex_20260611_002848  (quality: silver)

**Goal given to the planner:**

> Goal: Write training_pit/scripts/quality_histogram.py that reads reviewed_v1.json and prints example counts grouped by split and quality as an ASCII bar chart.

**Plan the planner produced:**

```json
{
  "plan": "Research Pythonic grouping methods and ASCII bar patterns, then implement the histogram script with specific data handling.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research JSON grouping and ASCII bar patterns",
      "description": "Investigate Python standard library methods for reading JSON files and grouping nested dictionary keys (specifically 'split' and 'quality'). Identify common patterns for generating ASCII bar charts using string multiplication (e.g., '\u2588' * count). Define a sample schema for reviewed_v1.json that includes entries with 'split' (string) and 'quality' (string) fields to ensure consistent parsing logic."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write training_pit/scripts/quality_histogram.py",
      "description": "Write a Python script in training_pit/scripts/quality_histogram.py. The script must: (1) Read a file named reviewed_v1.json from the current directory. (2) Parse the JSON list of objects. (3) Group and count occurrences by 'split' and then by 'quality'. (4) Print an ASCII bar chart to the console where each line represents a split-quality pair, showing the count followed by a visual bar. Use the research findings for efficient grouping logic. Ensure the script handles cases where keys might be missing or the file is empty."
    }
  ]
}
```

## Example 10 — ID: ex_20260611_023613  (quality: silver)

**Goal given to the planner:**

> Goal: Write tools/env_doctor.ps1 in PowerShell that checks the installed versions of dotnet, python, git, and ollama against minimums defined at the top of the script, printing one PASS or FAIL line per tool and exiting 1 when anything fails.

**Plan the planner produced:**

```json
{
  "plan": "Research tool version strings and PowerShell comparison methods, then implement the environment doctor script with a verification step.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research version commands and PowerShell [version] casting",
      "description": "Investigate the exact command-line output for 'dotnet --version', 'python --version', 'git --version', and 'ollama --version'. Identify how to reliably extract only the numeric version string from these outputs (e.g., handling 'Python 3.10.1' vs 'git version 2.34.1'). Provide a PowerShell example of comparing two version strings using the [version] type cast to ensure correct numerical comparison (e.g., ensuring '3.10' is recognized as greater than '3.9')."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write tools/env_doctor.ps1",
      "description": "Create tools/env_doctor.ps1 in PowerShell. The script must: 1. Define variables at the top for minimum versions of dotnet, python, git, and ollama (e.g., $MinDotNet = '6.0.0'). 2. For each tool, check if it exists in the system PATH. 3. If a tool exists, execute its version command, extract the numeric version using the research findings, and compare it against the minimum. 4. Print 'PASS' or 'FAIL' on a new line for each tool. 5. Exit with code 1 if any tool fails to meet the minimum or is missing from the PATH. Use PowerShell syntax exclusively."
    },
    {
      "role": "TESTER",
      "priority": 2,
      "title": "Verify tools/env_doctor.ps1 syntax",
      "description": "Run a syntax check on tools/env_doctor.ps1 by attempting to parse the file content without executing the logic (e.g., using Get-Content and checking for parsing errors). Verify that all four required tools are checked and that the script includes an exit code 1 mechanism for failures."
    }
  ]
}
```

## Example 11 — ID: ex_20260611_025821  (quality: silver)

**Goal given to the planner:**

> Goal: Add an installed-size total to the Model Wiki footer in ModelWikiWindow.xaml showing the combined disk size of all listed models and the count, recomputed when the list refreshes in ModelWikiWindow.xaml.cs.

**Plan the planner produced:**

```json
{
  "plan": "Update ModelWikiWindow to aggregate and display model statistics by researching C# size calculation methods, implementing the logic in the code-behind, and updating the XAML UI.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research C# disk size aggregation and WPF binding updates",
      "description": "Investigate: (1) The most efficient way in C# to calculate the total disk size of multiple files or directories (e.g., using `FileInfo` or `DirectoryInfo`). (2) How to format these sizes into human-readable strings (KB, MB, GB). (3) Best practices for updating properties that are bound to WPF UI elements during a collection refresh, specifically ensuring `INotifyPropertyChanged` is handled correctly if not already present. Return a summary with specific C# snippets for summing sizes and formatting them."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Update ModelWikiWindow.xaml.cs",
      "description": "Using C#, update 'ModelWikiWindow.xaml.cs' to include two public properties: 'TotalSize' (string) and 'ModelCount' (string). Identify the existing method responsible for refreshing or loading the model list (e.g., 'RefreshModels', 'LoadData', or similar) and modify it to iterate through all loaded models. For each model, calculate its disk size based on the researcher's findings, sum the total size, and increment a count. Update 'TotalSize' and 'ModelCount' with the final results formatted as human-readable strings. Ensure these properties trigger UI updates (use `INotifyPropertyChanged` if necessary)."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update ModelWikiWindow.xaml",
      "description": "Using XAML, update 'ModelWikiWindow.xaml' to include a footer section at the bottom of the window. Add two labels or TextBlocks within this footer: one for 'Total Size' and one for 'Model Count'. Bind these elements to the 'TotalSize' and 'ModelCount' properties in 'ModelWikiWindow.xaml.cs'. Apply standard WPF styling to ensure the footer is clearly visible and aligned with the rest of the UI."
    }
  ]
}
```

## Example 12 — ID: ex_20260611_012053  (quality: silver)

**Goal given to the planner:**

> Goal: Add an editor zoom percentage indicator to the MainWindow.xaml status bar that updates live as the font size changes, with a click handler in MainWindow.xaml.cs that resets zoom to 100 percent.

**Plan the planner produced:**

```json
{
  "plan": "Update the WPF application's UI and backend logic using C# to display a live zoom indicator and provide a reset functionality.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research WPF font size to zoom calculation in C#",
      "description": "Investigate how to calculate a zoom percentage based on changes to a font size property in a WPF application using C#. Identify the best way to bind a TextBlock's content to a numeric value that updates dynamically (e.g., via Data Binding or PropertyChanged events). Return a summary of the formula for calculating percentage relative to a base font size (e.g., 12pt) and the recommended binding method."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Modify MainWindow.xaml UI",
      "description": "Modify MainWindow.xaml to add a TextBlock named 'ZoomIndicator' inside the StatusBar. The indicator should display text like 'Zoom: 100%'. Add a Click event handler named 'ResetZoom_Click' to this indicator. Ensure all UI elements are defined using XAML as part of the C# WPF project."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write MainWindow.xaml.cs logic",
      "description": "Write/Update MainWindow.xaml.cs with C# code. Implement a property 'CurrentZoomPercentage' that calculates the zoom level based on the current font size compared to a base value (e.g., 12.0) using the formula provided by the researcher. Ensure this property updates whenever the font size changes. Implement the C# click handler 'ResetZoom_Click' which sets the font size back to the base value and updates the percentage display."
    }
  ]
}
```
