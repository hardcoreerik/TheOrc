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
/// Heartbeat watchdog: tasks claimed but silent for >45s are re-queued so
/// another worker can pick them up (crash recovery, network drop).
///
/// Endpoint map (all under /hive/):
///   GET  /hive/tasks/next?lanes=researcher,coder   → next claimable bundle (204 if empty)
///   POST /hive/tasks/{id}/claim                    → atomic claim (409 if already claimed)
///   POST /hive/tasks/{id}/heartbeat                → keep-alive signal
///   POST /hive/tasks/{id}/complete                 → submit result
///   POST /hive/tasks/{id}/fail                     → report failure
///   GET  /hive/tasks/status                        → full queue snapshot
///   GET  /hive/session/context                     → session context for workers
/// </summary>
public sealed class HiveTaskQueue : IDisposable
{
    public const int QueuePort = 7079;

    // ── Internal queue state ─────────────────────────────────────────────────

    private sealed class QueuedTask
    {
        public HiveTaskBundle                    Bundle          { get; init; } = null!;
        public string                            Status          { get; set; } = "pending";
        public string?                           ClaimedBy       { get; set; }
        public string?                           ClaimedByUrl    { get; set; }
        public DateTime?                         ClaimedAt       { get; set; }
        public DateTime?                         LastHeartbeat   { get; set; }
        public TaskCompletionSource<HiveTaskResult?> CompletionTcs { get; init; } = new();
    }

    private readonly ConcurrentDictionary<string, QueuedTask> _tasks = new();
    private readonly SemaphoreSlim  _claimLock = new(1, 1);
    private readonly System.Threading.Timer _watchdog;
    private HiveSessionContext _sessionCtx = new();

    private HttpListener?           _listener;
    private CancellationTokenSource _cts = new();

    // ── Public surface ────────────────────────────────────────────────────────

    public event Action<string>? OnLog;

    /// <summary>Base URL workers POST results to (e.g. "http://192.168.1.10:7079").</summary>
    public string BaseUrl { get; private set; } = "";

    public bool IsListening => _listener?.IsListening == true;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public HiveTaskQueue()
    {
        _watchdog = new System.Threading.Timer(
            CheckHeartbeats, null,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void Start(HiveSessionContext sessionCtx, int port = QueuePort)
    {
        _sessionCtx = sessionCtx;

        // Determine the reachable IP so workers on other machines can POST back
        var ip = HiveRpcWorker.LocalAddresses().FirstOrDefault() ?? "127.0.0.1";
        BaseUrl = $"http://{ip}:{port}";

        _listener = new HttpListener();
        var started = TryBind($"http://+:{port}/hive/");
        if (!started)
            TryBind($"http://localhost:{port}/hive/");

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
    /// Returns the worker's result, or null if the session was cancelled.
    /// This is the main integration point for SwarmSession distributed mode.
    /// </summary>
    public async Task<HiveTaskResult?> EnqueueAndWaitAsync(
        string taskId, HiveTaskBundle bundle, CancellationToken ct)
    {
        var entry = new QueuedTask { Bundle = bundle };
        if (!_tasks.TryAdd(taskId, entry))
            throw new InvalidOperationException($"Task {taskId} already in queue");

        using var reg = ct.Register(() => entry.CompletionTcs.TrySetResult(null));

        return await entry.CompletionTcs.Task;
    }

    /// <summary>
    /// Cancels all pending tasks (called on SwarmSession.Stop()).
    /// Waiting EnqueueAndWaitAsync calls return null immediately.
    /// </summary>
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
                _ = HandleAsync(ctx);
            }
            catch { break; }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            var path   = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";

            // GET /hive/tasks/next
            if (method == "GET" && path == "/hive/tasks/next")
            { await HandleGetNextAsync(ctx); return; }

            // GET /hive/tasks/status
            if (method == "GET" && path == "/hive/tasks/status")
            { await HandleGetStatusAsync(ctx); return; }

            // GET /hive/session/context
            if (method == "GET" && path == "/hive/session/context")
            { await HandleGetContextAsync(ctx); return; }

            // POST /hive/tasks/{id}/{action}
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (method == "POST" && parts.Length == 4
                && parts[0] == "hive" && parts[1] == "tasks")
            {
                var taskId = parts[2];
                var action = parts[3];
                switch (action)
                {
                    case "claim":     await HandleClaimAsync(ctx, taskId);     return;
                    case "heartbeat": await HandleHeartbeatAsync(ctx, taskId); return;
                    case "complete":  await HandleCompleteAsync(ctx, taskId);  return;
                    case "fail":      await HandleFailAsync(ctx, taskId);      return;
                }
            }

            ctx.Response.StatusCode = 404;
        }
        catch { }
        finally
        {
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

    private async Task HandleClaimAsync(HttpListenerContext ctx, string taskId)
    {
        await _claimLock.WaitAsync();
        try
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.Status != "pending")
            {
                ctx.Response.StatusCode = 409;
                return;
            }

            var req = await ReadJsonAsync<HiveClaimRequest>(ctx.Request);
            entry.Status        = "claimed";
            entry.ClaimedBy     = req?.WorkerId ?? "unknown";
            entry.ClaimedByUrl  = req?.WorkerUrl ?? "";
            entry.ClaimedAt     = DateTime.UtcNow;
            entry.LastHeartbeat = DateTime.UtcNow;

            Log($"🐝 [{entry.Bundle.Role}] '{entry.Bundle.Title}' claimed by {entry.ClaimedBy}");
            WriteJson(ctx, new { taskId, status = "claimed" });
        }
        finally
        {
            _claimLock.Release();
        }
    }

