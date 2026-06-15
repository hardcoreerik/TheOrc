// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using NUnit.Framework;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T03 — Command palette (Ctrl+K): opens, accepts input, shows filtered results, closes with Esc.
/// </summary>
[TestFixture]
public class T03_CommandPaletteTests : RecordingTestBase
{
    private void OpenPalette()
    {
        // Bring window into focus first
        AppFixture.MainWindow.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
    }

    [Test]
    public void CommandPalette_OpensWithCtrlK()
    {
        OpenPalette();

        var appeared = AppFixture.WaitUntil(() =>
            AppFixture.FindById("CommandPalette.Search") != null,
            TimeSpan.FromSeconds(5));

        Assert.That(appeared, Is.True, "CommandPalette.Search should appear after Ctrl+K.");

        // Close it
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
    }

    [Test]
    public void CommandPalette_Search_AcceptsInput()
    {
        OpenPalette();

        AppFixture.WaitUntil(() => AppFixture.FindById("CommandPalette.Search") != null);
        var searchBox = AppFixture.RequireById("CommandPalette.Search").AsTextBox();

        searchBox.Enter("tool");
        Thread.Sleep(200);

        Assert.That(searchBox.Text, Does.Contain("tool"),
            "Search box should reflect typed text.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
    }

    [Test]
    public void CommandPalette_ResultsList_PopulatesOnSearch()
    {
        OpenPalette();

        AppFixture.WaitUntil(() => AppFixture.FindById("CommandPalette.Search") != null);
        var searchBox = AppFixture.RequireById("CommandPalette.Search").AsTextBox();
        searchBox.Enter("tool");
        Thread.Sleep(400);

        var results = AppFixture.RequireById("CommandPalette.Results");
        // The list should have at least one child item matching "tool"
        var items = results.FindAllChildren();
        Assert.That(items.Length, Is.GreaterThan(0),
            "ResultsList should contain at least one item when searching 'tool'.");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(300);
    }

    [Test]
    public void CommandPalette_ClosesWithEscape()
    {
        OpenPalette();
        AppFixture.WaitUntil(() => AppFixture.FindById("CommandPalette.Search") != null);

        Keyboard.Press(VirtualKeyShort.ESCAPE);

        var closed = AppFixture.WaitUntil(() =>
            AppFixture.FindById("CommandPalette.Search") == null,
            TimeSpan.FromSeconds(3));

        Assert.That(closed, Is.True, "Command palette should close when Escape is pressed.");
    }
}
