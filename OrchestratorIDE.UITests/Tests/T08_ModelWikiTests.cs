using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T08 — Model Wiki / Lab: UI smoke tests.
///
/// Verifies the Model Wiki / Lab window:
///   1. Opens from the Models menu.
///   2. Exposes the required AutomationId elements.
///   3. Loads and displays the model catalogue list.
///   4. Accepts text in the search box.
///   5. Opens the capability-test dialog from the header button.
///
/// Tests do NOT run live capability tests against Ollama.
/// Tests do NOT launch swarm or agent functionality.
///
/// Window-discovery strategy:
///   ModelWikiWindow is non-modal (Show(), not ShowDialog()), single-instance,
///   and carries AutomationId="ModelWiki.Root".  We prefer FindWindowByAutomationId
///   over title matching, and scope all searches to the app process (ByProcessId)
///   so foreign windows with similar titles are never mistakenly matched.
///
/// Menu navigation strategy:
///   Menu items now carry stable AutomationIds (Menu.Models, Menu.ModelWiki).
///   We click by AutomationId rather than loose name matching, which avoids
///   fragility caused by WPF stripping the _ access-key prefix from Header text.
/// </summary>
[TestFixture]
public class T08_ModelWikiTests : RecordingTestBase
{
    // ── Setup / teardown ─────────────────────────────────────────────────────

    [SetUp]
    public void OpenModelWikiWindow()
    {
        // Ensure no stale wiki window from a previous test cycle is still open.
        CloseAllWikiWindows();

        try { AppFixture.MainWindow.Focus(); } catch { /* UIA transient event dispatch error — safe to ignore */ }
        Thread.Sleep(300);

        // ── Step 1: click the Models top-level menu item by AutomationId ────────
        var modelsItem = AppFixture.WaitUntilGet(
            () => AppFixture.MainWindow.FindFirstDescendant(
                      c => c.ByAutomationId("Menu.Models")),
            TimeSpan.FromSeconds(5));

        if (modelsItem == null)
        {
            // Fallback: name-based search (covers builds before AutomationId was added)
            modelsItem = AppFixture.MainWindow.FindFirstDescendant(
                c => c.ByControlType(ControlType.MenuItem).And(c.ByName("Models")));
        }

        Assert.That(modelsItem, Is.Not.Null,
            "Could not find the 'Models' menu item (AutomationId=Menu.Models or Name=Models). " +
            "Verify the XAML has AutomationProperties.AutomationId=\"Menu.Models\" on the MenuItem.");

        modelsItem!.AsMenuItem().Click();
        Thread.Sleep(250);   // let the popup render

        // ── Step 2: click "Model Wiki / Lab…" by AutomationId ───────────────────
        var wikiItem = AppFixture.WaitUntilGet(
            () => AppFixture.MainWindow.FindFirstDescendant(
                      c => c.ByAutomationId("Menu.ModelWiki")),
            TimeSpan.FromSeconds(3));

        if (wikiItem == null)
        {
            // Fallback: name-based search
            wikiItem = AppFixture.MainWindow.FindFirstDescendant(
                c => c.ByControlType(ControlType.MenuItem)
                      .And(c.ByName("Model Wiki / Lab…")));
        }

        Assert.That(wikiItem, Is.Not.Null,
            "'Model Wiki / Lab…' menu item not found (AutomationId=Menu.ModelWiki). " +
            "Ensure the menu item has AutomationProperties.AutomationId=\"Menu.ModelWiki\" in MainWindow.xaml.");

        wikiItem!.AsMenuItem().Click();

        // ── Step 3: wait for the wiki window ────────────────────────────────────
        // Primary: search by AutomationId="ModelWiki.Root" (most reliable).
        // Fallback: title fragment "Model Wiki".
        // Both searches are scoped to the app process via ByProcessId.
        var appeared = AppFixture.WaitUntil(
            () => AppFixture.FindWindowByAutomationId("ModelWiki.Root") != null
               || AppFixture.FindWindowByTitle("Model Wiki") != null,
            TimeSpan.FromSeconds(10));

        if (!appeared)
        {
            // Emit diagnostics before failing so we can see what windows ARE open.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Model Wiki window did not appear within 10 s.");
            sb.AppendLine($"App PID: {AppFixture.AppProcessId}");
            sb.AppendLine("Windows currently in app process:");
            foreach (var (name, aid) in AppFixture.EnumerateAppWindows())
                sb.AppendLine($"  Name='{name}'  AutomationId='{aid}'");

            Assert.Fail(sb.ToString());
        }
    }

