// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// The URL-ACL-reservation + firewall-rule logic OrchestratorSetup's HiveEnroller has always
/// had, plus a diagnostic HiveEnroller never had: detecting that the interface a peer would
/// actually connect through is classified "Public" by Windows, which makes Windows Firewall
/// silently drop traffic even when the URL ACL bind and the (deliberately Private-profile-only)
/// firewall rules are both correctly in place. Found 2026-06-21 in live multi-machine pairing:
/// a node showed every other check green and still failed every pairing attempt with a 10s
/// connect timeout, because the IP a peer was reaching it on belonged to an adapter classified
/// Public, where the Private-only rules never applied.
///
/// Deliberately has ZERO dependency on HiveNodeServer/HiveIdentity/HivePeerStore/etc. -- ports
/// are passed in by the caller, not hardcoded here -- so this file can be shared via a plain
/// `&lt;Compile Include&gt;` into OrchestratorSetup (the installer) without dragging that
/// project's build through the rest of the Hive stack's dependency graph. The installer's own
/// HiveEnroller.cs delegates its URL-ACL/firewall portion here instead of duplicating it;
/// OrchestratorIDE.Avalonia (the running app) is the first caller of the network-category
/// diagnostic, since HiveEnroller (install-time only) never had a UI surface to show it from.
/// </summary>
public static class HiveNetworkEnroller
{
    public sealed record UrlAclSpec(int Port, string PathPrefix);
    public sealed record FirewallSpec(string Name, int Port, string Protocol); // Protocol: "TCP" or "UDP"

    public sealed record PublicInterfaceWarning(
        string InterfaceAlias, string ProfileName, string NetworkCategory, string IPv4Address);

    // ── URL ACL + firewall enrollment ────────────────────────────────────────
    // Same shape as OrchestratorSetup.Services.HiveEnroller.Enroll() before this extraction:
    // try unelevated first (no-op if already granted), batch everything still missing into
    // one UAC prompt, then re-verify each item independently (cmd.exe's own exit code only
    // reflects the LAST command in a multi-command batch, so trusting it would mask an
    // earlier failure inside the batch).

    public static bool Enroll(IReadOnlyList<UrlAclSpec> urlAcls, IReadOnlyList<FirewallSpec> firewallRules,
                               Action<string>? log)
    {
        // netsh url-acl/firewall-profile concepts are Windows-only -- runtime guard, not
        // #if/MSBuild-conditional compilation, matching INSTALLER_REVAMP_SPEC.md Phase 1's
        // established precedent (HardwareDetector.Detect's OperatingSystem.IsWindows() guard):
        // this file is referenced unconditionally from HivePanel.axaml.cs, which compiles on
        // every OS, so the TYPE must exist everywhere even though the behavior is Windows-only.
        // Linux/macOS have their own separate, already-shipped firewall handling at install
        // time (LinuxPlatformInstaller/MacPlatformInstaller) -- this method is not where that
        // would plug in.
        if (!OperatingSystem.IsWindows())
        {
            log?.Invoke("  HiveNetworkEnroller is Windows-only -- nothing to do on this OS.");
            return false;
        }

        var ok = true;
        var pending = new List<string>();

        foreach (var acl in urlAcls)
        {
            var prefix = $"http://+:{acl.Port}{acl.PathPrefix}";
            if (UrlAclReserved(prefix))
            {
                log?.Invoke($"  URL ACL already reserved: {prefix}");
                continue;
            }
            var args = $"http add urlacl url={prefix} user=Everyone";
            if (RunNetsh(args) == 0 && UrlAclReserved(prefix))
            {
                log?.Invoke($"  ✓ URL ACL reserved: {prefix}");
                continue;
            }
            pending.Add(args);
        }

        foreach (var rule in firewallRules)
        {
            if (FirewallRuleExists(rule.Name, rule.Port, rule.Protocol))
            {
                log?.Invoke($"  Firewall rule '{rule.Name}' already correct ({rule.Protocol} {rule.Port}, Private profile).");
                continue;
            }
            var addArgs =
                $"advfirewall firewall add rule name=\"{rule.Name}\" dir=in action=allow " +
                $"protocol={rule.Protocol} localport={rule.Port} profile=private";
            if (RunNetsh(addArgs) == 0 && FirewallRuleExists(rule.Name, rule.Port, rule.Protocol))
            {
                log?.Invoke($"  ✓ Firewall rule '{rule.Name}' ({rule.Protocol} {rule.Port}, Private profile).");
                continue;
            }
            pending.Add(addArgs);
        }

        if (pending.Count > 0)
        {
            log?.Invoke($"  {pending.Count} step(s) need administrator — requesting elevation once…");
            RunElevatedBatch(pending);

            foreach (var acl in urlAcls)
            {
                var prefix = $"http://+:{acl.Port}{acl.PathPrefix}";
                if (UrlAclReserved(prefix)) { log?.Invoke($"  ✓ URL ACL reserved (elevated): {prefix}"); continue; }
                ok = false;
                log?.Invoke($"  ⚠ URL ACL still missing after elevation: {prefix}");
            }
            foreach (var rule in firewallRules)
            {
                if (FirewallRuleExists(rule.Name, rule.Port, rule.Protocol))
                {
                    log?.Invoke($"  ✓ Firewall rule '{rule.Name}' applied (elevated).");
                    continue;
                }
                ok = false;
                log?.Invoke($"  ⚠ Firewall rule '{rule.Name}' still missing after elevation.");
            }
        }

        return ok;
    }

