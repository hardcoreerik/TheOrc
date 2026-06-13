using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Runs the Reviewer Quality Gate on a swarm staging directory by invoking
/// gate-review.ps1 (which calls Codex CLI in a temp git repo). Returns a
/// structured verdict the UI can surface before the user applies staged output.
/// </summary>
public static class ReviewGateService
{
    public enum GateVerdict { Clean, Minor, Blocker }

    public sealed record GateFinding(string Severity, string Location, string Message);

    public sealed record GateResult(
        GateVerdict                Verdict,
        IReadOnlyList<GateFinding> Findings,
        string                     VerdictFilePath,
        string                     RawVerdict);

    private static readonly Regex FindingRe = new(
        @"^(BLOCKER|MINOR)\s+(.+?)\s+—\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Runs gate-review.ps1 on <paramref name="stagingDir"/> and returns a parsed verdict.
    /// Returns null if the script or Codex CLI is not available (non-fatal: gate is advisory).
    /// </summary>
    public static async Task<GateResult?> RunAsync(
        string            stagingDir,
        string            workspaceRoot,
        string            focus  = "",
        CancellationToken ct     = default)
    {
        var script = ResolveScript(workspaceRoot);
        if (script is null) return null;

        var reviewDir   = Path.Combine(workspaceRoot, ".orc", "reviews");
        var beforeFiles = Directory.Exists(reviewDir)
            ? Directory.GetFiles(reviewDir, "gate_*.md").ToHashSet()
            : (HashSet<string>)[];

        var psi = new ProcessStartInfo
        {
            FileName               = "powershell",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in BuildArgs(script, stagingDir, focus))
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start powershell for gate review");

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var stdout = await stdoutTask;

        // gate-review.ps1 exit 3 = codex not found
        if (p.ExitCode == 3) return null;

        // Prefer the file written by the script over raw stdout (richer content)
        var verdictFile = Directory.Exists(reviewDir)
            ? Directory.GetFiles(reviewDir, "gate_*.md")
                .Where(f => !beforeFiles.Contains(f))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        var raw = verdictFile is not null
            ? await File.ReadAllTextAsync(verdictFile, ct)
            : stdout;

        return ParseVerdict(raw, verdictFile ?? "");
    }

    private static string? ResolveScript(string workspaceRoot)
    {
        var candidates = new[]
        {
            Path.Combine(workspaceRoot, "Tools", "gate-review.ps1"),
            Path.GetFullPath(Path.Combine(workspaceRoot, "..", "Tools", "gate-review.ps1")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> BuildArgs(string script, string stagingDir, string focus)
    {
        yield return "-NoProfile";
        yield return "-ExecutionPolicy";
        yield return "Bypass";
        yield return "-File";
        yield return script;
        yield return "-StagingDir";
        yield return stagingDir;
        if (!string.IsNullOrEmpty(focus))
        {
            yield return "-Focus";
            yield return focus;
        }
    }

    private static GateResult ParseVerdict(string raw, string verdictFile)
    {
        var findings = new List<GateFinding>();
        foreach (Match m in FindingRe.Matches(raw))
            findings.Add(new GateFinding(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));

        var verdict = findings.Any(f => f.Severity == "BLOCKER") ? GateVerdict.Blocker
                    : findings.Any(f => f.Severity == "MINOR")   ? GateVerdict.Minor
                    : GateVerdict.Clean;

        return new GateResult(verdict, findings, verdictFile, raw);
    }
}
