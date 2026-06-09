#!/usr/bin/env python3
"""
_generate_examples.py — Regenerate training_pit/examples/*.jsonl with the
correct production BOSS_SYSTEM_PROMPT.

Run from repo root:
    python training_pit/scripts/_generate_examples.py

Why this script exists:
    chat_sft_good_*.jsonl files were originally written with an abbreviated
    system prompt using wrong role names (RESEARCHER|CODER|TESTER|DOCS).
    The production model uses RESEARCHER|CODER|UIDEVELOPER|TESTER.
    Training on the wrong system prompt would teach the model to respond to
    a prompt it never sees in production. This script regenerates them with
    the canonical BOSS_SYSTEM_PROMPT from convert_plan_captures.py.

Writes:
    training_pit/examples/chat_sft_good_001.jsonl  — updated system prompt
    training_pit/examples/chat_sft_good_002.jsonl  — updated system prompt
    training_pit/examples/chat_sft_good_003.jsonl  — UI/backend wiring
    training_pit/examples/chat_sft_good_004.jsonl  — refactor planning
    training_pit/examples/chat_sft_good_005.jsonl  — TESTER: verify existing pipeline
    training_pit/examples/chat_sft_eval_collapse_001.jsonl  — collapse (eval only)
    training_pit/examples/chat_sft_synthetic_001.jsonl      — over-decompose (eval only)
"""

import json
import sys
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

# ── BOSS_SYSTEM_PROMPT ─────────────────────────────────────────────────────────
# Keep in sync with SwarmSession.BossDecomposeSystemPrompt (SwarmSession.cs)
# and training_pit/scripts/convert_plan_captures.py
BOSS_SYSTEM_PROMPT = (
    "You are TheOrc — the Orchestrator of a multi-agent AI coding swarm.\n"
    "You direct four specialist minions:\n"
    "  • RESEARCHER  — investigates APIs, libraries, docs; does NOT write production code\n"
    "  • CODER       — writes full implementation code using the researcher's findings\n"
    "  • UIDEVELOPER — writes UI code (XAML, WPF, HTML/CSS) and styling\n"
    "  • TESTER      — runs existing code, executes tests, checks syntax, reports results; does NOT write or modify files\n"
    "\n"
    "Given a user's coding goal, break it into 2–4 concurrent subtasks.\n"
    "Assign each subtask to the best-fit minion role.\n"
    "\n"
    "Rules:\n"
    "- RESEARCHER tasks always get priority 1 (they run first, alone)\n"
    "- CODER, UIDEVELOPER, and TESTER tasks get priority 2 (run concurrently after research)\n"
    "- If no research is needed, skip RESEARCHER and assign CODER/UIDEVELOPER/TESTER tasks directly\n"
    "- TESTER tasks verify code that already exists in the workspace — they do NOT receive output from CODER tasks in the same run\n"
    "- Descriptions must be self-contained — minions cannot ask follow-up questions\n"
    "- Maximum 4 tasks total: up to 1 RESEARCHER + up to 3 CODER/UIDEVELOPER/TESTER\n"
    "- Prefer 3 priority-2 tasks when the goal has distinct implementation concerns\n"
    "\n"
    "FILENAME RULE — task titles MUST name the output file(s):\n"
    '- Good title: "Write scraper.py and ollama_client.py"\n'
    '- Good title: "Build main.py Tkinter UI"\n'
    '- Bad title:  "Implement article fetcher" (no filename — workers won\'t know what to name the file)\n'
    "\n"
    "API CONTRACT RULE — when worker A produces a module that worker B imports:\n"
    "- Decide the EXACT function/class names ONCE and use the same names in BOTH task descriptions.\n"
    "- Example: if CODER writes scraper.py with function fetch_article_text(url), then the UIDEVELOPER task MUST say "
    '"from scraper import fetch_article_text" — not a different name.\n'
    "- This is non-negotiable: mismatched names cause import errors at runtime.\n"
    "\n"
    "Respond with ONLY valid JSON — no markdown fences, no preamble, no trailing text.\n"
    "String values MUST NOT contain literal newlines — use \\\\n inside strings if needed.\n"
    "{\n"
    '  "plan": "one-sentence overall approach",\n'
    '  "tasks": [\n'
    "    {\n"
    '      "role": "RESEARCHER",\n'
    '      "priority": 1,\n'
    '      "title": "Short descriptive title",\n'
    '      "description": "Detailed, self-contained instructions for this minion. Use \\\\n for line breaks inside this string."\n'
    "    }\n"
    "  ]\n"
    "}"
)


