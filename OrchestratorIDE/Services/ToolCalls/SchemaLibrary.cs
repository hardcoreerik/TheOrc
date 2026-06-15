// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Confirmed schema record ───────────────────────────────────────────────────

/// <summary>
/// A schema that has been successfully used with a specific model.
/// Stored in SchemaLibrary so AgentLoop can reuse it instead of the default ToolDefinition.
/// </summary>
public record ConfirmedSchema(
    string   ModelId,
    string   ToolName,
    string   FormatVariantName,    // FormatVariant enum name as string
    object   SchemaPayload,        // the exact Ollama API schema object that worked
    int      SuccessCount,         // how many times this schema was used successfully
    DateTime FirstConfirmedAt,
    DateTime LastUsedAt
)
{
    /// <summary>Has been used successfully at least MinConfirmations times.</summary>
    public bool IsReliable => SuccessCount >= 2;
}

// ── Library ───────────────────────────────────────────────────────────────────

/// <summary>
/// GOBLIN MIND Phase 3 — Persistent Confirmed-Schema Store.
///
/// After a tool call succeeds for a specific model, the schema that was sent
/// is stored here. On future sessions AgentLoop checks this library first;
/// if a confirmed schema exists for (model, toolName) it is used instead of
/// the default ToolDefinition schema.
///
/// Storage: %AppData%\OrchestratorIDE\schema-library.json
/// Key: "{modelId}::{toolName}"
/// </summary>
public static class SchemaLibrary
{
    public static readonly string LibraryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "schema-library.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented        = true,
        Converters           = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly SemaphoreSlim _lock = new(1, 1);

    // ── Key helpers ───────────────────────────────────────────────────────────

    private static string Key(string modelId, string toolName)
        => $"{NormalizeModel(modelId)}::{toolName.ToLowerInvariant()}";

    /// <summary>
    /// Strip quantisation suffix so "qwen2.5-coder:14b-q4_K_M" matches
    /// stored entries for "qwen2.5-coder:14b".
    /// </summary>
    private static string NormalizeModel(string modelId)
    {
        var parts = modelId.Split(':');
        if (parts.Length < 2) return modelId.ToLowerInvariant();
        var tag = parts[1].Split('-')[0];
        return $"{parts[0].ToLowerInvariant()}:{tag}";
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the confirmed schema for (modelId, toolName), or null if not stored.
    /// Only returns schemas that have been confirmed at least once.
    /// </summary>
    public static ConfirmedSchema? GetBestSchema(string modelId, string toolName)
    {
        var all = LoadAllInternal();
        var key = Key(modelId, toolName);
        return all.TryGetValue(key, out var schema) ? schema : null;
    }

    public static List<ConfirmedSchema> LoadAll()
        => [.. LoadAllInternal().Values.OrderBy(s => s.ModelId).ThenBy(s => s.ToolName)];

    public static List<ConfirmedSchema> LoadForModel(string modelId)
    {
        var norm = NormalizeModel(modelId);
        return LoadAllInternal()
            .Where(kv => kv.Key.StartsWith(norm + "::", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value)
            .OrderBy(s => s.ToolName)
            .ToList();
    }

    private static Dictionary<string, ConfirmedSchema> LoadAllInternal()
    {
        if (!File.Exists(LibraryPath)) return [];
        try
        {
            var text = File.ReadAllText(LibraryPath);
            return JsonSerializer.Deserialize<Dictionary<string, ConfirmedSchema>>(text, _json) ?? [];
        }
        catch { return []; }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Record a successful tool call — increments SuccessCount or creates a new entry.
    /// Called by AgentLoop after parsing a successful tool call response.
    /// </summary>
    public static async Task RecordSuccessAsync(
        string       modelId,
        string       toolName,
        FormatVariant format,
        object       schemaPayload)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
            var all = LoadAllInternal();
            var key = Key(modelId, toolName);

            if (all.TryGetValue(key, out var existing))
            {
                all[key] = existing with
                {
                    SuccessCount = existing.SuccessCount + 1,
                    LastUsedAt   = DateTime.UtcNow,
                    SchemaPayload = schemaPayload,   // always keep the latest payload
                };
            }
            else
            {
                all[key] = new ConfirmedSchema(
                    ModelId:           modelId,
                    ToolName:          toolName,
                    FormatVariantName: format.ToString(),
                    SchemaPayload:     schemaPayload,
                    SuccessCount:      1,
                    FirstConfirmedAt:  DateTime.UtcNow,
                    LastUsedAt:        DateTime.UtcNow);
            }

            await File.WriteAllTextAsync(LibraryPath, JsonSerializer.Serialize(all, _json));
        }
        catch { /* non-fatal */ }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Explicitly save a confirmed schema (e.g. from probe results or evolution runs).
    /// </summary>
    public static async Task SaveConfirmedAsync(
        string        modelId,
        string        toolName,
        FormatVariant format,
        object        schemaPayload,
        int           successCount = 1)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath)!);
            var all = LoadAllInternal();
            var key = Key(modelId, toolName);

            all[key] = new ConfirmedSchema(
                ModelId:           modelId,
                ToolName:          toolName,
                FormatVariantName: format.ToString(),
                SchemaPayload:     schemaPayload,
                SuccessCount:      successCount,
                FirstConfirmedAt:  DateTime.UtcNow,
                LastUsedAt:        DateTime.UtcNow);

            await File.WriteAllTextAsync(LibraryPath, JsonSerializer.Serialize(all, _json));
        }
        catch { /* non-fatal */ }
        finally { _lock.Release(); }
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public static int CountForModel(string modelId)
        => LoadForModel(modelId).Count;

    public static int CountReliable(string modelId)
        => LoadForModel(modelId).Count(s => s.IsReliable);
}
