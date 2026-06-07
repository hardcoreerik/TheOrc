# TheOrc / TheSwarm Repeatable Test Prompts

Use these prompts to test how **TheOrc Swarm Mode** behaves across different model and agent combinations.

The goal is to test whether **TheOrc** can decide when to delegate, how many agents to launch, and how well the results are aggregated back into a usable final output.

> **See also:** `theorc_swarm_complete_benchmark_pack.md` for the full deliverable contract, scoring rubric, and detailed benchmark write-ups.

---

## Before Running Any Prompt

1. **Set a workspace folder** — Swarm will not launch without one. Use Single Mode → File Explorer to set it first, then switch back to Swarm Mode.
2. **Confirm the amber workspace warning is gone** from the Swarm panel before hitting Launch.
3. **Set Trust Level to Standard** for benchmark runs. This allows file writes but still asks before shell commands.
4. **Confirm Nemotron is installed** — the Launch button stays disabled until at least one Nemotron model is detected.

---

## What These Prompts Are Testing

Good swarm test prompts should be:

1. Repeatable
2. Small enough to finish in one run
3. Clearly divisible into roles
4. Not dependent on live internet results
5. Easy to judge afterward
6. Likely to expose whether TheOrc delegates appropriately — not just by count, but by reasoning

The test prompt should not be "build me a whole operating system."

It should be a compact project with obvious lanes:

- Research / planning
- Coding
- Testing / debugging
- UX / documentation

---

# Recommended First Test Prompt

## Prompt 01 — CleanCSV

Start here. Small, deterministic, easy to score.

```text
You are testing TheOrc Swarm Mode.

Create a small desktop utility called CleanCSV.

Goal:
The user should be able to load a CSV file, preview it, clean common issues, and export a cleaned copy.

Requirements:
- Load a CSV file.
- Show a preview of rows and columns.
- Provide cleaning actions: trim whitespace, remove blank rows, remove duplicate rows, normalize column names to snake_case.
- Export the cleaned CSV.
- Include a sample messy CSV file.
- Include a README and a manual test checklist.
- Keep the app small. Avoid cloud services and unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Do not use extra agents just to use them.
Delegate clear roles: data logic, GUI, testing, documentation.
Each agent should produce a concise result.
TheOrc should aggregate results into the final implementation.
Do not bypass TheOrc's existing file-edit or shell-command approval gates.
```

### Expected Delegation

```text
2 agents (ideal):
  Agent 1: CSV logic + export
  Agent 2: README + TEST_PLAN

3 agents (also acceptable):
  Agent 1: CSV logic
  Agent 2: GUI / preview
  Agent 3: docs + sample data
```

If the model uses 3 agents for this, note whether each agent actually contributed distinct work. Generic splitting without real role separation is a signal to watch.

---

## Prompt 02 — BugDex (Recommended First Complex Test)

Best all-around benchmark. Tests data model, GUI reasoning, and multi-agent delegation.

```text
You are testing TheOrc Swarm Mode.

Create a small, complete, local-first desktop app called BugDex.

Goal:
BugDex lets a user maintain a simple local database of software bug types and troubleshooting notes.

Requirements:
- Use a local JSON file as the database.
- Provide a simple GUI.
- Allow adding, editing, deleting, and searching bug records.
- Each bug record should include: bug type name, category, severity, symptoms, likely causes, suggested fixes, notes.
- Include 5 starter bug records.
- Include a README with setup and run instructions.
- Include a manual test checklist.
- Keep the app small. Avoid cloud services and unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether to use 1, 2, or 3 agents.
If multiple agents are useful, delegate clear roles such as:
- research/planning agent
- coding agent
- testing/debugging agent
- documentation agent

Each agent should produce a concise result.
TheOrc should aggregate the results into the final implementation.
Do not bypass TheOrc's existing file-edit or shell-command approval gates.
```

### Expected Delegation

