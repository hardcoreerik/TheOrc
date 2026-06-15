// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND Phase 3 — Warchief-side distributed task queue (port 7079).
///
/// SwarmSession calls EnqueueAndWaitAsync() instead of running a task locally.
/// The task enters the queue; any connected worker node polls /hive/tasks/next,
/// claims it, executes it, and POSTs the result back. The TCS resolves and
/// SwarmSession continues exactly as if the task ran locally.
///
/// Timeout and re-queue rules:
///   • Pending timeout: 60s — if no worker claims, TCS resolves with null so
///     SwarmSession can fall back to local execution.
///   • Heartbeat timeout: 45s — if a claimed task stops heartbeating, it is
///     re-queued (new claim token) so another worker can pick it up.
///   • Stale /complete guard: a /complete or /fail is only accepted from the
///     worker currently holding the claim token; stale workers get 409.
///
/// Endpoint map (all under /hive/):
///   GET  /hive/tasks/next?lanes=researcher,coder   → next claimable bundle (204 if empty)
///   POST /hive/tasks/{id}/claim                    → atomic claim (409 if already claimed)
///   POST /hive/tasks/{id}/heartbeat                → keep-alive signal
///   POST /hive/tasks/{id}/complete                 → submit result (409 if stale worker)
///   POST /hive/tasks/{id}/fail                     → report failure  (409 if stale worker)
///   GET  /hive/tasks/status                        → full queue snapshot
///   GET  /hive/session/context                     → session context for workers
///   POST /hive/events                              → worker pushes a lifecycle event
///   GET  /hive/events?since=&lt;seq&gt;               → poll events after seq (-1 = tail)
/// </summary>
public sealed class HiveTaskQueue : IDisposable
{
    public const int QueuePort             = 7079;
    private const int PendingTimeoutSec    = 60;   // no-claim → fallback to local
    private const int HeartbeatTimeoutSec  = 45;   // claimed but silent → re-queue

    // ── Internal queue state ─────────────────────────────────────────────────

    private sealed class QueuedTask
    {
        public HiveTaskBundle                       Bundle        { get; init; } = null!;
        public string                               Status        { get; set; } = "pending";
        public string?                              ClaimedBy     { get; set; }
        public string?                              ClaimedByUrl  { get; set; }
        public DateTime?                            ClaimedAt     { get; set; }
        public DateTime?                            LastHeartbeat { get; set; }
        public DateTime                             EnqueuedAt    { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Rotates on every claim (including re-claim after watchdog re-queue).
        /// Workers MUST include this in /complete and /fail; a mismatch means the
        /// result is from a stale/re-queued worker and is rejected with 409.
        /// </summary>
        public string                               ClaimToken    { get; set; } = "";

        public TaskCompletionSource<HiveTaskResult?> CompletionTcs { get; init; } = new();
    }

    private readonly ConcurrentDictionary<string, QueuedTask> _tasks = new();
    private readonly SemaphoreSlim  _claimLock = new(1, 1);
    private readonly System.Threading.Timer _watchdog;
    private HiveSessionContext _sessionCtx = new();

    private HttpListener?           _listener;
    private CancellationTokenSource _cts = new();
    private int                     _inFlight;   // accepted requests still being handled

    // All callers on port 7079 are enrolled worker nodes — no grace period.
    // "queue" persistence key survives the nonce cache across restart (replay protection).
    private readonly HiveAuthMiddleware _auth = new("queue") { GracePeriodActive = false };

    // ── Public surface ────────────────────────────────────────────────────────

    public event Action<string>? OnLog;

    /// <summary>
    /// In-memory ring buffer of task lifecycle events. UI polls this directly
    /// (same process); remote monitors use GET /hive/events?since=&lt;seq&gt;.
    /// </summary>
    public HiveEventBus Events { get; } = new();

    /// <summary>
    /// Base URL workers POST results to (e.g. "http://192.168.1.10:7079").
    /// Set after bind so it accurately reflects which address is actually reachable.
    /// Falls back to localhost when the wildcard prefix binding fails (non-elevated).
    /// </summary>
    public string BaseUrl { get; private set; } = "";

