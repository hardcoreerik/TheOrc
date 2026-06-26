// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND node API server (port 7078).
///
/// Endpoints:
///   GET  /hive/info                       — capability advertisement (unauthenticated; read-only)
///   POST /hive/pair                       — initiate pairing ceremony (unauthenticated; mutual proof)
///   GET  /hive/pair/{sessionId}           — poll pairing result (unauthenticated)
///   POST /hive/pair/{sessionId}/respond   — approve or reject pairing (local only / admin)
///   POST /hive/mesh/heartbeat             — authenticated peer liveness pulse
///   POST /hive/mesh/election/suspect      — authenticated election: Warchief suspect notice
///   POST /hive/mesh/election/claim        — authenticated election: claim temporary Warchief
///   POST /hive/mesh/election/recover      — authenticated election: original Warchief recovering
///   POST /hive/mesh/election/stepdown     — authenticated election: temp Warchief stepping down
///   GET  /hive/update/version             — this node's installed version (unauthenticated)
///   POST /hive/update/deploy              — Warchief-authenticated remote update trigger
///
/// Auth: all /hive/mesh/* endpoints validate HMAC headers via HiveAuthMiddleware.
/// /hive/tasks/* endpoints (port 7079, HiveTaskQueue) are wired separately and
/// also enforce HMAC auth (GracePeriodActive=false). /hive/info and /hive/pair/*
/// are intentionally unauthenticated (discovery and bootstrapping — read-only or
/// proof-of-key only).
/// </summary>
public sealed class HiveNodeServer : IDisposable
{
    public const int ApiPort = 7078;

    private static readonly JsonSerializerOptions _jsonIn = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly JsonSerializerOptions _jsonOut = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private HttpListener?             _listener;
    private HiveNodeInfo              _info = new("", "", [], 0, 0, []);
    private CancellationTokenSource   _cts  = new();
    private int                       _inFlight;   // accepted requests still being handled
    // All authenticated endpoints are fail-closed — grace period never applies on the server.
    // "node" persistence key survives the nonce cache across restart (replay protection).
    private readonly HiveAuthMiddleware     _strictAuth   = new("node");
    private readonly HivePeerStore          _peers    = HivePeerStore.Default;

    // Injected by the app after construction
    public HiveMeshHeartbeat?   MeshHeartbeat    { get; set; }
    public HiveElectionService? ElectionService  { get; set; }
    /// <summary>
    /// HIVE_MEMBERSHIP_SPEC.md §6.4 — applied to newly paired (non-mobile) peers at
    /// approval time instead of the previously hardcoded Ask. Set from AppSettings.
    /// HiveDefaultAcceptControlFrom by the app at startup/on settings change.
    /// </summary>
    public HiveAcceptControlPolicy DefaultAcceptControlFrom { get; set; } = HiveAcceptControlPolicy.Ask;
    /// <summary>
    /// Called on the calling thread after a successful remote /hive/update/deploy.
    /// WPF: <c>() =&gt; Dispatcher.InvokeAsync(() =&gt; Application.Current.Shutdown())</c>.
    /// Daemon: <c>() =&gt; Environment.Exit(0)</c>.
    /// Null = no action after relaunch is prepared (safe default).
    /// </summary>
    public Action? ShutdownCallback { get; set; }

    // Pending pairing sessions: sessionId → (request, expiry, initiator-remote-ip)
    private readonly Dictionary<string, (HivePairingRequest Req, DateTime Expiry, string RemoteIp)> _pendingPairings = [];
    // Completed results: sessionId → (response, stored-at).  Pruned after 10 min.
    private readonly Dictionary<string, (HivePairingResponse Resp, DateTime StoredAt)>  _pairingResults  = [];
    private readonly Lock _pairingLock = new();

    // ── Approval callback (wired to UI) ───────────────────────────────────────

    /// <summary>
    /// Raised when a pairing request needs human approval.
    /// UI subscribes, shows the approval card, then calls ApprovePairing / RejectPairing.
    /// </summary>
    public event Action<string, HivePairingRequest>? OnPairingRequestReceived;

    /// <summary>
    /// Fired when a role-assignment request (HIVE_MEMBERSHIP_SPEC.md §6) arrives from a peer
    /// whose AcceptControlFrom policy is Ask. UI subscribes, shows an approval card, and on
    /// approval calls <c>HiveIdentity.Load().SetSelfRole(role)</c> directly — there is no
    /// session/poll mechanism for this (unlike pairing): the assigner's immediate HTTP
    /// response already says "pending_approval" and does not wait to learn the eventual
    /// human decision.
    /// </summary>
    public event Action<string, HiveNodeRole>? OnRoleAssignReceived;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    // Set once from TryBind's own return value during Start() -- never re-read
    // _listener.IsListening afterward, since a failed fallback bind can leave the listener
    // disposed/unusable and IsListening's getter throws ObjectDisposedException in that state
    // rather than returning false (Codex CLI BLOCKER, 2026-06-20; mirrors the existing
    // HiveTaskQueue.BaseUrl-after-bind pattern just above Start()'s wildcard/fallback logic).
    private bool _bound;
    private bool _wideBound;

