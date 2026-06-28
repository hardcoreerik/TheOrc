// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.ContextFabric;

public static class ContextFabricBenchmarkExpansionWriter
{
    private static readonly JsonSerializerOptions _json = new(FabricJson.Options)
    {
        WriteIndented = true,
    };

    public static async Task<(string JsonPath, string MarkdownPath)> WriteQuoteAnchoringAsync(
        FabricQuoteAnchorReport report,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return await WriteAsync(
            report,
            BuildQuoteAnchoringMarkdown(report),
            outputDirectory,
            "cf0_quote_anchor",
            ct).ConfigureAwait(false);
    }

    public static async Task<(string JsonPath, string MarkdownPath)> WriteBoundaryStitchAsync(
        FabricBoundaryStitchReport report,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return await WriteAsync(
            report,
            BuildBoundaryStitchMarkdown(report),
            outputDirectory,
            "cf0_stitch",
            ct).ConfigureAwait(false);
    }

    private static async Task<(string JsonPath, string MarkdownPath)> WriteAsync<T>(
        T report,
        string markdown,
        string outputDirectory,
        string prefix,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var stamp = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        var jsonPath = Path.Combine(root, $"{prefix}_{stamp}.json");
        var markdownPath = Path.Combine(root, $"{prefix}_{stamp}.md");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, _json), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, markdown, ct).ConfigureAwait(false);
        return (jsonPath, markdownPath);
    }

    private static string BuildQuoteAnchoringMarkdown(FabricQuoteAnchorReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Context Fabric Quote Anchoring Diagnostics");
        sb.AppendLine();
        sb.AppendLine($"Generated: {report.GeneratedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("| Case | Mode | Accepted | Token overlap |");
        sb.AppendLine("|---|---|---|---:|");
        foreach (var result in report.Results)
            sb.AppendLine($"| `{Escape(result.CaseId)}` | `{result.Mode}` | {(result.Accepted ? "yes" : "no")} | {result.TokenOverlap:F2} |");
        sb.AppendLine();
        foreach (var result in report.Results.Where(result => result.Errors.Count > 0))
        {
            sb.AppendLine($"## {Escape(result.CaseId)}");
            foreach (var error in result.Errors)
                sb.AppendLine($"- {Escape(error)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildBoundaryStitchMarkdown(FabricBoundaryStitchReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Context Fabric Boundary Stitch Diagnostics");
        sb.AppendLine();
        sb.AppendLine($"Runtime: `{Escape(report.RuntimeName)}`");
        sb.AppendLine($"Generated: {report.GeneratedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("| Case | Result | Duration |");
        sb.AppendLine("|---|---|---:|");
        foreach (var result in report.Results)
            sb.AppendLine($"| `{Escape(result.CaseId)}` | {(result.Passed ? "PASS" : "FAIL")} | {result.Metrics.DurationMs} ms |");
        sb.AppendLine();
        foreach (var result in report.Results)
        {
            sb.AppendLine($"## {Escape(result.CaseId)}");
            sb.AppendLine();
            sb.AppendLine(result.Summary.Length == 0 ? "_No summary produced._" : Escape(result.Summary));
            if (result.LinkedFacts.Count > 0)
            {
                sb.AppendLine();
                foreach (var fact in result.LinkedFacts)
                    sb.AppendLine($"- {Escape(fact)}");
            }
            if (result.Errors.Count > 0)
            {
                sb.AppendLine();
                foreach (var error in result.Errors)
                    sb.AppendLine($"- {Escape(error)}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Escape(string value) => value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
