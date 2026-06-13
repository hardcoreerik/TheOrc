using System.Diagnostics;

namespace OrchestratorSetup.Services;

/// <summary>
/// HIVE MIND enrollment (docs/HIVE_MIND_SPEC.md §8): makes this PC usable by
/// other TheOrc machines on the same PRIVATE network.
///   1. OLLAMA_HOST=0.0.0.0 in the user environment (Ollama listens on LAN;
///      takes effect when Ollama next restarts).
///   2. Windows Firewall inbound rules for Ollama (11434) and the hive node
///      port (7077) — Private profile ONLY, never Public/Domain.
/// Firewall rules need elevation; failures are reported, not fatal — the app
/// offers one-click re-enrollment later (HIVE roster panel).
/// </summary>
public static class HiveEnroller
{
    public const int OllamaPort    = 11434;
    public const int HivePort      = 7077;   // UDP beacon
    public const int HiveApiPort   = 7078;   // HTTP node-info endpoint

    /// <summary>Runs enrollment; logs progress; returns true if fully applied.</summary>
    public static bool Enroll(Action<string> log)
    {
        var ok = true;

        log("  Setting OLLAMA_HOST=0.0.0.0 (user environment)…");
        try
        {
            Environment.SetEnvironmentVariable("OLLAMA_HOST", "0.0.0.0",
                EnvironmentVariableTarget.User);
        }
        catch (Exception ex) { log($"  ⚠ env var failed: {ex.Message}"); ok = false; }

        foreach (var (name, port) in new[]
                 { ("TheOrc Hive - Ollama", OllamaPort),
                   ("TheOrc Hive - Beacon", HivePort),
                   ("TheOrc Hive - API",    HiveApiPort) })
        {
            log($"  Firewall rule '{name}' (TCP {port}, Private profile)…");
            if (!AddFirewallRule(name, port, log)) ok = false;
        }

        log(ok ? "  ✓ HIVE MIND enrollment complete (restart Ollama to apply)."
               : "  ⚠ Enrollment partially applied — TheOrc can finish this later from the HIVE panel.");
        return ok;
    }

    private static bool AddFirewallRule(string name, int port, Action<string> log)
    {
        try
        {
            // Idempotent: delete any prior rule with this name, then add.
            RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
            var exit = RunNetsh(
                $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow " +
                $"protocol=TCP localport={port} profile=private");
            if (exit != 0)
            {
                log($"  ⚠ netsh exited {exit} (needs administrator) — rule not added.");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log($"  ⚠ firewall rule failed: {ex.Message}");
            return false;
        }
    }

    private static int RunNetsh(string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh", Arguments = args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        });
        p!.WaitForExit(15000);
        return p.ExitCode;
    }
}
