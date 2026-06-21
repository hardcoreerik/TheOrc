// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Initiator side of the HIVE pairing ceremony (POST /hive/pair, then poll
/// GET /hive/pair/{sessionId}). The responder side already existed in
/// HiveNodeServer (HandlePairInitiateAsync / HandlePairPoll / HandlePairRespond)
/// and HivePanel.OnPairingRequest -- but nothing in either UI (WPF, before its
/// deletion, or Avalonia) ever called the initiate endpoint. Found 2026-06-20:
/// pairing had no way to actually be started by a user on either side.
/// </summary>
public static class HivePairingClient
{
    // HiveNodeServer serializes responses with PropertyNamingPolicy.CamelCase (_jsonOut,
    // e.g. "status", "warchiefNodeId"). Deserializing with default (case-sensitive, PascalCase-
    // matching) options means every property silently misses and stays at its type default --
    // HivePairingResponse.Status defaults to "pending", so EVERY poll looked like "pending"
    // forever regardless of the server's real state, masking a real "approved" response and
    // eventually returning Outcome.TimedOut even when the server approved within seconds.
    // Found 2026-06-21 in a live pairing test: server's hive-peers.json showed the peer paired
    // ~5s after the request, but the client (with --timeout up to 90s) still reported timeout.
    private static readonly JsonSerializerOptions _jsonIn = new() { PropertyNameCaseInsensitive = true };

    public enum Outcome { Approved, Rejected, Expired, TimedOut, AlreadyPaired, Error }

    public sealed record Result(Outcome Outcome, string? Message = null, PendingTrust? Pending = null);

    /// <summary>
    /// A derived peer + shared secret awaiting explicit operator confirmation before
    /// being persisted. GET /hive/pair/{sessionId} is deliberately unauthenticated
    /// (HiveNodeServer.cs doc comment, ~line 17) -- the NodeId-binding check in
    /// <see cref="CompletePairing"/> only proves the response is internally
    /// self-consistent, NOT that it genuinely came from the intended target rather
    /// than an on-path attacker forging an "approved" reply with their own keys
    /// (Codex CLI BLOCKER, 2026-06-20). Persisting trust before that human check
    /// happens is fail-open -- the caller MUST show <see cref="Fingerprint"/> to the
    /// operator and only call <see cref="ConfirmAndTrust"/> if they confirm it
    /// matches what the target machine's own HIVE panel displays.
    /// </summary>
    public sealed record PendingTrust(string Fingerprint, HivePeer Peer, byte[] Secret);

    /// <summary>
    /// Persists a peer + shared secret the caller has confirmed (via out-of-band
    /// fingerprint comparison) is trustworthy. Call only after the operator has
    /// explicitly verified <see cref="PendingTrust.Fingerprint"/>.
    /// </summary>
    public static void ConfirmAndTrust(PendingTrust pending)
    {
        HivePeerStore.Default.AddOrUpdate(pending.Peer);
        HivePeerStore.Default.SetSharedSecret(pending.Peer.NodeId, pending.Secret);
    }

