// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Microsoft.Data.Sqlite;

namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Durable storage for HIVEMIND task + event history (Phase 4). This is the single most
/// security-sensitive repository: most of what it stores comes <b>straight off the wire</b>
/// from remote worker nodes (result blobs, worker ids, claim tokens, event messages).
///
/// Every defence required by docs/sql-migration/02_SECURITY_HIVEMIND.md is enforced HERE,
/// at the write boundary, never trusted from the caller:
///   • SQL injection — closed structurally by the RepositoryBase parameterized choke point.
///   • Length caps — every wire string is truncated to a hard max (never trust declared length).
///   • Charset — identifier fields keep only [A-Za-z0-9._-]; control chars / newlines (log
///     forging) are stripped.
///   • Per-node quota — a single authenticated node cannot write unbounded rows per session.
///   • Provenance — the AUTHENTICATED node id, an authenticated flag, and the claim token are
///     persisted on every row so a poisoned result is traceable and revocable after the fact.
///   • Retention — retain_until + <see cref="SweepExpired"/> bound durable growth.
/// </summary>
public sealed class HiveRepository : RepositoryBase
{
    // Write-path length caps (truncate at write — never trust the wire-declared length).
    private const int MaxId         = 128;          // task_id / session_id / worker / token / node
    private const int MaxTitle      = 512;
    private const int MaxRole       = 32;
    private const int MaxStatus     = 32;
    private const int MaxType       = 64;
    private const int MaxMsg        = 1024;
    private const int MaxResultBlob = 256 * 1024;   // 256 KB

    /// <summary>Default cap on rows a single authenticated node may create per session.
    /// Over-quota inserts are rejected and flagged; updates to existing rows are always allowed.</summary>
    public const int DefaultMaxRowsPerNodePerSession = 5_000;

    private readonly int      _maxRowsPerNode;
    private readonly TimeSpan _retention;

    /// <param name="maxRowsPerNodePerSession">Per-node row quota (see
    /// <see cref="DefaultMaxRowsPerNodePerSession"/>). Lowered in tests.</param>
    /// <param name="retention">How long durable hive rows live before
    /// <see cref="SweepExpired"/> removes them. Defaults to 7 days; mirrors the old 5-min
    /// in-memory eviction intent but long enough to be useful history.</param>
    public HiveRepository(
        SqliteStore store,
        int maxRowsPerNodePerSession = DefaultMaxRowsPerNodePerSession,
        TimeSpan? retention = null) : base(store)
    {
        _maxRowsPerNode = maxRowsPerNodePerSession;
        _retention      = retention ?? TimeSpan.FromDays(7);
    }

    // ── Task rows ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Idempotent upsert of one task's current state, keyed on (session_id, task_id).
    /// Returns false (and writes nothing) when an authenticated node is over its per-session
    /// row quota for a NEW task — the caller logs/flags. Updates to an existing row never
    /// hit the quota. All wire strings are sanitised at this boundary.
    /// </summary>
    public bool UpsertTask(
        string taskId, string sessionId, string? role, string? title, string status,
        string? authenticatedNode, string? worker, bool authenticated, string? claimToken,
        string? resultBlob, int? durationMs, string? errorMsg, DateTime enqueuedAt)
    {
        var node = SanId(authenticatedNode, MaxId);

        // Quota only applies to authenticated remote nodes creating NEW rows. The Warchief's
        // own local enqueue (node == null) and updates to existing rows are never blocked.
        if (node is not null
            && !TaskExists(sessionId, taskId)
            && NodeRowCount(node, sessionId) >= _maxRowsPerNode)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        Execute(
            """
            INSERT INTO hive_tasks
                (task_id, session_id, role, title, status, claimed_by_node,
                 claimed_by_worker, authenticated, claim_token, result_blob,
                 duration_ms, error_msg, enqueued_at, updated_at, retain_until)
            VALUES
                ($task_id, $session_id, $role, $title, $status, $node,
                 $worker, $auth, $token, $blob,
                 $dur, $err, $enq, $upd, $retain)
            ON CONFLICT(session_id, task_id) DO UPDATE SET
                role              = excluded.role,
                title             = excluded.title,
                status            = excluded.status,
                claimed_by_node   = COALESCE(excluded.claimed_by_node, hive_tasks.claimed_by_node),
                claimed_by_worker = COALESCE(excluded.claimed_by_worker, hive_tasks.claimed_by_worker),
                authenticated     = excluded.authenticated,
                claim_token       = COALESCE(excluded.claim_token, hive_tasks.claim_token),
                result_blob       = COALESCE(excluded.result_blob, hive_tasks.result_blob),
                duration_ms       = COALESCE(excluded.duration_ms, hive_tasks.duration_ms),
                error_msg         = COALESCE(excluded.error_msg, hive_tasks.error_msg),
                updated_at        = excluded.updated_at,
                retain_until      = excluded.retain_until
            """,
            ps =>
            {
                P(ps, "$task_id",    SanId(taskId, MaxId));
                P(ps, "$session_id", SanId(sessionId, MaxId));
                P(ps, "$role",       San(role, MaxRole));
                P(ps, "$title",      San(title, MaxTitle));
                P(ps, "$status",     San(status, MaxStatus));
                P(ps, "$node",       node);
                P(ps, "$worker",     SanId(worker, MaxId));
                P(ps, "$auth",       authenticated ? 1 : 0);
                P(ps, "$token",      SanId(claimToken, MaxId));
                P(ps, "$blob",       San(resultBlob, MaxResultBlob));
                P(ps, "$dur",        durationMs);
                P(ps, "$err",        San(errorMsg, MaxMsg));
                P(ps, "$enq",        enqueuedAt.ToString("o"));
                P(ps, "$upd",        now.ToString("o"));
                P(ps, "$retain",     now.Add(_retention).ToString("o"));
            });
        return true;
    }

