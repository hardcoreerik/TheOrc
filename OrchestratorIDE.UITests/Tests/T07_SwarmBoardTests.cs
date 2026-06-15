// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T07 — Swarm Board: idle-state smoke tests.
///
/// These tests verify the SwarmBoardPanel loads correctly and exposes its idle-state
/// controls via AutomationIds. They do NOT start a swarm run (that requires a live
/// Ollama server), so all assertions are against PnlIdle elements only.
///
/// Active-state controls (NodeTester, TabTester, LogTester, etc.) live inside
/// PnlActive which is Visibility.Collapsed at idle — they are deliberately absent from
/// the UIA tree until a run starts. Verify those via integration tests with a live model.
/// </summary>
[TestFixture]
public class T07_SwarmBoardTests : RecordingTestBase
{
    [SetUp]
    public void NavigateToSwarmBoard()
    {
        // Click the Swarm mode button in the activity bar / toolbar
        AppFixture.RequireById("Mode.Swarm").AsButton().Click();

        // Wait for the goal input to appear — that proves PnlIdle is live
        var appeared = AppFixture.WaitUntil(
            () => AppFixture.FindById("Swarm.GoalInput") != null,
            TimeSpan.FromSeconds(8));

        Assert.That(appeared, Is.True,
            "SwarmBoard idle panel did not appear within timeout after clicking Mode.Swarm.");
    }

    [Test]
    public void SwarmBoard_Panel_IsPresent()
    {
        var panel = AppFixture.FindById("Panel.SwarmBoard");
        Assert.That(panel, Is.Not.Null, "'Panel.SwarmBoard' root UserControl not found.");
        Assert.That(panel!.IsOffscreen, Is.False, "SwarmBoard panel should be on-screen after navigation.");
    }

    [Test]
    public void SwarmBoard_IdleControls_ArePresent()
    {
        // Goal input + launch
        string[] required =
        [
            "Swarm.GoalInput",
            "Swarm.Launch",
        ];

        foreach (var id in required)
        {
            var el = AppFixture.FindById(id);
            Assert.That(el, Is.Not.Null, $"Idle-state control '{id}' not found in UIA tree.");
        }
    }

    [Test]
    public void SwarmBoard_ConfigControls_ArePresent()
    {
        // Model selectors + auto-config toggle — all live in PnlIdle
        string[] configIds =
        [
            "Swarm.BossModel",
            "Swarm.WorkerModel",
            "Swarm.ResearcherModel",
            "Swarm.AutoConfig",
        ];

        foreach (var id in configIds)
        {
            var el = AppFixture.FindById(id);
            Assert.That(el, Is.Not.Null, $"Config control '{id}' not found in UIA tree.");
        }
    }

    [Test]
    public void SwarmBoard_RecentGoalSlots_ArePresent()
    {
        // Four recent-goal slots in PnlIdle
        string[] slotIds = ["Swarm.Slot1", "Swarm.Slot2", "Swarm.Slot3", "Swarm.Slot4"];

        foreach (var id in slotIds)
        {
            var el = AppFixture.FindById(id);
            Assert.That(el, Is.Not.Null, $"Recent goal slot '{id}' not found in UIA tree.");
        }
    }

    [Test]
    public void SwarmBoard_GoalInput_AcceptsText()
    {
        var tb = AppFixture.RequireById("Swarm.GoalInput").AsTextBox();
        tb.Enter("Smoke test goal");
        Thread.Sleep(100);

        Assert.That(tb.Text, Is.EqualTo("Smoke test goal"),
            "Goal input should reflect typed text.");

        // Clean up so we don't pollute later tests
        tb.Text = "";
        Thread.Sleep(50);
    }

    [Test]
    public void SwarmBoard_LaunchButton_IsPresent()
    {
        // The Launch button's IsEnabled state is gate-controlled:
        // it requires both a workspace folder AND OLLAMA_NUM_PARALLEL ≥ 3.
        // In a bare test environment neither may be set, so we only verify
        // the button exists and is reachable in the UIA tree.
        var btn = AppFixture.RequireById("Swarm.Launch").AsButton();
        Assert.That(btn, Is.Not.Null, "Launch button should be present in idle state.");
        Assert.That(btn.IsOffscreen, Is.False, "Launch button should be on-screen.");
    }

    /// <summary>
    /// Documents that TESTER active-state elements are Visibility.Collapsed at idle
    /// and therefore intentionally absent from the UIA tree.
    /// This is correct WPF behavior: Collapsed elements are not automation-visible.
    /// Verify Swarm.NodeTester, Swarm.TabTester, Swarm.LogTester etc. via a live swarm run.
    /// </summary>
    [Test]
    public void SwarmBoard_TesterActiveElements_NotInUiaTreeAtIdle()
    {
        // These AutomationIds are in PnlActive (Visibility.Collapsed at idle).
        // Asserting they are NULL proves the panel correctly hides them — not a bug.
        string[] activePanelIds =
        [
            "Swarm.NodeTester",
            "Swarm.TabTester",
            "Swarm.LogTester",
        ];

        foreach (var id in activePanelIds)
        {
            var el = AppFixture.FindById(id);
            Assert.That(el, Is.Null,
                $"'{id}' should not be in the UIA tree when PnlActive is Collapsed (idle state). " +
                $"If this fails, PnlActive is unexpectedly visible.");
        }
    }
}
