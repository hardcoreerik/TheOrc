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
    public void RejectsPublicUrls()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        Assert.Throws<ArgumentException>(() =>
            ArtForgeTools.Register(registry, new Uri("https://example.com"), "test-device-token-012345"));
    }

    [Test]
    public void RegistersWithNoTokenOnALocalHost()
    {
        // hardcoreerik's own Art Forge Studio/ComfyUI run unauthenticated -- a bearer token is
        // an optional extra, not a requirement, as long as the host is local (enforced above by
        // RejectsPublicUrls -- an unauthenticated call must never be able to reach a public URL).
        var registry = new ToolRegistry(new ApprovalQueue());
        Assert.DoesNotThrow(() =>
            ArtForgeTools.Register(registry, new Uri("http://localhost:8288")));
        Assert.That(registry.TryGet("image_create", out var create) && create is not null, Is.True);
    }

    [Test]
    public void RejectsNoTokenOnAPrivateLanHost()
    {
        // CodeRabbit review, PR #100, CWE-306: a private-IP host (unlike loopback) is reachable
        // by another machine on the same LAN -- tokenless access there is a real exposure, not
        // just this one machine's own hobby-project ComfyUI front end. IsLocal still admits
        // this host (RegistersWithNoTokenOnALocalHost above proves the loopback case is fine);
        // this proves the stricter no-token-requires-loopback boundary actually rejects the
        // broader IsLocal cases when unauthenticated.
        var registry = new ToolRegistry(new ApprovalQueue());
        Assert.Throws<ArgumentException>(() =>
            ArtForgeTools.Register(registry, new Uri("http://192.168.1.50:8288")));
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
