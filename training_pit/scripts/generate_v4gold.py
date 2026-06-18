#!/usr/bin/env python3
"""
generate_v4gold.py — Generate synthetic boss-plan training examples via a local Ollama model.

Targets the files_named gap: every CODER/UIDEVELOPER task must include an explicit
output filename in its title.  Rejects any plan that omits one.

Run from repo root:
    python training_pit/scripts/generate_v4gold.py --key v4gold --count 200

Args:
    --model         Ollama model name (default: qwen2.5-coder:14b)
    --count         Target valid examples to generate (default: 200)
    --key           Output dataset key; writes train_{key}.jsonl + eval_{key}.jsonl
    --ollama-host   Ollama base URL (default: http://localhost:11434)
    --seed          RNG seed for goal shuffling (default: 42)
    --eval-split    Fraction reserved for eval set (default: 0.1)
"""

import argparse
import json
import os
import random
import re
import sys
import time
import uuid
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

try:
    import requests
except ImportError:
    print("ERROR: requests not installed — run: pip install requests", flush=True)
    sys.exit(1)

# ── Paths ──────────────────────────────────────────────────────────────────────

REPO_ROOT    = Path(__file__).resolve().parent.parent.parent
DATASETS_DIR = REPO_ROOT / "training_pit" / "datasets"
OUTPUTS_DIR  = REPO_ROOT / "training_pit" / "outputs"

# ── BOSS_SYSTEM_PROMPT ─────────────────────────────────────────────────────────
# Keep in sync with SwarmSession.BossDecomposeSystemPrompt and _generate_examples.py

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
    '      "description": "Detailed, self-contained instructions. Use \\\\n for line breaks inside this string."\n'
    "    }\n"
    "  ]\n"
    "}"
)

# ── Goal templates ─────────────────────────────────────────────────────────────

