using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.ToolCalls;

// ── Persisted profile record ─────────────────────────────────────────────────

/// <summary>
/// Persisted tool-call capability profile for one model.
/// Written by ToolCallProbeEngine, read by AgentLoop to select dispatch strategy.
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
    /// <summary>How long ago this profile was captured.</summary>
    public TimeSpan Age => DateTime.UtcNow - TestedAt;

    /// <summary>Should this profile be re-tested? (> 7 days old or unknown mode)</summary>
    public bool IsStale => Age.TotalDays > 7 || RecommendedMode == ToolCallMode.Unknown;

    /// <summary>One-line display summary.</summary>
    public string Summary =>
        $"native={NativePasses}/{TotalTests / 2}  text={TextPasses}/{TotalTests / 2}  → {RecommendedMode}  (tested {TestedAt:yyyy-MM-dd})";
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

        var profile = new ToolCallProfile(
            ModelId:         result.ModelId,
            TestedAt:        result.TestedAt,
            RecommendedMode: result.RecommendedMode,
            NativePasses:    result.NativePasses,
            TextPasses:      result.TextPasses,
            TotalTests:      result.TotalTests,
            TestPassMap:     testMap
        );
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
}
