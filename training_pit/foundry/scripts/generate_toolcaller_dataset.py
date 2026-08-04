#!/usr/bin/env python3
"""
generate_toolcaller_dataset.py — Synthetic toolcaller-v0 training data generator.

Generates balanced examples across all 4 decision types (call/no_tool/clarify/unsupported)
and all 4 roles (researcher/coder/ui_developer/tester) by prompting a teacher model with
seeded scenario templates. The decision type and tool are **predetermined** by the
generation recipe — the model only fills in realistic request text and arguments, so the
label cannot drift from the intended category.

Supports four backends (--api):
  swarmcli Preferred local backend. Drives Tools/SwarmCli's `--native-complete` mode --
           the REAL in-process Native Runtime v2 (LLamaSharpRuntime, our own code over
           the LLamaSharp inference kernel). No server, no Ollama, no third-party
           binary. Needs --swarmcli-exe (built swarmcli.exe) and --swarmcli-gguf (a
           local GGUF file).
  native   Use llama-server.exe's OpenAI-compat HTTP server (port 8080). This is a
           THIRD-PARTY server binary (llama.cpp's own, not ours) -- prefer `swarmcli`
           instead. Kept for compatibility with an already-running llama-server.exe.
  claude   Use Anthropic Claude API. Reads ANTHROPIC_API_KEY env var.
           Default model: claude-haiku-4-5-20251001 (fast and high quality).
  ollama   Use a local Ollama instance (--ollama-host / --model). Per memory
           no-ollama-orc-development, this is never auto-selected -- pass --api ollama
           explicitly if you have a specific reason to use it.

For Cerebras-backed generation, use the separate
generate_toolcaller_dataset_cerebras.py script instead (mirrors
generate_v4gold_cerebras.py's standalone pattern — Cerebras is an external development
tool for bulk dataset generation, kept deliberately apart from this multi-backend script).

Each output file is a valid toolcaller-v0 capture JSON placed in:
    training_pit/datasets/toolcaller/

After generation, run Stage 3 (THE FOUNDRY) → "Validate captures" then "Export dataset"
to gate and convert the outputs into training-ready JSONL.

Run from repo root:
    # Native runtime (TheOrc llama.cpp, fully local):
    python training_pit/foundry/scripts/generate_toolcaller_dataset.py --api native --count 200

    # Claude API (auto-detected when ANTHROPIC_API_KEY is set):
    python training_pit/foundry/scripts/generate_toolcaller_dataset.py --count 200

    # Ollama:
    python training_pit/foundry/scripts/generate_toolcaller_dataset.py \\
        --api ollama --model qwen2.5-coder:14b --count 200

Args:
    --api          Backend: "native", "claude", or "ollama".
                   Auto-detect: native if runtime healthy, else claude if key set, else ollama.
    --native-host  Native runtime base URL (default: http://127.0.0.1:8080)
    --claude-model Claude model ID (default: claude-haiku-4-5-20251001)
    --model        Ollama model tag, or explicit model name for native (default: qwen2.5-coder:14b)
    --count        Target valid examples (default: 200)
    --key          Key for output/progress naming (default: toolcaller)
    --ollama-host  Ollama API base URL (default: http://localhost:11434)
    --seed         RNG seed for reproducibility (default: 42)
"""

import argparse
import contextlib
import json
import os
import queue
import random
import re
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

try:
    import requests
except ImportError:
    print("ERROR: requests not installed — run: pip install requests", flush=True)
    sys.exit(1)

# ── Paths ──────────────────────────────────────────────────────────────────────

REPO_ROOT    = Path(__file__).resolve().parents[3]
SCHEMAS_DIR  = REPO_ROOT / "training_pit" / "schemas"
DATASETS_DIR = REPO_ROOT / "training_pit" / "datasets"
CAPTURES_DIR = DATASETS_DIR / "toolcaller"
OUTPUTS_DIR  = REPO_ROOT / "training_pit" / "outputs"

FROZEN_TOOLS_PATH = SCHEMAS_DIR / "toolcaller_v0_frozen_tools.json"
TOOL_SCHEMA_HASH  = "c456ca416882788664b14ea332aa968de76735171a2e53a76eac7c4c6e2bfefd"

# ── Per-role tool subsets (source: SwarmSession.GetWorkerTools) ────────────────
# Must exactly match the frozen inventory in TOOLCALLER_V0_FROZEN_INVENTORY.md