    /// <summary>
    /// Sends a pairing request to <paramref name="targetHost"/> (bare host/IP,
    /// no scheme or port -- same convention as <see cref="HiveNodeServer.ProbeAsync"/>),
    /// polls for approval every 2s up to <paramref name="timeoutSec"/>, and on
    /// approval derives the shared secret and persists the peer into
    /// <see cref="HivePeerStore.Default"/>. Never throws -- every failure mode,
    /// including identity load / DPAPI errors before the request is even built,
    /// converts to a Result so callers always get a clear outcome to show the user.
    /// </summary>
    public static async Task<Result> PairAsync(
        string targetHost,
        bool isMobile = false,
        int vramMb = 0,
        string suggestedRole = "Worker",
        int timeoutSec = 120,
        CancellationToken ct = default)
    {
        try
        {
            return await PairCoreAsync(targetHost, isMobile, vramMb, suggestedRole, timeoutSec, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new Result(Outcome.TimedOut);
        }
        catch (Exception ex)
        {
            return new Result(Outcome.Error, ex.Message);
        }
    }

    private static async Task<Result> PairCoreAsync(
        string targetHost, bool isMobile, int vramMb, string suggestedRole,
        int timeoutSec, CancellationToken ct)
    {
        var identity = HiveIdentity.Load();
        var sessionId = Guid.NewGuid().ToString();

        var proofData = Encoding.UTF8.GetBytes(sessionId + identity.NodeId);
        var proof = identity.Sign(proofData);

        var req = new HivePairingRequest
        {
            SessionId            = sessionId,
            InitiatorNodeId      = identity.NodeId,
            InitiatorName        = Environment.MachineName,
            InitiatorFingerprint = identity.Fingerprint,
            SigningPublicKeyDer  = Convert.ToBase64String(identity.SigningPublicKeyDer),
            ExchangePublicKeyDer = Convert.ToBase64String(identity.ExchangePublicKeyDer),
            ProofSignature       = Convert.ToBase64String(proof),
            IsMobileClient       = isMobile,
            VramMb               = vramMb,
            SuggestedRole        = suggestedRole,
            HiveId               = identity.HiveId,
        };

        var apiHost = $"http://{targetHost}:{HiveNodeServer.ApiPort}";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        HttpResponseMessage initResp;
        try
        {
            initResp = await http.PostAsJsonAsync($"{apiHost}/hive/pair", req, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The raw exception message ("The request was canceled due to the configured
            // HttpClient.Timeout of 10 seconds elapsing") is technically accurate but doesn't
            // tell the operator what to actually check. The most common cause by far: the
            // target's Ollama port is reachable (so it shows "online" in the constellation
            // view) but its HIVE node server on this port simply isn't running -- the two
            // ports are independent. Lead with that likely explanation, keep the raw detail
            // for anyone who needs it (found 2026-06-21 from a live pairing-failure report).
            return new Result(Outcome.Error,
                $"Could not reach {targetHost} on the HIVE port. The target machine may be " +
                "reachable for other purposes (e.g. Ollama) while its HIVE node server isn't " +
                $"running -- check \"Enable HIVE MIND\" in its Settings. ({ex.Message})");
        }
        using (initResp) // disposed once we leave this scope -- not needed past the checks below
        {
            if (initResp.StatusCode == HttpStatusCode.Conflict)
            {
                var body = await initResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                // Server sends { "status": "already_paired" } or { "status": "session_id_collision" }
                // (HiveNodeServer.cs ~line 457). Parse the field properly rather than substring-
                // matching the raw body, which would be fragile against response format changes.
                string? status = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    status = doc.RootElement.GetProperty("status").GetString();
                }
                catch { /* fall through with status == null -> Outcome.Error below */ }

                return new Result(
                    status switch
                    {
                        "already_paired"  => Outcome.AlreadyPaired,
                        "hiveid_mismatch" => Outcome.Error,
                        _                  => Outcome.Error,
                    },
                    status == "hiveid_mismatch"
                        ? $"{targetHost} already belongs to a different hive than this machine. " +
                          "Pairing across two separate hives isn't supported — see HIVE_MEMBERSHIP_SPEC.md §4.3."
                        : body);
            }
            if (!initResp.IsSuccessStatusCode)
            {
                var body = await initResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new Result(Outcome.Error, $"HTTP {(int)initResp.StatusCode}: {body}");
            }
        }

        // ── Poll for approval ──────────────────────────────────────────────────
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        var pollUrl  = $"{apiHost}/hive/pair/{sessionId}";

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);

            HivePairingResponse? resp;
            try
            {
                var json = await http.GetStringAsync(pollUrl, ct).ConfigureAwait(false);
                resp = JsonSerializer.Deserialize<HivePairingResponse>(json, _jsonIn);
            }
            catch
            {
                continue; // transient network blip -- keep polling until the deadline
            }

            if (resp is null) continue;

            switch (resp.Status)
            {
                case "pending":
                    continue;
                case "rejected":
                    return new Result(Outcome.Rejected);
                case "expired":
                    return new Result(Outcome.Expired);
                case "approved":
                    return CompletePairing(identity, resp, targetHost);
                default:
                    return new Result(Outcome.Error, $"Unexpected status: {resp.Status}");
            }
        }

        return new Result(Outcome.TimedOut);
    }

