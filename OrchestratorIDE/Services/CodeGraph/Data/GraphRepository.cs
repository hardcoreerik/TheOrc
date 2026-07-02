// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OrchestratorIDE.Services.CodeGraph;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.Services.CodeGraph.Data;

/// <summary>
/// SQLite persistence for the code knowledge graph (nodes, edges, FTS5 search index, ADRs).
/// Extends RepositoryBase so all SQL is parameterized (no injection) and uses the single
/// .orc/theorc.db (WAL, shared with captures/hive/etc).
///
/// CamelCase names are split at write time into the FTS index (so "updateCloudClient" matches
/// natural language "cloud client update"). The nodes table always stores the original
/// qualified_name / name for display and symbol resolution. FTS correction uses direct
/// 'delete' + insert after the AFTER triggers have run (external-content pattern).
///
/// All heavy writes use InTransaction for atomicity + correct FTS + degree recompute.
/// </summary>
public sealed class GraphRepository : RepositoryBase
{
    public GraphRepository(SqliteStore store) : base(store) { }

    // ─────────────────────────────────────────────────────────────────────────
    // Write path — nodes + FTS correction + edges + degrees
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Idempotent upsert of a single node (keyed on (project, qualified_name)).
    /// Use for small updates or tests. The write is wrapped in a transaction so the
    /// FTS correction (split tokens) and any trigger side-effects are atomic.
    /// Returns the stable row id.
    /// </summary>
    public int UpsertNode(CodeNode node)
    {
        int id = 0;
        InTransaction((conn, tx) =>
        {
            const string sql = """
                INSERT INTO graph_nodes
                    (project, label, name, qualified_name, file_path,
                     line_start, line_end, cyclomatic, cognitive, loop_depth,
                     transitive_loop_depth, linear_scan_in_loop, is_recursive, degree)
                VALUES
                    ($project, $label, $name, $qname, $fpath,
                     $lstart, $lend, $cyc, $cog, $ld, $tld, $lin, $rec, 0)
                ON CONFLICT(project, qualified_name) DO UPDATE SET
                    label                 = excluded.label,
                    name                  = excluded.name,
                    file_path             = excluded.file_path,
                    line_start            = excluded.line_start,
                    line_end              = excluded.line_end,
                    cyclomatic            = excluded.cyclomatic,
                    cognitive             = excluded.cognitive,
                    loop_depth            = excluded.loop_depth,
                    transitive_loop_depth = excluded.transitive_loop_depth,
                    linear_scan_in_loop   = excluded.linear_scan_in_loop,
                    is_recursive          = excluded.is_recursive
                """;

            using var cmd = CreateCmd(conn, tx, sql);
            BindNode(cmd.Parameters, node);
            cmd.ExecuteNonQuery();

            // Fetch the id (works for both insert and the conflict update path)
            using var idCmd = CreateCmd(conn, tx,
                "SELECT id FROM graph_nodes WHERE project = $p AND qualified_name = $q");
            P(idCmd.Parameters, "$p", node.Project);
            P(idCmd.Parameters, "$q", node.QualifiedName);
            var idObj = idCmd.ExecuteScalar();
            id = idObj is null ? throw new InvalidOperationException("Failed to obtain node id after upsert") : Convert.ToInt32(idObj);

            // The node write (insert or update) fired triggers that (re)populated graph_fts
            // with the *raw* name/qname. Correct it to the camel-split version for search.
            CorrectFtsForId(tx, id, node.Name, node.QualifiedName, node.FilePath);
        });

        // After tx, recompute degree for this node (and neighbors) — cheap for single.
        RecomputeDegrees(node.Project);
        return id;
    }

