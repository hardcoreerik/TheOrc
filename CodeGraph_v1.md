<!-- Copyright (C) 2025-present hardcoreerik / TheOrc contributors | SPDX-License-Identifier: AGPL-3.0-or-later -->
# TheOrc — Code Knowledge Graph (CodeGraphService) v1

**Target version:** v1.9.0
**Status:** Design sketch
**Author:** design pass 2026-06-16

Turn the open workspace into a queryable graph of code structure, so the agent
queries the graph instead of grepping/reading files one-by-one. Two payoffs:

- **Level 1** — agent spends token budget on reasoning, not exploration
  (fewer grep/read cycles). Serves the five-nines tool-calling goal.
- **Level 2** — graph-grounded trajectories are higher-signal → cleaner LoRA
  datasets, and the graph tools become a *new trainable tool schema* fed
  through the existing capture → ToolCallProbeEngine pipeline.

Built **100% native and in-process** — no external binary, no install, no
network egress. Reuses Roslyn + SQLite + (later) Ollama, all already in stack.

---

## Scope

| | v1 (this doc) | v2 | v3 |
|---|---|---|---|
| Languages | **C# only** (Roslyn) | + Ollama embeddings | + tree-sitter polyglot |
| Search | FTS5 (BM25) + structural | + semantic (cosine) | — |
| Community detection | — | Louvain modules | — |
| HIVE shared graph artifact | — | — | export/import graph |

v1 deliberately ships C#-first: Roslyn gives a *full semantic model* (symbols,
call edges, find-all-refs, type resolution) with zero new dependencies, and C#
is both our own codebase and the primary capture-target language.

---

## Architecture

```
OrchestratorIDE/Services/CodeGraph/
  CodeGraphService.cs      Lifecycle + public query API (façade over the store)
  RoslynIndexer.cs         Workspace → nodes/edges (MSBuildWorkspace / AdhocWorkspace)
  ComplexityAnalyzer.cs    Roslyn ControlFlowGraph → per-node metrics
  GraphModels.cs           CodeNode / CodeEdge / records
  Data/GraphRepository.cs  SQLite read/write (extends RepositoryBase)
OrchestratorIDE/Tools/
  GraphTools.cs            Register(registry, svc): graph_search, trace_path,
                           get_architecture, detect_changes
```

- **Owner:** single process (the app), same as `SqliteStore`. Remote HIVE nodes
  never touch the file — they query via the existing HTTP layer (v3 concern).
- **Storage:** new tables in the existing `.orc/theorc.db` via a forward-only
  **Migration v5** (current head is v4). No second DB file.
