// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T01 — Verify the application launches and the top-level shell is intact.
/// These tests must pass before any other suite is meaningful.
/// </summary>
[TestFixture]
public class T01_LaunchTests : RecordingTestBase
{
    [Test]
    public void MainWindow_IsVisible()
    {
        Assert.That(AppFixture.MainWindow.IsOffscreen, Is.False,
            "Main window should be on-screen after launch.");
    }

    [Test]
    public void MainWindow_HasCorrectTitle()
    {
        var title = AppFixture.MainWindow.Title;
        Assert.That(title, Does.Contain("Orchestrator IDE"),
            "Window title should contain 'Orchestrator IDE'.");
    }

    [Test]
    public void MainWindow_AutomationId_IsSet()
    {
        Assert.That(AppFixture.MainWindow.AutomationId, Is.EqualTo("MainWindow"),
            "Root window AutomationId must be 'MainWindow'.");
    }

    [Test]
    public void StatusBar_Workspace_IsPresent()
    {
        var el = AppFixture.RequireById("StatusBar.Workspace");
        Assert.That(el.IsOffscreen, Is.False, "Status bar workspace label should be visible.");
    }

    [Test]
    public void StatusBar_Model_IsPresent()
    {
        var el = AppFixture.RequireById("StatusBar.Model");
        Assert.That(el.IsOffscreen, Is.False, "Status bar model label should be visible.");
    }
}