ROLE_TOOLS: dict[str, list[str]] = {
    "researcher":   ["grep_code", "read_file", "list_files", "ask_user"],
    "coder":        ["write_file", "read_file", "run_shell", "list_files", "grep_code", "ask_user"],
    "ui_developer": ["write_file", "read_file", "run_shell", "list_files", "ask_user"],
    "tester":       ["run_shell", "read_file", "list_files", "ask_user"],
}

# ── Decision type distribution ─────────────────────────────────────────────────
# Intentionally biased toward "call" (most common real-world case)

DECISION_WEIGHTS = [
    ("call",        0.42),
    ("no_tool",     0.28),
    ("clarify",     0.20),
    ("unsupported", 0.10),
]

# ── Context seeds: realistic variety without contrived phrasing ────────────────

CALL_SEEDS: dict[str, list[str]] = {
    "write_file": [
        "Create the config file at the specified path",
        "Write the generated migration script to disk",
        "Save the updated implementation",
        "Create a test fixture file with sample data",
        "Write the README for this module",
        "Create the boilerplate source file",
        "Save the patched version with the fix applied",
        "Write the environment config to the output path",
        "Create the schema definition file",
        "Output the generated type definitions",
        "Write the build configuration file",
        "Save the API client stub to the project",
    ],
    "read_file": [
        "Check the current contents before modifying",
        "Read the existing implementation to understand it",
        "Review the config file",
        "Read the test file to see what's already covered",
        "Check what the interface file currently defines",
        "Read the migration history",
        "Review the schema file before updating it",
    ],
    "run_shell": [
        "Build the project to verify the changes compile",
        "Run the test suite",
        "Execute the linter on the changed files",
        "Run the migration",
        "Build and check the output",
        "Execute the setup script",
        "Run the formatter on the source files",
        "Check if the service starts correctly",
    ],
    "list_files": [
        "See what's currently in the directory",
        "Check which files exist before creating a new one",
        "List the test files to find the right one",
        "See the project structure",
        "Check what's in the output directory",
        "Find what configuration files are present",
    ],
    "grep_code": [
        "Find all usages of the symbol across the codebase",
        "Search for the pattern in source files",
        "Find where the function is defined",
        "Search for TODO comments",
        "Find all callers of the method",
        "Search for the import statement",
        "Find all references to the interface",
        "Search for the constant definition",
    ],
    "ask_user": [
        "Clarify which approach to take before proceeding",
        "Ask which output format is expected",
        "Confirm the correct target path before writing",
        "Check whether to overwrite the existing file",
    ],
}

NO_TOOL_SEEDS = [
    "Explain the design approach without looking at any files",
    "Describe the standard pattern for this type of problem",
    "What are the trade-offs between the two approaches?",
    "Summarize how this algorithm works",
    "Describe the differences between these two concepts",
    "What is the best practice here?",
    "How should this type of class be structured?",
    "Explain what this error message typically means",
    "Describe the purpose of this pattern",
    "What does this architectural decision imply?",
]

CLARIFY_SEEDS = [
    "Write the fix",
    "Update the file with the changes",
    "Run the test",
    "Create the file",
    "Add the configuration",
    "Fix the bug in the code",
    "Update it with the new logic",
    "Check the output",
    "Add error handling",
    "Write the implementation for it",
    "Make the change",
    "Do the update",
]

UNSUPPORTED_SEEDS = [
    "Send an email notification about the result",
    "Query the production database for the current record count",
    "Upload the build artifact to the remote storage",
    "Post a message to the team channel",
    "Make an HTTP GET request to the external API",
    "Fetch the dependency package from the registry",
    "Access the version control API to open a pull request",
    "Connect to the remote service and retrieve the data",
    "Call the external webhook endpoint",
    "Download the package from the package manager",
    "Push the container image to the registry",
    "Read from the cloud secrets manager",
]

# ── Generator prompt ────────────────────────────────────────────────────────────

