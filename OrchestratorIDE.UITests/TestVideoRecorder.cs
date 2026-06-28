// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using SharpAvi;
using SharpAvi.Codecs;
using SharpAvi.Output;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OrchestratorIDE.UITests;

/// <summary>
/// Captures a screen rectangle to an MJPEG AVI during a FlaUI test.
///
/// Each test gets its own timestamped file:
///   UITest_{ClassName}_{TestName}_{yyyy-MM-dd_HH-mm-ss}.avi
///
/// Failed tests have _FAILED appended before the extension so they're
/// immediately obvious in the folder without opening each file.
///
/// All recordings land in %APPDATA%\OrchestratorIDE\Recordings\ —
/// the same folder as the main-app F12 recordings (one place to look).
///
/// Encoding uses a self-contained GDI+ MJPEG encoder — no WPF required.
/// </summary>
public sealed class TestVideoRecorder : IDisposable
{
    // ── Config ────────────────────────────────────────────────────────────────

    private const int    Fps         = 10;
    private const int    JpegQuality = 75;
    private const string FolderName  = "Recordings";

    // ── State ─────────────────────────────────────────────────────────────────

    private Thread?          _thread;
    private volatile bool    _running;
    private AviWriter?       _writer;
    private IAviVideoStream? _stream;
    private string?          _filePath;
    private Rectangle        _captureRect;
    private bool             _disposed;

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsRecording => _running;

