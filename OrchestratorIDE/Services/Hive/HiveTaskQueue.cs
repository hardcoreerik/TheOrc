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
///   POST /hive/tasks/submit                        → remotely submit a new task (202 + taskId)
///   GET  /hive/tasks/{id}                          → single-task status/result lookup
///   POST /hive/tasks/{id}/claim                    → atomic claim (409 if already claimed)
///   POST /hive/tasks/{id}/heartbeat                → keep-alive signal
///   POST /hive/tasks/{id}/complete                 → submit result (409 if stale worker)
///   POST /hive/tasks/{id}/fail                     → report failure  (409 if stale worker)
///   GET  /hive/tasks/status                        → full queue snapshot
///   GET  /hive/session/context                     → session context for workers
///   POST /hive/events                              → worker pushes a lifecycle event
///   GET  /hive/events?since=&lt;seq&gt;               → poll events after seq (-1 = tail)
///
/// POST /hive/tasks/submit (added 2026-06-24) closes a real gap: previously the ONLY way a
/// task ever entered this queue was SwarmSession calling EnqueueAndWaitAsync() in-process —
/// there was no way to remotely dispatch a task to a Warband from anywhere. Same auth gate as
/// every other endpoint here (enrolled peers only, fail-closed); no new trust mechanism.
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
        /// <summary>Session id frozen at enqueue — immune to UpdateSessionContext rollovers.</summary>
        public string                               SessionId     { get; init; } = "";

        /// <summary>
        /// Rotates on every claim (including re-claim after watchdog re-queue).
        /// Workers MUST include this in /complete and /fail; a mismatch means the
        /// result is from a stale/re-queued worker and is rejected with 409.
        /// </summary>
        public string                               ClaimToken    { get; set; } = "";

        /// <summary>Set on completion so GET /hive/tasks/{id} can return the result to a
        /// remote submitter — the TCS alone only reaches the in-process EnqueueAndWaitAsync
        /// awaiter, not an external HTTP poller.</summary>
        public string?                              ResultText    { get; set; }
        public HiveTaskResult?                      StructuredResult { get; set; }
        /// <summary>Set on failure, same reasoning as <see cref="ResultText"/>.</summary>
        public string?                              ErrorMsg      { get; set; }

        /// <summary>Verification leases are internal siblings and do not increase logical campaign totals.</summary>
        public bool                                 IsVerification { get; init; }
        public string?                              VerificationParentTaskId { get; init; }

        public TaskCompletionSource<HiveTaskResult?> CompletionTcs { get; init; } = new();
    }

    private readonly ConcurrentDictionary<string, QueuedTask> _tasks = new();
    private readonly ConcurrentDictionary<string, (string Name, string Status)> _campaigns = new();
    private readonly SemaphoreSlim  _claimLock = new(1, 1);
    private readonly System.Threading.Timer _watchdog;
    private HiveSessionContext _sessionCtx = new();

    private HttpListener?           _listener;
    private CancellationTokenSource _cts = new();
    private int                     _inFlight;   // accepted requests still being handled

    // All callers on port 7079 are enrolled worker nodes — no grace period.
    // "queue" persistence key survives the nonce cache across restart (replay protection).
    private readonly HiveAuthMiddleware _auth = new("queue") { GracePeriodActive = false };

    /// <summary>
    /// Optional durable store for hive task/event history (Phase 4). Set once at startup
    /// (MainWindow). Null = no persistence. Every write is best-effort — a DB failure must
    /// never break a swarm run, exactly like <see cref="Swarm.DatasetCapture.Repository"/>.
    /// </summary>
    public static Data.HiveRepository? Repository { get; set; }
    public static Data.CampaignRepository? CampaignRepository { get; set; }
    public ContentAddressedStore? ArtifactStore { get; set; }
    public ContentAddressedStore? ModelStore { get; set; }

    // Retention sweep is throttled — the watchdog ticks every 10s but rows live for days.
    private DateTime _lastSweep = DateTime.MinValue;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

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

    // Set once from TryBind's own return value during Start() -- never re-read
    // _listener.IsListening afterward, since a failed fallback bind can leave the listener
    // disposed/unusable and IsListening's getter throws ObjectDisposedException in that state
    // rather than returning false (Codex CLI BLOCKER, 2026-06-20).
    private bool _bound;

    public bool IsListening => _bound;

    /// True only if the wildcard "+" bind succeeded (BaseUrl is LAN/Tailscale-reachable,
    /// not just localhost).
    public bool IsRemoteReachable { get; private set; }

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
        var bound         = wideSucceeded;

        if (wideSucceeded)
        {
            BaseUrl = $"http://{lanIp}:{port}";
        }
        else
        {
            // A failed Start() (e.g. the wildcard bind needs admin rights or a URL ACL
            // reservation) can leave the listener disposed internally -- replace it
            // before the fallback attempt rather than reusing it. Same root cause and
            // fix as HiveNodeServer.Start() (2026-06-20). Close() is best-effort: the
            // listener may already be in that same broken state, so swallow rather
            // than risk a second exception while merely cleaning up.
            try { _listener.Close(); } catch { /* already disposed/unusable */ }
            _listener = new HttpListener();
            bound = TryBind($"http://localhost:{port}/hive/");
            if (bound)
            {
                // Bound to loopback only — workers must be on the same machine
                BaseUrl = $"http://localhost:{port}";
                Log($"⚠ HiveTaskQueue bound to localhost only (no admin ACL). " +
                    $"Workers on remote machines cannot connect. " +
                    $"Run OrchestratorSetup to open the firewall/ACL for port {port}.");
            }
        }

        // Track success via TryBind's own return value rather than re-reading
        // _listener.IsListening afterward -- if the fallback bind also failed, the
        // listener may be in the same disposed-but-not-null state, and IsListening's
        // getter can throw ObjectDisposedException the same way Prefixes' did.
        _bound            = bound;
        IsRemoteReachable = wideSucceeded;
        if (bound)
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
        // The Warchief's own enqueue is local (not authenticated, no node) — same as before
        // this was extracted into EnqueueCore.
        var entry = EnqueueCore(taskId, bundle, authenticated: false, authNode: null);

        // Session stop cancels all waits immediately
        using var reg = ct.Register(() => entry.CompletionTcs.TrySetResult(null));

        // Pending timeout is enforced by CheckTimeouts() watchdog — no inline timer
        // needed here; the watchdog fires every 10s and resolves TCS with null when
        // the task has been pending > PendingTimeoutSec with no claim.
        return await entry.CompletionTcs.Task;
    }

    /// <summary>
    /// Submits a new task WITHOUT waiting for completion — backs POST /hive/tasks/submit.
    /// The remote caller polls GET /hive/tasks/{id} (or /tasks/status) for the result; this
    /// queue's existing claim/heartbeat/complete/watchdog lifecycle handles everything else
    /// identically to a task enqueued via EnqueueAndWaitAsync. Generates its own TaskId since
    /// an external submitter has no reason to know the queue's ID scheme.
    /// </summary>
    private string EnqueueRemote(HiveTaskBundle bundle, string? authNode)
    {
        var taskId = Guid.NewGuid().ToString("N");
        bundle.TaskId = taskId;
        EnqueueCore(taskId, bundle, authenticated: true, authNode);
        return taskId;
    }

    /// <summary>Shared by EnqueueAndWaitAsync and EnqueueRemote — adds to the queue, logs the
    /// lifecycle event, and persists durable-history provenance. Does not touch CompletionTcs;
    /// callers that need to block on it do so themselves (EnqueueAndWaitAsync only).</summary>
    private QueuedTask EnqueueCore(string taskId, HiveTaskBundle bundle, bool authenticated, string? authNode)
    {
        var entry = new QueuedTask { Bundle = bundle, SessionId = _sessionCtx.SessionId };
        if (!_tasks.TryAdd(taskId, entry))
            throw new InvalidOperationException($"Task {taskId} already in queue");

        Events.Append("task_queued", $"[{bundle.Role}] {bundle.Title}",
            taskId, sessionId: _sessionCtx.SessionId);

        PersistTask(taskId, entry.SessionId, bundle.Role, bundle.Title, "pending",
            authNode, worker: null, authenticated, claimToken: null,
            resultBlob: null, durationMs: null, errorMsg: null, entry.EnqueuedAt);

        return entry;
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
            const int MaxBodyBytes = ContentAddressedStore.MaxChunkBytes;
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

            // Same-machine callers are implicitly trusted and exempt from HMAC: the in-process
            // worker polling its OWN queue (warchief: self) has no peer record for itself and
            // therefore no shared secret to sign with, so without this it could never claim a
            // task from the very node it runs on (found 2026-06-24 testing remote dispatch to a
            // Raspberry Pi: a remotely-submitted task authenticated and enqueued fine, then sat
            // unclaimed forever because the local worker's self-poll was rejected 401). A process
            // on this box already has full local access, so trusting loopback adds no privilege.
            // req.IsLocal is the exact same trusted-local check HiveNodeServer already uses to
            // gate POST /hive/pair/{id}/respond. Remote callers (a different LAN/Tailscale
            // machine) still require an enrolled-peer signature, unchanged — verified: a real
            // cross-machine /hive/tasks/submit still goes through full HMAC validation below.
            var authResult = req.IsLocal
                ? HiveAuthResult.Authenticated("local")
                : _auth.Validate(req, body);
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

            if (method == "POST" && path == "/hive/tasks/submit")
            { await HandleSubmitAsync(ctx, body, authResult.NodeId); return; }

            if (method == "POST" && path == "/hive/tasks/lease")
            { await HandleLeaseAsync(ctx, body, authResult.NodeId); return; }

            if (method == "POST" && path == "/hive/campaigns")
            { await HandleCreateCampaignAsync(ctx, body); return; }

            if (method == "GET" && path == "/hive/campaigns")
            { HandleListCampaigns(ctx); return; }

            if (method == "GET" && path == "/hive/models")
            { HandleListModels(ctx); return; }

            if (method == "GET"  && path == "/hive/session/context")
            { await HandleGetContextAsync(ctx); return; }

            if (method == "POST" && path == "/hive/events")
            { await HandlePostEventAsync(ctx, body, authResult.NodeId); return; }

            if (method == "GET"  && path == "/hive/events")
            { HandleGetEventsAsync(ctx); return; }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[0] == "hive" &&
                (parts[1] == "artifacts" || parts[1] == "models"))
            {
                var store = parts[1] == "artifacts" ? ArtifactStore : ModelStore;
                if (store is null) { ctx.Response.StatusCode = 503; return; }
                if (method == "PUT")
                { await HandlePutContentAsync(ctx, store, parts[2], body); return; }
                if (method == "HEAD")
                { HandleHeadContent(ctx, store, parts[2]); return; }
                if (method == "GET")
                { await HandleGetContentAsync(ctx, store, parts[2]); return; }
            }
            if (parts.Length >= 3 && parts[0] == "hive" && parts[1] == "campaigns")
            {
                if (method == "GET" && parts.Length == 3)
                { HandleGetCampaign(ctx, parts[2]); return; }
                if (method == "POST" && parts.Length == 4)
                { HandleCampaignAction(ctx, parts[2], parts[3]); return; }
            }
            if (method == "POST" && parts.Length == 4
                && parts[0] == "hive" && parts[1] == "tasks")
            {
                var taskId = parts[2];
                var action = parts[3];
                // authResult.NodeId is the HMAC-authenticated sender — persisted as provenance.
                switch (action)
                {
                    case "claim":     await HandleClaimAsync(ctx, taskId, body, authResult.NodeId);     return;
                    case "heartbeat": await HandleHeartbeatAsync(ctx, taskId, body); return;
                    case "complete":  await HandleCompleteAsync(ctx, taskId, body, authResult.NodeId);  return;
                    case "fail":      await HandleFailAsync(ctx, taskId, body, authResult.NodeId);      return;
                }
            }

            // Single-task lookup for a remote submitter polling its result — checked after
            // every literal-path GET above so it never shadows /tasks/next or /tasks/status.
            if (method == "GET" && parts.Length == 3
                && parts[0] == "hive" && parts[1] == "tasks")
            { HandleGetTaskAsync(ctx, parts[2]); return; }

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

    /// <summary>
    /// Stage/dependency-barrier check (CF-6): a work unit with DependsOnWorkUnitIds is only
    /// leasable once every dependency's task in the same campaign has reached "completed".
    /// If any dependency is in a terminal failure state (failed/timeout/cancelled) the entry
    /// is immediately marked "failed" so the campaign can reach a terminal state rather than
    /// wedging in pending forever. Returns false whenever the entry is not yet ready to run.
    /// </summary>
    private bool AreDependenciesSatisfied(QueuedTask entry)
    {
        if (entry.Bundle.DependsOnWorkUnitIds.Length == 0) return true;
        foreach (var depWorkUnitId in entry.Bundle.DependsOnWorkUnitIds)
        {
            var depTaskId = $"{entry.Bundle.CampaignId}-{depWorkUnitId}";
            if (!_tasks.TryGetValue(depTaskId, out var dep)) return false;
            if (dep.Status == "completed") continue;

            // Cascade a terminal failure: a dependency that can never complete should not
            // leave this entry pending forever.
            if (dep.Status is "failed" or "timeout" or "cancelled")
            {
                if (entry.Status != "failed" && entry.Status != "cancelled")
                {
                    entry.Status = "failed";
                    CampaignRepository?.UpdateWorkUnit(
                        entry.Bundle.CampaignId, entry.Bundle.WorkUnitId,
                        "failed", entry.Bundle.Attempt,
                        error: $"Dependency '{depWorkUnitId}' reached terminal state '{dep.Status}'.");
                    UpdateCampaignAfterTerminal(entry.Bundle.CampaignId);
                }
            }
            return false;
        }
        return true;
    }

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
            if (!AreDependenciesSatisfied(entry)) continue;
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

    private async Task HandleClaimAsync(HttpListenerContext ctx, string taskId, byte[] body, string authNode)
    {
        await _claimLock.WaitAsync();
        try
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.Status != "pending" ||
                !AreDependenciesSatisfied(entry))
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

            // Durable history: record provenance — the authenticated node id that claimed it.
            PersistTask(taskId, entry.SessionId, entry.Bundle.Role, entry.Bundle.Title, "claimed",
                authNode, entry.ClaimedBy, authenticated: true, entry.ClaimToken,
                resultBlob: null, durationMs: null, errorMsg: null, entry.EnqueuedAt);

            WriteJson(ctx, new { taskId, status = "claimed", claimToken = entry.ClaimToken });
        }
        finally
        {
            _claimLock.Release();
        }
    }

    /// <summary>
    /// Atomically selects and claims the best pending task that the worker can execute. This
    /// removes the Phase 3A GET-next/POST-claim race while leaving those endpoints compatible.
    /// </summary>
    private async Task HandleLeaseAsync(HttpListenerContext ctx, byte[] body, string authNode)
    {
        var request = ReadJson<HiveLeaseRequest>(body);
        if (request is null || !HiveAuthMiddleware.IsValidWorkerId(request.WorkerId))
        {
            ctx.Response.StatusCode = 400;
            WriteJson(ctx, new { error = "valid workerId and capabilities are required" });
            return;
        }

        var lanes = request.Lanes.Select(l => l.Trim().ToLowerInvariant()).ToHashSet();
        await _claimLock.WaitAsync();
        try
        {
            var selected = _tasks
                .Where(kv => kv.Value.Status == "pending")
                .Where(kv => string.IsNullOrEmpty(kv.Value.Bundle.CampaignId) ||
                    !_campaigns.TryGetValue(kv.Value.Bundle.CampaignId, out var campaign) ||
                    campaign.Status == CampaignStates.Running)
                .Where(kv => AreDependenciesSatisfied(kv.Value))
                .Where(kv => lanes.Count == 0 || lanes.Contains(kv.Value.Bundle.Role.ToLowerInvariant()))
                .Where(kv => CampaignCapabilityMatcher.IsEligible(kv.Value.Bundle, request.Capabilities))
                .OrderByDescending(kv => CampaignCapabilityMatcher.Score(kv.Value.Bundle, request.Capabilities))
                .ThenBy(kv => kv.Value.EnqueuedAt)
                .FirstOrDefault();

            if (selected.Value is null)
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            var entry = selected.Value;
            entry.ClaimToken    = Guid.NewGuid().ToString("N")[..12];
            entry.Status        = "claimed";
            entry.ClaimedBy     = request.WorkerId;
            entry.ClaimedByUrl  = request.WorkerUrl;
            entry.ClaimedAt     = DateTime.UtcNow;
            entry.LastHeartbeat = DateTime.UtcNow;

            if (entry.IsVerification && entry.Bundle.Verification.RequireDifferentNode)
            {
                foreach (var sibling in _tasks.Values.Where(e => e.Status == "pending" &&
                    e.VerificationParentTaskId == entry.VerificationParentTaskId))
                {
                    sibling.Bundle.Requirements = sibling.Bundle.Requirements with
                    {
                        ExcludedWorkerIds = sibling.Bundle.Requirements.ExcludedWorkerIds
                            .Append(request.WorkerId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    };
                }
            }

            Events.Append("task_claimed",
                $"[{entry.Bundle.Role}] {entry.Bundle.Title} → {entry.ClaimedBy}",
                selected.Key, entry.ClaimedBy);
            PersistTask(selected.Key, entry.SessionId, entry.Bundle.Role, entry.Bundle.Title, "claimed",
                authNode, entry.ClaimedBy, authenticated: true, entry.ClaimToken,
                resultBlob: null, durationMs: null, errorMsg: null, entry.EnqueuedAt);
            if (entry.Bundle.CampaignId.Length > 0)
                CampaignRepository?.UpdateWorkUnit(entry.Bundle.CampaignId, entry.Bundle.WorkUnitId,
                    "running", entry.Bundle.Attempt, authNode);

            WriteJson(ctx, new HiveLeaseResponse { Bundle = entry.Bundle, ClaimToken = entry.ClaimToken });
        }
        finally
        {
            _claimLock.Release();
        }
    }

    public IReadOnlyList<string> SubmitCampaign(CampaignDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.CampaignId) || definition.WorkUnits.Count == 0)
            throw new ArgumentException("Campaign id and at least one work unit are required.", nameof(definition));

        // Reject any unit that references a DependsOn ID not present in this campaign — an unresolvable
        // dependency would silently wedge the campaign forever in AreDependenciesSatisfied.
        var unitIds = definition.WorkUnits.Select(u => u.WorkUnitId).ToHashSet(StringComparer.Ordinal);
        foreach (var unit in definition.WorkUnits)
        {
            foreach (var dep in unit.DependsOn)
            {
                if (!unitIds.Contains(dep))
                    throw new ArgumentException(
                        $"Work unit '{unit.WorkUnitId}' depends on '{dep}' which does not exist in the campaign.",
                        nameof(definition));
            }
        }

        if (!_campaigns.TryAdd(definition.CampaignId, (definition.Name, CampaignStates.Running)))
            throw new InvalidOperationException($"Campaign {definition.CampaignId} already exists.");

        CampaignRepository?.Create(definition with { Status = CampaignStates.Running });
        var taskIds = new List<string>(definition.WorkUnits.Count);
        foreach (var unit in definition.WorkUnits)
        {
            var taskId = $"{definition.CampaignId}-{unit.WorkUnitId}";
            var bundle = new HiveTaskBundle
            {
                TaskId = taskId,
                SessionId = _sessionCtx.SessionId,
                Role = string.IsNullOrWhiteSpace(unit.Role) ? "Worker" : unit.Role,
                Title = unit.Title,
                Spec = unit.Spec,
                TimeoutMs = unit.TimeoutMs,
                ExecutionKind = unit.ExecutionKind,
                CampaignId = definition.CampaignId,
                WorkUnitId = unit.WorkUnitId,
                PackId = unit.PackId.Length > 0 ? unit.PackId : definition.PackId,
                PackVersion = unit.PackVersion.Length > 0 ? unit.PackVersion : definition.PackVersion,
                NativeRole = unit.NativeRole.Length > 0 ? unit.NativeRole : unit.Role,
                Requirements = unit.Requirements,
                Verification = unit.Verification,
                Parameters = unit.Parameters,
                InputArtifacts = unit.Inputs,
                Attempt = 1,
                MaxAttempts = Math.Max(1, unit.MaxAttempts),
                WarchiefUrl = BaseUrl,
                DependsOnWorkUnitIds = unit.DependsOn,
            };
            EnqueueCore(taskId, bundle, authenticated: false, authNode: null);
            CampaignRepository?.BindTask(definition.CampaignId, unit.WorkUnitId, taskId);
            taskIds.Add(taskId);
        }

        Events.Append("campaign_started", $"{definition.Name} · {taskIds.Count} work units",
            sessionId: _sessionCtx.SessionId);
        return taskIds;
    }

    public bool SetCampaignState(string campaignId, string state)
    {
        if (!_campaigns.TryGetValue(campaignId, out var campaign)) return false;
        _campaigns[campaignId] = (campaign.Name, state);
        CampaignRepository?.SetStatus(campaignId, state);

        if (state == CampaignStates.Cancelled)
        {
            foreach (var entry in _tasks.Values.Where(e => e.Bundle.CampaignId == campaignId))
            {
                _claimLock.Wait();
                try
                {
                    if (entry.Status is "completed" or "failed" or "cancelled") continue;
                    entry.Status = "cancelled";
                    entry.ClaimToken = Guid.NewGuid().ToString("N");
                    entry.CompletionTcs.TrySetResult(new HiveTaskResult
                    {
                        TaskId = entry.Bundle.TaskId,
                        Status = "cancelled",
                        ErrorMsg = "Campaign cancelled",
                    });
                    CampaignRepository?.UpdateWorkUnit(campaignId, entry.Bundle.WorkUnitId,
                        "cancelled", entry.Bundle.Attempt, error: "Campaign cancelled");
                }
                finally { _claimLock.Release(); }
            }
        }

        Events.Append($"campaign_{state}", $"{campaign.Name} → {state}", sessionId: _sessionCtx.SessionId);
        return true;
    }

    public CampaignStatusSnapshot? GetCampaignStatus(string campaignId)
    {
        if (!_campaigns.TryGetValue(campaignId, out var campaign)) return null;
        var units = _tasks.Values.Where(e => e.Bundle.CampaignId == campaignId && !e.IsVerification).ToList();
        return new CampaignStatusSnapshot
        {
            CampaignId = campaignId,
            Name = campaign.Name,
            Status = campaign.Status,
            Total = units.Count,
            Pending = units.Count(e => e.Status == "pending"),
            Running = units.Count(e => e.Status == "claimed"),
            Verifying = units.Count(e => e.Status == "verifying"),
            Completed = units.Count(e => e.Status == "completed"),
            Failed = units.Count(e => e.Status == "failed"),
            Cancelled = units.Count(e => e.Status == "cancelled"),
        };
    }

    public IReadOnlyList<CampaignStatusSnapshot> GetCampaignStatuses() => _campaigns.Keys
        .Select(GetCampaignStatus)
        .Where(s => s is not null)
        .Cast<CampaignStatusSnapshot>()
        .OrderByDescending(s => s.Status == CampaignStates.Running)
        .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private Task HandleCreateCampaignAsync(HttpListenerContext ctx, byte[] body)
    {
        var campaign = ReadJson<CampaignDefinition>(body);
        if (campaign is null)
        {
            ctx.Response.StatusCode = 400;
            WriteJson(ctx, new { error = "invalid campaign definition" });
            return Task.CompletedTask;
        }

        try
        {
            var taskIds = SubmitCampaign(campaign);
            ctx.Response.StatusCode = 202;
            WriteJson(ctx, new { campaignId = campaign.CampaignId, status = CampaignStates.Running, taskIds });
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            WriteJson(ctx, new { error = ex.Message });
        }
        return Task.CompletedTask;
    }

    private void HandleListCampaigns(HttpListenerContext ctx) =>
        WriteJson(ctx, _campaigns.Keys.Select(GetCampaignStatus).Where(s => s is not null).ToList());

    private void HandleListModels(HttpListenerContext ctx)
    {
        if (ModelStore is null) { ctx.Response.StatusCode = 503; return; }
        WriteJson(ctx, ModelStore.GetDigests().Select(d =>
            new ApprovedModelAsset(d, new FileInfo(ModelStore.GetPath(d)).Length)).ToArray());
    }

    private void HandleGetCampaign(HttpListenerContext ctx, string campaignId)
    {
        var status = GetCampaignStatus(campaignId);
        if (status is null) { ctx.Response.StatusCode = 404; return; }
        WriteJson(ctx, status);
    }

    private void HandleCampaignAction(HttpListenerContext ctx, string campaignId, string action)
    {
        var state = action.ToLowerInvariant() switch
        {
            "pause" => CampaignStates.Paused,
            "resume" => CampaignStates.Running,
            "cancel" => CampaignStates.Cancelled,
            _ => "",
        };
        if (state.Length == 0) { ctx.Response.StatusCode = 404; return; }
        if (!SetCampaignState(campaignId, state)) { ctx.Response.StatusCode = 404; return; }
        WriteJson(ctx, GetCampaignStatus(campaignId)!);
    }

    private static async Task HandlePutContentAsync(HttpListenerContext ctx,
        ContentAddressedStore store, string digest, byte[] body)
    {
        if (!long.TryParse(ctx.Request.Headers["X-Hive-Offset"], out var offset) ||
            !long.TryParse(ctx.Request.Headers["X-Hive-Total-Bytes"], out var total))
        {
            ctx.Response.StatusCode = 400;
            WriteJson(ctx, new { error = "X-Hive-Offset and X-Hive-Total-Bytes are required" });
            return;
        }
        try
        {
            var result = await store.WriteChunkAsync(digest, offset, total, body).ConfigureAwait(false);
            ctx.Response.StatusCode = result.Complete ? 201 : 202;
            WriteJson(ctx, new { digest, complete = result.Complete, bytesStored = result.BytesStored });
        }
        catch (ArgumentException ex)
        {
            ctx.Response.StatusCode = 400;
            WriteJson(ctx, new { error = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            ctx.Response.StatusCode = 409;
            WriteJson(ctx, new { error = ex.Message, resumeOffset = SafeOffset(store, digest) });
        }
        catch (IOException ex)
        {
            ctx.Response.StatusCode = 507;
            WriteJson(ctx, new { error = ex.Message });
        }
    }

    private static void HandleHeadContent(HttpListenerContext ctx, ContentAddressedStore store, string digest)
    {
        try
        {
            var offset = store.GetResumeOffset(digest);
            ctx.Response.Headers["X-Hive-Stored-Bytes"] = offset.ToString();
            ctx.Response.Headers["X-Hive-Complete"] = store.Has(digest) ? "true" : "false";
            ctx.Response.StatusCode = offset > 0 ? 200 : 404;
        }
        catch (ArgumentException) { ctx.Response.StatusCode = 400; }
    }

    private static async Task HandleGetContentAsync(HttpListenerContext ctx,
        ContentAddressedStore store, string digest)
    {
        try
        {
            var path = store.GetPath(digest);
            var length = new FileInfo(path).Length;
            var start = 0L;
            var end = length - 1;
            var range = ctx.Request.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                var values = range[6..].Split('-', 2);
                if (!long.TryParse(values[0], out start) || start < 0 || start >= length)
                { ctx.Response.StatusCode = 416; return; }
                if (values.Length == 2 && values[1].Length > 0 && long.TryParse(values[1], out var requestedEnd))
                    end = Math.Min(end, requestedEnd);
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
            }

            var count = end - start + 1;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = count;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = start;
            var remaining = count;
            var buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining))).ConfigureAwait(false);
                if (read == 0) break;
                await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                remaining -= read;
            }
        }
        catch (FileNotFoundException) { ctx.Response.StatusCode = 404; }
        catch (ArgumentException) { ctx.Response.StatusCode = 400; }
    }

    private static long SafeOffset(ContentAddressedStore store, string digest)
    {
        try { return store.GetResumeOffset(digest); }
        catch { return 0; }
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

    private async Task HandleCompleteAsync(HttpListenerContext ctx, string taskId, byte[] body, string authNode)
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

            if (result.OutputArtifacts.Count > 0 && (ArtifactStore is null ||
                result.OutputArtifacts.Any(a => !ArtifactStore.Has(a.DigestSha256) ||
                    new FileInfo(ArtifactStore.GetPath(a.DigestSha256)).Length != a.SizeBytes)))
            {
                ctx.Response.StatusCode = 409;
                WriteJson(ctx, new { error = "one or more output artifacts are missing or unverified" });
                return;
            }

            if (!string.IsNullOrEmpty(entry.ClaimToken) && result.ClaimToken != entry.ClaimToken)
            {
                Log($"⚠ Stale /complete for '{entry.Bundle.Title}' from {result.WorkerId} " +
                    $"(token mismatch — re-queued task already claimed by {entry.ClaimedBy})");
                ctx.Response.StatusCode = 409;
                return;
            }

            // Mark completed under lock so CheckTimeouts cannot re-queue concurrently.
            entry.Status           = "completed";
            entry.ResultText       = result.Result;
            entry.StructuredResult = result;
        }
        finally { _claimLock.Release(); }

        // Resolve TCS outside lock — continuations run inline and must not re-enter _claimLock.
        entry!.CompletionTcs.TrySetResult(result);

        Log($"🐝 [{entry.Bundle.Role}] '{entry.Bundle.Title}' ✅ completed by {result!.WorkerId} " +
            $"({result.DurationMs / 1000.0:F1}s, {result.Result.Length} chars)");
        Events.Append("task_complete",
            $"[{result.WorkerId}] {entry.Bundle.Title} ✓ {result.DurationMs / 1000.0:F1}s · {result.Result.Length} chars",
            taskId, result.WorkerId);

        // Durable history: persist the result blob + provenance. This is the row the threat
        // model cares about most — a poisoned "completed" result is now traceable to its node.
        PersistTask(taskId, entry.SessionId, entry.Bundle.Role, entry.Bundle.Title, "completed",
            authNode, result.WorkerId, authenticated: true, entry.ClaimToken,
            result.Result, result.DurationMs, errorMsg: null, entry.EnqueuedAt);
        if (entry.Bundle.CampaignId.Length > 0)
        {
            if (entry.IsVerification)
            {
                FinalizeVerificationGroup(entry.VerificationParentTaskId!);
            }
            else if (entry.Bundle.Verification.RequiredIndependentRuns > 1)
            {
                ScheduleVerificationRuns(taskId, entry, result);
            }
            else
            {
                AcceptWorkUnit(entry, result, authNode);
            }
            if (ArtifactStore is not null)
                foreach (var artifact in result.OutputArtifacts)
                    CampaignRepository?.AddArtifact(entry.Bundle.CampaignId, entry.Bundle.WorkUnitId,
                        artifact, ArtifactStore.GetPath(artifact.DigestSha256), verified: true);
            UpdateCampaignAfterTerminal(entry.Bundle.CampaignId);
        }

        WriteJson(ctx, new { taskId, status = "completed" });
    }

    private async Task HandleFailAsync(HttpListenerContext ctx, string taskId, byte[] body, string authNode)
    {
        var result = ReadJson<HiveTaskResult>(body);

        QueuedTask? entry    = null;
        HiveTaskResult? failResult = null;
        var requeued = false;
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

            failResult   = result ?? new HiveTaskResult
            {
                TaskId   = taskId,
                WorkerId = entry.ClaimedBy ?? "unknown",
                Status   = "failed",
                ErrorMsg = "Worker reported failure with no details",
            };
            entry.ErrorMsg = failResult.ErrorMsg;
            if (entry.Bundle.CampaignId.Length > 0 && entry.Bundle.Attempt < entry.Bundle.MaxAttempts)
            {
                entry.Bundle.Attempt++;
                entry.Status = "pending";
                entry.ClaimedBy = null;
                entry.ClaimedByUrl = null;
                entry.ClaimedAt = null;
                entry.LastHeartbeat = null;
                entry.ClaimToken = Guid.NewGuid().ToString("N");
                requeued = true;
            }
            else
            {
                entry.Status = "failed";
            }
        }
        finally { _claimLock.Release(); }

        if (requeued)
        {
            Events.Append("task_requeued",
                $"[{failResult!.WorkerId}] {entry!.Bundle.Title} · retry {entry.Bundle.Attempt}/{entry.Bundle.MaxAttempts}",
                taskId, failResult.WorkerId);
            CampaignRepository?.UpdateWorkUnit(entry.Bundle.CampaignId, entry.Bundle.WorkUnitId,
                "pending", entry.Bundle.Attempt, error: failResult.ErrorMsg);
            PersistTask(taskId, entry.SessionId, entry.Bundle.Role, entry.Bundle.Title, "pending",
                authNode, failResult.WorkerId, authenticated: true, entry.ClaimToken,
                resultBlob: null, failResult.DurationMs, failResult.ErrorMsg, entry.EnqueuedAt);
            WriteJson(ctx, new { taskId, status = "pending", attempt = entry.Bundle.Attempt });
            return;
        }

        entry!.CompletionTcs.TrySetResult(failResult);

        Log($"⚠ [{entry.Bundle.Role}] '{entry.Bundle.Title}' failed by {failResult!.WorkerId}: {failResult.ErrorMsg}");
        Events.Append("task_failed",
            $"[{failResult.WorkerId}] {entry.Bundle.Title} ✗ {failResult.ErrorMsg}",
            taskId, failResult.WorkerId);

        // Durable history: persist the failure + provenance.
        PersistTask(taskId, entry.SessionId, entry.Bundle.Role, entry.Bundle.Title, "failed",
            authNode, failResult.WorkerId, authenticated: true, entry.ClaimToken,
            resultBlob: null, failResult.DurationMs, failResult.ErrorMsg, entry.EnqueuedAt);
        if (entry.Bundle.CampaignId.Length > 0)
        {
            if (entry.IsVerification)
                FinalizeVerificationGroup(entry.VerificationParentTaskId!);
            else
                CampaignRepository?.UpdateWorkUnit(entry.Bundle.CampaignId, entry.Bundle.WorkUnitId,
                    "failed", entry.Bundle.Attempt, authNode, error: failResult.ErrorMsg);
            UpdateCampaignAfterTerminal(entry.Bundle.CampaignId);
        }

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

    /// <summary>
    /// POST /hive/tasks/submit — lets an authenticated, enrolled peer remotely add a task to
    /// THIS queue. No SwarmSession/Boss-decomposition pipeline needs to be running anywhere;
    /// the caller supplies a single self-contained prompt. Returns 202 immediately with the
    /// generated TaskId — the caller polls GET /hive/tasks/{id} for the outcome, since this
    /// queue's normal claim/heartbeat/complete lifecycle (unchanged) can take up to
    /// TimeoutMs to resolve and an HTTP request shouldn't block that long.
    /// </summary>
    private Task HandleSubmitAsync(HttpListenerContext ctx, byte[] body, string authNode)
    {
        var req = ReadJson<HiveSubmitTaskRequest>(body);
        if (req is null || string.IsNullOrWhiteSpace(req.Role) || string.IsNullOrWhiteSpace(req.Spec))
        {
            ctx.Response.StatusCode = 400;
            WriteJson(ctx, new { error = "role and spec are required" });
            return Task.CompletedTask;
        }

        var bundle = new HiveTaskBundle
        {
            Role           = req.Role,
            Title          = string.IsNullOrWhiteSpace(req.Title) ? req.Role : req.Title,
            Spec           = req.Spec,
            ProjectGoal    = req.ProjectGoal,
            TargetLanguage = req.TargetLanguage,
            ModelHint      = req.ModelHint,
            WarchiefUrl    = BaseUrl,   // results post back to THIS queue, since it holds the task
            TimeoutMs      = req.TimeoutMs > 0 ? req.TimeoutMs : 300_000,
        };

        var taskId = EnqueueRemote(bundle, authNode);

        Log($"🐝 [{bundle.Role}] '{bundle.Title}' submitted remotely by {authNode} (taskId={taskId})");

        ctx.Response.StatusCode = 202;
        WriteJson(ctx, new HiveSubmitTaskResponse { TaskId = taskId, Status = "pending" });
        return Task.CompletedTask;
    }

    /// <summary>
    /// GET /hive/tasks/{id} — single-task lookup, primarily for a remote submitter polling
    /// for its result without parsing the full /tasks/status snapshot. Returns the actual
    /// result/error text once available (entry.ResultText/ErrorMsg, set at completion/failure
    /// time) — the in-memory CompletionTcs that EnqueueAndWaitAsync awaits only ever reaches
    /// the original in-process caller, never an external HTTP poller.
    /// </summary>
    private void HandleGetTaskAsync(HttpListenerContext ctx, string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var entry))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        WriteJson(ctx, new HiveTaskStatusResponse
        {
            TaskId    = taskId,
            Title     = entry.Bundle.Title,
            Role      = entry.Bundle.Role,
            Status    = entry.Status,
            ClaimedBy = entry.ClaimedBy,
            ClaimedAt = entry.ClaimedAt,
            Result    = entry.ResultText,
            ErrorMsg  = entry.ErrorMsg,
        });
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
            string? sqlStatus    = null;   // durable status transition to mirror to SQL
            string? sqlSessionId = null;   // frozen at enqueue; immune to session rollover
            bool    resolveNull = false;

            _claimLock.Wait();
            try
            {
                switch (entry.Status)
                {
                    case "pending":
                        if (string.IsNullOrEmpty(entry.Bundle.CampaignId) &&
                            now - entry.EnqueuedAt > TimeSpan.FromSeconds(PendingTimeoutSec))
                        {
                            entry.Status = "timeout";
                            resolveNull  = true;
                            sqlStatus    = "timeout";
                            sqlSessionId = entry.SessionId;
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
                            var campaignTask = !string.IsNullOrEmpty(entry.Bundle.CampaignId);
                            var exhausted = campaignTask && entry.Bundle.Attempt >= entry.Bundle.MaxAttempts;
                            entry.Status        = exhausted ? "failed" : "pending";
                            entry.ClaimedBy     = null;
                            entry.ClaimedByUrl  = null;
                            entry.ClaimedAt     = null;
                            entry.LastHeartbeat = null;
                            entry.ClaimToken    = Guid.NewGuid().ToString();
                            if (!exhausted) entry.Bundle.Attempt++;
                            sqlStatus    = entry.Status;
                            sqlSessionId = entry.SessionId;
                            logMsg = exhausted
                                ? $"⚠ Task '{entry.Bundle.Title}' exhausted {entry.Bundle.MaxAttempts} attempts after heartbeat loss"
                                : $"⚠ Task '{entry.Bundle.Title}' heartbeat timeout from {who} — re-queued (attempt {entry.Bundle.Attempt})";
                            evType = exhausted ? "task_failed" : "task_requeued";
                            evMsg  = exhausted
                                ? $"{entry.Bundle.Title} exhausted retry budget"
                                : $"{entry.Bundle.Title} lost heartbeat from {who} → re-queued";
                            evWho  = who;
                            if (campaignTask)
                                CampaignRepository?.UpdateWorkUnit(entry.Bundle.CampaignId,
                                    entry.Bundle.WorkUnitId, entry.Status, entry.Bundle.Attempt,
                                    error: exhausted ? "Worker heartbeat lost; retry budget exhausted" : null);
                        }
                        break;

                    case "completed":
                    case "failed":
                    case "timeout":
                        // Evict from MEMORY after 5 min; the durable SQL row stays (that's the
                        // whole point of Phase 4) and is removed later by the retention sweep.
                        if (now - entry.EnqueuedAt > TimeSpan.FromMinutes(5))
                            toEvict.Add(id);
                        break;
                }
            }
            finally { _claimLock.Release(); }

            // TCS, logging, and SQL outside the lock — TCS continuations must not re-enter _claimLock.
            if (resolveNull)  entry.CompletionTcs.TrySetResult(null);
            if (logMsg is not null) Log(logMsg);
            if (evType is not null) Events.Append(evType, evMsg!, id, evWho ?? "");
            if (sqlStatus is not null && sqlSessionId is not null)
                try { Repository?.UpdateStatus(sqlSessionId, id, sqlStatus); } catch { }
        }

        foreach (var id in toEvict)
            _tasks.TryRemove(id, out var __evicted);

        // Retention sweep — throttled; durable hive history can never grow unbounded.
        if (Repository is { } repo && now - _lastSweep > SweepInterval)
        {
            _lastSweep = now;
            try { repo.SweepExpired(); } catch { }
        }
    }

    private void UpdateCampaignAfterTerminal(string campaignId)
    {
        var snapshot = GetCampaignStatus(campaignId);
        if (snapshot is null || snapshot.Pending > 0 || snapshot.Running > 0) return;
        var next = snapshot.Failed > 0 ? CampaignStates.Failed
            : snapshot.Verifying > 0 ? CampaignStates.Verifying
            : CampaignStates.Completed;
        if (_campaigns.TryGetValue(campaignId, out var current) && current.Status == next) return;
        if (_campaigns.TryGetValue(campaignId, out var campaign))
            _campaigns[campaignId] = (campaign.Name, next);
        CampaignRepository?.SetStatus(campaignId, next);
        Events.Append($"campaign_{next}", $"{snapshot.Name} → {next}", sessionId: _sessionCtx.SessionId);
    }

    private void ScheduleVerificationRuns(string parentTaskId, QueuedTask parent, HiveTaskResult firstResult)
    {
        parent.Status = "verifying";
        CampaignRepository?.UpdateWorkUnit(parent.Bundle.CampaignId, parent.Bundle.WorkUnitId,
            "verifying", parent.Bundle.Attempt, firstResult.WorkerId,
            JsonSerializer.Serialize(firstResult, _json));

        var required = Math.Clamp(parent.Bundle.Verification.RequiredIndependentRuns, 2, 5);
        for (var run = 2; run <= required; run++)
        {
            var taskId = $"{parentTaskId}#verify-{run}";
            var bundle = CloneForVerification(parent.Bundle, taskId, firstResult.WorkerId, run);
            var verification = new QueuedTask
            {
                Bundle = bundle,
                SessionId = parent.SessionId,
                IsVerification = true,
                VerificationParentTaskId = parentTaskId,
            };
            if (!_tasks.TryAdd(taskId, verification)) continue;
            Events.Append("verification_queued", $"{parent.Bundle.Title} · independent run {run}/{required}",
                taskId, sessionId: parent.SessionId);
        }
    }

    private void FinalizeVerificationGroup(string parentTaskId)
    {
        if (!_tasks.TryGetValue(parentTaskId, out var parent) || parent.StructuredResult is null) return;
        var replicas = _tasks.Values.Where(e => e.VerificationParentTaskId == parentTaskId).ToList();
        if (replicas.Any(e => e.Status is "pending" or "claimed")) return;

        var evidence = new[] { parent.StructuredResult }
            .Concat(replicas.Where(e => e.StructuredResult is not null).Select(e => e.StructuredResult!))
            .ToList();
        var required = Math.Clamp(parent.Bundle.Verification.RequiredIndependentRuns, 1, 5);
        var error = replicas.Any(e => e.Status == "failed")
            ? "An independent verification run failed."
            : VerifyEvidence(parent.Bundle.Verification, evidence, required);

        if (error is null)
        {
            parent.Status = "completed";
            AcceptWorkUnit(parent, parent.StructuredResult, parent.StructuredResult.WorkerId);
            Events.Append("work_unit_accepted", $"{parent.Bundle.Title} · {evidence.Count} verified runs",
                parentTaskId, sessionId: parent.SessionId);
        }
        else
        {
            parent.Status = "failed";
            parent.ErrorMsg = error;
            CampaignRepository?.UpdateWorkUnit(parent.Bundle.CampaignId, parent.Bundle.WorkUnitId,
                "failed", parent.Bundle.Attempt, error: error);
            Events.Append("verification_failed", $"{parent.Bundle.Title} · {error}",
                parentTaskId, sessionId: parent.SessionId);
        }
    }

    private void AcceptWorkUnit(QueuedTask entry, HiveTaskResult result, string acceptedBy)
    {
        CampaignRepository?.UpdateWorkUnit(entry.Bundle.CampaignId, entry.Bundle.WorkUnitId,
            "completed", entry.Bundle.Attempt, acceptedBy, JsonSerializer.Serialize(result, _json));
    }

    internal static string? VerifyEvidence(VerificationPolicy policy, IReadOnlyList<HiveTaskResult> evidence, int required)
    {
        if (evidence.Count < required) return $"Expected {required} successful runs, received {evidence.Count}.";
        if (policy.RequireDifferentNode && evidence.Select(e => e.WorkerId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < required)
            return "Verification policy requires different worker nodes.";

        var attestations = evidence.Select(e => e.Attestation).ToList();
        if (attestations.Any(a => a is null)) return "A verification result is missing its execution attestation.";
        var baseline = AttestationFingerprint(attestations[0]!);
        if (attestations.Skip(1).Any(a => AttestationFingerprint(a!) != baseline))
            return "Verification runs used different inputs, models, adapters, or container images.";

        if (policy.Mode is "deterministic_rerun" or "hash_only")
        {
            var expected = ResultFingerprint(evidence[0]);
            if (evidence.Skip(1).Any(r => ResultFingerprint(r) != expected))
                return "Deterministic verification outputs did not match.";
        }
        return null;
    }

    private static string AttestationFingerprint(ExecutionAttestation a) => JsonSerializer.Serialize(new
    {
        a.ModelHash,
        a.AdapterHash,
        a.ContainerDigest,
        Inputs = a.InputDigests.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray(),
    });

    private static string ResultFingerprint(HiveTaskResult result) => JsonSerializer.Serialize(new
    {
        result.Result,
        Artifacts = result.OutputArtifacts.Select(a => a.DigestSha256).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
    });

    private static HiveTaskBundle CloneForVerification(HiveTaskBundle source, string taskId, string originalWorker, int run) => new()
    {
        TaskId = taskId,
        SessionId = source.SessionId,
        Role = source.Role,
        Title = $"{source.Title} [verification {run}]",
        Spec = source.Spec,
        ProjectGoal = source.ProjectGoal,
        TargetLanguage = source.TargetLanguage,
        ModelHint = source.ModelHint,
        WarchiefUrl = source.WarchiefUrl,
        TimeoutMs = source.TimeoutMs,
        ExecutionKind = source.ExecutionKind,
        CampaignId = source.CampaignId,
        WorkUnitId = source.WorkUnitId,
        PackId = source.PackId,
        PackVersion = source.PackVersion,
        NativeRole = source.NativeRole,
        Requirements = source.Requirements with
        {
            ExcludedWorkerIds = source.Verification.RequireDifferentNode
                ? source.Requirements.ExcludedWorkerIds.Append(originalWorker).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : source.Requirements.ExcludedWorkerIds,
        },
        Verification = source.Verification,
        Parameters = source.Parameters,
        InputArtifacts = source.InputArtifacts,
        Attempt = 1,
        MaxAttempts = source.MaxAttempts,
        UpstreamArtifacts = source.UpstreamArtifacts,
    };

    // ── Durable-history helper (best-effort; a DB failure never breaks a swarm run) ──

    private void PersistTask(
        string taskId, string sessionId, string? role, string? title, string status,
        string? authNode, string? worker, bool authenticated, string? claimToken,
        string? resultBlob, int? durationMs, string? errorMsg, DateTime enqueuedAt)
    {
        if (Repository is not { } repo) return;
        try
        {
            var ok = repo.UpsertTask(taskId, sessionId, role, title, status,
                authNode, worker, authenticated, claimToken,
                resultBlob, durationMs, errorMsg, enqueuedAt);
            if (!ok)
                Log($"⚠ HIVE persist rejected (per-node row quota) for task '{title}' from {authNode}");
        }
        catch { /* best-effort — durable history must never break a swarm run */ }
    }

    // ── Event endpoints ───────────────────────────────────────────────────────

    private async Task HandlePostEventAsync(HttpListenerContext ctx, byte[] body, string authNode)
    {
        var ev = ReadJson<HiveEventPost>(body);
        if (ev is null) { ctx.Response.StatusCode = 400; return; }
        Events.Append(ev.Type, ev.Msg, ev.TaskId, ev.WorkerId, ev.SessionId);

        // Durable, provenance-tagged copy of the remote-submitted event. Use the session id
        // the event carries (what the worker thinks it's posting to), not the current session
        // context — late posts after a session rollover must not land in the wrong run's history.
        try
        {
            var evSession = !string.IsNullOrEmpty(ev.SessionId) ? ev.SessionId : _sessionCtx.SessionId;
            var stored = Repository?.AppendEvent(ev.Type, ev.Msg, ev.TaskId, ev.WorkerId,
                evSession, authNode, authenticated: true);
            if (stored == false)
                Log($"⚠ HIVE event persist rejected (per-node quota) from {authNode}");
        }
        catch { /* best-effort */ }

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
        // Without this, IsListening/IsRemoteReachable keep reporting true after shutdown --
        // a caller checking health post-Dispose would be lied to (Codex CLI MINOR, 2026-06-21).
        _bound            = false;
        IsRemoteReachable = false;
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
