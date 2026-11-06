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
}
