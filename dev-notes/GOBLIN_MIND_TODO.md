# 🧠 GOBLIN MIND — Universal Model Capability Discovery
### TheOrc v1.1 Major Milestone

> **"Don't guess what a goblin can do. Find out."**
>
> The Goblin Mind initiative teaches the swarm to understand itself.
> Every model that connects to TheOrc gets behaviorally probed — its tool-call format,
> its task category limits, and its schema preferences are discovered at runtime and
> stored permanently. TheOrc uses this knowledge to route every task to the right goblin,
> every time, without hard-coding anything.

---

## Milestone Summary

| | |
|---|---|
| **Codename** | GOBLIN MIND |
| **Version Target** | v1.1 |
| **Research basis** | Options 1, 3, 2, 4, 5 (rated — see `GOBLIN_MIND_UPDATES.md`) |
| **Primary constraint** | No test prompt code. TheOrc's internal methods must steer and correct. Models must do the work. |
| **Current foundation** | `ToolCallProbeEngine.cs`, `ToolCallProfileStore.cs`, `AgentLoop.cs` dispatch hook |

---

## Phase 1 — Behavioral Format Fingerprinting ✅ `DONE`

> Discover each model's *native* preferred tool-call serialization format.
> Rating: **9/10** — cheapest, most actionable, directly feeds dispatch.

### Tasks

- [ ] **`Services/ToolCalls/FormatProbeEngine.cs`** — New probe engine for format discovery
  - Send the same logical tool request in 5 serialization variants to a fresh context:
    1. OpenAI envelope — `{"name": "...", "arguments": {...}}`
    2. Hermes XML — `<tool_call><name>...</name><parameters>...</parameters></tool_call>`
    3. Bare JSON — `{"tool": "...", "args": {...}}`
    4. Python-style text — `tool_name(arg="value")`
    5. YAML block — `tool: ...\nargs:\n  key: value`
  - Measure: parse success rate via `TryParseTextToolCalls` for each variant
  - Measure: token overhead per format
  - Output: `FormatFingerprint` record — preferred format + reliability score per variant

- [ ] **Extend `ToolCallProfile`** in `ToolCallProfileStore.cs`
  - Add `FormatFingerprint? FormatProfile` field
  - Add `string PreferredFormat` computed property (falls back to `"openai"` if unprobed)
  - Serialize to the existing `tool-call-profiles.json`

- [ ] **`Services/ToolCalls/FormatProbeEngine.cs`** — `RunAsync(modelId, onProgress, ct)`
  - 5 format variants × 1 test each = 5 API calls total
  - Returns `FormatProbeResult` with ranked format list

- [ ] **`AgentLoop.cs`** — Read format fingerprint at session start
  - If `PreferredFormat != "openai"`, shape the system prompt tool schema preamble accordingly
  - New method: `ApplyFormatFingerprint(session, profile)` → adjusts tool block format in the request

- [ ] **`Tests/ToolCallTestWindow.xaml.cs`** — Add "Format Probe" column to results grid
  - Show preferred format chip (OPENAI / HERMES / BARE / PYTHON / YAML)

---

## Phase 2 — Category Boundary Mapping ✅ `DONE`

> Discover which *task categories* each model handles reliably.
> Rating: **8.5/10** — feeds swarm routing directly, cheap (14 queries per model).

### Task Categories (7 total)

| ID | Category | Description |
|---|---|---|
| C1 | `file_ops` | Read, write, delete files |
| C2 | `network` | HTTP GET/POST, fetch URL |
| C3 | `code_exec` | Run shell command, eval code |
| C4 | `data_transform` | Parse, format, convert data |
| C5 | `system_inspect` | List files, env vars, process state |
| C6 | `structured_output` | Produce multi-field structured JSON |
| C7 | `task_planning` | Break goal into ordered steps |

### Tasks

- [ ] **`Services/ToolCalls/CategoryProbeEngine.cs`** — New engine
  - 7 categories × 2 tests each = 14 API calls total
  - Each test: minimal 3-line tool stub description → request that naturally requires that category
  - Verify: did the model call the right category? Did it confuse adjacent categories?
  - Output: `CategoryBoundaryMap` record — per-category `Pass/Fail/Confused` + confusion target

- [ ] **`Services/ToolCalls/CategoryBoundaryMap.cs`** — Data model
  - `record CategoryBoundaryMap(string ModelId, DateTime TestedAt, Dictionary<string,CategoryResult> Results)`
  - `CategoryResult` — `Pass`, `Fail`, `Confused(targetCategory)`
  - Persist alongside `ToolCallProfile` in `tool-call-profiles.json`

