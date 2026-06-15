# TheOrc — Swarm Guide

Swarm mode is how TheOrc handles bigger, more complex tasks. Instead of one AI doing everything, a "boss" AI breaks your goal into pieces and hands each piece to a specialized worker. The workers run in parallel, each focused on what it's best at.

For terminology, see [GLOSSARY.md](GLOSSARY.md). For the system-level view, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Why Use Swarm Mode?

Single mode works well for focused tasks. Swarm mode shines when:

- A task has multiple parts (research + coding + testing)
- You want a researcher to gather context before a coder starts writing
- You want a separate tester to verify changes without also having write access
- You want parallel progress across lanes instead of one sequential thread

The boss keeps track of the whole goal while workers stay in their lane.

---

## The Four Worker Lanes

Each lane has a specific job and specific tool access. This isn't just organization — the code enforces it.

### RESEARCHER

The RESEARCHER gathers information. It reads files, searches the web, fetches URLs, and summarizes findings. It does not write production files. Use it to investigate before coding starts.

### CODER

The CODER implements changes. It writes and edits files, runs shell commands, and produces the actual code output. It has broad tool access because it needs to get things done.

### UIDEVELOPER

The UIDEVELOPER handles interface work — XAML, layout, styling, and visual components. It has the same broad tool access as the CODER but is prompted to focus on UI concerns.

### TESTER

The TESTER verifies the work. It runs tests, inspects output, and reports whether things pass or fail. It intentionally cannot write files — this prevents it from accidentally "fixing" something it was supposed to be evaluating.

---

## The Boss

The boss is not one of the four workers — it's the coordinator. It reads your goal, decides how to split it up, assigns work to the right lanes, and merges everything at the end. Think of it as the project manager.

The boss also does a final staged-files summary: it shows you what files were produced by the swarm run before anything is committed to your workspace.

---

## What Happens in a Swarm Run — Step by Step

1. **You describe your goal** in the swarm input.
2. **The boss decomposes the goal** — it figures out what needs to happen and in what order.
3. **The capture system records the plan** — if the plan is high quality, it's staged as a training example for the Training Pit.
4. **The researcher runs first** — it gathers context, reads relevant files, and summarizes findings.
5. **A quality check filters weak research** — if the researcher came back with nothing useful, that result is filtered out so it doesn't confuse the next step.
6. **The coder and UI developer implement** — they use the researcher's findings to write and edit files.
7. **The tester verifies** — it checks whether the changes work.
8. **If the tester finds issues**, an optional fix task runs to address them.
9. **The boss merges and summarizes** — it produces a staged-files summary you can review before anything lands in your workspace.

---

## The Swarm Board

The Swarm Board is the main screen in Swarm mode. Here you can:

- Pick models for the boss, coder, and researcher roles
- See capability badges under each picker showing what that model can and can't do
- Click **Probe Now** to test a model's tool-call ability before starting
- Watch live streams from each lane as the run progresses
- Read per-lane activity columns alongside the streams
- Review metrics history from past runs

The Launch button only activates when you have a valid workspace and all required model slots are filled.

---

## Capability Badges

Under each model picker, a capability badge summarizes what TheOrc has learned about that model through probing. It shows:

- The recommended dispatch mode (how to send tool calls to this model)
- The preferred tool-call format
- Which task categories it passed
- Whether schema simplification is active for it
- How old the probe data is (stale probes are flagged)

If a model has never been probed, the badge says so. Click **Probe Now** to run a fresh probe.

---

## The Routing System

The routing system (called SwarmSteering internally) is how TheOrc decides whether a model is actually suited for a role. It doesn't just accept whatever you picked.

Each role has required capabilities:

- **Boss** — needs structured output and task planning
- **Coder / UI Developer** — need file operations and code execution
- **Researcher** — needs network access and data transformation
- **Tester** — needs code execution and system inspection

If a model is missing required capabilities for a role, it's flagged before the run starts — and the swarm can fall back to a safer choice with a logged explanation.

---

## Talking to Workers During a Run

Workers aren't sealed off once they start. You can interact with a running swarm:

- If a worker calls `ask_user`, it pauses and waits for your reply
- You can send steering input to a worker while it's still running
- After a worker finishes, you can send a follow-up message to continue that conversation using its saved history

This is why the Swarm Board has per-lane input boxes, not just a single global prompt.

---

## Metrics History

After enough runs, the Swarm Board's metrics history panel becomes useful. It groups past runs by the boss/coder/researcher model combination you used and shows:

- How many runs you've done with that combination
- The success rate
- The tester pass rate
- Average run time
- A composite quality score

Over time, this tells you which model mixes work best on your hardware.

---

## Swarm Output Files

Swarm runs save their artifacts under `.orc/swarm/runs/<runId>/` inside your workspace:

- A plan JSON file
- Trace and task files
- Staged output files
- The boss's final staged-files summary

The staged-files summary is designed for review — the swarm can produce output without forcing it directly into your workspace. You decide what to keep.

---

## Where to Go Next

- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) — how swarm run captures become training data
- [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) — how to evaluate and compare models for swarm roles
- [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) — how to run swarm workers across multiple PCs
