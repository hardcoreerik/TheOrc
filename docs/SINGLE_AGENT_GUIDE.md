# TheOrc — Single Agent Guide

---

## What Single Agent Mode Is

In Single Agent mode, one model does everything for one task at a time:
planning, writing code, running shell commands, and reading files. There
is no boss/worker split — the model you pick handles the full loop.

Select **Single** in the mode selector (top left of the main window).

---

## The Plan → Execute Loop

Every task in Single Agent mode goes through two stages.

### Stage 1 — Plan

1. Type a goal in the chat input and press **Enter** or click **Plan**
2. TheOrc sends your goal to the model with the current workspace context
3. The model returns a **plan** — a numbered list of steps it intends to take
4. The plan is shown in the chat panel for review

At this point nothing has been written or run. You can:

- **Approve** the plan → move to Execute
- **Reject** the plan → the model sees your rejection and may revise
- **Edit** the goal and re-plan

### Stage 2 — Execute

1. The model executes the plan step by step
2. Before each `write_file` call, TheOrc shows a **visual diff** in the DiffViewer
   - Click **Approve** → the file is written and git checkpoint is updated
   - Click **Reject** → the write is skipped; the model receives a rejection notice
3. Before each `run_shell` call, a **ShellApprovalCard** shows the exact command
   - Click **Approve** → the shell command runs
   - Click **Reject** → the command is skipped

The model can use `read_file`, `list_files`, `grep_code`, and `get_outline` without approval.

---

## Trust Levels

The trust level controls how much the agent can do without asking:

| Trust Level | Write file | Run shell |
|---|---|---|
| **Plan** | Never — plan step only | Never |
| **Guarded** | Requires approval | Requires approval |
| **Standard** | Requires approval | Requires approval |
| **Full Auto** | Auto-approved | Auto-approved |

The default is **Guarded**. Use **Full Auto** only for trusted, well-understood tasks.

Trust level is shown as a pill in the status bar and can be changed at any time.

---

## T06 — Single-Agent Autonomous File Writing

**T06** is the internal benchmark for single-agent autonomous code generation:

> The agent is given a coding goal and must write working project files from start
> to finish without human approval of each step (Full Auto trust, capable model).

T06 is the hardest single-agent task — it requires:
- A model with strong `write_file` JSON reliability for large payloads
- A capable model (≥7B parameters recommended)
- A clear, specific goal with enough context

T06 **will fail** with:
- Models under ~7B parameters (confirmed: Nemotron Nano 4B truncates JSON before closing braces)
- Models that start `write_file` but can't complete large payloads
- Goals that are too vague to produce a coherent plan

See [MODEL_GUIDE.md](MODEL_GUIDE.md) and [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for diagnosis.

---

## Why Small Models Fail Long write_file Tasks

`write_file hello.txt "Hello World"` — ~30 chars — almost any model handles this.

`write_file app.py` with 150 lines of Python (all newlines escaped as `\n`) — ~5 KB JSON string.
The model must maintain JSON schema context, `{` / `}` balance, escape state, and code coherence
for hundreds of tokens. This is a parameter-count capacity issue, not a format issue.

**A LoRA adapter cannot fix this.** Fine-tuning changes adapter weights, not the model's
active parameter count. Nemotron Nano 4B Q8 (the highest quality 4B quantization) still
truncates on pass after pass. Q4 vs Q8 changes precision, not parameter count.

Run **Models → Run Model Capability Test…** to measure exactly what your model can handle.
The FileWriteSmall / FileWriteMedium / FileWriteLarge test suite gives a direct answer.

---

## Recommended Models for Single Agent

| VRAM | Model | Notes |
|---|---|---|
| 6 GB | `qwen2.5-coder:7b` | Smallest viable single-agent model |
| 8 GB | `qwen2.5-coder:7b-instruct-q5_k_m` | Near-lossless 7B |
| 10–12 GB | `qwen2.5-coder:14b` | Strong coder; best at this tier |
| 16 GB | `qwen2.5-coder:14b-instruct-q5_k_m` | High-quality 14B |

Models to avoid for single-agent coding (autonomous file writing):
- `nemotron-3-nano:4b` / `nemotron-3-nano:4b-q8_0` — confirmed fail on long payloads
- Any model below ~7B parameters

For research/chat tasks that do not require `write_file`, smaller models work fine.

---

## How to Pick a Model

1. Open **Models → Model Wiki / Lab…**
2. Browse models by VRAM and scores
3. Check the **Section B — Role Scores** for CoderScore
4. Check **Section C — Observations** for local test results
5. Run **Models → Run Model Capability Test…** to measure FileWriteSmall / Medium / Large

See [MODEL_GUIDE.md](MODEL_GUIDE.md) for profile score definitions.

---

## The Rules File (.agent.md)

The `.agent.md` file in your workspace root is injected into every Plan and Execute prompt.
Use it to tell the model about your project.

Open or edit it: **View → Edit Rules File** (`Ctrl+Shift+R`)

Example useful `.agent.md` content:

```markdown
## Project
This is a Python 3.12 FastAPI project with Alembic migrations.

## Conventions
- Use snake_case for variables and functions
- All new modules must have type hints
- Tests in tests/ using pytest

## Never modify
- alembic/env.py
- src/config.py (contains production settings)
```

The installer generates a profile-specific `.agent.md` for new projects (Web, Systems, Security, etc.).

---

## Workspace and File Paths

Click the **📁 workspace badge** in the top bar to open a folder as your workspace.

- `write_file` paths are resolved relative to the workspace root
- `read_file`, `list_files`, `grep_code` all operate within the workspace
- If the workspace has a `.git` folder, TheOrc commits a checkpoint before every Execute run

---

## Session Logs

Activity logs are shown in the chat panel in real time. They are not persisted to disk by default.

Git checkpoints accumulate in your workspace git history and survive across sessions.

---

## Troubleshooting

**Agent runs but writes no files** → see [TROUBLESHOOTING.md#agent-runs-but-writes-no-files](TROUBLESHOOTING.md)

**Model takes too long** → try a smaller or faster model; check VRAM usage in Task Manager

**Plan keeps being rejected** → review the `.agent.md` and make the goal more specific
