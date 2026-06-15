using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T18 — HIVE MIND panel UI smoke tests.
/// Verifies the constellation canvas, action buttons, and event log
/// are all present and interactive after navigating to HIVE mode.
/// These tests do NOT require live Ollama nodes or a running HIVE network.
/// </summary>
[TestFixture]
public class T18_HivePanelTests : RecordingTestBase
{
    // ── Navigation ─────────────────────────────────────────────────────────────

    [Test, Order(1)]
    public void ModeHive_Button_IsPresent()
    {
        var btn = AppFixture.FindById("Mode.Hive");
        Assert.That(btn, Is.Not.Null, "Mode.Hive button must exist in the mode bar.");
    }

    [Test, Order(2)]
    public void ModeHive_Click_ShowsHivePanel()
    {
        AppFixture.RequireById("Mode.Hive").AsButton().Click();

        var appeared = AppFixture.WaitUntil(
            () => AppFixture.FindById("Hive.Panel") != null,
            TimeSpan.FromSeconds(8));

        Assert.That(appeared, Is.True, "Hive.Panel should be visible after clicking Mode.Hive.");
    }

    // ── Panel structure ────────────────────────────────────────────────────────

    [Test, Order(3)]
    public void HivePanel_ConstellationArea_IsPresent()
    {
        // WPF Canvas has no UIA peer — verify via the hint TextBlock below it instead.
        NavigateToHive();
        var hint = AppFixture.FindById("Hive.HintText");
        Assert.That(hint, Is.Not.Null, "Hive.HintText must be present (confirms constellation area rendered).");
        Assert.That(hint!.IsOffscreen, Is.False);
    }

    [Test, Order(4)]
    public void HivePanel_EventLog_IsPresent()
    {
        // WPF Border has no UIA peer — verify via the inner "⚡ EVENTS" TextBlock instead.
        NavigateToHive();
        var label = AppFixture.FindById("Hive.EventsLabel");
        Assert.That(label, Is.Not.Null, "Hive.EventsLabel must be present (confirms event log area rendered).");
    }

    // ── Action buttons ─────────────────────────────────────────────────────────

    [Test, Order(5)]
    public void HivePanel_ScanLan_ButtonIsEnabled()
    {
        NavigateToHive();
        var btn = AppFixture.RequireById("Hive.ScanLan").AsButton();
        Assert.That(btn.IsEnabled, Is.True, "Scan LAN button should be enabled.");
    }

    [Test, Order(6)]
    public void HivePanel_FindTailscale_ButtonIsEnabled()
    {
        NavigateToHive();
        var btn = AppFixture.RequireById("Hive.FindTailscale").AsButton();
        Assert.That(btn.IsEnabled, Is.True, "Find Tailscale button should be enabled.");
    }

    [Test, Order(7)]
    public void HivePanel_AddNode_ButtonIsEnabled()
    {
        NavigateToHive();
        var btn = AppFixture.RequireById("Hive.AddNode").AsButton();
        Assert.That(btn.IsEnabled, Is.True, "Add node button should be enabled.");
    }

    [Test, Order(8)]
    public void HivePanel_Rescan_ButtonIsEnabled()
    {
        NavigateToHive();
        var btn = AppFixture.RequireById("Hive.Rescan").AsButton();
        Assert.That(btn.IsEnabled, Is.True, "Rescan button should be enabled.");
    }

    // ── Interaction smoke tests ────────────────────────────────────────────────

    [Test, Order(9)]
    public void HivePanel_ScanLan_Click_DoesNotCrash()
    {
        NavigateToHive();
        // Fire the LAN scan and wait briefly — it runs async in the background.
        // We just verify the app doesn't throw or close.
        AppFixture.RequireById("Hive.ScanLan").AsButton().Click();
        Thread.Sleep(500);

        Assert.That(AppFixture.MainWindow.IsOffscreen, Is.False,
            "App should remain open after clicking Scan LAN.");
    }

    [Test, Order(10)]
    public void HivePanel_Rescan_Click_DoesNotCrash()
    {
        NavigateToHive();
        AppFixture.RequireById("Hive.Rescan").AsButton().Click();
        Thread.Sleep(500);

        Assert.That(AppFixture.MainWindow.IsOffscreen, Is.False,
            "App should remain open after clicking Rescan.");
    }

    [Test, Order(11)]
    public void HivePanel_FindTailscale_Click_DoesNotCrash()
    {
        NavigateToHive();
        AppFixture.RequireById("Hive.FindTailscale").AsButton().Click();
        Thread.Sleep(500);

        Assert.That(AppFixture.MainWindow.IsOffscreen, Is.False,
            "App should remain open after clicking Find Tailscale.");
    }

    // ── Return to normal mode (cleanup) ───────────────────────────────────────

    [Test, Order(99)]
    public void CleanUp_ReturnToAgentMode()
    {
        // Leave the app in Agent mode so subsequent suites (if run together) start clean.
        var agentBtn = AppFixture.FindById("ActivityBar.Agent");
        agentBtn?.AsButton().Click();
        AppFixture.WaitUntil(() => AppFixture.FindById("AgentPanel.Input") != null,
            TimeSpan.FromSeconds(5));
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private static void NavigateToHive()
    {
        if (AppFixture.FindById("Hive.Panel") != null) return;  // already there

        AppFixture.RequireById("Mode.Hive").AsButton().Click();
        var ok = AppFixture.WaitUntil(
            () => AppFixture.FindById("Hive.Panel") != null,
            TimeSpan.FromSeconds(8));

        if (!ok) Assert.Fail("Could not navigate to Hive.Panel — Mode.Hive click did not show the panel.");
    }
}
