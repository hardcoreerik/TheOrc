// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Current state of the leader election state machine.</summary>
public enum ElectionState
{
    Normal,             // Warchief healthy and known
    SuspectDeclared,    // this node broadcast suspect; waiting for quorum
    ElectionUnderway,   // quorum reached; computing winner
    TemporaryWarchief,  // this node is serving as temporary Warchief
    RecoverySync,       // original Warchief recovering; sync in progress
}

/// <summary>
/// Deterministic Warchief failover service for HIVE MIND.
///
/// When the active Warchief misses 3 consecutive heartbeats, any node that
/// detects this broadcasts a suspect notice. If a simple majority of online
/// peers confirm, election starts.
///
/// Winner rule: lowest EnrollmentSeq among online, non-Observer, non-mobile peers.
/// Every node applies the same rule independently — no voting, no network round-trip
/// for the decision itself. Safe for N ≤ 10 nodes (Raft is overkill here).
///
/// Recovery: original Warchief reconnects, syncs queue state from temp Warchief,
/// then temp Warchief steps down. Seamless — task board never pauses.
/// </summary>
public sealed class HiveElectionService
{
    private static readonly JsonSerializerOptions _json = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HiveIdentity  _identity;
    private readonly HivePeerStore _peers;

    // ── State ─────────────────────────────────────────────────────────────────

    public ElectionState State             { get; private set; } = ElectionState.Normal;
    public string?       WarchiefNodeId    { get; private set; }
    public bool          IsTemporaryWarchief => State == ElectionState.TemporaryWarchief;

    // Stores the Warchief we were trying to recover so GetOriginalWarchiefId stays deterministic.
    private string? _preFailoverWarchiefId;

    private readonly Dictionary<string, DateTime> _suspectVotes     = [];
    private readonly HashSet<string>              _electionAcks     = [];
    private readonly Lock                         _stateLock        = new();

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired when election state or Warchief assignment changes.</summary>
    public event Action<ElectionState, string?>? OnStateChanged;
    public event Action<string>?                 OnLog;

    public HiveElectionService(HiveIdentity identity, HivePeerStore peers)
    {
        _identity = identity;
        _peers    = peers;
    }

    /// <summary>
    /// Sets the known Warchief. Must be called by the app when a HIVE session starts
    /// (either this node is the Warchief, or we've received a session context identifying who is).
    /// Without this, suspect detection cannot fire.
    /// </summary>
    public void SetWarchief(string nodeId)
    {
        lock (_stateLock)
        {
            WarchiefNodeId         = nodeId;
            _preFailoverWarchiefId = null;   // clear any stale election state
            _suspectVotes.Clear();
            State = ElectionState.Normal;
        }
        Log($"⚙ Warchief set to {nodeId}");
    }

    /// <summary>
    /// Infers the initial Warchief from the peer store: the enrolled peer with the
    /// lowest EnrollmentSeq that has Controller MaxRole. Call after enrollment completes
    /// if the app doesn't have an explicit Warchief assignment.
    /// </summary>
    public void InferWarchiefFromPeerStore()
    {
        var boss = _peers.All()
            .Where(p => !p.Revoked && !p.IsMobile && p.MaxRole >= HiveNodeRole.Controller)
            .OrderBy(p => p.EnrollmentSeq)
            .FirstOrDefault();

        if (boss is not null)
            SetWarchief(boss.NodeId);
        else
            Log("⚙ No Controller-capable peers enrolled — Warchief not set");
    }

    // ── Called by HiveMeshHeartbeat ───────────────────────────────────────────

    /// <summary>Called when a peer that is the current Warchief goes suspect.</summary>
    public async Task OnPeerSuspectAsync(HivePeer peer, CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (State != ElectionState.Normal && State != ElectionState.SuspectDeclared) return;
            if (peer.NodeId != WarchiefNodeId) return;   // not the Warchief — ignore

            Log($"⚡ Warchief {peer.Name} suspect — broadcasting notice");
            _suspectVotes[_identity.NodeId] = DateTime.UtcNow;
            State = ElectionState.SuspectDeclared;
        }

