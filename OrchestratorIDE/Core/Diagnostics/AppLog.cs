// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Core.Diagnostics;

public enum AppLogLevel { Debug, Info, Warn, Error }

/// <summary>
/// The desktop app's persistent log file, for finding bugs after the fact. Before this, the
/// Activity panel kept its last 5,000 lines in memory only, unhandled exceptions went nowhere,
/// and llama.cpp's own error lines (the real reason a model fails to load) were never saved.
///
/// <list type="bullet">
/// <item>One UTF-8 file per day, <c>theorc-YYYY-MM-DD.log</c>, kept for 14 days, capped at
///   20 MB per day.</item>
/// <item>Location: macOS <c>~/Library/Logs/TheOrc</c> (readable in Console.app); Windows
///   <c>%LOCALAPPDATA%\TheOrc\logs</c>; Linux <c>$XDG_STATE_HOME/theorc/logs</c> or
///   <c>~/.local/state/theorc/logs</c>.</item>
/// <item>Local only: nothing is uploaded. Anything that looks like a key, token or password is
///   masked before it is written, and long messages are truncated.</item>
/// <item>Off until <see cref="Initialize"/> is called (the desktop app does; the Daemon and
///   tests don't), and it never throws: an I/O failure just stops logging.</item>
/// </list>
/// </summary>
public static class AppLog
{
    public const int RetentionDays = 14;
    public const long MaxBytesPerDay = 20L * 1024 * 1024;
    public const int MaxMessageChars = 2_000;

    private static readonly object Gate = new();
    private static string? _directory;
    private static StreamWriter? _writer;
    private static string? _currentPath;
    private static DateOnly _currentDay;
    private static bool _capReached;
    private static Func<DateTimeOffset> _clock = () => DateTimeOffset.Now;
    private static long _maxBytes = MaxBytesPerDay;

    public static bool IsEnabled { get { lock (Gate) return _directory is not null; } }

