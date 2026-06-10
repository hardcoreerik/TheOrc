using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T08 — Model Wiki / Lab: UI smoke tests.
///
/// Verifies the Model Wiki / Lab window:
///   1. Opens from Models menu.
///   2. Exposes the required AutomationId elements.
///   3. Loads and displays the model catalogue list.
///   4. Accepts text in the search box.
///   5. Opens the capability-test dialog from the header button.
///
/// Tests do NOT run live capability tests against Ollama.
/// Tests do NOT launch swarm or agent functionality.
///
/// Window-discovery note:
///   ModelWikiWindow uses Show() (non-modal) so it does NOT appear in
///   MainWindow.ModalWindows. We search the desktop by window title instead.
/// </summary>
[TestFixture]
public class T08_ModelWikiTests : RecordingTestBase
{
    // ── Setup / teardown ─────────────────────────────────────────────────────

    [SetUp]
    public void OpenModelWikiWindow()
    {
        AppFixture.MainWindow.Focus();
        Thread.Sleep(200);

        // ── Step 1: open the Models menu ─────────────────────────────────────
        // The menu bar is a descendant of the main window with ControlType.MenuBar.
        // The Models item has Header="_Models" in XAML → UIA name is "Models".
        var menuBar = AppFixture.MainWindow.FindFirstDescendant(
            c => c.ByControlType(ControlType.MenuBar));

        Assert.That(menuBar, Is.Not.Null, "Could not find the menu bar in the main window.");

        var modelsItem = menuBar!.FindFirstChild(
            c => c.ByControlType(ControlType.MenuItem).And(c.ByName("Models")));

        // Fallback: search whole window tree in case the MenuBar level varies
        modelsItem ??= AppFixture.MainWindow.FindFirstDescendant(
            c => c.ByControlType(ControlType.MenuItem).And(c.ByName("Models")));

        Assert.That(modelsItem, Is.Not.Null,
            "Could not find the 'Models' menu item in the menu bar.");

        modelsItem!.AsMenuItem().Click();
        Thread.Sleep(250);

        // ── Step 2: click "Model Wiki / Lab…" ────────────────────────────────
        // The menu item is now a descendant in the popup — search the full window.
        var wikiItem = AppFixture.WaitUntilGet(
            () => AppFixture.MainWindow.FindFirstDescendant(
                c => c.ByControlType(ControlType.MenuItem)
                      .And(c.ByName("Model Wiki / Lab…"))),
            TimeSpan.FromSeconds(3));

        Assert.That(wikiItem, Is.Not.Null,
            "'Model Wiki / Lab…' menu item not found. Ensure MainWindow.xaml contains it " +
            "and the build is up to date.");

        wikiItem!.AsMenuItem().Click();

        // ── Step 3: wait for wiki window (non-modal — search desktop) ─────────
        var appeared = AppFixture.WaitUntil(
            () => AppFixture.FindWindowByTitle("Model Wiki") != null,
            TimeSpan.FromSeconds(8));

        Assert.That(appeared, Is.True,
            "Model Wiki / Lab window did not appear on the desktop within timeout.");
    }

    [TearDown]
    public void CloseModelWikiWindow()
    {
        // Close capability test dialog first — if the test failed before reaching
        // dlg?.Close(), the dialog stays open and blocks the next test's menu navigation.
        try
        {
            var dlg = AppFixture.FindWindowByTitle("Capability Test")
                   ?? AppFixture.FindWindowByTitle("Model Capability");
            dlg?.Close();
            Thread.Sleep(150);
        }
        catch { /* non-fatal */ }

        try
        {
            var win = AppFixture.FindWindowByTitle("Model Wiki");
            win?.Close();
            Thread.Sleep(200);
        }
        catch { /* non-fatal */ }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Test]
    public void ModelWiki_Window_Opens()
    {
        var win = AppFixture.FindWindowByTitle("Model Wiki");
        Assert.That(win, Is.Not.Null,
            "Model Wiki / Lab window should be visible on the desktop.");
        Assert.That(win!.IsOffscreen, Is.False,
            "Model Wiki window should not be off-screen.");
    }

    [Test]
    public void ModelWiki_RequiredAutomationIds_ArePresent()
    {
        var wikiWin = GetWikiWindow();

        string[] required =
        [
            "ModelWiki.Search",
            "ModelWiki.ModelList",
            "ModelWiki.Detail",
            "ModelWiki.RunCapabilityTest",
        ];

        foreach (var id in required)
        {
            var el = wikiWin.FindFirstDescendant(c => c.ByAutomationId(id));
            Assert.That(el, Is.Not.Null,
                $"Required AutomationId '{id}' not found in the Model Wiki window.");
        }
    }

