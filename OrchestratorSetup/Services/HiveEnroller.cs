// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace OrchestratorSetup.Services;

/// <summary>
/// HIVE MIND enrollment (docs/HIVE_MIND_SPEC.md §8): makes this PC usable by
/// other TheOrc machines on the same PRIVATE network.
///   1. OLLAMA_HOST=0.0.0.0 in the user environment (Ollama listens on LAN;
///      takes effect when Ollama next restarts).
///   2. http.sys URL ACL reservations for the HIVE node-server ports (7078,
///      7079) so a normal-user process can bind the wildcard prefix
///      (http://+:port/) instead of silently falling back to localhost-only,
///      which makes the node invisible to every other machine. Found
///      2026-06-20 during multi-machine testing: this step was missing
///      entirely, so every non-admin install was mesh-unreachable.
///   3. Windows Firewall inbound rules for Ollama (11434) and the hive ports
///      (7077-7079, 50052) — Private profile ONLY, never Public/Domain.
/// Both URL ACLs and firewall rules need elevation. Each is tried unelevated
/// first (no-op if already granted or already present); anything still
/// missing is batched into a single UAC prompt rather than one per item.
/// Failures are reported, not fatal — the app offers one-click
/// re-enrollment later (HIVE roster panel).
/// </summary>
public static class HiveEnroller
{
    public const int OllamaPort    = 11434;
    public const int HivePort      = 7077;   // UDP beacon
    public const int HiveApiPort   = 7078;   // HTTP node-info endpoint
    public const int TaskQueuePort = 7079;   // Warchief distributed task queue (Phase 3)
    public const int RpcPort       = 50052;  // llama.cpp RPC worker (C2)

    /// <summary>One privileged step still needed, plus how to verify it actually landed.</summary>
    private sealed record PendingStep(string NetshArgs, Func<bool> Verify, string Description);

    /// <summary>Runs enrollment; logs progress; returns true if fully applied.</summary>
    public static bool Enroll(Action<string> log)
    {
        var ok = true;
        var pending = new List<PendingStep>();

        log("  Setting OLLAMA_HOST=0.0.0.0 (user environment)…");
        try
        {
            Environment.SetEnvironmentVariable("OLLAMA_HOST", "0.0.0.0",
                EnvironmentVariableTarget.User);
        }
        catch (Exception ex) { log($"  ⚠ env var failed: {ex.Message}"); ok = false; }

        // ── URL ACLs: must match the exact HttpListener prefixes the app binds
        //    (HiveNodeServer / HiveTaskQueue both use "http://+:{port}/hive/"). ──
        foreach (var port in new[] { HiveApiPort, TaskQueuePort })
        {
            var prefix = $"http://+:{port}/hive/";
            if (UrlAclReserved(prefix))
            {
                log($"  URL ACL already reserved: {prefix}");
                continue;
            }
            var args = $"http add urlacl url={prefix} user=Everyone";
            if (RunNetsh(args) == 0 && UrlAclReserved(prefix))
            {
                log($"  ✓ URL ACL reserved: {prefix}");
                continue;
            }
            pending.Add(new PendingStep(args, () => UrlAclReserved(prefix), $"URL ACL {prefix}"));
        }

        // ── Firewall rules — Private profile ONLY, never Public/Domain. ──
        foreach (var (name, port, proto) in new[]
                 { ("TheOrc Hive - Ollama",  OllamaPort,    "TCP"),
                   ("TheOrc Hive - Beacon",  HivePort,      "UDP"),   // UDP beacon
                   ("TheOrc Hive - API",     HiveApiPort,   "TCP"),
                   ("TheOrc Hive - Queue",   TaskQueuePort, "TCP"),
                   ("TheOrc Hive - RPC",     RpcPort,       "TCP") })
        {
            // Skip entirely if a correctly-configured rule already exists -- the previous
            // version unconditionally ran "delete rule" first (unelevated), but deleting an
            // existing admin-created firewall rule needs elevation too, so that delete
            // silently failed every time, then the add succeeded via the one elevated batch
            // below and piled a new rule on top of the old one(s). Confirmed in the field: a
            // machine re-enrolled across several install/repair runs had FOUR duplicate rule
            // objects per port (found 2026-06-21). Checking existence first makes this
            // idempotent instead of additive.
            if (FirewallRuleExists(name, port, proto))
            {
                log($"  Firewall rule '{name}' already correct ({proto} {port}, Private profile).");
                continue;
            }
            var addArgs =
                $"advfirewall firewall add rule name=\"{name}\" dir=in action=allow " +
                $"protocol={proto} localport={port} profile=private";
            if (RunNetsh(addArgs) == 0 && FirewallRuleExists(name, port, proto))
            {
                log($"  ✓ Firewall rule '{name}' ({proto} {port}, Private profile).");
                continue;
            }
            pending.Add(new PendingStep(addArgs, () => FirewallRuleExists(name, port, proto),
                $"firewall rule '{name}'"));
        }

        if (pending.Count > 0)
        {
            log($"  {pending.Count} step(s) need administrator — requesting elevation once…");
            RunElevatedBatch(pending.Select(p => p.NetshArgs).ToList());

            // Verify each pending item independently rather than trusting the batch's
            // own exit code -- cmd.exe only returns the LAST command's exit code, so an
            // earlier failure inside a multi-command batch would otherwise be masked by
            // a later success (found via Codex CLI review, 2026-06-20).
            foreach (var step in pending)
            {
                if (step.Verify())
                {
                    log($"  ✓ {step.Description} applied (elevated).");
                }
                else
                {
                    ok = false;
                    log($"  ⚠ {step.Description} still missing after elevation (skipped, " +
                        "cancelled, or failed).");
                }
            }
        }

        log(ok ? "  ✓ HIVE MIND enrollment complete (restart Ollama to apply)."
               : "  ⚠ Enrollment partially applied — HIVE MIND may not be reachable from other " +
                 "machines until this is fixed. TheOrc can finish this later from the HIVE panel, " +
                 "or run as Administrator and retry.");
        return ok;
    }

