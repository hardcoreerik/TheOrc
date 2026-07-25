// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Daemon;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// DaemonConfig.DevAutoApproveMinutes gates a real trust-relaxation window (see
/// HiveNodeServer.EnableDevAutoApprove) -- it must never be settable to a value that leaves
/// that window open indefinitely. These tests exist so a future edit that reverts the clamp
/// back to a plain auto-property (as it originally shipped, per CodeRabbit PR #89 review)
/// fails CI instead of shipping silently.
/// </summary>
[TestFixture]
public sealed class DaemonConfigTests
{
    [Test]
    public void DevAutoApproveMinutes_NegativeValue_ClampsToZero()
    {
        var cfg = new DaemonConfig { DevAutoApproveMinutes = -5 };
        Assert.That(cfg.DevAutoApproveMinutes, Is.EqualTo(0));
    }

    [Test]
    public void DevAutoApproveMinutes_AboveMax_ClampsToMax()
    {
        var cfg = new DaemonConfig { DevAutoApproveMinutes = int.MaxValue };
        Assert.That(cfg.DevAutoApproveMinutes, Is.EqualTo(DaemonConfig.MaxDevAutoApproveMinutes));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(60)]
    [TestCase(1440)]
    public void DevAutoApproveMinutes_InRangeValue_PassesThroughUnchanged(int minutes)
    {
        var cfg = new DaemonConfig { DevAutoApproveMinutes = minutes };
        Assert.That(cfg.DevAutoApproveMinutes, Is.EqualTo(minutes));
    }

    [Test]
    public void DevAutoApproveMinutes_DefaultsToZero_Off()
    {
        var cfg = new DaemonConfig();
        Assert.That(cfg.DevAutoApproveMinutes, Is.EqualTo(0));
    }
}
