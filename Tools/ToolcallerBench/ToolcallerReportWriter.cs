// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace ToolcallerBench;

public static class ToolcallerReportWriter
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(
        ToolcallerValidationReport report,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var stamp = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        var jsonPath = Path.Combine(root, $"toolcaller_validate_{stamp}.json");
        var markdownPath = Path.Combine(root, $"toolcaller_validate_{stamp}.md");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, _json), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), ct).ConfigureAwait(false);
        return (jsonPath, markdownPath);
    }

    public static string BuildMarkdown(ToolcallerValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("# Toolcaller v0 — Mechanical Validation Report");
        sb.AppendLine();
        sb.AppendLine($"> Verdict: **{(report.Passed ? "PASS" : "FAIL")}**");
        sb.AppendLine($"> Schema version: `{report.SchemaVersion}`");
        sb.AppendLine($"> Frozen tool schema hash: `{report.FrozenToolSchemaHash}`");
        sb.AppendLine($"> Generated: {report.GeneratedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| Total examples | {report.TotalExamples} |");
        sb.AppendLine($"| Passed | {report.PassedExamples} |");
        sb.AppendLine($"| Failed | {report.FailedExamples} |");
        sb.AppendLine();

        sb.AppendLine("## Coverage Note");
        sb.AppendLine();
        sb.AppendLine("This validator does not mechanically check two gates from " +
            "`training_pit/TOOLCALLER_CAPTURE_SCHEMA.md`: (1) whether `approval_state` " +
            "implies a call was already executed/approved by the model — this needs " +
            "reviewer judgment, not a keyword heuristic; (2) live cross-verification of " +
            "`policy_outcome` against a fresh `ToolPolicyEngine.Evaluate()` call — " +
            "`ToolPolicyEngine.cs` is only compiled into `OrchestratorIDE.Avalonia.csproj` " +
            "today, and this tool intentionally does not pull in that dependency. Only " +
            "self-consistency of `policy_outcome` (e.g. `evaluated` must be true for " +
            "`call` decisions) is checked here.");
        sb.AppendLine();

        if (report.Findings.Count > 0)
        {
            sb.AppendLine("## Findings");
            sb.AppendLine();
            sb.AppendLine("| Example | Gate | Severity | Detail |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var finding in report.Findings)
            {
                sb.AppendLine($"| `{Escape(finding.ExampleId)}` | `{Escape(finding.Gate)}` | " +
                    $"{finding.Severity} | {Escape(finding.Detail)} |");
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
