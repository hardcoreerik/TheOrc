// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;

namespace OrchestratorIDE.UITests;

/// <summary>
/// Thin end-to-end smoke for the Avalonia shell using the shared AppFixture so
/// the full UI lane does not co-launch a second desktop process.
/// </summary>
[TestFixture]
[Category("AvaloniaSmoke")]
public class AvaloniaSmokeTests
{
    [Test]
    public void Avalonia_shell_launches_and_shows_main_window()
    {
        Assert.That(AppFixture.MainWindow, Is.Not.Null, "Avalonia main window did not appear within timeout.");
        Assert.That(AppFixture.MainWindow.Title, Is.Not.Null.And.Not.Empty, "Main window should have a title.");
    }
}
