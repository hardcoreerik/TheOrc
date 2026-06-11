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
*Generated 2026-06-10 from training_pit/datasets/train_v1.jsonl (163 examples in split, sample seed 42).*

---

## Example 1 — ID: ex_20260611_013649  (quality: silver)

**Goal given to the planner:**

> Goal: Add a 'Copy as Markdown' context menu item to task cards in SwarmBoardPanel.xaml that renders the task's role, title, and description as a markdown bullet via SwarmBoardPanel.xaml.cs and puts it on the clipboard.

**Plan the planner produced:**

```json
{
  "plan": "Add a 'Copy as Markdown' context menu item by updating the XAML layout and implementing the C# clipboard logic in the code-behind.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research WPF ContextMenu handling for DataTemplates and Clipboard API",
      "description": "Investigate: (1) How to access the DataContext of an element within an ItemsControl or DataTemplate when a ContextMenu item is clicked in WPF. (2) The standard C# method for setting text to the system clipboard using System.Windows.Clipboard.SetText(). Return a summary with specific C# snippets for retrieving data from a UI sender and writing to the clipboard."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update SwarmBoardPanel.xaml to include CopyAsMarkdownItem",
      "description": "Modify SwarmBoardPanel.xaml to add a ContextMenu to the Task Card container. Inside this ContextMenu, add a MenuItem named 'CopyAsMarkdownItem' with the label 'Copy as Markdown'. Ensure the MenuItem has a Click event handler that points to a method in the code-behind. Use XAML for all layout changes."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Implement HandleCopyAsMarkdown in SwarmBoardPanel.xaml.cs",
      "description": "In SwarmBoardPanel.xaml.cs, implement the C# method 'HandleCopyAsMarkdown(object sender, RoutedEventArgs e)'. This method must: (1) Identify the task object from the clicked element's DataContext. (2) Extract the 'Role', 'Title', and 'Description' properties from that object. (3) Format these into a markdown string: \\n- **Role**: [Role]\\n- **Title**: [Title]\\n- **Description**: [Description]. (4) Use System.Windows.Clipboard.SetText() to copy this formatted string to the clipboard. Ensure all logic is written in C#."
    }
  ]
}
```

## Example 2 — ID: ex_20260611_020910  (quality: silver)

**Goal given to the planner:**

> Goal: Create an NUnit test class T14_SettingsStoreTests.cs in OrchestratorIDE.UITests/Tests/ covering Services/SettingsStore.cs: Get returns the default for a missing key, Set then Get round-trips a string and an int, and the SettingChanged event fires exactly once per change. The TESTER lane should run the new tests and report results.

**Plan the planner produced:**

```json
{
  "plan": "Research NUnit event testing patterns in C#, implement the T14_SettingsStoreTests.cs test class, and execute the tests using the dotnet runner.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research NUnit C# event testing and assertions",
      "description": "Investigate: (1) Best practices for verifying that a C# event fires exactly once (e.g., using a local counter or boolean flag within an anonymous method). (2) Standard NUnit assertions for checking default values of missing keys and round-tripping types like string and int. Return a summary with C# code snippets for these specific scenarios."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write OrchestratorIDE.UITests/Tests/T14_SettingsStoreTests.cs",
      "description": "Create the C# test file `OrchestratorIDE.UITests/Tests/T14_SettingsStoreTests.cs` using the NUnit framework. The tests must cover `Services.SettingsStore`. Implement three distinct test cases: (1) Verify that `Get` returns the default value when a key is missing. (2) Verify that `Set` followed by `Get` correctly round-trips both a string and an integer value. (3) Verify that the `SettingChanged` event fires exactly once per change (use a counter to track invocations). Ensure all imports for NUnit and the project's Services namespace are correct."
    },
    {
      "role": "TESTER",
      "priority": 2,
      "title": "Run OrchestratorIDE.UITests/Tests/T14_SettingsStoreTests.cs",
      "description": "Execute the C# unit tests located in `OrchestratorIDE.UITests/Tests/T14_SettingsStoreTests.cs` using the `dotnet test` command or the appropriate NUnit runner. Capture the console output and report the success or failure of each specific test case back to the user."
    }
  ]
}
```