    public bool IsListening => _listener?.IsListening == true;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public HiveTaskQueue()
    {
        _watchdog = new System.Threading.Timer(
            CheckTimeouts, null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void Start(HiveSessionContext sessionCtx, int port = QueuePort)
    {
        _sessionCtx = sessionCtx;
        _listener   = new HttpListener();

        // Try wildcard bind (requires admin or a netsh ACL entry from the installer).
        // BLOCKER-2 fix: BaseUrl is set AFTER we know which binding succeeded, so
        // remote workers are not handed a LAN IP that resolves to localhost only.
        var lanIp        = HiveRpcWorker.LocalAddresses().FirstOrDefault() ?? "127.0.0.1";
        var wideSucceeded = TryBind($"http://+:{port}/hive/");

        if (wideSucceeded)
        {
            BaseUrl = $"http://{lanIp}:{port}";
        }
        else if (TryBind($"http://localhost:{port}/hive/"))
        {
            // Bound to loopback only — workers must be on the same machine
            BaseUrl = $"http://localhost:{port}";
            Log($"⚠ HiveTaskQueue bound to localhost only (no admin ACL). " +
                $"Workers on remote machines cannot connect. " +
                $"Run OrchestratorSetup to open the firewall/ACL for port {port}.");
        }

        if (_listener.IsListening)
        {
            _ = ServeAsync(_cts.Token);
            Log($"🐝 HiveTaskQueue listening on :{port} — workers connect to {BaseUrl}");
        }
        else
        {
            Log($"⚠ HiveTaskQueue failed to bind on port {port}");
        }
    }

    public void UpdateSessionContext(HiveSessionContext ctx) => _sessionCtx = ctx;

    // ── Dispatch API (called by SwarmSession) ─────────────────────────────────

    /// <summary>
    /// Enqueues a task bundle and awaits its completion by a remote worker.
    /// Returns the worker's result, or null when:
    ///   • the session CancellationToken fires (SwarmSession.Stop()), or
    ///   • no worker claims the task within PendingTimeoutSec (60s) →
    ///     SwarmSession.DispatchToQueueAsync() then falls back to local execution.
    /// </summary>
    public async Task<HiveTaskResult?> EnqueueAndWaitAsync(
        string taskId, HiveTaskBundle bundle, CancellationToken ct)
    {
        var entry = new QueuedTask { Bundle = bundle };
        if (!_tasks.TryAdd(taskId, entry))
            throw new InvalidOperationException($"Task {taskId} already in queue");

        Events.Append("task_queued", $"[{bundle.Role}] {bundle.Title}",
            taskId, sessionId: _sessionCtx.SessionId);

        // Session stop cancels all waits immediately
        using var reg = ct.Register(() => entry.CompletionTcs.TrySetResult(null));

        // Pending timeout is enforced by CheckTimeouts() watchdog — no inline timer
        // needed here; the watchdog fires every 10s and resolves TCS with null when
        // the task has been pending > PendingTimeoutSec with no claim.
        return await entry.CompletionTcs.Task;
    }

    /// <summary>Cancels all pending tasks (called on SwarmSession.Stop()).</summary>
    public void CancelAll()
    {
        foreach (var entry in _tasks.Values)
            entry.CompletionTcs.TrySetResult(null);
    }

    // ── HTTP server ──────────────────────────────────────────────────────────

    private bool TryBind(string prefix)
    {
        try
        {
            _listener!.Prefixes.Add(prefix);
            _listener.Start();
            return true;
        }
        catch
        {
            try { _listener!.Prefixes.Clear(); } catch { }
            return false;
        }
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                Interlocked.Increment(ref _inFlight);   // balanced by decrement in HandleAsync finally
                _ = HandleAsync(ctx);
            }
            catch { break; }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req    = ctx.Request;
            var method = req.HttpMethod.ToUpperInvariant();
            var path   = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

            // Read body once with a 1 MB hard cap (prevents oversized-body DoS).
            const int MaxBodyBytes = 1 * 1024 * 1024;
            byte[] body;
            using (var ms = new MemoryStream())
            {
                var buf   = new byte[8192];
                int total = 0, read;
                while ((read = await req.InputStream.ReadAsync(buf)) > 0)
                {
                    total += read;
                    if (total > MaxBodyBytes)
                    {
                        ctx.Response.StatusCode = 413;
                        WriteJson(ctx, new { error = "request body too large" });
                        return;
                    }
                    ms.Write(buf, 0, read);
                }
                body = ms.ToArray();
            }

            // All task-queue endpoints require an enrolled peer — no grace period.
            // Workers sign outbound requests via HiveWorkerAgent.SignIfPaired().
            var authResult = _auth.Validate(req, body);
            if (!authResult.Ok)
            {
                ctx.Response.StatusCode = 401;
                WriteJson(ctx, new { error = authResult.Reason ?? "unauthorized" });
                return;
            }

            if (method == "GET"  && path == "/hive/tasks/next")
            { await HandleGetNextAsync(ctx); return; }

            if (method == "GET"  && path == "/hive/tasks/status")
            { await HandleGetStatusAsync(ctx); return; }

            if (method == "GET"  && path == "/hive/session/context")
            { await HandleGetContextAsync(ctx); return; }

            if (method == "POST" && path == "/hive/events")
            { await HandlePostEventAsync(ctx, body); return; }

            if (method == "GET"  && path == "/hive/events")
            { HandleGetEventsAsync(ctx); return; }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (method == "POST" && parts.Length == 4
                && parts[0] == "hive" && parts[1] == "tasks")
            {
                var taskId = parts[2];
                var action = parts[3];
                switch (action)
                {
                    case "claim":     await HandleClaimAsync(ctx, taskId, body);     return;
                    case "heartbeat": await HandleHeartbeatAsync(ctx, taskId, body); return;
                    case "complete":  await HandleCompleteAsync(ctx, taskId, body);  return;
                    case "fail":      await HandleFailAsync(ctx, taskId, body);      return;
                }
            }

            ctx.Response.StatusCode = 404;
        }
        catch { }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            try { ctx.Response.Close(); } catch { }
        }
    }

