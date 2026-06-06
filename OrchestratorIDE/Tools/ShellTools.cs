using System.Diagnostics;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

public static class ShellTools
{
    private static readonly string[] _blocked =
        ["rm ", "rmdir", "del ", "format", "shutdown", "reboot",
         "git reset --hard", "git checkout --", "taskkill", "Stop-Process"];

    public static void Register(ToolRegistry registry, string workspaceRoot)
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

                // Combine env_setup + command into a single invocation so the
                // environment set by env_setup is visible to command.
                var fullCmd = string.IsNullOrWhiteSpace(envSetup)
                    ? cmd
                    : $"{envSetup.TrimEnd(';', ' ')}; {cmd}";

                // Safety check (run on the combined string)
                if (_blocked.Any(b => fullCmd.Contains(b, StringComparison.OrdinalIgnoreCase)))
                    return "[BLOCKED] This command is not allowed for safety.";

                return await RunAsync(fullCmd, cwd, ct);
            }
        });
    }

    public static async Task<string> RunAsync(string cmd, string cwd, CancellationToken ct, int maxBytes = 32_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = "powershell",
            Arguments = $"-NoProfile -NonInteractive -Command \"{cmd.Replace("\"", "\\\"")}\"",
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
