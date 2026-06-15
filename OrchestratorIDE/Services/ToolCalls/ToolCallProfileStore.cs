// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Persisted profile record ─────────────────────────────────────────────────

/// <summary>
/// Persisted tool-call capability profile for one model.
/// Written by ToolCallProbeEngine, read by AgentLoop to select dispatch strategy.
///
/// Extended in GOBLIN MIND v1.1:
///   FormatProfile   — preferred serialization format (from FormatProbeEngine)
///   CategoryProfile — task-category boundary map (from CategoryProbeEngine)
///   Simplification  — schema simplification rules derived from probe failures
/// </summary>
public record ToolCallProfile(
    string         ModelId,
    DateTime       TestedAt,
    ToolCallMode   RecommendedMode,
    int            NativePasses,
    int            TextPasses,
    int            TotalTests,
    // Per-test pass flags: key = "BASIC_Native", value = true/false
    Dictionary<string, bool> TestPassMap
)
{
    // ── GOBLIN MIND Phase 1: Format Fingerprint ─────────────────────────────
    public FormatFingerprint?          FormatProfile      { get; init; }

    // ── GOBLIN MIND Phase 2: Category Boundary Map ──────────────────────────
    public CategoryBoundaryMap?        CategoryProfile    { get; init; }

    // ── GOBLIN MIND Phase 4: Schema Simplification Rules ────────────────────
    public SchemaSimplificationRules?  Simplification     { get; init; }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>How long ago this profile was captured.</summary>
    public TimeSpan Age => DateTime.UtcNow - TestedAt;

    /// <summary>Should this profile be re-tested? (> 7 days old or unknown mode)</summary>
    public bool IsStale => Age.TotalDays > 7 || RecommendedMode == ToolCallMode.Unknown;

    /// <summary>Has the full Goblin Mind profile been run (format + categories)?</summary>
    public bool HasGoblinMindProfile => FormatProfile != null && CategoryProfile != null;

    /// <summary>One-line display summary.</summary>
    public string Summary =>
        $"native={NativePasses}/{TotalTests / 2}  text={TextPasses}/{TotalTests / 2}  → {RecommendedMode}  (tested {TestedAt:yyyy-MM-dd})" +
        (FormatProfile  != null ? $"  fmt={FormatProfile.PreferredFormat}" : "") +
        (CategoryProfile != null ? $"  cats={CategoryProfile.ShortSummary}" : "");
}

// ── Store ────────────────────────────────────────────────────────────────────

