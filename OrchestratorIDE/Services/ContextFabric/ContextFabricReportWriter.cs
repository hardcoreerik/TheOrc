// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.ContextFabric;

public static class ContextFabricReportWriter
{
    private static readonly JsonSerializerOptions _json = new(FabricJson.Options)
    {
        WriteIndented = true,
    };

    public static async Task<(string JsonPath, string MarkdownPath)> WriteAsync(
        FabricFeasibilityReport report,
        string outputDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        var stamp = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        var jsonPath = Path.Combine(root, $"cf0_{stamp}.json");
        var markdownPath = Path.Combine(root, $"cf0_{stamp}.md");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, _json), ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), ct).ConfigureAwait(false);
        return (jsonPath, markdownPath);
    }

    public static string BuildMarkdown(FabricFeasibilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("# Context Fabric CF-0 Feasibility Report");
        sb.AppendLine();
        sb.AppendLine($"> Verdict: **{(report.Passed ? "PASS" : "FAIL")}**");
        sb.AppendLine($"> Runtime: `{Escape(report.RuntimeName)}`");
        sb.AppendLine($"> Corpus generation: `{report.GenerationId}`");
        sb.AppendLine($"> Generated: {report.GeneratedUtc:O}");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Result |");
        sb.AppendLine("|---|---:|");
        sb.AppendLine($"| Accepted segments | {report.Summary.AcceptedSegments} / {report.Summary.ExpectedSegments} |");
        sb.AppendLine($"| Verified questions | {report.Summary.PassedQuestions} / {report.Summary.TotalQuestions} |");
        sb.AppendLine($"| Estimated source tokens | {report.Summary.EstimatedSourceTokens:N0} |");
        sb.AppendLine($"| Maximum prompt tokens | {report.Summary.MaximumPromptTokens:N0} |");
        sb.AppendLine($"| Source / working-context ratio | {report.Summary.SourceToWorkingContextRatio:F2}x |");
        sb.AppendLine($"| Duration | {TimeSpan.FromMilliseconds(report.Summary.DurationMs):g} |");
        sb.AppendLine();
        if (report.Environment?.Lanes.Count > 0)
        {
            sb.AppendLine("## Benchmark Environment");
            sb.AppendLine();
            sb.AppendLine("| Role | Model | Admission | Family | Params |");
            sb.AppendLine("|---|---|---|---|---:|");
            foreach (var lane in report.Environment.Lanes)
            {
                var parameters = lane.ParametersB is double value ? $"{value:0.#}B" : "unknown";
                sb.AppendLine($"| `{Escape(lane.Role)}` | `{Escape(lane.ModelDisplayName)}` | `{lane.AdmissionVerdict}` | `{Escape(lane.FamilyLabel)}` | {parameters} |");
                foreach (var reason in lane.Reasons)
                    sb.AppendLine($"Reason ({Escape(lane.Role)}): {Escape(reason)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Gates");
        sb.AppendLine();
        sb.AppendLine("| Gate | Result | Blocking? | Detail |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var gate in report.Gates)
            sb.AppendLine($"| `{Escape(gate.Name)}` | {(gate.Passed ? "PASS" : "FAIL")} | {(gate.IsBlocking ? "yes" : "no")} | {Escape(gate.Detail)} |");

        sb.AppendLine();
        sb.AppendLine("## Questions");
        sb.AppendLine();
        foreach (var result in report.QuestionResults)
        {
            sb.AppendLine($"### {Escape(result.Question.QuestionId)} - {(result.Verification.Passed ? "PASS" : "FAIL")}");
            sb.AppendLine();
            AppendFencedText(sb, result.Answer?.Answer ?? "No answer was produced.");
            sb.AppendLine();
            sb.AppendLine($"Evidence coverage: {result.IncludedSegmentIds.Count} segment(s); citation precision: {result.Verification.CitationPrecision:P1}.");
            if (result.Verification.Errors.Count > 0)
            {
                sb.AppendLine();
                foreach (var error in result.Verification.Errors)
                    sb.AppendLine($"- {Escape(error)}");
            }
            sb.AppendLine();
        }

        var failedCalls = report.Calls.Where(call => !call.Succeeded).ToArray();
        if (failedCalls.Length > 0)
        {
            sb.AppendLine("## Failed Calls");
            sb.AppendLine();
            foreach (var call in failedCalls)
            {
                sb.AppendLine($"### {Escape(call.Stage)}/{Escape(call.ItemId)}");
                sb.AppendLine();
                sb.AppendLine($"Role: `{call.Role}`");
                sb.AppendLine($"Prompt path: `{Escape(call.PromptPath ?? "unknown")}`");
                if (!string.IsNullOrWhiteSpace(call.Error))
                    sb.AppendLine($"Error: {Escape(call.Error)}");
                if (!string.IsNullOrWhiteSpace(call.RawOutputExcerpt))
                {
                    sb.AppendLine();
                    AppendFencedText(sb, call.RawOutputExcerpt);
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Escape(string value) => value
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static void AppendFencedText(StringBuilder sb, string value)
    {
        var longestRun = 0;
        var currentRun = 0;
        foreach (var character in value)
        {
            currentRun = character == '`' ? currentRun + 1 : 0;
            longestRun = Math.Max(longestRun, currentRun);
        }

        var fence = new string('`', Math.Max(3, longestRun + 1));
        sb.AppendLine(fence + "text");
        sb.AppendLine(value);
        sb.AppendLine(fence);
    }
}