    /// <summary>
    /// Begin recording the screen region <paramref name="captureRect"/>.
    /// File name is derived from <paramref name="testName"/>.
    /// Silently no-ops if already recording or if the rectangle is empty.
    /// </summary>
    public void Start(string testName, Rectangle captureRect)
    {
        if (_running || captureRect.IsEmpty) return;

        _captureRect = SnapToEven(captureRect);
        if (_captureRect.Width <= 0 || _captureRect.Height <= 0) return;

        _filePath = BuildFilePath(testName);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

        _writer = new AviWriter(_filePath)
        {
            FramesPerSecond = Fps,
            EmitIndex1      = true,
        };

        // GdiMjpegEncoder: no WPF required — encodes using System.Drawing (GDI+)
        var encoder = new GdiMjpegEncoder(_captureRect.Width, _captureRect.Height, JpegQuality);
        _stream = _writer.AddEncodingVideoStream(
            encoder, true, _captureRect.Width, _captureRect.Height);
        _stream.Name = "TheOrc UITest";

        _running = true;
        _thread  = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name         = $"UITest-Recorder [{testName}]",
        };
        _thread.Start();
    }

    /// <summary>
    /// Stop recording. If <paramref name="passed"/> is false the file is
    /// renamed with a _FAILED suffix. Returns the final file path.
    /// </summary>
    public string Stop(bool passed)
    {
        if (!_running && _filePath is null) return "";

        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;

        try { _writer?.Close(); } catch { /* best-effort flush */ }
        _writer = null;
        _stream = null;

        var path  = _filePath ?? "";
        _filePath = null;

        if (!passed && File.Exists(path))
        {
            var dir      = Path.GetDirectoryName(path)!;
            var stem     = Path.GetFileNameWithoutExtension(path);
            var ext      = Path.GetExtension(path);
            var failPath = Path.Combine(dir, stem + "_FAILED" + ext);
            try { File.Move(path, failPath, overwrite: true); path = failPath; }
            catch { /* keep original name if rename fails */ }
        }

        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_running) Stop(passed: true);
    }

    // ── Capture loop ──────────────────────────────────────────────────────────

    private void CaptureLoop()
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / Fps);

        while (_running)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { CaptureFrame(); } catch { /* never crash the test process */ }
            var remaining = interval - sw.Elapsed;
            if (remaining > TimeSpan.Zero)
                Thread.Sleep(remaining);
        }
    }

    private void CaptureFrame()
    {
        if (_stream is null) return;

        // Capture screen region into a 32-bpp bitmap.
        // Format32bppRgb on Windows = B G R 0 in memory = Bgr32 pixel layout.
        using var bmp = new Bitmap(_captureRect.Width, _captureRect.Height,
                                   PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(_captureRect.Location, Point.Empty, _captureRect.Size);

        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var pixels = new byte[bmp.Height * stride];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            _stream.WriteFrame(true, pixels, 0, pixels.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildFilePath(string testName)
    {
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrchestratorIDE", FolderName);
        var safe = MakeSafe(testName);
        var name = $"UITest_{safe}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.avi";
        return Path.Combine(dir, name);
    }

    private static string MakeSafe(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>AVI dimensions must be even numbers.</summary>
    private static Rectangle SnapToEven(Rectangle r) =>
        new(r.X, r.Y,
            r.Width  % 2 == 0 ? r.Width  : r.Width  - 1,
            r.Height % 2 == 0 ? r.Height : r.Height - 1);
}

// ─────────────────────────────────────────────────────────────────────────────
// GDI+ MJPEG encoder — no WPF required
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// SharpAvi <see cref="IVideoEncoder"/> that compresses frames as JPEG using
/// System.Drawing (GDI+). Drop-in replacement for MotionJpegVideoEncoderWpf
/// in contexts where WPF is not available (e.g. NUnit test projects).
///
/// Input: raw BGR32 pixel bytes (same layout as Format32bppRgb LockBits).
/// Output: JFIF JPEG bytes written directly into the AVI MJPEG stream.
/// </summary>
internal sealed class GdiMjpegEncoder : IVideoEncoder
{
    private readonly int             _width;
    private readonly int             _height;
    private readonly ImageCodecInfo  _jpegCodec;
    private readonly EncoderParameters _encParams;

    public GdiMjpegEncoder(int width, int height, int quality)
    {
        _width  = width;
        _height = height;

        _jpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        _encParams = new EncoderParameters(1)
        {
            Param = { [0] = new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, (long)quality) },
        };
    }

    // IVideoEncoder
    public FourCC       Codec        => CodecIds.MotionJpeg;
    public BitsPerPixel BitsPerPixel => BitsPerPixel.Bpp24;

    /// <summary>
    /// Upper bound on encoded output. JPEG at any quality is smaller than raw
    /// BGR32 input, so width*height*4 is a safe over-estimate.
    /// </summary>
    public int MaxEncodedSize => _width * _height * 4 + 4096;

    public int EncodeFrame(byte[] source, int srcOffset,
                           byte[] destination, int destOffset,
                           out bool isKeyFrame)
    {
        isKeyFrame = true;   // MJPEG — every frame is a keyframe
        var jpeg = EncodeFrameToJpeg(source.AsSpan(srcOffset));
        Buffer.BlockCopy(jpeg, 0, destination, destOffset, jpeg.Length);
        return jpeg.Length;
    }

    public int EncodeFrame(ReadOnlySpan<byte> source, Span<byte> destination, out bool isKeyFrame)
    {
        // ponytail: test-only adapter for SharpAvi 3 span API; keep array path as the single implementation.
        isKeyFrame = true;
        var jpeg = EncodeFrameToJpeg(source);
        jpeg.CopyTo(destination);
        return jpeg.Length;
    }

    private byte[] EncodeFrameToJpeg(ReadOnlySpan<byte> source)
    {
        using var bmp = new Bitmap(_width, _height, PixelFormat.Format32bppRgb);
        var bits = bmp.LockBits(new Rectangle(0, 0, _width, _height),
                                ImageLockMode.WriteOnly,
                                PixelFormat.Format32bppRgb);
        try
        {
            var stride = Math.Abs(bits.Stride);
            var byteCount = checked(_height * stride);
            if (source.Length < byteCount)
                throw new ArgumentException("Source frame is smaller than the expected bitmap size.", nameof(source));

            var raw = source[..byteCount].ToArray();
            Marshal.Copy(raw, 0, bits.Scan0, byteCount);
        }
        finally { bmp.UnlockBits(bits); }

        using var ms = new MemoryStream();
        bmp.Save(ms, _jpegCodec, _encParams);
        return ms.ToArray();
    }
}
