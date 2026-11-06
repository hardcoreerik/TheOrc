// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public class ListFilesToolTests
{
    // A Swarm run's workspace is <ws>/.orc/swarm/runs/<id>/staging: every parent folder up to
    // .orc starts with a dot. Tester runs kept seeing "(empty directory)" for it.
    [Test]
    public async Task ListFiles_InsideDotFolderPath_ListsFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-lf-" + Guid.NewGuid().ToString("N")[..8],
                                ".orc", "swarm", "runs", "r1", "staging");
        Directory.CreateDirectory(Path.Combine(root, "textstats"));
        File.WriteAllText(Path.Combine(root, "textstats", "stats.py"), "x = 1\n");
        File.WriteAllText(Path.Combine(root, "test_stats.py"), "y = 2\n");
        try
        {
            var reg = new ToolRegistry(new ApprovalQueue { AutoApprove = true });
            FileTools.Register(reg, root);
            foreach (var path in new[] { ".", "", "textstats" })
            {
                var args = path.Length == 0 ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["path"] = path };
                var result = await reg.ExecuteAsync(new ToolCall { Name = "list_files", Arguments = args }, CancellationToken.None);
                Assert.That(result, Does.Not.Contain("empty directory"), $"path='{path}': {result}");
            }
            var top = await reg.ExecuteAsync(new ToolCall { Name = "list_files", Arguments = new() { ["path"] = "." } }, CancellationToken.None);
            Assert.That(top, Does.Contain("test_stats.py").And.Contain("textstats/").And.Contain("stats.py"));
        }
        finally { try { Directory.Delete(Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", "..")), true); } catch { } }
    }

    // A write_file call with no "path" resolved to the workspace folder and failed with
    // "Access to the path '<workspace>' is denied" (seen on every write of a Gemma 4 Swarm run).
    [Test]
    public async Task WriteFile_WithoutPath_SaysPathIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-wf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var reg = new ToolRegistry(new ApprovalQueue { AutoApprove = true });
            FileTools.Register(reg, root);
            var noPath = await reg.ExecuteAsync(new ToolCall { Name = "write_file", Arguments = new() { ["content"] = "x = 1" } }, CancellationToken.None);
            Assert.That(noPath, Does.StartWith("[ERROR]").And.Contain("\"path\"").And.Contain("content"));
            var dotPath = await reg.ExecuteAsync(new ToolCall { Name = "write_file", Arguments = new() { ["path"] = ".", ["content"] = "x" } }, CancellationToken.None);
            Assert.That(dotPath, Does.StartWith("[ERROR]"));
            var ok = await reg.ExecuteAsync(new ToolCall { Name = "write_file", Arguments = new() { ["path"] = "a/b.py", ["content"] = "x = 1" } }, CancellationToken.None);
            Assert.That(ok, Does.StartWith("[OK]"));
            Assert.That(File.ReadAllText(Path.Combine(root, "a", "b.py")), Is.EqualTo("x = 1"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