```text
3 agents (ideal):
  Agent 1: data model design + 5 starter records
  Agent 2: GUI + JSON load/save
  Agent 3: test plan + README

2 agents (acceptable):
  Agent 1: full implementation
  Agent 2: docs + testing checklist
```

### Why This Is the Default Benchmark

BugDex has natural, obvious agent lanes. It's small enough to finish cleanly but complex enough to give the orchestrator a real delegation decision. Output is easy to inspect and compare across model runs.

---

## Prompt 03 — Local Log Analyzer (Deterministic Benchmark)

The most verifiable benchmark. Run this when you want a countable, objective result.

```text
You are testing TheOrc Swarm Mode.

Create a small local log analyzer tool.

Goal:
The user should be able to load a plain text log file and see a summary of warnings, errors, and repeated messages.

Requirements:
- Accept .txt and .log files.
- Count lines containing ERROR, WARN, WARNING, FAIL, EXCEPTION, and SUCCESS.
- Show the top 10 repeated lines.
- Show a simple summary panel.
- Allow exporting the summary to a Markdown report.
- Include sample log data for testing.
- Include a README and test checklist.
- Avoid cloud services and unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether to use 1, 2, or 3 agents.
Delegate roles: parsing/coding, testing, documentation.
Do not bypass TheOrc's existing file-edit or shell-command approval gates.
```

### Expected Delegation

```text
2 agents (ideal):
  Agent 1: file loader, parser, report exporter
  Agent 2: sample log creation + test validation + README
```

### Verification Tip

Before running, write a `sample.log` with exactly **9 ERRORs, 4 WARNs, 2 FAILs**. After the run, load it into the generated tool and verify the counts match. A mismatch means the model hallucinated or misread the requirement.

---

## Prompt 04 — GuardScan (Defensive Security)

Use standard worker models for this — not the security/uncensored stack. Tests defensive pattern-matching reasoning.

```text
You are testing TheOrc Swarm Mode in a defensive security workspace.

Create a small local-only security review tool called GuardScan.

Goal:
GuardScan scans a folder of source files and reports potentially risky patterns.

Requirements:
- Scan .py, .js, .ts, .cs, .json, .env.example, and .txt files.
- Detect hardcoded secrets using simple pattern matching.
- Detect risky code patterns: eval(, exec(, shell=True, Invoke-Expression, process.env with suspicious key names, TODO SECURITY, password =, api_key =, token =.
- Output a Markdown report.
- Include sample test files with safe fake secrets only.
- Include README.md, TEST_PLAN.md, IMPLEMENTATION_NOTES.md.
- Keep it local only. No real exploit code. No cloud services.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Delegate roles: security rules planner, coding agent, testing/reporting agent.
Use only fake sample secrets — no real credentials.
Do not bypass TheOrc's existing approval gates.
```

### Expected Delegation

```text
2-3 agents:
  Agent 1: define detection rules / pattern list
  Agent 2: build scanner + report output
  Agent 3 (optional): create fake sample files + verify detection
```

---

## Prompt 05 — Portable MP3 Player (Do Not Start Here)

Compatibility-heavy. Introduces real-world library and platform constraints. Run this after completing at least Prompts 01 and 02.

```text
You are testing TheOrc Swarm Mode.

Create a small portable cross-platform MP3 player app.

Goal:
The app should allow a user to select a local folder of MP3 files, display the songs in a list, and play/pause/stop the selected file.

Requirements:
- Keep the app simple. Avoid paid or cloud services.
- Prefer cross-platform libraries.
- Include a README with setup instructions.
- Include notes about Windows, Linux, and macOS compatibility.
- Include a basic manual test checklist and implementation notes.
- Avoid unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Delegate clear roles: research/library selection, coding, OS compatibility review, debugging.
Do not bypass TheOrc's existing approval gates.
```

### Expected Delegation

