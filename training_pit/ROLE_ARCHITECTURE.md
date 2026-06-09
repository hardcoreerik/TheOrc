# TheOrc Role Architecture

> **Status:** Transitional. Three advertised boss roles, four execution lanes, alias routing active.
> Future expansion planned but not yet scheduled.

---

## The Two Layers

TheOrc uses a two-layer role model to separate *what kind of work a task represents*
from *which worker lane executes it*.

| Layer | Name | Where | Purpose |
|-------|------|--------|---------|
| Logical role | `LogicalRole` (`SwarmTask`) | Boss JSON → runtime | What kind of software work this is |
| Execution role | `SwarmWorkerRole` enum | Runtime → worker | Which system prompt and tool set runs the task |

**Example:** A boss plan might contain `"role": "DOCS"`. The parser normalizes this to
`SwarmWorkerRole.Coder` (execution lane) and stores `LogicalRole = "DOCS"` on the task.
The worker writes documentation using the Coder tool set. Future: a dedicated DOCS execution
lane with a writing-focused system prompt.

---

## Current Execution Lanes

Four execution lanes are implemented in `SwarmSession.cs`. Each has a distinct
system prompt (`WorkerSystemPrompt`) and tool set (`GetWorkerTools`):

| Execution Lane | Enum | Tools | Key constraint |
|---|---|---|---|
| RESEARCHER | `SwarmWorkerRole.Researcher` | fetch_url, grep_code, get_outline, read_file, list_files | NO write_file — investigate only |
| CODER | `SwarmWorkerRole.Coder` | write_file, read_file, run_shell, list_files, grep_code, fetch_url | Full write access |
| UIDEVELOPER | `SwarmWorkerRole.UIDeveloper` | write_file, read_file, run_shell, list_files, fetch_url | Full write; UI-focused prompt |
| TESTER | `SwarmWorkerRole.Tester` | run_shell, read_file, list_files | NO write_file — run and report only |

All execution lanes also have `ask_user` available for critical blocking questions.

**Scheduling:** RESEARCHER tasks run first (priority 1), alone. All other lanes run
concurrently at priority 2. There is no per-task model differentiation within a lane today —
all CODERs run on the same model. Per-role model routing is planned for Phase 4.

---

## Boss-Advertised Roles vs Execution Lanes

The `BOSS_SYSTEM_PROMPT` advertises **four roles** to the boss model:

```
• RESEARCHER  — investigates APIs, libraries, docs; does NOT write production code
• CODER       — writes full implementation code using the researcher's findings
• UIDEVELOPER — writes UI code (XAML, WPF, HTML/CSS) and styling
• TESTER      — runs existing code, executes tests, checks syntax, reports results; does NOT write or modify files
```

TESTER was added after confirming it is fully wired end-to-end:
- System prompt with structured PASS/FAIL/PARTIAL output format
- Tool set: `run_shell, read_file, list_files` — write_file excluded by design
- Parser routing in `ParseBossPlan` (was dead code prior to Phase 2 verification pass)
- `AgentKey` correctly returns `"tester"`
- Scheduling rule explicit in both boss prompt and worker prompt: TESTER verifies
  code that **already exists** in the workspace — it does not wait for CODER output
  from the same run

---

## Role Alias Map

`ParseBossPlan` in `SwarmSession.cs` normalizes logical role strings to execution lanes.
This allows future fine-tuned model variants (or manually authored plans) to use a richer
role vocabulary without breaking the current runtime.

| Logical role string(s) | Normalized execution lane | Rationale |
|---|---|---|
| `RESEARCHER` | RESEARCHER | Native |
| `UIDEVELOPER` | UIDEVELOPER | Native |
| `TESTER` | TESTER | Native (implemented and advertised) |
| `ARCHITECT`, `PLANNER`, `REVIEWER`, `ANALYST` | RESEARCHER | Investigate/design; no code output |
| `FRONTEND_DEVELOPER`, `FRONTEND`, `UI` | UIDEVELOPER | UI work → UIDEVELOPER lane |
| `QA`, `QUALITY_ASSURANCE` | TESTER | Test execution → TESTER lane |
| `CODER`, `BACKEND_DEVELOPER`, `BACKEND` | CODER | Code implementation |
| `DOCS`, `DOCUMENTATION` | CODER | Documentation writing (future: own lane) |
| `DEVOPS`, `RELEASE_MANAGER` | CODER | Infrastructure/release scripting |
| `SECURITY`, `PERFORMANCE` | CODER | Audit/optimization tasks |
| `ML_ENGINEER`, `DATA_ENGINEER` | CODER | ML/data pipeline implementation |
| *(any unknown string)* | CODER | Safe default — no crashes |

