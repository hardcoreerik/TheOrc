// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T05 — Agent panel: input box, mode toggle, workspace badge, Send / Stop buttons.
/// Does NOT actually invoke the agent (that would require an Ollama server).
/// </summary>
[TestFixture]
public class T05_AgentPanelTests : RecordingTestBase
{
    [SetUp]
    public void NavigateToAgent()
    {
        AppFixture.RequireById("ActivityBar.Agent").AsButton().Click();
        AppFixture.WaitUntil(() => AppFixture.FindById("AgentPanel.Input") != null,
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void AgentPanel_CoreControls_ArePresent()
    {
        string[] ids =
        [
            "AgentPanel.Input",
            "AgentPanel.Send",
            "AgentPanel.Stop",
            "AgentPanel.ModePlan",
            "AgentPanel.ModeExecute",
            "AgentPanel.WorkspaceBadge",
            "AgentPanel.MessageList",
        ];

        foreach (var id in ids)
            Assert.That(AppFixture.FindById(id), Is.Not.Null, $"'{id}' not found in Agent panel.");
    }

    [Test]
    public void AgentPanel_InputBox_AcceptsText()
    {
        var tb = AppFixture.RequireById("AgentPanel.Input").AsTextBox();
        tb.Enter("Hello agent");
        Thread.Sleep(100);

        Assert.That(tb.Text, Is.EqualTo("Hello agent"),
            "Input box should reflect typed text.");

        // Clean up
        tb.Text = "";
    }

    [Test]
    public void AgentPanel_StopButton_StartedDisabled()
    {
        var btn = AppFixture.RequireById("AgentPanel.Stop").AsButton();
        Assert.That(btn.IsEnabled, Is.False,
            "Stop button should be disabled when no agent run is active.");
    }

    [Test]
    public void AgentPanel_SendButton_IsEnabled()
    {
        var btn = AppFixture.RequireById("AgentPanel.Send").AsButton();
        Assert.That(btn.IsEnabled, Is.True,
            "Send button should always be enabled.");
    }

    [Test]
    public void AgentPanel_ModePlan_IsDefaultChecked()
    {
        var rbPlan = AppFixture.RequireById("AgentPanel.ModePlan").AsRadioButton();
        Assert.That(rbPlan.IsChecked, Is.True,
            "Plan mode radio button should be selected by default.");
    }

    [Test]
    public void AgentPanel_ModeToggle_SwitchesModes()
    {
        var rbExec = AppFixture.RequireById("AgentPanel.ModeExecute").AsRadioButton();
        rbExec.Click();
        Thread.Sleep(150);

        Assert.That(rbExec.IsChecked, Is.True, "Execute mode should be selected after click.");

        // Reset to Plan
        var rbPlan = AppFixture.RequireById("AgentPanel.ModePlan").AsRadioButton();
        rbPlan.Click();
        Thread.Sleep(150);
        Assert.That(rbPlan.IsChecked, Is.True, "Plan mode should restore.");
    }

    [Test]
    public void AgentPanel_WorkspaceBadge_IsVisible()
    {
        var badge = AppFixture.RequireById("AgentPanel.WorkspaceBadge");
        Assert.That(badge.IsOffscreen, Is.False,
            "Workspace badge should be visible in the agent panel.");
    }

    [Test]
    public void AgentPanel_MessageList_IsPresent()
    {
        var list = AppFixture.RequireById("AgentPanel.MessageList");
        Assert.That(list, Is.Not.Null, "Message list container should exist in the panel.");
    }
}
