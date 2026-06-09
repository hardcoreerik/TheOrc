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
| `Services/ToolCalls/SchemaEvolution.cs` | ⬜ Todo | Phase 5 |
| `Services/ToolCalls/FitnessMap.cs` | ⬜ Todo | Phase 5 |

---

*Codename: GOBLIN MIND | TheOrc v1.1 | Started: 2026-06-08*
