// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T17 — HIVE v1.6 security pure-logic tests (no network, no disk).
/// Covers: HiveAuthMiddleware validators, HMAC round-trip, nonce replay,
/// timestamp window, HiveIdentity crypto, fingerprint stability,
/// ECDH shared-secret symmetry, and HiveElectionService state machine.
/// </summary>
[TestFixture]
public class T17_HiveSecurityTests
{
    // ── HiveAuthMiddleware: static input validators ────────────────────────────

    [TestCase("550e8400-e29b-41d4-a716-446655440000", true)]
    [TestCase("550E8400-E29B-41D4-A716-446655440000", true)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("not-a-guid", false)]
    [TestCase("550e8400-e29b-41d4-a716-44665544000G", false)]
    [TestCase("550e8400-e29b-41d4-a716", false)]
    public void IsValidGuid(string? input, bool expected)
        => Assert.That(HiveAuthMiddleware.IsValidGuid(input), Is.EqualTo(expected));

    [TestCase("BIGRIG-01", true)]
    [TestCase("node_a", true)]
    [TestCase("x", true)]
    [TestCase("", false)]
    [TestCase(null, false)]
    [TestCase("has space", false)]
    [TestCase("has@symbol", false)]
    public void IsValidWorkerId(string? input, bool expected)
        => Assert.That(HiveAuthMiddleware.IsValidWorkerId(input), Is.EqualTo(expected));

    [TestCase("Researcher",   true)]
    [TestCase("Coder",        true)]
    [TestCase("UIDeveloper",  true)]
    [TestCase("Tester",       true)]
    [TestCase("Admin",        false)]
    [TestCase("researcher",   false)]
    [TestCase(null,           false)]
    [TestCase("",             false)]
    public void IsValidRole(string? input, bool expected)
        => Assert.That(HiveAuthMiddleware.IsValidRole(input), Is.EqualTo(expected));

    [Test] public void Clamp_Null_ReturnsNull()
        => Assert.That(HiveAuthMiddleware.Clamp(null, 10), Is.Null);

    [Test] public void Clamp_ShortString_ReturnsUnchanged()
        => Assert.That(HiveAuthMiddleware.Clamp("hello", 10), Is.EqualTo("hello"));

    [Test] public void Clamp_ExactLength_ReturnsUnchanged()
        => Assert.That(HiveAuthMiddleware.Clamp("hello", 5), Is.EqualTo("hello"));

    [Test] public void Clamp_OverLength_Truncates()
        => Assert.That(HiveAuthMiddleware.Clamp("hello world", 5), Is.EqualTo("hello"));

    // ── HiveAuthMiddleware: HMAC round-trip ────────────────────────────────────

    // Returns (auth, nodeId, secret) — disposables already cleaned up inside.
    private static (HiveAuthMiddleware auth, string nodeId, byte[] secret) MakeAuthSetup()
    {
        using var identity = HiveIdentity.CreateEphemeral();
        var secret = RandomNumberGenerator.GetBytes(32);
        var peer   = new HivePeer { NodeId = identity.NodeId, Name = "TestNode" };
        var store  = HivePeerStore.CreateForTest([peer]);
        store.SetSharedSecret(identity.NodeId, secret);
        return (new HiveAuthMiddleware(store) { GracePeriodActive = false }, identity.NodeId, secret);
    }

    private static (string nodeId, string nonce, string tsStr, string sig) ExtractHeaders(
        HttpRequestMessage req)
    {
        return (
            req.Headers.GetValues("X-Hive-Node-Id").First(),
            req.Headers.GetValues("X-Hive-Nonce").First(),
            req.Headers.GetValues("X-Hive-Ts").First(),
            req.Headers.GetValues("X-Hive-Sig").First());
    }

