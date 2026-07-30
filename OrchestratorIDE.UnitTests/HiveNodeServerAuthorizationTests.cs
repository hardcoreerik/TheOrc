// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// HiveNodeServer.IsWarchief is the shared authority gate for the Warchief-only mutation
/// endpoints (remote deploy, /hive/roles/degrade, /hive/tasks/cancel — HV-3 item 3 / HV-4
/// item 1). It reads ElectionService.WarchiefNodeId. The "configured and matches" branch needs
/// a real HiveElectionService, which needs a real HiveIdentity + HivePeerStore — this was
/// previously assumed too heavy to construct for a cheap authorization check. It isn't:
/// HiveIdentity.CreateEphemeral() and HivePeerStore.CreateForTest() (both internal, both
/// already used elsewhere for exactly this purpose) construct both with zero disk I/O, safe to
/// call from any test. Verified 2026-07-30 while auditing this exact "blocked" claim for
/// staleness (the pattern that closed several other stale claims in
/// NATIVE_RUNTIME_V2_SPEC.md/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md this session).
/// </summary>
[TestFixture]
public sealed class HiveNodeServerAuthorizationTests
{
    [TestCase("")]
    [TestCase("some-node-id")]
    public void IsWarchief_ReturnsFalse_WhenElectionServiceNotConfigured(string nodeId)
    {
        using var server = new HiveNodeServer();
        Assert.That(server.ElectionService, Is.Null, "test assumes the default-constructed state");
        Assert.That(server.IsWarchief(nodeId), Is.False);
    }

    [Test]
    public void IsWarchief_ReturnsTrue_WhenNodeIdMatchesElectionServicesWarchief()
    {
        using var identity = HiveIdentity.CreateEphemeral();
        var peers = HivePeerStore.CreateForTest();
        using var server = new HiveNodeServer
        {
            ElectionService = new HiveElectionService(identity, peers),
        };

        const string warchiefNodeId = "warchief-node-id";
        server.ElectionService.SetWarchief(warchiefNodeId);

        Assert.That(server.IsWarchief(warchiefNodeId), Is.True);
    }

    [Test]
    public void IsWarchief_ReturnsFalse_ForADifferentNodeId_EvenWithElectionServiceConfigured()
    {
        // Peer-local and non-transitive (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md:90-99):
        // having a live ElectionService at all must not be mistaken for "any caller passes" --
        // only the specific nodeId ElectionService currently recognizes as Warchief should.
        using var identity = HiveIdentity.CreateEphemeral();
        var peers = HivePeerStore.CreateForTest();
        using var server = new HiveNodeServer
        {
            ElectionService = new HiveElectionService(identity, peers),
        };

        server.ElectionService.SetWarchief("the-real-warchief");

        Assert.That(server.IsWarchief("some-other-node"), Is.False);
    }
}