## Example 3 — ID: ex_20260611_021147  (quality: gold)

**Goal given to the planner:**

> Goal: Create an NUnit test class T17_DebounceDispatcherTests.cs in OrchestratorIDE.UITests/Tests/ covering Services/DebounceDispatcher.cs: rapid successive calls collapse to one execution, the action receives the latest invocation, and Throttle allows at most one execution per window. The TESTER lane should run the new tests and report results.

**Plan the planner produced:**

```json
{
  "plan": "Research existing service signatures, implement NUnit tests for debounce and throttle behaviors, and schedule test execution.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Analyze Services/DebounceDispatcher.cs",
      "description": "Investigate the existing file Services/DebounceDispatcher.cs to identify the public methods used for 'Debounce' (collapsing rapid calls) and 'Throttle' (limiting execution per window). Identify the method names, required parameters (e.g., Action, delay), and any initialization requirements. Return a summary of these C# signatures."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write OrchestratorIDE.UITests/Tests/T17_DebounceDispatcherTests.cs",
      "description": "Create a C# NUnit test class in OrchestratorIDE.UITests/Tests/T17_DebounceDispatcherTests.cs to verify the functionality of Services/DebounceDispatcher.cs using the signatures identified by the RESEARCHER. The tests must cover: (1) Debounce Behavior: Verify that rapid successive calls collapse into a single execution and that it uses the data from the final call. (2) Throttle Behavior: Verify that multiple calls within a specific time window result in at most one execution per window. Use standard NUnit attributes ([TestFixture], [Test])."
    },
    {
      "role": "TESTER",
      "priority": 2,
      "title": "Run OrchestratorIDE.UITests/Tests/T17_DebounceDispatcherTests.cs",
      "description": "Execute the C# NUnit tests located in OrchestratorIDE.UITests/Tests/T17_DebounceDispatcherTests.cs using the dotnet test command or equivalent runner. Report the success or failure of each test case."
    }
  ]
}
```

## Example 4 — ID: ex_20260611_012459  (quality: silver)

**Goal given to the planner:**

> Goal: Create a reusable toast notification control Controls/NotificationToast.xaml with a code-behind Controls/NotificationToast.xaml.cs exposing a static Show(string message) method that fades in, holds 3 seconds, and fades out in the corner of MainWindow.xaml.

**Plan the planner produced:**

```json
{
  "plan": "Build a reusable WPF toast notification control using C# and XAML by researching animation patterns, implementing the UI and logic, and verifying the code.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research C# WPF Toast Animations and Window Injection",
      "description": "Investigate: (1) How to use WPF DoubleAnimation for FadeIn and FadeOut effects with specific durations (0.5s). (2) The best way to implement a static Show(string message) method in C# that finds the current active MainWindow via Application.Current.Windows. (3) Techniques for dynamically adding a UserControl to the VisualTree of a window and positioning it at the bottom-right corner. Return a summary of required namespaces and the specific Storyboard logic."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write Controls/NotificationToast.xaml and Controls/NotificationToast.xaml.cs",
      "description": "Create two files: \n1. Controls/NotificationToast.xaml: A WPF UserControl containing a TextBlock named 'MessageText'. Include XAML Storyboards for FadeIn (0.5s), Hold (3s), and FadeOut (0.5s). \n2. Controls/NotificationToast.xaml.cs: Implement a public static void Show(string message) method in C#. This method must find the active MainWindow, instantiate NotificationToast, set MessageText content, add it to the window's visual tree at the bottom-right corner, and trigger the FadeAnimation storyboard."
    },
    {
      "role": "TESTER",
      "priority": 2,
      "title": "Verify syntax of NotificationToast files",
      "description": "Check both Controls/NotificationToast.xaml and Controls/NotificationToast.xaml.cs for correct C# syntax, valid XAML tags, and proper naming of UI elements (MessageText) and Storyboards (FadeAnimation). Ensure the static Show method signature matches the requirements exactly."
    }
  ]
}
```