    private Task HandleHeartbeatAsync(HttpListenerContext ctx, string taskId)
    {
        if (_tasks.TryGetValue(taskId, out var entry) && entry.Status == "claimed")
            entry.LastHeartbeat = DateTime.UtcNow;

        WriteJson(ctx, new { taskId, status = "alive" });
        return Task.CompletedTask;
    }

    private async Task HandleCompleteAsync(HttpListenerContext ctx, string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var entry))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        // Idempotent: if already completed (e.g. a re-queued task that was re-run),
        // the second completion is silently ignored.
        if (entry.Status == "completed")
        {
            ctx.Response.StatusCode = 409;
            return;
        }

        var result = await ReadJsonAsync<HiveTaskResult>(ctx.Request);
        if (result is null)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        entry.Status = "completed";
        entry.CompletionTcs.TrySetResult(result);

        Log($"🐝 [{entry.Bundle.Role}] '{entry.Bundle.Title}' ✅ completed by {result.WorkerId} " +
            $"({result.DurationMs / 1000.0:F1}s, {result.Result.Length} chars)");

        WriteJson(ctx, new { taskId, status = "completed" });
    }

    private async Task HandleFailAsync(HttpListenerContext ctx, string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var entry))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var result = await ReadJsonAsync<HiveTaskResult>(ctx.Request);
        entry.Status = "failed";

        var failResult = result ?? new HiveTaskResult
        {
            TaskId   = taskId,
            WorkerId = entry.ClaimedBy ?? "unknown",
            Status   = "failed",
            ErrorMsg = "Worker reported failure with no details"
        };
        entry.CompletionTcs.TrySetResult(failResult);

        Log($"⚠ [{entry.Bundle.Role}] '{entry.Bundle.Title}' failed by {failResult.WorkerId}: {failResult.ErrorMsg}");
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

    // ── Heartbeat watchdog ────────────────────────────────────────────────────

    private void CheckHeartbeats(object? _)
    {
        var timeout = TimeSpan.FromSeconds(45);
        var now     = DateTime.UtcNow;

        foreach (var (id, entry) in _tasks)
        {
            if (entry.Status != "claimed") continue;
            if (entry.LastHeartbeat is null || now - entry.LastHeartbeat.Value > timeout)
            {
                var who = entry.ClaimedBy ?? "unknown";
                entry.Status        = "pending";
                entry.ClaimedBy     = null;
                entry.ClaimedByUrl  = null;
                entry.ClaimedAt     = null;
                entry.LastHeartbeat = null;
                Log($"⚠ Task '{entry.Bundle.Title}' heartbeat timeout from {who} — re-queued");
            }
        }
    }

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy         = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition       = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented                = false,
    };

    private static void WriteJson(HttpListenerContext ctx, object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, _json);
        ctx.Response.ContentType     = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest req)
    {
        try
        {
            using var ms = new System.IO.MemoryStream();
            await req.InputStream.CopyToAsync(ms);
            ms.Position = 0;
            return await JsonSerializer.DeserializeAsync<T>(ms, _json);
        }
        catch { return default; }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _watchdog.Dispose();
        _cts.Cancel();
        CancelAll();
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        _claimLock.Dispose();
    }
}