- [ ] **`Agents/SwarmSession.cs`** — Gate task routing by category map
  - Researcher model: must pass `C7 task_planning` + `C6 structured_output`
  - Coder model: must pass `C3 code_exec` + `C1 file_ops`
  - Boss/TheOrc: must pass `C6 structured_output` + `C7 task_planning`
  - If a model fails its required categories → log warning + fall back to next-best available model

- [ ] **`UI/Panels/SwarmBoardPanel.xaml.cs`** — Category capability badges
  - Show colored capability dots (✅/⚠️/❌) per model next to each swarm slot picker
  - Tooltip: "Coder: file_ops ✅ code_exec ✅ network ⚠️"

- [ ] **`Tests/ToolCallTestWindow.xaml`** — Add "Categories" tab / expand grid
  - Show category pass matrix (7-column grid) per model

---

## Phase 3 — Adaptive Schema Generation ✅ `DONE`

> Generate tool schemas shaped to each model's capability profile and preferred format.
> Combines Format Fingerprint (Phase 1) + Category Map (Phase 2).

### Tasks

- [ ] **`Services/ToolCalls/SchemaGenerator.cs`** — New generator
  - Input: `ToolCallProfile` (with `FormatFingerprint` + `CategoryBoundaryMap`)
  - Output: `GeneratedToolSchema` — tool definition in the model's preferred format
  - Apply `SchemaSimplificationRules` for models with known complexity thresholds (see Phase 5)
  - `GenerateForRole(role, modelId)` — returns role-appropriate tool set shaped for that model

- [ ] **`Services/ToolCalls/SchemaLibrary.cs`** — Persistent confirmed-schema store
  - Persist to `%AppData%\OrchestratorIDE\schema-library.json`
  - `SaveConfirmed(modelId, toolName, schema)` / `Load(modelId, toolName)`
  - `GetBestSchema(modelId, toolName)` — returns confirmed schema if available, else default

- [ ] **`Core/AgentLoop.cs`** — Schema library integration
  - Before building `tools[]` array: check `SchemaLibrary.GetBestSchema()` per tool
  - If confirmed schema exists for this model, use it instead of default definition
  - Log when a custom schema is applied (visible in activity log)

- [ ] **Few-Shot Bootstrapping loop** (Option 4 from research)
  - After a successful probe run, collect successful tool call outputs as text examples
  - Store as `FewShotExamples` in `ToolCallProfile`
  - `SchemaGenerator.BootstrapFromExamples(examples, newToolDescription)` — generates new schema
  - Auto-probes the bootstrapped schema immediately before storing

---

## Phase 4 — Adaptive Schema Reduction Middleware ✅ `DONE`

> Automatically simplify tool schemas for models that fail on complexity.
> Runs transparently in `AgentLoop` — zero friction for users.

### Simplification Rules (applied in order)
1. Flatten all nested objects to top-level fields
2. Replace typed strings (`path`, `uri`) with plain `string`
3. Remove optional fields
4. Shorten field descriptions to < 10 words
5. Replace enum fields with plain `string` fields
6. Reduce to single required field only (last resort)

### Tasks

- [ ] **`Services/ToolCalls/SchemaSimplifier.cs`** — Middleware
  - `Simplify(toolDef, rules)` — applies ordered simplification rules
  - `DetermineComplexityThreshold(modelId)` — reads probe history to find failing point
  - Returns simplified `ToolDefinition` or original if no simplification needed

- [ ] **`Core/AgentLoop.cs`** — Wire simplifier
  - After `GetForProfile()`, before API call: `SchemaSimplifier.Simplify(tools, modelId)`
  - Log when simplification was applied

- [ ] **`Tests/ToolCallTestWindow`** — Show complexity score per model
  - New column: "Schema Complexity" — how much simplification was needed

---

## CLI Backend — tool-probe.exe ✅ `DONE`

> Full GOBLIN MIND subcommand interface for headless/scripted probing.
> Same `tool-call-profiles.json` file shared with the GUI (ToolCallTestWindow).

### Subcommands

| Command | What it does |
|---|---|
| `tool-probe dispatch` | Original dispatch probe (default — backward-compat) |
| `tool-probe format` | Format fingerprinting probe (5 variants) |
| `tool-probe categories` | Category boundary mapping (7 categories × 2 tests) |
| `tool-probe full` | Dispatch + format + categories in sequence |
| `tool-probe evolve` | Schema mutation + fitness evaluation (N generations) |
| `tool-probe list` | List stored profiles from `tool-call-profiles.json` |

