# TheOrc / OrchestratorIDE — Agent Instructions

Cross-tool briefing doc. Any AI agent working in this repo (Claude, Codex, Grok,
or otherwise) should read this first. Bakes in project conventions so every
session starts with the right mental model — no need to re-derive architecture
or rules from scratch.

---

## Project identity

- **Local AI coding assistant** — 100% on-device, no cloud in the shipped product
- **Dual UI:** WPF (`net10.0-windows`, primary shipping app) + Avalonia 12 (`net10.0`, cross-platform preview)
- Both UIs share `OrchestratorIDE/` service layer — never duplicate logic between them
- **Avalonia-first migration work:** new Native Runtime operator-facing UI/status surfaces land in Avalonia first; touch WPF only for shared core compatibility or an explicit fallback fix.
- **License:** AGPL-3.0-or-later · SPDX headers required on every new source file
- Repo: `hardcoreerik/TheOrc` on GitHub. Dev work happens in worktree `F:\Ai\OrchestratorIDE-dev`, branch `master`.

---

## Hard rules (never violate)

1. **No cloud API in shipped code.** `ANTHROPIC_API_KEY`, `CEREBRAS_API_KEY`, `XAI_API_KEY` are dev-time tools only — never referenced in `OrchestratorIDE/` or `OrchestratorIDE.Avalonia/`.
2. **No Anthropic API for bulk data generation, ever.** Use Cerebras (`tools/generate_cerebras_gold.py`) or Grok. `generate_claude_gold.py` was archived 2026-06-17 for violating this.
3. **Pit Boss model must be local.** No `glm-4.6:cloud`, no API-backed model in the training wizard UI. Prefer ≤7B to spare VRAM for training.
4. **HIVE crypto = standard primitives only.** ECDSA P-256, ECDH X25519, HMAC-SHA256, AES-256-GCM via .NET BCL. No custom crypto.
5. **SQLite is the one store.** Never add a second DB file. All persistence extends `RepositoryBase` and targets `.orc/theorc.db`. Parameterized queries only — never string-concatenate into SQL.
6. **Secrets never in repo.** API keys live as machine-level env vars (`[Environment]::SetEnvironmentVariable(...,'User')`). No `.env` files committed.
7. **Training corpora stay as JSONL files.** Never put training datasets in SQL.
8. **Training data must pass the suitability gate** (`training_pit/scripts/suitability_gate.py`) before training starts.
9. **Reviewer-gate BLOCKER findings must not be silently bypassed** or weakened during refactors.
10. **Dataset pairing convention:** `train_{KEY}.jsonl` + `eval_{KEY}.jsonl` — Training Pit auto-indexes by this pattern.
11. **HIVE secrets must be initialized before store access.** Call `SecretProtection.Initialize(...)` before any HIVE store or pairing code touches `SecretProtection.Current`.

---

## Architecture quick-map

```
OrchestratorIDE/            WPF app (net10.0-windows)
  Core/                     OllamaClient, AgentLoop, ContextManager, UpdateChecker, ScreenRecorder, ...
  Core/Runtime/             IModelRuntime abstraction — OllamaRuntime, LlamaCppServerRuntime,
                            LLamaSharpRuntime (Native Runtime migration, see status below)
  Services/                 SwarmOrchestrator, HiveService, PitBossService, SqliteStore,
                            DatasetCaptureService, CodeGraph/ (v1.9)
  Services/Hive/             HiveWorkerAgent, HiveIdentity, HivePeerStore, HiveAuthMiddleware
  Services/Swarm/            OllamaReviewService (runtime-backed reviewer gate)
  Agents/                    SwarmSession
  Research/                  ChatEngine
  Tools/                     ToolRegistry, SearchTools, ShellTools, GraphTools (v1.9)
  UI/Panels/                 WPF panels (.xaml + .xaml.cs)
  UI/Dialogs/                WPF dialogs
OrchestratorIDE.Avalonia/   Avalonia app (net10.0)
  UI/Panels/                 Avalonia panels (.axaml + .axaml.cs)
  UI/Controls/                MarkdownView, ...
OrchestratorIDE.UnitTests/          WPF unit tests (NUnit, in-memory SQLite)
OrchestratorIDE.Avalonia.HeadlessTests/  Avalonia headless tests
OrchestratorIDE.UITests/    WPF UI/FlaUI and shared headless test sources (`T{NN}_*.cs`)
training_pit/               Dataset pipeline + Forge training scripts
.grok/                      Project truth, specs, review scratch — PROJECT_TRUTH.md is canonical
docs/ROADMAP.md             Public roadmap/status narrative; keep in sync with PROJECT_TRUTH
tools/  & Tools/            Dev-time scripts (Cerebras gen, Codex/Grok review runners)
```

---

## Native Runtime migration status

