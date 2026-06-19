// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Runs the local reviewer on swarm-staged output files.
/// Returns a GateResult in the same shape as ReviewGateService so the UI
/// surface (OnGateResult, ShowGateResult) works unchanged.
/// </summary>
public static class OllamaReviewService
{
    private static readonly Regex FindingRe = new(
        @"^(BLOCKER|MINOR)\s+(\S+):(\d+)\s+[—\-–]+\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static async Task<ReviewGateService.GateResult?> RunAsync(
        string            stagingDir,
        string            workspaceRoot,
        IModelRuntime     runtime,
        string            model   = "qwen2.5-coder:14b",
        string            focus   = "",
        CancellationToken ct      = default)
    {
        if (!Directory.Exists(stagingDir)) return null;

        var files = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories)
                             .OrderBy(f => f)
                             .ToArray();
        if (files.Length == 0) return null;

        var content  = await BuildFileContents(stagingDir, files, ct);
        var messages = BuildMessages(content, focus);
        var raw      = await CallRuntime(runtime, model, messages, ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (LooksLikeRuntimeError(raw)) return null;

        var reviewDir = Path.Combine(workspaceRoot, ".orc", "reviews");
        Directory.CreateDirectory(reviewDir);
        var outFile = Path.Combine(reviewDir,
            $"local_review_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        await File.WriteAllTextAsync(outFile,
            $"# Local Review — {DateTime.Now:yyyy-MM-dd HH:mm}\nModel: {model}\n\n{raw}", ct);

        return ParseVerdict(raw, outFile);
    }

    // ── private ──────────────────────────────────────────────────────────────

    private static async Task<string> BuildFileContents(
        string stagingDir, string[] files, CancellationToken ct)
    {
        const int MaxChars = 60_000;
        var sb     = new StringBuilder();
        var total  = 0;

        foreach (var f in files)
        {
            var rel     = Path.GetRelativePath(stagingDir, f).Replace('\\', '/');
            var text    = await File.ReadAllTextAsync(f, ct);
            var snippet = total + text.Length > MaxChars
                ? text[..(MaxChars - total)] + "\n// [truncated]"
                : text;

            sb.AppendLine($"=== {rel} ===");
            sb.AppendLine(snippet);
            sb.AppendLine();
            total += snippet.Length;
            if (total >= MaxChars) break;
        }

        return sb.ToString();
    }

    private static List<AgentMessage> BuildMessages(string fileContents, string focus) =>
    [
        new()
        {
            Role = MessageRole.System,
            Content = "You are a senior engineer reviewing new code files produced by an AI coding swarm.",
            Status = MessageStatus.Complete,
        },
        new()
        {
            Role = MessageRole.User,
            Content = $"""
        These are complete new files, not a diff.
        {(string.IsNullOrEmpty(focus) ? "" : $"\nExtra focus: {focus}\n")}
        Check for, in order of severity:
        1. Crash risk — null refs, async void exception paths, wrong init order
        2. Correctness — logic errors, off-by-one, type mismatches, missing awaits
        3. Security — command injection, path traversal, hardcoded secrets
        4. Resource leaks — undisposed streams, processes, connections
        5. API contract violations — wrong signatures, missing interface members

        For each issue output EXACTLY this format (one per line):
        BLOCKER <file>:<line> — <concise description>
        MINOR <file>:<line> — <concise description>

        If nothing is found, output:
        CLEAN — no significant issues

        Then end with:
        FINDINGS_SUMMARY:
        <repeat every BLOCKER/MINOR line, or "CLEAN — no significant issues">

        FILES TO REVIEW:
        {fileContents}
        """,
            Status = MessageStatus.Complete,
        },
    ];

    private static async Task<string?> CallRuntime(
        IModelRuntime runtime, string model, IReadOnlyList<AgentMessage> messages, CancellationToken ct)
    {
        try
        {
            var sb = new StringBuilder();
            await foreach (var token in runtime.StreamCompletionAsync(
                model, messages, temperature: 0.05, maxTokens: 8192, ct: ct))
                sb.Append(token);
            return sb.ToString();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static ReviewGateService.GateResult? ParseVerdict(string raw, string outFile)
    {
        var summaryIdx = raw.IndexOf("FINDINGS_SUMMARY:", StringComparison.OrdinalIgnoreCase);
        var searchIn   = summaryIdx >= 0 ? raw[summaryIdx..] : raw;

        var findings = new List<ReviewGateService.GateFinding>();
        foreach (Match m in FindingRe.Matches(searchIn))
            findings.Add(new ReviewGateService.GateFinding(
                m.Groups[1].Value,
                $"{m.Groups[2].Value}:{m.Groups[3].Value}",
                m.Groups[4].Value));

        if (findings.Count == 0 &&
            !Regex.IsMatch(searchIn, @"\bCLEAN\b", RegexOptions.IgnoreCase))
            return null;

        var verdict = findings.Any(f => f.Severity == "BLOCKER") ? ReviewGateService.GateVerdict.Blocker
                    : findings.Any(f => f.Severity == "MINOR")   ? ReviewGateService.GateVerdict.Minor
                    : ReviewGateService.GateVerdict.Clean;

        return new ReviewGateService.GateResult(verdict, findings, outFile, raw);
    }

    private static bool LooksLikeRuntimeError(string raw)
    {
        var trimmed = raw.TrimStart();
        return trimmed.StartsWith("[ERROR", StringComparison.OrdinalIgnoreCase);
    }
}
