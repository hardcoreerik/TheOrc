#!/usr/bin/env python3
"""
_make_test_fixtures.py — Create synthetic plan capture fixtures for end-to-end testing.
Writes 4 fixture files to .orc/swarm/dataset-staging-test/:
  - good_001 (score=85, silver) — 3-task CLI plan, clean JSON
  - good_002 (score=92, gold)  — 3-task WPF plan, clean JSON
  - good_003 (score=65)        — marginal, should be SKIPPED by converter (below min-score)
  - good_004 (score=80, silver) — 2-task FastAPI plan, clean JSON

Run from repo root:
    python training_pit/scripts/_make_test_fixtures.py
"""
import json
import sys
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

staging = Path(".orc/swarm/dataset-staging-test")
staging.mkdir(parents=True, exist_ok=True)

fixtures = [
    ("plan_capture_good_test_001_085.json", {
        "schema_version": "1.0",
        "example_id": "ex_test_001",
        "run_id": "test_001",
        "captured_at": "2026-06-09T10:00:00Z",
        "source": "swarm_run",
        "boss_model": "theorc-boss:gemma4",
        "benchmark_id": None,
        "goal": "Build a Python CLI tool that watches a directory for new CSV files and appends them to a master Parquet file using pandas.",
        "domain": "python_cli",
        "difficulty": 2,
        "plan": {
            "plan": "Use watchdog for FS events, pandas for CSV to Parquet append, click for CLI entry point.",
            "tasks": [
                {
                    "role": "RESEARCHER",
                    "priority": 1,
                    "title": "Research watchdog and pandas Parquet append API",
                    "description": "Investigate the watchdog FileSystemEventHandler API for on_created events. Find the pandas read_csv and DataFrame.to_parquet append pattern (engine=fastparquet or pyarrow). Document the exact imports and method signatures needed."
                },
                {
                    "role": "CODER",
                    "priority": 2,
                    "title": "Write watcher.py and parquet_writer.py",
                    "description": "Implement watcher.py using watchdog.observers.Observer and a FileSystemEventHandler subclass that fires on_created for *.csv files. Call append_to_parquet(path) from parquet_writer.py. In parquet_writer.py, implement append_to_parquet(csv_path: str) using pandas to read the CSV and append to master.parquet."
                },
                {
                    "role": "CODER",
                    "priority": 2,
                    "title": "Write cli.py entry point with click",
                    "description": "Implement cli.py with a @click.command that accepts --watch-dir and --output-file arguments. Import Observer from watcher.py. Start the observer, block on KeyboardInterrupt, then stop cleanly. Add a __main__ guard."
                }
            ]
        },
        "quality_score": 85,
        "rubric_scores": {
            "task_count": 18, "description_depth": 25, "filename_presence": 15,
            "api_contract": 10, "domain_accuracy": 10, "json_validity": 15
        },
        "example_class": "positive",
        "failure_mode": None,
        "correct_plan_reference": None,
        "notes": "",
        "annotator": "auto",
        "tags": []
    }),

    ("plan_capture_good_test_002_092.json", {
        "schema_version": "1.0",
        "example_id": "ex_test_002",
        "run_id": "test_002",
        "captured_at": "2026-06-09T10:05:00Z",
        "source": "swarm_run",
        "boss_model": "theorc-boss:gemma4",
        "benchmark_id": None,
        "goal": "Add a WPF DataGrid to OrchestratorIDE that shows all running swarm tasks with role, status, and elapsed time, updating every 2 seconds.",
        "domain": "csharp_wpf",
        "difficulty": 3,
        "plan": {
            "plan": "Use an ObservableCollection bound to DataGrid, updated by a DispatcherTimer every 2s.",
            "tasks": [
                {
                    "role": "CODER",
                    "priority": 2,
                    "title": "Write SwarmTaskViewModel.cs with INotifyPropertyChanged",
                    "description": "Create SwarmTaskViewModel.cs in OrchestratorIDE/ViewModels/. Implement INotifyPropertyChanged. Properties: Role (string), Title (string), Status (string), ElapsedSeconds (int). Add ElapsedDisplay property formatting ElapsedSeconds as MM:SS. This class will be the ItemsSource type for the DataGrid."
                },
                {
                    "role": "CODER",
                    "priority": 2,
                    "title": "Write SwarmMonitorControl.xaml.cs with DispatcherTimer",
                    "description": "Create SwarmMonitorControl.xaml.cs as a UserControl. Expose ObservableCollection<SwarmTaskViewModel> Tasks. Start a DispatcherTimer with Interval=TimeSpan.FromSeconds(2). On each tick, increment ElapsedSeconds on running tasks. Bind Tasks to DataGrid ItemsSource in SwarmMonitorControl.xaml."
                },
                {
                    "role": "UIDEVELOPER",
                    "priority": 2,
                    "title": "Write SwarmMonitorControl.xaml DataGrid layout",
                    "description": "Create SwarmMonitorControl.xaml. DataGrid with AutoGenerateColumns=False, IsReadOnly=True, ItemsSource bound to Tasks. Add DataGridTextColumns for Role, Title, Status, ElapsedDisplay. Apply AlternatingRowBackground. Style Status column with DataTrigger: green for Running, gray for Done."
                }
            ]
        },
        "quality_score": 92,
        "rubric_scores": {
            "task_count": 18, "description_depth": 25, "filename_presence": 15,
            "api_contract": 10, "domain_accuracy": 10, "json_validity": 15
        },
        "example_class": "positive",
        "failure_mode": None,
        "correct_plan_reference": None,
        "notes": "",
        "annotator": "auto",
        "tags": []
    }),

    ("plan_capture_good_test_003_065.json", {
        "schema_version": "1.0",
        "example_id": "ex_test_003",
        "run_id": "test_003",
        "captured_at": "2026-06-09T10:10:00Z",
        "source": "swarm_run",
        "boss_model": "theorc-boss:gemma4",
        "benchmark_id": None,
        "goal": "Build a file renamer tool.",
        "domain": "python_utility",
        "difficulty": 1,
        "plan": {
            "plan": "Use pathlib to rename files.",
            "tasks": [
                {
                    "role": "CODER",
                    "priority": 2,
                    "title": "Write renamer.py",
                    "description": "Rename files."
                }
            ]
        },
        "quality_score": 65,
        "rubric_scores": {
            "task_count": 0, "description_depth": 5, "filename_presence": 15,
            "api_contract": 10, "domain_accuracy": 10, "json_validity": 15
        },
        "example_class": "marginal",
        "failure_mode": None,
        "correct_plan_reference": None,
        "notes": "Marginal — single task, thin description",
        "annotator": "auto",
        "tags": []
    }),

    ("plan_capture_good_test_004_080.json", {
        "schema_version": "1.0",
        "example_id": "ex_test_004",
        "run_id": "test_004",
        "captured_at": "2026-06-09T10:15:00Z",
        "source": "swarm_run",
        "boss_model": "theorc-boss:gemma4",
        "benchmark_id": None,
        "goal": "Build a Python REST API using FastAPI that exposes a /health endpoint and a /summarize POST endpoint that calls an Ollama model.",
        "domain": "python_web",
        "difficulty": 2,
        "plan": {
            "plan": "FastAPI app with /health and /summarize; Ollama client via httpx.",
            "tasks": [
                {
                    "role": "RESEARCHER",
                    "priority": 1,
                    "title": "Research FastAPI and Ollama httpx client patterns",
                    "description": "Find the FastAPI @app.post() pattern for a JSON request body. Document the Ollama /api/generate endpoint request/response schema. Confirm async httpx.AsyncClient usage for non-blocking calls to Ollama."
                },
                {
                    "role": "CODER",
                    "priority": 2,
                    "title": "Write api.py FastAPI app with /health and /summarize",
                    "description": "Implement api.py with FastAPI(). Define GET /health returning {status: ok}. Define POST /summarize accepting SummarizeRequest(text: str, model: str). Use async httpx.AsyncClient to POST to http://localhost:11434/api/generate. Return the response text as JSON."
                }
            ]
        },
        "quality_score": 80,
        "rubric_scores": {
            "task_count": 12, "description_depth": 25, "filename_presence": 15,
            "api_contract": 10, "domain_accuracy": 10, "json_validity": 15
        },
        "example_class": "positive",
        "failure_mode": None,
        "correct_plan_reference": None,
        "notes": "",
        "annotator": "auto",
        "tags": []
    }),
]

for name, data in fixtures:
    p = staging / name
    p.write_text(json.dumps(data, indent=2), encoding="utf-8")
    print(f"  Wrote {p.name}  (score={data['quality_score']})")

print(f"\nFixtures written to: {staging}/")