GOAL_TEMPLATES = [
    # ── Python standalone tools ───────────────────────────────────────────────
    "Build a Python CLI tool that watches a directory for new {ext} files and logs their names and timestamps to watcher.log.",
    "Write a Python script that reads {src}.csv, removes duplicate rows, and writes cleaned data to {src}_clean.csv.",
    "Create a Python web scraper in scraper.py that fetches article titles and URLs from {site} and saves them to articles.json.",
    "Build a Python REST API client in api_client.py that wraps the {service} API with get, post, and delete methods.",
    "Write a Python CLI tool in rename_tool.py that batch-renames files in a directory by replacing spaces with underscores.",
    "Create a Python uptime monitor in monitor.py that polls a URL every 30 seconds and writes status changes to monitor.log.",
    "Build a Python data pipeline in pipeline.py that reads JSONL from stdin, applies a transformation, and writes to stdout.",
    "Write a Python email sender in mailer.py that reads a CSV of recipients and sends templated messages via SMTP.",
    "Create a Python summarizer in summarize.py that calls the Ollama API to summarize text files passed as CLI arguments.",
    "Build a Python file deduplicator in dedup.py that finds duplicate files by SHA-256 hash and writes a report to duplicates.csv.",
    "Write a Python commit-stats tool in git_stats.py that parses git log output and generates a commit frequency report.",
    "Create a Python database exporter in db_export.py that connects to SQLite and exports every table to a separate CSV file.",
    "Build a Python format converter in convert.py that converts between JSON and YAML, accepting --from and --to flags.",
    "Write a Python backup tool in backup.py that zips a source directory and stores it locally with a timestamp in the filename.",
    "Create a Python task runner in tasks.py that reads a task list from tasks.json and executes shell commands in sequence.",
    "Build a Python CSV differ in diff_report.py that compares two files column-by-column and writes differences to diff.csv.",
    "Write a Python image resizer in image_resize.py that batch-resizes images in a folder to a target width, preserving aspect ratio.",
    "Create a Python log parser in log_parser.py that parses nginx access logs and writes per-IP request counts to stats.json.",
    "Build a Python environment checker in env_check.py that validates required env vars are set and prints a pass/fail report.",
    "Write a Python token counter in token_counter.py that counts tokens in text files using tiktoken and writes a report to counts.csv.",
    "Create a Python secret scanner in scan_secrets.py that searches source files for common API key patterns and writes findings to secrets.csv.",
    "Build a Python markdown linter in md_lint.py that checks markdown files for broken links and missing alt text, writing issues to lint.log.",
    "Write a Python fixture generator in gen_fixtures.py that produces randomized test JSONL fixtures conforming to a given schema file.",
    "Create a Python rate limiter benchmark in bench_ratelimit.py that measures throughput under token-bucket rate limiting.",
    "Build a Python CSV merger in merge_csv.py that combines multiple CSV files with matching headers into a single output.csv.",

    # ── C# services and models ────────────────────────────────────────────────
    "Add a C# service class in Services/CacheService.cs that wraps MemoryCache with typed Get, Set, and Invalidate methods.",
    "Create a C# data model in Models/WorkItem.cs with properties for Id, Title, Status, Priority, CreatedAt, and UpdatedAt.",
    "Write a C# background service in Services/HeartbeatService.cs that pings a configured health endpoint every 60 seconds.",
    "Add a C# repository class in Data/TaskRepository.cs that wraps SQLiteConnection with CRUD operations for Task records.",
    "Create a C# string extension class in Extensions/StringExtensions.cs with ToSlug, Truncate, and ToTitleCase methods.",
    "Write a C# plan validator in Validation/PlanValidator.cs that checks boss plan JSON for required fields and role constraints.",
    "Add a C# event bus in Services/EventBus.cs with Subscribe<T>, Unsubscribe<T>, and Publish<T> thread-safe methods.",
    "Create a C# file watcher service in Services/FileWatcherService.cs that monitors a directory and raises typed events on changes.",
    "Write a C# configuration loader in Config/AppConfig.cs that reads appsettings.json and exposes typed property accessors.",
    "Add a C# retry helper in Utilities/RetryHelper.cs with an async ExecuteWithRetry<T> method and configurable back-off.",
    "Create a C# rate limiter in Services/RateLimiter.cs using SemaphoreSlim to cap concurrent outbound API requests.",
    "Write a C# Ollama adapter in Adapters/OllamaAdapter.cs that wraps HttpClient calls to /api/chat with streaming support.",
    "Add a discriminated-union Result type in Models/Result.cs with Ok<T> and Fail factory methods plus IsSuccess and Error properties.",
    "Create a C# metrics collector in Services/MetricsService.cs that tracks request counts and P95 latency per endpoint.",
    "Write a C# migration runner in Data/MigrationRunner.cs that applies numbered SQL script files from a migrations folder in order.",
    "Add a C# plugin loader in Services/PluginLoader.cs that discovers types implementing IPlugin from DLLs in a plugins directory.",
    "Create a C# token bucket in Services/TokenBucket.cs for egress rate limiting with configurable capacity and per-second refill.",
    "Write a C# password hasher in Security/PasswordHasher.cs using PBKDF2-SHA256 with configurable iterations and salt length.",
    "Add a C# workflow state machine in Workflow/StateMachine.cs with configurable transitions, guards, and entry/exit actions.",
    "Create a C# line-diff utility in Utilities/Differ.cs that computes unified-diff output between two strings and returns it as a list.",
    "Write a C# JSON schema validator in Utilities/SchemaValidator.cs using System.Text.Json that checks a JObject against a schema file.",
    "Add a C# circuit breaker in Services/CircuitBreaker.cs that opens after a configurable failure count and resets after a timeout.",
    "Create a C# changelog generator in Tools/ChangelogWriter.cs that reads git log output and writes a formatted CHANGELOG.md.",
    "Write a C# environment probe in Startup/EnvProbe.cs that checks required environment variables at startup and throws if any are missing.",
    "Add a C# HMAC request signer in Security/RequestSigner.cs that adds HMAC-SHA256 Authorization headers to outbound HttpClient calls.",

    # ── Avalonia UI components ─────────────────────────────────────────────────
    "Add an Avalonia settings dialog in UI/Dialogs/SettingsDialog.axaml and SettingsDialog.axaml.cs with tabs for general and appearance.",
    "Create an Avalonia connection status panel in UI/Panels/StatusPanel.axaml and StatusPanel.axaml.cs showing live HIVE health.",
    "Write an Avalonia hex color picker in UI/Controls/ColorPicker.axaml and ColorPicker.axaml.cs with hex input and a live swatch.",
    "Add an Avalonia blocking progress overlay in UI/Controls/ProgressOverlay.axaml and ProgressOverlay.axaml.cs for long operations.",
    "Create an Avalonia log viewer in UI/Panels/LogViewer.axaml and LogViewer.axaml.cs that tails a log file with keyword filtering.",
    "Write an Avalonia notification toast in UI/Controls/Toast.axaml and Toast.axaml.cs that slides in from the corner and auto-dismisses.",
    "Add an Avalonia file tree panel in UI/Panels/FileTreePanel.axaml and FileTreePanel.axaml.cs backed by a real filesystem directory.",
    "Create an Avalonia task card control in UI/Controls/TaskCard.axaml and TaskCard.axaml.cs showing role, title, status, and elapsed time.",
    "Write an Avalonia Ollama model selector in UI/Controls/ModelSelector.axaml and ModelSelector.axaml.cs that lists local Ollama models.",
    "Add an Avalonia markdown renderer in UI/Controls/MarkdownView.axaml and MarkdownView.axaml.cs for displaying formatted plain text.",

    # ── Multi-file / refactor ─────────────────────────────────────────────────
    "Refactor SwarmSession.cs to extract boss-decompose logic into BossDecomposer.cs without changing any public interfaces.",
    "Add pytest unit tests in tests/test_api_client.py for the api_client.py REST wrapper using unittest.mock.",
    "Create a FastAPI server in server.py and a C# client in Services/ServerClient.cs that calls its /summarize endpoint.",
    "Write a Python fixture generator in gen_fixtures.py that produces test JSONL data for the training pipeline validate step.",
    "Add a C# integration test in Tests/HiveIntegrationTest.cs that starts a local HIVE node and asserts peer discovery succeeds.",
    "Build a Python export script in export_adapter.py and a C# loader in Services/AdapterLoader.cs for sharing LoRA adapters.",
    "Create a Python schema validator in validate_schema.py that checks JSONL datasets against a JSON Schema definition file.",
    "Write a Python health probe in health_check.py and a C# endpoint in Controllers/HealthController.cs for deployment readiness.",
    "Build a Python config generator in gen_config.py that reads a .env.template and writes appsettings.json for a target environment.",
    "Add a Python data augmenter in augment.py that paraphrases JSONL training examples using a local Ollama model and writes augmented.jsonl.",
]