GENERATOR_SYSTEM = """You are a training-data engineer for TheOrc toolcaller model.
Generate ONE synthetic training example as a JSON object.

You will receive a scenario specification with a pre-determined decision type and (for "call") a specific tool.
Your job: generate a realistic, specific request that fits the scenario, and fill in the expected output.

Output format (JSON object only, no prose, no markdown fences):

For decision "call":
{"request": "...", "approval_state": "approved", "expected_decision": "call", "expected_tool": "<name>", "expected_arguments": {"<param>": "<value>"}, "reason_code": null}

For decision "no_tool":
{"request": "...", "approval_state": "n/a", "expected_decision": "no_tool", "expected_tool": null, "expected_arguments": null, "reason_code": null}

For decision "clarify":
{"request": "...", "approval_state": "pending", "expected_decision": "clarify", "expected_tool": null, "expected_arguments": null, "reason_code": "<missing_required_argument|ambiguous_target|ambiguous_intent>"}

For decision "unsupported":
{"request": "...", "approval_state": "n/a", "expected_decision": "unsupported", "expected_tool": null, "expected_arguments": null, "reason_code": "<no_matching_tool|tool_outside_role>"}

Rules:
- The request must be 1-3 sentences, realistic, like something an orchestrator would send to a coding agent.
- For "call": arguments must exactly match the tool schema (correct field names, realistic values). Use realistic file paths like "src/config.json" or "tests/unit/test_auth.cs". For run_shell use real PowerShell/shell commands.
- For "clarify": the request must genuinely be missing a required piece of information (which file? what value? what kind of fix?).
- For "unsupported": the request must ask for something none of the available tools can do (network, database, email, registry, etc.).
- Output ONLY the JSON object."""


def build_generator_prompt(
    role: str,
    decision: str,
    tool: str | None,
    context_hint: str,
    tool_schemas: dict,
) -> str:
    available = ROLE_TOOLS[role]
    lines = [
        f"Role: {role}",
        f"Available tools: {', '.join(available)}",
        "",
        "Tool schemas for available tools:",
    ]
    for t in available:
        if t in tool_schemas:
            s = tool_schemas[t]
            params = s.get("parameters", {})
            req    = s.get("required", [])
            pdesc  = "; ".join(
                f"{k} ({v.get('type','str')}{'*' if k in req else ''}): {v.get('description','')}"
                for k, v in params.items()
            )
            lines.append(f"  {t}: {s.get('description','')} | params: {pdesc}")
    lines += [
        "",
        f"Target decision: {decision}",
    ]
    if tool:
        lines.append(f"Target tool to call: {tool}")
    lines += [
        f"Context hint: {context_hint}",
        "",
        "Generate the example.",
    ]
    return "\n".join(lines)


# ── Ollama client ───────────────────────────────────────────────────────────────

def ollama_generate(host: str, model: str, system: str, prompt: str, timeout: int = 60) -> str:
    url = f"{host.rstrip('/')}/api/generate"
    payload = {
        "model":  model,
        "system": system,
        "prompt": prompt,
        "stream": False,
        "options": {"temperature": 0.8, "top_p": 0.9},
    }
    r = requests.post(url, json=payload, timeout=timeout)
    r.raise_for_status()
    return r.json().get("response", "").strip()


# ── Native runtime client (llama.cpp OpenAI-compat) ─────────────────────────────

def native_generate(host: str, model: str, system: str, prompt: str, timeout: int = 60) -> str:
    """Call the llama.cpp native runtime via POST /v1/chat/completions (non-streaming)."""
    url = f"{host.rstrip('/')}/v1/chat/completions"
    payload = {
        "model":       model,
        "messages":    [
            {"role": "system", "content": system},
            {"role": "user",   "content": prompt},
        ],
        "stream":      False,
        "temperature": 0.8,
        "max_tokens":  512,
    }
    r = requests.post(url, json=payload, timeout=timeout)
    r.raise_for_status()
    return r.json()["choices"][0]["message"]["content"].strip()


def native_list_models(host: str) -> list[str]:
    """Return model IDs currently loaded in the native runtime."""
    r = requests.get(f"{host.rstrip('/')}/v1/models", timeout=10)
    r.raise_for_status()
    return [m["id"] for m in r.json().get("data", [])]


# ── swarmcli --native-complete client (real in-process Native Runtime v2) ────────
#
# native_generate() above talks to llama-server.exe's OpenAI-compat HTTP surface --
# that's a third-party server binary (llama.cpp's own), same category concern as
# Ollama, just a different vendor (see memory no-ollama-orc-development). This backend
# instead drives Tools/SwarmCli's `--native-complete` mode: our own in-process
# LLamaSharpRuntime code, no server, no external process beyond the one subprocess we
# own. The subprocess loads the GGUF once and answers one JSON line per generate() call
# for the life of the batch -- launch a SwarmCliBackend as a context manager and reuse
# it across every call instead of spawning per-example (a fresh LLamaSharp model load
# takes real time).

