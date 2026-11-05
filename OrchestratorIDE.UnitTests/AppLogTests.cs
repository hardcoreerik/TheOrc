// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Diagnostics;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// The desktop app's persistent log file. AppLog is process-wide state, so these tests run one
/// at a time, each against its own temp directory, and always shut logging off afterwards.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class AppLogTests
{
    private string _dir = "";
    private DateTimeOffset _now;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "orc-applog-" + Guid.NewGuid().ToString("N"));
        _now = new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.FromHours(-8));
    }

    [TearDown]
    public void TearDown()
    {
        AppLog.Shutdown();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Start(long maxBytes = AppLog.MaxBytesPerDay) =>
        AppLog.Initialize(_dir, () => _now, maxBytes);

    private string Today => File.ReadAllText(Path.Combine(_dir, "theorc-2026-11-05.log"));

    [Test]
    public void Off_until_initialized_and_after_shutdown()
    {
        AppLog.Info("Test", "before init");
        Assert.That(AppLog.IsEnabled, Is.False);
        Assert.That(Directory.Exists(_dir), Is.False);

        Start();
        AppLog.Shutdown();
        AppLog.Info("Test", "after shutdown");
        Assert.That(Today, Does.Not.Contain("after shutdown"));
    }

    [Test]
    public void Writes_a_session_header_then_timestamped_entries_to_a_daily_file()
    {
        Start();
        AppLog.Warn("Native Runtime", "VRAM admission denied");

        var lines = Today.TrimEnd().Split(Environment.NewLine);
        Assert.That(lines[0], Does.Contain("INFO  [Session] TheOrc "));
        Assert.That(lines[^1], Is.EqualTo("2026-11-05T09:30:00.000-08:00 WARN  [Native Runtime] VRAM admission denied"));
    }

    [Test]
    public void Masks_secrets_but_keeps_what_they_were()
    {
        Start();
        AppLog.Info("Test", "token ghp_" + new string('a', 36) + " and key sk-ant-" + new string('b', 30));
        AppLog.Info("Test", "api_key=supersecret123 password: hunter2hunter2 Authorization: Bearer " + new string('c', 32));
        AppLog.Info("Test", "-----BEGIN RSA PRIVATE KEY-----\nMIIEow\n-----END RSA PRIVATE KEY-----");

        var log = Today;
        Assert.Multiple(() =>
        {
            Assert.That(log, Does.Not.Contain(new string('a', 36)));
            Assert.That(log, Does.Not.Contain(new string('b', 30)));
            Assert.That(log, Does.Not.Contain("supersecret123"));
            Assert.That(log, Does.Not.Contain("hunter2hunter2"));
            Assert.That(log, Does.Not.Contain(new string('c', 32)));
            Assert.That(log, Does.Not.Contain("MIIEow"));
            Assert.That(log, Does.Contain("api_key=[REDACTED]"), "the field name stays readable");
        });
    }

    [Test]
    public void Long_messages_are_truncated_and_multiline_entries_indented()
    {
        Start();
        AppLog.Info("Test", new string('x', AppLog.MaxMessageChars + 500));
        AppLog.Error("Crash", "line one\nline two");

        var log = Today;
        Assert.That(log, Does.Contain("[truncated 500 chars]"));
        Assert.That(log, Does.Contain("ERROR [Crash] line one\n    line two"), "continuation lines are indented");
    }

    [Test]
    public void Only_llama_cpp_warnings_and_errors_are_kept()
    {
        Start();
        AppLog.Native("[llama:Info] load_tensors: CPU_Mapped model buffer size = 125.19 MiB");
        AppLog.Native("[llama:Debug] ggml_metal_library_compile_pipeline: compiling pipeline");
        AppLog.Native("[llama:Error] llama_model_load: error loading model: missing tensor 'blk.40.ssm_conv1d.weight'");
        AppLog.Native("[llama:Warning] something odd");

        var log = Today;
        Assert.That(log, Does.Not.Contain("CPU_Mapped").And.Not.Contain("compile_pipeline"));
        Assert.That(log, Does.Contain("ERROR [llama.cpp] [llama:Error] llama_model_load: error loading model: missing tensor"));
        Assert.That(log, Does.Contain("WARN  [llama.cpp] [llama:Warning] something odd"));
    }

    [Test]
    public void A_new_day_starts_a_new_file()
    {
        Start();
        AppLog.Info("Test", "day one");
        _now = _now.AddDays(1);
        AppLog.Info("Test", "day two");

        Assert.That(Today, Does.Contain("day one").And.Not.Contain("day two"));
        Assert.That(File.ReadAllText(Path.Combine(_dir, "theorc-2026-11-06.log")), Does.Contain("day two"));
    }

    [Test]
    public void Files_older_than_the_retention_window_are_deleted_on_start()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, AppLog.FileNameFor(new DateOnly(2026, 10, 1)));
        var recent = Path.Combine(_dir, AppLog.FileNameFor(new DateOnly(2026, 11, 1)));
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.WriteAllText(unrelated, "keep");

        Start();

        Assert.That(File.Exists(old), Is.False);
        Assert.That(File.Exists(recent), Is.True);
        Assert.That(File.Exists(unrelated), Is.True, "only theorc-*.log files are pruned");
    }

    [Test]
    public void The_daily_size_cap_stops_writing_with_one_notice()
    {
        Start(maxBytes: 4_000);
        for (var i = 0; i < 200; i++)
            AppLog.Info("Test", $"entry {i} " + new string('z', 40));

        var log = Today;
        Assert.That(new FileInfo(Path.Combine(_dir, "theorc-2026-11-05.log")).Length, Is.LessThan(4_400));
        Assert.That(log.Split("cap; nothing more is written today").Length - 1, Is.EqualTo(1));
        Assert.That(log, Does.Not.Contain("entry 199"));
    }

    [Test]
    public void Concurrent_writers_never_interleave_lines()
    {
        Start();
        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 200; i++)
                AppLog.Info("Thread", $"t{t}-i{i}-end");
        });

        var entries = Today.TrimEnd().Split(Environment.NewLine).Skip(1).ToArray();
        Assert.That(entries, Has.Length.EqualTo(1600));
        Assert.That(entries, Has.All.Match(@"^\S+ INFO  \[Thread\] t\d-i\d+-end$"));
    }

    [Test]
    public void An_unusable_directory_turns_logging_off_without_throwing()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "a-file");
        File.WriteAllText(blocker, "");

        Assert.DoesNotThrow(() => AppLog.Initialize(Path.Combine(blocker, "logs")));
        Assert.That(AppLog.IsEnabled, Is.False);
        Assert.DoesNotThrow(() => AppLog.Error("Test", "nowhere to go"));
    }

    [Test]
    public void Log_folder_and_files_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Windows uses the per-user %LOCALAPPDATA% ACL.");
        Start();
        AppLog.Info("Test", "x");
        if (!OperatingSystem.IsWindows())
        {
            Assert.That(File.GetUnixFileMode(_dir), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
            Assert.That(File.GetUnixFileMode(Path.Combine(_dir, "theorc-2026-11-05.log")), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        }
    }

    [Test]
    public void Diagnostics_report_has_header_path_and_recent_lines()
    {
        Start();
        for (var i = 0; i < 10; i++)
            AppLog.Info("Test", $"line {i}");

        var report = AppLog.DiagnosticsReport(maxLines: 3);
        Assert.That(report, Does.StartWith("TheOrc diagnostics\nTheOrc "));
        Assert.That(report, Does.Contain($"Log file: {AppLog.CurrentFile}"));
        Assert.That(report, Does.Contain("Last 3 log lines:").And.Contain("line 9").And.Not.Contain("line 6"));
    }
}
