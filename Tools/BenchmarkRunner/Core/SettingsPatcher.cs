using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BenchmarkRunner.Core;

public static class SettingsPatcher
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "settings.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // Trust level name → int (matches AppSettings TrustLevel enum)
    private static readonly Dictionary<string, int> TrustMap = new()
    {
        ["Plan"]     = 0,
        ["Guarded"]  = 1,
        ["Standard"] = 2,
        ["FullAuto"] = 3,
    };

    public static bool SettingsExist => File.Exists(SettingsPath);

    /// <summary>Reads the current settings JSON, patches the test fields, writes back.</summary>
    public static void Apply(string bossModel, string workerModel, int slots, string trust,
        string workspace, string mode = "swarm", string researcherModel = "")
    {
        if (!File.Exists(SettingsPath))
            throw new FileNotFoundException($"Settings not found — launch TheOrc at least once.\n{SettingsPath}");

        var raw  = File.ReadAllText(SettingsPath);
        var node = JsonNode.Parse(raw) as JsonObject
                   ?? throw new InvalidOperationException("settings.json is not a JSON object.");

        node["lastSwarmModel"]      = bossModel;
        node["lastWorkerModel"]     = workerModel;
        node["lastResearcherModel"] = string.IsNullOrWhiteSpace(researcherModel) ? workerModel : researcherModel;
        node["ollamaParallelSlots"] = slots;
        node["trustLevel"]          = TrustMap.GetValueOrDefault(trust, 2);
        node["lastMode"]            = mode;

        if (!string.IsNullOrWhiteSpace(workspace))
        {
            node["defaultWorkspace"] = workspace;

            // Prepend to recentWorkspaces (deduplicate, cap 10)
            var recent = node["recentWorkspaces"]?.AsArray() ?? [];
            var list   = recent.Select(x => x?.GetValue<string>() ?? "")
                               .Where(s => s != workspace).Prepend(workspace)
                               .Take(10).ToList();
            var arr = new JsonArray();
            foreach (var s in list) arr.Add(s);
            node["recentWorkspaces"] = arr;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, node.ToJsonString(Opts));
    }

    /// <summary>Reads current values to pre-populate the UI on startup.</summary>
    public static (string boss, string worker, string researcher, int slots, string trust, string workspace) Read()
    {
        if (!File.Exists(SettingsPath))
            return ("qwen2.5-coder:14b", "nemotron-3-nano:4b-q8_0", "nemotron-3-nano:4b-q4_k_m", 3, "Standard", "");

        try
        {
            var node       = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            var boss       = node?["lastSwarmModel"]?.GetValue<string>()      ?? "qwen2.5-coder:14b";
            var worker     = node?["lastWorkerModel"]?.GetValue<string>()     ?? "nemotron-3-nano:4b-q8_0";
            var researcher = node?["lastResearcherModel"]?.GetValue<string>() ?? worker;
            var slots      = node?["ollamaParallelSlots"]?.GetValue<int>()    ?? 3;
            var trustN     = node?["trustLevel"]?.GetValue<int>()             ?? 2;
            var trust      = TrustMap.FirstOrDefault(kv => kv.Value == trustN).Key ?? "Standard";
            var ws         = node?["defaultWorkspace"]?.GetValue<string>()    ?? "";
            return (boss, worker, researcher, slots, trust, ws);
        }
        catch { return ("qwen2.5-coder:14b", "nemotron-3-nano:4b-q8_0", "nemotron-3-nano:4b-q4_k_m", 3, "Standard", ""); }
    }
}