- **Indexing:** background, off the UI thread. Triggered on workspace open and
  on git-change detection (reuse the swarm's existing watcher signal).

---

## Data model (Migration v5)

```sql
CREATE TABLE graph_nodes (
    id            INTEGER PRIMARY KEY,
    project       TEXT    NOT NULL,           -- workspace key (multi-project ready)
    label         TEXT    NOT NULL,           -- Function|Method|Class|Interface|Route|File
    name          TEXT    NOT NULL,           -- short name
    qualified_name TEXT   NOT NULL,           -- Namespace.Type.Member (Roslyn symbol key)
    file_path     TEXT    NOT NULL,
    line_start    INTEGER NOT NULL,
    line_end      INTEGER NOT NULL,
    -- complexity (Function/Method only; NULL otherwise)
    cyclomatic            INTEGER,
    cognitive             INTEGER,
    loop_depth            INTEGER,
    transitive_loop_depth INTEGER,            -- worst-case along CALLS edges
    linear_scan_in_loop   INTEGER,            -- hidden O(n²) the loop-depth misses
    is_recursive          INTEGER DEFAULT 0,
    degree        INTEGER DEFAULT 0,          -- in+out edge count (popularity)
    UNIQUE(project, qualified_name)
);
CREATE INDEX ix_gn_project ON graph_nodes(project);
CREATE INDEX ix_gn_label   ON graph_nodes(project, label);
CREATE INDEX ix_gn_file    ON graph_nodes(project, file_path);

CREATE TABLE graph_edges (
    id        INTEGER PRIMARY KEY,
    project   TEXT    NOT NULL,
    src_id    INTEGER NOT NULL REFERENCES graph_nodes(id) ON DELETE CASCADE,
    dst_id    INTEGER NOT NULL REFERENCES graph_nodes(id) ON DELETE CASCADE,
    edge_type TEXT    NOT NULL,               -- CALLS|IMPORTS|IMPLEMENTS|DATA_FLOWS|ROUTES_TO
    UNIQUE(project, src_id, dst_id, edge_type)
);
CREATE INDEX ix_ge_src ON graph_edges(src_id, edge_type);
CREATE INDEX ix_ge_dst ON graph_edges(dst_id, edge_type);

-- BM25 full-text search over names/qualified names (SQLite FTS5, no new dep)
CREATE VIRTUAL TABLE graph_fts USING fts5(
    name, qualified_name, file_path,
    content='graph_nodes', content_rowid='id',
    tokenize='unicode61'
);

-- Persistent Architecture Decision Records (repo-scoped agent memory)
CREATE TABLE graph_adr (
    id         INTEGER PRIMARY KEY,
    project    TEXT NOT NULL,
    section    TEXT NOT NULL,                 -- e.g. "auth", "persistence"
    content    TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(project, section)
);
```

camelCase identifiers are split into tokens at write time (`updateCloudClient`
→ `update cloud client`) so BM25 natural-language queries hit. Structural
boosting (Function/Method > Route > Class > File) is applied at query time, not
stored.

---

## Roslyn indexer

1. Load the workspace. Prefer `MSBuildWorkspace.OpenProjectAsync` when a
   `.csproj`/`.sln` exists; fall back to `AdhocWorkspace` over loose `.cs`
   files. Roslyn is already referenced (`Microsoft.CodeAnalysis.CSharp 4.13.0`).
2. For each `Compilation`, walk the `SemanticModel`:
   - **Nodes** — every `INamedTypeSymbol` (Class/Interface) and `IMethodSymbol`
     (Function/Method). `qualified_name` = `ISymbol.ToDisplayString(...)`.
   - **CALLS** — `SymbolFinder.FindCallersAsync` / invocation-expression walk.
   - **IMPLEMENTS** — interface/base lists.
   - **ROUTES_TO** — `[HttpGet]`/`[Route]` attribute scan → `Route` nodes
     (we have ASP.NET-style controllers in the HIVE node server).
3. **ComplexityAnalyzer** — Roslyn `ControlFlowGraph.Create(method)` gives
   cyclomatic + loop nesting directly; cognitive + `linear_scan_in_loop`
   (find/Contains/IndexOf inside a loop body) via syntax walk.
   `transitive_loop_depth` = max over CALLS edges, computed in a second pass
   after all nodes exist.
4. Write in one transaction per project; rebuild FTS via triggers or a single
   `INSERT INTO graph_fts(...) SELECT ...` after the node batch.

Incremental re-index on change: re-walk only the changed documents
(`detect_changes` reuses git diff), delete+reinsert their nodes (CASCADE clears
stale edges), recompute `degree`/`transitive_loop_depth` for the touched
neighborhood.

---

## Tool surface (registered via ToolRegistry)

Same registration shape as `SearchTools.Register(registry, workspaceRoot)`.
All read-only → `RequiresApproval = false`. Plain-text results, `[OK]`/`[ERROR]`.

| Tool | Args | Returns |
|---|---|---|
| `graph_search` | `query` or `name_pattern`, `label?`, `file_pattern?`, `min_degree?`, `limit=50` | Ranked nodes: qualified_name, label, file:line, degree. BM25 + structural boost. |
| `trace_path` | `qualified_name`, `direction=both`, `depth=3`, `mode=calls\|data_flow` | Caller/callee tree (or value-flow hops). Replaces grep-for-callers. |
| `get_architecture` | *(none)* | Packages/namespaces, route list, top-degree hubs, file/edge counts. |
| `detect_changes` | `since=HEAD~1` | Changed symbols + their inbound impact set (blast radius). |
| `get_code_snippet` | `qualified_name` | Source span for a node (search → snippet flow). |

`graph_adr` (get/update) is exposed as a sixth tool so the agent persists and
re-reads architectural decisions across sessions.

**Why typed methods, not Cypher:** a hand-written query API covers every Level 1
use case with no parser to build/secure. Cypher is a v3+ "if users ask" item.

---

## Integration points

- **AgentLoop** — `GraphTools` registered alongside `SearchTools`. System
  prompt nudges: "prefer graph_search/trace_path over grep_code for structural
  questions." This is where Level 1 token savings land.
- **DatasetCapture** — capture already records the tool-call trajectory; graph
  tools flow through unchanged, so graph-grounded trajectories land in the
  corpus automatically. Tag captures that used graph tools so we can measure
  trajectory-quality lift (Level 2).
- **Review gate / Pit Boss** — query `transitive_loop_depth >= 3 OR
  linear_scan_in_loop >= 1` to auto-surface refactor/hot-path targets.
- **No UI required for v1** — it's a tool layer. A graph panel is a later nicety.

---

## Non-goals (v1)

- Semantic / embedding search (→ v2, Ollama `nomic-embed-text`).
- Non-C# languages (→ v3, tree-sitter bindings).
- Cypher query engine.
- Cross-repo / HIVE shared-graph artifact (→ v3).
- 3D graph visualization.

---

## Build order

1. Migration v5 + `GraphRepository` (+ unit tests over an in-memory DB).
2. `RoslynIndexer` nodes + CALLS/IMPLEMENTS (no complexity yet) + `graph_search`.
3. `ComplexityAnalyzer` + `trace_path` + `get_architecture`.
4. `detect_changes` (git diff → impact) + `graph_adr`.
5. Wire into `AgentLoop`; tag graph-tool captures; Grok review each increment.