def make_example(messages, metadata):
    return json.dumps({"messages": messages, "metadata": metadata}, ensure_ascii=False)


def sys_msg():
    return {"role": "system", "content": BOSS_SYSTEM_PROMPT}


EXAMPLES = {

    # ── chat_sft_good_001.jsonl ─────────────────────────────────────────────
    # Original plan content preserved; system prompt corrected.
    "chat_sft_good_001.jsonl": make_example(
        messages=[
            sys_msg(),
            {"role": "user", "content": "Goal: Build a Python CLI tool that watches a directory for new CSV files and automatically runs a data cleaning script on each one, logging results to cleaned.log."},
            {"role": "assistant", "content": json.dumps({
                "plan": "Researcher surveys watchdog and pandas APIs; two coders build the watcher and the cleaner separately.",
                "tasks": [
                    {
                        "role": "RESEARCHER",
                        "priority": 1,
                        "title": "Research watchdog and pandas APIs",
                        "description": (
                            "Investigate watchdog.observers.Observer and watchdog.events.PatternMatchingEventHandler "
                            "for directory monitoring — find the correct on_created() signature and how to filter for "
                            "*.csv patterns. Research pandas.read_csv() and DataFrame.dropna() for cleaning. Summarize: "
                            "exact import paths, event handler class name and required methods, and a minimal pandas "
                            "cleaning pipeline that drops null rows. Return as a structured summary; do not write any files."
                        )
                    },
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write cleaner.py — CSV cleaning logic",
                        "description": (
                            "Create cleaner.py. Implement clean_csv(input_path: str) -> dict that: "
                            "(1) reads the CSV with pandas.read_csv(input_path), "
                            "(2) records original row count, "
                            "(3) drops all rows with any null via DataFrame.dropna(), "
                            "(4) saves cleaned file to <stem>_cleaned.csv in the same directory, "
                            "(5) returns {\"input\": input_path, \"original_rows\": int, \"cleaned_rows\": int, \"removed_rows\": int}. "
                            "Raises FileNotFoundError if path does not exist. No argparse — this module is imported by watcher.py."
                        )
                    },
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write watcher.py — directory monitor and log writer",
                        "description": (
                            "Create watcher.py. Accept one CLI argument: --watch-dir (default: current directory). "
                            "Import clean_csv from cleaner. Implement CsvHandler(PatternMatchingEventHandler) with "
                            "patterns=['*.csv'] and on_created(event) that calls clean_csv(event.src_path) and appends "
                            "a single log line to cleaned.log: '<ISO8601_TIMESTAMP> cleaned <input_path>: removed <N> rows'. "
                            "Start a watchdog Observer on the watch directory. "
                            "Print 'Watching <dir>. Press Ctrl+C to stop.' on startup. "
                            "Handle KeyboardInterrupt cleanly by calling observer.stop() and observer.join(). "
                            "Validation: run 'python watcher.py --watch-dir .' and drop a test CSV into the watched "
                            "directory — confirm a _cleaned.csv appears and cleaned.log is updated."
                        )
                    }
                ]
            }, separators=(",", ":"), ensure_ascii=False)},
        ],
        metadata={
            "category": "boss_planning",
            "task_type": "feature_plan",
            "source": "manual",
            "quality": "gold",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "user",
            "notes": (
                "Three-task plan. RESEARCHER task separates API discovery from coding. "
                "Clean API contract: cleaner.py is imported by watcher.py — no overlap. "
                "Validation command included in watcher task. "
                "System prompt: canonical BOSS_SYSTEM_PROMPT (corrected 2026-06-09)."
            )
        }
    ),

    # ── chat_sft_good_002.jsonl ─────────────────────────────────────────────
    # Original plan content preserved; system prompt corrected.
    "chat_sft_good_002.jsonl": make_example(
        messages=[
            sys_msg(),
            {"role": "user", "content": (
                "Error log from OrchestratorIDE build:\n\n"
                "Build FAILED.\n"
                "Error MSB3027: Could not copy 'obj\\Debug\\net10.0-windows\\OrchestratorIDE.exe' "
                "to 'bin\\Debug\\net10.0-windows\\OrchestratorIDE.exe'. Exceeded retry count of 10. Failed.\n"
                "Error MSB3021: Unable to copy file 'obj\\Debug\\net10.0-windows\\OrchestratorIDE.exe' "
                "to 'bin\\Debug\\net10.0-windows\\OrchestratorIDE.exe'. "
                "Access to the path '...\\bin\\Debug\\net10.0-windows\\OrchestratorIDE.exe' is denied.\n\n"
                "Goal: Diagnose the root cause and provide the fix."
            )},
            {"role": "assistant", "content": json.dumps({
                "plan": "Single diagnostic task — root cause is a locked output binary from a running process; fix is one PowerShell command.",
                "tasks": [
                    {
                        "role": "CODER",
                        "priority": 1,
                        "title": "Kill the locked OrchestratorIDE.exe process and rebuild",
                        "description": (
                            "Root cause: MSB3027 'Access denied' on the output .exe means the previous build is still "
                            "running and has locked the file — MSBuild cannot overwrite it. This is not a permissions "
                            "issue with the project or the user account. "
                            "Fix: (1) In PowerShell, run: Stop-Process -Name 'OrchestratorIDE' -Force -ErrorAction SilentlyContinue "
                            "— this terminates the running instance and releases the file lock. "
                            "(2) Rebuild: dotnet build OrchestratorIDE.sln --configuration Debug. "
                            "Expected result: build completes with 0 errors. "
                            "If the error recurs on the next build, check Task Manager for any background "
                            "OrchestratorIDE.exe instances and terminate them before building. "
                            "Do not change project permissions, do not run Visual Studio as Administrator "
                            "— the lock is process-based, not ACL-based."
                        )
                    }
                ]
            }, separators=(",", ":"), ensure_ascii=False)},
        ],
        metadata={
            "category": "debugging",
            "task_type": "root_cause_fix",
            "source": "terminal_log",
            "quality": "gold",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "user",
            "notes": (
                "Single-task plan is correct here — the diagnosis and fix are inseparable and there is only one actor. "
                "Root cause explicitly named (process lock, not ACL). PowerShell syntax used, not bash. "
                "Validation criterion included. Correct diagnosis: not a permissions issue, not a project config issue. "
                "System prompt: canonical BOSS_SYSTEM_PROMPT (corrected 2026-06-09)."
            )
        }
    ),

    # ── chat_sft_good_003.jsonl ─────────────────────────────────────────────
    # New: UI/backend wiring with explicit API contract.
    "chat_sft_good_003.jsonl": make_example(
        messages=[
            sys_msg(),
            {"role": "user", "content": (
                "Goal: Add a SwarmMonitor panel to OrchestratorIDE's main window that shows a live list of "
                "running swarm tasks (role, title, status, elapsed time), updating every 2 seconds."
            )},
            {"role": "assistant", "content": json.dumps({
                "plan": "CODER builds the ViewModel and timer logic; UIDEVELOPER builds the XAML DataGrid; both share the SwarmTaskViewModel type.",
                "tasks": [
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write SwarmTaskViewModel.cs and SwarmMonitorControl.xaml.cs",
                        "description": (
                            "Create SwarmTaskViewModel.cs in OrchestratorIDE/ViewModels/. "
                            "Implement INotifyPropertyChanged. Public properties: "
                            "Role (string), Title (string), Status (string), ElapsedSeconds (int). "
                            "Add computed ElapsedDisplay (string) that returns ElapsedSeconds formatted as M:SS. "
                            "Create SwarmMonitorControl.xaml.cs as a UserControl. "
                            "Expose public ObservableCollection<SwarmTaskViewModel> Tasks. "
                            "In constructor, start a DispatcherTimer with Interval = TimeSpan.FromSeconds(2). "
                            "On each Tick, iterate Tasks and call Interlocked.Increment on ElapsedSeconds for tasks "
                            "where Status == 'Running'. "
                            "Expose public void UpdateTasks(IEnumerable<SwarmTaskViewModel> updated) to refresh the list."
                        )
                    },
                    {
                        "role": "UIDEVELOPER",
                        "priority": 2,
                        "title": "Write SwarmMonitorControl.xaml DataGrid layout",
                        "description": (
                            "Create SwarmMonitorControl.xaml. Root element: UserControl. "
                            "Inside a DockPanel, add a DataGrid with Name=TaskGrid, AutoGenerateColumns=False, "
                            "IsReadOnly=True, ItemsSource bound to {Binding Tasks, RelativeSource={RelativeSource AncestorType=UserControl}}. "
                            "Add DataGridTextColumns: Header=Role Binding=Role, Header=Task Binding=Title, "
                            "Header=Status Binding=Status, Header=Elapsed Binding=ElapsedDisplay. "
                            "Set AlternatingRowBackground=#F5F5F5. "
                            "Add a DataTrigger on Status: when value='Running', set Foreground=#2ECC71 on the row. "
                            "When value='Done', set Foreground=#95A5A6. "
                            "Do not hardcode any task data — the DataGrid is purely data-bound to Tasks from "
                            "SwarmMonitorControl.xaml.cs (the ObservableCollection<SwarmTaskViewModel>)."
                        )
                    }
                ]
            }, separators=(",", ":"), ensure_ascii=False)},
        ],
        metadata={
            "category": "boss_planning",
            "task_type": "feature_plan",
            "source": "manual",
            "quality": "gold",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "user",
            "notes": (
                "UI/backend wiring example. Explicit API contract: UIDEVELOPER binds to "
                "ObservableCollection<SwarmTaskViewModel> Tasks defined by CODER — both descriptions name the same type. "
                "No RESEARCHER needed (WPF DataGrid + ObservableCollection are well-known patterns). "
                "2-task plan is correct: the UI and logic are separable but tightly coupled via the shared type. "
                "System prompt: canonical BOSS_SYSTEM_PROMPT."
            )
        }
    ),

    # ── chat_sft_good_004.jsonl ─────────────────────────────────────────────
    # New: refactor planning — preserve existing architecture, target specific files.
    "chat_sft_good_004.jsonl": make_example(
        messages=[
            sys_msg(),
            {"role": "user", "content": (
                "Goal: Refactor OrchestratorIDE's SwarmSession.cs to extract the boss-decompose logic "
                "into a separate BossDecomposer.cs class. Keep RunInternalAsync in SwarmSession. "
                "Do not change any public interface or method signatures visible to the UI layer."
            )},
            {"role": "assistant", "content": json.dumps({
                "plan": "RESEARCHER maps the existing decompose logic and its dependencies; CODER performs the extraction without changing public interfaces.",
                "tasks": [
                    {
                        "role": "RESEARCHER",
                        "priority": 1,
                        "title": "Map RunBossDecomposeAsync dependencies in SwarmSession.cs",
                        "description": (
                            "Read SwarmSession.cs. Identify all code inside or called by RunBossDecomposeAsync(). "
                            "List: (1) every private field it reads (_bossModel, _runId, etc.), "
                            "(2) every method it calls on SwarmSession (ParseBossPlan, Activity, etc.), "
                            "(3) every event it fires (OnTasksPlanned, OnError, etc.), "
                            "(4) the exact signature RunBossDecomposeAsync(string userGoal, ...) needs to keep. "
                            "Produce a dependency manifest. Do NOT edit any files. "
                            "The CODER will use this manifest to decide what to pass as constructor args vs method args."
                        )
                    },
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write BossDecomposer.cs and update SwarmSession.cs",
                        "description": (
                            "Create OrchestratorIDE/Agents/BossDecomposer.cs. "
                            "Move the decompose logic from SwarmSession.RunBossDecomposeAsync() into "
                            "BossDecomposer.DecomposeAsync(string userGoal) using the RESEARCHER's dependency manifest. "
                            "BossDecomposer constructor accepts: IOllamaClient client, string bossModel, string runId, "
                            "ITraceWriter trace, Action<string, string> activityCallback. "
                            "BossDecomposer.DecomposeAsync returns List<SwarmTask>. "
                            "In SwarmSession.RunBossDecomposeAsync(), replace the extracted logic with: "
                            "Tasks = await new BossDecomposer(...).DecomposeAsync(userGoal); "
                            "Preserve ALL existing public method signatures and event signatures in SwarmSession. "
                            "Run: dotnet build OrchestratorIDE.csproj — 0 errors required before marking done."
                        )
                    }
                ]
            }, separators=(",", ":"), ensure_ascii=False)},
        ],
        metadata={
            "category": "boss_planning",
            "task_type": "refactor_plan",
            "source": "manual",
            "quality": "gold",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "user",
            "notes": (
                "Refactor planning example. Key behaviors: RESEARCHER maps dependencies first so CODER knows exactly "
                "what to pass — avoids guessing constructor args. CODER explicitly told to preserve public interfaces. "
                "Build validation command included. No hallucinated types or methods. "
                "System prompt: canonical BOSS_SYSTEM_PROMPT."
            )
        }
    ),

    # ── chat_sft_eval_collapse_001.jsonl ────────────────────────────────────
    # EVAL ONLY — do not promote to train_v1.jsonl.
    # Negative example: classic single-task collapse (gemma4:12b failure mode).
    "chat_sft_eval_collapse_001.jsonl": make_example(
        messages=[
            sys_msg(),
            {"role": "user", "content": (
                "Goal: Build a CSV file cleaner tool with a Python GUI that can trim whitespace, "
                "remove blank rows, remove duplicates, and normalize headers."
            )},
            {"role": "assistant", "content": json.dumps({
                "plan": "",
                "tasks": [
                    {
                        "role": "Coder",
                        "priority": 1,
                        "title": "Execute goal",
                        "description": ""
                    }
                ]
            }, separators=(",", ":"), ensure_ascii=False)},
        ],
        metadata={
            "category": "boss_planning",
            "task_type": "feature_plan",
            "source": "synthetic",
            "quality": "rejected",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "user",
            "notes": (
                "EVAL ONLY — do not promote to train_v1.jsonl. "
                "Classic single_empty_task_collapse pattern: role='Coder' (wrong case), title='Execute goal', "
                "empty description, empty plan. EvalRubric score: ~5/100 (json_validity only). "
                "Use for: regression eval (fine-tuned model must NOT produce this output for this goal), "
                "DPO rejected-output pairing with chat_sft_good_001.jsonl. "
                "Observed in gemma4:12b base model at temp=1.0. Fixed by theorc-boss:gemma4 (temp=0.2 + system prompt). "
                "Correct plan reference: chat_sft_good_001.jsonl (same goal, passing plan)."
            )
        }
    ),

    # ── chat_sft_good_005.jsonl ─────────────────────────────────────────────
    # New: TESTER golden example — verify existing Training Pit pipeline health.
    # Two-task plan: CODER adds pytest coverage, TESTER verifies existing pipeline.
    # Demonstrates correct TESTER scheduling: runs concurrently with CODER,
    # but verifies PRE-EXISTING fixtures/scripts (not CODER's new output).
    "chat_sft_good_005.jsonl": make_example(
        messages=[
            sys_msg(),
            {"role": "user", "content": (
                "Goal: Add pytest smoke tests for the Training Pit scripts (validate_dataset.py "
                "and sanitize_dataset.py) and verify that the existing example files and "
                "end-to-end pipeline are currently healthy."
            )},
            {"role": "assistant", "content": json.dumps({
                "plan": "CODER writes the pytest suite; TESTER runs the existing pipeline health check in parallel — both work on pre-existing files.",
                "tasks": [
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write tests/test_training_pit_scripts.py",
                        "description": (
                            "Create tests/test_training_pit_scripts.py using pytest. "
                            "Test validate_dataset.py: (1) test_valid_example — call validate_file() on "
                            "training_pit/examples/chat_sft_good_001.jsonl, assert return value is 0. "
                            "(2) test_invalid_json — write a temp file with invalid JSON, assert return value is 1. "
                            "(3) test_missing_messages — write a temp file missing the 'messages' key, assert return is 1. "
                            "Test sanitize_dataset.py: (4) test_clean_file — call sanitize_file() on "
                            "training_pit/examples/chat_sft_good_001.jsonl, assert 0 rejects and 0 reviews. "
                            "(5) test_api_key_reject — write a temp file with 'sk-abc123' in content, assert rejects > 0. "
                            "Import validate_file from training_pit.scripts.validate_dataset and "
                            "sanitize_file from training_pit.scripts.sanitize_dataset. "
                            "Add conftest.py in tests/ if needed for sys.path setup."
                        )
                    },
                    {
                        "role": "TESTER",
                        "priority": 2,
                        "title": "Verify pipeline health: all examples pass validate + sanitize",
                        "description": (
                            "Verify the existing Training Pit pipeline is healthy. "
                            "These files already exist — do not wait for any other task in this run. "
                            "Step 1: list_files in training_pit/examples/ — confirm all chat_sft_*.jsonl files are present. "
                            "Step 2: for each chat_sft_*.jsonl file, run: "
                            "python training_pit/scripts/validate_dataset.py <file> "
                            "python training_pit/scripts/sanitize_dataset.py <file> "
                            "Step 3: run the end-to-end test: "
                            "python training_pit/scripts/_make_test_fixtures.py "
                            "python training_pit/scripts/convert_plan_captures.py "
                            "--staging-dir .orc/swarm/dataset-staging-test "
                            "--output training_pit/datasets/staging/health_check.jsonl "
                            "python training_pit/scripts/validate_dataset.py "
                            "training_pit/datasets/staging/health_check.jsonl "
                            "Report STATUS: PASS if all validations return 0 errors and 0 rejects. "
                            "Report STATUS: FAIL with exact error output if any check fails."
                        )
                    }
                ]
            }, separators=(",", ":"), ensure_ascii=False)},
        ],
        metadata={
            "category": "boss_planning",
            "task_type": "feature_plan",
            "source": "manual",
            "quality": "gold",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "user",
            "notes": (
                "TESTER golden example. Key behaviors: "
                "(1) TESTER runs concurrently with CODER but on pre-existing files — no sequential dependency. "
                "(2) TESTER description explicitly states 'these files already exist — do not wait for any other task.' "
                "(3) TESTER task title does NOT name an output file (correct — TESTER produces a report, not a file). "
                "(4) CODER task names its output file (tests/test_training_pit_scripts.py). "
                "(5) No hallucinated APIs — uses actual scripts from the training_pit/scripts/ directory. "
                "System prompt: canonical BOSS_SYSTEM_PROMPT with TESTER advertised."
            )
        }
    ),

    # ── chat_sft_synthetic_001.jsonl ────────────────────────────────────────
    # EVAL ONLY — synthetic over-decomposition stress example.
    # Tests that the rubric and eval loop correctly flag 6-task plans.
    "chat_sft_synthetic_001.jsonl": make_example(
        messages=[
            sys_msg(),
            {"role": "user", "content": (
                "Goal: Build a Python script that reads a CSV file and prints summary statistics."
            )},
            {"role": "assistant", "content": json.dumps({
                "plan": "Read CSV with pandas; compute mean, median, std per column; print formatted summary.",
                "tasks": [
                    {
                        "role": "RESEARCHER",
                        "priority": 1,
                        "title": "Research pandas read_csv API",
                        "description": "Investigate pandas.read_csv() signature and return type. Document arguments."
                    },
                    {
                        "role": "RESEARCHER",
                        "priority": 1,
                        "title": "Research pandas describe() API",
                        "description": "Investigate DataFrame.describe() and .mean() and .std(). Document return types."
                    },
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write csv_loader.py",
                        "description": "Create csv_loader.py with load_csv(path) function using pandas."
                    },
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write stats_calculator.py",
                        "description": "Create stats_calculator.py with compute_stats(df) function."
                    },
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write stats_formatter.py",
                        "description": "Create stats_formatter.py with format_stats(stats) function."
                    },
                    {
                        "role": "CODER",
                        "priority": 2,
                        "title": "Write main.py entry point",
                        "description": "Create main.py that calls csv_loader, stats_calculator, stats_formatter."
                    }
                ]
            }, separators=(",", ":"), ensure_ascii=False)},
        ],
        metadata={
            "category": "boss_planning",
            "task_type": "feature_plan",
            "source": "synthetic",
            "quality": "rejected",
            "contains_sensitive_data": False,
            "base_model_target": "gemma4:12b",
            "created_by": "user",
            "notes": (
                "EVAL ONLY — synthetic over-decomposition stress test. Do not promote to train_v1.jsonl. "
                "Failure modes: (1) over_decomposition — 6 tasks for a trivial script, rule is max 4; "
                "(2) two RESEARCHER tasks (rule: max 1 RESEARCHER); "
                "(3) thin descriptions (< 40 chars each); "
                "(4) unnecessary file fragmentation — a single stats.py would suffice. "
                "EvalRubric score: ~10/100 (task_count=10 penalised, description_depth=2, filename_presence=15, "
                "api_contract=0, domain_accuracy=0, json_validity=15). "
                "Use to verify: rubric correctly flags over-decomposition; eval prompts catch this pattern; "
                "fine-tuned model produces a 2-3 task plan instead."
            )
        }
    ),
}


def main():
    examples_dir = Path("training_pit/examples")
    if not examples_dir.exists():
        print(f"ERROR: {examples_dir} not found — run from repo root")
        sys.exit(1)

    for filename, content in EXAMPLES.items():
        path = examples_dir / filename
        path.write_text(content + "\n", encoding="utf-8")
        # Quick sanity check: parse the JSON we just wrote
        obj = json.loads(content)
        quality = obj["metadata"]["quality"]
        category = obj["metadata"]["category"]
        source = obj["metadata"]["source"]
        n_tasks = len(json.loads(obj["messages"][2]["content"])["tasks"])
        tier = "EVAL-ONLY" if quality == "rejected" else "TRAINING"
        print(f"  {'OK  ':5} {filename:45} ({tier}, {quality}, {n_tasks} tasks)")

    print(f"\nWrote {len(EXAMPLES)} example files to {examples_dir}/")
    print()
    print("Next: validate and sanitize all examples:")
    print("  for f in training_pit/examples/chat_sft_*.jsonl; do")
    print("    python training_pit/scripts/validate_dataset.py $f")
    print("    python training_pit/scripts/sanitize_dataset.py $f")
    print("  done")


if __name__ == "__main__":
    main()
