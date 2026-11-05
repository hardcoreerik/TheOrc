// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Debug › Start / Stop Recording (F12) on macOS, which records with /usr/sbin/screencapture.
/// A stand-in recorder script plays screencapture's part (writes the movie on Ctrl-C), so no
/// real screen is captured and no permission prompt appears.
/// </summary>
[TestFixture, NonParallelizable]
public sealed class MacScreenRecorderTests
{
    private string _dir = "";
    private string _savedPath = "";

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("macOS recorder (Windows/Linux keep the no-op stub).");
        _dir = Path.Combine(Path.GetTempPath(), "orc-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _savedPath = ScreenRecorder.ScreencapturePath;
        ScreenRecorder.DirectoryOverride = Path.Combine(_dir, "Recordings");
    }

    [TearDown]
    public void TearDown()
    {
        if (_dir.Length == 0) return;
        ScreenRecorder.ScreencapturePath = _savedPath;
        ScreenRecorder.DirectoryOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string StandIn(string body)
    {
        var path = Path.Combine(_dir, "screencapture");
        File.WriteAllText(path, $$"""
            #!/bin/bash
            printf '%s\n' "$@" > '{{_dir}}/args.txt'
            out="${@: -1}"
            {{body}}
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<string> WaitStopped(TaskCompletionSource<string> stopped)
    {
        var done = await Task.WhenAny(stopped.Task, Task.Delay(15_000));
        Assert.That(done, Is.SameAs(stopped.Task), "OnStopped never fired");
        return await stopped.Task;
    }

    [Test, CancelAfter(30_000)]
    public async Task Records_until_stopped_then_reports_the_saved_movie()
    {
        // Like screencapture -v: record until Ctrl-C (SIGINT), then write the movie and exit.
        ScreenRecorder.ScreencapturePath = StandIn("""
            trap 'echo movie > "$out"; exit 0' INT
            while true; do sleep 0.1; done
            """);
        using var recorder = new ScreenRecorder();
        var ticked = new TaskCompletionSource<string>();
        var stopped = new TaskCompletionSource<string>();
        recorder.OnTick += t => ticked.TrySetResult(t);
        recorder.OnStopped += p => stopped.TrySetResult(p);

        recorder.Start(new object());
        Assert.That(recorder.IsRecording, Is.True);
        Assert.That(await Task.WhenAny(ticked.Task, Task.Delay(5_000)), Is.SameAs(ticked.Task), "no OnTick");
        Assert.That(ticked.Task.Result, Does.Match(@"^\d\d:\d\d$"));

        recorder.Stop();
        var path = await WaitStopped(stopped);

        Assert.That(recorder.IsRecording, Is.False);
        Assert.That(Path.GetDirectoryName(path), Is.EqualTo(ScreenRecorder.RecordingsDirectory));
        Assert.That(Path.GetFileName(path), Does.Match(@"^recording_\d{8}_\d{6}\.mov$"));
        Assert.That(File.ReadAllText(path).Trim(), Is.EqualTo("movie"));
        // Whole screen, cursor and clicks shown, no shutter sound, then the file.
        Assert.That(File.ReadAllLines(Path.Combine(_dir, "args.txt")),
            Is.EqualTo(new[] { "-v", "-C", "-k", "-x", path }));
    }

    [Test, CancelAfter(30_000)]
    public async Task A_recorder_that_writes_nothing_reports_an_empty_path()
    {
        // What a refused Screen Recording permission looks like: exits without a movie.
        ScreenRecorder.ScreencapturePath = StandIn("exit 1");
        using var recorder = new ScreenRecorder();
        var stopped = new TaskCompletionSource<string>();
        recorder.OnStopped += p => stopped.TrySetResult(p);

        recorder.Start(new object());
        Assert.That(await WaitStopped(stopped), Is.Empty);
        Assert.That(recorder.IsRecording, Is.False);
    }

    [Test]
    public void Stop_without_a_recording_is_a_no_op() =>
        Assert.DoesNotThrow(() => new ScreenRecorder().Stop());
}