    private static void RunElevatedBatch(List<string> netshArgs)
    {
        string? batPath = null;
        try
        {
            batPath = Path.Combine(Path.GetTempPath(), $"theorc-hive-network-{Guid.NewGuid():N}.bat");
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
        catch { /* e.g. the user cancelled the UAC prompt -- the caller's own re-verification
                   above is what actually determines success, not this try/catch. */ }
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
        catch { return false; }
    }

    /// <summary>
    /// True only if a rule with this exact name AND the expected port/protocol exists --
    /// matching by name alone would consider a rule "present" even if its port/protocol
    /// drifted from what the current code expects, silently leaving the real port
    /// unprotected. "netsh ... show rule" prints one full block per matching rule
    /// (Protocol:/LocalPort: lines included), so a stale-port duplicate is distinguishable
    /// from a correct one rather than just counted as "exists" (ported as-is from
    /// OrchestratorSetup.Services.HiveEnroller's own hardening, Codex/Grok review 2026-06-21 --
    /// not re-simplified during this extraction).
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
        catch { return false; }
    }

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
            p!.WaitForExit(10000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    // ── Network-category diagnostic ──────────────────────────────────────────
    // Shells to PowerShell's Get-NetConnectionProfile rather than P/Invoking the
    // NetworkListManager COM API directly -- matches this codebase's established style of
    // wrapping OS CLI tools (netsh above, ufw/firewall-cmd in LinuxPlatformInstaller) instead
    // of adding COM-interop marshaling for a diagnostic that runs occasionally, not in a hot
    // path; the few hundred ms of process-start overhead is a non-issue here.

    public static async Task<List<PublicInterfaceWarning>> FindPublicInterfacesAsync(CancellationToken ct = default)
    {
        var warnings = new List<PublicInterfaceWarning>();
        if (!OperatingSystem.IsWindows()) return warnings; // see Enroll()'s guard comment above
        try
        {
            var (output, _) = await RunPowerShellAsync(
                "Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -ne 'Private' -and " +
                "$_.IPv4Connectivity -ne 'NoTraffic' -and $_.IPv4Connectivity -ne 'Disconnected' } | " +
                "Select-Object Name,InterfaceAlias,NetworkCategory | ConvertTo-Json -Compress", ct);
            if (string.IsNullOrWhiteSpace(output)) return warnings;

            // ConvertTo-Json emits a single object (not an array) when there's exactly one
            // match -- normalize both shapes before parsing.
            var trimmed = output.Trim();
            using var doc = JsonDocument.Parse(trimmed.StartsWith('[') ? trimmed : $"[{trimmed}]");

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var alias = el.TryGetProperty("InterfaceAlias", out var a) ? a.GetString() ?? "" : "";
                var name  = el.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var category = el.TryGetProperty("NetworkCategory", out var c) ? c.GetString() ?? "" : "";
                if (alias.Length == 0) continue;

                var ip = await GetIPv4ForInterfaceAsync(alias, ct);
                warnings.Add(new PublicInterfaceWarning(alias, name, category, ip));
            }
        }
        catch { /* best-effort diagnostic -- never throw into the caller's UI flow */ }
        return warnings;
    }

    private static async Task<string> GetIPv4ForInterfaceAsync(string interfaceAlias, CancellationToken ct)
    {
        try
        {
            var (output, _) = await RunPowerShellAsync(
                $"(Get-NetIPAddress -InterfaceAlias '{interfaceAlias.Replace("'", "''")}' " +
                "-AddressFamily IPv4 -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.IPAddress -notlike '169.254.*' } | " +
                "Select-Object -First 1 -ExpandProperty IPAddress)", ct);
            return output.Trim();
        }
        catch { return ""; }
    }

    public static async Task<bool> SetNetworkCategoryPrivateAsync(string interfaceAlias, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return false; // see Enroll()'s guard comment above
        string? scriptPath = null;
        try
        {
            scriptPath = Path.Combine(Path.GetTempPath(), $"theorc-hive-netcat-{Guid.NewGuid():N}.ps1");
            var escaped = interfaceAlias.Replace("'", "''");
            await File.WriteAllTextAsync(scriptPath,
                $"Set-NetConnectionProfile -InterfaceAlias '{escaped}' -NetworkCategory Private", ct);

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            await p.WaitForExitAsync(ct);

            var warnings = await FindPublicInterfacesAsync(ct);
            return !warnings.Any(w => w.InterfaceAlias == interfaceAlias);
        }
        catch { return false; } // e.g. the user cancelled the UAC prompt
        finally
        {
            if (scriptPath is not null) { try { File.Delete(scriptPath); } catch { } }
        }
    }

    /// <summary>
    /// Reads stdout AND stderr concurrently via Task.WhenAll -- a process that writes enough
    /// to stderr while only stdout is being awaited fills the OS pipe buffer and deadlocks,
    /// since the child then blocks writing and never reaches exit (same class of bug fixed in
    /// LinuxPlatformInstaller.RunAsync/MacPlatformInstaller.RunAsync during INSTALLER_REVAMP_SPEC.md
    /// Phases 4-5 -- applying that fix here from the start instead of re-discovering it).
    /// </summary>
    private static async Task<(string Output, int ExitCode)> RunPowerShellAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var proc = Process.Start(psi);
        if (proc is null) return ("", -1);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var outTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
        var errTask = proc.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await Task.WhenAll(outTask, errTask);
            await proc.WaitForExitAsync(linked.Token);
            return (outTask.Result, proc.ExitCode);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return ("", -1);
        }
    }
}
