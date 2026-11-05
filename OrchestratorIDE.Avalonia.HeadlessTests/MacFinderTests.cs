// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Tools;
using OrchestratorIDE.UI;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>"Show in Explorer" on macOS (Finder) and run_tests' Python command.</summary>
[TestFixture]
public class MacFinderTests
{
    [Test]
    public void Files_are_revealed_and_folders_are_opened() => Assert.Multiple(() =>
    {
        Assert.That(MacFinder.ArgsFor("/w/a.cs", isFile: true, isDirectory: false), Is.EqualTo(new[] { "-R", "/w/a.cs" }));
        Assert.That(MacFinder.ArgsFor("/w", isFile: false, isDirectory: true), Is.EqualTo(new[] { "/w" }));
        Assert.That(MacFinder.ArgsFor("/missing", isFile: false, isDirectory: false), Is.Null);
    });

    [Test]
    public void Reveal_is_a_safe_no_op_for_missing_or_empty_paths()
    {
        Assert.DoesNotThrow(() => MacFinder.Reveal(""));
        Assert.DoesNotThrow(() => MacFinder.Reveal("/definitely/not/here/" + Guid.NewGuid()));
    }

    [Test]
    public void Run_tests_uses_python3_off_windows_and_python_on_windows() =>
        Assert.That(TestTools.PythonCommand, Is.EqualTo(OperatingSystem.IsWindows() ? "python" : "python3"));
}
