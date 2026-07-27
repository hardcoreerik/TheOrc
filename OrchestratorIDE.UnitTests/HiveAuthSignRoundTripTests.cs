// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Security.Cryptography;
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// A request signed by <see cref="HiveAuthMiddleware.SignRequest"/> must validate against
/// <c>ValidateCore</c> when both sides hold the same secret. If it does not, every authenticated
/// HIVE call fails with "HMAC mismatch" and no worker can ever lease a task.
///
/// Written to close out the HV-3 campaign blocker
/// (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md, 2026-07-27). The shared secret itself was
/// already cleared as a suspect — <see cref="HivePairingSecretDerivationTests"/> proves both
/// halves of the pairing ceremony derive identical bytes — so the remaining candidate was the
/// canonical string disagreeing between signer and verifier.
///
/// The two sides build it from DIFFERENT sources, which is why this is worth pinning rather than
/// assuming: the signer uses <c>HttpRequestMessage.RequestUri.AbsolutePath</c> plus the body it
/// was handed, while the verifier uses <c>HttpListenerRequest.Url.AbsolutePath</c> plus the bytes
/// it read off the wire. The failing fleet calls were <c>GET /hive/models</c> (empty body) and a
/// POST heartbeat (JSON body), so both shapes are covered.
/// </summary>
[TestFixture]
public sealed class HiveAuthSignRoundTripTests
{
    /// <summary>
    /// The peer store encrypts secrets through the process-wide protector, which production
    /// initializes at startup (GUI: DPAPI, daemon: AES-GCM) but a test host never does. Idempotent
    /// and safe to repeat — Initialize is a plain assignment — so this does not fight any other
    /// fixture that sets it. DPAPI matches what the Warchief side actually uses.
    /// </summary>
    [OneTimeSetUp]
    public void InitSecretProtection() =>
        SecretProtection.Initialize(new DpapiSecretProtector());

    private static (HiveAuthMiddleware Auth, byte[] Secret, string NodeId) BuildPeer()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var nodeId = "3d157a4dc2fb7a1d956ab73891f9282b76b70b380388b44430b0a6dc61546642";

        var peer = new HivePeer
        {
            NodeId  = nodeId,
            Name    = "HARDCOREPC",
            Role    = HiveNodeRole.Worker,
            Revoked = false,
        };

        // Store the secret through the store's own encrypt path rather than assigning
        // SharedSecretEnc directly, so the test exercises the same encrypt/decrypt round-trip the
        // fleet does — that round-trip was itself one of the suspects for the "HMAC mismatch".
        var store = HivePeerStore.CreateForTest([peer]);
        store.SetSharedSecret(nodeId, secret);
        return (new HiveAuthMiddleware(store), secret, nodeId);
    }

    /// <summary>Extracts what SignRequest actually put on the wire, so the verifier is fed the
    /// signer's real output rather than a hand-rolled approximation of it.</summary>
    private static (string Nonce, string Ts, string Sig) HeadersOf(HttpRequestMessage req) =>
        (req.Headers.GetValues("X-Hive-Nonce").Single(),
         req.Headers.GetValues("X-Hive-Ts").Single(),
         req.Headers.GetValues("X-Hive-Sig").Single());

    [Test]
    public void SignedGet_WithEmptyBody_ValidatesAgainstTheVerifier()
    {
        // Exactly the call that failed on the fleet: GET /hive/models, empty body.
        var (auth, secret, nodeId) = BuildPeer();
        var body = System.Array.Empty<byte>();

        using var req = new HttpRequestMessage(HttpMethod.Get, "http://100.112.36.18:7079/hive/models");
        HiveAuthMiddleware.SignRequest(req, body, nodeId, secret);
        var (nonce, ts, sig) = HeadersOf(req);

        var result = auth.ValidateCore(nodeId, nonce, ts, sig, "GET", "/hive/models", body);

        Assert.That(result.Ok, Is.True, $"round-trip must validate; verifier said: {result.Reason}");
    }

    [Test]
    public void SignedPost_WithJsonBody_ValidatesAgainstTheVerifier()
    {
        var (auth, secret, nodeId) = BuildPeer();
        var body = System.Text.Encoding.UTF8.GetBytes("{\"workerId\":\"HardcorePC\",\"claimToken\":\"abc\"}");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "http://100.112.36.18:7079/hive/tasks/t-1/heartbeat");
        HiveAuthMiddleware.SignRequest(req, body, nodeId, secret);
        var (nonce, ts, sig) = HeadersOf(req);

        var result = auth.ValidateCore(nodeId, nonce, ts, sig, "POST", "/hive/tasks/t-1/heartbeat", body);

        Assert.That(result.Ok, Is.True, $"round-trip must validate; verifier said: {result.Reason}");
    }

    [Test]
    public void SignedRequest_WithQueryString_ValidatesOnAbsolutePathAlone()
    {
        // AbsolutePath excludes the query on both sides, so a signed URL carrying one must still
        // validate. If it did not, any endpoint that grew a query parameter would start failing
        // as "HMAC mismatch" with nothing pointing at the query as the cause.
        var (auth, secret, nodeId) = BuildPeer();
        var body = System.Array.Empty<byte>();

        using var req = new HttpRequestMessage(HttpMethod.Get,
            "http://100.112.36.18:7079/hive/tasks/next?lanes=coder&limit=1");
        HiveAuthMiddleware.SignRequest(req, body, nodeId, secret);
        var (nonce, ts, sig) = HeadersOf(req);

        var result = auth.ValidateCore(nodeId, nonce, ts, sig, "GET", "/hive/tasks/next", body);

        Assert.That(result.Ok, Is.True, $"round-trip must validate; verifier said: {result.Reason}");
    }

    [Test]
    public void SignedRequest_ValidatedWithADifferentSecret_IsRejectedAsHmacMismatch()
    {
        // Negative control: proves the passing cases above are not passing because validation is
        // lenient, and pins the exact reason string the fleet reported so a future refactor that
        // changes it does not silently invalidate the diagnosis recorded in the campaign log.
        var (_, secret, nodeId) = BuildPeer();

        var otherPeer = new HivePeer
        {
            NodeId = nodeId, Name = "HARDCOREPC", Role = HiveNodeRole.Worker, Revoked = false,
        };
        var otherStore = HivePeerStore.CreateForTest([otherPeer]);
        otherStore.SetSharedSecret(nodeId, RandomNumberGenerator.GetBytes(32));
        var verifier = new HiveAuthMiddleware(otherStore);

        var body = System.Array.Empty<byte>();
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://x/hive/models");
        HiveAuthMiddleware.SignRequest(req, body, nodeId, secret);
        var (nonce, ts, sig) = HeadersOf(req);

        var result = verifier.ValidateCore(nodeId, nonce, ts, sig, "GET", "/hive/models", body);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Reason, Is.EqualTo("HMAC mismatch"));
        });
    }
}
