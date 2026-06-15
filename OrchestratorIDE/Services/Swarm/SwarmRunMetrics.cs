// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.Swarm;

// ── Data model ────────────────────────────────────────────────────────────────

public enum TesterVerdict { Skipped, Pass, Fail, Partial }

/// <summary>
/// Immutable record of a single completed swarm run.
/// Stored as one JSON line in the metrics log file.
/// </summary>
public record SwarmRunRecord(
    string   RunId,
    DateTime StartedAt,
    int      DurationSeconds,
    string   BossModel,
    string   CoderModel,
    string   ResearcherModel,
    int      TotalVramGb,           // detected at run time
    string   Goal,                  // first 120 chars
    bool     SwarmSucceeded,
    int      FilesWritten,
    int      GhostResearcherCount,  // researchers that failed the quality gate
    int      CoderRetryCount,       // times a coder was retried for zero files
    TesterVerdict Verdict,
    bool     FixTaskSpawned,
    bool     FixTaskSucceeded,
    int      BossScoreAtRunTime,    // profile score used (for calibration)
    int      CoderScoreAtRunTime
);

/// <summary>
/// Aggregate stats for a specific model configuration key.
/// </summary>
public record ConfigStats(
    string ConfigKey,   // "{boss}|{coder}|{researcher}"
    string BossModel,
    string CoderModel,
    string ResearcherModel,
    int    RunCount,
    double TesterPassRate,      // fraction of runs where Tester said PASS
    double AvgDurationSeconds,
    double AvgRetries,
    double SuccessRate,
    int    TotalVramGb
)
{
    /// <summary>Composite quality score (higher = better).</summary>
    public double QualityScore =>
        (TesterPassRate * 0.45) + (SuccessRate * 0.35) + (1.0 - Math.Min(AvgRetries / 5.0, 1.0)) * 0.20;
}

// ── Persistence ───────────────────────────────────────────────────────────────

/// <summary>
/// Appends run records to a JSONL file and provides analysis over accumulated runs.
/// Thread-safe for concurrent appends.
/// </summary>
public class SwarmMetricsStore
{
    private static readonly string MetricsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "swarm-metrics.jsonl");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented          = false,
        Converters             = { new JsonStringEnumConverter() },
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly SemaphoreSlim _lock = new(1, 1);

    // ── Write ─────────────────────────────────────────────────────────────────

    public static async Task AppendAsync(SwarmRunRecord record)
    {
        try
        {
            await _lock.WaitAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(MetricsPath)!);
            var line = JsonSerializer.Serialize(record, _json);
            await File.AppendAllTextAsync(MetricsPath, line + "\n");
        }
        catch { /* non-fatal — metrics are supplementary */ }
        finally { _lock.Release(); }
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public static List<SwarmRunRecord> LoadAll()
    {
        var result = new List<SwarmRunRecord>();
        if (!File.Exists(MetricsPath)) return result;
        try
        {
            foreach (var line in File.ReadAllLines(MetricsPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var r = JsonSerializer.Deserialize<SwarmRunRecord>(line, _json);
                    if (r is not null) result.Add(r);
                }
                catch { /* skip malformed lines */ }
            }
        }
        catch { }
        return result;
    }

    // ── Analysis ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Groups all recorded runs by {boss}|{coder}|{researcher} and returns
    /// aggregate stats sorted by QualityScore descending.
    /// </summary>
    public static List<ConfigStats> GetConfigStats(int minRuns = 1)
    {
        var records = LoadAll();
        return records
            .GroupBy(r => $"{r.BossModel}|{r.CoderModel}|{r.ResearcherModel}")
            .Where(g => g.Count() >= minRuns)
            .Select(g =>
            {
                var list     = g.ToList();
                var passRuns = list.Count(r => r.Verdict == TesterVerdict.Pass);
                return new ConfigStats(
                    ConfigKey:          g.Key,
                    BossModel:          list[0].BossModel,
                    CoderModel:         list[0].CoderModel,
                    ResearcherModel:    list[0].ResearcherModel,
                    RunCount:           list.Count,
                    TesterPassRate:     list.Count > 0 ? passRuns / (double)list.Count : 0,
                    AvgDurationSeconds: list.Average(r => r.DurationSeconds),
                    AvgRetries:         list.Average(r => r.CoderRetryCount),
                    SuccessRate:        list.Count > 0
                                            ? list.Count(r => r.SwarmSucceeded) / (double)list.Count
                                            : 0,
                    TotalVramGb:        (int)list.Average(r => r.TotalVramGb)
                );
            })
            .OrderByDescending(s => s.QualityScore)
            .ToList();
    }

    /// <summary>
    /// Returns the best-known config for the given VRAM budget, or null if
    /// no config has enough run history yet.
    /// </summary>
    public static ConfigStats? BestConfigForVram(int totalVramGb, int minRuns = 2)
    {
        return GetConfigStats(minRuns)
            .Where(s => s.TotalVramGb <= totalVramGb)
            .FirstOrDefault();
    }

    public static string MetricsFilePath => MetricsPath;
    public static int TotalRuns          => LoadAll().Count;
}
