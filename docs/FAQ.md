# TheOrc — FAQ

---

## General

### What is TheOrc?

TheOrc is a Windows-native AI coding assistant that runs 100% locally on your hardware —
no cloud, no subscriptions, no data leaving your machine. It supports single-agent
Plan → Execute coding and multi-agent Swarm mode (boss + specialized workers).

### Is TheOrc free?

Yes. TheOrc is free and open source (MIT license). It always will be.
Optional support via Ko-fi, PayPal, and GitHub Sponsors.

### Does it require an internet connection?

Only for:
- Downloading models from Ollama or HuggingFace (one-time setup)
- The `fetch_url` tool (if the agent uses it during a task)
- The self-improve GitHub issue scanner (optional feature)

All inference is local once models are downloaded.

### Does it send my code to a server?

No. Inference is local. Your code, files, and prompts stay on your machine.

### What operating systems are supported?

**Windows 10/11 (64-bit) only.** The UI is built on WPF (.NET 10).

A cross-platform Docker + Blazor port is on the roadmap (v1.3). The WPF app will
remain the primary Windows experience.

---

## Setup and Models

### Which model should I start with?

Pick based on your VRAM:

| VRAM | Recommended first model |
|---|---|
| 6 GB | `qwen2.5-coder:7b` |
| 8 GB | `qwen2.5-coder:7b-instruct-q5_k_m` |
| 10–12 GB | `qwen2.5-coder:14b` |
| 16 GB | `qwen2.5-coder:14b-instruct-q5_k_m` |

See [MODEL_GUIDE.md](MODEL_GUIDE.md) for the full selection guide.

### Can I use Nemotron Nano 4B?

For short tasks, chat, and as a Tester role worker — yes.
For autonomous file writing or single-agent coding — **no**.

Nemotron Nano 4B is confirmed to truncate `write_file` JSON before closing braces on
large payloads. Zero files were written across 3 T06 test passes.

This is a 4B parameter ceiling, not a configuration issue. See [MODEL_GUIDE.md](MODEL_GUIDE.md).

### Do I need Ollama?

Yes, Ollama is the recommended inference backend. It provides the
`/v1/chat/completions` endpoint TheOrc uses.

llama.cpp direct (bundled `llama-server.exe`) is also supported but marked experimental.

### What is `OLLAMA_NUM_PARALLEL`?

An Ollama environment variable that controls how many models can serve requests concurrently.

Without it, Swarm workers queue instead of running in parallel.
Set it before starting Ollama:
```powershell
$env:OLLAMA_NUM_PARALLEL = "4"
ollama serve
```

Set to at least the number of workers you plan to run simultaneously.

---

## Behavior and Safety

### Will the agent modify my files without asking?

In **Guarded** mode (default), every `write_file` call shows a visual diff before writing.
Every `run_shell` command shows the exact command before running. You approve or reject each.

In **Full Auto** mode, writes and shell commands proceed without prompting.

The trust level is shown as a pill in the status bar and can be changed at any time.

### Can the agent delete my files?

The agent does not have a `delete_file` tool. File deletion can happen through `run_shell`
if the agent constructs a deletion command — which you must approve in Guarded mode.

### What are git checkpoints?

Before every Execute run, TheOrc commits a checkpoint to your workspace's git history:

```
[TheOrc checkpoint] Before execute: <first line of your goal>
```

You can always roll back with `git log` + `git reset`. Checkpoints are real git commits.

### What is the .agent.md file?

`.agent.md` is a rules file in your workspace root. TheOrc injects it into every Plan and
Execute system prompt. Use it to tell the agent about your project's conventions, files to
never modify, language/framework choices, etc.

Edit it: **View → Edit Rules File** (`Ctrl+Shift+R`)

---

## Swarm

### How is Swarm different from Single Agent?

- Single Agent: one model, one task at a time, full Plan → Execute loop
- Swarm: Boss decomposes your goal into 2–4 tasks; specialized workers run in parallel

Swarm is faster for complex projects that have parallel work (e.g., "write backend + tests + UI").

### Why does Swarm sometimes run sequentially instead of in parallel?

`OLLAMA_NUM_PARALLEL` is not set. See above.

### Can I add new swarm roles?

No. The four roles (RESEARCHER, CODER, UIDEVELOPER, TESTER) are the complete architecture.
No new execution roles will be added. See [SWARM_GUIDE.md](SWARM_GUIDE.md) for what each role does.

### Why is `gemma4:12b` not recommended as a boss model?

Planning collapse: raw `gemma4:12b` without Modelfile calibration outputs a single task
(`title='Execute goal', description=''`). Use `theorc-boss:gemma4` instead — it is the
same base model wrapped with a calibrated Modelfile (`temperature=0.2`, `think=false`,
`num_ctx=16384`, few-shot examples).

---

## Training Pit

### What is the Training Pit?

A system for fine-tuning TheOrc's models through structured data collection and LoRA/QLoRA
training. Currently in Phase 2 (data collection). Phase 3 training is blocked.

### When will training start?

Phase 3 is blocked until ≥150 reviewed positive training examples are in `train_v1.jsonl`.
Current count: 0/150.

See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for details.

### Can I contribute training examples?

Yes. Run swarms to auto-stage boss plans, then review and promote them following
[DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md).

---

## Troubleshooting

### The agent runs but no files are written.

Your model has a payload size ceiling. See [TROUBLESHOOTING.md#agent-runs-but-writes-no-files](TROUBLESHOOTING.md).

Short answer: switch to a model ≥7B. Run the Model Capability Test to confirm.

### The model list is empty.

Ollama is not reachable. Run `ollama serve` and check **Settings → Ollama Host**.

### FlaUI tests fail.

FlaUI tests require an interactive Windows desktop session. See [TESTING_GUIDE.md](TESTING_GUIDE.md).

### More help?

See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for a full diagnostic guide.