    private static Result CompletePairing(HiveIdentity identity, HivePairingResponse resp, string targetHost)
    {
        // Fail closed on a missing fingerprint rather than building a PendingTrust the
        // operator could click through without anything meaningful to compare -- since
        // the poll endpoint is unauthenticated, the fingerprint comparison is the only
        // real check, so it must not be skippable by omission (Codex CLI MINOR, 2026-06-20).
        if (string.IsNullOrWhiteSpace(resp.WarchiefFingerprint))
            return new Result(Outcome.Error, "Approved response had no fingerprint to verify -- refusing to trust this peer.");

        if (string.IsNullOrEmpty(resp.WarchiefNodeId) ||
            string.IsNullOrEmpty(resp.WarchiefSigningPublicKeyDer) ||
            string.IsNullOrEmpty(resp.WarchiefExchangePublicKeyDer))
        {
            return new Result(Outcome.Error, "Approved response was missing required peer fields.");
        }

        // HIVE_MEMBERSHIP_SPEC.md §4.3 reconciliation, initiator side. The responder already
        // decided the resulting HiveId (adopted ours, kept its own, or founded a fresh one if
        // neither side had one) -- we just need to adopt whatever it settled on. The request-time
        // hiveid_mismatch check in HiveNodeServer already prevents reaching this point when both
        // sides had one and they genuinely differed, but double-check here too rather than
        // trusting that invariant blindly across two different code paths.
        if (!string.IsNullOrEmpty(resp.HiveId))
        {
            if (string.IsNullOrEmpty(identity.HiveId))
                identity.SetHive(resp.HiveId, HiveRole.Member);
            else if (identity.HiveId != resp.HiveId)
                return new Result(Outcome.Error,
                    "This machine and the target belong to different hives -- refusing to complete pairing.");
        }

        byte[] secret;
        try
        {
            // Bind WarchiefNodeId to the supplied signing key, mirroring the same
            // check HiveNodeServer.HandlePairInitiateAsync performs on the
            // initiator's claimed NodeId (HiveNodeServer.cs ~line 434). Without
            // this, a tampered poll response could substitute a false identity
            // while keeping keys internally consistent.
            var signingKeyDer = Convert.FromBase64String(resp.WarchiefSigningPublicKeyDer);
            var expectedNodeId = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(signingKeyDer)).ToLowerInvariant();
            if (!string.Equals(resp.WarchiefNodeId, expectedNodeId, StringComparison.OrdinalIgnoreCase))
                return new Result(Outcome.Error, "Warchief NodeId does not match its signing key -- refusing to trust this peer.");

            var salt = HiveNodeServer.XorNodeIds(identity.NodeId, resp.WarchiefNodeId);
            secret = identity.DeriveSharedSecret(
                Convert.FromBase64String(resp.WarchiefExchangePublicKeyDer), salt);
        }
        catch (Exception ex)
        {
            return new Result(Outcome.Error, $"Key derivation failed: {ex.Message}");
        }

        var peer = new HivePeer
        {
            NodeId              = resp.WarchiefNodeId,
            Name                = resp.WarchiefName ?? resp.WarchiefNodeId,
            Fingerprint         = resp.WarchiefFingerprint ?? "",
            SigningPublicKeyDer = resp.WarchiefSigningPublicKeyDer,
            // Observer baseline, Worker ceiling -- NOT Controller for either. The
            // response only proves identity/key consistency, not what role this peer
            // actually warrants; pairing can now be initiated toward any live node
            // card, not just a known Warchief. MaxRole is the hard ceiling
            // HiveElectionService checks for Warchief eligibility (Codex CLI BLOCKER,
            // 2026-06-20) -- leaving it at Controller would let an unverified,
            // just-paired peer become eligible for real Warchief authority via
            // election, with no separate, conscious grant ever having happened.
            // Promoting a peer to Controller-eligible must be a deliberate, later
            // action, not a side effect of pairing.
            Role                = HiveNodeRole.Observer,
            MaxRole             = HiveNodeRole.Worker,
            PairedAt            = DateTime.UtcNow,
            // Mirrors the server-side convention in HiveNodeServer.ApprovePairing
            // (LastKnownAddress seeded from the pairing exchange so the mesh heartbeat
            // can probe immediately, without waiting for an inbound contact first).
            LastKnownAddress    = $"{targetHost}:{HiveNodeServer.ApiPort}",
        };

        // Deliberately NOT persisted here -- see PendingTrust's doc comment. The
        // caller must show peer.Fingerprint to the operator and only call
        // ConfirmAndTrust() if they confirm it matches the target machine's display.
        return new Result(Outcome.Approved, Pending: new PendingTrust(peer.Fingerprint, peer, secret));
    }
}
