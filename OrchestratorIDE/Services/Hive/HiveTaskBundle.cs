// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.Hive;

// ── Phase 3 HIVE MIND — Distributed Swarm wire format ────────────────────────
//
// The Warchief (coordinator) decomposes a goal into SwarmTasks, then pushes
// each task as a HiveTaskBundle into its HiveTaskQueue (port 7079).
// Worker nodes poll the queue, claim a task, execute it locally with their
// own Ollama/llama.cpp, and POST a HiveTaskResult back.
// The Warchief integrates results exactly as if the task ran locally.
//
// Dependency graph: Warchief controls phase ordering (research → code → test).
// Workers are stateless executors — they receive a full self-contained bundle.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Warchief → Worker: everything a worker needs to execute one SwarmTask remotely.
/// JSON-serialized and served at GET /hive/tasks/next.
/// </summary>
public sealed class HiveTaskBundle
{
    public string TaskId         { get; set; } = "";
    public string SessionId      { get; set; } = "";

    /// <summary>"Researcher" | "Coder" | "UIDeveloper" | "Tester"</summary>
    public string Role           { get; set; } = "";
    public string Title          { get; set; } = "";

    /// <summary>
    /// Full worker user-message, pre-built by Warchief using BuildWorkerUserMessage.
    /// Includes language lock, filename requirements, sibling module awareness.
    /// </summary>
    public string Spec           { get; set; } = "";

    public string ProjectGoal    { get; set; } = "";
    public string TargetLanguage { get; set; } = "";

    /// <summary>Preferred model name on the worker node. Worker may substitute its own.</summary>
    public string ModelHint      { get; set; } = "";

    /// <summary>URL workers POST results to (Warchief's HiveTaskQueue base URL).</summary>
    public string WarchiefUrl    { get; set; } = "";

    public int    TimeoutMs      { get; set; } = 300_000;   // 5 min default

    /// <summary>Upstream research/task output included so workers have full context.</summary>
    public List<HiveArtifact> UpstreamArtifacts { get; set; } = [];
}

/// <summary>A finished artifact (e.g. researcher output) bundled into a dependent task.</summary>
public sealed class HiveArtifact
{
    public string Source  { get; set; } = "";  // task title that produced this
    public string Role    { get; set; } = "";  // "Researcher", "Coder", etc.
    public string Content { get; set; } = "";
}

/// <summary>Worker → Warchief: result of executing a HiveTaskBundle.</summary>
public sealed class HiveTaskResult
{
    public string  TaskId     { get; set; } = "";
    public string  WorkerId   { get; set; } = "";   // node machine name (e.g. "BIGRIG")
    public string  WorkerUrl  { get; set; } = "";   // node Ollama URL
    public string  Result     { get; set; } = "";
    public string  Status     { get; set; } = "completed";  // "completed" | "failed" | "timeout"
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMsg   { get; set; }
    public int     DurationMs { get; set; }
    /// <summary>
    /// Claim token received from /claim response. Must match Warchief's current
    /// token or the /complete|/fail call is rejected (stale-worker guard).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClaimToken { get; set; }
}

/// <summary>Body of POST /hive/tasks/{id}/heartbeat.</summary>
public sealed class HiveHeartbeatRequest
{
    public string WorkerId   { get; set; } = "";
    public string ClaimToken { get; set; } = "";
}

/// <summary>Body of POST /hive/tasks/{id}/claim.</summary>
public sealed class HiveClaimRequest
{
    public string   WorkerId  { get; set; } = "";
    public string   WorkerUrl { get; set; } = "";
    public string[] Lanes     { get; set; } = [];
}

/// <summary>Response to GET /hive/tasks/status — full snapshot of queue state.</summary>
public sealed class HiveQueueStatus
{
    public string SessionId  { get; set; } = "";
    public int    Total      { get; set; }
    public int    Pending    { get; set; }
    public int    InProgress { get; set; }
    public int    Completed  { get; set; }
    public int    Failed     { get; set; }
    public List<HiveQueueEntry> Tasks { get; set; } = [];
}