class SwarmCliBackend:
    """Persistent stdin/stdout wrapper around `swarmcli --native-complete <gguf>`.

    Both stdout and stderr are drained by dedicated background threads into queues for the
    life of the subprocess (started once in __enter__), rather than read synchronously per
    call. Two real bugs this fixes (CodeRabbit review, PR #99):
      - stderr was only ever read once (the startup banner) -- any output after that sat in
        the OS pipe buffer, which fills and blocks the child process on Windows once enough
        accumulates, silently hanging generation with no diagnostic.
      - the startup readiness wait and each generate() call blocked on a plain readline() with
        no timeout at all -- a hung/crashed subprocess would block forever instead of raising.
    """

    def __init__(self, swarmcli_exe: Path, gguf_path: Path, startup_timeout: float = 120.0):
        self.swarmcli_exe = swarmcli_exe
        self.gguf_path    = gguf_path
        self._proc: subprocess.Popen | None = None
        self._startup_timeout = startup_timeout
        self._stdout_queue: "queue.Queue[str | None]" = queue.Queue()
        self._stderr_lines: list[str] = []

    def _pump(self, stream, into_queue: "queue.Queue[str | None] | None") -> None:
        """Background-thread target: reads lines from `stream` forever, either queuing them
        (stdout, one response per generate() call) or just buffering the last few for
        diagnostics (stderr, which is otherwise just noise/progress). Puts None on EOF so a
        blocked queue.get() call unblocks instead of hanging when the process exits."""
        try:
            for line in iter(stream.readline, ""):
                if into_queue is not None:
                    into_queue.put(line)
                else:
                    self._stderr_lines.append(line)
                    if len(self._stderr_lines) > 50:
                        self._stderr_lines.pop(0)
        finally:
            if into_queue is not None:
                into_queue.put(None)

    def __enter__(self) -> "SwarmCliBackend":
        if not self.swarmcli_exe.exists():
            raise FileNotFoundError(f"swarmcli executable not found: {self.swarmcli_exe}")
        if not self.gguf_path.exists():
            raise FileNotFoundError(f"GGUF model not found: {self.gguf_path}")
        self._proc = subprocess.Popen(
            [str(self.swarmcli_exe), "--native-complete", str(self.gguf_path)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", bufsize=1,
        )
        assert self._proc.stdout is not None and self._proc.stderr is not None
        stderr_queue: "queue.Queue[str | None]" = queue.Queue()
        threading.Thread(target=self._pump, args=(self._proc.stdout, self._stdout_queue), daemon=True).start()
        threading.Thread(target=self._pump, args=(self._proc.stderr, None), daemon=True).start()

        # First stderr line is the "loaded ... ready" banner (or a load failure) -- wait for it
        # with a real timeout instead of blocking forever, polling _stderr_lines (populated by
        # the background pump thread above) rather than reading the stream directly.
        deadline = time.monotonic() + self._startup_timeout
        while not self._stderr_lines:
            if self._proc.poll() is not None:
                raise RuntimeError("swarmcli --native-complete exited immediately during startup")
            if time.monotonic() > deadline:
                self._proc.kill()
                self._proc.wait(timeout=10)
                raise TimeoutError(
                    f"swarmcli --native-complete did not report ready within {self._startup_timeout}s")
            time.sleep(0.05)
        if self._proc.poll() is not None:
            raise RuntimeError(f"swarmcli --native-complete exited immediately: {self._stderr_lines[0].strip()}")
        return self

    def __exit__(self, *exc) -> None:
        if self._proc is not None:
            try:
                if self._proc.stdin:
                    self._proc.stdin.close()
                self._proc.wait(timeout=10)
            except Exception:
                self._proc.kill()
                self._proc.wait(timeout=10)

    def generate(self, system: str, prompt: str, max_tokens: int = 512, timeout: float = 60.0) -> str:
        assert self._proc is not None and self._proc.stdin is not None
        req = json.dumps({"system": system, "user": prompt, "max_tokens": max_tokens}, ensure_ascii=False)
        self._proc.stdin.write(req + "\n")
        self._proc.stdin.flush()

        deadline = time.monotonic() + timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(f"swarmcli --native-complete did not respond within {timeout}s")
            try:
                line = self._stdout_queue.get(timeout=remaining)
            except queue.Empty:
                raise TimeoutError(f"swarmcli --native-complete did not respond within {timeout}s")
            if line is None:
                raise RuntimeError("swarmcli --native-complete closed stdout unexpectedly")
            line = line.strip()
            if not line:
                continue
            try:
                resp = json.loads(line)
            except json.JSONDecodeError:
                # Non-JSON diagnostic noise on stdout (should not normally happen, but a crash
                # here would be worse than skipping a stray line) -- keep waiting for the real
                # response within the same deadline rather than raising.
                continue
            if "error" in resp:
                raise RuntimeError(f"swarmcli --native-complete: {resp['error']}")
            return resp["text"].strip()


# ── Claude API client ────────────────────────────────────────────────────────────

def claude_generate(api_key: str, model: str, system: str, prompt: str, timeout: int = 30) -> str:
    """Call Anthropic Messages API. No SDK required — pure requests."""
    url = "https://api.anthropic.com/v1/messages"
    headers = {
        "x-api-key":         api_key,
        "anthropic-version": "2023-06-01",
        "content-type":      "application/json",
    }
    payload = {
        "model":      model,
        "max_tokens": 512,
        "system":     system,
        "messages":   [{"role": "user", "content": prompt}],
    }
    r = requests.post(url, headers=headers, json=payload, timeout=timeout)
    r.raise_for_status()
    data = r.json()
    return data["content"][0]["text"].strip()


# ── JSON extraction ─────────────────────────────────────────────────────────────

_JSON_RE = re.compile(r"\{[\s\S]*\}", re.DOTALL)


def extract_json(text: str) -> dict | None:
    # Strip markdown fences
    text = re.sub(r"```[a-z]*\n?", "", text).strip()
    m = _JSON_RE.search(text)
    if not m:
        return None
    try:
        return json.loads(m.group())
    except json.JSONDecodeError:
        return None


# ── Validation ──────────────────────────────────────────────────────────────────

def validate_and_build_capture(
    raw: dict,
    role: str,
    tool_schemas: dict,
    lineage_id: str,
    example_id: str,
    model_name: str,
    teacher_model: str | None = None,
) -> dict | None:
    """Validate model output and assemble a full toolcaller-v0 capture dict.
    Returns None if the example fails any hard gate."""

    decision = raw.get("expected_decision", "")
    if decision not in ("call", "no_tool", "clarify", "unsupported"):
        return None

    available = ROLE_TOOLS[role]
    request   = (raw.get("request") or "").strip()
    if len(request) < 10:
        return None

    tool      = raw.get("expected_tool")
    arguments = raw.get("expected_arguments")
    reason    = raw.get("reason_code")
    approval  = raw.get("approval_state", "n/a")

    if decision == "call":
        if not tool or tool not in available:
            return None
        if tool not in tool_schemas:
            return None
        if not isinstance(arguments, dict):
            return None
        schema_params = set(tool_schemas[tool].get("parameters", {}).keys())
        required_params = set(tool_schemas[tool].get("required", []))
        # No invented argument keys
        if not set(arguments.keys()).issubset(schema_params):
            return None
        # All required params present and non-empty
        if not required_params.issubset(set(k for k, v in arguments.items() if v)):
            return None
        reason = None

    elif decision in ("clarify", "unsupported"):
        valid_clarify     = {"missing_required_argument", "ambiguous_target", "ambiguous_intent"}
        valid_unsupported = {"no_matching_tool", "tool_outside_role"}
        valid_reasons     = valid_clarify if decision == "clarify" else valid_unsupported
        if not reason or reason not in valid_reasons:
            return None
        tool      = None
        arguments = None

    else:  # no_tool
        tool      = None
        arguments = None
        reason    = None

    # Determine policy_outcome
    if decision == "call":
        # Lightweight risk heuristics (the real ToolPolicyEngine would run in C#)
        risk = "read_workspace"
        destructive = False
        if tool == "write_file":
            risk = "write_workspace"
        elif tool == "run_shell":
            risk = "shell"
            destructive = True
        policy = {
            "evaluated": True,
            "risk_level": risk,
            "is_destructive": destructive,
            "touches_outside_workspace": False,
            "network_access": False,
            "block_reason": None,
            "policy_gap_tool": tool in ("grep_code", "ask_user"),
        }
    else:
        policy = {
            "evaluated": False,
            "risk_level": None,
            "is_destructive": False,
            "touches_outside_workspace": False,
            "network_access": False,
            "block_reason": None,
            "policy_gap_tool": False,
        }

    return {
        "schema_version":    "toolcaller-v0",
        "tool_schema_hash":  TOOL_SCHEMA_HASH,
        "example_id":        example_id,
        "lineage_group_id":  lineage_id,
        "captured_at":       datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "provenance": {
            "source_type":             "synthetic",
            "producing_model":         model_name,
            "teacher_model":           teacher_model,
            "prompt_or_recipe_id":     "generate_toolcaller_dataset.py/v0",
            "derived_from_example_id": None,
        },
        "role":            role,
        "request":         request,
        "available_tools": available,
        "approval_state":  approval if approval in ("approved", "pending", "denied", "n/a") else "n/a",
        "expected": {
            "decision":    decision,
            "tool":        tool,
            "arguments":   arguments,
            "reason_code": reason,
        },
        "policy_outcome": policy,
        "review_status":  "pending",
        "reviewer":       None,
        "split":          None,
        "notes":          f"Synthetic — generated by generate_toolcaller_dataset.py using {model_name}",
        "tags":           ["synthetic"],
    }


# ── Progress tracking ───────────────────────────────────────────────────────────

def write_progress(progress_path: Path, status: str, generated: int, rejected: int, total: int):
    progress_path.parent.mkdir(parents=True, exist_ok=True)
    progress_path.write_text(json.dumps({
        "status":    status,
        "generated": generated,
        "rejected":  rejected,
        "total":     total,
        "pct":       int(100 * generated / max(total, 1)),
        "updated":   datetime.now(timezone.utc).isoformat(),
    }), encoding="utf-8")


# ── Scenario scheduler ──────────────────────────────────────────────────────────

def plan_scenarios(count: int, rng: random.Random) -> list[tuple[str, str, str | None, str]]:
    """Return a list of (role, decision, tool_or_none, context_hint) tuples."""
    decisions      = [d for d, _ in DECISION_WEIGHTS]
    weights        = [w for _, w in DECISION_WEIGHTS]
    roles          = list(ROLE_TOOLS.keys())
    scenarios: list[tuple[str, str, str | None, str]] = []

    for _ in range(count * 4):  # over-generate so we can hit the target after rejects
        decision = rng.choices(decisions, weights=weights, k=1)[0]
        role     = rng.choice(roles)
        available = ROLE_TOOLS[role]
        tool: str | None = None

        if decision == "call":
            # Only pick tools that are in both the role's subset and have call seeds
            callable_tools = [t for t in available if t in CALL_SEEDS]
            if not callable_tools:
                continue
            tool    = rng.choice(callable_tools)
            context = rng.choice(CALL_SEEDS[tool])
        elif decision == "no_tool":
            context = rng.choice(NO_TOOL_SEEDS)
        elif decision == "clarify":
            context = rng.choice(CLARIFY_SEEDS)
        else:
            context = rng.choice(UNSUPPORTED_SEEDS)

        scenarios.append((role, decision, tool, context))
        if len(scenarios) >= count * 3:
            break

    rng.shuffle(scenarios)
    return scenarios


# ── Main ────────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--api",          choices=["swarmcli", "native", "claude", "ollama"], default=None,
                    help="Backend. Auto: claude if ANTHROPIC_API_KEY is set, else swarmcli if the exe+gguf "
                         "resolve; otherwise errors (CodeRabbit review, PR #99: this help text previously "
                         "said the auto-detect falls back to ollama, but it doesn't -- memory "
                         "no-ollama-orc-development means ollama is never auto-selected here, only usable "
                         "via an explicit --api ollama). swarmcli is the real in-process Native Runtime v2 "
                         "(no server, no Ollama) -- prefer it over 'native', which talks to "
                         "llama-server.exe's HTTP server (a third-party binary).")
    ap.add_argument("--native-host",  default="http://127.0.0.1:8080",
                    help="Native llama.cpp SERVER runtime base URL (used when --api native, i.e. llama-server.exe)")
    ap.add_argument("--swarmcli-exe", type=Path,
                    default=Path(__file__).resolve().parents[3] / "Tools" / "SwarmCli" / "bin" / "Debug"
                            / "net10.0-windows" / "swarmcli.exe",
                    help="Path to swarmcli.exe (used when --api swarmcli)")
    ap.add_argument("--swarmcli-gguf", type=Path, default=None,
                    help="Path to the GGUF model swarmcli --native-complete should load (used when --api swarmcli)")
    ap.add_argument("--claude-model", default="claude-haiku-4-5-20251001",
                    help="Claude model ID (used when --api claude)")
    ap.add_argument("--model",        default="qwen2.5-coder:14b",
                    help="Ollama model tag or explicit native model name")
    ap.add_argument("--count",        type=int, default=200)
    ap.add_argument("--key",          default="toolcaller")
    ap.add_argument("--ollama-host",  default="http://localhost:11434")
    ap.add_argument("--seed",         type=int, default=42)
    args = ap.parse_args()

    # Auto-detect API backend: claude → swarmcli. Ollama is never auto-selected (see
    # memory no-ollama-orc-development: any Ollama use here must be a deliberate,
    # explicit --api ollama choice, not a silent fallback). "native" (llama-server.exe)
    # is likewise opt-in only via explicit --api native -- it's a third-party server
    # binary, not ours.
    api_key: str | None = os.environ.get("ANTHROPIC_API_KEY")
    if args.api is None:
        if api_key:
            args.api = "claude"
        elif args.swarmcli_gguf is not None and args.swarmcli_exe.exists() and args.swarmcli_gguf.exists():
            args.api = "swarmcli"
        else:
            print("ERROR: no backend could be auto-selected (no ANTHROPIC_API_KEY, and "
                  "--swarmcli-gguf not given or not found). Pass --api explicitly: "
                  "swarmcli --swarmcli-gguf <path>, claude (needs ANTHROPIC_API_KEY), "
                  "or native (needs a running llama-server.exe). Ollama is never "
                  "auto-selected here.", flush=True)
            sys.exit(1)

    rng = random.Random(args.seed)

    # Load frozen tool schemas
    if not FROZEN_TOOLS_PATH.exists():
        print(f"ERROR: frozen tool schema not found: {FROZEN_TOOLS_PATH}", flush=True)
        sys.exit(1)

    raw_schemas: list[dict] = json.loads(FROZEN_TOOLS_PATH.read_text(encoding="utf-8"))
    tool_schemas = {s["name"]: s for s in raw_schemas}

    CAPTURES_DIR.mkdir(parents=True, exist_ok=True)

    out_dir       = OUTPUTS_DIR / f"gen_{args.key}"
    out_dir.mkdir(parents=True, exist_ok=True)
    progress_path = out_dir / "gen_progress.json"

    # Find the next available example counter (don't overwrite existing captures)
    existing = sorted(CAPTURES_DIR.glob("*.json"))
    counter  = len(existing) + 1

    # Determine canonical model name and teacher provenance
    if args.api == "swarmcli":
        if args.swarmcli_gguf is None:
            print("ERROR: --api swarmcli requires --swarmcli-gguf <path-to-gguf>.", flush=True)
            sys.exit(1)
        active_model  = args.swarmcli_gguf.stem
        teacher_model = active_model  # local model acts as teacher
    elif args.api == "native":
        # Discover model from runtime if not explicitly set
        if args.model and args.model != "qwen2.5-coder:14b":
            active_model = args.model  # user specified a native model name
        else:
            try:
                found = native_list_models(args.native_host)
                active_model = found[0] if found else "native-unknown"
            except Exception:
                active_model = "native-unknown"
        teacher_model: str | None = active_model  # local model acts as teacher
    elif args.api == "claude":
        active_model  = args.claude_model
        teacher_model = args.claude_model  # Claude acts as teacher for Qwen student
    else:
        active_model  = args.model
        teacher_model = None  # Ollama is producer, no separate distillation teacher

    print(f"=== toolcaller dataset generator v0 ===", flush=True)
    print(f"api         : {args.api}", flush=True)
    print(f"model       : {active_model}", flush=True)
    print(f"target count: {args.count}", flush=True)
    print(f"output dir  : {CAPTURES_DIR}", flush=True)
    print(f"progress    : {progress_path}", flush=True)
    print(flush=True)

    # A `with` block guarantees SwarmCliBackend.__exit__ runs even on an exception raised
    # partway through the generation loop (including KeyboardInterrupt) -- manual __enter__/
    # __exit__ calls (the prior version of this code) leave the swarmcli.exe subprocess running
    # with the GGUF still loaded in VRAM if anything throws between the two calls (CodeRabbit
    # review, PR #99). nullcontext() for the non-swarmcli backends means the rest of this
    # function's generation loop can run identically regardless of backend, with the `with`
    # covering both cases uniformly rather than a swarmcli-only special case.
    swarmcli_backend_cm: SwarmCliBackend | contextlib.AbstractContextManager
    if args.api == "swarmcli":
        swarmcli_backend_cm = SwarmCliBackend(args.swarmcli_exe, args.swarmcli_gguf)
    else:
        swarmcli_backend_cm = contextlib.nullcontext()

    if args.api == "swarmcli":
        pass  # readiness printed once the `with` block below actually starts the subprocess
    elif args.api == "native":
        try:
            r = requests.get(f"{args.native_host.rstrip('/')}/health", timeout=10)
            r.raise_for_status()
        except Exception as e:
            print(f"ERROR: Native runtime not reachable at {args.native_host}: {e}", flush=True)
            print("Start TheOrc and load a model in the native runtime first.", flush=True)
            sys.exit(1)
        print(f"Native runtime ready at {args.native_host} · model: {active_model}", flush=True)
    elif args.api == "claude":
        if not api_key:
            print("ERROR: ANTHROPIC_API_KEY environment variable not set. "
                  "Set it or use --api native or --api ollama.", flush=True)
            sys.exit(1)
        try:
            test = claude_generate(api_key, active_model,
                                   "Reply with the word OK.", "OK", timeout=15)
            if not test:
                raise ValueError("empty response")
        except Exception as e:
            print(f"ERROR: Claude API not reachable ({active_model}): {e}", flush=True)
            sys.exit(1)
        print(f"Claude API ready ({active_model})", flush=True)
    else:
        try:
            r = requests.get(f"{args.ollama_host.rstrip('/')}/api/tags", timeout=10)
            r.raise_for_status()
        except Exception as e:
            print(f"ERROR: Ollama not reachable at {args.ollama_host}: {e}", flush=True)
            sys.exit(1)
        print(f"Ollama ready at {args.ollama_host}", flush=True)

    try:
        with swarmcli_backend_cm as swarmcli_backend:
            if args.api == "swarmcli":
                print(f"swarmcli --native-complete ready · model: {active_model} "
                      "(real in-process Native Runtime v2, no server, no Ollama)", flush=True)

            write_progress(progress_path, "running", 0, 0, args.count)

            scenarios = plan_scenarios(args.count, rng)
            generated = 0
            rejected  = 0

            for role, decision, tool, context_hint in scenarios:
                if generated >= args.count:
                    break

                prompt = build_generator_prompt(role, decision, tool, context_hint, tool_schemas)

                try:
                    if args.api == "swarmcli":
                        raw_text = swarmcli_backend.generate(GENERATOR_SYSTEM, prompt)
                    elif args.api == "native":
                        raw_text = native_generate(
                            host=args.native_host,
                            model=active_model,
                            system=GENERATOR_SYSTEM,
                            prompt=prompt,
                        )
                    elif args.api == "claude":
                        raw_text = claude_generate(api_key, active_model, GENERATOR_SYSTEM, prompt)
                    else:
                        raw_text = ollama_generate(
                            host=args.ollama_host,
                            model=active_model,
                            system=GENERATOR_SYSTEM,
                            prompt=prompt,
                        )
                except Exception as e:
                    backend_label = {"swarmcli": "swarmcli --native-complete", "native": "Native runtime",
                                      "claude": "Claude API"}.get(args.api, "Ollama")
                    print(f"  [warn] {backend_label} error: {e}", flush=True)
                    rejected += 1
                    continue

                raw_dict = extract_json(raw_text)
                if raw_dict is None:
                    print(f"  [reject] no JSON in response (decision={decision}, role={role})", flush=True)
                    rejected += 1
                    continue

                ts         = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
                example_id = f"tc_{ts}_{counter:04d}"
                lineage_id = f"tc_lg_{example_id}"

                capture = validate_and_build_capture(
                    raw=raw_dict,
                    role=role,
                    tool_schemas=tool_schemas,
                    lineage_id=lineage_id,
                    example_id=example_id,
                    model_name=active_model,
                    teacher_model=teacher_model,
                )

                if capture is None:
                    print(f"  [reject] validation failed (decision={decision}, role={role})", flush=True)
                    rejected += 1
                    continue

                out_file = CAPTURES_DIR / f"toolcaller_capture_{example_id}.json"
                out_file.write_text(json.dumps(capture, indent=2, ensure_ascii=False), encoding="utf-8")
                generated += 1
                counter   += 1

                req_preview = capture["request"][:60].replace("\n", " ")
                print(f"  [{generated:>4}/{args.count}] {decision:12s} {role:12s} "
                      f"{(tool or '-'):12s} — {req_preview!r}", flush=True)

                write_progress(progress_path, "running", generated, rejected, args.count)

                # Polite pause — Claude API has rate limits; native/Ollama just need breathing room
                time.sleep(0.3 if args.api == "claude" else 0.1)

            write_progress(progress_path, "done", generated, rejected, args.count)
    except (FileNotFoundError, RuntimeError, TimeoutError) as e:
        print(f"ERROR: swarmcli --native-complete failed to start: {e}", flush=True)
        sys.exit(1)

    print(flush=True)
    print(f"=== done: {generated} valid, {rejected} rejected ===", flush=True)
    print(f"Next step: THE FOUNDRY → 'Validate captures' → 'Export dataset' → 'Train toolcaller'", flush=True)

    if generated < args.count:
        print(f"WARNING: only {generated} of {args.count} target reached "
              f"({rejected} rejected). Consider a more capable model or re-run.", flush=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
