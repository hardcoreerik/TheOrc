// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.CodeGraph;
using OrchestratorIDE.Services.CodeGraph.Data;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// One unreadable folder under the workspace (e.g. <c>~/.Trash</c> on macOS) used to abort
/// CodeGraph indexing with UnauthorizedAccessException. It is now skipped.
/// </summary>
[TestFixture]
public class RoslynIndexerInaccessibleFolderTests
{
    private string _root = "";
    private string _locked = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-codegraph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "namespace N; public class Alpha { public void Run() { } }");
        File.WriteAllText(Path.Combine(_root, "sub", "Gamma.cs"), "namespace N; public class Gamma { }");

        _locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(_locked);
        File.WriteAllText(Path.Combine(_locked, "Hidden.cs"), "namespace N; public class Hidden { }");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_locked, UnixFileMode.None);
    }

    [TearDown]
    public void TearDown()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(_locked))
            File.SetUnixFileMode(_locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task IndexAsync_skips_unreadable_folders_instead_of_failing()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Uses Unix permissions to make a folder unreadable.");
        if (Environment.UserName == "root")
            Assert.Ignore("root can read a mode-000 folder.");

        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var repo = new GraphRepository(store);

        await new RoslynIndexer(repo).IndexAsync("locked-folder-test", _root);

        var names = repo.GetNodesForProject("locked-folder-test").Select(n => n.Name).ToList();
        Assert.That(names, Does.Contain("Alpha"));
        Assert.That(names, Does.Contain("Gamma"));
        Assert.That(names, Does.Not.Contain("Hidden"));
    }
}
