// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace OrchestratorIDE.Core;

// ── Result types ──────────────────────────────────────────────────────────────

public enum DiagSeverity { Info, Warning, Error }

public record ToolDiagnostic(
    DiagSeverity Severity,
    string       Code,
    string       Message,
    int          Line,
    int          Column)
{
    public string SeverityIcon => Severity switch
    {
        DiagSeverity.Error   => "✕",
        DiagSeverity.Warning => "⚠",
        _                    => "ℹ",
    };

    public string Location => $"Ln {Line}, Col {Column}";
}

public record CompileResult(
    bool                    Success,
    byte[]?                 AssemblyBytes,
    List<ToolDiagnostic>    Diagnostics);

public record LoadResult(
    bool    Success,
    string? ToolName,
    string? Error);

// ── Collectible assembly context ──────────────────────────────────────────────

/// <summary>
/// Isolated load context for one compiled custom tool assembly.
/// isCollectible=true lets the runtime GC the assembly when we call Unload(),
/// making true hot-swap possible without memory leaks.
/// </summary>
internal sealed class CollectibleToolContext : AssemblyLoadContext
{
    public CollectibleToolContext() : base(isCollectible: true) { }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Delegate everything to the host (default) context so that types like
        // ICustomTool, ToolParameter, etc. resolve to the already-loaded host
        // versions — not new copies that would cause type-identity mismatches.
        try { return Default.LoadFromAssemblyName(assemblyName); }
        catch { return null; }
    }
}

// ── ToolCompiler ──────────────────────────────────────────────────────────────

/// <summary>
/// Phase 7 Layer 3 — Roslyn hot-load tool editor engine.
///
/// Compile(source)       → Roslyn emit to MemoryStream, return diagnostics
/// Load(compiled)        → load into CollectibleToolContext, register in ToolRegistry
/// Save(source, name)    → persist to workspace/.orc/tools/{name}.cs
/// ScanAndLoadAll(root)  → called on workspace open, auto-loads saved tools
/// Scaffold()            → returns a ready-to-edit ICustomTool class template
/// </summary>
public class ToolCompiler
{
    private readonly ToolRegistry _registry;

    // One CollectibleToolContext per loaded tool name.
    // Cleared and recreated on every hot-swap so the old assembly can be GC'd.
    private readonly Dictionary<string, CollectibleToolContext> _contexts = [];

    public ToolCompiler(ToolRegistry registry)
    {
        _registry = registry;
    }

    // ── Compile ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compile C# source using Roslyn. Returns diagnostics immediately.
    /// Does NOT load the assembly — call LoadAsync() only after Success == true.
    /// </summary>
    public Task<CompileResult> CompileAsync(string source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: ct);

        var refs = BuildReferences();
        if (refs.Count == 0)
            return Task.FromResult(new CompileResult(false, null,
            [new ToolDiagnostic(DiagSeverity.Error, "TC0001",
                "Could not resolve runtime reference assemblies. " +
                "Run from the IDE debug build (not single-file publish).", 0, 0)]));