    public bool IsListening => _bound;

    /// True only if the wildcard "+" bind succeeded (reachable from other machines).
    /// False if it fell back to localhost-only, or never bound at all.
    public bool IsRemoteReachable => _wideBound;

    public void Start(HiveNodeInfo info)
    {
        _info = info;

        var identity = HiveIdentity.Load();

        // Ensure this node appears in the peer store so election logic can find it
        // and include it in EnrollmentSeq comparisons. Only insert if absent.
        if (_peers.Find(identity.NodeId) is null)
        {
            _peers.AddOrUpdate(new HivePeer
            {
                NodeId             = identity.NodeId,
                Name               = Environment.MachineName,
                Fingerprint        = identity.Fingerprint,
                SigningPublicKeyDer = Convert.ToBase64String(identity.SigningPublicKeyDer),
                Role               = HiveNodeRole.Controller,
                MaxRole            = HiveNodeRole.Controller,
                PairedAt           = DateTime.UtcNow,
                IsMobile           = false,
            });
        }

        // Initialize services if the app has not injected custom instances.
        ElectionService ??= new HiveElectionService(identity, _peers);
        MeshHeartbeat   ??= new HiveMeshHeartbeat(identity, _peers, ElectionService);

        // Arm election state: infer which peer is Warchief from enrollment order.
        // Must run after self-registration above so this node is in the candidate set.
        ElectionService.InferWarchiefFromPeerStore();

        MeshHeartbeat.Start();

        _listener = new HttpListener();
        var wideSucceeded = TryBind($"http://+:{ApiPort}/hive/");
        var bound = wideSucceeded;
        if (!bound)
        {
            // A failed Start() (e.g. the wildcard "+" bind needs admin rights or a URL ACL
            // reservation neither of which a normal user process has) can leave the listener
            // disposed/unusable internally -- replace it before retrying rather than reusing it.
            // Close() is best-effort here: the listener may already be in that same broken
            // state, so swallow rather than risk a second exception while merely cleaning up.
            try { _listener.Close(); } catch { /* already disposed/unusable -- nothing to clean up */ }
            _listener = new HttpListener();
            bound = TryBind($"http://localhost:{ApiPort}/hive/");
        }

        // Track success via TryBind's own return value rather than re-reading
        // _listener.IsListening afterward -- if this second bind also failed, the listener
        // may be in the same disposed-but-not-null state that caused the first failure,
        // and IsListening's getter can throw ObjectDisposedException just like Prefixes did.
        _bound     = bound;
        _wideBound = wideSucceeded;
        if (bound)
            _ = ServeAsync(_cts.Token);
    }

    public void UpdateInfo(HiveNodeInfo info) => _info = info;

    // ── Static probe helper (unchanged) ──────────────────────────────────────

