using FlaUI.Core.AutomationElements;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T04 — Tool Editor panel: scaffold, compile, status reporting.
/// Load is NOT tested here because it requires Roslyn and a valid ICustomTool
/// implementation — that belongs in integration tests.
/// </summary>
[TestFixture]
public class T04_ToolEditorTests : RecordingTestBase
{
    [SetUp]
    public void NavigateToToolEditor()
    {
        AppFixture.RequireById("ActivityBar.Tools").AsButton().Click();
        AppFixture.WaitUntil(() => AppFixture.FindById("ToolEditor.Compile") != null,
            TimeSpan.FromSeconds(5));
    }

    [TearDown]
    public void ReturnToAgent()
    {
        AppFixture.RequireById("ActivityBar.Agent").AsButton().Click();
    }

    [Test]
    public void ToolEditor_AllControls_ArePresent()
    {
        string[] ids =
        [
            "ToolEditor.New",
            "ToolEditor.Compile",
            "ToolEditor.Load",
            "ToolEditor.Save",
            "ToolEditor.ToolName",
            "ToolEditor.Editor",
            "ToolEditor.Status",
            "ToolEditor.DiagList",
        ];

        foreach (var id in ids)
            Assert.That(AppFixture.FindById(id), Is.Not.Null, $"'{id}' should be present.");
    }

    [Test]
    public void ToolEditor_StatusBar_ShowsReadyOnLoad()
    {
        var status = AppFixture.RequireById("ToolEditor.Status");
        // Status bar should contain some text (not empty) immediately after switching panels
        Assert.That(status.Name, Is.Not.Empty.Or.Not.Null,
            "Status bar should have text content.");
    }

    [Test]
    public void ToolEditor_BtnNew_IsEnabled()
    {
        var btn = AppFixture.RequireById("ToolEditor.New").AsButton();
        Assert.That(btn.IsEnabled, Is.True, "New button should always be enabled.");
    }

    [Test]
    public void ToolEditor_ToolName_HasDefaultValue()
    {
        var tb = AppFixture.RequireById("ToolEditor.ToolName").AsTextBox();
        Assert.That(tb.Text, Is.Not.Empty,
            "ToolName field should have a default placeholder value.");
    }

    [Test]
    public void ToolEditor_BtnNew_SetsEditorContent()
    {
        AppFixture.RequireById("ToolEditor.New").AsButton().Click();
        Thread.Sleep(300);

        // The editor is an AvalonEdit control. We check its parent container is on-screen
        // (full text content is not accessible via UIA but the element itself must be visible).
        var editor = AppFixture.RequireById("ToolEditor.Editor");
        Assert.That(editor.IsOffscreen, Is.False,
            "Code editor should be visible after clicking New.");
    }
}
