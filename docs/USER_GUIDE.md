# TheOrc — User Guide

TheOrc is a Windows desktop app that lets you run AI coding assistants on your own PC. It works with local AI models (no cloud required), keeps you in control of every file change, and supports everything from a single AI helper to a full team of specialized AI workers running across multiple computers. This guide walks you through the main parts of the app.

For terminology, see [GLOSSARY.md](GLOSSARY.md). For a step-by-step first run, see [QUICK_START.md](QUICK_START.md).

---

## The Mode Bar

The mode bar runs along the top of the app. Each button switches TheOrc into a different way of working. Your model choice is saved separately for each mode, so switching modes won't reset your setup.

### Single

One AI model works on your task from start to finish. It reads, plans, and writes code. This is the best starting point if you're new to TheOrc.

See [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md) for details.

### Swarm

One "boss" AI breaks your goal into pieces and hands them to a team of specialized worker AIs — a Researcher, a Coder, a UI Developer, and a Tester. Each worker focuses on what it's good at. Use Swarm when a task is large or benefits from parallel work.

See [SWARM_GUIDE.md](SWARM_GUIDE.md) for details.

### Chat

A direct conversation with an AI model. Good for research, questions, brainstorming, or anything that doesn't need file editing.

### Pit

The Training Pit — this is where you turn swarm runs into training data and train your own custom AI adapter. You can watch captures being collected, review examples, and kick off training runs.

See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for details.

### Hive

The HIVE MIND panel. If you have more than one PC running TheOrc, this shows your network of nodes as a visual constellation. You can see what each machine can do, which one is the elected leader (the Warchief), and route tasks to any node on your network.

See [HIVE_MIND_GUIDE.md](HIVE_MIND_SPEC.md) for details.

### ⬆ Update

The Update Center. Click here to check if a new version of TheOrc is available, install it on this machine, and watch the update progress. If you're the Warchief in a HIVE network, you can also push an update to all your connected worker nodes at once from here.

A gold dot appears on this button when an update is available.

---

## The Status Bar

The status bar runs along the bottom of the app. It's your at-a-glance health readout — not just decoration.

- **Workspace** — the folder TheOrc is working in. File tools and swarm runs are anchored here.
- **Git branch** — shows the current branch if your workspace is a git repository.
- **Build stamp** — tells you exactly which version of TheOrc you're running, including a commit ID when available. Useful for bug reports.
- **Active model** — the AI model currently selected for the active mode.
- **Status text** — what TheOrc is doing right now.

---

## Opening a Workspace

A workspace is the folder your project lives in. You need to set one before TheOrc can read or write your files.

To open a workspace, click the workspace badge in the status bar or use the file-opening flow.

Once a workspace is set:

- all file tools read and write relative to that folder
- the file explorer panel shows that folder's contents
- git checkpoints happen automatically if the folder is a git repository
- swarm run outputs and dataset captures are saved inside that folder

---

## The Approval Flow

TheOrc does not run code or write files without your say-so. This is how it keeps you in control.

Here's what happens when an AI wants to do something:

1. For **shell commands** — an approval card appears showing the exact command. You click to allow it.
2. For **file writes** — a diff appears showing exactly what will change. You approve before anything is saved.
3. For **plans** — the AI shows you its plan before it starts doing anything.

The approval queue is live. You're not reviewing a log after the fact — you're deciding in real time. This applies in both Single and Swarm modes.

---

## Keyboard Shortcuts

- `F1` — open the in-app Help window
- `F12` — start or stop screen recording
- `Ctrl+K` — open the command palette
- `Ctrl+Shift+E` — show the file explorer
- `Ctrl+Wheel` — zoom the active stream pane
- `Ctrl+0` — reset stream zoom

---

## Help (F1)

Press `F1` at any time to open the built-in Help window.

The Help window loads the documentation you're reading right now. Internal links (like `[SWARM_GUIDE.md](SWARM_GUIDE.md)`) open inside the Help window without leaving the app.

In a repo checkout, Help loads from the `docs/` folder on disk. In a published build, it uses embedded docs.

---

## Where to Go Next

- [QUICK_START.md](QUICK_START.md) — if you haven't done your first run yet
- [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md) — for single-model tasks
- [SWARM_GUIDE.md](SWARM_GUIDE.md) — for multi-agent runs
- [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) — for connecting multiple PCs
- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) — for training your own AI adapter
- [GLOSSARY.md](GLOSSARY.md) — for a list of all TheOrc terms