    /// <summary>In-place status transition (watchdog re-queue / timeout). No-op if the row
    /// doesn't exist yet.</summary>
    public void UpdateStatus(string sessionId, string taskId, string status)
        => Execute(
            "UPDATE hive_tasks SET status = $s, updated_at = $u " +
            "WHERE session_id = $sess AND task_id = $task",
            ps =>
            {
                P(ps, "$s",    San(status, MaxStatus));
                P(ps, "$u",    DateTime.UtcNow.ToString("o"));
                P(ps, "$sess", SanId(sessionId, MaxId));
                P(ps, "$task", SanId(taskId, MaxId));
            });

    // ── Event rows ───────────────────────────────────────────────────────────────

    /// <summary>Appends one lifecycle event with provenance. Used for remote-submitted
    /// events (POST /hive/events) where the node id and auth result matter.</summary>
    public void AppendEvent(
        string type, string? msg, string? taskId, string? workerId,
        string? sessionId, string? submittedByNode, bool authenticated)
    {
        var now = DateTime.UtcNow;
        Execute(
            """
            INSERT INTO hive_events
                (session_id, type, msg, task_id, worker_id, submitted_by_node,
                 authenticated, created_at, retain_until)
            VALUES
                ($sess, $type, $msg, $task, $worker, $node, $auth, $created, $retain)
            """,
            ps =>
            {
                P(ps, "$sess",    SanId(sessionId, MaxId));
                P(ps, "$type",    San(type, MaxType));
                P(ps, "$msg",     San(msg, MaxMsg));
                P(ps, "$task",    SanId(taskId, MaxId));
                P(ps, "$worker",  SanId(workerId, MaxId));
                P(ps, "$node",    SanId(submittedByNode, MaxId));
                P(ps, "$auth",    authenticated ? 1 : 0);
                P(ps, "$created", now.ToString("o"));
                P(ps, "$retain",  now.Add(_retention).ToString("o"));
            });
    }

    // ── Retention ────────────────────────────────────────────────────────────────

    /// <summary>Deletes hive rows past their retain_until. Returns total rows removed.
    /// Cheap (indexed on retain_until); safe to call on a timer.</summary>
    public int SweepExpired()
    {
        var nowIso = DateTime.UtcNow.ToString("o");
        var n  = Execute("DELETE FROM hive_tasks  WHERE retain_until < $now", ps => P(ps, "$now", nowIso));
        n     += Execute("DELETE FROM hive_events WHERE retain_until < $now", ps => P(ps, "$now", nowIso));
        return n;
    }

    // ── Read side (durable history queries) ──────────────────────────────────────

