// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Loads and provides access to the hand-curated model catalog
/// (Resources/curated-models.json embedded in the assembly).
///
/// The catalog enriches live HuggingFace search results with hand-verified
/// descriptions, swarm-role recommendations, and tool-use notes.
/// It is a metadata overlay — never a source of download URLs.
/// </summary>
public static class CuratedModelCatalog
{
    private static List<CuratedModelEntry>? _entries;
    private static readonly object _lock = new();

    // ── Load ──────────────────────────────────────────────────────────────────

    public static IReadOnlyList<CuratedModelEntry> All
    {
        get
        {
            if (_entries is not null) return _entries;
            lock (_lock)
            {
                _entries ??= Load();
                return _entries;
            }
        }
    }

    private static List<CuratedModelEntry> Load()
    {
        try
        {
            var json = ReadJson();
            if (json is null) return [];

            // Strip JS-style // comments before parsing (our JSON uses them for readability)
            var stripped = StripComments(json);

            var root   = JsonNode.Parse(stripped);
            var models = root?["models"]?.AsArray();
            if (models is null) return [];

            var result = new List<CuratedModelEntry>();
            foreach (var node in models)
            {
                if (node is null || node["id"] is null) continue;
                result.Add(new CuratedModelEntry
                {
                    Id                 = node["id"]!.GetValue<string>(),
                    Name               = node["name"]?.GetValue<string>()             ?? "",
                    HuggingFaceId      = node["huggingface_id"]?.GetValue<string>()   ?? "",
                    OllamaName         = node["ollama_name"]?.GetValue<string>()       ?? "",
                    Publisher          = node["publisher"]?.GetValue<string>()         ?? "",
                    Architecture       = node["architecture"]?.GetValue<string>()      ?? "",
                    ParametersB        = node["parameters_b"]?.GetValue<double>()      ?? 0,
                    ContextK           = node["context_k"]?.GetValue<int>()            ?? 0,
                    Description        = node["description"]?.GetValue<string>()       ?? "",
                    IntendedUse        = node["intended_use"]?.GetValue<string>()      ?? "",
                    ToolUse            = node["tool_use"]?.GetValue<string>()          ?? "",
                    SwarmRoles         = node["swarm_roles"]?.AsArray()
                                             .Select(r => r?.GetValue<string>() ?? "")
                                             .ToArray()                                ?? [],
                    SwarmCapable       = node["swarm_capable"]?.GetValue<bool>()       ?? false,
                    VramMinGb          = node["vram_min_gb"]?.GetValue<int>()          ?? 0,
                    VramRecommendedGb  = node["vram_recommended_gb"]?.GetValue<int>()  ?? 0,
                    CpuOk              = node["cpu_ok"]?.GetValue<bool>()              ?? false,
                    RecommendedQuant   = node["recommended_quant"]?.GetValue<string>() ?? "",
                    QualityStars       = node["quality_stars"]?.GetValue<int>()        ?? 0,
                    Tags               = node["tags"]?.AsArray()
                                             .Select(t => t?.GetValue<string>() ?? "")
                                             .ToArray()                                ?? [],
                });
            }
            return result;
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadJson()
    {
        // 1. Try embedded resource (production single-file build)
        var asm  = System.Reflection.Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
                      .FirstOrDefault(n => n.EndsWith("curated-models.json",
                                                       StringComparison.OrdinalIgnoreCase));
        if (name is not null)
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        // 2. Fall back to disk path (dev/debug mode)
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "curated-models.json"),
            Path.Combine(AppContext.BaseDirectory, "curated-models.json"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return File.ReadAllText(c);

        return null;
    }

    /// <summary>Strips // line comments from JSON text (not standard JSON but we use it).</summary>
    private static string StripComments(string json)
    {
        var lines = json.Split('\n');
        var sb    = new System.Text.StringBuilder(json.Length);
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//")) continue;   // whole-line comment
            // Inline comment: find // outside a string literal
            var inString = false;
            var i = 0;
            for (; i < line.Length - 1; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
                if (!inString && line[i] == '/' && line[i + 1] == '/') break;
            }
            sb.AppendLine(i < line.Length - 1 ? line[..i].TrimEnd() : line);
        }
        return sb.ToString();
    }

    // ── Query helpers ─────────────────────────────────────────────────────────

    /// <summary>Find a curated entry by its HuggingFace repo ID (case-insensitive).</summary>
    public static CuratedModelEntry? FindByHfId(string hfId) =>
        All.FirstOrDefault(e => string.Equals(e.HuggingFaceId, hfId,
                                              StringComparison.OrdinalIgnoreCase));

    /// <summary>Find curated entries whose name/description/tags match the query.</summary>
    public static IEnumerable<CuratedModelEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;

        var q = query.Trim().ToLowerInvariant();
        return All.Where(e =>
            e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)       ||
            e.Id.Contains(q, StringComparison.OrdinalIgnoreCase)          ||
            e.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase)   ||
            e.Architecture.Contains(q, StringComparison.OrdinalIgnoreCase)||
            e.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>All entries suitable for a given swarm role ("boss", "worker", "researcher").</summary>
    public static IEnumerable<CuratedModelEntry> ForRole(string role) =>
        All.Where(e => e.SwarmRoles.Contains(role, StringComparer.OrdinalIgnoreCase));
}