        await BroadcastAsync("/hive/mesh/election/suspect", new ElectionMessage
        {
            NodeId  = _identity.NodeId,
            Payload = peer.NodeId,
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sig     = SignElectionPayload(peer.NodeId),
        }, ct);

        CheckQuorumAndElect(ct);
    }

    /// <summary>Called when a heartbeat arrives from any peer (checks if Warchief recovered).</summary>
    public void OnPeerHeartbeatReceived(HiveMeshHeartbeatPayload payload)
    {
        lock (_stateLock)
        {
            if (State == ElectionState.SuspectDeclared && payload.NodeId == WarchiefNodeId)
            {
                // Warchief came back before election completed — cancel
                _suspectVotes.Clear();
                State = ElectionState.Normal;
                Log($"✅ Warchief {payload.NodeId} recovered before election — cancelled");
                OnStateChanged?.Invoke(State, WarchiefNodeId);
            }
        }
    }

    // ── Inbound election messages (called by HiveNodeServer) ─────────────────

    public void OnSuspectVoteReceived(ElectionMessage msg)
    {
        lock (_stateLock)
        {
            if (msg.Payload != WarchiefNodeId) return;

            // If a trusted peer detected the Warchief going suspect before we did,
            // join the suspect phase rather than discarding the vote.  The HMAC layer
            // guarantees the sender is an enrolled peer; we record their vote and let
            // CheckQuorumAndElect decide whether we have a majority.
            if (State == ElectionState.Normal)
                State = ElectionState.SuspectDeclared;

            // Discard votes that arrive after election is resolved.
            if (State != ElectionState.SuspectDeclared && State != ElectionState.ElectionUnderway)
                return;

            _suspectVotes[msg.NodeId] = DateTime.UtcNow;
        }
        CheckQuorumAndElect(CancellationToken.None);
    }

    public void OnElectionClaimReceived(ElectionMessage msg)
    {
        lock (_stateLock)
        {
            // Accept claims in both SuspectDeclared and ElectionUnderway.
            // The deterministic NodeId winner check below is the real guard — we compute
            // the expected winner locally from peer store state, so accepting in
            // SuspectDeclared is safe: a non-winner claim is rejected regardless of state.
            // Blocking until ElectionUnderway causes liveness issues when the claimer
            // reached quorum before this node and won't re-send the claim.
            if (State != ElectionState.ElectionUnderway && State != ElectionState.SuspectDeclared)
                return;

            var claimer = _peers.Find(msg.NodeId);
            // Reject claim from ineligible peers (mobile, Observer-max, the failed Warchief itself)
            if (claimer is null || claimer.IsMobile || claimer.MaxRole < HiveNodeRole.Worker) return;
            // When a claim arrives during SuspectDeclared (before quorum was verified
            // on this node), _preFailoverWarchiefId is not yet set — fall back to
            // WarchiefNodeId to ensure the failing Warchief is still excluded.
            var excludeId = _preFailoverWarchiefId ?? WarchiefNodeId;
            if (claimer.NodeId == excludeId) return;

            // Only accept if this claimer is the deterministic winner.
            // Winner = lexicographically-lowest NodeId among online eligible peers.
            // Recompute locally rather than trusting the claim message.
            var cutoff     = DateTime.UtcNow.AddSeconds(-90);
            var candidates = _peers.All()
                .Where(p => !p.Revoked && !p.IsMobile && p.MaxRole >= HiveNodeRole.Worker
                         && p.NodeId != excludeId
                         && (p.LastHeartbeat ?? DateTime.MinValue) > cutoff)
                .OrderBy(p => p.NodeId, StringComparer.Ordinal)
                .ToList();

            var selfPeer = _peers.Find(_identity.NodeId);
            bool selfIsEligible = selfPeer is { IsMobile: false }
                && selfPeer.MaxRole >= HiveNodeRole.Worker
                && selfPeer.NodeId != excludeId;

            // Expected winner: smallest NodeId among candidates and self (if eligible).
            string expectedWinnerNodeId = selfIsEligible
                ? (candidates.Count > 0
                    ? (string.CompareOrdinal(_identity.NodeId, candidates[0].NodeId) <= 0
                        ? _identity.NodeId : candidates[0].NodeId)
                    : _identity.NodeId)
                : (candidates.Count > 0 ? candidates[0].NodeId : "");

            if (claimer.NodeId != expectedWinnerNodeId) return;  // not the rightful winner

            WarchiefNodeId = msg.NodeId;
            State          = ElectionState.Normal;
            _suspectVotes.Clear();   // election complete — stale votes must not carry over
            Log($"👑 Accepted {claimer.Name} ({claimer.NodeId[..8]}…) as temporary Warchief");
            OnStateChanged?.Invoke(State, WarchiefNodeId);
        }
    }

    public async Task OnRecoveryRequestReceived(ElectionMessage msg, CancellationToken ct)
    {
        bool shouldStepDown;
        lock (_stateLock)
        {
            // Accept recovery from the node that was Warchief before the failover,
            // not a recomputed "who should be Warchief" — that could differ if peers changed.
            shouldStepDown = IsTemporaryWarchief && msg.NodeId == _preFailoverWarchiefId;
        }
        if (!shouldStepDown) return;

        Log($"🔄 Original Warchief {msg.NodeId} recovering — stepping down after sync");

        lock (_stateLock) State = ElectionState.RecoverySync;
        OnStateChanged?.Invoke(State, msg.NodeId);

        // Broadcast stepdown so all peers know the original is resuming
        await BroadcastAsync("/hive/mesh/election/stepdown", new ElectionMessage
        {
            NodeId  = _identity.NodeId,
            Payload = msg.NodeId,   // resuming Warchief's NodeId
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sig     = SignElectionPayload(msg.NodeId),
        }, ct);

        lock (_stateLock)
        {
            WarchiefNodeId         = msg.NodeId;
            _preFailoverWarchiefId = null;
            _suspectVotes.Clear();  // recovery complete — stale votes must not carry into next failover
            State                  = ElectionState.Normal;
        }
        Log($"✅ Stepped down — {msg.NodeId} is Warchief again");
        OnStateChanged?.Invoke(State, WarchiefNodeId);
    }

    public void OnStepdownReceived(ElectionMessage msg)
    {
        lock (_stateLock)
        {
            if (State == ElectionState.TemporaryWarchief) return;  // we're the temp — handled in OnRecovery

            // Only accept stepdown from the current temporary Warchief,
            // and only when it names the correct original Warchief as the resuming node.
            if (msg.NodeId != WarchiefNodeId) return;
            if (msg.Payload != _preFailoverWarchiefId) return;

            WarchiefNodeId = msg.Payload;
            _preFailoverWarchiefId = null;
            _suspectVotes.Clear();   // recovery complete — stale votes must not carry over
            State          = ElectionState.Normal;
            Log($"✅ {msg.Payload} resumed as Warchief");
            OnStateChanged?.Invoke(State, WarchiefNodeId);
        }
    }

    // ── Internal election logic ────────────────────────────────────────────────

    private void CheckQuorumAndElect(CancellationToken ct)
    {
        List<HivePeer> online;
        int            voteCount;
        lock (_stateLock)
        {
            if (State != ElectionState.SuspectDeclared) return;

            // Online = last heartbeat within 90s.
            // The local node never receives its own heartbeat so LastHeartbeat is always
            // null — treat it as always-online so quorum counts are accurate.
            // The suspected Warchief is excluded from the online count: they stopped
            // sending heartbeats, so counting them inflates quorum and can stall a 2-node mesh.
            var suspectedWarchief = WarchiefNodeId;
            var cutoff            = DateTime.UtcNow.AddSeconds(-90);
            online     = _peers.All()
                .Where(p => !p.Revoked
                         && p.NodeId != suspectedWarchief               // don't count the suspect
                         && (p.NodeId == _identity.NodeId               // self = always online
                             || (p.LastHeartbeat ?? DateTime.MinValue) > cutoff))
                .ToList();

            voteCount  = _suspectVotes.Count;
            int quorum = online.Count / 2 + 1;
            if (voteCount < quorum) return;

            State = ElectionState.ElectionUnderway;
            _preFailoverWarchiefId = WarchiefNodeId;  // track before any change
            Log($"⚡ Quorum ({voteCount}/{online.Count}) — election started");
        }

        // Deterministic winner: lexicographically-lowest NodeId among eligible online peers.
        // NodeId = hex(SHA-256(signing key)) — globally unique, globally consistent
        // (every node derives the same NodeId for a peer from the exchanged public key).
        // EnrollmentSeq is local-only and not safe for cross-node election decisions.
        // Exclude the failing Warchief — they're why we're electing.
        var suspectedId = _preFailoverWarchiefId;
        var candidates  = online
            .Where(p => p.MaxRole >= HiveNodeRole.Worker
                     && !p.IsMobile
                     && p.NodeId != suspectedId)   // exclude the failing Warchief
            .OrderBy(p => p.NodeId, StringComparer.Ordinal)
            .ToList();

        var selfPeer = _peers.Find(_identity.NodeId);
        bool selfIsEligible = selfPeer is { IsMobile: false }
            && selfPeer.MaxRole >= HiveNodeRole.Worker
            && selfPeer.NodeId != suspectedId;

        // This node wins if it's eligible AND its NodeId sorts first among all candidates
        // (both peer candidates and itself).
        bool iAmWinner = selfIsEligible
            && (candidates.Count == 0
                || string.CompareOrdinal(_identity.NodeId, candidates[0].NodeId) <= 0);

        if (iAmWinner)
        {
            lock (_stateLock)
            {
                State          = ElectionState.TemporaryWarchief;
                WarchiefNodeId = _identity.NodeId;
            }
            Log($"👑 I am the temporary Warchief (nodeId={_identity.NodeId[..8]}…)");
            OnStateChanged?.Invoke(ElectionState.TemporaryWarchief, _identity.NodeId);

            _ = BroadcastAsync("/hive/mesh/election/claim", new ElectionMessage
            {
                NodeId  = _identity.NodeId,
                Payload = "claim",
                Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sig     = SignElectionPayload("claim"),
            }, ct);
        }
        else if (candidates.Count > 0)
        {
            var expected = candidates[0];
            // Non-winning node: stay in ElectionUnderway and wait for the winner's /claim.
            // Do NOT set WarchiefNodeId here — only OnElectionClaimReceived does that
            // after it validates the claimer matches the deterministic winner.
            Log($"👑 Expecting {expected.Name} ({expected.NodeId[..8]}…) to claim Warchief — waiting");
            // State remains ElectionUnderway until claim arrives
        }
        else
        {
            // No eligible candidates and this node is also ineligible — no Warchief.
            // Roll back to SuspectDeclared so we can retry when peers come back online.
            Log("⚠ No eligible Warchief candidates — election inconclusive; will retry");
            lock (_stateLock) State = ElectionState.SuspectDeclared;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string SignElectionPayload(string payload)
    {
        var data = Encoding.UTF8.GetBytes(_identity.NodeId + payload);
        return Convert.ToBase64String(_identity.Sign(data));
    }

    private async Task BroadcastAsync(string path, ElectionMessage msg, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(msg, _json);

        var tasks = _peers.All()
            .Where(p => !p.Revoked && !string.IsNullOrEmpty(p.LastKnownAddress))
            .Select(peer => PostSignedAsync($"http://{peer.LastKnownAddress}{path}",
                                            body, peer.NodeId, ct))
            .ToList();

        await Task.WhenAll(tasks);
    }

    private async Task PostSignedAsync(string url, byte[] body, string peerNodeId,
                                        CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, url)
                { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var secret = _peers.GetSharedSecret(peerNodeId);
            if (secret is not null)
                HiveAuthMiddleware.SignRequest(req, body, _identity.NodeId, secret);

            await http.SendAsync(req, ct);
        }
        catch { /* non-fatal — election messages are best-effort */ }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);
}