    // ── Endpoint handlers ─────────────────────────────────────────────────────

    private Task HandleGetNextAsync(HttpListenerContext ctx)
    {
        var lanesParam = ctx.Request.QueryString["lanes"] ?? "";
        var lanes = lanesParam
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().ToLower())
            .ToHashSet();

        QueuedTask? found = null;
        foreach (var (_, entry) in _tasks)
        {
            if (entry.Status != "pending") continue;
            if (lanes.Count > 0 && !lanes.Contains(entry.Bundle.Role.ToLower())) continue;
            found = entry;
            break;
        }

        if (found is null)
        {
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        }

        WriteJson(ctx, found.Bundle);
        return Task.CompletedTask;
    }

    private async Task HandleClaimAsync(HttpListenerContext ctx, string taskId, byte[] body)
    {
        await _claimLock.WaitAsync();
        try
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.Status != "pending")
            {
                ctx.Response.StatusCode = 409;
                return;
            }

            var req = ReadJson<HiveClaimRequest>(body);

            // Rotate claim token on every (re-)claim so stale /complete calls can be rejected
            entry.ClaimToken    = Guid.NewGuid().ToString("N")[..12];
            entry.Status        = "claimed";
            entry.ClaimedBy     = req?.WorkerId ?? "unknown";
            entry.ClaimedByUrl  = req?.WorkerUrl ?? "";
            entry.ClaimedAt     = DateTime.UtcNow;
            entry.LastHeartbeat = DateTime.UtcNow;

            Log($"🐝 [{entry.Bundle.Role}] '{entry.Bundle.Title}' claimed by {entry.ClaimedBy} (token={entry.ClaimToken})");
            Events.Append("task_claimed",
                $"[{entry.Bundle.Role}] {entry.Bundle.Title} → {entry.ClaimedBy}",
                taskId, entry.ClaimedBy ?? "");
            WriteJson(ctx, new { taskId, status = "claimed", claimToken = entry.ClaimToken });
        }
        finally
        {
            _claimLock.Release();
        }
    }

    private async Task HandleHeartbeatAsync(HttpListenerContext ctx, string taskId, byte[] body)
    {
        var hb = ReadJson<HiveHeartbeatRequest>(body);

        await _claimLock.WaitAsync();
        try
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.Status != "claimed")
            {
                WriteJson(ctx, new { taskId, status = "not-claimed" });
                return;
            }

            // Reject heartbeats that don't carry the current claim token.
            // Held under _claimLock so CheckTimeouts cannot rotate the token concurrently.
            if (!string.IsNullOrEmpty(entry.ClaimToken)
                && (hb is null || hb.ClaimToken != entry.ClaimToken))
            {
                ctx.Response.StatusCode = 409;
                WriteJson(ctx, new { taskId, status = "stale" });
                return;
            }

            entry.LastHeartbeat = DateTime.UtcNow;
            WriteJson(ctx, new { taskId, status = "alive" });
        }
        finally { _claimLock.Release(); }
    }

    private async Task HandleCompleteAsync(HttpListenerContext ctx, string taskId, byte[] body)
    {
        var result = ReadJson<HiveTaskResult>(body);

        QueuedTask? entry = null;
        await _claimLock.WaitAsync();
        try
        {
            if (!_tasks.TryGetValue(taskId, out entry))
            { ctx.Response.StatusCode = 404; return; }

            if (entry.Status is not "claimed")
            { ctx.Response.StatusCode = 409; return; }

            if (result is null)
            { ctx.Response.StatusCode = 400; return; }

            if (!string.IsNullOrEmpty(entry.ClaimToken) && result.ClaimToken != entry.ClaimToken)
            {
                Log($"⚠ Stale /complete for '{entry.Bundle.Title}' from {result.WorkerId} " +
                    $"(token mismatch — re-queued task already claimed by {entry.ClaimedBy})");
                ctx.Response.StatusCode = 409;
                return;
            }

            // Mark completed under lock so CheckTimeouts cannot re-queue concurrently.
            entry.Status = "completed";
        }
        finally { _claimLock.Release(); }

        // Resolve TCS outside lock — continuations run inline and must not re-enter _claimLock.
        entry!.CompletionTcs.TrySetResult(result);

        Log($"🐝 [{entry.Bundle.Role}] '{entry.Bundle.Title}' ✅ completed by {result!.WorkerId} " +
            $"({result.DurationMs / 1000.0:F1}s, {result.Result.Length} chars)");
        Events.Append("task_complete",
            $"[{result.WorkerId}] {entry.Bundle.Title} ✓ {result.DurationMs / 1000.0:F1}s · {result.Result.Length} chars",
            taskId, result.WorkerId);

        WriteJson(ctx, new { taskId, status = "completed" });
    }

    private async Task HandleFailAsync(HttpListenerContext ctx, string taskId, byte[] body)
    {
        var result = ReadJson<HiveTaskResult>(body);

        QueuedTask? entry    = null;
        HiveTaskResult? failResult = null;
        await _claimLock.WaitAsync();
        try
        {
            if (!_tasks.TryGetValue(taskId, out entry))
            { ctx.Response.StatusCode = 404; return; }

            if (entry.Status is not "claimed")
            { ctx.Response.StatusCode = 409; return; }

            if (result is not null && !string.IsNullOrEmpty(entry.ClaimToken)
                && result.ClaimToken != entry.ClaimToken)
            { ctx.Response.StatusCode = 409; return; }

            entry.Status = "failed";
            failResult   = result ?? new HiveTaskResult
            {
                TaskId   = taskId,
                WorkerId = entry.ClaimedBy ?? "unknown",
                Status   = "failed",
                ErrorMsg = "Worker reported failure with no details",
            };
        }
        finally { _claimLock.Release(); }

        entry!.CompletionTcs.TrySetResult(failResult);

        Log($"⚠ [{entry.Bundle.Role}] '{entry.Bundle.Title}' failed by {failResult!.WorkerId}: {failResult.ErrorMsg}");
        Events.Append("task_failed",
            $"[{failResult.WorkerId}] {entry.Bundle.Title} ✗ {failResult.ErrorMsg}",
            taskId, failResult.WorkerId);
        WriteJson(ctx, new { taskId, status = "failed" });
    }

    private Task HandleGetStatusAsync(HttpListenerContext ctx)
    {
        var status = new HiveQueueStatus
        {
            SessionId  = _sessionCtx.SessionId,
            Total      = _tasks.Count,
            Pending    = _tasks.Values.Count(e => e.Status == "pending"),
            InProgress = _tasks.Values.Count(e => e.Status == "claimed"),
            Completed  = _tasks.Values.Count(e => e.Status == "completed"),
            Failed     = _tasks.Values.Count(e => e.Status == "failed"),
            Tasks      = _tasks.Select(kvp => new HiveQueueEntry
            {
                TaskId    = kvp.Key,
                Title     = kvp.Value.Bundle.Title,
                Role      = kvp.Value.Bundle.Role,
                Status    = kvp.Value.Status,
                ClaimedBy = kvp.Value.ClaimedBy,
                ClaimedAt = kvp.Value.ClaimedAt,
            }).ToList(),
        };

        WriteJson(ctx, status);
        return Task.CompletedTask;
    }

    private Task HandleGetContextAsync(HttpListenerContext ctx)
    {
        WriteJson(ctx, _sessionCtx);
        return Task.CompletedTask;
    }

    // ── Timeout watchdog ──────────────────────────────────────────────────────

    private void CheckTimeouts(object? _)
    {
        var now     = DateTime.UtcNow;
        var toEvict = new List<string>();

        foreach (var (id, entry) in _tasks)
        {
            // Captured outside the lock for log/event calls after release.
            string? logMsg    = null;
            string? evType    = null;
            string? evMsg     = null;
            string? evWho     = null;
            bool    resolveNull = false;

            _claimLock.Wait();
            try
            {
                switch (entry.Status)
                {
                    case "pending":
                        if (now - entry.EnqueuedAt > TimeSpan.FromSeconds(PendingTimeoutSec))
                        {
                            entry.Status = "timeout";
                            resolveNull  = true;
                            logMsg  = $"⚠ Task '{entry.Bundle.Title}' unclaimed after {PendingTimeoutSec}s — resolving for local fallback";
                            evType  = "task_timeout";
                            evMsg   = $"{entry.Bundle.Title} unclaimed after {PendingTimeoutSec}s → local fallback";
                        }
                        break;

                    case "claimed":
                        if (entry.LastHeartbeat is null ||
                            now - entry.LastHeartbeat.Value > TimeSpan.FromSeconds(HeartbeatTimeoutSec))
                        {
                            var who         = entry.ClaimedBy ?? "unknown";
                            entry.Status        = "pending";
                            entry.ClaimedBy     = null;
                            entry.ClaimedByUrl  = null;
                            entry.ClaimedAt     = null;
                            entry.LastHeartbeat = null;
                            entry.ClaimToken    = Guid.NewGuid().ToString();
                            logMsg = $"⚠ Task '{entry.Bundle.Title}' heartbeat timeout from {who} — re-queued";
                            evType = "task_requeued";
                            evMsg  = $"{entry.Bundle.Title} lost heartbeat from {who} → re-queued";
                            evWho  = who;
                        }
                        break;

                    case "completed":
                    case "failed":
                    case "timeout":
                        if (now - entry.EnqueuedAt > TimeSpan.FromMinutes(5))
                            toEvict.Add(id);
                        break;
                }
            }
            finally { _claimLock.Release(); }

            // TCS and logging outside the lock — TCS continuations must not re-enter _claimLock.
            if (resolveNull)  entry.CompletionTcs.TrySetResult(null);
            if (logMsg is not null) Log(logMsg);
            if (evType is not null) Events.Append(evType, evMsg!, id, evWho ?? "");
        }

        foreach (var id in toEvict)
            _tasks.TryRemove(id, out var __evicted);
    }

    // ── Event endpoints ───────────────────────────────────────────────────────

    private async Task HandlePostEventAsync(HttpListenerContext ctx, byte[] body)
    {
        var ev = ReadJson<HiveEventPost>(body);
        if (ev is null) { ctx.Response.StatusCode = 400; return; }
        Events.Append(ev.Type, ev.Msg, ev.TaskId, ev.WorkerId, ev.SessionId);
        WriteJson(ctx, new { status = "ok", seq = Events.HeadSeq });
    }

    private void HandleGetEventsAsync(HttpListenerContext ctx)
    {
        var sinceStr = ctx.Request.QueryString["since"] ?? "-1";
        long.TryParse(sinceStr, out var since);
        WriteJson(ctx, Events.Since(since));
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
    };

    private static void WriteJson(HttpListenerContext ctx, object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, _json);
        ctx.Response.ContentType     = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
    }

    private static T? ReadJson<T>(byte[] body)
    {
        try { return JsonSerializer.Deserialize<T>(body, _json); }
        catch { return default; }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    // ── IDisposable ───────────────────────────────────────────────────────────

    /// <summary>
    /// Blocks until all accepted requests finish handling, or 2s elapses. Called on
    /// shutdown so the nonce flush snapshot includes nonces from in-flight requests.
    /// </summary>
    private void DrainInFlight()
    {
        var spin = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _inFlight) > 0 && spin.ElapsedMilliseconds < 2000)
            Thread.Sleep(10);
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _cts.Cancel();
        CancelAll();
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        // Drain in-flight handlers (best-effort, 2s cap) so most nonces land in a single
        // snapshot, then seal+flush. The seal (flush-on-every-record) guarantees the
        // security property: every ACCEPTED request's nonce reaches disk — a handler that
        // reaches RecordNonce after the drain still self-persists, so correctness does not
        // depend on the 2s cap. A handler abandoned before RecordNonce never accepted the
        // request (validation happens inside RecordNonce), so it is not a replay vector.
        DrainInFlight();
        _auth.FlushAndSealForShutdown();
        _claimLock.Dispose();
    }
}
