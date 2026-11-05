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
        var text = ToolText.For(ShellResolver.Current);
        registry.Register(new ToolDefinition
        {
            Name = "run_shell",
            Description = text.Description,
            Parameters = new()
            {
                ["command"]   = new("string", text.Command),
                ["env_setup"] = new("string", text.EnvSetup),
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
        // PowerShell on Windows (unchanged); bash on macOS/Linux. See ShellResolver.
        var psi = ShellResolver.CreateStartInfo(cmd, cwd);

        using var proc = Process.Start(psi) ?? throw new Exception("Failed to start process");
        // Unix only (Windows doesn't redirect stdin): give the command EOF instead of a
        // terminal it can block on.
        if (psi.RedirectStandardInput)
            proc.StandardInput.Close();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var combined = (stdout + (stderr.Length > 0 ? $"\n[stderr]\n{stderr}" : "")).Trim();
        if (combined.Length > maxBytes) combined = combined[..maxBytes] + "\n… [truncated]";

        return $"[exit {proc.ExitCode}]\n{combined}";
    }

    /// <summary>
    /// What the model is told about run_shell. Windows: the original PowerShell wording,
    /// unchanged. macOS/Linux: the same instructions for bash, so models write commands the
    /// shell will actually run.
    /// </summary>
    internal sealed record ToolText(string Description, string Command, string EnvSetup)
    {
        public static ToolText For(ShellPlatform platform) => platform == ShellPlatform.Windows
            ? new(
                "Run a PowerShell command in the workspace. " +
                "Use env_setup to source an environment script BEFORE the command — " +
                "both run in the same process so variables like IDF_PATH survive. " +
                "Example: env_setup='. C:\\esp-idf\\export.ps1', command='idf.py build'. " +
                "Blocked: destructive commands.",
                "The PowerShell command to run.",
                "Optional. A PowerShell snippet run BEFORE command in the same process. " +
                "Use this to source environment scripts (e.g. '. C:\\esp-idf\\export.ps1'). " +
                "The environment it sets is visible to command.")
            : new(
                "Run a shell command (bash) in the workspace. " +
                "Use env_setup to source an environment script BEFORE the command — " +
                "both run in the same shell so exported variables like IDF_PATH survive. " +
                "Example: env_setup='source ~/esp/esp-idf/export.sh', command='idf.py build'. " +
                "Blocked: destructive commands.",
                "The bash command to run.",
                "Optional. A bash snippet run BEFORE command in the same shell. " +
                "Use this to source environment scripts (e.g. 'source ./env.sh'). " +
                "The environment it sets is visible to command.");
    }
}