# ── Template variable substitution ────────────────────────────────────────────

_VARS: dict[str, list[str]] = {
    "{ext}":     [".csv", ".json", ".log", ".txt", ".md", ".yaml", ".xml"],
    "{src}":     ["data", "records", "users", "orders", "events", "report", "logs"],
    "{site}":    ["a news website", "a tech blog", "a documentation site", "a public RSS feed"],
    "{service}": ["GitHub", "Jira", "Slack", "a REST API", "an internal service"],
}


def _expand(template: str, rng: random.Random) -> str:
    result = template
    for placeholder, choices in _VARS.items():
        if placeholder in result:
            result = result.replace(placeholder, rng.choice(choices), 1)
    return result


# ── Ollama chat ────────────────────────────────────────────────────────────────

def _ollama_chat(host: str, model: str, messages: list[dict], timeout: int = 90) -> str:
    url = f"{host.rstrip('/')}/api/chat"
    payload = {
        "model":   model,
        "messages": messages,
        "stream":  False,
        "options": {"temperature": 0.7, "top_p": 0.9},
    }
    resp = requests.post(url, json=payload, timeout=timeout)
    resp.raise_for_status()
    return resp.json()["message"]["content"]


# ── Validation ────────────────────────────────────────────────────────────────

_VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
_IMPL_ROLES  = {"CODER", "UIDEVELOPER"}
_FILE_RE     = re.compile(
    r'\b[\w\-/]+\.(py|cs|axaml|xaml|ts|tsx|js|html|css|json|sql|md|sh|ps1|rs|go|java|yaml|yml|txt|log|csv)\b',
    re.IGNORECASE,
)
_TESTER_WRITE_KW = re.compile(r'\b(write|create|modify|implement|add|generate)\b', re.IGNORECASE)