    [TearDown]
    public void CloseModelWikiWindow()
    {
        // Close capability test dialog first — if a test failed before reaching
        // its own cleanup, the dialog stays open and blocks the next test's menu
        // navigation.
        try
        {
            var dlg = AppFixture.FindWindowByAutomationId("ModelCapTest.Root")
                   ?? AppFixture.FindWindowByTitle("Capability Test")
                   ?? AppFixture.FindWindowByTitle("Model Capability");
            dlg?.Close();
            Thread.Sleep(150);
        }
        catch { /* non-fatal */ }

        CloseAllWikiWindows();
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Test]
    public void ModelWiki_Window_Opens()
    {
        var win = GetWikiWindow();
        Assert.That(win.IsOffscreen, Is.False,
            "Model Wiki window should be visible on screen.");
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
        // Wait up to 10 s for at least one list item to appear.
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

        // The capability test dialog is opened with ShowDialog() + Owner = wikiWin.
        // Search by AutomationId first (most reliable), then by title fragment,
        // then via wikiWin.ModalWindows — all scoped to the app process.
        var appeared = AppFixture.WaitUntil(
            () => AppFixture.FindWindowByAutomationId("ModelCapTest.Root") != null
               || AppFixture.FindWindowByTitle("Capability Test") != null
               || AppFixture.FindWindowByTitle("Model Capability") != null
               || (AppFixture.FindWindowByAutomationId("ModelWiki.Root")
                      ?.ModalWindows.Length ?? 0) > 0,
            TimeSpan.FromSeconds(5));

        if (!appeared)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Capability test dialog did not open after clicking Run Capability Test.");
            sb.AppendLine("Windows currently in app process:");
            foreach (var (name, aid) in AppFixture.EnumerateAppWindows())
                sb.AppendLine($"  Name='{name}'  AutomationId='{aid}'");
            Assert.Fail(sb.ToString());
        }

        // Close the dialog so TearDown is clean
        var dlg = AppFixture.FindWindowByAutomationId("ModelCapTest.Root")
               ?? AppFixture.FindWindowByTitle("Capability Test")
               ?? AppFixture.FindWindowByTitle("Model Capability")
               ?? AppFixture.FindWindowByAutomationId("ModelWiki.Root")
                      ?.ModalWindows.FirstOrDefault();
        try { dlg?.Close(); } catch { /* ok */ }
        Thread.Sleep(200);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Return the wiki window, preferring AutomationId lookup.
    /// Emits a clear failure message if not found.
    /// </summary>
    private static Window GetWikiWindow()
    {
        var win = AppFixture.FindWindowByAutomationId("ModelWiki.Root")
               ?? AppFixture.FindWindowByTitle("Model Wiki");

        Assert.That(win, Is.Not.Null,
            "Model Wiki / Lab window not found in the app process. Did SetUp succeed?");
        return win!;
    }

    /// <summary>
    /// Close every Model Wiki window currently open in the app process.
    /// Handles the single-instance guarantee as well as any leak from a crashed test.
    /// </summary>
    private static void CloseAllWikiWindows()
    {
        try
        {
            // Enumerate all process windows and close any that match
            foreach (var (name, aid) in AppFixture.EnumerateAppWindows().ToList())
            {
                if (aid == "ModelWiki.Root" ||
                    (name.Contains("Model Wiki", StringComparison.OrdinalIgnoreCase) &&
                     !name.StartsWith("  └─", StringComparison.Ordinal)))
                {
                    var w = AppFixture.FindWindowByAutomationId("ModelWiki.Root")
                         ?? AppFixture.FindWindowByTitle("Model Wiki");
                    try { w?.Close(); } catch { }
                    Thread.Sleep(150);
                }
            }
        }
        catch { /* non-fatal */ }
    }
}
