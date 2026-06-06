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
    private static Application? _app;
    private static UIA3Automation? _automation;

    public static Window MainWindow { get; private set; } = null!;

    // ── Path resolution ───────────────────────────────────────────────────

    private static string ResolveExePath()
    {
        // 1. Environment variable (CI / publish overrides)
        var envPath = Environment.GetEnvironmentVariable("ORCHESTRATOR_EXE");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return envPath;

        // 2. Relative to test output: navigate up to solution root then into OrchestratorIDE output
        var testDir = AppDomain.CurrentDomain.BaseDirectory;
        var exeRelative = Path.GetFullPath(
            Path.Combine(testDir,
                @"..\..\..\..\OrchestratorIDE\bin\Debug\net10.0-windows\OrchestratorIDE.exe"));

        if (File.Exists(exeRelative))
            return exeRelative;

        throw new FileNotFoundException(
            $"OrchestratorIDE.exe not found. " +
            $"Build the main project first, or set ORCHESTRATOR_EXE env var. " +
            $"Tried: {exeRelative}");
    }

    // ── One-time setup ────────────────────────────────────────────────────

    [OneTimeSetUp]
    public void LaunchApp()
    {
        var exePath = ResolveExePath();

        _automation = new UIA3Automation();
        _app        = Application.Launch(exePath);

        // Give WPF time to paint; 15 s is generous but avoids flakiness on CI
        MainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(15));
        Assert.That(MainWindow, Is.Not.Null, "Main window did not appear within timeout.");
    }

    // ── One-time teardown ─────────────────────────────────────────────────

    [OneTimeTearDown]
    public void KillApp()
    {
        try { _app?.Kill(); } catch { /* already gone */ }
        _automation?.Dispose();
        _app?.Dispose();
    }

    // ── Helpers shared by all test classes ───────────────────────────────

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
}