        var compilation = CSharpCompilation.Create(
            $"CustomTool_{Guid.NewGuid():N}",
            syntaxTrees: [tree],
            references:  refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Debug)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithPlatform(Platform.AnyCpu));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms, cancellationToken: ct);

        var diags = emit.Diagnostics
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new ToolDiagnostic(
                    d.Severity == DiagnosticSeverity.Error ? DiagSeverity.Error : DiagSeverity.Warning,
                    d.Id,
                    d.GetMessage(),
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1);
            })
            .ToList();

        if (!emit.Success)
            return Task.FromResult(new CompileResult(false, null, diags));

        return Task.FromResult(new CompileResult(true, ms.ToArray(), diags));
    }

    // ── Load / Hot-Swap ───────────────────────────────────────────────────────

    /// <summary>
    /// Load a successful CompileResult: spin up a CollectibleToolContext,
    /// reflect to find the ICustomTool implementation, instantiate it, and
    /// register it in ToolRegistry. Any previous version of the same tool is
    /// unloaded first (true hot-swap).
    /// </summary>
    public Task<LoadResult> LoadAsync(CompileResult compiled, CancellationToken ct = default)
    {
        if (!compiled.Success || compiled.AssemblyBytes is null)
            return Task.FromResult(new LoadResult(false, null, "Compilation failed — nothing to load."));

        var context = new CollectibleToolContext();
        Assembly asm;
        try
        {
            using var ms = new MemoryStream(compiled.AssemblyBytes);
            asm = context.LoadFromStream(ms);
        }
        catch (Exception ex)
        {
            context.Unload();
            return Task.FromResult(new LoadResult(false, null, $"Assembly load error: {ex.Message}"));
        }

        // Find a concrete type implementing ICustomTool
        Type? toolType = null;
        try
        {
            toolType = asm.GetTypes().FirstOrDefault(t =>
                !t.IsAbstract && !t.IsInterface &&
                t.GetInterfaces().Any(i => i.FullName == typeof(ICustomTool).FullName));
        }
        catch (ReflectionTypeLoadException ex)
        {
            context.Unload();
            var msg = ex.LoaderExceptions.FirstOrDefault()?.Message ?? "Type load failed.";
            return Task.FromResult(new LoadResult(false, null, msg));
        }

        if (toolType is null)
        {
            context.Unload();
            return Task.FromResult(new LoadResult(false, null,
                "No class implementing ICustomTool was found. " +
                "Make sure your class is public and implements ICustomTool."));
        }

        ICustomTool? instance;
        try
        {
            instance = (ICustomTool?)Activator.CreateInstance(toolType);
        }
        catch (Exception ex)
        {
            context.Unload();
            return Task.FromResult(new LoadResult(false, null, $"Could not create instance: {ex.Message}"));
        }

        if (instance is null)
        {
            context.Unload();
            return Task.FromResult(new LoadResult(false, null, "Activator returned null."));
        }

        // Hot-swap: unload the previous version before registering the new one
        UnloadByName(instance.Name);
        _contexts[instance.Name] = context;

        _registry.Register(new ToolDefinition
        {
            Name             = instance.Name,
            Description      = instance.Description,
            Parameters       = instance.Parameters,
            Required         = instance.Required,
            RequiresApproval = instance.RequiresApproval,
            Handler          = (args, innerCt) => instance.ExecuteAsync(args, innerCt),
        });

        return Task.FromResult(new LoadResult(true, instance.Name, null));
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Persist source to {workspaceRoot}/.orc/tools/{toolName}.cs.
    /// The file is auto-discovered and reloaded on next workspace open.
    /// </summary>
    public async Task SaveAsync(string source, string toolName, string workspaceRoot)
    {
        var dir  = Path.Combine(workspaceRoot, ".orc", "tools");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Sanitize(toolName)}.cs");
        await File.WriteAllTextAsync(path, source);
    }

    // ── Auto-load on workspace open ───────────────────────────────────────────

    /// <summary>
    /// Called when a workspace opens. Finds every *.cs in .orc/tools/,
    /// compiles and loads it. Returns per-file (filename, success, error?) tuples.
    /// </summary>
    public async Task<List<(string File, bool Ok, string? Error)>> ScanAndLoadAllAsync(
        string workspaceRoot, CancellationToken ct = default)
    {
        var dir = Path.Combine(workspaceRoot, ".orc", "tools");
        if (!Directory.Exists(dir)) return [];

        var results = new List<(string, bool, string?)>();
        foreach (var file in Directory.GetFiles(dir, "*.cs").OrderBy(f => f))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var source   = await File.ReadAllTextAsync(file, ct);
                var compiled = await CompileAsync(source, ct);
                if (!compiled.Success)
                {
                    var errs = string.Join("; ",
                        compiled.Diagnostics
                            .Where(d => d.Severity == DiagSeverity.Error)
                            .Select(d => d.Message));
                    results.Add((Path.GetFileName(file), false, errs));
                    continue;
                }
                var loaded = await LoadAsync(compiled, ct);
                results.Add((Path.GetFileName(file), loaded.Success, loaded.Error));
            }
            catch (Exception ex)
            {
                results.Add((Path.GetFileName(file), false, ex.Message));
            }
        }
        return results;
    }

    // ── Unload ────────────────────────────────────────────────────────────────

    public void UnloadByName(string toolName)
    {
        if (!_contexts.TryGetValue(toolName, out var ctx)) return;
        _contexts.Remove(toolName);
        ctx.Unload();
        // ToolRegistry keeps old entry until overwritten by Register() — acceptable.
    }

    public IReadOnlyList<string> LoadedToolNames => [.. _contexts.Keys];

    // ── Scaffold template ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns a ready-to-edit ICustomTool class stub.
    /// Drop into the editor and fill in your logic.
    ///
    /// Uses @"..." + Replace rather than $"""...""" to avoid the brace-escaping
    /// complexity of embedding C# code inside an interpolated raw string.
    /// </summary>
    public static string Scaffold(string className = "MyTool", string toolId = "my_tool")
    {
        return @"using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.CustomTools;

/// <summary>
/// __CLASSNAME__ — describe what this tool does.
/// The agent reads the Description property to decide when to call it.
/// </summary>
public class __CLASSNAME__ : ICustomTool
{
    public string Name        => ""__TOOLID__"";
    public string Description => ""Describe what this tool does in one sentence."";
    public bool   RequiresApproval => false;

    public Dictionary<string, ToolParameter> Parameters => new()
    {
        [""input""] = new ToolParameter(""string"", ""The input value for this tool""),
    };

    public string[] Required => [""input""];

    public async Task<string> ExecuteAsync(
        Dictionary<string, object?> args,
        CancellationToken ct)
    {
        var input = args.TryGetValue(""input"", out var v) ? v?.ToString() ?? """" : """";

        // ── Your logic here ───────────────────────────────────────────────
        // Full access to System.IO, System.Net.Http, etc.
        // Return ""[OK] ..."" on success or ""[ERROR] ..."" on failure.
        await Task.Delay(0, ct); // remove if not using async

        return $""[OK] {input}"";
    }
}
"
            .Replace("__CLASSNAME__", className)
            .Replace("__TOOLID__",    toolId);
    }

    // ── Reference assembly resolution ─────────────────────────────────────────

    private static List<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>();

        // BCL and runtime assemblies via the trusted platform assembly list
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (var path in trusted.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(path))
                refs.Add(MetadataReference.CreateFromFile(path));
        }

        // Our own assembly — exposes ICustomTool, ToolParameter, etc.
        // In a single-file publish Assembly.Location returns ""; fall back to BaseDirectory.
        // IL3000 is suppressed: the call is intentional and guarded by the fallback.
#pragma warning disable IL3000
        var ourPath = typeof(ToolCompiler).Assembly.Location;
#pragma warning restore IL3000
        if (string.IsNullOrEmpty(ourPath))
            ourPath = Path.Combine(AppContext.BaseDirectory, "OrchestratorIDE.dll");
        if (!string.IsNullOrEmpty(ourPath) && File.Exists(ourPath))
        {
            // Only add if not already in the trusted list
            if (!refs.Any(r => string.Equals(r.Display, ourPath, StringComparison.OrdinalIgnoreCase)))
                refs.Add(MetadataReference.CreateFromFile(ourPath));
        }

        return refs;
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_'));
}
