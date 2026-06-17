# TheOrc Dev Skill

Custom skill for TheOrchestrator development. Bakes in project conventions so
every Grok session starts with the right mental model.

---

## Project identity

- **Local AI coding assistant** — 100% on-device, no cloud in the shipped product
- **Dual UI:** WPF (`net10.0-windows`, primary shipping app) + Avalonia 12 (`net10.0`, cross-platform preview)
- Both UIs share `OrchestratorIDE/` service layer — never duplicate logic between them
- **License:** AGPL-3.0-or-later · SPDX headers required on every new source file

---

## Hard rules (never violate)

1. **No cloud API in shipped code.** `ANTHROPIC_API_KEY`, `CEREBRAS_API_KEY`, `XAI_API_KEY` are dev-time tools only — never referenced in `OrchestratorIDE/` or `OrchestratorIDE.Avalonia/`.
2. **Pit Boss model must be local.** No `glm-4.6:cloud`, no API-backed model in the training wizard UI.
3. **HIVE crypto = standard primitives only.** P-256 ECDSA, ECDH, HMAC-SHA256 via .NET BCL. No custom crypto.
4. **SQLite is the one store.** Never add a second DB file. All persistence extends `RepositoryBase` and targets `.orc/theorc.db`.
5. **Secrets never in repo.** API keys live as machine-level env vars (`[Environment]::SetEnvironmentVariable(...,'User')`). No `.env` files committed.

---

## Architecture quick-map

```
OrchestratorIDE/            WPF app (net10.0-windows)
  Core/                     OllamaClient, UpdateChecker, ScreenRecorder, ...
  Services/                 AgentLoop, SwarmOrchestrator, HiveService, PitBossService,
                            SqliteStore, DatasetCaptureService, CodeGraph/ (v1.9)
  Tools/                    ToolRegistry, SearchTools, ShellTools, GraphTools (v1.9)
  UI/Panels/                WPF panels (.xaml + .xaml.cs)
  UI/Dialogs/               WPF dialogs
OrchestratorIDE.Avalonia/   Avalonia app (net10.0)
  UI/Panels/                Avalonia panels (.axaml + .axaml.cs)
  UI/Controls/              MarkdownView, ...
OrchestratorIDE.UnitTests/          121 WPF unit tests
OrchestratorIDE.Avalonia.HeadlessTests/  21 Avalonia headless tests
OrchestratorIDE.UITests/    FlaUI black-box suite (T01-T20)
training_pit/               Dataset pipeline + Forge scripts
tools/                      Dev-time scripts (Cerebras gen, Codex review, etc.)
Tools/                      PowerShell review scripts (grok-review.ps1, dual-review.ps1)
```

---

## SQLite migration convention

- Current head: **Migration v4** (SQL persistence, HIVE, Phase 4)
- Next planned: **Migration v5** (CodeGraph — graph_nodes, graph_edges, graph_fts, graph_adr)
- All migrations live in `OrchestratorIDE/Services/SqliteStore.cs` as `ApplyMigrations()`
- Forward-only; never modify an existing migration
- Test in-memory: `new SqliteConnection("Data Source=:memory:")`

---

## Tool registration pattern

```csharp
// New tool set must follow SearchTools/ShellTools pattern:
public static class GraphTools
{
    public static void Register(ToolRegistry registry, CodeGraphService svc)
    {
        registry.Register(new ToolDefinition
        {
            Name = "graph_search",
            Description = "...",
            RequiresApproval = false,     // read-only tools never need approval
            ExecuteAsync = async (args, ct) => { ... }
        });
    }
}
```

---

## Dataset / training conventions

- Dataset pairing: `train_{KEY}.jsonl` + `eval_{KEY}.jsonl` — Training Pit auto-indexes by this pattern
- Current registered datasets: `train_v2gold` / `eval_v2gold` (1,784 / 200 examples)
- **Never generate training data with Claude API** — use Cerebras (`tools/generate_cerebras_gold.py`) or Grok
- Adapter output: `training_pit/outputs/lora_{KEY}/adapter/`

---

## Review workflow

- Before every commit: `pwsh Tools\grok-review.ps1` (Grok replaces Codex this cycle)
- For parallel review: `pwsh Tools\dual-review.ps1` (Grok + Claude)
- Exit codes: `CLEAN=0`, `BLOCKER=1`, `MINOR=2`, `error=3`
- Never skip the review gate for non-trivial changes

---

## Testing conventions

- All new services: unit tests in `OrchestratorIDE.UnitTests/` (NUnit, in-memory SQLite)
- New Avalonia controls: headless tests in `OrchestratorIDE.Avalonia.HeadlessTests/`
- FlaUI tests: `T{NN}_*.cs` naming, `[Category("...")]` tag
- Run all: `dotnet test` from repo root

---

## When implementing a new feature

1. Read relevant existing services in `OrchestratorIDE/Services/` first
2. For DB work: extend `RepositoryBase`, add a forward-only migration
3. For new tools: follow `ToolRegistry.Register` pattern, `RequiresApproval = false` for read-only
4. Wire into both WPF and Avalonia if it's a UI feature
5. Run `pwsh Tools\grok-review.ps1` before committing
6. Check test count didn't regress: `dotnet test` must stay green
