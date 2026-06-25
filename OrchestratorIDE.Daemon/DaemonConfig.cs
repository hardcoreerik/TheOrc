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

    // ── Remote dispatch ───────────────────────────────────────────────────────
    /// <summary>
    /// Base URL of a remote Warchief's HiveTaskQueue (e.g. "http://192.168.1.10:7079") this
    /// node's worker should poll instead of its own local queue. Empty (default) means "poll
    /// myself" -- HiveService wires this node's own _taskQueue.BaseUrl, exactly as it always
    /// has. Set via "Hive:WarchiefUrl" / HIVE__WARCHIEFURL, same binding mechanism as
    /// <see cref="CoderModel"/>.
    ///
    /// Before this, HiveService always hardcoded WarchiefUrl to the node's own queue -- there
    /// was no way to configure a Warband to join an existing Warchief's swarm and pull tasks
    /// from it, only to run as a fully self-contained, self-pointing node. This is the half of
    /// "no way exists to remotely dispatch a task to a Warband" that lived on the pull side
    /// (the other half, the missing submit endpoint, is HiveTaskQueue's new POST
    /// /hive/tasks/submit).
    /// </summary>
    public string WarchiefUrl { get; set; } = "";

    /// <summary>
    /// Optional NodeId of the Warchief at <see cref="WarchiefUrl"/>, used by
    /// HiveWorkerAgent.SignIfPaired for a direct HivePeerStore lookup. Safe to leave empty --
    /// SignIfPaired already falls back to matching the peer by hostname when this is unset, so
    /// this only needs setting if hostname-based lookup is ambiguous (e.g. multiple peers
    /// resolve to similar hostnames).
    /// </summary>
    public string WarchiefNodeId { get; set; } = "";
}
