// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Persists a ModelBenchReport as one timestamped JSON file per run -- the same convention as
/// NativeRuntimeComparisonReportStore (native-vs-Ollama parity reports): a workspace-relative
/// .orc/&lt;feature&gt;/ folder when a workspace is open, falling back to %APPDATA%\OrchestratorIDE\
/// .orc\&lt;feature&gt;\ otherwise. Deliberately NOT SQLite -- see docs/FOUNDRY_ARENA.md's stated
/// preference for a single immutable JSON artifact per evaluation run over a new tracking
/// service/database.
/// </summary>
public static class ModelBenchReportStore
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static string ResolveDirectory(string? workspaceRoot)
    {
        var root = !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? Path.Combine(workspaceRoot, ".orc", "model-bench")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", ".orc", "model-bench");

        Directory.CreateDirectory(root);
        return root;
    }

    public static async Task<string> WriteAsync(
        ModelBenchReport report,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        var root = ResolveDirectory(workspaceRoot);
        var path = Path.Combine(root, $"model_bench_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
        var payload = new
        {
            schema_version = "1",
            report.CorpusName,
            generated_utc = report.GeneratedUtc.ToString("O"),
            report.ModelsTested,
            report.Summaries,
            report.Results,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, _json), ct);
        return path;
    }

    /// <summary>
    /// Loads every persisted report in the resolved directory, newest first. Best-effort --
    /// a corrupt/unreadable file is skipped rather than failing the whole load, since this is
    /// evaluation history, not something a caller should crash over.
    /// </summary>
    public static List<ModelBenchReport> LoadAll(string? workspaceRoot = null)
    {
        var root    = ResolveDirectory(workspaceRoot);
        var reports = new List<(string Path, ModelBenchReport Report)>();

        foreach (var file in Directory.GetFiles(root, "model_bench_*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root2 = doc.RootElement;

                var corpusName  = root2.GetProperty("corpusName").GetString() ?? "";
                var generated   = DateTimeOffset.Parse(root2.GetProperty("generated_utc").GetString()!);
                var models      = root2.GetProperty("modelsTested").EnumerateArray()
                                        .Select(e => e.GetString() ?? "").ToList();
                var summaries   = JsonSerializer.Deserialize<List<ModelBenchModelSummary>>(
                                        root2.GetProperty("summaries").GetRawText(), _json) ?? [];
                var results     = JsonSerializer.Deserialize<List<ModelBenchCaseResult>>(
                                        root2.GetProperty("results").GetRawText(), _json) ?? [];

                reports.Add((file, new ModelBenchReport(corpusName, generated, models, results, summaries)));
            }
            catch
            {
                // Skip unreadable/corrupt files -- evaluation history, not critical state.
            }
        }

        return reports.OrderByDescending(r => r.Report.GeneratedUtc).Select(r => r.Report).ToList();
    }
}
