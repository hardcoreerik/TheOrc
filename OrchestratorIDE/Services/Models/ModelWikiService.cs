using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Swarm;
using OrchestratorIDE.Services.ToolCalls;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Merges model information from all available sources into ModelWikiEntry objects:
///   1. ModelProfiles   — built-in scores, capabilities, descriptions
///   2. Installed list  — OllamaClient.GetInstalledModelsAsync result
///   3. ToolCallProfiles — GOBLIN MIND probe results (format + categories)
///   4. SwarmMetrics    — historical swarm run records
///   5. Built-in observations — model-wiki-observations.json (embedded resource)
///   6. User test results — %APPDATA%/OrchestratorIDE/model-wiki/results.jsonl
/// </summary>
public static class ModelWikiService
{
    // ── Paths ─────────────────────────────────────────────────────────────────

    public static readonly string UserResultsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "model-wiki", "results.jsonl");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented          = false,
        Converters             = { new JsonStringEnumConverter() },
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a wiki entry for every model in ModelProfiles, merging in
    /// all available local data. <paramref name="installedModels"/> is the
    /// list returned by OllamaClient.GetInstalledModelsAsync.
    /// </summary>
    public static List<ModelWikiEntry> BuildAll(IReadOnlyCollection<string> installedModels)
    {
        var observations = LoadBuiltInObservations();
        var userResults  = LoadUserResults();
        var swarmAll     = SwarmMetricsStore.LoadAll();
        var probeAll     = ToolCallProfileStore.LoadAll();

        var entries = new List<ModelWikiEntry>();

        foreach (var (id, profile) in ModelProfiles.All)
        {
            entries.Add(BuildEntry(id, profile, installedModels,
                observations, userResults, swarmAll, probeAll));
        }

        // Also add any installed models not in the catalog (unknown profiles)
        foreach (var installed in installedModels)
        {
            if (entries.Any(e => string.Equals(e.ModelId, installed,
                    StringComparison.OrdinalIgnoreCase))) continue;

            var profile = ModelProfiles.Get(installed);  // returns defaults
            entries.Add(BuildEntry(installed, profile, installedModels,
                observations, userResults, swarmAll, probeAll));
        }

        return entries.OrderBy(e => e.DisplayName).ToList();
    }

    /// <summary>Build a single wiki entry for one model.</summary>
    public static ModelWikiEntry BuildEntry(
        string modelId,
        IReadOnlyCollection<string> installedModels)
    {
        var observations = LoadBuiltInObservations();
        var userResults  = LoadUserResults();
        var swarmAll     = SwarmMetricsStore.LoadAll();
        var probeAll     = ToolCallProfileStore.LoadAll();
        return BuildEntry(modelId, ModelProfiles.Get(modelId), installedModels,
            observations, userResults, swarmAll, probeAll);
    }

    // ── Routing recommendation ─────────────────────────────────────────────────

    /// <summary>
    /// Derive a plain-English routing recommendation for a model wiki entry.
    /// </summary>
    public static RoutingRecommendation GetRoutingRecommendation(ModelWikiEntry entry)
    {
        var p   = entry.Profile;
        var rec = new RoutingRecommendation();

        rec.Boss       = ScoreToLabel(p.BossScore);
        rec.Coder      = entry.HasLongWriteWarning
                            ? (p.CoderScore >= 6 ? "Limited" : "No")
                            : ScoreToLabel(p.CoderScore);
        rec.Researcher = ScoreToLabel(p.ResearcherScore);
        rec.Tester     = ScoreToLabel(p.TesterScore);

        // Single-agent Execute: needs reliable coder + long write_file
        rec.SingleAgent = entry.HasLongWriteWarning
            ? "Limited"
            : (p.CoderScore >= 6 ? "Yes" : "Limited");

        // Swarm worker: any role can work; boss needs BossScore, worker needs CoderScore
        rec.SwarmWorker = p.CoderScore >= 6 || p.ResearcherScore >= 6 || p.TesterScore >= 6
            ? "Yes" : "Limited";

        // Long write_file: warn if any observation flags it, or if CoderScore < 4
        rec.LongWriteFile = entry.HasLongWriteWarning ? "No"
            : p.CoderScore >= 7 ? "Yes"
            : p.CoderScore >= 5 ? "Limited"
            : "No";

        rec.Summary = BuildSummary(entry, rec);
        return rec;
    }

    /// <summary>Derive a LoRA/QLoRA suitability note for the model.</summary>
    public static string GetLoraGuidance(ModelWikiEntry entry)
    {
        var p = entry.Profile;

        if (p.ParamsBillions <= 0) return "Parameter count unknown — LoRA/QLoRA suitability cannot be determined.";

        if (p.ParamsBillions <= 4)
            return
                $"⚠ Small model ({p.ParamsBillions}B active parameters).\n\n" +
                "LoRA/QLoRA can improve format adherence and role behavior for narrow tasks " +
                "(e.g. short tester verdicts, log summaries). " +
                "LoRA/QLoRA probably cannot turn a 4B model into a reliable long-file autonomous coder — " +
                "the parameter ceiling limits context state for large JSON payloads regardless of adapter weights.\n\n" +
                "Best adapter target for this model: narrow tester or log-summarizer behavior, not primary coder.\n\n" +
                "Note: TheOrc's first Training Pit LoRA target is boss-planning behavior on the Gemma 4 QAT model (Phase 3 — blocked until 150 training examples are collected).";

        if (p.ParamsBillions <= 12)
            return
                $"✅ Medium model ({p.ParamsBillions}B parameters) — reasonable LoRA/QLoRA target.\n\n" +
                "QLoRA NF4 training fits in 12–14 GB VRAM on the current hardware (RTX 5070 Ti 16 GB). " +
                "LoRA can improve specific role behaviors: boss planning, structured JSON output, tool-call adherence.\n\n" +
                "Note: TheOrc's first Training Pit LoRA target is boss-planning behavior on gemma4:12b QAT (Phase 3 — blocked until 150 training examples are collected).";

        return
            $"✅ Large model ({p.ParamsBillions}B parameters) — LoRA/QLoRA possible but VRAM-intensive.\n\n" +
            "Standard LoRA on models over 14B requires 24 GB+ VRAM. Use QLoRA (NF4) to reduce VRAM to ~14 GB at cost of some quality. " +
            "For the current hardware (RTX 5070 Ti 16 GB), models over 14B should use QLoRA with paged optimizer.";
    }

    // ── Save / load user results ───────────────────────────────────────────────

    public static async Task SaveUserResultAsync(ModelCapabilityTestResult result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserResultsPath)!);
            var line = JsonSerializer.Serialize(result, _json) + "\n";
            await File.AppendAllTextAsync(UserResultsPath, line);
        }
        catch { /* non-fatal */ }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static ModelWikiEntry BuildEntry(
        string modelId,
        ModelProfile profile,
        IReadOnlyCollection<string> installedModels,
        List<ModelObservation> observations,
        List<ModelCapabilityTestResult> userResults,
        List<SwarmRunRecord> swarmAll,
        List<ToolCallProfile> probeAll)
    {
        bool installed = installedModels.Any(m =>
            string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase));

        // Model-specific observations (exact + fuzzy match on base name)
        var modelObs = observations.Where(o =>
            string.Equals(o.ModelId, modelId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(o.ModelId, modelId.Split(':')[0], StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Swarm runs involving this model (as boss, coder, or researcher)
        var modelSwarm = swarmAll.Where(r =>
            r.BossModel       == modelId ||
            r.CoderModel      == modelId ||
            r.ResearcherModel == modelId)
            .ToList();

        // User-run capability test results for this model
        var modelTests = userResults.Where(r =>
            string.Equals(r.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Timestamp)
            .ToList();

        // Probe profile (GOBLIN MIND)
        var probe = probeAll.FirstOrDefault(p =>
            string.Equals(p.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

        return new ModelWikiEntry
        {
            ModelId        = modelId,
            DisplayName    = profile.Name,
            IsInstalled    = installed,
            Profile        = profile,
            ProbeProfile   = probe,
            SwarmRuns      = modelSwarm,
            Observations   = modelObs,
            CapabilityTests = modelTests,
        };
    }

    private static List<ModelObservation> LoadBuiltInObservations()
    {
        try
        {
            var asm  = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.Contains("model-wiki-observations"));
            if (name == null) return [];
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<List<ModelObservation>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch { return []; }
    }

    private static List<ModelCapabilityTestResult> LoadUserResults()
    {
        var results = new List<ModelCapabilityTestResult>();
        if (!File.Exists(UserResultsPath)) return results;
        try
        {
            foreach (var line in File.ReadAllLines(UserResultsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<ModelCapabilityTestResult>(line, _json);
                    if (r != null) results.Add(r);
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { }
        return results;
    }

    private static string ScoreToLabel(int score) => score switch
    {
        >= 7 => "Yes",
        >= 5 => "Limited",
        _    => "No",
    };

    private static string BuildSummary(ModelWikiEntry entry, RoutingRecommendation rec)
    {
        var parts = new List<string>();
        var p = entry.Profile;

        // Opening line from description
        if (!string.IsNullOrWhiteSpace(p.Description))
            parts.Add(p.Description.Split('.')[0] + ".");

        // Role capability summary
        var good = new List<string>();
        if (rec.Boss       == "Yes") good.Add("boss planning");
        if (rec.Coder      == "Yes") good.Add("coding");
        if (rec.Researcher == "Yes") good.Add("research");
        if (rec.Tester     == "Yes") good.Add("testing");
        if (good.Count > 0)
            parts.Add($"Good for: {string.Join(", ", good)}.");

        // Long write_file warning
        if (entry.HasLongWriteWarning)
            parts.Add("⚠ Not recommended as primary single-agent Execute coder for multi-file generation — local evidence shows truncated write_file JSON.");

        // Observation summary (first observed failure or pass)
        var notable = entry.Observations.FirstOrDefault(o => o.Result is "failed" or "fail" or "pass");
        if (notable != null)
            parts.Add($"Local evidence ({notable.Source}): {notable.Summary}");

        return string.Join(" ", parts);
    }
}
