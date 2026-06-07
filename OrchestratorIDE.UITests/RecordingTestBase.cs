using NUnit.Framework;
using NUnit.Framework.Interfaces;
using System.Drawing;

namespace OrchestratorIDE.UITests;

/// <summary>
/// Base class for all FlaUI test fixtures.
///
/// Automatically records the OrchestratorIDE window for every test:
///   [SetUp]    → starts recording   (before the subclass [SetUp])
///   [TearDown] → stops  recording   (after  the subclass [TearDown])
///
/// NUnit ordering guarantees:
///   Setup:    base first  → derived second
///   Teardown: derived first → base second
///
/// This means recording wraps the full lifecycle of each test, including
/// any navigation the subclass performs in its own [SetUp].
///
/// Recordings appear in %APPDATA%\OrchestratorIDE\Recordings\ with the naming:
///   UITest_{ClassName}_{MethodName}_{timestamp}.avi          (passed)
///   UITest_{ClassName}_{MethodName}_{timestamp}_FAILED.avi   (failed)
///
/// The recording path is written to TestContext output so it's visible in
/// the NUnit test result XML / VS Test Explorer output.
/// </summary>
public abstract class RecordingTestBase
{
    private readonly TestVideoRecorder _recorder = new();

    [SetUp]
    public void StartRecording()
    {
        try
        {
            // Build a tidy test name:  T01_LaunchTests_MainWindow_IsVisible
            var ctx      = TestContext.CurrentContext;
            var cls      = ctx.Test.ClassName?.Split('.').Last() ?? "UnknownClass";
            var method   = ctx.Test.MethodName ?? "UnknownMethod";
            var testName = $"{cls}_{method}";

            var bounds = GetWindowBounds();
            _recorder.Start(testName, bounds);
        }
        catch
        {
            // Recording setup must never fail a test.
        }
    }

    [TearDown]
    public void StopRecording()
    {
        try
        {
            var passed = TestContext.CurrentContext.Result.Outcome.Status
                         != TestStatus.Failed;

            var path = _recorder.Stop(passed);

            if (!string.IsNullOrEmpty(path))
                TestContext.WriteLine($"[Recording] {path}");
        }
        catch
        {
            // Teardown errors must not obscure real test failures.
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current bounding rectangle of the OrchestratorIDE main window.
    /// Falls back to <see cref="Rectangle.Empty"/> if FlaUI can't read it.
    /// </summary>
    private static Rectangle GetWindowBounds()
    {
        try
        {
            var r = AppFixture.MainWindow.BoundingRectangle;
            return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        }
        catch
        {
            return Rectangle.Empty;
        }
    }
}