`Core/Runtime/IModelRuntime.cs` is the backend-neutral inference interface. Goal: drop
the Ollama dependency, run GGUF in-process via LLamaSharp.

| Phase | Scope | Status |
|---|---|---|
| 0 | `IModelRuntime` + `OllamaRuntime` (wrap existing `OllamaClient`); migrate one call site; zero behavior change | ✅ Landed |
| 1 | `LlamaCppServerRuntime` — wraps **existing** `LlamaServerManager` | ✅ Landed |
| 2 | `LLamaSharpRuntime` — in-process GGUF + LoRA; the "no Ollama" win | ✅ Prototype landed (LoRA apply still deferred) |
| 2.5 | Close abstraction leaks: `HiveWorkerAgent` + reviewer gate now use `IModelRuntime`; remote HIVE task-queue/node HTTP remains separate plumbing, not LLM inference | ✅ Closed |
| 3 | ModelDepot + SessionManager + AdapterManager (boss/worker/reviewer) + telemetry | 🔶 Near-closed — ModelDepot, SessionManager, AdapterManager, and `RuntimeOrchestrator` (wires all three) landed and Grok-CLEAN; §7 spike closed across 2 LoRA samples; first telemetry surface (Ollama only) landed; remaining: local-runtime-backed telemetry + flipping a live call site off Ollama |
| 4 | `OrcScheduler` — VRAM + lane-aware dispatch, pipeline boss→workers | ⬜ Planned |
| 5 | Prefix KV cache (research, non-blocking) | ⬜ Research |

When migrating a call site to `IModelRuntime`, follow the pattern already used for
`SwarmSession`/`ChatEngine` (commit `dc79041`). Full spec: `.grok/RUNTIME_PHASE0_SPEC.md`.
Current accurate status always lives in `.grok/PROJECT_TRUTH.md`; public-facing status
lives in `docs/ROADMAP.md`. Check both before claiming something is "done" or "not started."

---

## SQLite migration convention

- Migrations live in `OrchestratorIDE/Services/Data/Migrations.cs` as `Migrations.All`
- `OrchestratorIDE/Services/Data/SqliteStore.cs` calls `MigrationRunner.Apply(conn)` from `Initialize()`
- Forward-only; add a new numbered migration and never modify an existing migration
- Test in-memory: `new SqliteConnection("Data Source=:memory:")`
- Check `.grok/PROJECT_TRUTH.md` for the current migration head before adding a new one

---

## Tool registration pattern

```csharp
// New tool set must follow SearchTools/ShellTools/GraphTools pattern:
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

## Review workflow

- Run a review pass on every non-trivial commit. Available reviewers:
  - `pwsh Tools\codex-review.ps1` — Codex CLI (avoids stdin hang; never call `codex exec` directly)
  - `pwsh Tools\grok-review.ps1` — Grok adversarial review
  - `pwsh Tools\dual-review.ps1` — Grok + Claude in parallel
- **Token budget rule:** Grok review is meant to spend reviewer context so Codex does not have to. Keep Codex inspection narrow before/after Grok: stage tight diffs, use targeted `rg`/small line windows, do not dump large files, and only locally inspect Grok findings that need verification or fixes. Do not run Grok for tiny doc/status changes unless the user asks or the commit has real risk.
- Exit codes: `0 = CLEAN or MINOR-only`, `1 = BLOCKER`, `2 = timeout`, `5 = tool/model error`
- Verify build after every change: `dotnet build OrchestratorIDE.Avalonia/OrchestratorIDE.Avalonia.csproj --no-restore -v q` — expect 0 errors.
- Commit each logical fix separately so history stays bisectable.

---

## Testing conventions

- All new services: unit tests in `OrchestratorIDE.UnitTests/` (NUnit, in-memory SQLite)
- New Avalonia controls: headless tests in `OrchestratorIDE.Avalonia.HeadlessTests/`
- FlaUI tests: `T{NN}_*.cs` naming, `[Category("...")]` tag
- Run all: `dotnet test` from repo root — count must not regress

---

## When implementing a new feature

1. Read relevant existing services in `OrchestratorIDE/Services/` first — don't re-derive from scratch.
2. Check `.grok/PROJECT_TRUTH.md` and `docs/ROADMAP.md` for current accurate status before assuming a feature is or isn't done.
3. For DB work: extend `RepositoryBase`, add a forward-only migration.
4. For new tools: follow `ToolRegistry.Register` pattern, `RequiresApproval = false` for read-only.
5. Wire into both WPF and Avalonia if it's a UI feature.
6. Run a review pass before committing.
7. Run `dotnet test` — must stay green.
8. Update `.grok/PROJECT_TRUTH.md` and `docs/ROADMAP.md` when your change closes a known gap, changes a shipped/planned status, or alters the project direction — don't let docs drift from reality.