## Example 5 — ID: ex_20260611_025410  (quality: gold)

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

## Example 6 — ID: ex_20260611_024049  (quality: silver)

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

## Example 7 — ID: ex_20260611_015857  (quality: silver)

**Goal given to the planner:**

> Goal: Add a per-model notes field to the Model Wiki detail pane in ModelWikiWindow.xaml: a multiline TextBox whose contents save to .orc/model_notes.json keyed by model id via ModelWikiWindow.xaml.cs, loaded when the selection changes.

**Plan the planner produced:**

```json
{
  "plan": "Modify the existing WPF window to include a multiline TextBox for notes, then implement C# logic to persist these notes in a JSON file keyed by model ID.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research C# JSON serialization and WPF SelectionChanged patterns",
      "description": "Investigate: (1) Using System.Text.Json to read/write a Dictionary<string, string> where keys are unique IDs and values are text strings. (2) Best practices for handling selection change events in WPF to trigger UI updates from a data source. (3) How to ensure the .orc directory exists before writing the json file using System.IO. Return a summary of the specific C# methods needed."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Modify ModelWikiWindow.xaml to add NotesTextBox",
      "description": "Update ModelWikiWindow.xaml to include a multiline TextBox in the detail pane. \n- Name the control 'NotesTextBox'.\n- Set AcceptsReturn='True' and VerticalScrollBarVisibility='Auto'.\n- Ensure it is styled to fit within the existing layout.\n- Use XAML for this UI update."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Modify ModelWikiWindow.xaml.cs for JSON persistence",
      "description": "Update ModelWikiWindow.xaml.cs using C# to handle note persistence:\n1. Create a method LoadNote(string modelId) that reads from .orc/model_notes.json (using System.Text.Json) and updates NotesTextBox.Text.\n2. Create a method SaveNote(string modelId, string text) that writes the updated dictionary back to .orc/model_notes.json.\n3. Subscribe to the selection change event of the model list to call LoadNote() when a new model is selected.\n4. Subscribe to the TextChanged event of NotesTextBox to call SaveNote() whenever the user types."
    }
  ]
}
```

## Example 8 — ID: ex_20260610_063159  (quality: gold)

**Goal given to the planner:**

> Goal: After a swarm run that included a TESTER task completes, display a colored verdict summary card at the top of the Boss output tab in SwarmBoardPanel. The card should show PASS (green), PARTIAL (yellow), FAIL (red), or SKIPPED (gray) based on the existing TesterVerdict enum in SwarmSession.cs. Wire it to the existing OnSwarmComplete event in SwarmBoardPanel.xaml.cs.

**Plan the planner produced:**

