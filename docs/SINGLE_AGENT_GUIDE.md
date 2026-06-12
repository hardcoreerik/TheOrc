# TheOrc — Single Agent Guide

> This guide explains the current one-model execution loop. Read [USER_GUIDE.md](USER_GUIDE.md) for shell basics and [ARCHITECTURE.md](ARCHITECTURE.md) for the underlying `AgentLoop` and `ToolRegistry` design.

---

## What Single-Agent Mode Is

Single-agent mode runs one model through a plan-review-execute cycle. It is the simplest way to use TheOrc because there is no role routing or multi-lane orchestration.

This mode is best when:

- the task is narrow
- you want one conversational thread
- you want the smallest possible trust surface

---

## The Real Loop

The runtime split is not just a UI convention. `AgentLoop` really has separate planning and execution phases.

### Plan

During planning:

- the model gets workspace context
- project rules are injected if present
- no tools are available
- the output is expected to be a plan only

### Execute

During execution:

- `ToolRegistry` selects the tool set for the active model profile
- GOBLIN MIND data can influence tool dispatch mode and schema shaping
- write and shell approvals can pause execution
- tool results are fed back into the same conversation

---

## Tool Dispatch Reality

TheOrc does not assume every model handles tool calls the same way.

At execute time, `AgentLoop` can:

- use native tool calling
- fall back to text-JSON tool calling
- shape the prompt to the model's preferred format
- simplify schemas for models that fail on complex parameter structures

That is why one local model can work well in Single mode while another appears to "support tools" but still falls apart on larger writes.

---

## Write Approval And Verification

Two approval gates matter most in this mode:

- `write_file` can show a diff before any change is applied
- `run_shell` can show an approval card before the command executes

If auto-verify is enabled for the active profile, a write can be followed by `run_tests`.

---

## Rules And Workspace Context

Single-agent planning and execution can both load project rules from the workspace. This is how TheOrc keeps repo-specific guidance close to the actual task without hardcoding those conventions into the product itself.

The workspace also controls:

- where file tools read and write
- whether git checkpoints happen
- what the file explorer shows

---

## Refusal Handling

Single-agent mode actively pushes back on weak local-model behavior.

If the model responds with refusal text or code in chat instead of issuing real tool calls, `AgentLoop` can nudge it back toward:

- real `write_file` calls
- real shell calls
- actual tool usage instead of explanation-only output

This is one of the practical differences between TheOrc and a plain chat wrapper around a model.

---

## When Single-Agent Mode Is The Right Choice

Use it when:

- one model is already good enough
- the task is mostly sequential
- you want minimal orchestration overhead
- you want the cleanest audit trail around approvals

Switch to swarm when decomposition, specialization, or live multi-lane visibility matters more than keeping everything in one thread.