### Notes
- `--json` flag on all subcommands for machine-readable output
- `--generations N` for evolve (default 3)
- `--tools filter` for dispatch subset
- Reads/writes same `tool-call-profiles.json` as the GUI
- `ProfileStore.MutateAsync` pattern — partial updates, non-destructive

### File
- `Tools/ToolCallTester/Program.cs` — full rewrite (~900 lines), all logic self-contained

---

## Phase 4b — FileWrite Payload Probe ⬜ `PRIORITY: HIGH (before routing nano to coder roles)`

> Determine each model's maximum safe write_file payload size.
> Motivation: T06 confirmed (2026-06-09) that nemotron-3-nano:4b-q8_0 truncates
> write_file JSON on payloads representing ~50–200 line Python files. Tool support
> is not binary — a model can pass format probes (short calls) and still fail on
> long-payload file writes. This probe closes that gap.

### Probe Definitions

| Probe ID | Tool call | Payload size | Success criteria |
|---|---|---|---|
| `FileWriteSmall` | `write_file hello.txt` | 1 line (~20 chars) | valid JSON, file written, content exact |
| `FileWriteMedium` | `write_file script.py` | ~50 lines (~1.5 KB) | valid JSON, file written, full content preserved |
| `FileWriteLarge` | `write_file app.py` | ~200 lines (~6 KB) | valid JSON, file written, full content preserved, no truncation |

### Metrics to Collect

For each probe per model:
- `valid_json` — tool call JSON parsed without error
- `file_written` — file actually appeared on disk
- `content_preserved` — written content matches expected (no truncation)
- `truncation_detected` — unbalanced braces in raw agentlog (opens > closes)
- `retry_count` — how many attempts before success (or max reached)
- `max_safe_payload_chars` — largest payload that reliably succeeds (derived from Small/Medium/Large)

### Known Results (pre-probe — T06 runtime evidence)

| Model | FileWriteSmall | FileWriteMedium | FileWriteLarge | Source |
|---|---|---|---|---|
| nemotron-3-nano:4b-q8_0 | unknown | ❌ truncated | ❌ truncated | T06 2026-06-09 |
| theorc-boss:gemma4 | ✅ (inferred) | ✅ (inferred) | TBD | swarm run observations |
| qwen2.5-coder:14b | ✅ (inferred) | ✅ (inferred) | TBD | benchmark scores |

### Tasks

- [ ] **`Services/ToolCalls/FileWriteProbeEngine.cs`** — New probe engine
  - 3 probe variants (Small / Medium / Large)
  - Each: send the model a write_file request with the target payload
  - Verify: file written to temp workspace, content matches, JSON was complete
  - Record: `FileWriteProbeResult` per model per probe

- [ ] **`ToolCallProfile`** — Add `FileWriteProfile` field
  - `MaxSafePayloadChars` — derived from largest passing probe
  - `FileWriteSmall/Medium/LargeResult` — `Pass / Fail / Truncated / Unknown`
  - Persist to `tool-call-profiles.json` alongside format/category profiles

- [ ] **`SwarmConfigAdvisor.cs`** — Gate coder role assignment
  - Before assigning a model as CODER: check `FileWriteProfile.FileWriteLargeResult`
  - If `Truncated` or `Fail`: log warning, suggest upgrade to ≥12B model
  - If `Unknown`: allow but log "unprobed for file-write payload capacity"

- [ ] **`Tests/ToolCallTestWindow`** — Add "FileWrite" column
  - Show S/M/L chips (✅/⚠️/❌) per model
  - Tooltip: "Small ✅ Medium ❌ (truncated at 1.2 KB)"

---

## Phase 5 — Evolutionary Schema Search ⬜ `PRIORITY: LOW (overnight/on-demand)`

> Systematically mutate tool schemas to find each model's highest-fitness calling convention.
> Rating: **8/10** — most novel, most expensive, run on-demand.

### Tasks

- [ ] **`Services/ToolCalls/SchemaEvolution.cs`** — Mutation engine
  - `SeedSchema` — starting tool definition
  - `MutationSet` — field renames, type variations, nesting changes, description verbosity
  - `RunGenerationAsync(model, seed, generations, ct)` — N rounds of mutation + probe
  - `FitnessMap` — tracks `pass/fail` per mutation variant