    public static async Task<HiveNodeInfo?> ProbeAsync(
        string host, int timeoutMs = 2000, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var json = await http.GetStringAsync($"http://{host.TrimEnd('/')}:{ApiPort}/hive/info", ct);
            return JsonSerializer.Deserialize<HiveNodeInfo>(json);
        }
        catch { return null; }
    }

    // ── Pairing approval (called by UI) ──────────────────────────────────────

    public bool ApprovePairing(string sessionId, HiveNodeRole grantedRole,
                               string[] allowedLanes, bool isMobile)
    {
        lock (_pairingLock)
        {
            if (!_pendingPairings.TryGetValue(sessionId, out var pending)) return false;

            // Enforce session TTL — do not approve expired requests.
            if (pending.Expiry < DateTime.UtcNow)
            {
                _pendingPairings.Remove(sessionId);
                return false;
            }

            var req      = pending.Req;
            var remoteIp = pending.RemoteIp;
            var identity = HiveIdentity.Load();

            // Derive shared secret, and apply HIVE_MEMBERSHIP_SPEC.md §4.3 reconciliation —
            // both wrapped in the same try/catch: a malformed ExchangePublicKeyDer, a
            // Persist() disk/DPAPI failure, or a concurrent SetHive race (HiveIdentity's own
            // lock makes that last one safe but still throwing) must all reject this pairing
            // cleanly (return false) rather than letting an exception escape into the UI
            // event handler that invoked ApprovePairing (grok review BLOCKER, 2026-06-21).
            byte[] secret;
            try
            {
                // The hiveid_mismatch case (both sides set, differ) was already refused at
                // request time in HandlePairInitiateAsync — by the time a human reaches this
                // approval step, only the remaining three cases are possible:
                //   - identity has one, req doesn't  -> nothing to do, identity's HiveId stands
                //   - req has one, identity doesn't  -> this node adopts it
                //   - neither has one                -> this node (the approver) founds the hive
                // SetHive() is a no-op-safe call to make even when identity.HiveId already
                // equals the target value (it only throws on an actual differing overwrite).
                if (string.IsNullOrEmpty(identity.HiveId))
                {
                    var newHiveId = string.IsNullOrEmpty(req.HiveId) ? Guid.NewGuid().ToString() : req.HiveId;
                    identity.SetHive(newHiveId, req.HiveId == newHiveId && !string.IsNullOrEmpty(req.HiveId)
                        ? HiveRole.Member : HiveRole.Founder);
                }

                var salt = XorNodeIds(identity.NodeId, req.InitiatorNodeId);
                secret   = identity.DeriveSharedSecret(
                    Convert.FromBase64String(req.ExchangePublicKeyDer), salt);
            }
            catch { return false; }

            // Persist the peer, seeding LastKnownAddress from the pairing request's
            // remote IP so the mesh heartbeat can send the first outbound probe immediately.
            var peer = new HivePeer
            {
                NodeId              = req.InitiatorNodeId,
                Name                = req.InitiatorName,
                Fingerprint         = req.InitiatorFingerprint,
                SigningPublicKeyDer  = req.SigningPublicKeyDer,
                Role                = grantedRole,
                // MaxRole is the CEILING this peer can ever be promoted to later, not the
                // role it was granted at pairing time -- those are different things (grok/
                // Codex CLI BLOCKER, 2026-06-25). Mobile nodes are deliberately capped at
                // Worker (excluded from leader election); every other node's ceiling is
                // Controller, matching HivePeer.MaxRole's own documented default. Tying this
                // to grantedRole was a latent bug even before HivePanel offered a real
                // Observer/Worker choice at pairing time -- when every approval hardcoded
                // grantedRole=Worker, EVERY non-mobile peer's MaxRole was silently capped at
                // Worker too (never Controller), masked only because nothing exercised a
                // later promotion past Worker often enough to surface it. Now that Observer is
                // a real choice, the bug would have been worse: an Observer-granted peer's
                // MaxRole would freeze at Observer, permanently blocking even a later
                // promotion to Worker.
                MaxRole             = isMobile ? HiveNodeRole.Worker : HiveNodeRole.Controller,
                AllowedLanes        = allowedLanes,
                AcceptControlFrom   = isMobile ? HiveAcceptControlPolicy.Never : DefaultAcceptControlFrom,
                IsMobile            = isMobile,
                PairedAt            = DateTime.UtcNow,
                LastKnownAddress    = string.IsNullOrEmpty(remoteIp) ? "" : $"{remoteIp}:{ApiPort}",
            };
            _peers.AddOrUpdate(peer);
            _peers.SetSharedSecret(req.InitiatorNodeId, secret);

            // HIVE_MEMBERSHIP_SPEC.md §5.4 — only issue when both authorized to issue AND
            // the granted role is cert-eligible (never Controller, per §5.2). A Controller
            // grant still completes pairing normally; it just isn't backed by a cert, since
            // certs can never represent that authority tier. Best-effort: a failure to issue
            // must not fail the pairing that already succeeded above.
            string? membershipCert = null;
            if (identity.CanIssueMembershipCerts && grantedRole != HiveNodeRole.Controller)
            {
                try
                {
                    membershipCert = HiveMembershipCert.Issue(
                        identity, req.InitiatorNodeId, req.InitiatorName, grantedRole).ToBase64Json();
                }
                catch { /* non-fatal — peer is still paired, just without a cert to vouch with later */ }
            }

            _pairingResults[sessionId] = (new HivePairingResponse
            {
                Status                      = "approved",
                WarchiefNodeId              = identity.NodeId,
                WarchiefName                = Environment.MachineName,
                WarchiefFingerprint         = identity.Fingerprint,
                WarchiefSigningPublicKeyDer  = Convert.ToBase64String(identity.SigningPublicKeyDer),
                WarchiefExchangePublicKeyDer = Convert.ToBase64String(identity.ExchangePublicKeyDer),
                AssignedRole                = grantedRole.ToString(),
                AllowedLanes                = allowedLanes,
                HiveId                      = identity.HiveId,
                MembershipCert              = membershipCert,
            }, DateTime.UtcNow);
            _pendingPairings.Remove(sessionId);
            return true;
        }
    }

    public void RejectPairing(string sessionId)
    {
        lock (_pairingLock)
        {
            _pendingPairings.Remove(sessionId);
            _pairingResults[sessionId] = (new HivePairingResponse { Status = "rejected" }, DateTime.UtcNow);
        }
    }

    // Must be called under _pairingLock.
    private void PruneExpiredPairingResults()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var key in _pairingResults.Keys
                     .Where(k => _pairingResults[k].StoredAt < cutoff).ToList())
            _pairingResults.Remove(key);
    }

    // ── Serve loop ─────────────────────────────────────────────────────────────

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
            // Don't touch _listener further here -- a failed Start() can leave it
            // disposed internally, and accessing .Prefixes on it then throws
            // ObjectDisposedException, masking the real failure. The caller is
            // responsible for swapping in a fresh HttpListener before retrying.
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
                _ = HandleAsync(ctx, ct);
            }
            catch { break; }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req  = ctx.Request;
            var resp = ctx.Response;
            var path = req.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            var method = req.HttpMethod.ToUpperInvariant();

            // Read body once with a hard size cap to prevent oversized-body DoS.
            const int MaxBodyBytes = 1 * 1024 * 1024; // 1 MB
            byte[] body;
            using (var ms = new MemoryStream())
            {
                var buf   = new byte[8192];
                int total = 0;
                int read;
                while ((read = await req.InputStream.ReadAsync(buf, ct)) > 0)
                {
                    total += read;
                    if (total > MaxBodyBytes)
                    {
                        resp.StatusCode = 413;
                        Error(resp, "request body too large");
                        return;
                    }
                    ms.Write(buf, 0, read);
                }
                body = ms.ToArray();
            }

            // ── Unauthenticated endpoints ──────────────────────────────────

            if (method == "GET" && path == "/hive/info")
            {
                Ok(resp, JsonSerializer.Serialize(_info)); return;
            }

            if (method == "POST" && path == "/hive/pair")
            {
                var remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
                await HandlePairInitiateAsync(body, remoteIp, resp); return;
            }

            if (method == "GET" && path.StartsWith("/hive/pair/") && !path.EndsWith("/respond"))
            {
                HandlePairPoll(path, resp); return;
            }

            if (method == "POST" && path.StartsWith("/hive/pair/") && path.EndsWith("/respond"))
            {
                // Approve/reject is a local-UI action — reject from any remote caller.
                if (!req.IsLocal)
                {
                    resp.StatusCode = 403;
                    Error(resp, "pairing approval must originate from localhost");
                    return;
                }
                HandlePairRespond(path, body, resp); return;
            }

            // Update version probe — unauthenticated, read-only
            if (method == "GET" && path == "/hive/update/version")
            {
                var nodeId  = HiveIdentity.Load().NodeId;
                var version = OrchestratorIDE.Core.UpdateChecker.CurrentVersion();
                Ok(resp, JsonSerializer.Serialize(new { version, nodeId }, _jsonOut));
                return;
            }

            // ── Authenticated endpoints (all fail-closed — no grace period) ──

            // NOT YET WIRED: HIVE_MEMBERSHIP_SPEC.md §5.5 describes an X-Hive-Membership-Cert
            // header letting an unpaired-but-vouched-for node in via
            // HivePeerStore.TryAcceptViaMembershipCert (implemented, unit-tested). Deliberately
            // NOT consumed here yet -- _strictAuth.Validate's HMAC check requires a shared
            // secret from a completed ECDH exchange, which a cert-admitted node never had with
            // THIS verifier. Accepting a cert at this gate needs its own signature scheme (the
            // subject proving possession of its own signing key, not an HMAC it can't have) and
            // that's new security-critical surface that deserves a focused design+review pass
            // of its own, not a bolt-on here. Tracked as the explicit remainder of Phase 2.
            var authResult = _strictAuth.Validate(req, body);

            if (!authResult.Ok)
            {
                resp.StatusCode = 401;
                Error(resp, authResult.Reason ?? "unauthorized");
                return;
            }

            // Mesh heartbeat
            if (method == "POST" && path == "/hive/mesh/heartbeat")
            {
                HandleMeshHeartbeat(body, req, authResult.NodeId, resp); return;
            }

            // Election messages
            if (method == "POST" && path.StartsWith("/hive/mesh/election/"))
            {
                HandleElection(path, body, authResult.NodeId, resp); return;
            }

            // Role assignment (HIVE_MEMBERSHIP_SPEC.md §6) — manual, human-invoked override,
            // distinct from HiveElectionService's automatic failover above.
            if (method == "POST" && path == "/hive/mesh/role-assign")
            {
                HandleRoleAssign(body, authResult.NodeId, resp); return;
            }

            // Remote deploy — Warchief-only (authResult already enforced strict auth above)
            if (method == "POST" && path == "/hive/update/deploy")
            {
                var wc = ElectionService?.WarchiefNodeId;
                if (string.IsNullOrEmpty(wc) || wc != authResult.NodeId)
                {
                    resp.StatusCode = 403;
                    Error(resp, "only the Warchief may deploy updates");
                    return;
                }

                Ok(resp, JsonSerializer.Serialize(new { status = "update_started" }, _jsonOut));
                var shutdown = ShutdownCallback;
                _ = Task.Run(async () => await TriggerSelfUpdateAsync(shutdown));
                return;
            }

            resp.StatusCode = 404;
            Error(resp, "not found");
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 500;
                Error(ctx.Response, ex.Message);
            }
            catch { }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            try { ctx.Response.Close(); } catch { }
        }
    }

    // ── /hive/pair — initiate ─────────────────────────────────────────────────

    private async Task HandlePairInitiateAsync(byte[] body, string remoteIp, HttpListenerResponse resp)
    {
        HivePairingRequest? req;
        try { req = JsonSerializer.Deserialize<HivePairingRequest>(body, _jsonIn); }
        catch { resp.StatusCode = 400; Error(resp, "invalid JSON"); return; }

        if (req is null || !HiveAuthMiddleware.IsValidGuid(req.SessionId)
                        || string.IsNullOrEmpty(req.InitiatorNodeId)
                        || string.IsNullOrEmpty(req.SigningPublicKeyDer)
                        || string.IsNullOrEmpty(req.ExchangePublicKeyDer)
                        || string.IsNullOrEmpty(req.ProofSignature))
        {
            resp.StatusCode = 400; Error(resp, "missing or invalid required fields"); return;
        }

        // Guard against colliding caller-supplied SessionIds clobbering in-flight sessions.
        lock (_pairingLock)
        {
            PruneExpiredPairingResults();
            if (_pendingPairings.ContainsKey(req.SessionId) || _pairingResults.ContainsKey(req.SessionId))
            {
                resp.StatusCode = 409;
                Ok(resp, Json(new { status = "session_id_collision" }));
                return;
            }
        }

        // Verify proof and bind NodeId to signing key (prevents NodeId spoofing).
        // InitiatorNodeId must equal hex(SHA-256(signing public key DER)).
        // Proof: initiator signs (SessionId + InitiatorNodeId) with its signing key.
        try
        {
            var signingKeyDer = Convert.FromBase64String(req.SigningPublicKeyDer);

            // Cryptographically bind the claimed NodeId to the provided public key
            var expectedNodeId = Convert.ToHexString(
                SHA256.HashData(signingKeyDer)).ToLowerInvariant();
            if (!string.Equals(req.InitiatorNodeId, expectedNodeId, StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 403; Error(resp, "nodeId does not match signing key"); return;
            }

            // Also recompute the fingerprint so the UI shows a value we derived, not one the caller supplied
            req.InitiatorFingerprint = HiveIdentity.DeriveFingerprint(
                SHA256.HashData(signingKeyDer));

            var proof = Convert.FromBase64String(req.ProofSignature);
            var data  = Encoding.UTF8.GetBytes(req.SessionId + req.InitiatorNodeId);
            if (!HiveIdentity.Verify(signingKeyDer, data, proof))
            {
                resp.StatusCode = 403; Error(resp, "proof signature invalid"); return;
            }
        }
        catch { resp.StatusCode = 400; Error(resp, "invalid key/signature encoding"); return; }

        // Check if already paired
        if (_peers.IsTrusted(req.InitiatorNodeId))
        {
            resp.StatusCode = 409;
            Ok(resp, Json(new { status = "already_paired" }));
            return;
        }

        // HIVE_MEMBERSHIP_SPEC.md §4.3 — refuse rather than silently bridge two separate
        // hives. Checked here (request time) rather than only at approval time so the
        // responder isn't shown an approval card for a pairing that can never succeed.
        var localHiveId = HiveIdentity.Load().HiveId;
        if (!string.IsNullOrEmpty(localHiveId) && !string.IsNullOrEmpty(req.HiveId)
            && !string.Equals(localHiveId, req.HiveId, StringComparison.OrdinalIgnoreCase))
        {
            resp.StatusCode = 409;
            Ok(resp, Json(new { status = "hiveid_mismatch" }));
            return;
        }

        lock (_pairingLock)
        {
            _pendingPairings[req.SessionId] = (req, DateTime.UtcNow.AddMinutes(5), remoteIp);
        }

        // Fire UI event on thread pool (don't block the HTTP response)
        _ = Task.Run(() => OnPairingRequestReceived?.Invoke(req.SessionId, req));

        resp.StatusCode = 202;
        Ok(resp, Json(new { status = "pending", sessionId = req.SessionId }));
        await Task.CompletedTask;
    }

    // ── /hive/pair/{id} — poll ────────────────────────────────────────────────

    private void HandlePairPoll(string path, HttpListenerResponse resp)
    {
        var sessionId = path["/hive/pair/".Length..];
        lock (_pairingLock)
        {
            PruneExpiredPairingResults();
            // Check completed results first
            if (_pairingResults.TryGetValue(sessionId, out var result))
            {
                Ok(resp, JsonSerializer.Serialize(result.Resp, _jsonOut)); return;
            }
            // Check pending — still waiting
            if (_pendingPairings.TryGetValue(sessionId, out var pending))
            {
                if (pending.Expiry < DateTime.UtcNow)
                {
                    _pendingPairings.Remove(sessionId);
                    Ok(resp, Json(new { status = "expired" })); return;
                }
                Ok(resp, Json(new { status = "pending" })); return;
            }
        }
        resp.StatusCode = 404; Error(resp, "session not found");
    }

    // ── /hive/pair/{id}/respond — approve/reject ──────────────────────────────

    private void HandlePairRespond(string path, byte[] body, HttpListenerResponse resp)
    {
        // path = /hive/pair/{sessionId}/respond — extract by stripping known prefix/suffix
        const string prefix = "/hive/pair/";
        const string suffix = "/respond";
        var sessionId = path.Length > prefix.Length + suffix.Length
            ? path[prefix.Length..^suffix.Length]
            : "";

        string? action;
        bool    isMobile    = false;
        string  role        = "Worker";
        string[] lanes      = [];

        try
        {
            using var doc = JsonDocument.Parse(body);
            action   = doc.RootElement.GetProperty("action").GetString();
            if (doc.RootElement.TryGetProperty("role",     out var r))  role = r.GetString() ?? role;
            if (doc.RootElement.TryGetProperty("isMobile", out var m))  isMobile = m.GetBoolean();
            if (doc.RootElement.TryGetProperty("lanes",    out var l))
                lanes = l.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }
        catch { resp.StatusCode = 400; Error(resp, "invalid JSON"); return; }

        if (action == "approve")
        {
            Enum.TryParse<HiveNodeRole>(role, out var grantedRole);
            if (!ApprovePairing(sessionId, grantedRole, lanes, isMobile))
            {
                resp.StatusCode = 404;
                Error(resp, "pairing session not found or expired");
                return;
            }
            Ok(resp, Json(new { status = "approved" }));
        }
        else
        {
            RejectPairing(sessionId);
            Ok(resp, Json(new { status = "rejected" }));
        }
    }

    // ── /hive/mesh/role-assign ────────────────────────────────────────────────

    /// <summary>
    /// Never Controller through this path, regardless of what the wire body's NewRole says
    /// (HIVE_MEMBERSHIP_SPEC.md §6.1, §8 T14) -- enforced here, not just by UI convention.
    /// Pulled out of HandleRoleAssign as its own static method specifically so this
    /// security-critical restriction is unit-testable without standing up a real
    /// HttpListener (mirrors HiveAuthMiddleware's ValidateCore/IsValidGuid testing pattern).
    /// </summary>
    internal static bool TryParseAssignableRole(string? newRoleStr, out HiveNodeRole role)
    {
        role = default;
        if (!Enum.TryParse<HiveNodeRole>(newRoleStr, ignoreCase: true, out var parsed)) return false;
        if (parsed == HiveNodeRole.Controller) return false;
        role = parsed;
        return true;
    }

    private void HandleRoleAssign(byte[] body, string authenticatedNodeId, HttpListenerResponse resp)
    {
        HiveRoleAssignRequest? req;
        try { req = JsonSerializer.Deserialize<HiveRoleAssignRequest>(body, _jsonIn); }
        catch { resp.StatusCode = 400; Error(resp, "invalid JSON"); return; }
        if (req is null) { resp.StatusCode = 400; Error(resp, "missing body"); return; }

        // The wire body is attacker-shaped input the moment this endpoint exists --
        // AssignerNodeId is self-reported and informational only; the HMAC-authenticated
        // sender is the only identity that matters for authorization decisions below.
        if (!string.IsNullOrEmpty(req.AssignerNodeId)
            && !string.Equals(req.AssignerNodeId, authenticatedNodeId, StringComparison.OrdinalIgnoreCase))
        {
            resp.StatusCode = 403; Error(resp, "assigner_mismatch"); return;
        }

        if (!TryParseAssignableRole(req.NewRole, out var newRole))
        {
            resp.StatusCode = 400; Error(resp, "newRole must be Observer or Worker"); return;
        }

        var identity = HiveIdentity.Load();
        if (!string.IsNullOrEmpty(req.HiveId) && !string.IsNullOrEmpty(identity.HiveId)
            && req.HiveId != identity.HiveId)
        {
            resp.StatusCode = 409; Error(resp, "hiveid_mismatch"); return;
        }

        var assigner = _peers.Find(authenticatedNodeId);
        if (assigner is null || assigner.Revoked)
        {
            resp.StatusCode = 403; Error(resp, "not_paired"); return;
        }

        // First real enforcement of AcceptControlFrom -- previously defined but never
        // checked anywhere (HIVE_MEMBERSHIP_SPEC.md §2.4).
        string outcome;
        switch (assigner.AcceptControlFrom)
        {
            case HiveAcceptControlPolicy.Never:
                outcome = "rejected";
                break;
            case HiveAcceptControlPolicy.AnyPaired:
                identity.SetSelfRole(newRole);
                outcome = "accepted";
                break;
            case HiveAcceptControlPolicy.Allowlist:
                if (assigner.ControlAllowlist.Contains(authenticatedNodeId, StringComparer.OrdinalIgnoreCase))
                {
                    identity.SetSelfRole(newRole);
                    outcome = "accepted";
                }
                else
                {
                    outcome = "rejected";
                }
                break;
            case HiveAcceptControlPolicy.Ask:
            default:
                outcome = "pending_approval";
                _ = Task.Run(() => OnRoleAssignReceived?.Invoke(authenticatedNodeId, newRole));
                break;
        }

        Ok(resp, Json(new { status = outcome }));
    }

    /// <summary>
    /// Client-side: sends a signed role-assignment request to <paramref name="peer"/>.
    /// Returns the peer's reported status ("accepted" | "pending_approval" | "rejected") or
    /// "unreachable" (no shared secret / no known address) / "error:&lt;detail&gt;" on failure.
    /// Never throws. Used by the "Declare this machine Warchief" UI action
    /// (HIVE_MEMBERSHIP_SPEC.md §6.3).
    /// </summary>
    public static async Task<string> SendRoleAssignAsync(
        HivePeer peer, HiveNodeRole newRole, HiveIdentity identity, HivePeerStore peers,
        int timeoutMs = 8000)
    {
        if (string.IsNullOrEmpty(peer.LastKnownAddress)) return "unreachable";
        var secret = peers.GetSharedSecret(peer.NodeId);
        if (secret is null) return "unreachable";

        try
        {
            var reqBody = new HiveRoleAssignRequest
            {
                HiveId         = identity.HiveId,
                AssignerNodeId = identity.NodeId,
                NewRole        = newRole.ToString(),
                Reason         = "warchief-declaration",
            };
            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(reqBody, _jsonOut);

            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"http://{peer.LastKnownAddress}/hive/mesh/role-assign")
                { Content = new ByteArrayContent(bodyBytes) };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            HiveAuthMiddleware.SignRequest(req, bodyBytes, identity.NodeId, secret);

            using var resp = await http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return $"error:HTTP {(int)resp.StatusCode}";

            using var doc = JsonDocument.Parse(respBody);
            return doc.RootElement.TryGetProperty("status", out var s) ? (s.GetString() ?? "unknown") : "unknown";
        }
        catch (Exception ex) { return $"error:{ex.Message}"; }
    }

    // ── /hive/mesh/heartbeat ──────────────────────────────────────────────────

    private void HandleMeshHeartbeat(byte[] body, HttpListenerRequest req,
                                      string authenticatedNodeId, HttpListenerResponse resp)
    {
        if (MeshHeartbeat is null)
        {
            resp.StatusCode = 503;
            Error(resp, "mesh heartbeat service not running");
            return;
        }
        try
        {
            var payload = JsonSerializer.Deserialize<HiveMeshHeartbeatPayload>(body, _jsonIn);
            if (payload is null) { resp.StatusCode = 400; Error(resp, "invalid payload"); return; }

            // Use the HMAC-authenticated NodeId, not the self-reported payload.NodeId,
            // for any security-relevant peer lookup. The payload fields are used only
            // for display (VramFreeMb, ActiveTaskIds, etc.).
            var remoteIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
            MeshHeartbeat.ReceiveHeartbeat(authenticatedNodeId, payload, remoteIp);
            Ok(resp, Json(new { status = "ok" }));
        }
        catch { resp.StatusCode = 400; Error(resp, "bad request"); }
    }

    // ── /hive/mesh/election/* ─────────────────────────────────────────────────

    private void HandleElection(string path, byte[] body, string authenticatedNodeId,
                                 HttpListenerResponse resp)
    {
        ElectionMessage? msg;
        try { msg = JsonSerializer.Deserialize<ElectionMessage>(body, _jsonIn); }
        catch { resp.StatusCode = 400; Error(resp, "invalid JSON"); return; }
        if (msg is null) { resp.StatusCode = 400; Error(resp, "empty body"); return; }

        // Override msg.NodeId with the HMAC-authenticated sender identity.
        // Body-supplied NodeId is untrusted — it could claim to be any node.
        msg = msg with { NodeId = authenticatedNodeId };

        var election = ElectionService;
        if (election is null)
        {
            resp.StatusCode = 503;
            Error(resp, "election service not running");
            return;
        }

        var action = path["/hive/mesh/election/".Length..];
        switch (action)
        {
            case "suspect":  election.OnSuspectVoteReceived(msg);  break;
            case "claim":    election.OnElectionClaimReceived(msg); break;
            case "recover":  _ = election.OnRecoveryRequestReceived(msg, CancellationToken.None); break;
            case "stepdown": election.OnStepdownReceived(msg);     break;
            default: resp.StatusCode = 404; Error(resp, "unknown election action"); return;
        }

        Ok(resp, Json(new { status = "ok" }));
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    private static void Ok(HttpListenerResponse resp, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentType     = "application/json";
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes);
    }

    private static void Error(HttpListenerResponse resp, string msg)
    {
        var bytes = Encoding.UTF8.GetBytes(Json(new { error = msg }));
        resp.ContentType     = "application/json";
        resp.ContentLength64 = bytes.Length;
        try { resp.OutputStream.Write(bytes); } catch { }
    }

    private static string Json(object o) => JsonSerializer.Serialize(o, _jsonOut);

    // internal: shared with HivePairingClient, which must derive the identical
    // salt on the initiator side (XOR is commutative, so argument order doesn't
    // need to match the responder's call).
    internal static byte[] XorNodeIds(string a, string b)
    {
        var ba = Convert.FromHexString(a.Length >= 64 ? a[..64] : a.PadRight(64, '0'));
        var bb = Convert.FromHexString(b.Length >= 64 ? b[..64] : b.PadRight(64, '0'));
        var result = new byte[Math.Min(ba.Length, bb.Length)];
        for (int i = 0; i < result.Length; i++) result[i] = (byte)(ba[i] ^ bb[i]);
        return result;
    }

    // ── /hive/update/deploy — background self-update ─────────────────────────

    private static async Task TriggerSelfUpdateAsync(Action? shutdownCallback)
    {
        try
        {
            var settings = OrchestratorIDE.Core.AppSettings.Load();
            var result   = await OrchestratorIDE.Core.UpdateChecker.CheckAsync(settings, force: true);
            if (result is null || !result.UpdateAvailable) return;

            var stagingDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orc_update_staging");
            var progress   = new Progress<string>(_ => { });
            var updater    = new SelfUpdater();

            // Try pre-built release asset first, then fall back to build from source.
            var assetUrl = await OrchestratorIDE.Core.UpdateChecker.GetReleaseAssetUrlAsync();
            string? exePath = null;

            if (!string.IsNullOrEmpty(assetUrl))
                exePath = await updater.DownloadReleaseAsync(assetUrl, stagingDir, progress);

            if (exePath is null || !System.IO.File.Exists(exePath))
            {
                var sourceDir = string.IsNullOrEmpty(settings.SourceFolderPath)
                    ? System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "OrchestratorIDE", "source")
                    : settings.SourceFolderPath;

                await updater.PullSourceAsync(sourceDir, progress);
                await updater.BuildAndPublishAsync(sourceDir, stagingDir, progress);
                var builtExeName = OperatingSystem.IsWindows() ? "OrchestratorIDE.exe" : "OrchestratorIDE";
                exePath = System.IO.Path.Combine(stagingDir, builtExeName);
            }

            if (!System.IO.File.Exists(exePath)) return;

            updater.PrepareRelaunch(exePath);
            shutdownCallback?.Invoke();
        }
        catch { /* non-fatal — remote update is best-effort */ }
    }

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
        _cts.Cancel();
        MeshHeartbeat?.Stop();
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        // Without this, IsListening/IsRemoteReachable keep reporting true after shutdown --
        // a caller checking health post-Dispose would be lied to (Codex CLI MINOR, 2026-06-21).
        _bound     = false;
        _wideBound = false;
        // Drain in-flight handlers (best-effort, 2s cap) so most nonces land in a single
        // snapshot, then seal+flush. The seal (flush-on-every-record) guarantees the
        // security property: every ACCEPTED request's nonce reaches disk — a handler that
        // reaches RecordNonce after the drain still self-persists, so correctness does not
        // depend on the 2s cap. A handler abandoned before RecordNonce never accepted the
        // request (validation happens inside RecordNonce), so it is not a replay vector.
        DrainInFlight();
        _strictAuth.FlushAndSealForShutdown();
    }
}

/// <summary>Capability JSON returned by GET /hive/info.</summary>
public sealed record HiveNodeInfo(
    string   Name,
    string   OllamaUrl,
    string[] Models,
    int      VramFreeMb,
    int      VramTotalMb,
    string[] Lanes,
    int      RpcPort = 0);
