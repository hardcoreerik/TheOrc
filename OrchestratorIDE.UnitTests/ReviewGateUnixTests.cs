// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using NUnit.Framework;
using OrchestratorIDE.Services.Swarm;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// The Reviewer Quality Gate on macOS/Linux, which does gate-review.ps1's steps without Windows
/// PowerShell. A stand-in <c>codex</c> (a bash script) records how it was called and writes a
/// verdict, so the flow is checked end to end except for the real model.
/// </summary>
[TestFixture]
public sealed class ReviewGateUnixTests
{
    private string _dir = "";
    private string Staging => Path.Combine(_dir, "staging");
    private string Workspace => Path.Combine(_dir, "workspace");
    private string ArgsLog => Path.Combine(_dir, "codex-args.txt");
    private string GitLog => Path.Combine(_dir, "codex-git.txt");

    [SetUp]
    public void SetUp()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Windows runs gate-review.ps1.");
        _dir = Path.Combine(Path.GetTempPath(), "orc-gate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Staging, "sub"));
        File.WriteAllText(Path.Combine(Staging, "Foo.cs"), "class Foo {}");
        File.WriteAllText(Path.Combine(Staging, "sub", "Bar.cs"), "class Bar {}");
        Directory.CreateDirectory(Workspace);
    }

    [TearDown]
    public void TearDown()
    {
        if (_dir.Length > 0)
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A stand-in codex: logs its args and the temp repo's commit, then runs <paramref name="body"/>.</summary>
    private string FakeCodex(string body)
    {
        var path = Path.Combine(_dir, "codex");
        File.WriteAllText(path, $"""
            #!/bin/bash
            printf '%s\n' "$@" > '{ArgsLog}'
            # args: exec --sandbox read-only -C <repo> --output-last-message <file> <prompt>
            repo="$5"; out="$7"
            git -C "$repo" log --format=%s --name-only > '{GitLog}' 2>&1
            cat > /dev/null   # stdin must already be closed, or this blocks
            {body}
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Test, CancelAfter(30_000)]
    public async Task Findings_come_back_parsed_and_saved_like_the_windows_script()
    {
        var codex = FakeCodex("""
            printf 'BLOCKER Foo.cs:3 — null deref\nMINOR sub/Bar.cs:1 — naming\n' > "$out"
            """);

        var result = await ReviewGateService.RunUnixAsync(Staging, Workspace, "async void paths", codex);

        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result!.Verdict, Is.EqualTo(ReviewGateService.GateVerdict.Blocker));
            Assert.That(result.Findings.Select(f => (f.Severity, f.Location)),
                Is.EqualTo(new[] { ("BLOCKER", "Foo.cs:3"), ("MINOR", "sub/Bar.cs:1") }));

            // Saved where the app reads it, in the script's format; the .last scratch file is gone.
            Assert.That(Path.GetDirectoryName(result.VerdictFilePath), Is.EqualTo(Path.Combine(Workspace, ".orc", "reviews")));
            var saved = File.ReadAllText(result.VerdictFilePath);
            Assert.That(saved, Does.StartWith("# Gate review — ").And.Contain($"Staging: {Staging}"));
            Assert.That(File.Exists(result.VerdictFilePath + ".last"), Is.False);
        });

        // Codex ran read-only on a commit holding every staged file, with the script's prompt.
        var args = File.ReadAllText(ArgsLog);
        Assert.That(args, Does.StartWith("exec\n--sandbox\nread-only\n-C\n"));
        Assert.That(args, Does.Contain("--output-last-message").And.Contain("Extra attention: async void paths"));
        var git = File.ReadAllText(GitLog);
        Assert.That(git, Does.Contain("swarm output").And.Contain("Foo.cs").And.Contain("sub/Bar.cs"));

        // The temp repo is removed afterwards.
        var repo = args.Split('\n')[4];
        Assert.That(repo, Does.Contain("orc-gate-"));
        Assert.That(Directory.Exists(repo), Is.False);
    }

    [Test, CancelAfter(30_000)]
    public async Task Clean_output_is_a_clean_verdict()
    {
        var codex = FakeCodex("""echo CLEAN > "$out" """);
        var result = await ReviewGateService.RunUnixAsync(Staging, Workspace, "", codex);
        Assert.That(result!.Verdict, Is.EqualTo(ReviewGateService.GateVerdict.Clean));
        Assert.That(result.Findings, Is.Empty);
        Assert.That(File.ReadAllText(ArgsLog), Does.Not.Contain("Extra attention"));
    }

    [Test, CancelAfter(30_000)]
    public void A_codex_that_hangs_times_out_instead_of_reporting_clean()
    {
        var codex = FakeCodex("sleep 25");
        Assert.ThrowsAsync<TimeoutException>(() =>
            ReviewGateService.RunUnixAsync(Staging, Workspace, "", codex, TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Missing_codex_or_nothing_staged_skips_the_gate()
    {
        Assert.That(await ReviewGateService.RunUnixAsync(Staging, Workspace, "", Path.Combine(_dir, "no-codex")), Is.Null);
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        Assert.That(await ReviewGateService.RunUnixAsync(empty, Workspace, "", FakeCodex("exit 0")), Is.Null);
    }

    [Test]
    public async Task Workspaces_without_the_gate_script_skip_it_as_on_windows()
    {
        Assert.That(await ReviewGateService.RunAsync(Staging, Workspace), Is.Null);
    }

    [Test]
    public void The_prompt_matches_gate_review_ps1_word_for_word()
    {
        string? script = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && script is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Tools", "gate-review.ps1");
            if (File.Exists(candidate)) script = candidate;
        }
        Assert.That(script, Is.Not.Null, "Tools/gate-review.ps1 not found above the test output folder");

        var hereString = Regex.Match(File.ReadAllText(script!).Replace("\r\n", "\n"),
            "\\$prompt = @\"\\n(.*?)\\n\"@", RegexOptions.Singleline).Groups[1].Value;
        var expected = hereString.Replace("$(if ($Focus) { \"Extra attention: $Focus\" })", "Extra attention: X");
        Assert.That(ReviewGateService.GatePrompt("X"), Is.EqualTo(expected));
    }
}