/// <summary>
/// Reads and writes per-model tool-call profiles to a JSON file.
/// Used by AgentLoop to select the correct tool-call dispatch strategy.
/// Thread-safe for concurrent reads; writes are serialised via a semaphore.
/// </summary>
public static class ToolCallProfileStore
{
    public static readonly string ProfilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "tool-call-profiles.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented          = true,
        Converters             = { new JsonStringEnumConverter() },
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    };

    private static readonly SemaphoreSlim _lock = new(1, 1);

    // ── Write ─────────────────────────────────────────────────────────────────

    public static async Task SaveAsync(ToolCallProfile profile)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilesPath)!);
            var all = LoadAllInternal();
            all[profile.ModelId] = profile;
            await File.WriteAllTextAsync(ProfilesPath,
                JsonSerializer.Serialize(all, _json));
        }
        catch { /* non-fatal */ }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Convert a <see cref="ModelProbeResult"/> from the probe engine into a
    /// <see cref="ToolCallProfile"/> and save it.
    /// </summary>
    public static Task SaveFromProbeResultAsync(ModelProbeResult result)
    {
        var testMap = result.Outcomes.ToDictionary(
            o => $"{o.TestId}_{o.Mode}",
            o => o.Result == ProbeResult.Pass);

        // Preserve any existing GOBLIN MIND profile data so that re-running
        // the dispatch probe does not wipe previously recorded format/category info.
        var existing = Load(result.ModelId);

        var profile = new ToolCallProfile(
            ModelId:         result.ModelId,
            TestedAt:        result.TestedAt,
            RecommendedMode: result.RecommendedMode,
            NativePasses:    result.NativePasses,
            TextPasses:      result.TextPasses,
            TotalTests:      result.TotalTests,
            TestPassMap:     testMap
        )
        {
            FormatProfile    = existing?.FormatProfile,
            CategoryProfile  = existing?.CategoryProfile,
            Simplification   = existing?.Simplification,
        };
        return SaveAsync(profile);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public static ToolCallProfile? Load(string modelId)
    {
        var all = LoadAllInternal();
        // Exact match first
        if (all.TryGetValue(modelId, out var p)) return p;
        // Fuzzy: try base name (qwen2.5-coder:14b → qwen2.5-coder)
        var baseName = modelId.Split(':')[0];
        return all.Values.FirstOrDefault(v =>
            v.ModelId.StartsWith(baseName, StringComparison.OrdinalIgnoreCase));
    }

    public static List<ToolCallProfile> LoadAll()
        => [.. LoadAllInternal().Values.OrderBy(p => p.ModelId)];

    private static Dictionary<string, ToolCallProfile> LoadAllInternal()
    {
        if (!File.Exists(ProfilesPath))
            return [];
        try
        {
            var text = File.ReadAllText(ProfilesPath);
            return JsonSerializer.Deserialize<Dictionary<string, ToolCallProfile>>(text, _json) ?? [];
        }
        catch { return []; }
    }

    // ── AgentLoop query interface ─────────────────────────────────────────────

    /// <summary>
    /// Returns the recommended tool-call mode for <paramref name="modelId"/>.
    /// Falls back to <paramref name="profileDefault"/> when no tested profile exists.
    /// </summary>
    public static ToolCallMode GetMode(string modelId, bool profileDefault = true)
    {
        var profile = Load(modelId);
        if (profile == null || profile.RecommendedMode == ToolCallMode.Unknown)
            return profileDefault ? ToolCallMode.Native : ToolCallMode.TextJson;
        return profile.RecommendedMode;
    }

    /// <summary>
    /// True when the AgentLoop should pass a tools[] array to the API for this model.
    /// (Both Native and Both modes → send tools array.)
    /// </summary>
    public static bool ShouldSendNativeTools(string modelId, bool defaultValue = true)
    {
        var mode = GetMode(modelId, defaultValue);
        return mode is ToolCallMode.Native or ToolCallMode.Both or ToolCallMode.Unknown;
    }

    /// <summary>
    /// True when the AgentLoop should include the text-JSON tool format
    /// instructions in the system prompt for this model.
    /// </summary>
    public static bool ShouldUseTextJson(string modelId, bool defaultValue = false)
    {
        var mode = GetMode(modelId, !defaultValue);
        return mode is ToolCallMode.TextJson or ToolCallMode.Both;
    }

    // ── GOBLIN MIND query interface ───────────────────────────────────────────

    /// <summary>
    /// Returns the format fingerprint for a model, or null if not yet probed.
    /// Used by AgentLoop to pick the right text-JSON system prompt format.
    /// </summary>
    public static FormatFingerprint? GetFormatFingerprint(string modelId)
        => Load(modelId)?.FormatProfile;

    /// <summary>
    /// Returns the preferred FormatVariant for a model.
    /// Falls back to BareJson (safest default) when no fingerprint exists.
    /// </summary>
    public static FormatVariant GetPreferredFormat(string modelId)
        => Load(modelId)?.FormatProfile?.PreferredFormat ?? FormatVariant.BareJson;

    /// <summary>
    /// Returns the category boundary map for a model, or null if not yet probed.
    /// Used by SwarmSession to gate task routing.
    /// </summary>
    public static CategoryBoundaryMap? GetCategoryMap(string modelId)
        => Load(modelId)?.CategoryProfile;

    /// <summary>
    /// Returns schema simplification rules for a model.
    /// Null means no simplification needed (or not yet determined).
    /// </summary>
    public static SchemaSimplificationRules? GetSimplificationRules(string modelId)
        => Load(modelId)?.Simplification;

    /// <summary>
    /// Save updated format fingerprint onto an existing profile (or create one).
    /// Preserves all other profile data.
    /// </summary>
    public static async Task SaveFormatFingerprintAsync(string modelId, FormatFingerprint fp)
    {
        var existing = Load(modelId) ?? new ToolCallProfile(
            ModelId: modelId, TestedAt: DateTime.UtcNow,
            RecommendedMode: ToolCallMode.Unknown,
            NativePasses: 0, TextPasses: 0, TotalTests: 0,
            TestPassMap: []);
        await SaveAsync(existing with { FormatProfile = fp });
    }

    /// <summary>
    /// Save updated category map onto an existing profile (or create one).
    /// Preserves all other profile data including format fingerprint.
    /// Also derives and saves schema simplification rules.
    /// </summary>
    public static async Task SaveCategoryMapAsync(string modelId, CategoryBoundaryMap map)
    {
        var existing = Load(modelId) ?? new ToolCallProfile(
            ModelId: modelId, TestedAt: DateTime.UtcNow,
            RecommendedMode: ToolCallMode.Unknown,
            NativePasses: 0, TextPasses: 0, TotalTests: 0,
            TestPassMap: []);
        var rules = SchemaSimplifier.DeriveRules(existing);
        await SaveAsync(existing with { CategoryProfile = map, Simplification = rules });
    }
}
