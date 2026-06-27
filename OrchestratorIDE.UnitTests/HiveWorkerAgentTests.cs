// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class HiveWorkerAgentTests
{
    [TestCase("researcher", RuntimeRole.Researcher)]
    [TestCase("Researcher", RuntimeRole.Researcher)]
    [TestCase("coder", RuntimeRole.Worker)]
    [TestCase("uideveloper", RuntimeRole.Worker)]
    [TestCase("tester", RuntimeRole.Worker)]
    [TestCase("unknown-lane", RuntimeRole.Worker)]
    [TestCase(null, RuntimeRole.Worker)]
    public void MapHiveRoleToRuntimeRole_Maps_Researcher_Only_To_Researcher(
        string? hiveRole,
        RuntimeRole expected)
    {
        Assert.That(HiveNativeRoleExecutorAdapter.MapHiveRoleToRuntimeRole(hiveRole), Is.EqualTo(expected));
    }
}
