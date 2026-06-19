// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using NUnit.Framework;

namespace OrchestratorIDE.UITests;

/// <summary>
/// Shared NUnit fixture that launches OrchestratorIDE.exe once per test assembly,
/// gets the main window, and tears it all down afterwards.
///
/// Individual test classes use [SetUpFixture] indirectly by calling
/// AppFixture.Window to access UI elements.
///
/// Set the ORCHESTRATOR_EXE environment variable to override the default path
/// (useful in CI where the publish output is in a different location).
/// </summary>
[SetUpFixture]
public class AppFixture
{
    private static Application?    _app;
    private static UIA3Automation? _automation;

    public static Window          MainWindow  { get; private set; } = null!;
    public static UIA3Automation  Automation  => _automation!;
    public static int             AppProcessId => _app?.ProcessId ?? -1;

    // ── Path resolution ───────────────────────────────────────────────────

    private static string ResolveExePath()
    {
        return ExecutableResolver.Resolve(
            environmentVariable: "ORCHESTRATOR_EXE",
            projectDirectoryName: "OrchestratorIDE",
            targetFramework: "net10.0-windows",
            executableName: "OrchestratorIDE.exe");
    }

    // ── One-time setup ────────────────────────────────────────────────────

    [OneTimeSetUp]
    public void LaunchApp()
    {
        var exePath = ResolveExePath();
        TestContext.Progress.WriteLine($"Launching OrchestratorIDE: {exePath}");

        _automation = new UIA3Automation();
        _app        = Application.Launch(exePath);

        MainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(15));
        Assert.That(MainWindow, Is.Not.Null, "Main window did not appear within timeout.");

        try
        {
            MainWindow.Patterns.Window.Pattern.SetWindowVisualState(
                FlaUI.Core.Definitions.WindowVisualState.Maximized);
            Thread.Sleep(500);
        }
        catch { /* pattern unavailable — continue without maximizing */ }
    }

    // ── One-time teardown ─────────────────────────────────────────────────

    [OneTimeTearDown]
    public void KillApp()
    {
        try { _app?.Kill(); } catch { /* already gone */ }
        _automation?.Dispose();
        _app?.Dispose();
    }

    // ── Element helpers ───────────────────────────────────────────────────

    /// <summary>Find an element by its AutomationId. Returns null if not found.</summary>
    public static AutomationElement? FindById(string automationId)
        => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    /// <summary>Find and assert an element exists.</summary>
    public static AutomationElement RequireById(string automationId)
    {
        var el = FindById(automationId);
        Assert.That(el, Is.Not.Null, $"Element '{automationId}' not found in the UI tree.");
        return el!;
    }

    // ── Window helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Search for a top-level <see cref="Window"/> in the app process whose
    /// UIA Name contains <paramref name="titleFragment"/> (case-insensitive).
    /// Uses process-ID scoping so foreign windows with similar titles are ignored.
    /// </summary>
    public static Window? FindWindowByTitle(string titleFragment)
        => FindInProcess(w =>
            w.Name?.Contains(titleFragment, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>
    /// Search for a top-level <see cref="Window"/> in the app process whose
    /// AutomationId exactly matches <paramref name="automationId"/>.
    /// This is the most reliable way to find a window when its AutomationId is known.
    /// </summary>
    public static Window? FindWindowByAutomationId(string automationId)
        => FindInProcess(w => w.AutomationId == automationId);

    /// <summary>
    /// Core window search scoped to the app process.
    ///
    /// Search order:
    ///   1. Direct desktop children for our PID (standard top-level windows).
    ///   2. Direct Window-typed children of each app window (owned/double-owned windows
    ///      that UIA places under their owner rather than the desktop root).
    ///
    /// Returns null if not found or on any UIA error.
    /// </summary>
    private static Window? FindInProcess(Func<AutomationElement, bool> predicate)
    {
        try
        {
            if (_app == null || _automation == null) return null;

            var pid     = _app.ProcessId;
            var desktop = _automation.GetDesktop();

            // Pass 1: direct desktop children scoped to our process
            var appWins = desktop.FindAllChildren(cf => cf.ByProcessId(pid));

            var found = appWins.FirstOrDefault(predicate);
            if (found != null) return found.AsWindow();

            // Pass 2: owned windows that appear as children of another app window
            foreach (var parent in appWins)
            {
                try
                {
                    var childWins = parent.FindAllChildren(cf =>
                        cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
                    var child = childWins.FirstOrDefault(predicate);
                    if (child != null) return child.AsWindow();
                }
                catch { /* skip inaccessible windows */ }
            }

            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Enumerate ALL windows currently belonging to the app process.
    /// Used for diagnostic output when a window search fails.
    /// </summary>
    public static List<(string Name, string AutomationId)> EnumerateAppWindows()
    {
        var result = new List<(string, string)>();
        if (_app == null || _automation == null) return result;

        var pid     = _app.ProcessId;
        var desktop = _automation.GetDesktop();

        AutomationElement[] appWins;
        try { appWins = desktop.FindAllChildren(cf => cf.ByProcessId(pid)); }
        catch { return result; }

        foreach (var w in appWins)
        {
            result.Add((w.Name ?? "<null>", w.AutomationId ?? "<null>"));

            // also report owned children
            AutomationElement[] childWins;
            try
            {
                childWins = w.FindAllChildren(cf =>
                    cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
            }
            catch { continue; }

            foreach (var c in childWins)
                result.Add(($"  └─ {c.Name ?? "<null>"}", c.AutomationId ?? "<null>"));
        }

        return result;
    }

    // ── Wait helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Spin-wait until <paramref name="condition"/> returns true or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(150);
        }
        return false;
    }

    /// <summary>
    /// Spin-wait until <paramref name="getter"/> returns a non-null value,
    /// then return it; returns null on timeout.
    /// </summary>
    public static T? WaitUntilGet<T>(Func<T?> getter, TimeSpan? timeout = null)
        where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var v = getter();
            if (v != null) return v;
            Thread.Sleep(150);
        }
        return null;
    }
}
