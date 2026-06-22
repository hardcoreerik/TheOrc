// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Services.Hive;

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
///
/// Steps 2-3 delegate to HiveNetworkEnroller (OrchestratorIDE\Services\Hive\) rather than
/// duplicating the netsh logic here -- found 2026-06-21 in a live pairing-failure
/// investigation that this installer-only copy had no way to also surface the
/// network-category diagnostic (a Public-classified interface silently defeats these
/// Private-only firewall rules) that the running app needed too. One implementation, used
/// by both the installer (here) and the app's own Hive panel "Fix HIVE MIND on this
/// machine" action, instead of two copies that can drift.
/// </summary>
public static class HiveEnroller
{
    public const int OllamaPort    = 11434;
    public const int HivePort      = 7077;   // UDP beacon
    public const int HiveApiPort   = 7078;   // HTTP node-info endpoint
    public const int TaskQueuePort = 7079;   // Warchief distributed task queue (Phase 3)
    public const int RpcPort       = 50052;  // llama.cpp RPC worker (C2)

    // Must match the exact HttpListener prefixes HiveNodeServer/HiveTaskQueue bind
    // ("http://+:{port}/hive/").
    private static readonly HiveNetworkEnroller.UrlAclSpec[] UrlAcls =
    [
        new(HiveApiPort,   "/hive/"),
        new(TaskQueuePort, "/hive/"),
    ];

    private static readonly HiveNetworkEnroller.FirewallSpec[] FirewallRules =
    [
        new("TheOrc Hive - Ollama", OllamaPort,    "TCP"),
        new("TheOrc Hive - Beacon", HivePort,      "UDP"),
        new("TheOrc Hive - API",    HiveApiPort,   "TCP"),
        new("TheOrc Hive - Queue",  TaskQueuePort, "TCP"),
        new("TheOrc Hive - RPC",    RpcPort,       "TCP"),
    ];

    /// <summary>Runs enrollment; logs progress; returns true if fully applied.</summary>
    public static bool Enroll(Action<string> log)
    {
        log("  Setting OLLAMA_HOST=0.0.0.0 (user environment)…");
        var envOk = true;
        try
        {
            Environment.SetEnvironmentVariable("OLLAMA_HOST", "0.0.0.0",
                EnvironmentVariableTarget.User);
        }
        catch (Exception ex) { log($"  ⚠ env var failed: {ex.Message}"); envOk = false; }

        var networkOk = HiveNetworkEnroller.Enroll(UrlAcls, FirewallRules, log);

        var ok = envOk && networkOk;
        log(ok ? "  ✓ HIVE MIND enrollment complete (restart Ollama to apply)."
               : "  ⚠ Enrollment partially applied — HIVE MIND may not be reachable from other " +
                 "machines until this is fixed. TheOrc can finish this later from the HIVE panel, " +
                 "or run as Administrator and retry.");
        return ok;
    }
}