def _validate(raw: str) -> tuple[dict | None, str]:
    raw = raw.strip()
    # Strip accidental markdown fences
    if raw.startswith("```"):
        raw = re.sub(r"^```[^\n]*\n?", "", raw)
        raw = re.sub(r"\n?```$", "", raw.rstrip())

    try:
        obj = json.loads(raw)
    except json.JSONDecodeError as exc:
        return None, f"invalid_json:{exc}"

    if not isinstance(obj, dict):
        return None, "not_object"

    tasks = obj.get("tasks")
    if not isinstance(tasks, list) or not (1 <= len(tasks) <= 4):
        return None, f"task_count:{len(tasks) if isinstance(tasks, list) else 'missing'}"

    for t in tasks:
        role = t.get("role", "")
        if role not in _VALID_ROLES:
            return None, f"bad_role:{role!r}"

    # files_named check — every impl task title must contain a filename
    for t in tasks:
        if t.get("role") not in _IMPL_ROLES:
            continue
        title = t.get("title", "")
        if not _FILE_RE.search(title):
            return None, f"files_named_missing:{title!r}"

    # tester-write poison check
    for t in tasks:
        if t.get("role") != "TESTER":
            continue
        desc = t.get("description", "")
        if _FILE_RE.search(desc) and _TESTER_WRITE_KW.search(desc):
            return None, "tester_write_poison"

    return obj, "ok"


# ── Progress ──────────────────────────────────────────────────────────────────

def _write_progress(path: Path, status: str, generated: int,
                    rejected: int, target: int, last_goal: str = "") -> None:
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps({
        "status":    status,
        "generated": generated,
        "rejected":  rejected,
        "target":    target,
        "pid":       os.getpid(),
        "last_goal": last_goal[:120],
    }, ensure_ascii=False), encoding="utf-8")
    tmp.replace(path)


# ── Main ──────────────────────────────────────────────────────────────────────