```text
3 agents (correct):
  Agent 1: research cross-platform audio library options
  Agent 2: build the player
  Agent 3: OS compatibility notes + test checklist

If TheOrc skips the research agent and jumps straight to coding,
that is a delegation failure worth noting.
```

---

## Additional Prompts

### Prompt 06 — Event Checklist Builder

```text
Create a small desktop tool called EventChecklist.

Goal:
The user should be able to create, save, and reuse event setup checklists.

Requirements:
- Store checklists in a local JSON file.
- Allow creating checklist templates.
- Allow adding tasks with: task name, assigned role, due date, status, notes.
- Include filters for incomplete, completed, overdue, and by assigned role.
- Include 3 starter templates: Small Meeting, Outdoor Vendor Event, Large Public Event.
- Include a README and basic test plan.

Swarm Instructions:
TheOrc should delegate planning, coding, testing, and documentation as appropriate.
Do not bypass TheOrc's existing approval gates.
```

---

### Prompt 07 — Mini Kanban Board

```text
Create a small local Kanban board app.

Goal:
The user should be able to manage tasks across three columns: To Do, In Progress, and Done.

Requirements:
- Store data in a local JSON file.
- Add, edit, delete, and move cards between columns.
- Each card: title, description, priority, due date, tags.
- Search/filter by tag and priority.
- Include a README and manual test plan.

Swarm Instructions:
TheOrc should delegate UI, data model, testing, and documentation as appropriate.
Do not bypass TheOrc's existing approval gates.
```

---

### Prompt 08 — Simple Markdown Notes App

```text
Create a small local Markdown notes app.

Goal:
The user should be able to create, edit, save, search, and preview Markdown notes.

Requirements:
- Store notes as local .md files in a notes folder.
- Show a list of notes.
- Provide an editor pane and preview pane.
- Support search by note title and body text.
- Include 3 starter notes.
- Include a README and manual test checklist.

Swarm Instructions:
TheOrc should delegate work if it improves the result.
Do not bypass TheOrc's existing approval gates.
```

---

# Standard Comparison Set

Run these three for a consistent model comparison baseline:

```text
1. CleanCSV      — file handling, transformations, export, testability
2. Log Analyzer  — deterministic parsing, sample data, verification
3. BugDex        — data model, GUI, multi-agent delegation
```

---

# Recommended Model Comparison Matrix

| Run | Orchestrator | Workers | Max Workers |
|---|---|---|---|
| 1 | `gemma4:12b` | `nemotron-3-nano:4b-q8_0` | 2 |
| 2 | `qwen2.5-coder:14b` | `nemotron-3-nano:4b-q8_0` | 2 |
| 3 | `gemma4:12b` | `qwen2.5-coder:7b` | 2 |
| 4 | `nemotron-3-nano:4b-q8_0` | `nemotron-3-nano:4b-q8_0` | 2 |

Run 1 vs Run 2 answers: **Gemma or Qwen — which is the better boss?**
Run 3 vs Run 1 answers: **Nemotron or Qwen Coder — which is the better worker?**
Run 4 answers: **Can Nemotron boss itself effectively?**

---

# Repeatable Benchmark Prompt Template

Use this template when creating future swarm benchmark prompts:

```text
You are testing TheOrc Swarm Mode.

Create a small, complete, local-first app called [APP_NAME].

Goal:
[ONE SENTENCE GOAL]

Requirements:
- [Requirement 1]
- [Requirement 2]
- [Requirement 3]
- [Requirement 4]
- Include sample data.
- Include a README.
- Include a manual test checklist.
- Keep the implementation small.
- Avoid unnecessary dependencies.
- Do not use cloud services.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
If using multiple agents, delegate clear roles such as planning, coding, testing, compatibility review, and documentation.
Each agent should produce a concise result.
TheOrc should aggregate the results into a final implementation plan and final output.
Do not bypass existing file-edit or shell-command approval gates.
```
