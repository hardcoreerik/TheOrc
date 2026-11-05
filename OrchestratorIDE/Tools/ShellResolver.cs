// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace OrchestratorIDE.Tools;

internal enum ShellPlatform { Windows, Unix }

/// <summary>
/// Builds the process that runs an agent shell command (<c>run_shell</c>, <c>run_tests</c>).
///
/// Windows: exactly what <see cref="ShellTools.RunAsync"/> always did —
/// <c>powershell -NoProfile -NonInteractive -EncodedCommand &lt;UTF-16LE base64&gt;</c>.
///
/// macOS/Linux: Windows PowerShell doesn't exist there, so every command used to fail at
/// <c>Process.Start</c>. Instead the command runs as <c>/bin/bash -c &lt;command&gt;</c>:
/// <list type="bullet">
/// <item>bash, not the user's <c>$SHELL</c>: models write POSIX (<c>$VAR</c>, <c>2&gt;&amp;1</c>,
///   heredocs) that fish or csh would break; this is the approach Goose takes.</item>
/// <item>The command is a single argv entry (<c>ArgumentList</c>), so no quoting layer can
///   change what runs: <c>ToolPolicyEngine</c> and the approval prompt see the same
///   text bash executes.</item>
/// <item>stdin is closed right after start, so a command that waits for input gets EOF and ends
///   instead of hanging the agent.</item>
/// <item>PATH comes from the user's login shell, read once: an app opened from Finder inherits
///   only <c>/usr/bin:/bin:/usr/sbin:/sbin</c>, so Homebrew's <c>dotnet</c>, <c>rg</c>,
///   <c>python3</c> etc. would otherwise be "command not found".</item>
/// </list>
/// </summary>
public static class ShellResolver
{
    internal static ShellPlatform Current =>
        OperatingSystem.IsWindows() ? ShellPlatform.Windows : ShellPlatform.Unix;

    /// <summary>The process for <paramref name="command"/> on this OS.</summary>
    public static ProcessStartInfo CreateStartInfo(string command, string cwd) =>
        CreateStartInfo(command, cwd, Current);

    internal static ProcessStartInfo CreateStartInfo(string command, string cwd, ShellPlatform platform)
    {
        if (platform == ShellPlatform.Windows)
        {
            // Unchanged from the original ShellTools.RunAsync. -EncodedCommand avoids all
            // quote-escaping fragility; PowerShell expects UTF-16LE base64.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            return new ProcessStartInfo
            {
                FileName  = "powershell",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName = UnixShellPath,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,   // closed by the caller right after Start
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        if (LoginPath.Value is { Length: > 0 } path)
            psi.Environment["PATH"] = path;
        return psi;
    }

    /// <summary>Absolute path (never a PATH lookup), bash where it exists, else POSIX sh.</summary>
    internal static string UnixShellPath =>
        File.Exists("/bin/bash") ? "/bin/bash"
        : File.Exists("/usr/bin/bash") ? "/usr/bin/bash"
        : "/bin/sh";

    /// <summary>
    /// The login shell's PATH, then this process's PATH, then Homebrew's standard directories
    /// (only those that exist), de-duplicated in that order. Resolved once per process.
    /// </summary>
    internal static readonly Lazy<string?> LoginPath = new(ResolveLoginPath);

    private const string PathMarker = "__THEORC_PATH__";

    private static string? ResolveLoginPath()
    {
        string? login = null;
        try
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !Path.IsPathRooted(shell) || !File.Exists(shell))
                shell = File.Exists("/bin/zsh") ? "/bin/zsh" : UnixShellPath;

            var psi = new ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // -l loads the user's profile (where Homebrew's `brew shellenv` lives). The marker
            // separates PATH from anything a profile script prints.
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"printf '{PathMarker}%s' \"$PATH\"");

            using var process = Process.Start(psi);
            if (process is not null)
            {
                process.StandardInput.Close();
                var read = process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();   // drain so a chatty profile can't block
                if (process.WaitForExit(5000) && read.Wait(1000))
                {
                    var output = read.Result;
                    var at = output.LastIndexOf(PathMarker, StringComparison.Ordinal);
                    if (at >= 0)
                        login = output[(at + PathMarker.Length)..].Trim();
                }
                else
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                }
            }
        }
        catch
        {
            // No login shell available: fall back to this process's PATH plus Homebrew dirs.
        }

        return MergePath(login, Environment.GetEnvironmentVariable("PATH"),
            new[] { "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin" }.Where(Directory.Exists));
    }

    internal static string MergePath(string? first, string? second, IEnumerable<string> extras)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var entry in (first ?? "").Split(':')
                     .Concat((second ?? "").Split(':'))
                     .Concat(extras))
        {
            var trimmed = entry.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
                parts.Add(trimmed);
        }
        return string.Join(':', parts);
    }
}
