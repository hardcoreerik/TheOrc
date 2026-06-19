// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using FlaUI.Core;
using FlaUI.UIA3;
using NUnit.Framework;
using OrchestratorIDE.UITests;

// File lives in Tests/ as T20_*.cs per the suite convention, BUT the namespace is
// deliberately a SIBLING of OrchestratorIDE.UITests (not nested under it): NUnit's
// [SetUpFixture] scope is by namespace, so this keeps the WPF AppFixture from
// co-launching the WPF app whenever an Avalonia smoke test runs.
namespace OrchestratorIDE.AvaloniaSmoke;

/// <summary>
/// Thin end-to-end smoke for the Avalonia shell: launches the real
/// OrchestratorIDE.Avalonia.exe via UI Automation and asserts the main window
/// appears. Coexistence phase — WPF remains primary (covered by T01–T08); this
/// only proves the migrated UI boots as a standalone process.
///
/// Self-managed fixture (not a [SetUpFixture]) so it owns its own launch/teardown
/// independent of the WPF black-box suite. Requires an interactive Windows
/// session (UIA), so it is tagged [Category("AvaloniaSmoke")] for selective runs.
/// </summary>
[TestFixture]
[Category("AvaloniaSmoke")]
public class AvaloniaSmokeTests
{
    private Application?    _app;
    private UIA3Automation? _automation;

    private static string ResolveAvaloniaExe()
        => ExecutableResolver.Resolve(
            environmentVariable: "ORCHESTRATOR_AVALONIA_EXE",
            projectDirectoryName: "OrchestratorIDE.Avalonia",
            targetFramework: "net10.0",
            executableName: "OrchestratorIDE.Avalonia.exe");

    [OneTimeTearDown]
    public void TearDown()
    {
        try { _app?.Kill(); } catch { /* already gone */ }
        _automation?.Dispose();
        _app?.Dispose();
    }

    [Test]
    public void Avalonia_shell_launches_and_shows_main_window()
    {
        var exe = ResolveAvaloniaExe();
        TestContext.Progress.WriteLine($"Launching OrchestratorIDE.Avalonia: {exe}");

        _automation = new UIA3Automation();
        _app        = Application.Launch(exe);

        var main = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(20));
        Assert.That(main, Is.Not.Null, "Avalonia main window did not appear within timeout.");
        Assert.That(main.Title, Is.Not.Null.And.Not.Empty, "Main window should have a title.");
    }
}
