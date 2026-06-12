# TheOrc — Swarm Guide

> This guide explains how the current boss-plus-workers swarm actually behaves in code. Read [ARCHITECTURE.md](ARCHITECTURE.md) for the full lifecycle and [GLOSSARY.md](GLOSSARY.md) for role terms.

---

## What Swarm Mode Is

Swarm mode uses one boss model to decompose a goal and then routes work into four worker lanes:

- `RESEARCHER`
- `CODER`
- `UIDEVELOPER`
- `TESTER`

The boss is not a fifth worker. It is the orchestrator.

---

## Lane Responsibilities

The current role split is intentionally constrained.

- `RESEARCHER`: investigate, read, fetch, summarize
- `CODER`: implement code and file changes
- `UIDEVELOPER`: implement UI and layout changes
- `TESTER`: verify and report, with no `write_file` access

The code treats the TESTER and RESEARCHER restrictions as real execution constraints, not just suggestions.

---

## Session Lifecycle

`SwarmSession` runs through these phases:

1. boss decomposition
2. optional dataset capture
3. researcher phase
4. implementation phase
5. auto tester verification
6. optional fix task
7. boss merge and staged-file summary

That lifecycle is why swarm mode can produce both live output and durable run artifacts.

---

## Swarm Board

The Swarm Board is the main operator surface for swarm mode.

It exposes:

- boss, coder, and researcher model selection
- capability badges under each picker
- a `Probe Now` shortcut into the tool-call probe window
- lane streams and activity columns
- launch gating
- metrics history for past configurations

The board is doing real operations work, not just presentation.

---

## Capability Badges

The capability badges come from `ToolCallProfileStore` and summarize:

- recommended dispatch mode
- preferred tool-call format
- category pass summary
- whether schema reduction is active
- probe age and staleness

If a model has never been probed, the board says so explicitly.

---

## Capability-Aware Steering

Swarm routing is not purely role-name based.

`SwarmSteering` uses category maps to decide whether a role's primary model is capable enough. When a primary is missing required categories, the swarm can fall back and log the exact missing categories.

Required categories differ by role:

- boss: structured output and task planning
- coder and UI: file ops and code execution
- researcher: network and data transform
- tester: code execution and system inspection

---

## Researcher Quality Gate

Swarm mode does not blindly inject any researcher output into implementation prompts.

Ghost or weak researcher output is filtered out based on basic quality checks so that empty, trivial, or refusal-style research does not poison the coder phase.

---

## Co-Work And Follow-Ups

Workers are not sealed black boxes once launched.

The current swarm supports:

- waiting for user replies through `ask_user`
- queued steering while a worker is still in progress
- follow-up continuation on a completed worker using saved conversation history

This is why the board includes live lane input rather than only a global prompt box.

---

## Metrics History

The metrics history panel is backed by `SwarmMetricsStore`.

It groups past runs by boss/coder/researcher configuration and shows:

- run count
- success rate
- tester pass rate
- average duration
- composite quality score

This lets the swarm learn operationally which model mixes work best on your hardware.

---

## VRAM And Model Choice

`SwarmConfigAdvisor` uses detected GPU state and installed models to recommend role assignments. It prefers:

- observed best configurations when enough history exists
- otherwise, the strongest profile-based combination that fits available VRAM

This is one reason the swarm UI separates boss, coder, and researcher selection instead of assuming one model should do everything.

---

## Outputs And Review

Swarm runs write artifacts under `.orc/swarm/runs/<runId>/`.

The final operator-relevant pieces are:

- plan JSON
- traces and task files
- staged output files
- staged-files summary in the boss stream

The staged-files event is intentionally review-oriented: the swarm can produce output without silently forcing it into the workspace.