- [ ] **`Services/ToolCalls/FitnessMap.cs`** — Data model + persistence
  - Persist to `%AppData%\OrchestratorIDE\fitness-maps.json`
  - High-fitness variants auto-promoted to `SchemaLibrary` as confirmed schemas

- [ ] **Models menu** — "Run Schema Evolution…" menu item
  - Opens a dialog: pick model + seed tool + number of generations
  - Background task with progress bar
  - Results shown in `ToolCallTestWindow` under a new "Evolution" tab

---

## Phase 6 — TheOrc Steering & Correction Integration ✅ `DONE`

> The boss model reads capability profiles to steer the swarm.
> This is the core of Goblin Mind — TheOrc knows which goblin can do what.

### Tasks

- [ ] **`Agents/SwarmSession.cs`** — Capability-aware task dispatch
  - Before dispatching a subtask: call `GetCapableModel(taskCategory)` 
  - `GetCapableModel(category)` — ranks available models by category score, returns best fit
  - If no model passes the required category: TheOrc (boss) reformulates the task to avoid that category

- [ ] **Boss steering prompt** — Update system prompt for boss/TheOrc role
  - Inject capability summary: "Coder model handles: file_ops, code_exec. Does NOT handle: network."
  - Boss routes tasks accordingly without knowing model internals — just reads the summary

- [ ] **`Services/Swarm/SwarmConfigAdvisor.cs`** — Extend with capability data
  - `Recommend()` now weighs category boundary maps alongside benchmark scores
  - A model with 7/7 categories but low BossScore still gets considered for non-boss roles

- [ ] **`UI/Panels/SwarmBoardPanel.xaml`** — Goblin capability summary panel
  - Small expandable panel below each model picker
  - Shows: Format | Categories | Schema Complexity | Last Probed date
  - "Probe Now" button per slot

---

## Cross-Cutting Tasks

- [ ] **`ToolCallProfileStore.cs`** — Version the JSON schema (add `"version": 2`)
  - Migrate v1 profiles (no fingerprint/categories) gracefully
- [ ] **Activity log integration** — All probe/generation events logged to activity panel
- [ ] **`MODEL_PROFILES.md`** (new doc) — Auto-generated per-model capability summary
  - Generated by running all probes; human-readable reference
- [ ] **Wire `TotalVramGb` in `SwarmSession.cs`** — currently hardcoded to `0`
  - Call `SwarmConfigAdvisor.DetectHardwareAsync()` at swarm init, cache result

---

## Completion Criteria

The milestone is complete when:
- [ ] Any new Ollama model can be probed in < 2 minutes and produce a full capability profile
- [ ] TheOrc's swarm routing reads capability profiles instead of static config
- [ ] `AgentLoop` dispatches tool calls using the model's confirmed preferred format
- [ ] Schema simplification runs transparently for models that fail on complex schemas
- [ ] All results persist across sessions and survive app restart
- [ ] The ToolCallTestWindow shows: format | categories | schema complexity per model

---

## File Index

| File | Status | Phase |
|---|---|---|
| `Services/ToolCalls/ToolCallProbeEngine.cs` | ✅ Done | Foundation |
| `Services/ToolCalls/ToolCallProfileStore.cs` | ✅ Done | Foundation |
| `Core/AgentLoop.cs` (dispatch hook) | ✅ Done | Foundation |
| `Tests/ToolCallTestWindow.xaml/.cs` | ✅ Done | Foundation |
| `Services/ToolCalls/FormatProbeEngine.cs` | ✅ Done | Phase 1 |
| `Services/ToolCalls/CategoryProbeEngine.cs` | ✅ Done | Phase 2 |
| `Services/ToolCalls/CategoryBoundaryMap.cs` | ✅ Done (in CategoryProbeEngine) | Phase 2 |
| `Services/ToolCalls/SchemaGenerator.cs` | ✅ Done | Phase 3 |
| `Services/ToolCalls/SchemaLibrary.cs` | ✅ Done | Phase 3 |
| `Services/ToolCalls/SchemaSimplifier.cs` | ✅ Done | Phase 4 |
| `Agents/SwarmSession.cs` (routing) | ✅ Done | Phase 6 |
| `Services/ToolCalls/SchemaEvolution.cs` | ⬜ Todo (GUI/main project) | Phase 5 |
| `Services/ToolCalls/FitnessMap.cs` | ⬜ Todo (GUI/main project) | Phase 5 |
| `Tools/ToolCallTester/Program.cs` | ✅ Done (CLI — all subcommands) | CLI Backend |

---

*Codename: GOBLIN MIND | TheOrc v1.1 | Started: 2026-06-08*
