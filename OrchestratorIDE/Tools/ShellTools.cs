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
            Description = "Run a shell command in the workspace. Blocked: destructive commands.",
            Parameters = new()
            {
                ["command"] = new("string", "The command to run (PowerShell on Windows)."),
                ["cwd"]     = new("string", "Working directory (default: workspace root)."),
                ["reason"]  = new("string", "Why this command needs to run."),
            },
            Required = ["command"],
            RequiresApproval = true,   // all shell commands need approval
            Handler = async (args, ct) =>
            {
                var cmd = args.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
                var cwd = args.TryGetValue("cwd",     out var d) ? d?.ToString() ?? "" : "";
                if (string.IsNullOrEmpty(cwd)) cwd = workspaceRoot;

                // Safety check
                if (_blocked.Any(b => cmd.Contains(b, StringComparison.OrdinalIgnoreCase)))
                    return "[BLOCKED] This command is not allowed for safety.";

                return await RunAsync(cmd, cwd, ct);
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
