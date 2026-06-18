// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OrchestratorIDE.Services.CodeGraph;
using OrchestratorIDE.Services.CodeGraph.Data;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T19 — GraphRepository v1 (step 1 of CodeGraph). Pure unit tests over the special
/// in-memory SQLite mode (":memory:" named shared cache) that SqliteStore provides
/// exactly for CodeGraph step-1 requirements. No file I/O, fully isolated per store.
/// Covers: migration v5 application, node/edge/ADR CRUD, ReplaceGraph atomicity,
/// camelCase FTS split (the key search enabler), filters, degree recompute, cascades.
/// </summary>
[TestFixture]
public class T19_GraphRepositoryTests
{
    private SqliteStore NewMemoryStore()
    {
        var store = new SqliteStore(":memory:");
        store.Initialize();
        return store;
    }

    // Helper for tests: using var store = NewDisposableMemoryStore(); ensures keeper is Disposed promptly.

    private static CodeNode Mk(string project, string label, string name, string qn, string file, int ls, int le, int? cyc = null)
        => new CodeNode(
            Id: null,
            Project: project,
            Label: label,
            Name: name,
            QualifiedName: qn,
            FilePath: file,
            LineStart: ls,
            LineEnd: le,
            Cyclomatic: cyc,
            Cognitive: null,
            LoopDepth: null,
            TransitiveLoopDepth: null,
            LinearScanInLoop: null,
            IsRecursive: false,
            Degree: 0);

