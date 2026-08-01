// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ArtForgeToolsTests
{
    [Test]
    public void RegistersBoundedLocalTools()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        ArtForgeTools.Register(registry, new Uri("http://localhost:8288"), "test-device-token-012345");

        Assert.That(registry.GetRegisteredNames(), Is.EquivalentTo(new[]
        {
            "image_create", "image_status", "image_gallery", "image_open",
        }));
        Assert.Multiple(() =>
        {
            Assert.That(registry.TryGet("image_create", out var create) && create!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("image_status", out var status) && !status!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("image_gallery", out var gallery) && !gallery!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("image_open", out var open) && open!.RequiresApproval, Is.True);
        });
    }

    [Test]
    public void RejectsMissingTokenAndPublicUrls()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() =>
                ArtForgeTools.Register(registry, new Uri("http://localhost:8288"), ""));
            Assert.Throws<ArgumentException>(() =>
                ArtForgeTools.Register(registry, new Uri("https://example.com"), "test-device-token-012345"));
        });
    }

    [Test]
    public void RejectsMalformedJobIdBeforeSending()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        ArtForgeTools.Register(registry, new Uri("http://localhost:8288"), "test-device-token-012345");
        Assert.That(registry.TryGet("image_status", out var status), Is.True);
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await status!.Handler!(new() { ["job_id"] = "../not-a-job" }, CancellationToken.None));
    }
}