    /// <summary>
    /// Writes every still-needed privileged command to one temp batch file and runs
    /// it elevated once, instead of prompting UAC per item. Does not itself report
    /// success/failure -- the caller re-verifies each item's actual end state, since
    /// cmd.exe's own exit code only reflects the LAST command in a multi-command batch.
    /// </summary>
    private static void RunElevatedBatch(List<string> netshArgs)
    {
        string? batPath = null;
        try
        {
            batPath = Path.Combine(Path.GetTempPath(), $"theorc-hive-enroll-{Guid.NewGuid():N}.bat");
            File.WriteAllLines(batPath, netshArgs.Select(a => $"netsh {a}"));

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName        = batPath,
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
            p!.WaitForExit(30000);
        }
        catch
        {
            // Swallowed deliberately -- e.g. the user cancelled the UAC prompt. The
            // caller's per-item Verify() calls are what actually determine success.
        }
        finally
        {
            if (batPath is not null) { try { File.Delete(batPath); } catch { } }
        }
    }

    private static bool UrlAclReserved(string prefix)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh", Arguments = "http show urlacl",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            var output = p!.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output.Contains(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True only if a rule with this exact name AND the expected port/protocol exists --
    /// matching by name alone (the previous behavior) would consider a rule "present" even
    /// if its port/protocol drifted from what the current code expects (e.g. a future
    /// constant change), silently leaving the real port unprotected. "netsh ... show rule"
    /// prints one full block per matching rule (Protocol:/LocalPort: lines included), so a
    /// stale-port duplicate is distinguishable from a correct one rather than just counted
    /// as "exists" (Codex/Grok-style hardening added 2026-06-21 alongside the duplicate-rule
    /// fix above).
    /// </summary>
    private static bool FirewallRuleExists(string name, int port, string protocol)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh", Arguments = $"advfirewall firewall show rule name=\"{name}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            var output = p!.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            if (p.ExitCode != 0 || output.Contains("No rules match", StringComparison.OrdinalIgnoreCase))
                return false;

            // Each matching rule prints its own "Protocol:"/"LocalPort:" pair; check that at
            // least one block has both the expected protocol and port (a rule could exist
            // under this name with stale settings from an older code version).
            var blocks = output.Split("Rule Name:", StringSplitOptions.RemoveEmptyEntries);
            return blocks.Any(b =>
                System.Text.RegularExpressions.Regex.IsMatch(b, $@"Protocol:\s*{protocol}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(b, $@"LocalPort:\s*{port}\b"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs netsh and returns its exit code, or -1 if the process itself couldn't be
    /// started/communicated with (e.g. netsh missing, AV interference). Never throws --
    /// callers treat -1 the same as any other non-zero "didn't work" result, so a
    /// process-launch failure degrades to "needs elevation" rather than crashing Enroll().
    /// </summary>
    private static int RunNetsh(string args)
    {
        try
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
        catch
        {
            return -1;
        }
    }
}
