// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using OrchestratorIDE.Services.CodeGraph.Data;

namespace OrchestratorIDE.Services.CodeGraph;

/// <summary>
/// Roslyn-based indexer for CodeGraph v1 (step 2+3).
/// Loads via MSBuildWorkspace (when .csproj present and the assembly is loadable at runtime)
/// with fallback to Adhoc-style CSharpCompilation over discovered .cs files.
/// Produces CodeNode for Class/Interface + Method/Function and CALLS/IMPLEMENTS edges.
/// After ReplaceGraph, runs ComplexityAnalyzer on collected methods and calls UpdateComplexity + ComputeTransitiveLoopDepths.
/// </summary>
public sealed class RoslynIndexer
{
    private readonly GraphRepository _repo;

    public RoslynIndexer(GraphRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    /// <summary>
    /// Derive a stable project key from a workspace directory (folder name).
    /// </summary>
    public static string DeriveProjectKey(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "workspace";
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "workspace" : name;
    }

    /// <summary>
    /// Index a directory as one project. Uses first *.csproj found (top-level preferred) to drive loading
    /// strategy; falls back to loose *.cs discovery.
    /// </summary>
    public async Task IndexAsync(string projectKey, string directory, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory not found: {directory}");

        var normalizedDir = Path.GetFullPath(directory);

        // Discover csproj (prefer top-level to keep project scoping simple for v1)
        var topCsproj = Directory.EnumerateFiles(normalizedDir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var anyCsproj = topCsproj ?? Directory.EnumerateFiles(normalizedDir, "*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(f => !IsUnderObjOrBin(f));

        Compilation? compilation = null;
        List<string> sourcePaths;

        if (anyCsproj != null)
        {
            compilation = await TryLoadCompilationViaMsbuildAsync(anyCsproj, ct).ConfigureAwait(false);
            if (compilation != null)
            {
                sourcePaths = compilation.SyntaxTrees
                    .Select(t => t.FilePath)
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .ToList();
            }
            else
            {
                // Fallback: collect sources around the csproj and build our own compilation
                var projDir = Path.GetDirectoryName(anyCsproj)!;
                sourcePaths = CollectSourceFiles(projDir);
                compilation = BuildCompilation(projectKey, sourcePaths, ct);
            }
        }
        else
        {
            sourcePaths = CollectSourceFiles(normalizedDir);
            compilation = BuildCompilation(projectKey, sourcePaths, ct);
        }

        if (compilation == null)
        {
            // Last resort: still try to produce nodes from syntax only (limited semantics)
            compilation = BuildCompilation(projectKey, sourcePaths, ct);
        }

        var nodes = new List<CodeNode>();
        var edges = new List<(string SrcQualified, string DstQualified, string EdgeType)>();
        var qnToNode = new Dictionary<string, CodeNode>(StringComparer.Ordinal);

        if (compilation != null)
        {
            // First pass: types + methods (preserve accurate source locations from syntax)
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (ct.IsCancellationRequested) break;
                SemanticModel model;
                SyntaxNode root;
                try
                {
                    model = compilation.GetSemanticModel(tree);
                    root = tree.GetRoot(ct);
                }
                catch
                {
                    // Skip trees that Roslyn cannot produce a model for (parse errors, generated, etc.)
                    continue;
                }

                try
                {
                    // INamedTypeSymbol (Class / Interface primarily; include records/structs as Class per label model)
                    foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                    {
                        var sym = model.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                        if (sym == null || sym.IsImplicitlyDeclared) continue;
                        if (sym.TypeKind is not (TypeKind.Class or TypeKind.Interface or TypeKind.Struct))
                            continue;

                        var loc = FirstSourceLocation(sym);
                        if (loc == null || loc.SourceTree == null) continue;

                        var ls = loc.GetLineSpan().StartLinePosition.Line + 1;
                        var le = loc.GetLineSpan().EndLinePosition.Line + 1;
                        var qn = GetQualifiedName(sym);
                        if (qnToNode.ContainsKey(qn)) continue;

                        var label = sym.TypeKind == TypeKind.Interface ? "Interface" : "Class";
                        var node = new CodeNode(null, projectKey, label, sym.Name, qn,
                            loc.SourceTree.FilePath, ls, le, null, null, null, null, null, false, 0);
                        nodes.Add(node);
                        qnToNode[qn] = node;

                        // IMPLEMENTS (interfaces + base type list)
                        AddImplementsEdges(sym, projectKey, edges);
                    }

                    // IMethodSymbol (ordinary methods, constructors treated as methods for the graph)
                    foreach (var methodDecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                    {
                        var sym = model.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
                        if (sym == null || sym.IsImplicitlyDeclared) continue;

                        var loc = FirstSourceLocation(sym);
                        if (loc == null || loc.SourceTree == null) continue;

                        var ls = loc.GetLineSpan().StartLinePosition.Line + 1;
                        var le = loc.GetLineSpan().EndLinePosition.Line + 1;
                        var qn = GetQualifiedName(sym);
                        if (qnToNode.ContainsKey(qn)) continue;

                        // Heuristic label: top-level-ish functions vs methods inside named types
                        var containing = sym.ContainingType;
                        bool looksTopLevel = containing == null ||
                                             containing.SpecialType == SpecialType.System_Object ||
                                             containing.Name.Contains("<Program>", StringComparison.Ordinal) ||
                                             containing.Name == "<Program>$" ||
                                             containing.TypeKind == TypeKind.Module;

                        var label = looksTopLevel ? "Function" : "Method";
                        var node = new CodeNode(null, projectKey, label, sym.Name, qn,
                            loc.SourceTree.FilePath, ls, le, null, null, null, null, null, false, 0);
                        nodes.Add(node);
                        qnToNode[qn] = node;
                    }

                    // Also capture local functions as "Function" (IMethodSymbol via LocalFunctionStatementSyntax)
                    foreach (var localFunc in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
                    {
                        var sym = model.GetDeclaredSymbol(localFunc) as IMethodSymbol;
                        if (sym == null || sym.IsImplicitlyDeclared) continue;

                        var loc = FirstSourceLocation(sym);
                        if (loc == null || loc.SourceTree == null) continue;

                        var ls = loc.GetLineSpan().StartLinePosition.Line + 1;
                        var le = loc.GetLineSpan().EndLinePosition.Line + 1;
                        var qn = GetQualifiedName(sym);
                        if (qnToNode.ContainsKey(qn)) continue;

                        var node = new CodeNode(null, projectKey, "Function", sym.Name, qn,
                            loc.SourceTree.FilePath, ls, le, null, null, null, null, null, false, 0);
                        nodes.Add(node);
                        qnToNode[qn] = node;
                    }
                }
                catch
                {
                    // One tree's semantic walk failed; continue with others.
                }
            }

            // Second pass: CALLS edges via invocation-expression walk + semantic symbol resolution
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (ct.IsCancellationRequested) break;
                SemanticModel model;
                SyntaxNode root;
                try
                {
                    model = compilation.GetSemanticModel(tree);
                    root = tree.GetRoot(ct);
                }
                catch
                {
                    continue;
                }

                try
                {
                    foreach (var invoc in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var symInfo = model.GetSymbolInfo(invoc);
                        var called = (symInfo.Symbol as IMethodSymbol) ??
                                     (symInfo.CandidateSymbols.FirstOrDefault() as IMethodSymbol);
                        if (called == null) continue;

                        var srcMethod = FindContainingMethod(invoc, model);
                        if (srcMethod != null)
                        {
                            var srcQn = GetQualifiedName(srcMethod);
                            var dstQn = GetQualifiedName(called);
                            edges.Add((srcQn, dstQn, "CALLS"));
                        }
                    }
                }
                catch
                {
                    // tolerate bad invocation analysis in one tree
                }
            }
        }

        // Deduplicate edges (project, src, dst, type)
        edges = edges
            .GroupBy(e => (e.SrcQualified, e.DstQualified, e.EdgeType))
            .Select(g => g.First())
            .ToList();

        // ── Step 3: ComplexityAnalyzer (cyclomatic/cognitive/loop/linear via CFG + syntax walk) ──
        // Collect while compilation/semantic models are in scope.
        var methodQns = new HashSet<string>(StringComparer.Ordinal);
        var qnToMetrics = new Dictionary<string, ComplexityAnalyzer.Metrics>(StringComparer.Ordinal);
        var selfRecursiveQns = edges
            .Where(e => e.EdgeType == "CALLS" && e.SrcQualified == e.DstQualified)
            .Select(e => e.SrcQualified)
            .ToHashSet(StringComparer.Ordinal);

        if (compilation != null)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (ct.IsCancellationRequested) break;
                SemanticModel model;
                SyntaxNode root;
                try
                {
                    model = compilation.GetSemanticModel(tree);
                    root = tree.GetRoot(ct);
                }
                catch { continue; }

                try
                {
                    foreach (var mdecl in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                    {
                        var sym = model.GetDeclaredSymbol(mdecl) as IMethodSymbol;
                        if (sym == null || sym.IsImplicitlyDeclared) continue;
                        var qn = GetQualifiedName(sym);
                        if (methodQns.Add(qn) && !qnToMetrics.ContainsKey(qn))
                            qnToMetrics[qn] = ComplexityAnalyzer.Analyze(model, mdecl);
                    }

                    foreach (var ldecl in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
                    {
                        var sym = model.GetDeclaredSymbol(ldecl) as IMethodSymbol;
                        if (sym == null || sym.IsImplicitlyDeclared) continue;
                        var qn = GetQualifiedName(sym);
                        if (methodQns.Add(qn) && !qnToMetrics.ContainsKey(qn))
                            qnToMetrics[qn] = ComplexityAnalyzer.Analyze(model, ldecl);
                    }
                }
                catch { }
            }
        }

        // One transaction per project via GraphRepository.
        // ReplaceGraph performs node inserts + CorrectFtsForId (split tokens for BM25) + edge wiring + degree recompute
        // inside a single tx. This satisfies the "write in one transaction" + FTS maintenance after node batch.
        _repo.ReplaceGraph(projectKey, nodes, edges);

        // Wire complexity into rows via UpdateComplexity (per spec), then transitive over CALLS.
        foreach (var qn in methodQns)
        {
            if (qnToMetrics.TryGetValue(qn, out var met))
            {
                bool rec = selfRecursiveQns.Contains(qn);
                _repo.UpdateComplexity(projectKey, qn,
                    cyclomatic: met.Cyclomatic,
                    cognitive: met.Cognitive,
                    loopDepth: met.LoopDepth,
                    linearScanInLoop: met.LinearScanInLoop,
                    isRecursive: rec);
            }
        }
        _repo.ComputeTransitiveLoopDepths(projectKey);

        // If an explicit post-batch FTS SELECT rebuild is required by future steps, it can be added here
        // using direct INSERT INTO graph_fts(rowid, ...) SELECT ... with camel-split values.
    }

    /// <summary>Convenience overload using a derived project key for the directory.</summary>
    public Task IndexDirectoryAsync(string directory, CancellationToken ct = default)
        => IndexAsync(DeriveProjectKey(directory), directory, ct);

    // ─────────────────────────────────────────────────────────────────────────
    // MSBuild / compilation helpers (no hard dependency on extra NuGet at compile time)
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<Compilation?> TryLoadCompilationViaMsbuildAsync(string csprojPath, CancellationToken ct)
    {
        try
        {
            // Attempt to resolve MSBuildWorkspace without a compile-time reference to the workspaces package.
            const string typeName = "Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace";
            const string asmSimple = "Microsoft.CodeAnalysis.Workspaces.MSBuild";

            Assembly? asm = null;
            try { asm = Assembly.Load(asmSimple); } catch { /* not present */ }
            if (asm == null)
            {
                // Try to locate a sibling or runtime-provided copy (best-effort; zero new packages)
                var probeDirs = new[]
                {
                    AppContext.BaseDirectory,
                    Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "",
                    Environment.CurrentDirectory
                };
                foreach (var d in probeDirs.Distinct())
                {
                    if (string.IsNullOrWhiteSpace(d)) continue;
                    var candidate = Path.Combine(d, "Microsoft.CodeAnalysis.Workspaces.MSBuild.dll");
                    if (File.Exists(candidate))
                    {
                        try { asm = Assembly.LoadFrom(candidate); break; } catch { }
                    }
                }
            }
            if (asm == null) return null;

            var wsType = asm.GetType(typeName, throwOnError: false);
            if (wsType == null) return null;

            // MSBuildWorkspace.Create()
            var createMi = wsType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var ws = createMi?.Invoke(null, null);
            if (ws == null) return null;

            // OpenProjectAsync(string, CancellationToken)
            var openMethods = wsType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "OpenProjectAsync")
                .ToArray();
            MethodInfo? open = openMethods.FirstOrDefault(m =>
            {
                var ps = m.GetParameters();
                return ps.Length >= 1 && ps[0].ParameterType == typeof(string);
            }) ?? openMethods.FirstOrDefault();

            if (open == null) return null;

            object? taskObj;
            var psig = open.GetParameters();
            if (psig.Length >= 2 && psig[1].ParameterType == typeof(CancellationToken))
                taskObj = open.Invoke(ws, new object[] { csprojPath, ct });
            else
                taskObj = open.Invoke(ws, new object[] { csprojPath });

            if (taskObj is not Task task) return null;
            await task.ConfigureAwait(false);

            var resultProp = task.GetType().GetProperty("Result");
            var project = resultProp?.GetValue(task);
            if (project == null) return null;

            // project.GetCompilationAsync(ct)
            var getComp = project.GetType().GetMethod("GetCompilationAsync", new[] { typeof(CancellationToken) });
            if (getComp == null) return null;

            var compTaskObj = getComp.Invoke(project, new object[] { ct });
            if (compTaskObj is not Task compTask) return null;
            await compTask.ConfigureAwait(false);

            var compResultProp = compTask.GetType().GetProperty("Result");
            return compResultProp?.GetValue(compTask) as Compilation;
        }
        catch
        {
            return null; // graceful fallback
        }
    }

    private static Compilation BuildCompilation(string assemblyName, IEnumerable<string> files, CancellationToken ct)
    {
        var trees = new List<SyntaxTree>();
        foreach (var f in files)
        {
            try
            {
                var text = File.ReadAllText(f);
                var src = SourceText.From(text, System.Text.Encoding.UTF8);
                var tree = CSharpSyntaxTree.ParseText(src, path: f, options: CSharpParseOptions.Default, cancellationToken: ct);
                trees.Add(tree);
            }
            catch
            {
                // skip unreadable / encoding broken files
            }
        }

        var refs = GetMetadataReferences();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true);
        return CSharpCompilation.Create(assemblyName, trees, refs, options);
    }

    private static List<MetadataReference> GetMetadataReferences()
    {
        var list = new List<MetadataReference>();
        void AddIfValid(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                list.Add(MetadataReference.CreateFromFile(path));
        }

        // Core
        AddIfValid(typeof(object).Assembly.Location);
        AddIfValid(typeof(Enumerable).Assembly.Location);
        AddIfValid(typeof(List<>).Assembly.Location);
        AddIfValid(typeof(Task).Assembly.Location);
        AddIfValid(typeof(System.Text.RegularExpressions.Regex).Assembly.Location);

        // Useful for our own sources
        try { AddIfValid(typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.Location); } catch { }
        try { AddIfValid(typeof(System.Text.Json.JsonSerializer).Assembly.Location); } catch { }

        // Pull in as many currently loaded assemblies as practical (helps resolve symbols declared in dependencies)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (asm.IsDynamic) continue;
                var loc = asm.Location;
                if (string.IsNullOrWhiteSpace(loc) || !File.Exists(loc)) continue;
                if (list.Any(r => string.Equals(r.Display, loc, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(MetadataReference.CreateFromFile(loc));
            }
            catch { /* ignore */ }
        }

        return list;
    }

    private static List<string> CollectSourceFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsUnderObjOrBin(f))
                .Where(f => !IsGeneratedFile(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static bool IsUnderObjOrBin(string path)
    {
        var p = path.Replace('/', '\\');
        return p.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(".AssemblyAttributes", StringComparison.OrdinalIgnoreCase);
    }

    private static Location? FirstSourceLocation(ISymbol symbol)
    {
        return symbol.Locations.FirstOrDefault(l => l.IsInSource && l.SourceTree != null);
    }

    private static string GetQualifiedName(ISymbol symbol)
    {
        // Use display string that is stable and human + search friendly.
        var s = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (s.StartsWith("global::", StringComparison.Ordinal))
            s = s.Substring("global::".Length);
        return s;
    }

    private static void AddImplementsEdges(INamedTypeSymbol sym, string project, List<(string, string, string)> edges)
    {
        var src = GetQualifiedName(sym);

        foreach (var iface in sym.AllInterfaces)
        {
            var dst = GetQualifiedName(iface);
            edges.Add((src, dst, "IMPLEMENTS"));
        }

        var baseType = sym.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            var dst = GetQualifiedName(baseType);
            edges.Add((src, dst, "IMPLEMENTS"));
            baseType = baseType.BaseType;
        }
    }

    private static IMethodSymbol? FindContainingMethod(SyntaxNode node, SemanticModel model)
    {
        for (var current = node; current != null; current = current.Parent)
        {
            if (current is MethodDeclarationSyntax m)
                return model.GetDeclaredSymbol(m) as IMethodSymbol;
            if (current is LocalFunctionStatementSyntax lf)
                return model.GetDeclaredSymbol(lf) as IMethodSymbol;
            if (current is ConstructorDeclarationSyntax ctor)
                return model.GetDeclaredSymbol(ctor) as IMethodSymbol;
        }
        return null;
    }

    // ── Optional explicit FTS rebuild step (INSERT-style after batch) ──

    private void RebuildFtsForProject(string project)
    {
        // The ReplaceGraph path already corrects FTS to split-token form inside its transaction.
        // As an extra measure per the spec sketch we can force a content refresh for the project.
        // We do a lightweight "re-insert split" pass for all current rows of the project.
        // This is safe and idempotent.

        try
        {
            var all = _repo.GetNodesForProject(project);
            foreach (var n in all)
            {
                // Touch via the repository's upsert path is heavy; instead reach for the correction routine indirectly.
                // Since CorrectFts is private, the cheapest public way that keeps split tokens is a no-op ReplaceGraph of empty delta
                // or simply trust the prior write. For explicitness we call a tiny upsert that triggers Correct.
                // To avoid changing degree etc we do a direct SQL correction here using the same pattern.
                // For simplicity and to stay inside the public contract we re-upsert a tiny subset (the node itself).
                // UpsertNode will CorrectFts.
                _repo.UpsertNode(n); // safe re-write; degrees recomputed but cheap.
            }
        }
        catch
        {
            // best-effort; FTS state from ReplaceGraph is already correct for search.
        }
    }
}
