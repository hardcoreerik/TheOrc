# TheOrc — Swarm Guide

---

## What Swarm Mode Is

Swarm mode runs a coordinated squad of agents in parallel. The **Boss** model
receives your goal, decomposes it into 2–4 tasks, and assigns each task to a
specialized worker. Workers run concurrently. TheOrc (the boss) monitors progress,
steers workers that go off-track, and synthesizes the final result.

Switch to Swarm mode: set the mode selector (top left) to **Swarm**.

---

## The Four Swarm Roles

| Role | Tool access | Job |
|---|---|---|
| **RESEARCHER** | `read_file`, `list_files`, `grep_code`, `fetch_url`, `get_outline` | Investigates APIs, reads docs, summarizes findings. No file writing. |
| **CODER** | All tools including `write_file`, `run_shell` | Writes source code and creates files. |
| **UIDEVELOPER** | All tools including `write_file`, `run_shell` | Writes UI components, styles, and markup. |
| **TESTER** | `read_file`, `list_files`, `grep_code`, `run_shell`, `run_tests` | Runs tests, reads outputs, reports pass/fail. No `write_file` by design. |

> **No other execution roles exist beyond these four.** The four-role architecture is the
> complete system. RESEARCHER and TESTER intentionally lack `write_file` access — this is
> a hard constraint, not a configuration option.

---

## Swarm Board

Open: **View → Swarm Board** tab (or it appears automatically in Swarm mode).

The Swarm Board shows:

- **Boss model selector** — the model that decomposes the goal and steers workers
- **Worker model selectors** — separate models for CODER, RESEARCHER, UIDEVELOPER, TESTER
- **Goal input** — type your swarm goal here
- **Slots chip** — shows how many parallel Ollama slots are configured
- **Live node graph** — Boss node + worker nodes, each with state (IDLE / RUNNING / DONE / FAILED)
- **Task cards** — one card per task assigned by the boss, with status and description
- **Activity feed** — per-worker streaming log
- **Steering bar** — appears during active runs; type a mid-run correction and click ⬆ Steer
- **STOP button** — cancels the active swarm run
- **Launch Project / New Run** — appear on completion

---

## OLLAMA_NUM_PARALLEL — Required for True Parallelism

Swarm workers need their own Ollama slots to run concurrently. Without this setting,
workers queue and run sequentially — much slower.

```powershell
# Set before starting Ollama
$env:OLLAMA_NUM_PARALLEL = "4"
ollama serve
```

Set to at least the number of workers you plan to run simultaneously. For a full
four-role swarm (Boss + RESEARCHER + CODER + TESTER), set to 4.

If `OLLAMA_NUM_PARALLEL` is not set, workers will run but sequentially — not in parallel.

---

## Choosing Models for the Swarm

Each role has different requirements. See [MODEL_GUIDE.md](MODEL_GUIDE.md) for full scores.

### Boss

The Boss decomposes goals into tasks. It must:
- Output a JSON plan with 2–4 tasks, concrete descriptions, and named output files
- Avoid single-task collapse and vague descriptions

**Recommended Boss models:**
- `theorc-boss:gemma4` — custom Modelfile wrapper (QAT, `temperature=0.2`, `think=false`, 16K context)
- `qwen2.5-coder:14b` — strong planner, proven in swarm benchmarks

> **Important:** `gemma4:12b` without the Modelfile shows planning collapse — it outputs
> `title='Execute goal', description=''`. Use `theorc-boss:gemma4` for the boss role.
> See [MODEL_GUIDE.md](MODEL_GUIDE.md#theorc-bossgemma4).

### CODER / UIDEVELOPER

These workers write code and create files. They need strong `write_file` JSON reliability
for the expected file sizes. See [MODEL_GUIDE.md](MODEL_GUIDE.md) for the payload ceiling concept.

**Recommended:**
- `qwen2.5-coder:14b` — primary coder for 10–16 GB VRAM
- `qwen2.5-coder:7b` — solid for 6–8 GB
- `gemma4:12b` — excellent in worker roles

### RESEARCHER

The Researcher reads docs, fetches URLs, and summarizes findings. Context window matters.
No `write_file` is needed — small models work fine.

**Recommended:**
- `gemma4:12b` — 256K context, excellent summarizer
- `mistral-nemo:12b` — 128K context, strong long-doc research
- `nemotron-3-nano:4b` — fast, suitable for short lookups

### TESTER

The Tester runs commands and reports results. It never writes files.

**Recommended:**
- `nemotron-3-nano:4b` — fast, appropriate for short verdict output
- `qwen2.5-coder:7b` — more context-aware verdicts

---

## How the Swarm Runs

1. You type a goal and click **Launch Swarm**
2. The Boss model generates a JSON plan (2–4 tasks with roles, titles, and descriptions)
3. Each task is dispatched to a worker node
4. Workers run concurrently (if `OLLAMA_NUM_PARALLEL` is set correctly)
5. The Boss monitors results and can retry or correct workers
6. On completion: the Swarm Board shows all task results; **Launch Project** opens the workspace

---

## Steering a Live Swarm

While a swarm is running, the **steering bar** (bottom of Swarm Board) is visible.

Type a correction directive and click **⬆ Steer** to send it to the Boss. The Boss
reads the directive and adjusts the remaining work. Use this to:

- Redirect a worker going off-track
- Clarify a constraint the Boss missed
- Cancel a specific task

---

## DatasetCapture — Auto-Staging Boss Plans

`DatasetCapture.cs` evaluates every boss plan during a swarm run:

- Score ≥ 70 → auto-staged as `plan_capture_good_<runId>_<score>.json`
- Score ≤ 39 → auto-staged as `plan_capture_bad_<runId>_<score>.json`
- Score 40–69 → silently skipped (too noisy)

Staged plans land in `.orc/swarm/dataset-staging/` in your workspace root.
They are gitignored and are the raw material for Training Pit Phase 2.

See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for what to do with staged captures.

---

## Swarm Metrics

After each swarm run, metrics are written to `swarm-metrics.json` in the solution root.
These include per-configuration quality scores that `SwarmConfigAdvisor` uses to recommend
model combinations on future runs.

---

## Common Swarm Problems

**Workers queue instead of running in parallel**
→ `OLLAMA_NUM_PARALLEL` is not set or is set too low. Restart Ollama with it set to ≥4.

**Boss outputs a single task**
→ Planning collapse. Switch to `theorc-boss:gemma4` or a higher-quality boss model.
   Raw `gemma4:12b` without the Modelfile is known to collapse.

**Worker writes no files**
→ The coder model has a payload ceiling. Run the Model Capability Test on it.
   Nemotron Nano 4B is confirmed to fail on FileWriteLarge and FileWriteMedium.

**Swarm hangs with one worker at RUNNING**
→ The worker model is taking too long or has stalled. Use **STOP** and restart with a faster model.

See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for more.
