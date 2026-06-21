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
    [OneTimeSetUp]
    public void InitSecrets()
        => SecretProtection.Initialize(new DpapiSecretProtector());

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

    // ── HiveIdentity: HiveId / SetHive (HIVE_MEMBERSHIP_SPEC.md §4) ────────────

    [Test]
    public void SetHive_FromUnset_SetsHiveIdAndRole()
    {
        using var id = HiveIdentity.CreateEphemeral();
        Assert.That(id.HiveId, Is.Empty);
        Assert.That(id.HiveRole, Is.EqualTo(HiveRole.Unset));

        id.SetHive("hive-123", HiveRole.Founder);

        Assert.Multiple(() =>
        {
            Assert.That(id.HiveId, Is.EqualTo("hive-123"));
            Assert.That(id.HiveRole, Is.EqualTo(HiveRole.Founder));
        });
    }

    [Test]
    public void SetHive_SameValueAgain_DoesNotThrow()
    {
        using var id = HiveIdentity.CreateEphemeral();
        id.SetHive("hive-123", HiveRole.Founder);
        Assert.DoesNotThrow(() => id.SetHive("hive-123", HiveRole.Member));
    }

    [Test]
    public void SetHive_DifferingValue_ThrowsRatherThanBridgeTwoHives()
    {
        using var id = HiveIdentity.CreateEphemeral();
        id.SetHive("hive-123", HiveRole.Founder);
        Assert.Throws<InvalidOperationException>(() => id.SetHive("hive-456", HiveRole.Member));
        // The refused call must not have mutated state.
        Assert.That(id.HiveId, Is.EqualTo("hive-123"));
    }

    [Test]
    public void SetHive_EmptyString_Throws()
    {
        using var id = HiveIdentity.CreateEphemeral();
        Assert.Throws<ArgumentException>(() => id.SetHive("", HiveRole.Founder));
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
        var selfPeer = SignedPeer(identity);   // carries signing key so VerifyElectionSig can check it
        var peers    = HivePeerStore.CreateForTest([selfPeer]);
        var svc      = new HiveElectionService(identity, peers);
        if (warchiefId is not null) svc.SetWarchief(warchiefId);
        return (identity, peers, svc);
    }

    /// <summary>Peer record carrying its real signing public key, so HiveElectionService.VerifyElectionSig
    /// can validate messages it sends. Election handlers now reject any message whose ECDSA Sig
    /// doesn't verify against the sender's stored key.</summary>
    private static HivePeer SignedPeer(HiveIdentity id, HiveNodeRole maxRole = HiveNodeRole.Worker,
                                       bool online = false)
        => new HivePeer
        {
            NodeId              = id.NodeId,
            Name                = "n",
            MaxRole             = maxRole,
            SigningPublicKeyDer = Convert.ToBase64String(id.SigningPublicKeyDer),
            LastHeartbeat       = online ? DateTime.UtcNow : (DateTime?)null,
        };

    /// <summary>Builds an election message signed by <paramref name="sender"/> over NodeId+Payload —
    /// the exact canonical form HiveElectionService.SignElectionPayload produces.</summary>
    private static ElectionMessage SignedElectionMsg(HiveIdentity sender, string payload)
        => new ElectionMessage
        {
            NodeId  = sender.NodeId,
            Payload = payload,
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sig     = Convert.ToBase64String(sender.Sign(Encoding.UTF8.GetBytes(sender.NodeId + payload))),
        };

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
        var (identity, _, svc) = MakeElectionSetup("warchief-001");

        // Validly-signed vote, but its payload names a node that is NOT the warchief →
        // handler must ignore it. (A bad-sig message is rejected earlier; this exercises
        // the warchief-mismatch path specifically.)
        svc.OnSuspectVoteReceived(SignedElectionMsg(identity, "not-the-warchief"));

        Assert.That(svc.State, Is.EqualTo(ElectionState.Normal));
    }

    [Test]
    public void ElectionService_SuspectVote_QuorumMet_SelfBecomesTemporaryWarchief()
    {
        // Only self is in the peer store; warchief is an external node not in store.
        // online = [self] (warchief excluded) → quorum = 1; one vote suffices.
        var (identity, _, svc) = MakeElectionSetup("external-warchief-id");

        svc.OnSuspectVoteReceived(SignedElectionMsg(identity, "external-warchief-id"));

        Assert.That(svc.State,             Is.EqualTo(ElectionState.TemporaryWarchief));
        Assert.That(svc.WarchiefNodeId,    Is.EqualTo(identity.NodeId));
        Assert.That(svc.IsTemporaryWarchief, Is.True);
    }

    [Test]
    public void ElectionService_SuspectVote_MissingSignature_Ignored()
    {
        // Same payload that WOULD elect self, but with no signature → must be dropped.
        // Without sig verification an unsigned LAN message could drive the election.
        var (identity, _, svc) = MakeElectionSetup("external-warchief-id");

        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = identity.NodeId,
            Payload = "external-warchief-id",
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sig     = "",   // missing signature
        });

        Assert.That(svc.State, Is.EqualTo(ElectionState.Normal));
    }

    [Test]
    public void ElectionService_SuspectVote_WrongKeySignature_Ignored()
    {
        // Message claims to be from `identity` but is signed by a different key → rejected.
        // This is the forged-election-message attack the Sig verification closes.
        var (identity, _, svc) = MakeElectionSetup("external-warchief-id");
        using var attacker = HiveIdentity.CreateEphemeral();

        svc.OnSuspectVoteReceived(new ElectionMessage
        {
            NodeId  = identity.NodeId,
            Payload = "external-warchief-id",
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Sig     = Convert.ToBase64String(
                          attacker.Sign(Encoding.UTF8.GetBytes(identity.NodeId + "external-warchief-id"))),
        });

        Assert.That(svc.State, Is.EqualTo(ElectionState.Normal));
    }

    [Test]
    public void ElectionService_HeartbeatFromWarchief_DuringSuspect_CancelsElection()
    {
        // Use two online peers so quorum = 2; one vote leaves us in SuspectDeclared.
        var identity = HiveIdentity.CreateEphemeral();
        const string warchiefId = "warchief-node-id";

        var selfPeer  = SignedPeer(identity);
        var peer2     = new HivePeer { NodeId = "peer-002", Name = "Peer2",
                                       MaxRole = HiveNodeRole.Worker,
                                       LastHeartbeat = DateTime.UtcNow };
        var peers = HivePeerStore.CreateForTest([selfPeer, peer2]);
        var svc   = new HiveElectionService(identity, peers);
        svc.SetWarchief(warchiefId);

        // One vote → not quorum (quorum = 2)
        svc.OnSuspectVoteReceived(SignedElectionMsg(identity, warchiefId));
        Assert.That(svc.State, Is.EqualTo(ElectionState.SuspectDeclared));

        // Warchief recovers — cancel the suspect phase
        svc.OnPeerHeartbeatReceived(new HiveMeshHeartbeatPayload { NodeId = warchiefId });

        Assert.That(svc.State,          Is.EqualTo(ElectionState.Normal));
        Assert.That(svc.WarchiefNodeId, Is.EqualTo(warchiefId));
    }

    [Test]
    public void ElectionService_ClaimFromCorrectWinner_Accepted()
    {
        // Two real identities (election messages must be signature-verifiable). The
        // lexicographically-smaller NodeId is the deterministic winner; make the OTHER node
        // (not self) the winner so we exercise the claim-acceptance path.
        var idA = HiveIdentity.CreateEphemeral();
        var idB = HiveIdentity.CreateEphemeral();
        var (winner, self) = string.CompareOrdinal(idA.NodeId, idB.NodeId) < 0 ? (idA, idB) : (idB, idA);
        const string warchiefId = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        var peers = HivePeerStore.CreateForTest(
            [SignedPeer(self), SignedPeer(winner, online: true)]);
        var svc   = new HiveElectionService(self, peers);
        svc.SetWarchief(warchiefId);

        // Two votes → quorum (online = [self, winner], quorum = 2)
        svc.OnSuspectVoteReceived(SignedElectionMsg(self,   warchiefId));
        svc.OnSuspectVoteReceived(SignedElectionMsg(winner, warchiefId));
        // Self has the larger NodeId → does not win → ElectionUnderway, awaiting the claim

        svc.OnElectionClaimReceived(SignedElectionMsg(winner, "claim"));

        Assert.That(svc.State,          Is.EqualTo(ElectionState.Normal));
        Assert.That(svc.WarchiefNodeId, Is.EqualTo(winner.NodeId));
    }

    [Test]
    public void ElectionService_ClaimFromNonWinner_Rejected()
    {
        // Two real identities; self = the smaller NodeId (the deterministic winner),
        // loser = the larger. After quorum self wins, so the loser's later claim must be
        // rejected and must not overwrite the Warchief.
        var idA = HiveIdentity.CreateEphemeral();
        var idB = HiveIdentity.CreateEphemeral();
        var (self, loser) = string.CompareOrdinal(idA.NodeId, idB.NodeId) < 0 ? (idA, idB) : (idB, idA);
        const string warchiefId = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        var peers = HivePeerStore.CreateForTest(
            [SignedPeer(self), SignedPeer(loser, online: true)]);
        var svc   = new HiveElectionService(self, peers);
        svc.SetWarchief(warchiefId);

        // Two votes → quorum; self has the smaller NodeId → self wins → TemporaryWarchief
        svc.OnSuspectVoteReceived(SignedElectionMsg(self,  warchiefId));
        svc.OnSuspectVoteReceived(SignedElectionMsg(loser, warchiefId));

        // Loser claims — must be rejected; Warchief must not become the loser
        svc.OnElectionClaimReceived(SignedElectionMsg(loser, "claim"));

        Assert.That(svc.WarchiefNodeId, Is.Not.EqualTo(loser.NodeId));
    }

    [Test]
    public void ElectionService_OnStateChanged_FiresOnTransition()
    {
        var (identity, _, svc) = MakeElectionSetup("external-warchief-id");

        ElectionState? capturedState = null;
        svc.OnStateChanged += (state, _) => capturedState = state;

        svc.OnSuspectVoteReceived(SignedElectionMsg(identity, "external-warchief-id"));

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