    /// <summary>
    /// Atomically replaces the graph for one project: deletes prior nodes for the project
    /// (CASCADE removes all their edges and cleans FTS via triggers), inserts the supplied
    /// nodes (with FTS corrected to split tokens), wires the edges (by qualified name),
    /// then recomputes degree for the whole project.
    ///
    /// This is the bulk path used by RoslynIndexer on first index and on file-set changes.
    /// </summary>
    public void ReplaceGraph(
        string project,
        IReadOnlyList<CodeNode> nodes,
        IReadOnlyList<(string SrcQualified, string DstQualified, string EdgeType)> edges)
    {
        if (string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("project required", nameof(project));

        InTransaction((conn, tx) =>
        {
            // Full project wipe (edges + fts rows via cascade + delete triggers)
            DeleteFabricGraphLinksForProject(tx, project);
            ExecuteOn(tx, "DELETE FROM graph_nodes WHERE project = $p",
                ps => P(ps, "$p", project));

            var qnameToId = new Dictionary<string, int>(StringComparer.Ordinal);

            // Dedup caller list by qualified_name (last wins) to avoid UNIQUE violation on bulk path (blocker fix).
            var uniqueNodes = nodes
                .GroupBy(n => n.QualifiedName, StringComparer.Ordinal)
                .Select(g => g.Last())
                .ToList();

            const string ins = """
                INSERT INTO graph_nodes
                    (project, label, name, qualified_name, file_path,
                     line_start, line_end, cyclomatic, cognitive, loop_depth,
                     transitive_loop_depth, linear_scan_in_loop, is_recursive, degree)
                VALUES
                    ($project, $label, $name, $qname, $fpath,
                     $lstart, $lend, $cyc, $cog, $ld, $tld, $lin, $rec, 0)
                """;

            foreach (var n in uniqueNodes)
            {
                using var cmd = CreateCmd(conn, tx, ins + "; SELECT last_insert_rowid();");
                BindNode(cmd.Parameters, n);
                var idObj = cmd.ExecuteScalar();
                var newId = idObj is null ? throw new InvalidOperationException("Failed to obtain node id after bulk insert") : Convert.ToInt32(idObj);
                qnameToId[n.QualifiedName] = newId;

                // Correct the fts row that the AFTER INSERT trigger just wrote (raw → split)
                CorrectFtsForId(tx, newId, n.Name, n.QualifiedName, n.FilePath);
            }

            // Wire edges (only those whose endpoints are present in this batch)
            foreach (var (srcQ, dstQ, et) in edges)
            {
                if (qnameToId.TryGetValue(srcQ, out var sid) &&
                    qnameToId.TryGetValue(dstQ, out var did))
                {
                    ExecuteOn(tx,
                        """
                        INSERT OR IGNORE INTO graph_edges (project, src_id, dst_id, edge_type)
                        VALUES ($p, $s, $d, $e)
                        """,
                        ps =>
                        {
                            P(ps, "$p", project);
                            P(ps, "$s", sid);
                            P(ps, "$d", did);
                            P(ps, "$e", et);
                        });
                }
            }

            RecomputeProjectDegrees(tx, project);
        });
    }

    /// <summary>Delete all nodes (and by cascade all edges + FTS entries) for a project.</summary>
    public void ClearProject(string project)
    {
        InTransaction((conn, tx) =>
        {
            DeleteFabricGraphLinksForProject(tx, project);
            ExecuteOn(tx, "DELETE FROM graph_nodes WHERE project = $p",
                ps => P(ps, "$p", project));
        });
    }

    /// <summary>
    /// Delete nodes for specific files within a project (incremental re-index support).
    /// CASCADE + delete triggers clean edges and FTS rows.
    /// </summary>
    public void DeleteNodesForFiles(string project, IEnumerable<string> filePaths)
    {
        var files = filePaths?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct().ToList();
        if (files is null || files.Count == 0) return;

        InTransaction((conn, tx) =>
        {
            foreach (var f in files)
            {
                DeleteFabricGraphLinksForFile(tx, project, f);
                ExecuteOn(tx,
                    "DELETE FROM graph_nodes WHERE project = $p AND file_path = $f",
                    ps =>
                    {
                        P(ps, "$p", project);
                        P(ps, "$f", f);
                    });
            }
            RecomputeProjectDegrees(tx, project);   // keep degrees correct for survivors (blocker fix)
        });
    }

    private static void DeleteFabricGraphLinksForProject(SqliteTransaction tx, string project)
    {
        ExecuteOn(tx,
            """
            DELETE FROM fabric_graph_links
            WHERE (source_kind = 'codegraph_node' AND source_id IN (
                    SELECT CAST(id AS TEXT) FROM graph_nodes WHERE project = $p
                ))
               OR (target_kind = 'codegraph_node' AND target_id IN (
                    SELECT CAST(id AS TEXT) FROM graph_nodes WHERE project = $p
                ))
            """,
            ps => P(ps, "$p", project));
    }

    private static void DeleteFabricGraphLinksForFile(SqliteTransaction tx, string project, string filePath)
    {
        ExecuteOn(tx,
            """
            DELETE FROM fabric_graph_links
            WHERE (source_kind = 'codegraph_node' AND source_id IN (
                    SELECT CAST(id AS TEXT) FROM graph_nodes WHERE project = $p AND file_path = $f
                ))
               OR (target_kind = 'codegraph_node' AND target_id IN (
                    SELECT CAST(id AS TEXT) FROM graph_nodes WHERE project = $p AND file_path = $f
                ))
            """,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$f", filePath);
            });
    }

