// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using OrchestratorIDE.Tools;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// macOS/Linux side of the Reviewer Quality Gate. Windows runs <c>Tools/gate-review.ps1</c>
/// through Windows PowerShell; that script is Windows-only throughout (<c>%APPDATA%</c>,
/// <c>%TEMP%</c>, <c>codex.exe</c>, backslash paths), so here the same steps run directly:
/// copy the staged files into a temporary git repo, commit them, run
/// <c>codex exec --sandbox read-only</c> on that commit with the script's prompt, closed stdin
/// and a hard timeout, and save the verdict to <c>.orc/reviews/gate_&lt;timestamp&gt;.md</c>
/// in the same format. Same gating as Windows: only workspaces that ship the gate script.
/// </summary>
public static partial class ReviewGateService
{
    private static readonly TimeSpan DefaultGateTimeout = TimeSpan.FromSeconds(600);

    /// <param name="codexPath">The Codex CLI; null finds <c>codex</c> on the login-shell PATH.</param>
    /// <returns>Null when there is nothing to review or Codex isn't installed (gate skipped).</returns>
    /// <exception cref="TimeoutException">Codex didn't finish in time (its process tree is killed).</exception>
    internal static async Task<GateResult?> RunUnixAsync(
        string            stagingDir,
        string            workspaceRoot,
        string            focus,
        string?           codexPath = null,
        TimeSpan?         timeout   = null,
        CancellationToken ct        = default)
    {
        if (!Directory.Exists(stagingDir))
            return null;
        var staged = Directory.GetFiles(stagingDir, "*", SearchOption.AllDirectories);
        if (staged.Length == 0)
            return null;

        codexPath ??= await FindCodexAsync(ct);
        if (codexPath is null || !File.Exists(codexPath))
            return null;   // gate-review.ps1's exit 3: "codex not found"

        var tempRepo = Path.Combine(Path.GetTempPath(), "orc-gate-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            foreach (var file in staged)
            {
                var dst = Path.Combine(tempRepo, Path.GetRelativePath(stagingDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(file, dst, overwrite: true);
            }

            await GitAsync(tempRepo, ct, "init", "-q");
            await GitAsync(tempRepo, ct, "config", "user.email", "gate@orc");
            await GitAsync(tempRepo, ct, "config", "user.name", "OrcGate");
            await GitAsync(tempRepo, ct, "add", ".");
            await GitAsync(tempRepo, ct, "commit", "-q", "-m", "swarm output");

            var reviewDir = Path.Combine(workspaceRoot, ".orc", "reviews");
            Directory.CreateDirectory(reviewDir);
            var outFile  = Path.Combine(reviewDir, $"gate_{DateTime.Now:yyyyMMdd_HHmmss}.md");
            var lastFile = outFile + ".last";

            var psi = new ProcessStartInfo(codexPath)
            {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in new[] { "exec", "--sandbox", "read-only", "-C", tempRepo,
                                      "--output-last-message", lastFile, GatePrompt(focus) })
                psi.ArgumentList.Add(a);
            // The npm-installed codex is a node script: it needs the login shell's PATH.
            if (ShellResolver.LoginPath.Value is { Length: > 0 } path)
                psi.Environment["PATH"] = path;

            using var p = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start codex for gate review");
            p.StandardInput.Close();   // EOF instead of a hang if codex asks for input
            var stdoutTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = p.StandardError.ReadToEndAsync(CancellationToken.None);   // drain

            var limit = timeout ?? DefaultGateTimeout;
            using (var timer = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timer.CancelAfter(limit);
                try
                {
                    await p.WaitForExitAsync(timer.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException($"Codex gate review timed out after {limit.TotalSeconds:0}s");
                }
            }

            var verdict = File.Exists(lastFile)
                ? await File.ReadAllTextAsync(lastFile, ct)
                : await stdoutTask;
            try { File.Delete(lastFile); } catch { /* best effort */ }

            var content = $"# Gate review — {DateTime.Now:yyyy-MM-dd HH:mm}\nStaging: {stagingDir}\n\n{verdict.Trim()}\n";
            await File.WriteAllTextAsync(outFile, content, ct);
            return ParseVerdict(content, outFile);
        }
        finally
        {
            try { Directory.Delete(tempRepo, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>gate-review.ps1's prompt, word for word (the Focus line is blank without one).</summary>
    internal static string GatePrompt(string focus) => string.Join("\n",
        "You are a senior engineer reviewing code produced by an AI coding swarm.",
        "Review the commit at HEAD (run: git log -p HEAD -- . ':!*.psv' ':!*.tsv' ':!*.jsonl').",
        "These are NEW files — not patches against existing code. Treat all content as new production code.",
        "",
        "Repo context: TheOrc — WPF/.NET 10 local AI orchestration tool.",
        "",
        "Check in order of severity:",
        "1. Crash risk — null refs, async void exception paths, init order, binding failures, reentrancy",
        "2. Correctness — logic errors, off-by-one, type mismatches, missing error handling",
        "3. Security — command injection, path traversal, XSS, hardcoded secrets",
        "4. Resource leaks — undisposed streams/processes, missing using blocks",
        "5. Convention — AutomationProperties.AutomationId on interactive WPF controls, NUnit tests named T##_*.cs",
        string.IsNullOrEmpty(focus) ? "" : $"Extra attention: {focus}",
        "",
        "Output format: one line per finding —",
        "\"BLOCKER <file>:<line> — <issue>\" or \"MINOR <file>:<line> — <issue>\" —",
        "or the single word CLEAN if no issues found. No other prose.");

    private static async Task<string?> FindCodexAsync(CancellationToken ct)
    {
        try
        {
            var result = await ShellTools.RunAsync("command -v codex", Environment.CurrentDirectory, ct);
            var lines = result.Split('\n', StringSplitOptions.TrimEntries);
            return lines.Length > 1 && lines[0] == "[exit 0]" && Path.IsPathRooted(lines[1]) && File.Exists(lines[1])
                ? lines[1]
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private static async Task GitAsync(string repo, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (ShellResolver.LoginPath.Value is { Length: > 0 } path)
            psi.Environment["PATH"] = path;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {args[0]} failed for the gate review: {(await stderr).Trim()}");
    }
}
