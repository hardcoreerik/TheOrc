// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Rules model ───────────────────────────────────────────────────────────────

/// <summary>
/// Which simplification transforms to apply to tool schemas before sending
/// them to a model that fails on complex schemas.
/// </summary>
public record SchemaSimplificationRules(
    bool FlattenNested       = false,   // collapse nested objects to top-level fields
    bool ReplaceTypedStrings = false,   // replace "path","uri","filepath" types → "string"
    bool RemoveOptional      = false,   // strip non-required fields
    bool ShortenDescriptions = false,   // truncate field descriptions to 10 words
    bool ReplaceEnums        = false    // replace enum constraints → plain "string"
)
{
    public bool IsNoOp =>
        !FlattenNested && !ReplaceTypedStrings && !RemoveOptional &&
        !ShortenDescriptions && !ReplaceEnums;

    /// <summary>Human-readable summary for activity log.</summary>
    public string Summary
    {
        get
        {
            if (IsNoOp) return "none";
            var parts = new List<string>();
            if (FlattenNested)       parts.Add("flatten");
            if (ReplaceTypedStrings) parts.Add("typed→string");
            if (RemoveOptional)      parts.Add("drop-optional");
            if (ShortenDescriptions) parts.Add("short-desc");
            if (ReplaceEnums)        parts.Add("enum→string");
            return string.Join(", ", parts);
        }
    }
}

// ── Simplifier ────────────────────────────────────────────────────────────────

/// <summary>
/// GOBLIN MIND Phase 4 — Adaptive Schema Reduction Middleware.
///
/// Runs transparently in AgentLoop: given a list of tool schemas and a
/// model's known simplification rules, returns a transformed copy that
/// the model is more likely to call correctly.
///
/// Users never see this — it just makes complex tools work on weaker models.
/// </summary>
public static class SchemaSimplifier
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = false,
    };

    // ── Public entry ─────────────────────────────────────────────────────────

    /// <summary>
    /// Apply simplification rules to a list of tool schemas.
    /// Returns the original list untouched if rules.IsNoOp.
    /// </summary>
    public static IReadOnlyList<object> Apply(
        IReadOnlyList<object> tools,
        SchemaSimplificationRules rules)
    {
        if (tools.Count == 0 || rules.IsNoOp) return tools;

        try
        {
            var json    = JsonSerializer.Serialize(tools);
            var arr     = JsonNode.Parse(json)?.AsArray();
            if (arr == null) return tools;

            foreach (var toolNode in arr)
            {
                var paramNode = toolNode?["function"]?["parameters"];
                if (paramNode == null) continue;

                if (rules.FlattenNested)       FlattenNestedObjects(paramNode);
                if (rules.ReplaceTypedStrings) ReplaceTypedStringFields(paramNode);
                if (rules.RemoveOptional)      DropOptionalFields(paramNode);
                if (rules.ShortenDescriptions) ShortenAllDescriptions(paramNode);
                if (rules.ReplaceEnums)        StripEnumConstraints(paramNode);
            }

            // Return as List<JsonNode> — these serialize correctly when the
            // payload dict is JSON-serialized in OllamaClient.
            return arr.Select(n => (object)n!).ToList();
        }
        catch
        {
            return tools;   // non-fatal: return originals if anything goes wrong
        }
    }

    /// <summary>
    /// Determine which rules a model needs based on its probe history.
    /// Called when building rules from probe failures.
    /// </summary>
    public static SchemaSimplificationRules DeriveRules(ToolCallProfile profile)
    {
        // If we have no failure data, no rules needed
        if (profile.TestPassMap.Count == 0)
            return new SchemaSimplificationRules();

        // If StructuredOutput failed → model struggles with multi-field complex schemas
        var structFailed = profile.TestPassMap.TryGetValue(
            $"{ProbeTestId.StructuredOutput}_{ProbeMode.NativeApi}", out var sf) && !sf;
        var multilineFailed = profile.TestPassMap.TryGetValue(
            $"{ProbeTestId.MultilineContent}_{ProbeMode.NativeApi}", out var mf) && !mf;

        return new SchemaSimplificationRules(
            FlattenNested:       structFailed,
            ReplaceTypedStrings: structFailed,
            RemoveOptional:      structFailed,
            ShortenDescriptions: multilineFailed || structFailed,
            ReplaceEnums:        false
        );
    }

    // ── Transformations ───────────────────────────────────────────────────────

    private static void FlattenNestedObjects(JsonNode paramNode)
    {
        var props = paramNode["properties"]?.AsObject();
        if (props == null) return;

        var toFlatten = new List<(string key, JsonObject nested)>();
        foreach (var prop in props)
        {
            if (prop.Value is JsonObject obj &&
                obj["type"]?.GetValue<string>() == "object" &&
                obj["properties"] is JsonObject nestedProps)
            {
                toFlatten.Add((prop.Key, nestedProps));
            }
        }

        foreach (var (key, nested) in toFlatten)
        {
            props.Remove(key);
            foreach (var nestedProp in nested)
            {
                var flatKey = $"{key}_{nestedProp.Key}";
                if (!props.ContainsKey(flatKey))
                    props[flatKey] = nestedProp.Value?.DeepClone();
            }
        }
    }

    private static readonly HashSet<string> _typedStringTypes =
        new(StringComparer.OrdinalIgnoreCase) { "path", "uri", "filepath", "url", "file_path", "directory" };

    private static void ReplaceTypedStringFields(JsonNode paramNode)
    {
        var props = paramNode["properties"]?.AsObject();
        if (props == null) return;
        foreach (var prop in props)
        {
            if (prop.Value is JsonObject obj)
            {
                var type = obj["type"]?.GetValue<string>();
                if (type != null && _typedStringTypes.Contains(type))
                    obj["type"] = JsonValue.Create("string");
            }
        }
    }

    private static void DropOptionalFields(JsonNode paramNode)
    {
        var props    = paramNode["properties"]?.AsObject();
        var required = paramNode["required"]?.AsArray();
        if (props == null || required == null) return;

        var requiredSet = required.Select(r => r?.GetValue<string>()).Where(r => r != null).ToHashSet()!;
        var toRemove    = props.Select(p => p.Key).Where(k => !requiredSet.Contains(k)).ToList();
        foreach (var key in toRemove) props.Remove(key);
    }

    private static void ShortenAllDescriptions(JsonNode paramNode)
    {
        var props = paramNode["properties"]?.AsObject();
        if (props == null) return;
        foreach (var prop in props)
        {
            if (prop.Value is JsonObject obj && obj["description"] is JsonValue desc)
            {
                var text = desc.GetValue<string>();
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length > 10)
                    obj["description"] = JsonValue.Create(string.Join(' ', words.Take(10)) + ".");
            }
        }
    }

    private static void StripEnumConstraints(JsonNode paramNode)
    {
        var props = paramNode["properties"]?.AsObject();
        if (props == null) return;
        foreach (var prop in props)
        {
            if (prop.Value is JsonObject obj && obj.ContainsKey("enum"))
            {
                obj.Remove("enum");
                obj["type"] = JsonValue.Create("string");
            }
        }
    }
}
