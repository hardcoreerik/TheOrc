# TheOrc Foundry — Toolcaller v0 Frozen Tool Inventory

> **Status: 🔲 F-1 deliverable.** This document freezes the tool universe and schema
> version for the `theorc-toolcaller` v0 proof defined in
> [THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md). It does not authorize training.
>
> **Schema version:** `toolcaller-v0-tools-1.0`
> **Frozen tool set SHA-256:** `c456ca416882788664b14ea332aa968de76735171a2e53a76eac7c4c6e2bfefd`
> **Canonical artifact:** [training_pit/schemas/toolcaller_v0_frozen_tools.json](../training_pit/schemas/toolcaller_v0_frozen_tools.json)
>
> The hash is a plain SHA-256 over the checked-in file's raw bytes (not a re-serialized
> canonical form) so it is trivially reproducible from any language or tool —
> `sha256sum training_pit/schemas/toolcaller_v0_frozen_tools.json` reproduces it directly.
> Any edit to this file (including whitespace) changes the hash and invalidates every
> dataset example generated against the prior version — bump the schema version and
> regenerate rather than silently reusing stale examples.

---

## Decision

The frozen v0 tool universe is the **same 6 tools F-0 proposed**: `read_file`,
`list_files`, `grep_code`, `write_file`, `run_shell`, `ask_user`. This F-1 pass verified
each one against the live tool registrations rather than accepting the proposal on faith
(see [Verification](#verification) below), and found no reason to add or remove a tool
for the v0 proof. Scope stays at F-0's minimum because the smallest reproducible proof
answers the training-vs-baseline question fastest; expanding scope now would be solving
a problem the v0 proof does not yet need solved.

That said, verification surfaced two things the v0 dataset and evaluation design must
account for honestly rather than paper over:

1. **`ToolPolicyEngine` only actively risk-evaluates 4 of these 6 tools.** `read_file`,
   `list_files`, `write_file`, and `run_shell` each have a dedicated `Evaluate` case;
   `grep_code` and `ask_user` fall through to the engine's default
   `ToolRiskLevel.ReadWorkspace` assessment with no destructive/out-of-workspace/network
   checks of their own (`OrchestratorIDE/Trust/ToolPolicyEngine.cs`, `Evaluate()` switch).
   Dataset examples that need a real deterministic-policy outcome for `grep_code` or
   `ask_user` will get the default assessment, not a tool-specific one. This is a fact
   about the current policy layer, not a v0 dataset bug — it should be recorded as a
   known limitation in every baseline/eval report that touches those two tools.
2. **Swarm worker roles are `Researcher` / `Coder` / `UIDeveloper` / `Tester`,** not the
   "boss/coder/reviewer/worker" framing implied elsewhere. Each role has its own tool
   subset (below); `available_tools` in every dataset example must reflect the subset the
   originating role actually had, not the full frozen 6.

## Excluded From v0 (Verified, Not Assumed)

The live registry exposes far more than 6 tools: `get_outline`, `run_tests`, `fetch_url`,
four codegraph tools (`graph_search`, `trace_path`, `get_architecture`, `detect_changes`,
`graph_adr`), four Context Fabric library tools (`library_list`, `library_search`,
`library_open`, `library_graph`), and a chat-only research pack
(`web_search`, `fetch_page`, `save_markdown_document`) that deliberately excludes
`run_shell`. None of these are in the v0 universe. If a later Foundry phase wants
toolcaller coverage for any of them, treat that as a new frozen-inventory revision with
its own hash, not a silent addition to v0.

## Per-Role Available-Tool Subsets (Verified)

Source: `SwarmSession.GetWorkerTools()`, `OrchestratorIDE/Agents/SwarmSession.cs:1645-1667`.
`ask_user` is appended to every role (handled in-process, never dispatched through the
tool registry).

| Role | Tools available (within the v0 frozen 6) |
|---|---|
| `Researcher` | `grep_code`, `read_file`, `list_files`, `ask_user` (role also gets `fetch_url`, `get_outline`, both outside v0) |
| `Coder` | `write_file`, `read_file`, `run_shell`, `list_files`, `grep_code`, `ask_user` |
| `UIDeveloper` | `write_file`, `read_file`, `run_shell`, `list_files`, `ask_user` (no `grep_code`) |
| `Tester` | `run_shell`, `read_file`, `list_files`, `ask_user` (deliberately **no** `write_file` — prevents self-patching) |

A `theorc-toolcaller` v0 example's `available_tools` field must be the intersection of
this table's row with the frozen 6, not the full frozen set, whenever the example is
derived from or intended to represent a specific role.

## Verification

Each frozen tool was checked against its live `ToolDefinition` registration, not taken
from the F-0 proposal text:

| Tool | Registration | Required args |
|---|---|---|
| `read_file` | `OrchestratorIDE/Tools/FileTools.cs:33-62` | `path` |
| `write_file` | `OrchestratorIDE/Tools/FileTools.cs:65-114` | `path`, `content` |
| `list_files` | `OrchestratorIDE/Tools/FileTools.cs:117-...` | none |
| `grep_code` | `OrchestratorIDE/Tools/SearchTools.cs:14-...` | `pattern` |
| `run_shell` | `OrchestratorIDE/Tools/ShellTools.cs:22-...` | `command` |
| `ask_user` | `OrchestratorIDE/Agents/SwarmSession.cs` (`AskUserTool`, virtual — never dispatched through `_toolRegistry`) | `question` |

The exact `name` / `description` / `parameters` / `required` fields for all 6 are in
[training_pit/schemas/toolcaller_v0_frozen_tools.json](../training_pit/schemas/toolcaller_v0_frozen_tools.json).
Any future edit to these tool registrations must be reflected there and the hash above
recomputed before generating or accepting new dataset examples.

## Relationship to Other F-1 Deliverables

This document satisfies F-1 deliverable #1 ("frozen v0 tool/schema inventory") from
[THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md). It feeds directly into:

- [TOOLCALLER_CAPTURE_SCHEMA.md](../training_pit/TOOLCALLER_CAPTURE_SCHEMA.md) — the
  dataset schema that references this frozen tool set and its hash.
- `Tools/ToolcallerBench` — the eval harness skeleton, which loads
  `toolcaller_v0_frozen_tools.json` as its fixture source of truth rather than
  hand-duplicating tool definitions.

Remaining F-1 deliverables (baseline report, development/sealed-test manifests,
promotion margin, `run_manifest.json` contract, chat-template round-trip fixture) are not
addressed by this document and remain open F-1 work.
