// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using NUnit.Framework;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// scripts/make-mac-dev-app.sh: the TheOrc.app launcher that makes the Dock say "TheOrc"
/// instead of "dotnet". Builds throwaway bundles (not registered with Launch Services).
/// </summary>
[TestFixture]
public class MacDevAppScriptTests
{
    private string _dir = "";

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Ignore("macOS app bundle.");
        _dir = Path.Combine(Path.GetTempPath(), "orc-devapp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (_dir.Length > 0)
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "make-mac-dev-app.sh")))
                return dir.FullName;
        Assert.Fail("scripts/make-mac-dev-app.sh not found above the test output folder.");
        return "";
    }

    private static (int Exit, string Output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["THEORC_NO_REGISTER"] = "1";
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        Assert.That(p.WaitForExit(60_000), Is.True, "script timed out");
        return (p.ExitCode, output.Result + error.Result);
    }

    private static string Plist(string app, string key) =>
        Run("/usr/libexec/PlistBuddy", "-c", $"Print :{key}", Path.Combine(app, "Contents", "Info.plist"))
            .Output.Trim();

    // Use the build this test run already made (Debug or Release).
    private static string[] ConfigArgs() =>
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? ["--release", "--no-build"] : ["--no-build"];

    [Test]
    public void Builds_a_bundle_named_TheOrc_with_its_icon_and_a_launcher()
    {
        var app = Path.Combine(_dir, "TheOrc.app");
        var (exit, output) = Run(Path.Combine(Repo(), "scripts", "make-mac-dev-app.sh"), [.. ConfigArgs(), app]);
        Assert.That(exit, Is.EqualTo(0), output);

        Assert.Multiple(() =>
        {
            Assert.That(Plist(app, "CFBundleName"), Is.EqualTo("TheOrc"));
            Assert.That(Plist(app, "CFBundleExecutable"), Is.EqualTo("TheOrc"));
            Assert.That(Plist(app, "CFBundleIconFile"), Is.EqualTo("TheOrc"));
            Assert.That(File.Exists(Path.Combine(app, "Contents", "Resources", "TheOrc.icns")), Is.True);

            var launcher = Path.Combine(app, "Contents", "MacOS", "TheOrc");
            Assert.That(File.GetUnixFileMode(launcher).HasFlag(UnixFileMode.UserExecute), Is.True);
            var text = File.ReadAllText(launcher);
            Assert.That(text, Does.Contain("exec \"/").And.Contain("OrchestratorIDE.Avalonia.dll"),
                "absolute dotnet path: Finder launches don't get the shell's PATH");
        });
    }

    [Test, CancelAfter(600_000)]
    public void Standalone_bundle_is_self_contained_and_validly_signed_as_TheOrc()
    {
        var app = Path.Combine(_dir, "TheOrc.app");
        var (exit, output) = Run(Path.Combine(Repo(), "scripts", "make-mac-dev-app.sh"), "--standalone", app);
        Assert.That(exit, Is.EqualTo(0), output);

        var macos = Path.Combine(app, "Contents", "MacOS");
        var payload = Path.Combine(app, "Contents", "Resources", "app");
        Assert.Multiple(() =>
        {
            // Signed as its own program, so macOS asks for permissions on TheOrc's behalf.
            var verify = Run("/usr/bin/codesign", "--verify", "--deep", "--strict", app);
            Assert.That(verify.Exit, Is.EqualTo(0), verify.Output);
            Assert.That(Run("/usr/bin/codesign", "-dv", app).Output, Does.Contain("Identifier=local.dev.theorc"));

            // MacOS holds only code: the apphost and the symlink it follows to the app folder.
            Assert.That(Directory.GetFileSystemEntries(macos).Select(Path.GetFileName).Order(),
                Is.EqualTo(new[] { "OrchestratorIDE.Avalonia.dll", "TheOrc" }));
            Assert.That(new FileInfo(Path.Combine(macos, "OrchestratorIDE.Avalonia.dll")).LinkTarget,
                Is.EqualTo("../Resources/app/OrchestratorIDE.Avalonia.dll"));

            // Self-contained: the .NET runtime sits with the app (no SDK needed to run it).
            Assert.That(File.Exists(Path.Combine(payload, "libhostfxr.dylib")), Is.True);
            Assert.That(File.Exists(Path.Combine(payload, "libcoreclr.dylib")), Is.True);
            // Only this Mac's native libraries are kept.
            Assert.That(Directory.GetDirectories(Path.Combine(payload, "runtimes")).Select(Path.GetFileName),
                Is.EqualTo(new[] { "osx-arm64" }));
        });
    }

    [Test]
    public void Signing_with_a_certificate_that_is_not_in_the_keychain_fails_before_building()
    {
        var app = Path.Combine(_dir, "TheOrc.app");
        var (exit, output) = Run(Path.Combine(Repo(), "scripts", "make-mac-dev-app.sh"),
            "--standalone", "--sign", "No Such Certificate " + Guid.NewGuid().ToString("N"), app);
        Assert.That(exit, Is.EqualTo(1));
        Assert.That(output, Does.Contain("no code-signing certificate matching"));
        Assert.That(Directory.Exists(app), Is.False, "nothing is published or built");
    }

    [Test]
    public void Never_replaces_a_bundle_it_did_not_make()
    {
        var other = Path.Combine(_dir, "Other.app");
        Directory.CreateDirectory(Path.Combine(other, "Contents"));
        File.WriteAllText(Path.Combine(other, "Contents", "keep.txt"), "mine");

        var (exit, output) = Run(Path.Combine(Repo(), "scripts", "make-mac-dev-app.sh"), [.. ConfigArgs(), other]);
        Assert.That(exit, Is.Not.EqualTo(0));
        Assert.That(output, Does.Contain("not replacing"));
        Assert.That(File.Exists(Path.Combine(other, "Contents", "keep.txt")), Is.True);
    }
}
