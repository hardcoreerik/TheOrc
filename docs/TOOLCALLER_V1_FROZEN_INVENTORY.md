# TheOrc Foundry — Toolcaller v1 Frozen Tool Inventory

> **Status: 🔲 Deliberate scope expansion, not authorization to train.** This document
> freezes a second, wider tool universe and schema version for `theorc-toolcaller`,
> alongside — not replacing — the v0 universe in
> [TOOLCALLER_V0_FROZEN_INVENTORY.md](TOOLCALLER_V0_FROZEN_INVENTORY.md). v0's own text
> anticipated this: *"If a later Foundry phase wants toolcaller coverage for [tools
> outside the six], treat that as a new frozen-inventory revision with its own hash,
> not a silent addition to v0."* This is that revision.
>
> **Schema version:** `toolcaller-v1-tools-1.0`
> **Frozen tool set SHA-256:** `58a0e50de6cb6d6ae54a6034534026f97af9ea681361bb55e7e1dfacc3ea629a`
> **Canonical artifact:** [training_pit/schemas/toolcaller_v1_frozen_tools.json](../training_pit/schemas/toolcaller_v1_frozen_tools.json)
>
> Same hashing convention as v0: plain SHA-256 over the checked-in file's raw bytes.
> Any edit changes the hash and invalidates every v1 example generated against the
> prior version.

---

## Why v1, Not an Edit to v0

The v0 hash (`c456ca41...`) gates a merged, promoted specialist (`theorc-toolcaller:qwen25-1.5b`,
currently r3) and every sealed eval set built against it (`eval_toolcaller_v0.jsonl`,
`refusal_gauntlet_v0.jsonl`). Editing `toolcaller_v0_frozen_tools.json` in place would
silently invalidate all of that. v1 is a **separate, additive** tool universe and capture
stream — it does not touch v0's file, hash, datasets, training config, or shipped model.

## Motivation

The v0 universe covers only the 4 Swarm worker roles (researcher/coder/ui_developer/tester),
which fire `ToolcallerDatasetCapture` from `SwarmSession.RunWorkerLoopAsync`. OrcChat
(single-agent chat, `ChatEngine`/`ChatPanel`) is a materially higher-volume, everyday
usage surface and was capturing nothing. Its tool set is also genuinely different — no
`run_shell`, no `ask_user` (both excluded from chat by design, see
`OrcChatToolCatalog.cs`), but it adds web/library/verification tools v0 never had reason
to cover. Forcing OrcChat's decisions into the v0 schema would either misrepresent
`available_tools` (a role it doesn't have) or silently drop tools outside the frozen 6 —
both corrupt the training signal rather than add to it. A new frozen inventory with its
own role token is the honest fix.

## Decision

v1 = **v0's 6 tools, unchanged** + **OrcChat's 10 additional tools**, verified against
`OrcChatToolCatalog.TopToolNames` and each tool's live `ToolDefinition` registration —
not hand-typed from memory. 16 tools total.

| Tool | New in v1? | Registration | Required args |
|---|---|---|---|
| `read_file`, `list_files`, `grep_code`, `write_file`, `run_shell`, `ask_user` | No (from v0) | see [TOOLCALLER_V0_FROZEN_INVENTORY.md](TOOLCALLER_V0_FROZEN_INVENTORY.md) | — |
| `web_search` | Yes | `OrchestratorIDE/Research/ResearchToolset.cs` | `query` |
| `fetch_page` | Yes | `OrchestratorIDE/Research/ResearchToolset.cs` | `url` |
| `fetch_url` | Yes | `OrchestratorIDE/Tools/WebTools.cs` | `url` |
| `get_outline` | Yes | `OrchestratorIDE/Tools/SearchTools.cs` | `path` |
| `library_list` | Yes | `OrchestratorIDE/Tools/FabricTools.cs` | none |
| `library_search` | Yes | `OrchestratorIDE/Tools/FabricTools.cs` | `query` |
| `library_open` | Yes | `OrchestratorIDE/Tools/FabricTools.cs` | none |
| `library_graph` | Yes | `OrchestratorIDE/Tools/FabricTools.cs` | `corpus_id` |
| `run_tests` | Yes | `OrchestratorIDE/Tools/TestTools.cs` | none |
| `save_markdown_document` | Yes | `OrchestratorIDE/Research/OrcChatToolCatalog.cs` (built inline, not registry-sourced) | `filename`, `content` |

Exact `name`/`description`/`parameters`/`required` for all 16 are in
[toolcaller_v1_frozen_tools.json](../training_pit/schemas/toolcaller_v1_frozen_tools.json),
sorted alphabetically by name with sorted keys (reproducible via
`json.dumps(tools, indent=2, sort_keys=True)`).

**Deliberately still excluded**: the four CodeGraph tools (`graph_search`, `trace_path`,
`get_architecture`, `detect_changes`, `graph_adr`) — not part of `OrcChatToolCatalog`,
so out of scope for this OrcChat-driven revision. A future revision covering CodeGraph
usage gets its own hash, same rule as this one.

## Role

OrcChat has no `SwarmWorkerRole` equivalent — it's single-agent, not a swarm worker with
a role-scoped tool subset. v1 introduces exactly one new role token: **`chat`**, whose
`available_tools` is the full 16-tool set (OrcChat offers all of them to every
conversation; there is no per-role tool restriction in chat mode).

## Capture

`OrchestratorIDE/Services/Swarm/ToolcallerDatasetCapture.cs` gained a second capture
entry point (`StageChatDecisionAsync`) fired from a new `ChatEngine.OnToolcallerDecision`
event — `ChatEngine` itself stays workspace/staging-agnostic (per its own design intent);
`ChatPanel` owns turning the event into a capture, same separation of concerns as
`SwarmSession` owns it for swarm captures. Same opt-in flag
(`AppSettings.ToolcallerDatasetCaptureEnabled`) gates both — one toggle, two sources.

Captures land in `.orc/chat/dataset-staging/` (deliberately separate from
`.orc/swarm/dataset-staging/toolcaller/`, and filenamed with a `toolcaller_v1_chat_`
prefix) so v0 and v1 examples can never be accidentally merged by an exporter that
doesn't check `schema_version`.

One decision point is captured per user turn — the model's *first* response to a fresh
request (call, multiple calls, or no_tool). Follow-up turns inside a multi-step tool loop
(model calls a tool, sees the result, decides what's next) are not captured; that's a
different decision shape than the frozen "single bounded request → decision" schema
represents, same scoping the v0 capture makes implicitly by capturing per swarm-worker-step
rather than per-swarm-run.

## Status of Other F-1-shaped Deliverables for v1

None of v1's baseline, promotion, or training-authorization deliverables exist yet —
this document only freezes the tool inventory and role vocabulary (mirrors what
`TOOLCALLER_V0_FROZEN_INVENTORY.md` did for v0 before F-1 continued). No training,
export, or validator wiring for v1 is authorized by this document. Capture-only, same
posture v0 started from.
