// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Mutation types ────────────────────────────────────────────────────────────

/// <summary>
/// The kind of mutation applied to produce a schema variant.
/// </summary>
public enum MutationType
{
    Original,           // Seed (no mutation)
    RenameField,        // Renamed a parameter field
    NestFields,         // Wrapped fields in a nested object
    FlattenFields,      // Flattened nested object to top-level
    ShortenDescription, // Shortened description text
    VerboseDescription, // More detailed description text
    AddType,            // Added explicit type constraints
    RemoveType,         // Removed type constraints → plain string
    RemoveOptional,     // Stripped optional fields
    AddOptional,        // Added optional helper fields
    StripEnums,         // Replaced enum with plain string
    AddEnums,           // Added enum constraints
}

/// <summary>
/// A single schema variant that was tested during evolutionary search.
/// </summary>
public record SchemaVariant(
    string       VariantId,      // unique ID e.g. "gen2_v3"
    string       ToolName,
    MutationType Mutation,
    object       SchemaPayload,  // the schema that was sent
    bool?        Passed,         // null = not yet tested
    string?      ParsedOutput,   // what the model produced (for debugging)
    string?      FailReason,
    DateTime     TestedAt
)
{
    public bool IsTested  => Passed.HasValue;
    public bool IsWinner  => Passed == true;
    public int  Fitness   => Passed == true ? 1 : 0;
}

// ── Per-model, per-tool map ───────────────────────────────────────────────────

/// <summary>
/// All tested schema variants for one (model, tool) pair.
/// Stores the evolutionary search results and identifies the best-performing schema.
/// </summary>
public record ToolFitnessRecord(
    string ModelId,
    string ToolName,
    List<SchemaVariant> Variants,
    DateTime UpdatedAt
)
{
    /// <summary>Best-performing variant (first winner, or null if none passed).</summary>
    public SchemaVariant? BestVariant
        => Variants.FirstOrDefault(v => v.IsWinner);

    /// <summary>How many variants were tested.</summary>
    public int TestedCount => Variants.Count(v => v.IsTested);

    /// <summary>Win rate across tested variants.</summary>
    public double WinRate => TestedCount == 0 ? 0
        : (double)Variants.Count(v => v.IsWinner) / TestedCount;

    /// <summary>Short summary for activity log.</summary>
    public string Summary => $"{Variants.Count(v => v.IsWinner)}/{TestedCount} passed  best={BestVariant?.Mutation}";
}

// ── Store ─────────────────────────────────────────────────────────────────────

/// <summary>
/// GOBLIN MIND Phase 5 — Evolutionary Schema Fitness Map.
///
/// Persists the results of evolutionary schema search: which schema variants
/// passed, which failed, and which should be promoted to SchemaLibrary.
///
/// Storage: %AppData%\OrchestratorIDE\fitness-maps.json
/// Key: "{modelId}::{toolName}"
/// </summary>
public static class FitnessMap
{
    public static readonly string MapPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "fitness-maps.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented        = true,
        Converters           = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly SemaphoreSlim _lock = new(1, 1);

    // ── Key helper ────────────────────────────────────────────────────────────

    private static string Key(string modelId, string toolName)
        => $"{modelId.ToLowerInvariant().Split(':')[0]}::{toolName.ToLowerInvariant()}";

    // ── Read ──────────────────────────────────────────────────────────────────

    public static ToolFitnessRecord? Load(string modelId, string toolName)
    {
        var all = LoadAllInternal();
        var key = Key(modelId, toolName);
        return all.TryGetValue(key, out var rec) ? rec : null;
    }

    public static List<ToolFitnessRecord> LoadAll()
        => [.. LoadAllInternal().Values.OrderBy(r => r.ModelId).ThenBy(r => r.ToolName)];

    public static List<ToolFitnessRecord> LoadForModel(string modelId)
    {
        var prefix = modelId.ToLowerInvariant().Split(':')[0] + "::";
        return LoadAllInternal()
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .OrderBy(r => r.ToolName)
            .ToList();
    }

    private static Dictionary<string, ToolFitnessRecord> LoadAllInternal()
    {
        if (!File.Exists(MapPath)) return [];
        try
        {
            var text = File.ReadAllText(MapPath);
            return JsonSerializer.Deserialize<Dictionary<string, ToolFitnessRecord>>(text, _json) ?? [];
        }
        catch { return []; }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Save or update a ToolFitnessRecord (full replace for its key).
    /// </summary>
    public static async Task SaveAsync(ToolFitnessRecord record)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapPath)!);
            var all = LoadAllInternal();
            all[Key(record.ModelId, record.ToolName)] = record;
            await File.WriteAllTextAsync(MapPath, JsonSerializer.Serialize(all, _json));
        }
        catch { /* non-fatal */ }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Append a single variant result to an existing or new record.
    /// </summary>
    public static async Task RecordVariantAsync(
        string        modelId,
        string        toolName,
        SchemaVariant variant)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapPath)!);
            var all = LoadAllInternal();
            var key = Key(modelId, toolName);

            if (all.TryGetValue(key, out var existing))
            {
                var updated = existing.Variants.ToList();
                updated.RemoveAll(v => v.VariantId == variant.VariantId);
                updated.Add(variant);
                all[key] = existing with { Variants = updated, UpdatedAt = DateTime.UtcNow };
            }
            else
            {
                all[key] = new ToolFitnessRecord(
                    ModelId:   modelId,
                    ToolName:  toolName,
                    Variants:  [variant],
                    UpdatedAt: DateTime.UtcNow);
            }

            await File.WriteAllTextAsync(MapPath, JsonSerializer.Serialize(all, _json));
        }
        catch { /* non-fatal */ }
        finally { _lock.Release(); }
    }

    // ── Promotion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Promotes all winning variants from the fitness map into SchemaLibrary
    /// so they are used automatically by AgentLoop on future sessions.
    /// Returns the number of schemas promoted.
    /// </summary>
    public static async Task<int> PromoteWinnersAsync(string modelId)
    {
        var records = LoadForModel(modelId);
        var promoted = 0;

        foreach (var record in records)
        {
            var winner = record.BestVariant;
            if (winner == null) continue;

            var format = ToolCallProfileStore.GetPreferredFormat(modelId);
            await SchemaLibrary.SaveConfirmedAsync(
                modelId:       record.ModelId,
                toolName:      record.ToolName,
                format:        format,
                schemaPayload: winner.SchemaPayload,
                successCount:  3);   // evolution pass counts as 3 confirmations
            promoted++;
        }

        return promoted;
    }
}
