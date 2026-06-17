// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.CodeGraph;
using OrchestratorIDE.Services.CodeGraph.Data;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.Tools;

/// <summary>
/// Code knowledge graph tools (v1 step 2+3).
/// Follows the exact registration shape of SearchTools.Register(registry, workspaceRoot).
/// All operations are read-only and require no approval.
/// </summary>
public static class GraphTools
{
    public static void Register(ToolRegistry registry, string workspaceRoot)
    {
        // ── graph_search ───────────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "graph_search",
            Description = "Search the code knowledge graph (BM25 full-text over names + qualified names with camelCase tokenization, plus structural boosting). Returns a plain-text ranked list. Read-only.",
            Parameters = new()
            {
                ["query"]        = new("string", "Search terms (natural language or identifier fragments). BM25 over graph_fts."),
                ["label"]        = new("string", "Optional label filter: Method | Function | Class | Interface | Route | File."),
                ["file_pattern"] = new("string", "Optional file path substring or wildcard filter."),
                ["min_degree"]   = new("number", "Optional minimum combined edge degree (in+out)."),
                ["limit"]        = new("number", "Maximum results to return. Default 50, max 200."),
            },
            Required = [], // query is the common case but broad structural queries are valid with no query
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                var query = GetString(args, "query");
                var label = GetString(args, "label");
                var filePattern = GetString(args, "file_pattern");
                int? minDegree = GetInt(args, "min_degree");
                int limit = GetInt(args, "limit") ?? 50;
                limit = System.Math.Clamp(limit, 1, 200);

                // Also honor legacy/alt name for query surface
                if (string.IsNullOrWhiteSpace(query))
                    query = GetString(args, "name_pattern");

                try
                {
                    using var store = new SqliteStore(workspaceRoot);
                    // Idempotent and cheap when already migrated; ensures graph tables exist.
                    store.Initialize();
                    var repo = new GraphRepository(store);

                    var hits = repo.SearchNodes(
                        query: query,
                        namePattern: null,
                        label: label,
                        filePattern: filePattern,
                        minDegree: minDegree,
                        limit: limit,
                        project: null);

                    if (hits == null || hits.Count == 0)
                        return Task.FromResult("[No matches]");

                    var sb = new StringBuilder();
                    sb.AppendLine($"[graph_search] {hits.Count} result(s)");

                    foreach (var n in hits)
                    {
                        string displayFile = n.FilePath;
                        if (Path.IsPathRooted(displayFile))
                        {
                            try { displayFile = Path.GetRelativePath(workspaceRoot, displayFile); }
                            catch { displayFile = Path.GetFileName(displayFile); }
                        }
                        sb.AppendLine($"{n.QualifiedName} [{n.Label}] {displayFile}:{n.LineStart} (degree={n.Degree})");
                    }

                    return Task.FromResult(sb.ToString().TrimEnd());
                }
                catch (System.Exception ex)
                {
                    return Task.FromResult($"[ERROR] {ex.Message}");
                }
            }
        });

        // ── trace_path ─────────────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "trace_path",
            Description = "Trace caller/callee paths in the code graph to a given depth. Uses structural CALLS edges (or DATA_FLOWS when available). Returns a plain-text indented tree. Read-only.",
            Parameters = new()
            {
                ["qualified_name"] = new("string", "Fully qualified name of the starting symbol (from graph_search)."),
                ["direction"]      = new("string", "both | callers | callees. Default 'both'."),
                ["depth"]          = new("number", "Max depth to traverse. Default 3, max 8."),
                ["mode"]           = new("string", "calls | data_flow. 'calls' uses CALLS edges; 'data_flow' prefers DATA_FLOWS when present."),
            },
            Required = ["qualified_name"],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                var qname = GetString(args, "qualified_name");
                if (string.IsNullOrWhiteSpace(qname))
                    return Task.FromResult("[ERROR] qualified_name is required");

                string dir = (GetString(args, "direction") ?? "both").ToLowerInvariant();
                int depth = GetInt(args, "depth") ?? 3;
                depth = System.Math.Clamp(depth, 0, 8);
                string mode = (GetString(args, "mode") ?? "calls").ToLowerInvariant();
                string edge = mode == "data_flow" ? "DATA_FLOWS" : "CALLS";

                try
                {
                    using var store = new SqliteStore(workspaceRoot);
                    store.Initialize();
                    var repo = new GraphRepository(store);

                    // Need a project? Use null (aggregates) or try to resolve via GetNode scan.
                    // For simplicity we scan without project filter by trying common patterns or null.
                    string? project = null;
                    var start = repo.GetNode(project, qname);
                    if (start == null)
                    {
                        // Try to find any project that has it
                        // (cheap: search with structural for this qname)
                        var candidates = repo.SearchNodes(namePattern: qname, limit: 1);
                        if (candidates.Count > 0)
                        {
                            start = candidates[0];
                            project = start.Project;
                        }
                    }
                    else
                    {
                        project = start.Project;
                    }

                    if (start == null)
                        return Task.FromResult($"[trace_path] No node found for '{qname}'");

                    project ??= start.Project;

                    var sb = new StringBuilder();
                    sb.AppendLine($"[trace_path] {qname} (direction={dir}, depth={depth}, mode={mode})");

                    bool doCallers = dir == "both" || dir == "callers";
                    bool doCallees = dir == "both" || dir == "callees";

                    if (doCallers)
                    {
                        var callers = repo.TraceCallers(project, qname, depth, edge);
                        sb.AppendLine("Callers:");
                        AppendTree(sb, callers, workspaceRoot);
                    }
                    if (doCallees)
                    {
                        var callees = repo.TraceCallees(project, qname, depth, edge);
                        sb.AppendLine("Callees:");
                        AppendTree(sb, callees, workspaceRoot);
                    }

                    return Task.FromResult(sb.ToString().TrimEnd());
                }
                catch (System.Exception ex)
                {
                    return Task.FromResult($"[ERROR] {ex.Message}");
                }
            }
        });

        // ── get_architecture ───────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "get_architecture",
            Description = "Returns a high-level architecture snapshot from the graph: namespaces, top-degree hub nodes, routes, and aggregate counts. Read-only.",
            Parameters = new() { },
            Required = [],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                try
                {
                    using var store = new SqliteStore(workspaceRoot);
                    store.Initialize();
                    var repo = new GraphRepository(store);

                    // Aggregate over everything (project=null). In multi-project workspaces this summarizes the union.
                    var nodes = repo.GetNodesForProject(project: null);
                    var nodeCount = repo.CountNodes();
                    var edgeCount = repo.CountEdges();
                    int fileCount = nodes.Select(n => n.FilePath).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).Count();

                    // Namespaces / packages (take prefix before last segment of qname)
                    var namespaces = nodes
                        .Select(n => ExtractNamespace(n.QualifiedName))
                        .Where(ns => !string.IsNullOrWhiteSpace(ns))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(s => s, StringComparer.Ordinal)
                        .Take(50)
                        .ToList();

                    // Top hubs by degree
                    var hubs = nodes
                        .OrderByDescending(n => n.Degree)
                        .ThenBy(n => n.QualifiedName, StringComparer.Ordinal)
                        .Take(10)
                        .ToList();

                    // Route nodes (when ROUTES_TO labeling is present)
                    var routes = nodes
                        .Where(n => string.Equals(n.Label, "Route", StringComparison.OrdinalIgnoreCase))
                        .Select(n => n.QualifiedName)
                        .OrderBy(s => s, StringComparer.Ordinal)
                        .ToList();

                    var sb = new StringBuilder();
                    sb.AppendLine($"[get_architecture] files={fileCount} nodes={nodeCount} edges={edgeCount}");

                    sb.AppendLine("Namespaces:");
                    if (namespaces.Count == 0) sb.AppendLine("  (none)");
                    foreach (var ns in namespaces)
                        sb.AppendLine("  " + ns);

                    sb.AppendLine("Top hubs (by degree):");
                    if (hubs.Count == 0) sb.AppendLine("  (none)");
                    foreach (var h in hubs)
                        sb.AppendLine($"  {h.QualifiedName} [{h.Label}] degree={h.Degree} {ShortFile(h.FilePath, workspaceRoot)}:{h.LineStart}");

                    sb.AppendLine("Routes:");
                    if (routes.Count == 0) sb.AppendLine("  (none)");
                    foreach (var r in routes)
                        sb.AppendLine("  " + r);

                    return Task.FromResult(sb.ToString().TrimEnd());
                }
                catch (System.Exception ex)
                {
                    return Task.FromResult($"[ERROR] {ex.Message}");
                }
            }
        });

        // ── detect_changes (step 4) ────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "detect_changes",
            Description = "Use git (LibGit2Sharp) to find .cs files changed since base_ref (default HEAD~1), look up graph nodes for those files, and report impact sorted by degree. Includes blast-radius count of unique callers within depth 2. Read-only.",
            Parameters = new()
            {
                ["base_ref"]    = new("string", "Git ref or commit to diff against (e.g. HEAD~1, abc123). Default HEAD~1."),
                ["path_filter"] = new("string", "Optional glob or substring to filter changed file paths (e.g. src/* or *Service.cs)."),
            },
            Required = [],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                string baseRef = GetString(args, "base_ref") ?? "HEAD~1";
                string? pathFilter = GetString(args, "path_filter");

                try
                {
                    if (!Repository.IsValid(workspaceRoot))
                        return Task.FromResult("[detect_changes] not a git repository");

                    using var git = new Repository(workspaceRoot);
                    var workDir = git.Info.WorkingDirectory ?? workspaceRoot;

                    // Resolve base tree (graceful for shallow/first commit: fall back to HEAD)
                    Tree? baseTree = null;
                    try
                    {
                        var baseCommit = git.Lookup<Commit>(baseRef);
                        baseTree = baseCommit?.Tree;
                    }
                    catch { /* not found */ }

                    if (baseTree == null)
                    {
                        baseTree = git.Head?.Tip?.Tree;
                    }
                    if (baseTree == null)
                        return Task.FromResult("[detect_changes] no commits or empty repository");

                    var changes = git.Diff.Compare<TreeChanges>(baseTree, DiffTargets.WorkingDirectory);

                    var changedFilePaths = new List<string>();
                    foreach (var ch in changes)
                    {
                        // Focus on current path for the file (renames use Path)
                        string p = ch.Path ?? ch.OldPath ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(p)) continue;
                        if (!p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!PathMatches(p, pathFilter)) continue;

                        try
                        {
                            string full = Path.GetFullPath(Path.Combine(workDir, p));
                            if (!changedFilePaths.Contains(full, StringComparer.OrdinalIgnoreCase))
                                changedFilePaths.Add(full);
                        }
                        catch
                        {
                            if (!changedFilePaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                                changedFilePaths.Add(p);
                        }
                    }

                    using var store = new SqliteStore(workspaceRoot);
                    store.Initialize();
                    var repo = new GraphRepository(store);

                    var changedNodes = new List<CodeNode>();
                    foreach (var fp in changedFilePaths)
                    {
                        var nodes = repo.GetNodesByFile(fp);
                        changedNodes.AddRange(nodes);
                    }

                    // Dedup + sort by degree desc
                    var impact = changedNodes
                        .GroupBy(n => n.QualifiedName, StringComparer.Ordinal)
                        .Select(g => g.First())
                        .OrderByDescending(n => n.Degree)
                        .ThenBy(n => n.QualifiedName, StringComparer.Ordinal)
                        .ToList();

                    // Blast radius: unique nodes reachable via TraceCallers depth<=2 (callers direction)
                    var blast = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var n in impact)
                    {
                        try
                        {
                            var callers = repo.TraceCallers(n.Project, n.QualifiedName, 2, "CALLS");
                            foreach (var (cn, d) in callers)
                            {
                                if (d <= 2) blast.Add(cn.QualifiedName);
                            }
                        }
                        catch { /* trace resilient */ }
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine($"[detect_changes] base_ref={baseRef} changed_files={changedFilePaths.Count} nodes={impact.Count} blast_radius={blast.Count}");

                    if (impact.Count == 0)
                    {
                        sb.AppendLine("(no matching graph nodes for changed .cs files)");
                    }
                    else
                    {
                        sb.AppendLine("Changed nodes (by degree desc):");
                        foreach (var n in impact)
                        {
                            string f = ShortFile(n.FilePath, workspaceRoot);
                            sb.AppendLine($"  {n.QualifiedName} [{n.Label}] degree={n.Degree} {f}:{n.LineStart}");
                        }
                    }

                    sb.AppendLine($"Blast radius (unique callers within depth 2): {blast.Count}");
                    return Task.FromResult(sb.ToString().TrimEnd());
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"[ERROR] {ex.Message}");
                }
            }
        });

        // ── graph_adr (step 4) ───────────────────────────────────────────────
        registry.Register(new ToolDefinition
        {
            Name = "graph_adr",
            Description = "Architecture Decision Records store. action=list|get|add. list: recent 20. get: by id (full incl body). add: requires title+decision+status (proposed|accepted|deprecated|superseded); writes to DB. status is required for add.",
            Parameters = new()
            {
                ["action"]   = new("string", "list | get | add"),
                ["id"]       = new("number", "For get: ADR id"),
                ["title"]    = new("string", "For add: short title"),
                ["decision"] = new("string", "For add: the decision text"),
                ["status"]   = new("string", "For add: proposed | accepted | deprecated | superseded"),
                ["body"]     = new("string", "For add: optional additional body/details"),
            },
            Required = ["action"],
            RequiresApproval = true,  // add writes; list/get are reads but tool is write-capable
            Handler = (args, ct) =>
            {
                string action = (GetString(args, "action") ?? "").Trim().ToLowerInvariant();
                int? id = GetInt(args, "id");
                string? title = GetString(args, "title");
                string? decision = GetString(args, "decision");
                string? status = GetString(args, "status");
                string? body = GetString(args, "body");

                try
                {
                    using var store = new SqliteStore(workspaceRoot);
                    store.Initialize();
                    var repo = new GraphRepository(store);

                    if (action == "list")
                    {
                        var list = repo.ListAdrs(20);
                        if (list.Count == 0)
                            return Task.FromResult("[graph_adr list]\n(no ADRs yet)");

                        var sb = new StringBuilder();
                        sb.AppendLine($"[graph_adr list] {list.Count} record(s)");
                        for (int i = 0; i < list.Count; i++)
                        {
                            var a = list[i];
                            string when = string.IsNullOrWhiteSpace(a.CreatedAt) ? "" : a.CreatedAt.Substring(0, Math.Min(10, a.CreatedAt.Length));
                            sb.AppendLine($"{i + 1}. #{a.Id} [{a.Status}] {a.Title} ({when})");
                            // one-line decision preview
                            string preview = (a.Decision ?? "").Replace('\n', ' ').Trim();
                            if (preview.Length > 80) preview = preview.Substring(0, 77) + "...";
                            if (!string.IsNullOrWhiteSpace(preview)) sb.AppendLine($"    {preview}");
                        }
                        return Task.FromResult(sb.ToString().TrimEnd());
                    }

                    if (action == "get")
                    {
                        if (id == null || id <= 0)
                            return Task.FromResult("[ERROR] id (number) is required for get");

                        var a = repo.GetAdrById(id.Value);
                        if (a == null)
                            return Task.FromResult($"[graph_adr get] no ADR with id={id}");

                        var sb = new StringBuilder();
                        sb.AppendLine($"[graph_adr #{a.Id}] project={a.Project} status={a.Status} created={a.CreatedAt}");
                        sb.AppendLine($"title: {a.Title}");
                        sb.AppendLine("decision:");
                        sb.AppendLine(a.Decision ?? "");
                        if (!string.IsNullOrWhiteSpace(a.Body) && a.Body != a.Decision)
                        {
                            sb.AppendLine("body:");
                            sb.AppendLine(a.Body);
                        }
                        return Task.FromResult(sb.ToString().TrimEnd());
                    }

                    if (action == "add")
                    {
                        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(decision))
                            return Task.FromResult("[ERROR] title and decision are required for add");

                        string st = (status ?? "proposed").Trim().ToLowerInvariant();
                        if (st is not ("proposed" or "accepted" or "deprecated" or "superseded"))
                            st = "proposed";

                        string projectKey = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        if (string.IsNullOrWhiteSpace(projectKey)) projectKey = "workspace";

                        int newId = repo.AddAdr(projectKey, title, decision, st, body);
                        return Task.FromResult($"[graph_adr add] OK id={newId} status={st} title=\"{title}\"");
                    }

                    return Task.FromResult($"[ERROR] unknown action '{action}'. Use list | get | add.");
                }
                catch (Exception ex)
                {
                    return Task.FromResult($"[ERROR] {ex.Message}");
                }
            }
        });
    }

    private static string ExtractNamespace(string qn)
    {
        if (string.IsNullOrWhiteSpace(qn)) return "";
        int dot = qn.LastIndexOf('.');
        if (dot <= 0) return qn;
        return qn.Substring(0, dot);
    }

    private static string ShortFile(string filePath, string root)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return "";
        try
        {
            if (Path.IsPathRooted(filePath))
                return Path.GetRelativePath(root, filePath);
            return filePath;
        }
        catch { return System.IO.Path.GetFileName(filePath); }
    }

    private static void AppendTree(StringBuilder sb, IReadOnlyList<(CodeNode Node, int Depth)> items, string root)
    {
        if (items.Count == 0)
        {
            sb.AppendLine("  (none)");
            return;
        }
        // items[0] is the root at depth 0
        for (int i = 0; i < items.Count; i++)
        {
            var (n, d) = items[i];
            string indent = new string(' ', 2 * (d + 1));
            string f = ShortFile(n.FilePath, root);
            sb.AppendLine($"{indent}- {n.QualifiedName} [{n.Label}] {f}:{n.LineStart}");
        }
    }

    private static string? GetString(System.Collections.Generic.Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var v) && v is not null)
        {
            var s = v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
    }

    private static int? GetInt(System.Collections.Generic.Dictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var v) && v is not null)
        {
            if (int.TryParse(v.ToString(), out var i)) return i;
        }
        return null;
    }

    // Simple glob/substring filter for changed paths (supports * and ? via regex, else contains)
    private static bool PathMatches(string path, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (!filter.Contains('*') && !filter.Contains('?'))
            return path.Contains(filter, StringComparison.OrdinalIgnoreCase);

        try
        {
            var like = "^" + Regex.Escape(filter).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(path, like, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch { return path.Contains(filter, StringComparison.OrdinalIgnoreCase); }
    }
}