```json
{
  "plan": "Update SwarmBoardPanel with a colored verdict summary card by researching existing session data structures, implementing the event-driven logic in C#, and creating the styled XAML UI component.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research SwarmSession.cs and SwarmBoardPanel.xaml.cs",
      "description": "Identify the exact definition of the 'TesterVerdict' enum in 'SwarmSession.cs'. Locate the 'OnSwarmComplete' event handler in 'SwarmBoardPanel.xaml.cs'. Analyze the current layout of the Boss output tab in 'SwarmBoardPanel.xaml' to determine where to insert the new Verdict Card. Return a summary of: (1) Enum values for TesterVerdict, (2) The signature of OnSwarmComplete, and (3) The name of the container element at the top of the Boss output tab."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Update SwarmBoardPanel.xaml.cs logic",
      "description": "Modify 'SwarmBoardPanel.xaml.cs' to handle the 'OnSwarmComplete' event. Inside the handler, retrieve the 'TesterVerdict' from the current 'SwarmSession'. Create a public property 'CurrentVerdict' (string) and 'VerdictColor' (Brush/Color). Update these properties based on the enum: PASS -> Green, PARTIAL -> Yellow, FAIL -> Red, SKIPPED -> Gray. Ensure the UI updates automatically when these values change."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Create VerdictCard in SwarmBoardPanel.xaml",
      "description": "Add a new UI element (e.g., a Border or Grid) at the top of the Boss output tab in 'SwarmBoardPanel.xaml'. Style it as a summary card with high visibility. Bind its text to the 'CurrentVerdict' property and its background color to the 'VerdictColor' property defined by the CODER. Use standard WPF/XAML styling for Green (#2ecc71), Yellow (#f1c40f), Red (#e74c3c), and Gray (#95a5a6)."
    }
  ]
}
```

## Example 9 — ID: ex_20260611_024901  (quality: gold)

**Goal given to the planner:**

> Goal: Add a workspace-statistics popover opened from the MainWindow.xaml status bar: total files, total size, and per-extension counts computed on a background task in MainWindow.xaml.cs with a loading spinner while it scans.

**Plan the planner produced:**

```json
{
  "plan": "Implement a non-blocking workspace statistics scanner using a C# background service and a WPF Popup UI.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research C# System.IO and WPF async patterns",
      "description": "Investigate the following in C#: (1) The most efficient way to recursively enumerate files in a directory using `System.IO.Directory.EnumerateFiles` vs `GetFiles`, specifically handling large directories without memory spikes. (2) Best practices for aggregating file sizes and extensions into a Dictionary<string, int> during a single pass of the filesystem. (3) WPF `Popup` control usage within a `StatusBar` or at the bottom of a `Grid`. Return a summary of the recommended C# methods and any specific threading considerations for updating UI elements from background tasks."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Write WorkspaceStatsService.cs",
      "description": "Create a C# class `WorkspaceStatsService`. Define a DTO class `WorkspaceStats` with properties: `TotalFiles` (long), `TotalSize` (long), and `ExtensionCounts` (Dictionary<string, int>). Implement an async method `GetStatsAsync(string rootPath)` that returns `Task<WorkspaceStats>`. This method must use `System.IO.Directory.EnumerateFiles` to recursively scan the path, calculating total files, total size in bytes, and counting occurrences of each file extension. Ensure the logic runs on a background thread using `Task.Run`."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update MainWindow.xaml and MainWindow.xaml.cs",
      "description": "Modify `MainWindow.xaml` to add a Status Bar at the bottom containing a button/icon to trigger statistics. Create a WPF `Popup` (or a hidden `Grid` container) that displays: (1) A 'Loading' spinner (ProgressBar with `IsIndeterminate='True'`), (2) Labels for 'Total Files', 'Total Size' (formatted as MB/GB), and (3) an `ItemsControl` or `ListBox` showing the extension counts. In `MainWindow.xaml.cs`, implement the click handler to call `WorkspaceStatsService.GetStatsAsync`. Use `await` to prevent UI freezing, show the loading spinner while the task is running, and update the Popup content once the result is returned."
    }
  ]
}
```

## Example 10 — ID: ex_20260611_014020  (quality: gold)

**Goal given to the planner:**

> Goal: Show a rough token estimate on each task card in SwarmBoardPanel.xaml computed from the description length divided by four, exposed as an EstimatedTokens property on SwarmTask in SwarmTask.cs and rendered via SwarmBoardPanel.xaml.cs.

**Plan the planner produced:**

