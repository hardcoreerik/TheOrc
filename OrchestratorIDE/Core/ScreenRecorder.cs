// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// Screen recording requires WPF (SharpAvi + WPF RenderTargetBitmap).
// The Avalonia project includes this file but compiles only the stub (no WPF).
#if WPF
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SharpAvi.Codecs;
using SharpAvi.Output;
#endif

namespace OrchestratorIDE.Core;

#if WPF
/// <summary>
/// Captures the main WPF window at ~10 fps and writes a timestamped .avi
/// to %APPDATA%\OrchestratorIDE\Recordings\.
///
/// Usage:
///   _recorder.Start(mainWindow);   // begin capture
///   _recorder.Stop();              // flush + close file
///
/// Output: MJPEG-compressed AVI — plays in VLC, Windows Media Player, any browser.
/// Toggle with F12. Red dot + elapsed timer shown in the status bar while active.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const int    Fps         = 10;
    private const int    JpegQuality = 75;   // 70-85 is a good debug trade-off
    private const string FolderName  = "Recordings";

    // ── State ─────────────────────────────────────────────────────────────────

    private DispatcherTimer?      _timer;
    private AviWriter?            _writer;
    private IAviVideoStream?      _stream;
    private FrameworkElement?     _target;
    private string?               _filePath;
    private int                   _frameWidth;
    private int                   _frameHeight;
    private DateTime              _startTime;
    private bool                  _disposed;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fired every second with the elapsed time string (e.g. "00:42").</summary>
    public event Action<string>?  OnTick;

    /// <summary>Fired when recording stops. Passes the output file path.</summary>
    public event Action<string>?  OnStopped;

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRecording => _timer?.IsEnabled == true;

    /// <summary>
    /// Begin recording <paramref name="target"/> (typically the main Window).
    /// Silently no-ops if already recording.
    /// </summary>
    public void Start(FrameworkElement target)
    {
        if (IsRecording) return;

        _target      = target;
        _frameWidth  = SnapToEven((int)target.ActualWidth);
        _frameHeight = SnapToEven((int)target.ActualHeight);
        _startTime   = DateTime.Now;
        _filePath    = BuildFilePath();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            _writer = new AviWriter(_filePath)
            {
                FramesPerSecond = Fps,
                EmitIndex1      = true,
            };

            var encoder = new MotionJpegVideoEncoderWpf(_frameWidth, _frameHeight, JpegQuality);
            _stream = _writer.AddEncodingVideoStream(encoder, true, _frameWidth, _frameHeight);
            _stream.Name = "TheOrc Debug";
        }
        catch (Exception ex)
        {
            _writer?.Close();
            _writer   = null;
            _stream   = null;
            _filePath = null;
            throw new InvalidOperationException($"Failed to open recording file: {ex.Message}", ex);
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / Fps),
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    /// <summary>
    /// Stop recording, flush the AVI file, and fire <see cref="OnStopped"/>.
    /// </summary>
    public void Stop()
    {
        if (!IsRecording) return;

        _timer!.Stop();
        _timer.Tick -= OnTimerTick;
        _timer = null;

        try
        {
            _writer?.Close();
        }
        catch { /* best-effort flush */ }

        _writer = null;
        _stream = null;

        var path = _filePath ?? "";
        _filePath = null;

        OnStopped?.Invoke(path);
    }

    // ── Frame capture ─────────────────────────────────────────────────────────

    private int _tickCount;

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_target is null || _stream is null) return;

        try
        {
            // Capture the WPF element to a Bgr32 pixel buffer.
            // MotionJpegVideoEncoderWpf expects raw BGR32 pixels — the encoder
            // handles the JPEG compression internally before writing to the AVI.
            var rtb = new RenderTargetBitmap(
                _frameWidth, _frameHeight, 96, 96, PixelFormats.Bgr32);
            rtb.Render(_target);

            var stride = _frameWidth * 4;
            var pixels = new byte[_frameHeight * stride];
            rtb.CopyPixels(pixels, stride, 0);

            _stream.WriteFrame(true, pixels, 0, pixels.Length);

            // Fire elapsed timer once per second (every Fps ticks)
            _tickCount++;
            if (_tickCount % Fps == 0)
            {
                var elapsed = DateTime.Now - _startTime;
                OnTick?.Invoke($"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}");
            }
        }
        catch
        {
            // Never crash the app due to a failed frame capture
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildFilePath()
    {
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrchestratorIDE", FolderName);
        var name = $"TheOrc_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.avi";
        return Path.Combine(dir, name);
    }

    /// <summary>AVI dimensions must be even numbers.</summary>
    private static int SnapToEven(int value) => value % 2 == 0 ? value : value - 1;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Opens the Recordings folder in Windows Explorer.
    /// Creates it if it doesn't exist yet.
    /// </summary>
    public static void OpenRecordingsFolder()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrchestratorIDE", FolderName);
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }
}
#else
/// <summary>
/// Screen recording requires WPF — not available in the Avalonia build.
/// This stub preserves the public API so cross-platform code compiles unchanged.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    public event Action<string>? OnTick;
    public event Action<string>? OnStopped;
    public bool IsRecording => false;

    public void Start(object target) { }
    public void Stop() { }
    public void Dispose() { }
    public static void OpenRecordingsFolder() { }
}
#endif
