// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.ContextFabric;

public static class ContextFabricBenchmarkGateWriter
{
    private static readonly JsonSerializerOptions Json = new(FabricJson.Options)
    {
        WriteIndented = true,
    };

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(
        FabricCf7BenchmarkGateReport report,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var stamp = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        var jsonPath = Path.Combine(root, $"cf7_gate_{stamp}.json");
        var markdownPath = Path.Combine(root, $"cf7_gate_{stamp}.md");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, Json), ct)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), ct)
            .ConfigureAwait(false);
        return (jsonPath, markdownPath);
    }

    public static string BuildMarkdown(FabricCf7BenchmarkGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("# Context Fabric CF-7 Benchmark Gate");
        sb.AppendLine();
        sb.AppendLine($"> Verdict: **{(report.ReadyForExpansion ? "GO" : "NO-GO")}**");
        sb.AppendLine($"> Corpus: `{Escape(report.CorpusId)}`");
        sb.AppendLine($"> Generation: `{Escape(report.GenerationId)}`");
        sb.AppendLine($"> Generated: {report.GeneratedUtc:O}");
        sb.AppendLine();

        sb.AppendLine("## B0-B4 Systems");
        sb.AppendLine();
        sb.AppendLine("| ID | System | Status | Detail |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var system in report.Systems)
            sb.AppendLine($"| `{Escape(system.SystemId)}` | {Escape(system.Label)} | `{system.Status}` | {Escape(system.Detail)} |");
        sb.AppendLine();

        sb.AppendLine("## Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value | Target | Result | Detail |");
        sb.AppendLine("|---|---:|---:|---|---|");
        foreach (var metric in report.Metrics)
            sb.AppendLine($"| `{Escape(metric.Name)}` | {metric.Value:0.###} | {metric.Target:0.###} | {(metric.Passed ? "PASS" : "FAIL")} | {Escape(metric.Detail)} |");
        sb.AppendLine();

        sb.AppendLine("## Gates");
        sb.AppendLine();
        sb.AppendLine("| Gate | Result | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (var gate in report.Gates)
            sb.AppendLine($"| `{Escape(gate.Name)}` | {(gate.Passed ? "PASS" : "FAIL")} | {Escape(gate.Detail)} |");
        return sb.ToString();
    }

    private static string Escape(string value) => value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("`", "'", StringComparison.Ordinal);
}
