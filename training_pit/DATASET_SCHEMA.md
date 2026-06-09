# The Training Pit — Canonical Dataset Schema

> **Schema version:** 1.0
> **Status:** Defined. Data collection starts Phase 2.
>
> This is the **canonical** training format for all LoRA/QLoRA fine-tuning in The Training Pit.
> For the specialized boss/swarm plan capture format, see `PLAN_CAPTURE_SCHEMA.md`.

---

## Format: Chat-Style JSONL

Each training example is a single line of JSON (one example per line, no trailing comma).
The format follows the OpenAI messages[] convention used by Unsloth, PEFT/TRL, and most
modern fine-tuning frameworks.

```jsonc
{
  "messages": [
    {
      "role": "system",
      "content": "You are TheOrc boss model. Be direct, patch-focused, Windows-aware, and delegation-capable."
    },
    {
      "role": "user",
      "content": "The task, code, logs, terminal output, repo context, or user request goes here."
    },
    {
      "role": "assistant",
      "content": "The ideal TheOrc-style answer goes here."
    }
  ],
  "metadata": {
    "category": "boss_planning",
    "task_type": "feature_plan",
    "source": "manual",
    "quality": "gold",
    "contains_sensitive_data": false,
    "base_model_target": "gemma4:12b",
    "created_by": "user",
    "notes": ""
  }
}
```

---

## Schema Rules

### `messages[]`

- **Required.** Array of 2–4 message objects.
- Must include at least one `user` turn and one `assistant` turn.
- `system` turn is optional but strongly recommended — use TheOrc's actual system prompt
  where appropriate so training is consistent with inference context.
- The `assistant` message is the **ideal TheOrc-style answer** for this input.
- Do not include private credentials, API keys, or sensitive personal data in any turn.

### `metadata`

All metadata fields are required. `notes` may be empty string.

---

## Valid Field Values

### `category`

| Value | Description |
|---|---|
| `boss_planning` | Boss decomposing a goal into worker tasks |
| `debugging` | Root-cause analysis of an error, log, or failed output |
| `delegation` | Deciding which goblin worker handles a task |
| `minimal_patching` | Targeted code fix without broad rewrite |
| `powershell` | Windows/PowerShell commands, validation, scripting |
| `esp_idf` | ESP-IDF build errors, CMake, embedded C |
| `ollama` | Ollama config, model management, API issues |
| `openclaw` | OpenClaw config and workflow |
| `continue_config` | Continue extension config and setup |
| `react_dashboard` | React/Next.js/dashboard UI tasks |
| `python_utility` | Python scripts, utilities, data processing |
| `validation_commands` | Generating test/verify commands after a fix |
| `uncertainty_handling` | Honest handling of missing context or unknown files |
| `hallucination_resistance` | Refusing to invent files, APIs, or project structure |
| `code_review` | Reviewing a diff or PR without rewriting everything |
| `imported_adapter_eval` | Evaluating whether an imported adapter is safe to use |

### `source`

| Value | Description |
|---|---|
| `manual` | Human-authored ideal answer |
| `corrected_model_output` | Model output that was edited to be correct |
| `terminal_log` | Derived from actual terminal session or build log |
| `repo_issue` | Derived from a real issue in this repo |
| `swarm_capture` | Captured from a live swarm run (see PLAN_CAPTURE_SCHEMA.md for plan captures) |
| `eval_failure` | Created to correct a specific eval failure |
| `synthetic` | Generated from a template or augmented |
| `imported` | From an external dataset or adapter evaluation |

### `quality`

| Value | Description |
|---|---|
| `gold` | Human-reviewed, high-confidence correct answer — use in training |
| `silver` | Plausibly correct, not deeply reviewed — use with caution |
| `draft` | Needs review before training |
| `rejected` | Do not train on this example — kept for audit |

### `base_model_target`

The Ollama model tag for the intended inference model. Usually `gemma4:12b` or
`hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0`. Used to verify adapter compatibility.

---

## Key Principles

1. **This format is the canonical SFT / LoRA / QLoRA training input.** All other capture
   formats (plan captures, terminal logs) should be convertible to this format before training.

2. **The assistant message must represent the ideal answer.** Not what the model currently
   produces — what it *should* produce. If you are capturing a corrected model output,
   mark `source: "corrected_model_output"`.

3. **Train behavior and workflow, not secrets.** See `SAFETY_AND_PRIVACY.md` for what
   must never appear in a training example.

4. **Validate before training.** Run `scripts/validate_dataset.py` on every JSONL file
   before using it in a training job. Run `scripts/sanitize_dataset.py` to scan for secrets.

5. **Hold out examples for eval.** At least 15–20% of examples should be held out in
   `datasets/eval_v1.jsonl` and never used for training. Use them to measure improvement.

6. **Plan captures are a separate format.** See `PLAN_CAPTURE_SCHEMA.md`. They can be
   *converted* to chat JSONL for SFT training, but they have additional plan-quality fields
   that don't belong in the canonical format.

---

## Example: Boss Planning (Gold)

```jsonl
{"messages":[{"role":"system","content":"You are TheOrc boss. Decompose the user goal into 2-4 worker tasks. Respond only in JSON."},{"role":"user","content":"Goal: Build a Python CLI tool that watches a directory and syncs changed files to a remote server via SFTP."},{"role":"assistant","content":"{\"plan\":\"Researcher surveys paramiko SFTP and watchdog APIs; two coders split the sync engine and CLI entry point.\",\"tasks\":[{\"role\":\"RESEARCHER\",\"priority\":1,\"title\":\"Research watchdog and paramiko APIs\",\"description\":\"Investigate watchdog.observers.Observer and watchdog.events.FileSystemEventHandler for directory monitoring. Research paramiko.SSHClient and SFTPClient.put() for file transfer. Return a summary with exact import paths, event handler signatures, and SFTP connection setup code.\"},{\"role\":\"CODER\",\"priority\":2,\"title\":\"Write sync_engine.py — SFTP transfer and change detection\",\"description\":\"Create sync_engine.py. Implement a SyncHandler(FileSystemEventHandler) class with on_modified() and on_created() methods that call upload_file(local_path). Implement upload_file(path) using paramiko SFTPClient. Read host/user/key from a config dict passed at construction. Include retry on connection error.\"},{\"role\":\"CODER\",\"priority\":2,\"title\":\"Write main.py — CLI entry point with argparse\",\"description\":\"Create main.py. Use argparse for: --watch-dir, --remote-host, --remote-user, --key-file, --remote-dir. Import SyncHandler from sync_engine. Start watchdog Observer on the watch directory. Print startup confirmation including watch path and remote host. Handle KeyboardInterrupt cleanly.\"}]}"}],"metadata":{"category":"boss_planning","task_type":"feature_plan","source":"manual","quality":"gold","contains_sensitive_data":false,"base_model_target":"gemma4:12b","created_by":"user","notes":"Three-task plan. Explicit API contract between sync_engine and main."}}
```

---

## JSONL File Format

Each line in `datasets/train_v1.jsonl` or `datasets/eval_v1.jsonl` is one complete JSON
object (no line breaks inside the object). Files must be UTF-8 encoded.

```
{"messages":[...],"metadata":{...}}
{"messages":[...],"metadata":{...}}
...
```

Validate with: `python training_pit/scripts/validate_dataset.py datasets/train_v1.jsonl`

---

## Version History

| Version | Date | Changes |
|---|---|---|
| 1.0 | 2026-06-09 | Initial canonical chat-JSONL schema |