public sealed class HiveQueueEntry
{
    public string    TaskId    { get; set; } = "";
    public string    Title     { get; set; } = "";
    public string    Role      { get; set; } = "";
    public string    Status    { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string?   ClaimedBy { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ClaimedAt { get; set; }
}

/// <summary>
/// Minimal session context a worker fetches once (GET /hive/session/context)
/// so it can configure itself (models, language, goal) without the full bundle.
/// </summary>
public sealed class HiveSessionContext
{
    public string SessionId       { get; set; } = "";
    public string ProjectGoal     { get; set; } = "";
    public string TargetLanguage  { get; set; } = "";
    public string CoderModel      { get; set; } = "";
    public string ResearcherModel { get; set; } = "";
}

/// <summary>
/// A single task lifecycle event stored in HiveEventBus and served at
/// GET /hive/events. Warchief emits claim/complete/fail/timeout events
/// internally; workers POST task_executing events via POST /hive/events.
/// </summary>
public sealed class HiveEvent
{
    public long     Seq       { get; set; }
    public DateTime Ts        { get; set; }
    /// <summary>
    /// task_queued | task_claimed | task_executing | task_complete |
    /// task_failed | task_timeout | task_requeued
    /// </summary>
    public string   Type      { get; set; } = "";
    public string   Msg       { get; set; } = "";
    public string   TaskId    { get; set; } = "";
    public string   WorkerId  { get; set; } = "";
    public string   SessionId { get; set; } = "";
}

/// <summary>Body of POST /hive/events — workers push lifecycle events to Warchief.</summary>
public sealed class HiveEventPost
{
    public string Type      { get; set; } = "";
    public string Msg       { get; set; } = "";
    public string TaskId    { get; set; } = "";
    public string WorkerId  { get; set; } = "";
    public string SessionId { get; set; } = "";
}

// ── HIVE Mesh Heartbeat ───────────────────────────────────────────────────────

/// <summary>
/// Payload for POST /hive/mesh/heartbeat — the unified peer-to-peer liveness signal.
/// Replaces the role of the UDP beacon for enrolled peers (beacon is retained for discovery only).
/// Sent every 15 s (LAN) or 30 s (Tailscale) from every node to every peer in hive-peers.json.
/// </summary>
public sealed record HiveMeshHeartbeatPayload
{
    public string   NodeId        { get; init; } = "";
    public string   NodeName      { get; init; } = "";
    public long     Timestamp     { get; init; }       // Unix ms UTC
    public string   CurrentRole   { get; init; } = ""; // "Observer" | "Worker" | "Controller"
    public int      VramFreeMb    { get; init; }
    public string[] ActiveTaskIds { get; init; } = [];
    public long     Sequence      { get; init; }
}

// ── HIVE Pairing ─────────────────────────────────────────────────────────────

/// <summary>Body of POST /hive/pair — initiates a pairing ceremony.</summary>
public sealed class HivePairingRequest
{
    public string SessionId          { get; set; } = "";  // random GUID from initiator
    public string InitiatorNodeId    { get; set; } = "";
    public string InitiatorName      { get; set; } = "";
    public string InitiatorFingerprint { get; set; } = "";
    /// <summary>Initiator's Ed25519 signing public key DER, Base64.</summary>
    public string SigningPublicKeyDer { get; set; } = "";
    /// <summary>Initiator's ECDH exchange public key DER, Base64.</summary>
    public string ExchangePublicKeyDer { get; set; } = "";
    /// <summary>Signed(SessionId + InitiatorNodeId), Base64 — proves possession of signing key.</summary>
    public string ProofSignature     { get; set; } = "";
    public bool   IsMobileClient     { get; set; }
    public int    VramMb             { get; set; }
    public string SuggestedRole      { get; set; } = "Worker";
    /// <summary>
    /// Initiator's hive identifier, "" if unset (HiveIdentity.HiveRole == Unset).
    /// See HIVE_MEMBERSHIP_SPEC.md §4.3 for the reconciliation rule applied by the responder.
    /// </summary>
    public string HiveId             { get; set; } = "";
}

/// <summary>Response to GET /hive/pair/{sessionId} — approval polling result.</summary>
public sealed class HivePairingResponse
{
    /// <summary>"pending" | "approved" | "rejected" | "expired"</summary>
    public string Status             { get; set; } = "pending";
    public string? WarchiefNodeId    { get; set; }
    public string? WarchiefName      { get; set; }
    public string? WarchiefFingerprint { get; set; }
    public string? WarchiefSigningPublicKeyDer  { get; set; }
    public string? WarchiefExchangePublicKeyDer { get; set; }
    public string? AssignedRole      { get; set; }  // actual role granted
    public string[]? AllowedLanes    { get; set; }
    /// <summary>
    /// Responder's hive identifier after reconciliation (HIVE_MEMBERSHIP_SPEC.md §4.3) —
    /// "" only if neither side had one and the responder did not become a founder (should
    /// not happen in practice once Phase 1 ships, since approving always founds-or-adopts).
    /// The mismatch case (both sides set, differ) never reaches this type at all — it's
    /// refused at request time in HandlePairInitiateAsync with a separate, ad-hoc
    /// { "status": "hiveid_mismatch" } body before a HivePairingResponse is ever built.
    /// </summary>
    public string? HiveId            { get; set; }
}

// ── HIVE Election ─────────────────────────────────────────────────────────────

/// <summary>Body for POST /hive/mesh/election/* messages.</summary>
public sealed record ElectionMessage
{
    public string NodeId   { get; init; } = "";  // sender NodeId — overwritten with authenticated id on receipt
    public string Payload  { get; init; } = "";  // context-dependent (suspect NodeId, "claim", "stepdown", etc.)
    public long   Ts       { get; init; }        // Unix ms UTC
    public string Sig      { get; init; } = "";  // Base64 signature of (NodeId+Payload+Ts)
}
