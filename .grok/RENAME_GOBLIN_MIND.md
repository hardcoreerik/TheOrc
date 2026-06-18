# Rename: GOBLIN MIND → ORCISH TONGUE

> **Status:** READY TO EXECUTE on greenlight. Not yet applied. Mechanical rename; needs a build + test pass after.
> **Why:** "GOBLIN MIND" collides with "HIVE MIND" (both `_____ MIND`, unrelated systems). ORCISH TONGUE names what the system does — speak every local model's tool-call dialect. It is TheOrc's universal tool caller.
> **Scope:** Only `GOBLIN MIND` / `GoblinMind` / `Goblin Mind`. **GOBLIN HARVEST stays** (unrelated, no collision).

---

## What the system is

The tool-call format intelligence under `OrchestratorIDE/Services/ToolCalls/` — `FormatProbeEngine`, `CategoryProbeEngine`, `SchemaLibrary`, `SchemaSimplifier`, `SchemaGenerator`, `SchemaEvolution`, `FitnessMap`, `ToolCallProfileStore`. Probes each model's preferred tool-call format and adapts. See `RUNTIME_PHASE0_SPEC.md` §11 for the native-runtime GBNF upgrade path.

---

## Two layers to change

### 1. Code identifiers (compile-affecting — change carefully, build after)

| Symbol | File | New name |
|---|---|---|
| `RunGoblinMindAsync` (method + call site) | `Tests/ToolCallTestWindow.xaml.cs:249,254` | `RunOrcishTongueAsync` |
| `BtnRunGoblinMind_Click` (handler) | `Tests/ToolCallTestWindow.xaml.cs:237` + `.xaml:103` | `BtnRunOrcishTongue_Click` |
| `BtnRunGoblinMind` (x:Name) | `Tests/ToolCallTestWindow.xaml:98` | `BtnRunOrcishTongue` |
| `HasGoblinMindProfile` (property) | `Services/ToolCalls/ToolCallProfileStore.cs:49` | `HasOrcishTongueProfile` |

Check call sites of `HasGoblinMindProfile` before renaming (grep first — it may be referenced in UI/services).

### 2. Display strings + comments (text only — no build impact)

~50 occurrences of `GOBLIN MIND` / `Goblin Mind` across:
- **Code comments + status text** — `AgentLoop.cs`, `SwarmSession.cs`, `SwarmSteering.cs` ("## Goblin Capability Map" → "## Orcish Capability Map"), `ToolCallProfileStore.cs`, all `Services/ToolCalls/*.cs`, `Services/Models/*.cs`
- **UI** — `ToolCallTestWindow.xaml` (Title, button tooltips), `ModelWikiWindow.xaml(.cs)`, `SwarmBoardPanel.xaml(.cs)`, `ModelCompareWindow.xaml.cs`
- **Docs** — `docs/ARCHITECTURE.md`, `GLOSSARY.md`, `FAQ.md`, `MODEL_GUIDE.md`, `MODEL_WIKI_AND_LAB.md`, `SPONSOR_TEST_LAB.md`, `SINGLE_AGENT_GUIDE.md`, `docs/README.md`, root `README.md`
- **Assets** — `Assets/hero-architecture.svg` (2 text nodes)
- **Tools** — `Tools/ToolCallTester/Program.cs`
- **Tests** — `OrchestratorIDE.UITests/Tests/T11_SteeringTests.cs`

### 3. Leave alone (historical / data integrity)

- `training_pit/datasets/**` — `reviewed_v1.json`, `*.jsonl.bak`: **do not edit.** These are captured training data; the literal string is part of the recorded example, not a label. Rewriting them would corrupt provenance.
- `.grok/PROJECT_TRUTH.md` version history — keep the historical "GOBLIN MIND" in shipped-version descriptions, but add a note that it is now ORCISH TONGUE.

---

## Execution plan (one focused commit, post-greenlight)

1. Grep `HasGoblinMindProfile` call sites; rename property + all references.
2. Rename the 3 test-window symbols (method, handler, x:Name) — keep handler name and x:Name in sync or XAML won't bind.
3. Sweep display strings `GOBLIN MIND` → `ORCISH TONGUE` and `Goblin Mind` → `Orcish Tongue` across code/docs/UI/SVG, **excluding `training_pit/datasets/`**.
4. `SwarmSteering.cs` "Goblin Capability Map" → "Orcish Capability Map".
5. Build (`dotnet build`) + run the UI/tool-call test suite (T11 + ToolCallTestWindow path).
6. Update `GLOSSARY.md` with a "formerly GOBLIN MIND" alias line so old references resolve.
7. One commit: `refactor: rename GOBLIN MIND → ORCISH TONGUE (universal tool caller)`.

**Do not bundle with the runtime work or v3.** Standalone, reviewable, mechanical.
