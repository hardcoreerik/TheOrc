// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core;

/// <summary>
/// Helpers for detecting and configuring OLLAMA_NUM_PARALLEL on the current machine.
///
/// Ollama's parallel-inference slot count is controlled exclusively by the
/// OLLAMA_NUM_PARALLEL environment variable read at server startup — there is
/// no REST API to query or change it at runtime.
///
/// Detection strategy:
///   1. Read the Windows *user* environment variable (most reliable).
///   2. If not set, read the *process* env (covers non-Windows shells that set it inline).
///   3. Default assumption: 1 slot if neither is set.
///
/// Configuration strategy (Option A + C as agreed):
///   A. Show a guide + copy-paste restart command (immediate, no restart required from us).
///   C. Set the Windows user env var permanently so Ollama picks it up on next launch.
/// </summary>
public static class OllamaParallelHelper
{
    // ── Detection ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the currently active OLLAMA_NUM_PARALLEL value as seen by
    /// the running Ollama server (or best estimate thereof).
    /// </summary>
    public static int DetectCurrentSlots()
    {
        // 1. Windows user environment (set via Option C — survives reboots)
        var userVal = Environment.GetEnvironmentVariable(
            "OLLAMA_NUM_PARALLEL", EnvironmentVariableTarget.User);
        if (int.TryParse(userVal, out var u) && u > 0) return u;

        // 2. Machine-level env var (set by an admin or installer)
        var machineVal = Environment.GetEnvironmentVariable(
            "OLLAMA_NUM_PARALLEL", EnvironmentVariableTarget.Machine);
        if (int.TryParse(machineVal, out var m) && m > 0) return m;

        // 3. Process-level (set in the shell that launched TheOrc)
        var processVal = Environment.GetEnvironmentVariable("OLLAMA_NUM_PARALLEL");
        if (int.TryParse(processVal, out var p) && p > 0) return p;

        // Not set → Ollama defaults to 1 (or sometimes 4 on high-VRAM cards — but
        // we conservatively report 1 to encourage explicit configuration).
        return 1;
    }

    /// <summary>True if Ollama has been explicitly configured for parallel inference.</summary>
    public static bool IsConfigured => DetectCurrentSlots() > 1;

    /// <summary>Friendly status string for display in the Settings panel.</summary>
    public static string StatusText(int slots) => slots switch
    {
        <= 1 => "⚠  Single-slot mode — agents run one at a time",
        2    => "✓  2 parallel slots — basic multi-agent ready",
        3    => "✓  3 parallel slots — good for 2 specialists + orchestrator",
        4    => "✓  4 parallel slots — optimal for TheOrc multi-agent",
        _    => $"✓  {slots} parallel slots configured"
    };

    public static string StatusColor(int slots) => slots > 1 ? "#76B900" : "#CCA700";

    // ── Option C — Set permanently (Windows user env var) ───────────────────

    /// <summary>
    /// Writes OLLAMA_NUM_PARALLEL to the Windows *user* environment block.
    /// Takes effect the next time ollama.exe (or any process) is launched —
    /// does NOT affect the currently running Ollama server.
    /// </summary>
    public static void SetPermanently(int slots)
    {
        Environment.SetEnvironmentVariable(
            "OLLAMA_NUM_PARALLEL",
            slots.ToString(),
            EnvironmentVariableTarget.User);
    }

    /// <summary>
    /// Removes the user-level env var (reverts to Ollama default).
    /// </summary>
    public static void ClearPermanent()
    {
        Environment.SetEnvironmentVariable(
            "OLLAMA_NUM_PARALLEL", null, EnvironmentVariableTarget.User);
    }

    // ── Option A — Guide text ────────────────────────────────────────────────

    /// <summary>
    /// PowerShell one-liner the user can paste to restart Ollama with the
    /// desired parallel slot count immediately (without rebooting).
    /// </summary>
    public static string GetRestartCommand(int slots) =>
        $"$env:OLLAMA_NUM_PARALLEL={slots}; " +
        "Stop-Process -Name ollama -Force -ErrorAction SilentlyContinue; " +
        "Start-Sleep 1; ollama serve";

    /// <summary>
    /// Plain explanation text shown in the setup card.
    /// </summary>
    public static string GetExplanation(int slots) =>
        slots > 1
            ? $"Ollama is configured for {slots} parallel slots. " +
              "Multiple agents can run concurrently without queuing."
            : "Ollama is running in single-slot mode. Each request waits for the previous " +
              "one to finish — multi-agent tasks will run sequentially, not in parallel.\n\n" +
              $"Setting OLLAMA_NUM_PARALLEL=4 allows up to 4 concurrent inference requests, " +
              "which is optimal for TheOrc's multi-agent system on most NVIDIA GPUs.";

    // ── Recommended slot count ───────────────────────────────────────────────

    /// <summary>
    /// Recommends a slot count based on detected VRAM.
    /// Rule of thumb: each Nemotron 4B slot uses ~3-4 GB VRAM.
    /// </summary>
    public static int RecommendedSlots(double vramGb) => vramGb switch
    {
        >= 24 => 6,
        >= 16 => 4,
        >= 12 => 3,
        >= 8  => 2,
        _     => 1
    };
}
