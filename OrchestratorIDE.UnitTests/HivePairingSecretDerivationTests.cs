// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// The pairing ceremony's two halves must end holding byte-identical shared secrets, or every
/// signed request afterwards fails HMAC validation.
///
/// Written because the HV-3 campaign stalled on exactly that failure: both fleet machines
/// completed a ceremony each side reported as successful (Warchief stored its peer at
/// 06:25:24, the worker stored its peer at 06:25:25), both peer stores held the other side with a
/// secret, clocks were within 2s, and yet every worker request came back
/// "HTTP 401 - HMAC mismatch" so no job could ever be leased
/// (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md, 2026-07-27).
///
/// The two derivation call sites are asymmetric in shape, which is what makes this worth pinning
/// down rather than assuming ECDH "just works" both ways:
///   responder — HiveNodeServer.ApprovePairing: XorNodeIds(self, initiator),
///               DeriveSharedSecret(initiator's exchange public key)
///   initiator — HivePairingClient.CompletePairing: XorNodeIds(self, responder),
///               DeriveSharedSecret(responder's exchange public key)
/// The node-id arguments are in OPPOSITE order on the two sides, so the salt is only equal if
/// XorNodeIds is genuinely commutative for the inputs it actually receives -- including the
/// padding/truncation branch it applies to ids that are not exactly 64 hex chars.
///
/// These tests use CreateEphemeral identities, so they exercise the real key types and the real
/// derivation without touching hive-identity.json.
/// </summary>
[TestFixture]
public sealed class HivePairingSecretDerivationTests
{
    [Test]
    public void DeriveSharedSecret_BothSidesOfPairing_ProduceIdenticalBytes()
    {
        var responder = HiveIdentity.CreateEphemeral();
        var initiator = HiveIdentity.CreateEphemeral();

        // Exactly the two call sites, in the argument order each one really uses.
        var responderSalt = HiveNodeServer.XorNodeIds(responder.NodeId, initiator.NodeId);
        var responderSecret = responder.DeriveSharedSecret(initiator.ExchangePublicKeyDer, responderSalt);

        var initiatorSalt = HiveNodeServer.XorNodeIds(initiator.NodeId, responder.NodeId);
        var initiatorSecret = initiator.DeriveSharedSecret(responder.ExchangePublicKeyDer, initiatorSalt);

        Assert.Multiple(() =>
        {
            Assert.That(initiatorSalt, Is.EqualTo(responderSalt),
                "salts must match despite the two sides passing the node ids in opposite order");
            Assert.That(initiatorSecret, Is.EqualTo(responderSecret),
                "both halves of a successful pairing must hold the same secret, or every signed " +
                "request afterwards fails with 'HMAC mismatch'");
            Assert.That(responderSecret, Has.Length.EqualTo(32));
        });
    }

    [Test]
    public void XorNodeIds_IsCommutative_ForRealNodeIdLengths()
    {
        var a = HiveIdentity.CreateEphemeral().NodeId;
        var b = HiveIdentity.CreateEphemeral().NodeId;

        Assert.That(HiveNodeServer.XorNodeIds(a, b), Is.EqualTo(HiveNodeServer.XorNodeIds(b, a)));
    }

    [Test]
    public void XorNodeIds_IsCommutative_WhenAnIdIsShorterThan64Chars()
    {
        // The padding branch is the one that could plausibly break commutativity: a short id gets
        // PadRight'd while a long one gets truncated, so an asymmetry here would produce
        // different salts on the two sides and therefore different secrets -- with pairing still
        // reporting success on both machines. Hex-valid inputs only; the method assumes that.
        const string shortId = "abcdef";
        const string fullId  = "3d157a4dc2fb7a1d956ab73891f9282b76b70b380388b44430b0a6dc61546642";

        Assert.That(HiveNodeServer.XorNodeIds(shortId, fullId),
                    Is.EqualTo(HiveNodeServer.XorNodeIds(fullId, shortId)));
    }

    [Test]
    public void DeriveSharedSecret_DiffersForADifferentPeer()
    {
        // Guards the assertion above from being vacuously true: if derivation ignored its inputs
        // and returned something constant, the equality test would pass for the wrong reason.
        var responder = HiveIdentity.CreateEphemeral();
        var initiator = HiveIdentity.CreateEphemeral();
        var stranger  = HiveIdentity.CreateEphemeral();

        var salt = HiveNodeServer.XorNodeIds(responder.NodeId, initiator.NodeId);
        var real = responder.DeriveSharedSecret(initiator.ExchangePublicKeyDer, salt);
        var other = responder.DeriveSharedSecret(stranger.ExchangePublicKeyDer, salt);

        Assert.That(real, Is.Not.EqualTo(other));
    }
}
