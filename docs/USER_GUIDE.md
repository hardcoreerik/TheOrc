# TheOrc — User Guide

> This guide explains how to operate the current shell, not the idealized future product. Pair it with [ARCHITECTURE.md](ARCHITECTURE.md) for internals and [GLOSSARY.md](GLOSSARY.md) for terminology.

---

## What The Shell Is Organizing

TheOrc's shell is responsible for three operator jobs:

- choosing a mode and model
- keeping execution inside visible trust boundaries
- exposing enough live state that you can tell what the system is doing

---

## Main Modes

The mode selector in the main window exposes three runtime surfaces:

- `Single`: one model, one plan-and-execute loop
- `Swarm`: one boss plus worker lanes
- `Chat`: direct conversation and research-style use

The active model is remembered separately across modes, so switching modes does not mean redoing the entire model setup each time.

---

## Status Bar

The status bar is not decoration. It is the fastest health readout in the app.

It shows:

- workspace
- git branch
- build stamp
- active model
- current status text

The build stamp exists specifically to identify which binary is running, including the commit suffix when available.

---

## Help And Embedded Docs

Press `F1` to open the in-app Help window.

What the help surface does:

- loads `docs/*.md` from disk in a repo checkout
- falls back to embedded docs in published builds
- keeps internal guide links in-app

If a guide link is written like `[User Guide](USER_GUIDE.md)`, the help window routes it internally.

---

## Workspace Model

The workspace is the operational root for file-centric work.

When you open a workspace:

- file tools resolve relative paths against it
- the explorer panel reflects it
- git checkpoints run against it when it is a git repo
- swarm artifacts and dataset captures land beneath it

This is why the workspace badge matters more than a cosmetic project title.

---

## Trust And Approval Flow

TheOrc treats plan generation and execution as different trust stages.

In practice:

- planning can happen without tools
- shell commands can require explicit approval
- file writes can require diff approval
- the approval queue is part of runtime control, not a post-hoc audit log

This is the core safety model for both single-agent and swarm workflows.

---

## Single-Agent Experience

In `Single` mode:

1. you ask for a task
2. the model proposes a plan
3. you approve execution
4. tool calls run with approval and feedback
5. the final response is returned in the same thread

For the deeper execution loop, read [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md).

---

## Swarm Experience

In `Swarm` mode:

- the boss decomposes the goal
- workers run by lane
- capability badges show what is known about each selected model
- per-lane streams let you watch progress in real time
- co-work input can answer a worker, steer a worker, or continue a completed worker thread

For the lane and routing model, read [SWARM_GUIDE.md](SWARM_GUIDE.md).

---

## Model Surfaces

The model UX is broader than a single dropdown.

Important surfaces:

- status-bar model picker
- Model Wiki / Lab
- capability test dialog
- model comparison window
- Swarm Board capability badges

These surfaces exist because local model suitability is evidence-driven in TheOrc, not guess-driven.

---

## Training Pit Surface

The Training Pit panel is the operator view into the dataset and training loop.

It currently exposes:

- live capture state
- review queue
- gate counters
- ORC ACADEMY controls
- VRAM meter
- heartbeat-driven training progress
- resume and re-attach behavior

That makes the Training Pit a shell feature, not a folder of scripts hidden off to the side.

---

## Shortcuts That Matter

- `F1`: open Help
- `F12`: start or stop screen recording
- `Ctrl+K`: open command palette
- `Ctrl+Shift+E`: show file explorer
- `Ctrl+Wheel`: zoom the active stream pane
- `Ctrl+0`: reset stream zoom

---

## What To Read Next

- [SINGLE_AGENT_GUIDE.md](SINGLE_AGENT_GUIDE.md) if you mostly want one-model workflows
- [SWARM_GUIDE.md](SWARM_GUIDE.md) if you want the multi-agent flow
- [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) if you are evaluating local models
- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) if you want to understand how swarm plans become adapters