    /// <summary>Most-recent tasks for a session, newest first.</summary>
    public List<HiveTaskRow> RecentTasks(string sessionId, int limit = 200)
        => Query(
            """
            SELECT task_id, session_id, role, title, status, claimed_by_node,
                   claimed_by_worker, authenticated, claim_token, duration_ms,
                   error_msg, enqueued_at, updated_at
            FROM hive_tasks
            WHERE session_id = $sess
            ORDER BY updated_at DESC
            LIMIT $lim
            """,
            MapTask,
            ps => { P(ps, "$sess", SanId(sessionId, MaxId)); P(ps, "$lim", limit); });

    public int TaskCount()
        => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM hive_tasks") ?? 0);

    public int EventCount()
        => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM hive_events") ?? 0);

    /// <summary>The (possibly truncated) result blob for one task — lazy-loaded so the
    /// history list view doesn't carry 256 KB per row.</summary>
    public string? GetResultBlob(string sessionId, string taskId)
        => Scalar(
            "SELECT result_blob FROM hive_tasks WHERE session_id = $sess AND task_id = $task",
            ps => { P(ps, "$sess", SanId(sessionId, MaxId)); P(ps, "$task", SanId(taskId, MaxId)); })
           as string;

    private bool TaskExists(string sessionId, string taskId)
        => Convert.ToInt32(Scalar(
            "SELECT COUNT(*) FROM hive_tasks WHERE session_id = $sess AND task_id = $task",
            ps => { P(ps, "$sess", SanId(sessionId, MaxId)); P(ps, "$task", SanId(taskId, MaxId)); }) ?? 0) > 0;

    private int NodeRowCount(string node, string sessionId)
        => Convert.ToInt32(Scalar(
            "SELECT COUNT(*) FROM hive_tasks WHERE claimed_by_node = $node AND session_id = $sess",
            ps => { P(ps, "$node", node); P(ps, "$sess", SanId(sessionId, MaxId)); }) ?? 0);

    private static HiveTaskRow MapTask(SqliteDataReader r) => new(
        TaskId:       GetStr(r, "task_id")           ?? "",
        SessionId:    GetStr(r, "session_id")        ?? "",
        Role:         GetStr(r, "role"),
        Title:        GetStr(r, "title"),
        Status:       GetStr(r, "status")            ?? "",
        ClaimedByNode: GetStr(r, "claimed_by_node"),
        ClaimedByWorker: GetStr(r, "claimed_by_worker"),
        Authenticated: (GetInt(r, "authenticated") ?? 0) != 0,
        ClaimToken:   GetStr(r, "claim_token"),
        DurationMs:   GetInt(r, "duration_ms"),
        ErrorMsg:     GetStr(r, "error_msg"),
        EnqueuedAt:   GetStr(r, "enqueued_at")       ?? "",
        UpdatedAt:    GetStr(r, "updated_at")        ?? "");

    // ── Sanitisers (the write-boundary defences) ─────────────────────────────────

    /// <summary>Truncates to maxLen and strips control characters (newlines/tabs included —
    /// they enable log forging). Null in → null out.</summary>
    private static string? San(string? v, int maxLen)
    {
        if (v is null) return null;
        if (v.Length > maxLen) v = v[..maxLen];
        var sb = new StringBuilder(v.Length);
        foreach (var ch in v)
            if (!char.IsControl(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Identifier sanitiser: keeps only [A-Za-z0-9._-], truncates to maxLen.
    /// Used for node id / worker id / claim token / session id / task id — fields that must
    /// never carry separators or control chars into logs or downstream display.</summary>
    private static string? SanId(string? v, int maxLen)
    {
        if (v is null) return null;
        if (v.Length > maxLen) v = v[..maxLen];
        var sb = new StringBuilder(v.Length);
        foreach (var ch in v)
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-') sb.Append(ch);
        return sb.ToString();
    }
}

/// <summary>A durable hive task row (read-side projection).</summary>
public sealed record HiveTaskRow(
    string  TaskId,
    string  SessionId,
    string? Role,
    string? Title,
    string  Status,
    string? ClaimedByNode,
    string? ClaimedByWorker,
    bool    Authenticated,
    string? ClaimToken,
    int?    DurationMs,
    string? ErrorMsg,
    string  EnqueuedAt,
    string  UpdatedAt);
