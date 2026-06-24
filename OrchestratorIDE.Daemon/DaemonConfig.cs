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

    /// <summary>
    /// Ollama model tag this worker uses for coder-lane tasks. Empty means "use whatever
    /// Core.AppSettings.Load() (the GUI's own settings.json) has for LastWorkerModel" -- which
    /// is never populated on a headless box that has no realistic path to ever run the GUI
    /// (found 2026-06-24 testing on a Raspberry Pi: HiveWorkerAgent throws "no model configured"
    /// the first time a real task is dispatched, since AppSettings.Load() returns all-defaults
    /// with no settings.json on disk). Set via "Hive:CoderModel" in appsettings.json or the
    /// HIVE__CODERMODEL env var -- the same binding mechanism every other DaemonConfig field
    /// already uses, so a pure-headless deployment never needs the GUI-coupled settings file.
    /// </summary>
    public string CoderModel { get; set; } = "";

    /// <summary>Same as <see cref="CoderModel"/> but for researcher-lane tasks. Empty falls
    /// back the same way.</summary>
    public string ResearcherModel { get; set; } = "";
}
