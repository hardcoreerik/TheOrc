// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Daemon;

/// <summary>Daemon runtime configuration, loaded from appsettings.json or env vars.</summary>
public sealed class DaemonConfig
{
    // ── Identity ──────────────────────────────────────────────────────────────
    /// <summary>Human-readable node name advertised to the HIVE swarm.</summary>
    public string NodeName { get; set; } = Environment.MachineName;

    // ── Ports ─────────────────────────────────────────────────────────────────
    // NodeApiPort (7078) and TaskQueuePort (7079) are HIVE protocol constants
    // shared across the swarm — all nodes must agree on the same values.
    // They are intentionally not exposed as per-node config to avoid split-brain.
    public int TaskQueuePort   { get; set; } = 7079;   // HiveTaskQueue

    // ── Inference ─────────────────────────────────────────────────────────────
    /// <summary>Base URL of the local Ollama instance this node can advertise to Warchiefs.</summary>
    public string OllamaUrl    { get; set; } = "http://localhost:11434";

    // ── Workspace ────────────────────────────────────────────────────────────
    /// <summary>Path to the workspace root that contains .orc/theorc.db.
    /// Defaults to a TheOrc sub-folder in the system application data directory.</summary>
    public string WorkspaceRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheOrc", "daemon-workspace");

    // ── Mode ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Roles this node accepts as a HIVE worker (empty = all roles).
    /// Ignored when <see cref="WorkerMode"/> is false.
    /// </summary>
    public List<string> WorkerLanes { get; set; } = [];

    /// <summary>Enable the worker polling agent (claims and executes remote tasks via Ollama).
    /// Set false to run as a Warchief-only coordinator with no local execution.</summary>
    public bool WorkerMode { get; set; } = true;
}
