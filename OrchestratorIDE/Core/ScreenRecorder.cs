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
    public static bool IsSupported => true;

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
/// The Avalonia build's recorder. Windows/Linux: a no-op stub, as before (the recorder above
/// needs WPF). macOS: records the whole screen with the system's own
/// <c>/usr/sbin/screencapture -v</c> (cursor and clicks shown, no shutter sound) into
/// <c>~/Library/Application Support/OrchestratorIDE/Recordings/recording_&lt;timestamp&gt;.mov</c>,
/// and stops it the way its "Press Ctrl-C to stop" means: SIGINT, which finishes the movie.
/// macOS asks the user once to allow Screen Recording for TheOrc; if that's refused the movie
/// is never written and <see cref="OnStopped"/> reports an empty path.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    private const string FolderName = "Recordings";
    private const int    SigInt     = 2;

    /// <summary>Elapsed time as mm:ss, once a second while recording (macOS).</summary>
    public event Action<string>? OnTick;

    /// <summary>The finished movie's path, or "" when nothing was written (macOS).</summary>
    public event Action<string>? OnStopped;

    // Swappable for tests (a stand-in recorder and a temp folder).
    internal static string  ScreencapturePath   = "/usr/sbin/screencapture";
    internal static string? DirectoryOverride;

    private readonly object        _gate = new();
    private System.Diagnostics.Process? _process;
    private System.Threading.Timer?     _ticker;
    private string                 _file = "";
    private Task<string>?          _stderr;
    private DateTime               _startedUtc;

    /// <summary>Whether Start actually records here (macOS only in this build).</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS();

    public static string RecordingsDirectory => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrchestratorIDE", FolderName);

    public bool IsRecording
    {
        get { lock (_gate) return _process is { HasExited: false }; }
    }

    public void Start(object target)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        lock (_gate)
        {
            if (_process is { HasExited: false })
                return;
            Directory.CreateDirectory(RecordingsDirectory);
            _file = Path.Combine(RecordingsDirectory, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mov");

            var psi = new System.Diagnostics.ProcessStartInfo(ScreencapturePath)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            foreach (var a in new[] { "-v", "-C", "-k", "-x", _file })
                psi.ArgumentList.Add(a);

            var p = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("screencapture didn't start");
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => Finish(p);
            _ = p.StandardOutput.ReadToEndAsync();
            _stderr = p.StandardError.ReadToEndAsync();
            _process    = p;
            _startedUtc = DateTime.UtcNow;
            _ticker     = new System.Threading.Timer(_ =>
            {
                var t = DateTime.UtcNow - _startedUtc;
                OnTick?.Invoke($"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
            }, null, 1000, 1000);
            if (p.HasExited)   // exited before Exited was hooked up
                ThreadPool.QueueUserWorkItem(_ => Finish(p));
        }
    }

    public void Stop()
    {
        System.Diagnostics.Process? p;
        lock (_gate) p = _process;
        if (p is null || p.HasExited)
            return;
        try
        {
            kill(p.Id, SigInt);   // screencapture writes out the movie and exits
            if (!p.WaitForExit(10_000))
                p.Kill();
        }
        catch { /* already gone */ }
    }

    private void Finish(System.Diagnostics.Process p)
    {
        string file;
        Task<string>? stderr;
        lock (_gate)
        {
            if (!ReferenceEquals(_process, p))
                return;   // already reported
            _process = null;
            _ticker?.Dispose();
            _ticker = null;
            file = _file;
            stderr = _stderr;
        }
        var saved = File.Exists(file) && new FileInfo(file).Length > 0;
        if (!saved)
        {
            // Keep screencapture's own reason for the bug report (Help › Copy Diagnostics).
            var exit = -1;
            try { exit = p.ExitCode; } catch { /* unknown */ }
            var why = "";
            try { why = stderr is not null && stderr.Wait(2000) ? stderr.Result.Trim() : ""; } catch { /* none */ }
            Diagnostics.AppLog.Warn("Recording",
                $"screencapture exited {exit} without a movie{(why.Length > 0 ? ": " + why : "")}");
        }
        p.Dispose();
        OnStopped?.Invoke(saved ? file : "");
    }

    public void Dispose() => Stop();

    public static void OpenRecordingsFolder()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        try { Directory.CreateDirectory(RecordingsDirectory); } catch { /* Reveal still tries */ }
        UI.MacFinder.Reveal(RecordingsDirectory);
    }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.dylib", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
#endif
