// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// HiveNodeServer.IsWarchief is the shared authority gate for the Warchief-only mutation
/// endpoints (remote deploy, /hive/roles/degrade, /hive/tasks/cancel — HV-3 item 3 / HV-4
/// item 1). It reads ElectionService.WarchiefNodeId, which requires a real HiveElectionService
/// (itself requiring a real HiveIdentity, private-constructor-only) to test the "configured and
/// matches" branch — too heavy for what should be a cheap authorization check. This covers the
/// safe-default branch instead: no ElectionService configured must mean nobody is authorized
/// (fail-closed), not "any caller passes."
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
}