```json
{
  "plan": "Update the SwarmBoard system to display a token estimate by modifying the data model, XAML layout, and code-behind logic in C#.",
  "tasks": [
    {
      "role": "CODER",
      "priority": 2,
      "title": "Modify SwarmTask.cs",
      "description": "In SwarmTask.cs, add a new public property named 'EstimatedTokens' of type double. Implement logic (either in the constructor or as a calculated property) so that 'EstimatedTokens' is automatically set to the length of the 'Description' string divided by four. Ensure all implementation details use C#."
    },
    {
      "role": "UIDEVELOPER",
      "priority": 2,
      "title": "Update SwarmBoardPanel.xaml",
      "description": "Modify SwarmBoardPanel.xaml to include a UI element (such as a TextBlock) on each task card that displays the 'EstimatedTokens' value. Use XAML for this update and ensure it aligns with the existing styling of the board panel."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Modify SwarmBoardPanel.xaml.cs",
      "description": "Update SwarmBoardPanel.xaml.cs to ensure that the data binding correctly connects the 'EstimatedTokens' property from the SwarmTask objects to the new UI elements in SwarmBoardPanel.xaml. Ensure all implementation details use C#."
    }
  ]
}
```

## Example 11 — ID: ex_20260611_002825  (quality: silver)

**Goal given to the planner:**

> Goal: Add a 300ms search debounce to the Model Wiki search box using a DispatcherTimer in ModelWikiWindow.xaml.cs so filtering runs only after typing pauses.

**Plan the planner produced:**

```json
{
  "plan": "Research the C# DispatcherTimer debounce pattern for WPF, then modify ModelWikiWindow.xaml.cs to wrap the existing filtering logic with a 300ms delay.",
  "tasks": [
    {
      "role": "RESEARCHER",
      "priority": 1,
      "title": "Research DispatcherTimer debounce patterns in C# WPF",
      "description": "Investigate the standard implementation of a 'debounce' mechanism using `System.Windows.Threading.DispatcherTimer` in a C# WPF application.\\n\\nSpecifically: (1) How to correctly stop and restart the timer on every keystroke/text change event. (2) The best way to handle the Tick event to execute a search or filter method. (3) Ensure thread safety when accessing UI elements from the Tick handler (though DispatcherTimer usually runs on the UI thread). Return a summary with the specific code snippets for stopping, starting, and handling the Tick event."
    },
    {
      "role": "CODER",
      "priority": 2,
      "title": "Modify ModelWikiWindow.xaml.cs for debounced search",
      "description": "Update `ModelWikiWindow.xaml.cs` to implement a 300ms debounce on the wiki search box.\\n\\n1. Declare a private field: `private System.Windows.Threading.DispatcherTimer _searchTimer;`.\\n2. In the window constructor or initialization method, initialize `_searchTimer`: set its interval to 300ms and subscribe to the `Tick` event.\\n3. Locate the existing text change event handler for the search box (e.g., `TextChanged`). Modify it so that every time the text changes:\\n   - Call `_searchTimer.Stop();`\\n   - Call `_searchTimer.Start();`\\n4. Implement the `_searchTimer_Tick` handler. This handler must call the existing filtering logic (identify the current filter function, e.g., `UpdateFilter()` or `PerformSearch()`) exactly once when the timer expires.\\n5. Ensure all code is written in C# as per project requirements."
    },
    {
      "role": "TESTER",
      "priority": 2,
      "title": "Verify search debounce in ModelWikiWindow.xaml.cs",
      "description": "Verify that the search box correctly debounces input using the DispatcherTimer.\\n\\n1. Simulate rapid typing in the search box (e.g., typing 'Apple' quickly). Verify that the filtering logic is NOT triggered for every individual character.\\n2. Wait for a pause of at least 300ms after typing and verify that the filter results update exactly once.\\n3. Verify that if the user continues typing before 300ms has passed, the previous timer tick is effectively ignored or reset.\\n4. Report any instances where the UI freezes or multiple filters are executed simultaneously."
    }
  ]
}
```

## Example 12 — ID: ex_20260611_023613  (quality: silver)

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