    [Test]
    public void ValidateCore_SignedRequest_ReturnsAuthenticated()
    {
        var (auth, nodeId, secret) = MakeAuthSetup();
        var body = Encoding.UTF8.GetBytes("{\"data\":1}");
        var req  = new HttpRequestMessage(HttpMethod.Post, "http://host/hive/mesh/heartbeat");
        HiveAuthMiddleware.SignRequest(req, body, nodeId, secret);
        var (nId, nonce, ts, sig) = ExtractHeaders(req);

        var result = auth.ValidateCore(nId, nonce, ts, sig, "POST", "/hive/mesh/heartbeat", body);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.NodeId, Is.EqualTo(nodeId));
    }

    [Test]
    public void ValidateCore_TamperedBody_RejectsHmacMismatch()
    {
        var (auth, nodeId, secret) = MakeAuthSetup();
        var body     = Encoding.UTF8.GetBytes("{\"data\":1}");
        var tampered = Encoding.UTF8.GetBytes("{\"data\":2}");
        var req      = new HttpRequestMessage(HttpMethod.Post, "http://host/hive/mesh/heartbeat");
        HiveAuthMiddleware.SignRequest(req, body, nodeId, secret);
        var (nId, nonce, ts, sig) = ExtractHeaders(req);

        var result = auth.ValidateCore(nId, nonce, ts, sig, "POST", "/hive/mesh/heartbeat", tampered);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Reason, Does.Contain("HMAC"));
    }

    [Test]
    public void ValidateCore_NonceReplay_RejectsSecondUse()
    {
        var (auth, nodeId, secret) = MakeAuthSetup();
        var body = Encoding.UTF8.GetBytes("{}");
        var req  = new HttpRequestMessage(HttpMethod.Post, "http://host/hive/test");
        HiveAuthMiddleware.SignRequest(req, body, nodeId, secret);
        var (nId, nonce, ts, sig) = ExtractHeaders(req);

        var first  = auth.ValidateCore(nId, nonce, ts, sig, "POST", "/hive/test", body);
        var second = auth.ValidateCore(nId, nonce, ts, sig, "POST", "/hive/test", body);

        Assert.That(first.Ok,  Is.True);
        Assert.That(second.Ok, Is.False);
        Assert.That(second.Reason, Does.Contain("replay"));
    }

    [Test]
    public void ValidateCore_OldTimestamp_RejectsClockSkew()
    {
        var (auth, nodeId, secret) = MakeAuthSetup();
        var body   = Encoding.UTF8.GetBytes("{}");
        var oldTs  = DateTimeOffset.UtcNow.AddSeconds(-31).ToUnixTimeMilliseconds().ToString();
        var nonce  = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var sig    = SignManual(secret, "POST", "/hive/test", nonce, oldTs, body);

        var result = auth.ValidateCore(nodeId, nonce, oldTs, sig, "POST", "/hive/test", body);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Reason, Does.Contain("clock skew"));
    }

    [Test]
    public void ValidateCore_FutureTimestamp_RejectsClockSkew()
    {
        var (auth, nodeId, secret) = MakeAuthSetup();
        var body     = Encoding.UTF8.GetBytes("{}");
        var futureTs = DateTimeOffset.UtcNow.AddSeconds(31).ToUnixTimeMilliseconds().ToString();
        var nonce    = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var sig      = SignManual(secret, "POST", "/hive/test", nonce, futureTs, body);

        var result = auth.ValidateCore(nodeId, nonce, futureTs, sig, "POST", "/hive/test", body);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Reason, Does.Contain("clock skew"));
    }

    [Test]
    public void ValidateCore_MissingHeaders_GracePeriodActive_ReturnsAnonymous()
    {
        var auth = new HiveAuthMiddleware(HivePeerStore.CreateForTest()) { GracePeriodActive = true };

        var result = auth.ValidateCore(null, null, null, null, "GET", "/hive/status", []);

        Assert.That(result.Ok,     Is.True);
        Assert.That(result.NodeId, Is.EqualTo("anonymous"));
    }

    [Test]
    public void ValidateCore_MissingHeaders_StrictMode_ReturnsFailed()
    {
        var auth = new HiveAuthMiddleware(HivePeerStore.CreateForTest()) { GracePeriodActive = false };

        var result = auth.ValidateCore(null, null, null, null, "GET", "/hive/status", []);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Reason, Does.Contain("missing"));
    }

    [Test]
    public void ValidateCore_UnknownNode_StrictMode_ReturnsFailed()
    {
        var auth  = new HiveAuthMiddleware(HivePeerStore.CreateForTest()) { GracePeriodActive = false };
        var nonce = RandomNumberGenerator.GetHexString(32, lowercase: true);
        var ts    = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        var result = auth.ValidateCore("unknownnodeid", nonce, ts, "fakesig", "POST", "/hive/test", []);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Reason, Does.Contain("unknown"));
    }

    // ── HiveIdentity: sign / verify ────────────────────────────────────────────

    [Test]
    public void HiveIdentity_SignVerify_RoundTrip()
    {
        using var id = HiveIdentity.CreateEphemeral();
        var data = Encoding.UTF8.GetBytes("hello hive");
        var sig  = id.Sign(data);

        Assert.That(HiveIdentity.Verify(id.SigningPublicKeyDer, data, sig), Is.True);
    }

    [Test]
    public void HiveIdentity_Verify_TamperedData_ReturnsFalse()
    {
        using var id = HiveIdentity.CreateEphemeral();
        var data     = Encoding.UTF8.GetBytes("hello hive");
        var tampered = Encoding.UTF8.GetBytes("hello hive!");
        var sig      = id.Sign(data);

        Assert.That(HiveIdentity.Verify(id.SigningPublicKeyDer, tampered, sig), Is.False);
    }

    [Test]
    public void HiveIdentity_Verify_TamperedSig_ReturnsFalse()
    {
        using var id = HiveIdentity.CreateEphemeral();
        var data = Encoding.UTF8.GetBytes("hello hive");
        var sig  = id.Sign(data);
        sig[0] ^= 0xFF;

        Assert.That(HiveIdentity.Verify(id.SigningPublicKeyDer, data, sig), Is.False);
    }

    [Test]
    public void HiveIdentity_Verify_WrongKey_ReturnsFalse()
    {
        using var id1 = HiveIdentity.CreateEphemeral();
        using var id2 = HiveIdentity.CreateEphemeral();
        var data = Encoding.UTF8.GetBytes("hello hive");
        var sig  = id1.Sign(data);

        Assert.That(HiveIdentity.Verify(id2.SigningPublicKeyDer, data, sig), Is.False);
    }

    // ── HiveIdentity: NodeId is SHA-256 of signing key DER ───────────────────

    [Test]
    public void HiveIdentity_NodeId_IsHexSha256OfSigningKey()
    {
        using var id = HiveIdentity.CreateEphemeral();
        var expected = Convert.ToHexString(SHA256.HashData(id.SigningPublicKeyDer)).ToLowerInvariant();
        Assert.That(id.NodeId, Is.EqualTo(expected));
    }

    // ── HiveIdentity: fingerprint ─────────────────────────────────────────────

    [Test]
    public void DeriveFingerprint_SameHash_SamePhrase()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("stability-test"));
        Assert.That(HiveIdentity.DeriveFingerprint(hash), Is.EqualTo(HiveIdentity.DeriveFingerprint(hash)));
    }

    [Test]
    public void DeriveFingerprint_Format_EightWordsDashDelimited()
    {
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes("format-test"));
        var parts = HiveIdentity.DeriveFingerprint(hash).Split('-');
        Assert.Multiple(() =>
        {
            Assert.That(parts,  Has.Length.EqualTo(8));
            Assert.That(parts,  Has.All.Matches<string>(w => w.Length > 0));
        });
    }

    // ── HiveIdentity: ECDH shared-secret symmetry ─────────────────────────────

    [Test]
    public void DeriveSharedSecret_BothDirections_ProduceSameBytes()
    {
        using var alice = HiveIdentity.CreateEphemeral();
        using var bob   = HiveIdentity.CreateEphemeral();
        // XOR of NodeId bytes — commutative, so both sides compute the same salt
        var salt = XorNodeIdBytes(alice.NodeId, bob.NodeId);

        var secretA = alice.DeriveSharedSecret(bob.ExchangePublicKeyDer, salt);
        var secretB = bob.DeriveSharedSecret(alice.ExchangePublicKeyDer, salt);

        Assert.That(secretA, Is.EqualTo(secretB));
    }

    private static byte[] XorNodeIdBytes(string a, string b)
    {
        var ba = Convert.FromHexString(a);
        var bb = Convert.FromHexString(b);
        var r  = new byte[ba.Length];
        for (int i = 0; i < ba.Length; i++) r[i] = (byte)(ba[i] ^ bb[i]);
        return r;
    }

    // ── HiveElectionService: state machine ────────────────────────────────────

    private static (HiveIdentity identity, HivePeerStore peers, HiveElectionService svc)
        MakeElectionSetup(string? warchiefId = null)
    {
        var identity = HiveIdentity.CreateEphemeral();
        var selfPeer = new HivePeer { NodeId = identity.NodeId, Name = "Self",
                                      MaxRole = HiveNodeRole.Worker };
        var peers    = HivePeerStore.CreateForTest([selfPeer]);
        var svc      = new HiveElectionService(identity, peers);
        if (warchiefId is not null) svc.SetWarchief(warchiefId);
        return (identity, peers, svc);
    }

    [Test]
    public void ElectionService_SetWarchief_SetsNormalState()
    {
        var (_, _, svc) = MakeElectionSetup();
        svc.SetWarchief("warchief-node-id");

        Assert.That(svc.State,         Is.EqualTo(ElectionState.Normal));
        Assert.That(svc.WarchiefNodeId, Is.EqualTo("warchief-node-id"));
    }

    [Test]
    public void ElectionService_SuspectVote_ForNonWarchief_NoStateChange()
    {
        var (_, _, svc) = MakeElectionSetup("warchief-001");

        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = "some-peer",
            Payload = "not-the-warchief",
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        Assert.That(svc.State, Is.EqualTo(ElectionState.Normal));
    }

    [Test]
    public void ElectionService_SuspectVote_QuorumMet_SelfBecomesTemporaryWarchief()
    {
        // Only self is in the peer store; warchief is an external node not in store.
        // online = [self] (warchief excluded) → quorum = 1; one vote suffices.
        var (identity, _, svc) = MakeElectionSetup("external-warchief-id");

        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = identity.NodeId,
            Payload = "external-warchief-id",
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        Assert.That(svc.State,             Is.EqualTo(ElectionState.TemporaryWarchief));
        Assert.That(svc.WarchiefNodeId,    Is.EqualTo(identity.NodeId));
        Assert.That(svc.IsTemporaryWarchief, Is.True);
    }

    [Test]
    public void ElectionService_HeartbeatFromWarchief_DuringSuspect_CancelsElection()
    {
        // Use two online peers so quorum = 2; one vote leaves us in SuspectDeclared.
        var identity = HiveIdentity.CreateEphemeral();
        const string warchiefId = "warchief-node-id";

        var selfPeer  = new HivePeer { NodeId = identity.NodeId, Name = "Self",
                                       MaxRole = HiveNodeRole.Worker };
        var peer2     = new HivePeer { NodeId = "peer-002", Name = "Peer2",
                                       MaxRole = HiveNodeRole.Worker,
                                       LastHeartbeat = DateTime.UtcNow };
        var peers = HivePeerStore.CreateForTest([selfPeer, peer2]);
        var svc   = new HiveElectionService(identity, peers);
        svc.SetWarchief(warchiefId);

        // One vote → not quorum (quorum = 2)
        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = identity.NodeId,
            Payload = warchiefId,
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        Assert.That(svc.State, Is.EqualTo(ElectionState.SuspectDeclared));

        // Warchief recovers — cancel the suspect phase
        svc.OnPeerHeartbeatReceived(new HiveMeshHeartbeatPayload { NodeId = warchiefId });

        Assert.That(svc.State,          Is.EqualTo(ElectionState.Normal));
        Assert.That(svc.WarchiefNodeId, Is.EqualTo(warchiefId));
    }

    [Test]
    public void ElectionService_ClaimFromCorrectWinner_Accepted()
    {
        // winnerPeer has NodeId "000...0" — always sorts before any real SHA-256 hash.
        // This guarantees winnerPeer wins deterministically without relying on random key order.
        const string winnerNodeId = "0000000000000000000000000000000000000000000000000000000000000000";
        const string warchiefId   = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        var identity = HiveIdentity.CreateEphemeral();
        var selfPeer = new HivePeer { NodeId = identity.NodeId, Name = "Self",
                                      MaxRole = HiveNodeRole.Worker };
        var winnerPeer = new HivePeer { NodeId = winnerNodeId, Name = "Winner",
                                        MaxRole   = HiveNodeRole.Worker,
                                        LastHeartbeat = DateTime.UtcNow };
        var peers = HivePeerStore.CreateForTest([selfPeer, winnerPeer]);
        var svc   = new HiveElectionService(identity, peers);
        svc.SetWarchief(warchiefId);

        // Two votes → quorum (online = [self, winnerPeer], quorum = 2)
        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = identity.NodeId,
            Payload = warchiefId,
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = winnerNodeId,
            Payload = warchiefId,
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        // Self doesn't win (winnerNodeId sorts before identity.NodeId) → ElectionUnderway

        svc.OnElectionClaimReceived(new ElectionMessage
        {
            NodeId  = winnerNodeId,
            Payload = "claim",
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        Assert.That(svc.State,          Is.EqualTo(ElectionState.Normal));
        Assert.That(svc.WarchiefNodeId, Is.EqualTo(winnerNodeId));
    }

    [Test]
    public void ElectionService_ClaimFromNonWinner_Rejected()
    {
        // loserNodeId sorts AFTER identity.NodeId, so self is the expected winner.
        // Any claim from loserNodeId must be rejected.
        const string loserNodeId  = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        const string warchiefId   = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        var identity = HiveIdentity.CreateEphemeral();
        var selfPeer = new HivePeer { NodeId = identity.NodeId, Name = "Self",
                                      MaxRole = HiveNodeRole.Worker };
        var loserPeer = new HivePeer { NodeId = loserNodeId, Name = "Loser",
                                       MaxRole = HiveNodeRole.Worker,
                                       LastHeartbeat = DateTime.UtcNow };
        var peers = HivePeerStore.CreateForTest([selfPeer, loserPeer]);
        var svc   = new HiveElectionService(identity, peers);
        svc.SetWarchief(warchiefId);

        // Two votes → quorum; self should win (identity.NodeId < loserNodeId = "fff...")
        // (identity.NodeId is hex SHA-256, which starts with 0-9 or a-f; "fff..." is the max)
        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = identity.NodeId,
            Payload = warchiefId,
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        // Self wins → TemporaryWarchief; loser's claim must be rejected
        // (If self already won, OnElectionClaimReceived state guard rejects; but let's
        // also cover the case where we're in SuspectDeclared — force it with only 1 vote.)
        // We need 2 peers online for quorum = 2 so 1 vote doesn't elect self first.

        // Actually with online=[self, loserPeer], quorum=2, one vote leaves SuspectDeclared.
        // Add the second vote from loser now:
        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = loserNodeId,
            Payload = warchiefId,
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        // Quorum met; self wins → TemporaryWarchief. Loser's claim should be rejected.

        var warchiefBefore = svc.WarchiefNodeId;

        svc.OnElectionClaimReceived(new ElectionMessage
        {
            NodeId  = loserNodeId,
            Payload = "claim",
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        // Warchief must not have changed to loserNodeId
        Assert.That(svc.WarchiefNodeId, Is.Not.EqualTo(loserNodeId));
    }

    [Test]
    public void ElectionService_OnStateChanged_FiresOnTransition()
    {
        var (identity, _, svc) = MakeElectionSetup("external-warchief-id");

        ElectionState? capturedState = null;
        svc.OnStateChanged += (state, _) => capturedState = state;

        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = identity.NodeId,
            Payload = "external-warchief-id",
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

        Assert.That(capturedState, Is.EqualTo(ElectionState.TemporaryWarchief));
    }

    // ── Helper: compute HMAC-SHA256 with the same canonical form as SignRequest ─

    private static string SignManual(byte[] secret, string method, string path,
                                     string nonce, string tsMs, byte[] body)
    {
        var bodyHash  = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = $"{method.ToUpperInvariant()}\n{path}\n{nonce}\n{tsMs}\n{bodyHash}";
        using var hmac = new HMACSHA256(secret);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