    /// <summary>Where log files go on this OS (whether or not logging is on).</summary>
    public static string DefaultDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
                return Path.Combine(home, "Library", "Logs", "TheOrc");
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheOrc", "logs");
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            return Path.Combine(string.IsNullOrEmpty(state) ? Path.Combine(home, ".local", "state") : state, "theorc", "logs");
        }
    }

    /// <summary>The directory in use, or null when logging is off.</summary>
    public static string? LogDirectory { get { lock (Gate) return _directory; } }

    /// <summary>Today's log file path, or null when logging is off.</summary>
    public static string? CurrentFile { get { lock (Gate) return _currentPath; } }

    /// <summary>
    /// Turns logging on: creates the directory, deletes files older than
    /// <see cref="RetentionDays"/>, and writes a session header. Safe to call more than once.
    /// </summary>
    public static void Initialize(string? directory = null, Func<DateTimeOffset>? clock = null)
        => Initialize(directory, clock, MaxBytesPerDay);

    /// <summary>Test seam: a smaller daily cap.</summary>
    internal static void Initialize(string? directory, Func<DateTimeOffset>? clock, long maxBytesPerDay)
    {
        lock (Gate)
        {
            CloseWriter();
            _clock = clock ?? (() => DateTimeOffset.Now);
            _maxBytes = maxBytesPerDay;
            _capReached = false;
            try
            {
                var dir = directory ?? DefaultDirectory;
                Directory.CreateDirectory(dir);
                // Logs can contain snippets of agent output: owner-only on macOS/Linux
                // (Windows' %LOCALAPPDATA% is already per-user).
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                _directory = dir;
                PruneOldFiles(dir, DateOnly.FromDateTime(_clock().LocalDateTime));
            }
            catch
            {
                _directory = null;
                return;
            }
        }
        Write(AppLogLevel.Info, "Session", SessionHeader());
    }

    /// <summary>Turns logging off and closes the file (tests; app exit).</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            CloseWriter();
            _directory = null;
        }
    }

    public static void Info(string category, string message) => Write(AppLogLevel.Info, category, message);
    public static void Warn(string category, string message) => Write(AppLogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(AppLogLevel.Error, category, message);

    public static void Exception(string category, Exception ex, string? context = null) =>
        Write(AppLogLevel.Error, category, (context is null ? "" : context + "\n") + ex);

    /// <summary>
    /// A native llama.cpp log line, as delivered to TheOrc's log sink ("[llama:Error] …").
    /// Only warnings and errors are kept: llama.cpp also emits hundreds of info/debug lines per
    /// model load (kernel compiles, tensor lists) that would bury everything else.
    /// </summary>
    public static void Native(string line)
    {
        if (line.Contains("[llama:Error]", StringComparison.Ordinal))
            Write(AppLogLevel.Error, "llama.cpp", line);
        else if (line.Contains("[llama:Warn", StringComparison.Ordinal))
            Write(AppLogLevel.Warn, "llama.cpp", line);
    }

    public static void Write(AppLogLevel level, string category, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_directory is null)
                    return;
                var now = _clock();
                if (!EnsureWriter(DateOnly.FromDateTime(now.LocalDateTime)))
                    return;

                var line = Format(now, level, category, message);
                if (_writer!.BaseStream.Length + Encoding.UTF8.GetByteCount(line) > _maxBytes)
                {
                    _capReached = true;
                    _writer.WriteLine(Format(now, AppLogLevel.Warn, "AppLog",
                        $"Daily log reached its {_maxBytes / (1024 * 1024.0):0.##} MB cap; nothing more is written today."));
                    _writer.Flush();
                    return;
                }
                _writer.Write(line);
                _writer.Flush();
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>The last <paramref name="maxLines"/> lines of today's log ("" when off).</summary>
    public static string Tail(int maxLines)
    {
        string? path;
        lock (Gate)
        {
            path = _currentPath;
            _writer?.Flush();
        }
        if (path is null || !File.Exists(path))
            return "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>(maxLines + 1);
            while (reader.ReadLine() is { } l)
            {
                lines.Enqueue(l);
                if (lines.Count > maxLines) lines.Dequeue();
            }
            return string.Join('\n', lines);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Bug-report text: the session header, where the log lives, and the last
    /// <paramref name="maxLines"/> log lines (entries are redacted when written).
    /// </summary>
    public static string DiagnosticsReport(int maxLines)
    {
        var tail = Tail(maxLines);
        return "TheOrc diagnostics\n" +
               SessionHeader() + "\n" +
               $"Log file: {CurrentFile ?? "(logging is off)"}\n\n" +
               (tail.Length > 0 ? $"Last {tail.Split('\n').Length} log lines:\n{tail}" : "(no log lines yet)");
    }

    /// <summary>Version, OS, architecture, CPU and memory, for the session header and bug reports.</summary>
    public static string SessionHeader()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppLog).Assembly;
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var memGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        return $"TheOrc {version} | {RuntimeInformation.OSDescription} | {RuntimeInformation.OSArchitecture} | " +
               $"{Environment.ProcessorCount} cores | {memGb:F0} GB RAM | .NET {Environment.Version} | pid {Environment.ProcessId}";
    }

    // ── Formatting and redaction ──────────────────────────────────────────────────────

    internal static string Format(DateTimeOffset time, AppLogLevel level, string category, string message)
    {
        var text = Redact(message ?? "");
        if (text.Length > MaxMessageChars)
            text = text[..MaxMessageChars] + $" … [truncated {text.Length - MaxMessageChars} chars]";
        // Continuation lines are indented so every entry starts with a timestamp.
        text = text.Replace("\r\n", "\n").Replace("\n", "\n    ");
        var tag = level switch
        {
            AppLogLevel.Debug => "DEBUG",
            AppLogLevel.Info  => "INFO ",
            AppLogLevel.Warn  => "WARN ",
            _                 => "ERROR",
        };
        return $"{time:yyyy-MM-dd'T'HH:mm:ss.fffzzz} {tag} [{Redact(category ?? "")}] {text}{Environment.NewLine}";
    }

    private static readonly Regex[] SecretPatterns =
    [
        new(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled),
        new(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{20,}", RegexOptions.Compiled),
        new(@"\bxai-[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled),
        new(@"\bxox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled),
        new(@"\bAIza[0-9A-Za-z_-]{35}\b", RegexOptions.Compiled),
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.Compiled),
    ];

    // key=value / key: value / "key": "value" for secret-ish names; keeps the name, masks the value.
    private static readonly Regex SecretAssignment = new(
        @"(?i)(\b[\w-]*(?:password|passwd|secret|token|api[_-]?key|apikey|auth)[\w-]*[""']?\s*[:=]\s*[""']?)([^\s""',;]{6,})",
        RegexOptions.Compiled);

    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var pattern in SecretPatterns)
            text = pattern.Replace(text, "[REDACTED]");
        return SecretAssignment.Replace(text, m => m.Groups[1].Value + "[REDACTED]");
    }

    // ── Files ─────────────────────────────────────────────────────────────────────────

    private static bool EnsureWriter(DateOnly day)
    {
        if (_writer is not null && day == _currentDay)
            return !_capReached;

        CloseWriter();
        _capReached = false;
        _currentDay = day;
        _currentPath = Path.Combine(_directory!, FileNameFor(day));
        var options = new FileStreamOptions
        {
            Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600 for new files
        var stream = new FileStream(_currentPath, options);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _capReached = stream.Length >= _maxBytes;
        return !_capReached;
    }

    internal static string FileNameFor(DateOnly day) => $"theorc-{day:yyyy-MM-dd}.log";

    private static void PruneOldFiles(string dir, DateOnly today)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "theorc-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (DateOnly.TryParseExact(name["theorc-".Length..], "yyyy-MM-dd", out var day) &&
                day < today.AddDays(-RetentionDays))
            {
                try { File.Delete(file); } catch { /* keep going */ }
            }
        }
    }

    private static void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _currentPath = null;
    }
}