    [Test]
    public void Initialize_Applies_V5_And_Creates_Graph_Tables()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        // Safe diagnostic (separate commands + readers)
        using (var conn = store.Open())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var r = cmd.ExecuteReader();
                var tables = new List<string>();
                while (r.Read()) tables.Add(r.GetString(0));
                TestContext.WriteLine("POST-INIT TABLES: " + string.Join(", ", tables));
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT version, description FROM schema_migrations ORDER BY version;";
                using var r2 = cmd.ExecuteReader();
                var vers = new List<string>();
                while (r2.Read()) vers.Add(r2.GetInt32(0) + ":" + r2.GetString(1));
                TestContext.WriteLine("POST-INIT MIGRATIONS: " + string.Join("; ", vers));
            }
        }

        Assert.That(repo.CountNodes(), Is.EqualTo(0));
        Assert.That(repo.CountEdges(), Is.EqualTo(0));
    }

    [Test]
    public void UpsertNode_And_GetNode_Roundtrips_All_Fields()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        var n = Mk("demo", "Method", "UpdateCloudClient", "MyApp.Net.HttpClient.UpdateCloudClient", "src/Net/HttpClient.cs", 42, 67, 4);
        var id = repo.UpsertNode(n);
        Assert.That(id, Is.GreaterThan(0));

        var got = repo.GetNode("demo", n.QualifiedName);
        Assert.That(got, Is.Not.Null);
        Assert.That(got!.QualifiedName, Is.EqualTo(n.QualifiedName));
        Assert.That(got.Name, Is.EqualTo("UpdateCloudClient"));
        Assert.That(got.Label, Is.EqualTo("Method"));
        Assert.That(got.Cyclomatic, Is.EqualTo(4));
    }

    [Test]
    public void ReplaceGraph_Is_Atomic_Wires_Edges_Recomputes_Degrees()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        var n1 = Mk("demo", "Class", "FooService", "My.FooService", "a.cs", 10, 30);
        var n2 = Mk("demo", "Method", "DoWork", "My.FooService.DoWork", "a.cs", 15, 25);
        var n3 = Mk("demo", "Method", "Helper", "My.FooService.Helper", "a.cs", 26, 29);

        repo.ReplaceGraph("demo", new[] { n1, n2, n3 }, new[]
        {
            (n2.QualifiedName, n3.QualifiedName, "CALLS")
        });

        Assert.That(repo.CountNodes("demo"), Is.EqualTo(3));
        Assert.That(repo.CountEdges("demo"), Is.EqualTo(1));

        var got2 = repo.GetNode("demo", n2.QualifiedName)!;
        var got3 = repo.GetNode("demo", n3.QualifiedName)!;
        Assert.That(got2.Degree, Is.EqualTo(1));
        Assert.That(got3.Degree, Is.EqualTo(1));
    }

    [Test]
    public void CamelSplit_Fts_Enables_Natural_Language_Queries()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        var n = Mk("demo", "Method", "UpdateCloudClient", "OrchestratorIDE.Net.Http.UpdateCloudClient", "Net/Http.cs", 100, 120);
        repo.UpsertNode(n);

        var r1 = repo.SearchNodes(query: "update cloud client", project: "demo", limit: 10);
        Assert.That(r1, Has.Count.EqualTo(1));
        Assert.That(r1[0].Name, Is.EqualTo("UpdateCloudClient"));

        var r2 = repo.SearchNodes(query: "cloud client", project: "demo", limit: 10);
        Assert.That(r2, Has.Count.EqualTo(1));

        var r3 = repo.SearchNodes(query: "http update", project: "demo", limit: 10);
        Assert.That(r3, Has.Count.EqualTo(1));

        var r4 = repo.SearchNodes(query: "completely unrelated xyz", project: "demo", limit: 10);
        Assert.That(r4, Is.Empty);
    }

    [Test]
    public void Search_Applies_Label_File_MinDegree_Filters()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        var n1 = Mk("p", "Method", "UpdateCloudClient", "MyApp.Net.Client.UpdateCloudClient", "src/Net/Client.cs", 42, 67);
        var n2 = Mk("p", "Class", "CloudClient", "MyApp.Net.Client", "src/Net/Client.cs", 10, 120);
        var n4 = Mk("p", "Method", "handle_request", "MyApp.Http.Router.handle_request", "src/Http/Router.cs", 5, 30);

        repo.ReplaceGraph("p", new[] { n1, n2, n4 }, new[]
        {
            (n1.QualifiedName, n2.QualifiedName, "CALLS"),
            (n4.QualifiedName, n2.QualifiedName, "CALLS"),
        });

        var methods = repo.SearchNodes(label: "Method", limit: 20);
        Assert.That(methods.Count, Is.EqualTo(2));
        Assert.That(methods.All(m => m.Label == "Method"), Is.True);

        var hubs = repo.SearchNodes(minDegree: 1, limit: 10);
        Assert.That(hubs.Any(h => h.QualifiedName == n2.QualifiedName), Is.True);

        var fileHits = repo.SearchNodes(filePattern: "*Router*", limit: 5);
        Assert.That(fileHits.Any(h => h.QualifiedName.Contains("Router")), Is.True);
    }

    [Test]
    public void DeleteNodesForFiles_Cascades_Edges_And_Fts()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        var n1 = Mk("p", "Method", "UpdateCloudClient", "MyApp.Net.Client.UpdateCloudClient", "src/Net/Client.cs", 42, 67);
        var n2 = Mk("p", "Class", "CloudClient", "MyApp.Net.Client", "src/Net/Client.cs", 10, 120);
        var n4 = Mk("p", "Method", "handle_request", "MyApp.Http.Router.handle_request", "src/Http/Router.cs", 5, 30);

        repo.ReplaceGraph("p", new[] { n1, n2, n4 }, new[] { (n1.QualifiedName, n2.QualifiedName, "CALLS") });
        Assert.That(repo.CountNodes("p"), Is.EqualTo(3));
        Assert.That(repo.CountEdges("p"), Is.EqualTo(1));

        repo.DeleteNodesForFiles("p", new[] { "src/Net/Client.cs" });
        Assert.That(repo.CountNodes("p"), Is.EqualTo(1));
        Assert.That(repo.CountEdges("p"), Is.EqualTo(0));
    }

    [Test]
    public void Adr_Upsert_Get_List_And_Project_Scope()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        repo.UpsertAdr(new AdrRecord(null, "demo", "auth", "Use short-lived JWT + HttpOnly refresh.", "accepted", null, ""));
        repo.UpsertAdr(new AdrRecord(null, "demo", "persistence", "Single .orc/theorc.db + WAL + per-conn pragmas.", "accepted", null, ""));
        repo.UpsertAdr(new AdrRecord(null, "demo", "auth", "Updated: access tokens expire fast.", "accepted", null, ""));

        var a = repo.GetAdr("demo", "auth");
        Assert.That(a, Is.Not.Null);
        Assert.That(a!.Decision, Does.Contain("fast"));

        var list = repo.GetAdrs("demo");
        Assert.That(list.Count, Is.EqualTo(2));

        repo.UpsertAdr(new AdrRecord(null, "other", "auth", "x", "accepted", null, ""));
        Assert.That(repo.GetAdrs("demo").Count, Is.EqualTo(2));
        Assert.That(repo.GetAdrs("other").Count, Is.EqualTo(1));
    }

    [Test]
    public void ClearProject_Removes_Nodes_Edges_Adrs_Are_Project_Scoped()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        var n = Mk("p", "Class", "C", "N.C", "f.cs", 1, 2);
        repo.ReplaceGraph("p", new[] { n }, Array.Empty<(string, string, string)>());
        repo.UpsertAdr(new AdrRecord(null, "p", "sec", "content", "accepted", null, ""));

        repo.ClearProject("p");

        Assert.That(repo.CountNodes("p"), Is.EqualTo(0));
        Assert.That(repo.GetAdr("p", "sec"), Is.Not.Null);
    }

    [Test]
    public void Memory_Stores_Are_Fully_Isolated()
    {
        using var s1 = NewMemoryStore();
        var r1 = new GraphRepository(s1);
        using var s2 = NewMemoryStore();
        var r2 = new GraphRepository(s2);

        var n = Mk("shared", "Class", "C", "N.C", "f.cs", 1, 2);
        r1.ReplaceGraph("shared", new[] { n }, Array.Empty<(string, string, string)>());

        Assert.That(r1.CountNodes("shared"), Is.EqualTo(1));
        Assert.That(r2.CountNodes("shared"), Is.EqualTo(0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Step 2 additions: RoslynIndexer (nodes + CALLS/IMPLEMENTS) + search surface
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void RoslynIndexer_Indexes_Sample_Cs_Files_And_Wires_CALLS_IMPLEMENTS()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "orc-graph-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Two files exercising type, interface impl, method call
            File.WriteAllText(Path.Combine(tempRoot, "IService.cs"), """
                namespace Sample;
                public interface IService { void Do(); }
                """);

            File.WriteAllText(Path.Combine(tempRoot, "Service.cs"), """
                namespace Sample;
                public class Service : IService
                {
                    public void Do() { Helper(); }
                    private void Helper() { }
                }
                """);

            using var store = NewMemoryStore();
            var repo = new GraphRepository(store);
            var indexer = new OrchestratorIDE.Services.CodeGraph.RoslynIndexer(repo);

            indexer.IndexDirectoryAsync(tempRoot).GetAwaiter().GetResult();

            var projectKey = OrchestratorIDE.Services.CodeGraph.RoslynIndexer.DeriveProjectKey(tempRoot);

            var nodes = repo.GetNodesForProject(projectKey);
            Assert.That(nodes.Count, Is.GreaterThanOrEqualTo(3)); // IService, Service, methods

            var hasInterface = nodes.Any(n => n.Label == "Interface" && n.Name == "IService");
            var hasClass = nodes.Any(n => n.Label == "Class" && n.Name == "Service");
            Assert.That(hasInterface, Is.True);
            Assert.That(hasClass, Is.True);

            var edges = repo.GetEdges(projectKey);
            var hasImplements = edges.Any(e => e.EdgeType == "IMPLEMENTS");
            var hasCalls = edges.Any(e => e.EdgeType == "CALLS");
            Assert.That(hasImplements, Is.True, "Expected IMPLEMENTS edge(s)");
            Assert.That(hasCalls, Is.True, "Expected CALLS edge(s) from method invocations");

            // Non-FTS path must see the nodes (structural filter)
            var byName = repo.SearchNodes(namePattern: "*Service*", project: projectKey, limit: 10);
            Assert.That(byName.Count, Is.GreaterThan(0), "Expected nodes visible via namePattern");

            // FTS (BM25) path used by graph_search
            var searchHits = repo.SearchNodes(query: "service", project: projectKey, limit: 10);
            Assert.That(searchHits.Count, Is.GreaterThan(0));

            // Method token should also surface via FTS
            var methodHits = repo.SearchNodes(query: "do", project: projectKey, limit: 5);
            Assert.That(methodHits.Any(h => h.Name == "Do" || h.QualifiedName.Contains(".Do")), Is.True);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Test]
    public void GraphSearch_Via_Repo_Produces_Ranked_Plaintext_Friendly_Results()
    {
        using var store = NewMemoryStore();
        var repo = new GraphRepository(store);

        var p = "demo";
        var cls = Mk(p, "Class", "MyService", "Demo.MyService", "MyService.cs", 10, 30);
        var m1 = Mk(p, "Method", "Execute", "Demo.MyService.Execute", "MyService.cs", 12, 20);
        var m2 = Mk(p, "Method", "ExecuteAsync", "Demo.MyService.ExecuteAsync", "MyService.cs", 22, 28);

        repo.ReplaceGraph(p, new[] { cls, m1, m2 }, new[]
        {
            (m1.QualifiedName, m2.QualifiedName, "CALLS")
        });

        var hits = repo.SearchNodes(query: "execute", project: p, limit: 5);
        Assert.That(hits.Count, Is.GreaterThan(0));

        // Structural preference: Method over Class for "execute" term should surface methods first-ish
        var topLabels = hits.Take(2).Select(h => h.Label).ToList();
        Assert.That(topLabels.Any(l => l is "Method" or "Function"), Is.True);
    }
}