def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model",       default="qwen2.5-coder:14b")
    ap.add_argument("--count",       type=int,   default=200)
    ap.add_argument("--key",         default="v4gold")
    ap.add_argument("--ollama-host", default="http://localhost:11434")
    ap.add_argument("--seed",        type=int,   default=42)
    ap.add_argument("--eval-split",  type=float, default=0.1)
    args = ap.parse_args()

    rng    = random.Random(args.seed)
    target = args.count

    out_dir       = OUTPUTS_DIR / f"gen_{args.key}"
    out_dir.mkdir(parents=True, exist_ok=True)
    progress_path = out_dir / "gen_progress.json"
    log_path      = out_dir / "gen.log"

    train_path = DATASETS_DIR / f"train_{args.key}.jsonl"
    eval_path  = DATASETS_DIR / f"eval_{args.key}.jsonl"
    DATASETS_DIR.mkdir(parents=True, exist_ok=True)

    print(f"model:  {args.model}",   flush=True)
    print(f"target: {target}",       flush=True)
    print(f"train:  {train_path}",   flush=True)
    print(f"eval:   {eval_path}",    flush=True)

    _write_progress(progress_path, "starting", 0, 0, target)

    generated: int = 0
    rejected:  int = 0
    examples:  list[dict] = []

    goals = GOAL_TEMPLATES.copy()
    rng.shuffle(goals)
    # Cycle if more examples needed than templates
    goal_cycle = (goals * (target // len(goals) + 2))

    def log(msg: str) -> None:
        ts = time.strftime("%H:%M:%S")
        print(f"[{ts}] {msg}", flush=True)

    log(f"generator started — model={args.model} target={target}")

    for raw_goal in goal_cycle:
        if generated >= target:
            break

        goal = _expand(raw_goal, rng)
        messages = [
            {"role": "system", "content": BOSS_SYSTEM_PROMPT},
            {"role": "user",   "content": f"Goal: {goal}"},
        ]

        _write_progress(progress_path, "generating", generated, rejected, target, goal)

        try:
            raw = _ollama_chat(args.ollama_host, args.model, messages)
        except Exception as exc:
            log(f"SKIP  ollama error: {exc}")
            rejected += 1
            continue

        plan_obj, reason = _validate(raw)
        if plan_obj is None:
            log(f"REJECT ({reason}): {goal[:70]}")
            rejected += 1
            continue

        example = {
            "messages": [
                {"role": "system",    "content": BOSS_SYSTEM_PROMPT},
                {"role": "user",      "content": f"Goal: {goal}"},
                {"role": "assistant", "content": json.dumps(
                    plan_obj, separators=(",", ":"), ensure_ascii=False)},
            ],
            "metadata": {
                "category":              "boss_planning",
                "task_type":             "feature_plan",
                "source":                "synthetic_local",
                "quality":               "silver",
                "contains_sensitive_data": False,
                "base_model_target":     "gemma4:12b",
                "generator_model":       args.model,
                "example_id":            str(uuid.uuid4()),
                "created_by":            "generate_v4gold",
            },
        }
        examples.append(example)
        generated += 1
        n_tasks = len(plan_obj["tasks"])
        log(f"[{generated}/{target}] OK  ({n_tasks}t): {goal[:70]}")
        _write_progress(progress_path, "generating", generated, rejected, target, goal)

    # ── Split and write ────────────────────────────────────────────────────
    rng.shuffle(examples)
    n_eval         = max(1, int(len(examples) * args.eval_split))
    eval_examples  = examples[:n_eval]
    train_examples = examples[n_eval:]

    with train_path.open("w", encoding="utf-8") as fh:
        for ex in train_examples:
            fh.write(json.dumps(ex, ensure_ascii=False) + "\n")

    with eval_path.open("w", encoding="utf-8") as fh:
        for ex in eval_examples:
            fh.write(json.dumps(ex, ensure_ascii=False) + "\n")

    _write_progress(progress_path, "done", generated, rejected, target)

    # Write meta sidecar alongside the JSONL files
    reject_rate = round(rejected / max(1, generated + rejected) * 100, 1)
    meta = {
        "description": "",
        "purpose":     "boss_planning",
        "generator":   "generate_v4gold",
        "model":       args.model,
        "created":     time.strftime("%Y-%m-%d"),
        "generated":   generated,
        "rejected":    rejected,
        "reject_rate_pct": reject_rate,
        "train_count": len(train_examples),
        "eval_count":  len(eval_examples),
    }
    meta_path = DATASETS_DIR / f"{args.key}.meta.json"
    meta_path.write_text(json.dumps(meta, indent=2, ensure_ascii=False), encoding="utf-8")

    log(f"done — {len(train_examples)} train · {len(eval_examples)} eval · {rejected} rejected")
    print(f"\ntrain → {train_path}", flush=True)
    print(f"eval  → {eval_path}",   flush=True)
    print(f"meta  → {meta_path}",   flush=True)


if __name__ == "__main__":
    main()