    /// <summary>Insert a single edge (idempotent via OR IGNORE). Used by tests / small updates.</summary>
    public void InsertEdge(int srcId, int dstId, string edgeType, string project)
    {
        Execute(
            """
            INSERT OR IGNORE INTO graph_edges (project, src_id, dst_id, edge_type)
            VALUES ($p, $s, $d, $e)
            """,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$s", srcId);
                P(ps, "$d", dstId);
                P(ps, "$e", edgeType);
            });
    }

    /// <summary>Recompute in+out degree for every node in the project (call after bulk changes).</summary>
    public void RecomputeDegrees(string project)
    {
        Execute(
            """
            UPDATE graph_nodes
            SET degree = (
                SELECT COUNT(*) FROM graph_edges e
                WHERE (e.src_id = graph_nodes.id OR e.dst_id = graph_nodes.id)
                  AND e.project = graph_nodes.project
            )
            WHERE project = $p
            """,
            ps => P(ps, "$p", project));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ADR (graph_adr) — step 4: list|get|add with title/decision/status/created_at/body
    // Old Upsert/GetAdr kept for compat (map section<->title, content<->decision)
    // ─────────────────────────────────────────────────────────────────────────

    public void UpsertAdr(AdrRecord a)
    {
        // Compat path for pre-step4 call sites: fall back to empty if Title not supplied
        string title = string.IsNullOrWhiteSpace(a.Title) ? "untitled" : a.Title;
        string decision = string.IsNullOrWhiteSpace(a.Decision) ? "" : a.Decision;
        string status = string.IsNullOrWhiteSpace(a.Status) ? "accepted" : a.Status;
        string body = a.Body ?? decision;
        var ts = string.IsNullOrWhiteSpace(a.CreatedAt)
            ? DateTime.UtcNow.ToString("o")
            : a.CreatedAt;

        int affected = Execute(
            """
            UPDATE graph_adr
            SET decision = $dec, status = $st, body = $b, created_at = $c
            WHERE project = $p AND title = $t
            """,
            ps =>
            {
                P(ps, "$dec", decision);
                P(ps, "$st", status);
                P(ps, "$b", body);
                P(ps, "$c", ts);
                P(ps, "$p", a.Project);
                P(ps, "$t", title);
            });

        if (affected == 0)
        {
            Execute(
                """
                INSERT INTO graph_adr (project, title, decision, status, body, created_at)
                VALUES ($p, $t, $dec, $st, $b, $c)
                """,
                ps =>
                {
                    P(ps, "$p", a.Project);
                    P(ps, "$t", title);
                    P(ps, "$dec", decision);
                    P(ps, "$st", status);
                    P(ps, "$b", body);
                    P(ps, "$c", ts);
                });
        }
    }

    public AdrRecord? GetAdr(string project, string sectionOrTitle)
    {
        var list = Query(
            """
            SELECT id, project, title, decision, status, body, created_at
            FROM graph_adr
            WHERE project = $p AND title = $t
            LIMIT 1
            """,
            MapAdr,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$t", sectionOrTitle);
            });
        return list.Count > 0 ? list[0] : null;
    }

    public List<AdrRecord> GetAdrs(string project)
        => Query(
            """
            SELECT id, project, title, decision, status, body, created_at
            FROM graph_adr
            WHERE project = $p
            ORDER BY created_at DESC, title
            """,
            MapAdr,
            ps => P(ps, "$p", project));

    // ── Step 4 native surface for graph_adr tool ─────────────────────────────

    /// <summary>List recent ADRs (newest first). Used by action=list.</summary>
    public List<AdrRecord> ListAdrs(int limit = 20)
        => Query(
            """
            SELECT id, project, title, decision, status, body, created_at
            FROM graph_adr
            ORDER BY created_at DESC
            LIMIT $lim
            """,
            MapAdr,
            ps => P(ps, "$lim", Math.Max(1, Math.Min(limit, 100))));

    /// <summary>Get full ADR by numeric id (incl. body). Used by action=get.</summary>
    public AdrRecord? GetAdrById(int id)
    {
        var list = Query(
            """
            SELECT id, project, title, decision, status, body, created_at
            FROM graph_adr
            WHERE id = $id
            LIMIT 1
            """,
            MapAdr,
            ps => P(ps, "$id", id));
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>
    /// Insert a new ADR (id auto). Used by action=add. Returns the new id.
    /// status should be one of: proposed | accepted | deprecated | superseded
    /// </summary>
    public int AddAdr(string project, string title, string decision, string status, string? body = null)
    {
        if (string.IsNullOrWhiteSpace(project)) project = "workspace";
        if (string.IsNullOrWhiteSpace(title)) title = "untitled";
        if (string.IsNullOrWhiteSpace(decision)) decision = "";
        status = (status ?? "proposed").ToLowerInvariant();
        if (status is not ("proposed" or "accepted" or "deprecated" or "superseded"))
            status = "proposed";

        var now = DateTime.UtcNow.ToString("o");

        Execute(
            """
            INSERT INTO graph_adr (project, title, decision, status, body, created_at)
            VALUES ($p, $t, $d, $s, $b, $c)
            """,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$t", title);
                P(ps, "$d", decision);
                P(ps, "$s", status);
                P(ps, "$b", body);
                P(ps, "$c", now);
            });

        var idObj = Scalar("SELECT last_insert_rowid()");
        return idObj is null ? 0 : Convert.ToInt32(idObj);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Read / search (graph_search foundation — step 2 tool will call this)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Primary query entry point for graph_search.
    /// - If <paramref name="query"/> supplied → FTS5 BM25 search (with camel-split processing on both index and query side).
    /// - Else falls back to structural filters.
    /// Structural boost (Method/Function > Route > Class > File) applied at query time.
    /// </summary>
    public List<CodeNode> SearchNodes(
        string? query = null,
        string? namePattern = null,
        string? label = null,
        string? filePattern = null,
        int? minDegree = null,
        int limit = 50,
        string? project = null)
    {
        limit = Math.Max(1, Math.Min(limit, 500));

        if (!string.IsNullOrWhiteSpace(query))
        {
            return SearchViaFts(query, namePattern, label, filePattern, minDegree, limit, project);
        }

        return SearchByFilters(namePattern, label, filePattern, minDegree, limit, project);
    }

    private List<CodeNode> SearchViaFts(
        string query,
        string? namePattern,
        string? label,
        string? filePattern,
        int? minDegree,
        int limit,
        string? project)
    {
        var ftsQ = BuildFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQ))
            return SearchByFilters(namePattern, label, filePattern, minDegree, limit, project);

        // Over-fetch a bit for post-sort by structural boost
        int fetch = Math.Min(2000, limit * 4);

        var sql = """
            SELECT n.id, n.project, n.label, n.name, n.qualified_name, n.file_path,
                   n.line_start, n.line_end, n.cyclomatic, n.cognitive, n.loop_depth,
                   n.transitive_loop_depth, n.linear_scan_in_loop, n.is_recursive, n.degree,
                   bm25(graph_fts) AS rank
            FROM graph_fts
            JOIN graph_nodes n ON n.id = graph_fts.rowid
            WHERE graph_fts MATCH $q
              AND ($proj IS NULL OR n.project = $proj)
              AND ($lab IS NULL OR n.label = $lab)
            ORDER BY rank
            LIMIT $lim
            """;

        var scored = new List<(CodeNode Node, double Rank)>();
        Query(sql,
            r =>
            {
                var node = MapNode(r);
                var rank = GetReal(r, "rank") ?? 0.0;
                scored.Add((node, rank));
                return node; // ignored
            },
            ps =>
            {
                P(ps, "$q", ftsQ);
                P(ps, "$proj", project);
                P(ps, "$lab", label);
                P(ps, "$lim", fetch);
            });

        // Apply remaining filters + structural boost + take
        var boosted = scored
            .Select(x => (x.Node, Eff: x.Rank - (LabelBoost(x.Node.Label) * 0.05)))
            .Where(x =>
                (namePattern is null || MatchesLike(x.Node.QualifiedName, namePattern)) &&
                (filePattern is null || MatchesLike(x.Node.FilePath, filePattern)) &&
                (!minDegree.HasValue || x.Node.Degree >= minDegree.Value))
            .OrderBy(x => x.Eff)
            .Take(limit)
            .Select(x => x.Node)
            .ToList();

        return boosted;
    }

    private List<CodeNode> SearchByFilters(
        string? namePattern,
        string? label,
        string? filePattern,
        int? minDegree,
        int limit,
        string? project)
    {
        var sql = """
            SELECT id, project, label, name, qualified_name, file_path,
                   line_start, line_end, cyclomatic, cognitive, loop_depth,
                   transitive_loop_depth, linear_scan_in_loop, is_recursive, degree
            FROM graph_nodes
            WHERE ($proj IS NULL OR project = $proj)
              AND ($lab IS NULL OR label = $lab)
            ORDER BY degree DESC, qualified_name
            LIMIT $lim
            """;

        var rows = Query(sql,
            MapNode,
            ps =>
            {
                P(ps, "$proj", project);
                P(ps, "$lab", label);
                P(ps, "$lim", limit);
            });

        // Post-filter for patterns + minDegree (LIKE semantics for patterns)
        return rows
            .Where(n =>
                (namePattern is null || MatchesLike(n.QualifiedName, namePattern) || MatchesLike(n.Name, namePattern)) &&
                (filePattern is null || MatchesLike(n.FilePath, filePattern)) &&
                (!minDegree.HasValue || n.Degree >= minDegree.Value))
            .Take(limit)
            .ToList();
    }

    public CodeNode? GetNode(string? project, string qualifiedName)
    {
        var list = Query(
            """
            SELECT id, project, label, name, qualified_name, file_path,
                   line_start, line_end, cyclomatic, cognitive, loop_depth,
                   transitive_loop_depth, linear_scan_in_loop, is_recursive, degree
            FROM graph_nodes
            WHERE ($p IS NULL OR project = $p) AND qualified_name = $q
            LIMIT 1
            """,
            MapNode,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$q", qualifiedName);
            });
        return list.Count > 0 ? list[0] : null;
    }

    public CodeNode? GetNodeById(string? project, int id)
    {
        var list = Query(
            """
            SELECT id, project, label, name, qualified_name, file_path,
                   line_start, line_end, cyclomatic, cognitive, loop_depth,
                   transitive_loop_depth, linear_scan_in_loop, is_recursive, degree
            FROM graph_nodes
            WHERE ($p IS NULL OR project = $p) AND id = $id
            LIMIT 1
            """,
            MapNode,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$id", id);
            });
        return list.Count > 0 ? list[0] : null;
    }

    public List<CodeNode> GetNodesForProject(string? project = null, string? label = null)
        => Query(
            """
            SELECT id, project, label, name, qualified_name, file_path,
                   line_start, line_end, cyclomatic, cognitive, loop_depth,
                   transitive_loop_depth, linear_scan_in_loop, is_recursive, degree
            FROM graph_nodes
            WHERE ($proj IS NULL OR project = $proj) AND ($lab IS NULL OR label = $lab)
            ORDER BY line_start, qualified_name
            """,
            MapNode,
            ps =>
            {
                P(ps, "$proj", project);
                P(ps, "$lab", label);
            });

    /// <summary>
    /// Lookup nodes by exact stored file_path (for detect_changes impact).
    /// Tries exact match; falls back to alternate path separators for robustness (git vs Roslyn paths).
    /// </summary>
    public List<CodeNode> GetNodesByFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return new List<CodeNode>();

        string sql = """
            SELECT id, project, label, name, qualified_name, file_path,
                   line_start, line_end, cyclomatic, cognitive, loop_depth,
                   transitive_loop_depth, linear_scan_in_loop, is_recursive, degree
            FROM graph_nodes
            WHERE file_path = $f
            LIMIT 200
            """;

        var res = Query(sql, MapNode, ps => P(ps, "$f", filePath));
        if (res.Count > 0) return res;

        // Fallback: flip separators
        var alt = filePath.Replace('\\', '/');
        if (alt != filePath)
        {
            res = Query(sql, MapNode, ps => P(ps, "$f", alt));
            if (res.Count > 0) return res;
        }
        alt = filePath.Replace('/', '\\');
        if (alt != filePath)
        {
            res = Query(sql, MapNode, ps => P(ps, "$f", alt));
            if (res.Count > 0) return res;
        }

        // Last attempt: full path normalization
        try
        {
            var full = Path.GetFullPath(filePath);
            if (full != filePath && full != alt)
            {
                res = Query(sql, MapNode, ps => P(ps, "$f", full));
                if (res.Count > 0) return res;
            }
        }
        catch { }

        return res;
    }

    public List<CodeEdge> GetEdges(string? project, int? srcId = null, int? dstId = null, string? edgeType = null)
        => Query(
            """
            SELECT id, project, src_id, dst_id, edge_type
            FROM graph_edges
            WHERE ($p IS NULL OR project = $p)
              AND ($s IS NULL OR src_id = $s)
              AND ($d IS NULL OR dst_id = $d)
              AND ($e IS NULL OR edge_type = $e)
            ORDER BY src_id, id
            """,
            MapEdge,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$s", srcId);
                P(ps, "$d", dstId);
                P(ps, "$e", edgeType);
            });

    public int CountNodes(string? project = null)
    {
        var sql = project is null
            ? "SELECT COUNT(*) FROM graph_nodes"
            : "SELECT COUNT(*) FROM graph_nodes WHERE project = $p";
        var val = project is null
            ? Scalar(sql)
            : Scalar(sql, ps => P(ps, "$p", project));
        return Convert.ToInt32(val ?? 0);
    }

    public int CountEdges(string? project = null)
    {
        var sql = project is null
            ? "SELECT COUNT(*) FROM graph_edges"
            : "SELECT COUNT(*) FROM graph_edges WHERE project = $p";
        var val = project is null
            ? Scalar(sql)
            : Scalar(sql, ps => P(ps, "$p", project));
        return Convert.ToInt32(val ?? 0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Trace (for trace_path tool)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the call tree (or data-flow) of callers up to maxDepth (0 = just the node).
    /// Uses CALLS edges by default. Direction is "up" (callers).
    /// </summary>
    public IReadOnlyList<(CodeNode Node, int Depth)> TraceCallers(string project, string qualifiedName, int maxDepth = 3, string edgeType = "CALLS")
        => TraceDirection(project, qualifiedName, maxDepth, edgeType, callers: true);

    /// <summary>
    /// Returns the call tree (or data-flow) of callees down to maxDepth.
    /// </summary>
    public IReadOnlyList<(CodeNode Node, int Depth)> TraceCallees(string project, string qualifiedName, int maxDepth = 3, string edgeType = "CALLS")
        => TraceDirection(project, qualifiedName, maxDepth, edgeType, callers: false);

    private IReadOnlyList<(CodeNode Node, int Depth)> TraceDirection(string project, string qualifiedName, int maxDepth, string edgeType, bool callers)
    {
        if (maxDepth < 0) maxDepth = 0;
        var start = GetNode(project, qualifiedName);
        if (start == null || start.Id == null) return Array.Empty<(CodeNode, int)>();

        // Prefetch all nodes + relevant edges for the project to avoid N+1 connection opens per hop.
        var allNodes = GetNodesForProject(project);
        var qnToNode = allNodes.ToDictionary(n => n.QualifiedName, n => n, StringComparer.Ordinal);
        var idToQn = allNodes.Where(n => n.Id.HasValue).ToDictionary(n => n.Id!.Value, n => n.QualifiedName);

        var edges = GetEdges(project, edgeType: edgeType);
        // Build adj lists: for callers (reverse): dst -> list<srcQ>
        // for callees: src -> list<dstQ>
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in edges)
        {
            string? sQn = idToQn.TryGetValue(e.SrcId, out var sq) ? sq : null;
            string? dQn = idToQn.TryGetValue(e.DstId, out var dq) ? dq : null;
            if (sQn == null || dQn == null) continue;

            var key = callers ? dQn : sQn;
            var val = callers ? sQn : dQn;
            if (!adj.TryGetValue(key, out var lst))
            {
                lst = new List<string>();
                adj[key] = lst;
            }
            lst.Add(val);
        }

        var result = new List<(CodeNode Node, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Walk(string qn, int d)
        {
            if (d > maxDepth) return;
            if (!visited.Add(qn)) return;
            if (qnToNode.TryGetValue(qn, out var node))
                result.Add((node, d));

            if (adj.TryGetValue(qn, out var neigh))
            {
                foreach (var nb in neigh)
                    Walk(nb, d + 1);
            }
        }

        Walk(start.QualifiedName, 0);
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Complexity updates (post-index)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Partial update for complexity columns (and is_recursive). Null values leave the column unchanged (via COALESCE).
    /// Used by RoslynIndexer after ReplaceGraph + by the transitive pass.
    /// </summary>
    public void UpdateComplexity(string project, string qualifiedName,
        int? cyclomatic = null,
        int? cognitive = null,
        int? loopDepth = null,
        int? linearScanInLoop = null,
        bool? isRecursive = null,
        int? transitiveLoopDepth = null)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(qualifiedName)) return;

        Execute(
            """
            UPDATE graph_nodes
            SET cyclomatic            = COALESCE($cyc, cyclomatic),
                cognitive             = COALESCE($cog, cognitive),
                loop_depth            = COALESCE($ld,  loop_depth),
                linear_scan_in_loop   = COALESCE($lin, linear_scan_in_loop),
                is_recursive          = COALESCE($rec, is_recursive),
                transitive_loop_depth = COALESCE($tld, transitive_loop_depth)
            WHERE project = $p AND qualified_name = $q
            """,
            ps =>
            {
                P(ps, "$p", project);
                P(ps, "$q", qualifiedName);
                P(ps, "$cyc", cyclomatic);
                P(ps, "$cog", cognitive);
                P(ps, "$ld", loopDepth);
                P(ps, "$lin", linearScanInLoop);
                P(ps, "$rec", isRecursive.HasValue ? (isRecursive.Value ? 1 : 0) : (object?)null);
                P(ps, "$tld", transitiveLoopDepth);
            });
    }

    /// <summary>
    /// Second pass: for every node compute transitive_loop_depth = max(loop_depth along reachable CALLS callees, including self).
    /// Called after the full graph (nodes + CALLS edges) exists.
    /// </summary>
    public void ComputeTransitiveLoopDepths(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) return;

        InTransaction((conn, tx) =>
        {
            // Load loop info for the project
            var idToBase = new Dictionary<int, int>();
            using (var cmd = CreateCmd(conn, tx,
                "SELECT id, COALESCE(loop_depth, 0) FROM graph_nodes WHERE project = $p"))
            {
                P(cmd.Parameters, "$p", project);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int id = r.GetInt32(0);
                    int ld = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                    idToBase[id] = ld;
                }
            }

            // Build callee map for CALLS only
            var callees = new Dictionary<int, List<int>>();
            using (var cmd = CreateCmd(conn, tx,
                """
                SELECT src_id, dst_id FROM graph_edges
                WHERE project = $p AND edge_type = 'CALLS'
                """))
            {
                P(cmd.Parameters, "$p", project);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    int s = r.GetInt32(0);
                    int d = r.GetInt32(1);
                    if (!callees.TryGetValue(s, out var list))
                    {
                        list = new List<int>();
                        callees[s] = list;
                    }
                    list.Add(d);
                }
            }

            // Memoized max over call subgraph
            var memo = new Dictionary<int, int>();
            int MaxAlongCalls(int id)
            {
                if (memo.TryGetValue(id, out var cached)) return cached;

                int res = idToBase.TryGetValue(id, out var b) ? b : 0;
                if (callees.TryGetValue(id, out var kids))
                {
                    foreach (var k in kids)
                        res = Math.Max(res, MaxAlongCalls(k));
                }
                memo[id] = res;
                return res;
            }

            // Apply updates
            foreach (var id in idToBase.Keys)
            {
                int t = MaxAlongCalls(id);
                ExecuteOn(tx,
                    "UPDATE graph_nodes SET transitive_loop_depth = $t WHERE id = $id",
                    ps =>
                    {
                        P(ps, "$t", t);
                        P(ps, "$id", id);
                    });
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void BindNode(SqliteParameterCollection ps, CodeNode n)
    {
        P(ps, "$project", n.Project);
        P(ps, "$label", n.Label);
        P(ps, "$name", n.Name);
        P(ps, "$qname", n.QualifiedName);
        P(ps, "$fpath", n.FilePath);
        P(ps, "$lstart", n.LineStart);
        P(ps, "$lend", n.LineEnd);
        P(ps, "$cyc", n.Cyclomatic);
        P(ps, "$cog", n.Cognitive);
        P(ps, "$ld", n.LoopDepth);
        P(ps, "$tld", n.TransitiveLoopDepth);
        P(ps, "$lin", n.LinearScanInLoop);
        P(ps, "$rec", n.IsRecursive ? 1 : 0);
    }

    private void CorrectFtsForId(SqliteTransaction tx, int rowid, string name, string qname, string filePath)
    {
        var splitN = CamelSplit(name);
        var splitQ = CamelSplit(qname);

        // Remove whatever the node trigger(s) wrote (raw text)
        ExecuteOn(tx,
            "INSERT INTO graph_fts(graph_fts, rowid, name, qualified_name, file_path) VALUES ('delete', $rid, $n, $q, $f);",
            ps =>
            {
                P(ps, "$rid", rowid);
                P(ps, "$n", name);
                P(ps, "$q", qname);
                P(ps, "$f", filePath);
            });

        // Insert the split-token version that BM25 will actually use
        ExecuteOn(tx,
            "INSERT INTO graph_fts(rowid, name, qualified_name, file_path) VALUES ($rid, $n, $q, $f);",
            ps =>
            {
                P(ps, "$rid", rowid);
                P(ps, "$n", splitN);
                P(ps, "$q", splitQ);
                P(ps, "$f", filePath);
            });
    }

    private void RecomputeProjectDegrees(SqliteTransaction tx, string project)
    {
        ExecuteOn(tx,
            """
            UPDATE graph_nodes
            SET degree = (
                SELECT COUNT(*) FROM graph_edges e
                WHERE (e.src_id = graph_nodes.id OR e.dst_id = graph_nodes.id)
                  AND e.project = graph_nodes.project
            )
            WHERE project = $p
            """,
            ps => P(ps, "$p", project));
    }

    private static CodeNode MapNode(SqliteDataReader r) => new(
        Id: GetInt(r, "id"),
        Project: GetStr(r, "project") ?? "",
        Label: GetStr(r, "label") ?? "",
        Name: GetStr(r, "name") ?? "",
        QualifiedName: GetStr(r, "qualified_name") ?? "",
        FilePath: GetStr(r, "file_path") ?? "",
        LineStart: GetInt(r, "line_start") ?? 0,
        LineEnd: GetInt(r, "line_end") ?? 0,
        Cyclomatic: GetInt(r, "cyclomatic"),
        Cognitive: GetInt(r, "cognitive"),
        LoopDepth: GetInt(r, "loop_depth"),
        TransitiveLoopDepth: GetInt(r, "transitive_loop_depth"),
        LinearScanInLoop: GetInt(r, "linear_scan_in_loop"),
        IsRecursive: (GetInt(r, "is_recursive") ?? 0) != 0,
        Degree: GetInt(r, "degree") ?? 0);

    private static CodeEdge MapEdge(SqliteDataReader r) => new(
        Id: GetInt(r, "id"),
        Project: GetStr(r, "project") ?? "",
        SrcId: GetInt(r, "src_id") ?? 0,
        DstId: GetInt(r, "dst_id") ?? 0,
        EdgeType: GetStr(r, "edge_type") ?? "");

    private static AdrRecord MapAdr(SqliteDataReader r) => new(
        Id: GetInt(r, "id"),
        Project: GetStr(r, "project") ?? "",
        Title: GetStr(r, "title") ?? "",
        Decision: GetStr(r, "decision") ?? "",
        Status: GetStr(r, "status") ?? "",
        Body: GetStr(r, "body"),
        CreatedAt: GetStr(r, "created_at") ?? GetStr(r, "updated_at") ?? "");

    private static int LabelBoost(string label) => label switch
    {
        "Function" or "Method" => 120,
        "Route" => 90,
        "Class" or "Interface" => 60,
        "File" => 20,
        _ => 0
    };

    private static bool MatchesLike(string? value, string pattern)
    {
        if (value is null) return false;
        if (string.IsNullOrEmpty(pattern)) return true;
        // Support simple * and ? wildcards by converting to SQL LIKE wildcards
        var like = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, like, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Split camelCase / Pascal / snake into space-separated words for FTS indexing + querying.
    // "updateCloudClient" → "update Cloud Client"
    // "ISymbol" / "HttpGet" / "SQLStore" handled reasonably.
    private static string CamelSplit(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;

        s = s.Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
        // lower/digit followed by Upper
        s = Regex.Replace(s, "([a-z0-9])([A-Z])", "$1 $2");
        // acronym followed by Title (XMLHttp → XML Http)
        s = Regex.Replace(s, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        return s;
    }

    private static string BuildFtsQuery(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var parts = raw.Split(new[] { ' ', '\t', '.', '_', '-', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokens = new List<string>();
        foreach (var p in parts)
        {
            var split = CamelSplit(p);
            foreach (var t in split.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(t)) tokens.Add(t);
            }
        }

        // Dedup case-insens, preserve first occurrence
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniq = new List<string>();
        foreach (var t in tokens)
        {
            if (seen.Add(t)) uniq.Add(t);
        }

        if (uniq.Count == 0) return "";
        // Quote each token and AND them so "cloud client" requires both; prevents most FTS syntax issues.
        return string.Join(" AND ", uniq.Select(t => $"\"{t.Replace("\"", "")}\""));
    }
}
