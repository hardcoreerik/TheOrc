using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T02 — Activity bar buttons exist, are enabled, and swap the main content panel.
/// </summary>
[TestFixture]
public class T02_ActivityBarTests
{
    private static readonly string[] ActivityBarIds =
    [
        "ActivityBar.Explorer",
        "ActivityBar.Agent",
        "ActivityBar.Checkpoints",
        "ActivityBar.Tools",
        "ActivityBar.Settings",
    ];

    [Test]
    public void AllActivityBarButtons_ArePresent()
    {
        foreach (var id in ActivityBarIds)
        {
            var el = AppFixture.FindById(id);
            Assert.That(el, Is.Not.Null, $"Activity bar button '{id}' not found.");
        }
    }

    [Test]
    public void AllActivityBarButtons_AreEnabled()
    {
        foreach (var id in ActivityBarIds)
        {
            var btn = AppFixture.RequireById(id).AsButton();
            Assert.That(btn.IsEnabled, Is.True, $"Button '{id}' should be enabled.");
        }
    }

    [Test]
    public void BtnAgent_Click_ShowsAgentPanel()
    {
        // Click the Agent button — AgentPanel.Input should become reachable
        AppFixture.RequireById("ActivityBar.Agent").AsButton().Click();

        var appeared = AppFixture.WaitUntil(() =>
            AppFixture.FindById("AgentPanel.Input") != null,
            TimeSpan.FromSeconds(5));

        Assert.That(appeared, Is.True, "AgentPanel.Input should be visible after clicking Agent button.");
    }

    [Test]
    public void BtnTools_Click_ShowsToolEditorPanel()
    {
        AppFixture.RequireById("ActivityBar.Tools").AsButton().Click();

        // AvalonEdit enters the visual tree for the first time here; allow extra
        // time for its initial render before querying the UIA accessibility tree.
        var appeared = AppFixture.WaitUntil(() =>
            AppFixture.FindById("ToolEditor.Compile") != null,
            TimeSpan.FromSeconds(15));

        Assert.That(appeared, Is.True, "ToolEditor.Compile should be visible after clicking Tools button.");

        // Return to Agent panel so subsequent tests start in a known state
        AppFixture.RequireById("ActivityBar.Agent").AsButton().Click();
        AppFixture.WaitUntil(() => AppFixture.FindById("AgentPanel.Input") != null,
            TimeSpan.FromSeconds(5));
    }
}
