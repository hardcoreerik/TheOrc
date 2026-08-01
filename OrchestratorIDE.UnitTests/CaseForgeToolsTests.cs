// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class CaseForgeToolsTests
{
    [Test]
    public void RegistersBoundedLocalToolsAndPack()
    {
        var registry = new ToolRegistry(new ApprovalQueue { AutoApprove = true });
        CaseForgeTools.Register(registry, new Uri("http://NEWCOREPC:8788"), "test-token-012345");

        Assert.That(registry.GetRegisteredNames(), Is.EquivalentTo(new[]
        {
            "model3d_create", "model3d_status", "model3d_cancel", "model3d_open",
        }));
        Assert.Multiple(() =>
        {
            Assert.That(registry.TryGet("model3d_create", out var create) && create!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("model3d_status", out var status) && !status!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("model3d_cancel", out var cancel) && cancel!.RequiresApproval, Is.True);
            Assert.That(CampaignPackCatalog.Find(CampaignPackCatalog.CaseForgePackId,
                CampaignPackCatalog.CaseForgePackVersion), Is.Not.Null);
        });
    }

    [Test]
    public void RejectsPublicWorkerUrls()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        Assert.Throws<ArgumentException>(() =>
            CaseForgeTools.Register(registry, new Uri("https://example.com"), "test-token-012345"));
    }

    [Test]
    public void RejectsMissingTokenAndPublicWorkspace()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() =>
                CaseForgeTools.Register(registry, new Uri("http://localhost:8788")));
            Assert.Throws<ArgumentException>(() =>
                CaseForgeTools.Register(registry, new Uri("http://localhost:8788"), "test-token-012345",
                    new Uri("https://example.com")));
        });
    }

    [Test]
    public void RejectsMalformedJobIdBeforeSending()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        CaseForgeTools.Register(registry, new Uri("http://localhost:8788"), "test-token-012345");
        Assert.That(registry.TryGet("model3d_status", out var status), Is.True);
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await status!.Handler!(new() { ["job_id"] = "../not-a-job" }, CancellationToken.None));
    }
}
