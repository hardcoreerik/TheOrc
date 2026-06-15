// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Observed liveness state of a hive peer.</summary>
public enum HivePeerLiveness { Online, Suspect, Offline }

/// <summary>
/// Peer-to-peer mesh heartbeat service.
///
/// Every enrolled node sends a signed POST /hive/mesh/heartbeat to every peer
/// in hive-peers.json on a configurable interval (15s LAN / 30s Tailscale).
///
/// Liveness thresholds:
///   Suspect  = 3 consecutive missed heartbeats (~45s)
///   Offline  = 6 consecutive missed heartbeats (~90s)
///
/// On Warchief suspect: notifies HiveElectionService to start election.
/// On recovery: missed counter resets, liveness returns to Online.
///
/// The UDP beacon is retained for unauthenticated new-node discovery; this
/// service handles all liveness signalling for enrolled peers.
/// </summary>
public sealed class HiveMeshHeartbeat : IDisposable
{
    public const string HeartbeatPath = "/hive/mesh/heartbeat";

    private static readonly JsonSerializerOptions _json = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HiveIdentity       _identity;
    private readonly HivePeerStore      _peers;
    private readonly HiveElectionService _election;
    private          CancellationTokenSource? _cts;

    // ── Configuration ─────────────────────────────────────────────────────────

    public int          HeartbeatIntervalMs { get; set; } = 15_000;
    public string       LocalNodeName       { get; set; } = Environment.MachineName;
    public int          VramFreeMb          { get; set; }
    public HiveNodeRole CurrentRole         { get; set; } = HiveNodeRole.Worker;
    public Func<string[]>? GetActiveTaskIds { get; set; }  // live snapshot from task queue

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, int> _missedCounts = [];
    private          long                    _sequence;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired on the thread-pool when a peer's liveness state changes.</summary>
    public event Action<string, HivePeerLiveness>? OnPeerLivenessChanged;
    public event Action<string>?                   OnLog;

    public HiveMeshHeartbeat(HiveIdentity identity, HivePeerStore peers,
                              HiveElectionService election)
    {
        _identity = identity;
        _peers    = peers;
        _election = election;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_cts is { IsCancellationRequested: false }) return;
        _cts = new CancellationTokenSource();
        _ = HeartbeatLoopAsync(_cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    // ── Outbound heartbeat loop ───────────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatIntervalMs, ct); }
            catch (OperationCanceledException) { break; }

            await SendHeartbeatsAsync(ct);
        }
    }

    private async Task SendHeartbeatsAsync(CancellationToken ct)
    {
        var payload = new HiveMeshHeartbeatPayload
        {
            NodeId        = _identity.NodeId,
            NodeName      = LocalNodeName,
            Timestamp     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CurrentRole   = CurrentRole.ToString(),
            VramFreeMb    = VramFreeMb,
            ActiveTaskIds = GetActiveTaskIds?.Invoke() ?? [],
            Sequence      = Interlocked.Increment(ref _sequence),
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(payload, _json);

        var tasks = _peers.All()
            .Where(p => !p.Revoked && !string.IsNullOrEmpty(p.LastKnownAddress))
            .Select(peer => SendToPeerAsync(peer, body, ct))
            .ToList();

        await Task.WhenAll(tasks);
    }

    private async Task SendToPeerAsync(HivePeer peer, byte[] body, CancellationToken ct)
    {
        var reached = await PostSignedAsync(
            $"http://{peer.LastKnownAddress}{HeartbeatPath}", body, peer.NodeId, ct);

        int missed;
        lock (_missedCounts)
        {
            _missedCounts.TryGetValue(peer.NodeId, out missed);
            if (reached)
                _missedCounts[peer.NodeId] = 0;
            else
                _missedCounts[peer.NodeId] = missed + 1;
        }

        if (!reached)
        {
            var liveness = (missed + 1) switch
            {
                >= 6 => HivePeerLiveness.Offline,
                >= 3 => HivePeerLiveness.Suspect,
                _    => HivePeerLiveness.Online,
            };
            OnPeerLivenessChanged?.Invoke(peer.NodeId, liveness);

            if (liveness == HivePeerLiveness.Suspect)
                _ = _election.OnPeerSuspectAsync(peer, ct);
        }
        else if (missed > 0)
        {
            OnPeerLivenessChanged?.Invoke(peer.NodeId, HivePeerLiveness.Online);
        }
    }

    // ── Inbound heartbeat (called by HiveNodeServer when /hive/mesh/heartbeat arrives) ──

    /// <param name="authenticatedNodeId">The NodeId verified by HMAC auth — not the self-reported value from the payload body.</param>
    public void ReceiveHeartbeat(string authenticatedNodeId, HiveMeshHeartbeatPayload payload, string remoteIp)
    {
        lock (_missedCounts)
            _missedCounts[authenticatedNodeId] = 0;

        // Use authenticated id for peer lookup; payload fields are display/metrics only.
        var address = $"{remoteIp}:{HiveNodeServer.ApiPort}";
        // Always update liveness so LastHeartbeat is stamped even if role is unrecognized.
        // Fall back to the peer's current ActiveRole rather than leaving it stale.
        var existing = _peers.Find(authenticatedNodeId);
        var role     = Enum.TryParse<HiveNodeRole>(payload.CurrentRole, out var parsed)
            ? parsed
            : (existing?.ActiveRole ?? HiveNodeRole.Observer);
        _peers.UpdateLiveness(authenticatedNodeId, address, role, payload.VramFreeMb);

        OnPeerLivenessChanged?.Invoke(authenticatedNodeId, HivePeerLiveness.Online);

        // Pass a payload with the authenticated NodeId so election logic is also clean.
        _election.OnPeerHeartbeatReceived(payload with { NodeId = authenticatedNodeId });
    }

    // ── HTTP helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the peer is REACHABLE (connection succeeded), regardless of HTTP status.
    /// HTTP errors (401, 500, etc.) mean the peer is online but there's an app/auth issue —
    /// that must NOT be counted as a missed heartbeat or it would falsely trigger election.
    /// Only an exception (connection refused, timeout, DNS failure) means the peer is down.
    /// </summary>
    private async Task<bool> PostSignedAsync(string url, byte[] body, string peerNodeId,
                                              CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, url)
                { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var secret = _peers.GetSharedSecret(peerNodeId);
            if (secret is not null)
                HiveAuthMiddleware.SignRequest(req, body, _identity.NodeId, secret);

            // Any HTTP response (even 4xx/5xx) means the peer is alive and reachable.
            await http.SendAsync(req, ct);
            return true;
        }
        catch { return false; }  // exception = connection failed = peer is down
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose() => Stop();
}
