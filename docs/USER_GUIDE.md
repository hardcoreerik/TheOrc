# TheOrc — User Guide

---

## Mode Selector

The mode selector is in the top-left of the main window. Three modes are available:

| Mode | What it does |
|---|---|
| **Single** | One agent, one model, one task at a time. Full Plan → Execute loop. |
| **Swarm** | Multi-agent: Boss decomposes the goal, workers run in parallel. |
| **Chat** | Direct conversation without a task loop. Research, Q&A, exploration. |

Switch modes at any time. The active model is remembered separately per mode.

---

## Opening a Workspace

Click the **📁 workspace badge** in the top bar to open a folder as your workspace.

- TheOrc sets this folder as the root for all file operations
- The file explorer (left panel) shows the workspace tree
- `write_file` paths are resolved relative to the workspace root
- Git auto-checkpoint: if the workspace contains a `.git` folder, TheOrc commits
  a checkpoint before every Execute run

**There is no restriction on what folder you open.** You can open any project folder,
including TheOrc's own source code (which is how the Self-Improve feature works).

---

## Plan Mode vs Execute Mode

Every task in Single Agent mode goes through two stages:

### Plan
1. You type a goal and press Enter (or click **Plan**)
2. TheOrc sends your goal to the model with the current workspace context
3. The model returns a **plan** — a step-by-step list of what it intends to do
4. You review the plan in the chat panel

At this point nothing has been written or run. You can:
- **Approve** → move to Execute
- **Reject** → ask the model to revise or start over

### Execute
1. The model executes the plan one step at a time
2. Before each `write_file` call, TheOrc shows a **visual diff** — you Approve or Reject
3. Before each `run_shell` call, TheOrc shows a **ShellApprovalCard** — you Approve or Reject
4. The model can use `read_file`, `list_files`, `grep_code`, and `get_outline` without approval

---

## Trust Levels

The trust level controls how much the agent can do without asking:

| Trust Level | Write file | Run shell | Notes |
|---|---|---|---|
| **Plan** | ❌ Never | ❌ Never | Plan step only — no execution |
| **Guarded** | ✅ Requires approval | ✅ Requires approval | Default — safest for first use |
| **Standard** | ✅ Requires approval | ✅ Requires approval | Same as Guarded in current build |
| **Full Auto** | ✅ Auto-approved | ✅ Auto-approved | No prompts — use only for trusted tasks |

Trust level is shown as a pill in the status bar and can be changed at any time.

---

## The Model Badge

The model badge in the status bar shows the currently selected model for the active mode.

- Click the badge to open the model picker flyout
- Models are loaded from the connected Ollama instance
- TheOrc shows each model's profile scores (Boss, Coder, etc.) if available
- The auto-select logic picks the best model for your available VRAM on startup;
  you can override this at any time

Swarm mode has separate model selectors for Boss, Coder, Researcher, etc. on the Swarm Board.

---

## Git Checkpoints

If your workspace has a git repository, TheOrc automatically commits a checkpoint
before every Execute run:

```
[TheOrc checkpoint] Before execute: <first line of your goal>
```

This means:
- You can always roll back to before any agent run with `git log` + `git reset`
- Checkpoints accumulate in git history — use `git log` to see them
- The checkpoint is a real commit on whatever branch you're currently on

If the workspace is not a git repo, no checkpoint is made.

---

## Where Output and Logs Live

| Item | Location |
|---|---|
| Agent session logs | Activity log in the chat panel (in-app only, not persisted to disk by default) |
| Git checkpoints | `.git/` in your workspace (standard git history) |
| Settings | `%APPDATA%\OrchestratorIDE\settings.json` |
| Model Wiki results | `%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl` |
| UI test recordings | `%APPDATA%\OrchestratorIDE\Recordings\` (AVI files from FlaUI test runs) |
| Swarm metrics | `swarm-metrics.json` in the solution root (if swarm benchmarks have been run) |
| Dataset staging | `.orc/swarm/dataset-staging/` in the workspace (boss plan captures, gitignored) |

---

## File Writing and Staging

All file writes go through the diff viewer in Guarded mode:

1. Model calls `write_file` with a path and content
2. TheOrc reads the existing file (if any) and generates a diff
3. The diff is shown in the DiffViewer with **Approve** / **Reject** buttons
4. On Approve: the file is written and the git checkpoint (if any) is updated
5. On Reject: the write is skipped; the agent receives a rejection notice and may try again

In **Full Auto** mode, writes proceed without showing the diff.

---

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+K` | Open command palette (fuzzy search over all commands) |
| `Ctrl+Shift+E` | Toggle file explorer |
| `Ctrl+Shift+C` | Toggle code editor |
| `Ctrl+Shift+R` | Edit rules file (`.agent.md`) |
| `Ctrl+Shift+M` | Open model picker (Choose Model…) |
| `F12` | Start / Stop screen recording |
| `Ctrl+=` | Increase font size |
| `Ctrl+-` | Decrease font size |
| `Ctrl+0` | Reset font size |

---

## The Rules File (`.agent.md`)

Every workspace can have a `.agent.md` file in its root. TheOrc injects this file
into every Plan and Execute system prompt.

Use `.agent.md` to tell the agent about your project:
- What language and framework it uses
- What conventions to follow
- What files should never be modified
- Any project-specific tool notes

The installer generates a profile-specific `.agent.md` (Web, Systems, Security, etc.)
for new projects. You can edit it at any time with **View → Edit Rules File** (Ctrl+Shift+R).