When a logical role is aliased, `SwarmTask.LogicalRole` preserves the original string.
When no aliasing occurs (boss emitted a native execution lane name), `LogicalRole` is null.

---

## Future Execution Lanes

These logical roles are common enough to warrant their own execution lanes eventually.
Each would need: a distinct system prompt, a tool set, scheduling rules, and training data.

| Future lane | Logical roles it covers | Blocking work |
|---|---|---|
| `DOCS` | DOCS, DOCUMENTATION, TECHNICAL_WRITER | Writing-focused system prompt; no run_shell needed |
| `DEVOPS` | DEVOPS, RELEASE_MANAGER, INFRASTRUCTURE | Shell-heavy prompt; environment-aware |
| `REVIEWER` | REVIEWER, AUDITOR, SECURITY | Read-only analysis; detailed critique format |

None of these will be activated until:
1. The boss fine-tuned model reliably generates correct structured tasks for the lane
2. Worker system prompt and tool set are defined and tested
3. Scheduling rules are confirmed (does it run concurrent with CODER? After?)
4. At least 20 eval prompts exist for the new lane

---

## Dataset Implications

### What the training dataset should teach

Training examples should use **execution role names only** — the four currently
advertised by the boss system prompt: `RESEARCHER`, `CODER`, `UIDEVELOPER`, `TESTER`.

Training the boss model on unadvertised roles (e.g. `DOCS`, `ARCHITECT`, `DEVOPS`)
would cause the model to emit role strings the system prompt doesn't describe,
creating inconsistent and unpredictable decomposition behavior.

### When to expand dataset roles

Dataset examples should only include a new role once:
1. The execution lane is fully implemented (system prompt + tools + scheduling)
2. The role is added to `BOSS_SYSTEM_PROMPT` and `convert_plan_captures.py`
3. At least 5 hand-authored golden examples for that role are reviewed

TESTER was promoted to advertised status after confirming all three conditions are met.

### The `LogicalRole` field — future option

The boss JSON schema could be extended with an optional `logical_role` field:

```json
{
  "role": "CODER",
  "logical_role": "BACKEND_DEVELOPER",
  "priority": 2,
  "title": "Write api_server.py with FastAPI routes",
  "description": "..."
}
```

This allows the fine-tuned model to be expressive about intent while the runtime
normalizes to the execution lane. `ParseBossPlan` would read `logical_role` into
`SwarmTask.LogicalRole` if present.

**This is not implemented yet.** It requires:
- Updating `BOSS_SYSTEM_PROMPT` to explain the field
- Updating `ParseBossPlan` to read `logical_role`
- Dataset examples demonstrating both fields
- Validator and sanitizer support for the new field

For the first LoRA, `logical_role` is documented here as architecture intent only.
The `LogicalRole` property on `SwarmTask` serves the same purpose at the parser level
without changing the boss JSON schema.

---

## Role Evolution Policy

1. **Never add a role to `BOSS_SYSTEM_PROMPT` before the execution lane is ready.**
   The boss learns what it is told. Advertising a role that crashes or misbehaves in
   the worker damages the training signal.

2. **Never remove a role from the alias map.** Once a role string is handled, it stays
   handled. Downstream tooling (scripts, traces, captures) may contain these strings.

3. **The alias map is additive-only.** New logical roles can be added to the `_` branch
   explicitly at any time. Existing mappings should not change without careful analysis
   of whether any captures or fine-tuned behavior depends on them.

4. **Logical roles in captures are preserved.** `DatasetCapture.cs` stores the raw boss
   JSON (including the original `"role"` string). If a future model emits `"DOCS"` tasks,
   those are preserved in the capture and can be reviewed before deciding whether
   `DOCS` → CODER (current) is still the right mapping.

---

*Last updated: 2026-06-09 — Initial role architecture document. Alias map active.*
