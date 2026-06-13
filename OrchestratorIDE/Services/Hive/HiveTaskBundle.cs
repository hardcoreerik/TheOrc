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
