// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Headless.NUnit;
using NUnit.Framework;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// The explorer loads folders one level at a time, off the UI thread, instead of walking the
/// whole workspace (depth 5) synchronously in MainWindow's constructor.
/// </summary>
[TestFixture]
public class FileExplorerLazyLoadTests
{
    private string _root = "";
    private string _locked = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "orc-explorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "b.txt"), "");
        File.WriteAllText(Path.Combine(_root, "A.cs"), "");
        File.WriteAllText(Path.Combine(_root, ".hidden"), "");
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "");
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));

        // 7 levels deep: past the old eager walk's depth-5 cut-off.
        var deep = _root;
        for (var i = 1; i <= 7; i++) deep = Path.Combine(deep, i == 1 ? "sub" : $"d{i}");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "leaf.txt"), "");

        _locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(_locked);
        File.WriteAllText(Path.Combine(_locked, "secret.txt"), "");
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
    public void ListChildren_lists_one_level_folders_first_with_existing_filters()
    {
        var kids = FileExplorerPanel.ListChildren(_root);

        Assert.That(kids.Select(k => k.Name),
            Is.EqualTo(new[] { "locked", "sub", ".gitignore", "A.cs", "b.txt" }));

        // Child folders are not enumerated yet: just the placeholder that gives them an arrow.
        var sub = kids.Single(k => k.Name == "sub");
        Assert.That(sub.IsFile, Is.False);
        Assert.That(sub.Children, Has.Count.EqualTo(1));
        Assert.That(sub.Children[0].IsPlaceholder, Is.True);
        Assert.That(kids.Single(k => k.Name == "b.txt").Children, Is.Empty);
    }

    [Test]
    public void ListChildren_of_unreadable_folder_is_empty_not_an_exception()
    {
        if (OperatingSystem.IsWindows())
            Assert.Ignore("Uses Unix permissions to make a folder unreadable.");
        if (Environment.UserName == "root")
            Assert.Ignore("root can read a mode-000 folder.");

        Assert.That(FileExplorerPanel.ListChildren(_locked), Is.Empty);
    }

    [Test]
    public async Task EnsureChildrenLoaded_replaces_placeholder_and_reaches_past_old_depth_limit()
    {
        var node = FileExplorerPanel.ListChildren(_root).Single(k => k.Name == "sub");

        for (var level = 2; level <= 7; level++)
        {
            await FileExplorerPanel.EnsureChildrenLoadedAsync(node);
            Assert.That(node.Children.Any(c => c.IsPlaceholder), Is.False);
            node = node.Children.Single(c => c.Name == $"d{level}");
        }

        await FileExplorerPanel.EnsureChildrenLoadedAsync(node);
        Assert.That(node.Children.Select(c => c.Name), Is.EqualTo(new[] { "leaf.txt" }));

        // A second expand doesn't reload or duplicate children.
        await FileExplorerPanel.EnsureChildrenLoadedAsync(node);
        Assert.That(node.Children, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public void LoadWorkspace_returns_immediately_with_an_unloaded_root()
    {
        var panel = new FileExplorerPanel();
        panel.LoadWorkspace(_root);

        var root = panel.FileTree.ItemsSource!.Cast<FileNode>().Single();
        Assert.That(root.FullPath, Is.EqualTo(_root));
        Assert.That(root.IsFile, Is.False);
    }
}