    [Test]
    public void ModelWiki_Search_AcceptsText()
    {
        var wikiWin = GetWikiWindow();
        var search  = wikiWin.FindFirstDescendant(c => c.ByAutomationId("ModelWiki.Search"));
        Assert.That(search, Is.Not.Null, "ModelWiki.Search TextBox not found.");

        var tb = search!.AsTextBox();
        tb.Enter("gemma");
        Thread.Sleep(200);

        Assert.That(tb.Text, Does.Contain("gemma"),
            "Search box should contain the typed text.");

        tb.Text = "";
        Thread.Sleep(100);
    }

    [Test]
    public void ModelWiki_ModelList_IsPresent()
    {
        var wikiWin   = GetWikiWindow();
        var modelList = wikiWin.FindFirstDescendant(c => c.ByAutomationId("ModelWiki.ModelList"));

        Assert.That(modelList, Is.Not.Null, "ModelWiki.ModelList element not found.");
        Assert.That(modelList!.IsOffscreen, Is.False,
            "Model list should be visible on screen.");
    }

    [Test]
    public void ModelWiki_ModelList_LoadsModels()
    {
        var wikiWin = GetWikiWindow();

        // ModelWikiService.BuildAll runs in the background on Loaded.
        // Wait up to 10 seconds for at least one list item to appear.
        var loaded = AppFixture.WaitUntil(() =>
        {
            try
            {
                var list = wikiWin.FindFirstDescendant(
                    c => c.ByAutomationId("ModelWiki.ModelList"));
                return list?.FindAllChildren().Length > 0;
            }
            catch { return false; }
        }, TimeSpan.FromSeconds(10));

        Assert.That(loaded, Is.True,
            "Model list should contain at least one item from the ModelProfiles catalogue.");
    }

    [Test]
    public void ModelWiki_DetailPane_IsPresent()
    {
        var wikiWin = GetWikiWindow();
        var detail  = wikiWin.FindFirstDescendant(c => c.ByAutomationId("ModelWiki.Detail"));

        Assert.That(detail, Is.Not.Null, "ModelWiki.Detail ScrollViewer not found.");
    }

    [Test]
    public void ModelWiki_RunCapabilityTest_ButtonIsPresent()
    {
        var wikiWin = GetWikiWindow();
        var btn     = wikiWin.FindFirstDescendant(c => c.ByAutomationId("ModelWiki.RunCapabilityTest"));

        Assert.That(btn, Is.Not.Null, "ModelWiki.RunCapabilityTest button not found.");
        Assert.That(btn!.AsButton().IsEnabled, Is.True,
            "Run Capability Test button should be enabled.");
    }

    [Test]
    public void ModelWiki_RunCapabilityTest_OpensDialog()
    {
        var wikiWin = GetWikiWindow();
        var btn     = wikiWin.FindFirstDescendant(
            c => c.ByAutomationId("ModelWiki.RunCapabilityTest"));
        Assert.That(btn, Is.Not.Null);

        btn!.AsButton().Click();
        Thread.Sleep(300);

        // The capability test dialog is opened with ShowDialog() + Owner = wikiWin,
        // so it may surface as a ModalWindow of the wiki window rather than a
        // standalone desktop child. Check both.
        var appeared = AppFixture.WaitUntil(
            () => AppFixture.FindWindowByTitle("Capability Test") != null ||
                  AppFixture.FindWindowByTitle("Model Capability") != null ||
                  (AppFixture.FindWindowByTitle("Model Wiki")?.ModalWindows.Length ?? 0) > 0,
            TimeSpan.FromSeconds(5));

        Assert.That(appeared, Is.True,
            "Capability test dialog did not open after clicking Run Capability Test.");

        // Close the dialog so TearDown is clean
        var dlg = AppFixture.FindWindowByTitle("Capability Test")
               ?? AppFixture.FindWindowByTitle("Model Capability");
        if (dlg == null)
        {
            // Fallback: close via ModalWindows on the wiki window
            var modal = AppFixture.FindWindowByTitle("Model Wiki")?.ModalWindows.FirstOrDefault();
            try { modal?.Close(); } catch { /* ok */ }
        }
        else
        {
            try { dlg.Close(); } catch { /* ok */ }
        }
        Thread.Sleep(200);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static Window GetWikiWindow()
    {
        var win = AppFixture.FindWindowByTitle("Model Wiki");
        Assert.That(win, Is.Not.Null,
            "Model Wiki / Lab window not found on desktop. Did SetUp succeed?");
        return win!;
    }
}
