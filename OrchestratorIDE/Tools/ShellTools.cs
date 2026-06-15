// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using OrchestratorIDE.Core;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.Tools;

public static class ShellTools
{

    /// <param name="onSandboxBypass">
    ///   Called when the requested working directory is outside the workspace sandbox.
    ///   Signature: <c>(toolName, escapedPath, sandboxRoot, ct) → Task&lt;bool&gt;</c>.
    ///   Note: shell commands themselves can still address arbitrary absolute paths;
    ///   this guard covers the <c>cwd</c> argument only.
    /// </param>
    public static void Register(ToolRegistry registry, string workspaceRoot,
        Func<string, string, string, CancellationToken, Task<bool>>? onSandboxBypass = null)
    {
        registry.Register(new ToolDefinition
        {
            Name = "run_shell",
            Description =
                "Run a PowerShell command in the workspace. " +
                "Use env_setup to source an environment script BEFORE the command — " +
                "both run in the same process so variables like IDF_PATH survive. " +
                "Example: env_setup='. C:\\esp-idf\\export.ps1', command='idf.py build'. " +
                "Blocked: destructive commands.",
            Parameters = new()
            {
                ["command"]   = new("string", "The PowerShell command to run."),
                ["env_setup"] = new("string",
                    "Optional. A PowerShell snippet run BEFORE command in the same process. " +
                    "Use this to source environment scripts (e.g. '. C:\\esp-idf\\export.ps1'). " +
                    "The environment it sets is visible to command."),
                ["cwd"]       = new("string", "Working directory (default: workspace root)."),
                ["reason"]    = new("string", "Why this command needs to run."),
            },
            Required = ["command"],
            RequiresApproval = true,   // all shell commands need approval
            Handler = async (args, ct) =>
            {
                var cmd      = args.TryGetValue("command",   out var c) ? c?.ToString() ?? "" : "";
                var envSetup = args.TryGetValue("env_setup", out var e) ? e?.ToString() ?? "" : "";
                var cwd      = args.TryGetValue("cwd",       out var d) ? d?.ToString() ?? "" : "";
                if (string.IsNullOrEmpty(cwd)) cwd = workspaceRoot;

                // ── Sandbox guard: cwd ────────────────────────────────────
                // Resolve cwd relative to workspace if it's not absolute
                var resolvedCwd = Path.IsPathRooted(cwd)
                    ? Path.GetFullPath(cwd)
                    : Path.GetFullPath(Path.Combine(workspaceRoot, cwd));

                if (!PathSandbox.IsInsideSandbox(resolvedCwd, workspaceRoot))
                {
                    var allowed = onSandboxBypass is not null
                        && await onSandboxBypass("run_shell (cwd)", resolvedCwd, workspaceRoot, ct);
                    if (!allowed)
                        return $"[SANDBOX BLOCKED] run_shell: working directory '{resolvedCwd}' is " +
                               $"outside the workspace '{workspaceRoot}'. " +
                               $"Use a cwd inside the workspace, or omit cwd to use the workspace root.";
                    cwd = resolvedCwd; // use the resolved path after bypass
                }
                else
                {
                    cwd = resolvedCwd;
                }

                // Combine env_setup + command into a single invocation so the
                // environment set by env_setup is visible to command.
                var fullCmd = string.IsNullOrWhiteSpace(envSetup)
                    ? cmd
                    : $"{envSetup.TrimEnd(';', ' ')}; {cmd}";

                // Policy check — replaces the old hard-coded string list
                var policy = ToolPolicyEngine.Evaluate("run_shell", args, workspaceRoot);
                if (policy.BlockReason is not null)
                    return policy.BlockReason;

                return await RunAsync(fullCmd, cwd, ct);
            }
        });
    }

    public static async Task<string> RunAsync(string cmd, string cwd, CancellationToken ct, int maxBytes = 32_000)
    {
        // Use -EncodedCommand to avoid all quote-escaping fragility.
        // PowerShell -EncodedCommand expects UTF-16LE base64.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(cmd));

        var psi = new ProcessStartInfo
        {
            FileName  = "powershell",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi) ?? throw new Exception("Failed to start process");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var combined = (stdout + (stderr.Length > 0 ? $"\n[stderr]\n{stderr}" : "")).Trim();
        if (combined.Length > maxBytes) combined = combined[..maxBytes] + "\n… [truncated]";

        return $"[exit {proc.ExitCode}]\n{combined}";
    }
}
