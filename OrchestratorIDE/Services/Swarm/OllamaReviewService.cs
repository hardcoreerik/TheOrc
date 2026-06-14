using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Runs the local Ollama reviewer on swarm-staged output files.
/// Returns a GateResult in the same shape as ReviewGateService so the UI
/// surface (OnGateResult, ShowGateResult) works unchanged.
/// </summary>
public static class OllamaReviewService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(600) };

    private static readonly Regex FindingRe = new(
        @"^(BLOCKER|MINOR)\s+(\S+):(\d+)\s+[—\-–]+\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static async Task<ReviewGateService.GateResult?> RunAsync(
        string            stagingDir,
        string            workspaceRoot,
        string            model   = "qwen2.5-coder:14b",
        string            baseUrl = "http://localhost:11434",
        string            focus   = "",
        CancellationToken ct      = default)
    {
        if (!Directory.Exists(stagingDir)) return null;

        var files = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories)
                             .OrderBy(f => f)
                             .ToArray();
        if (files.Length == 0) return null;

        var content  = await BuildFileContents(stagingDir, files, ct);
        var prompt   = BuildPrompt(content, focus);
        var raw      = await CallOllama(baseUrl, model, prompt, ct);
        if (raw is null) return null;

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

    private static string BuildPrompt(string fileContents, string focus) => $"""
        You are a senior engineer reviewing new code files produced by an AI coding swarm.
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
        """;

    private static async Task<string?> CallOllama(
        string baseUrl, string model, string prompt, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                model,
                prompt,
                stream  = false,
                options = new { temperature = 0.05, num_ctx = 32768 }
            });

            var resp = await _http.PostAsync(
                $"{baseUrl.TrimEnd('/')}/api/generate",
                new StringContent(body, Encoding.UTF8, "application/json"),
                ct);

            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("response").GetString();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static ReviewGateService.GateResult ParseVerdict(string raw, string outFile)
    {
        var summaryIdx = raw.IndexOf("FINDINGS_SUMMARY:", StringComparison.OrdinalIgnoreCase);
        var searchIn   = summaryIdx >= 0 ? raw[summaryIdx..] : raw;

        var findings = new List<ReviewGateService.GateFinding>();
        foreach (Match m in FindingRe.Matches(searchIn))
            findings.Add(new ReviewGateService.GateFinding(
                m.Groups[1].Value,
                $"{m.Groups[2].Value}:{m.Groups[3].Value}",
                m.Groups[4].Value));

        var verdict = findings.Any(f => f.Severity == "BLOCKER") ? ReviewGateService.GateVerdict.Blocker
                    : findings.Any(f => f.Severity == "MINOR")   ? ReviewGateService.GateVerdict.Minor
                    : ReviewGateService.GateVerdict.Clean;

        return new ReviewGateService.GateResult(verdict, findings, outFile, raw);
    }
}
