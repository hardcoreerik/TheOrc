// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class KeyHoundAtlasToolsTests
{
    private const string Token = "test-keyhound-token-0123456789abcdef";

    [Test]
    public void RegistersBoundedLocalTools()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        KeyHoundAtlasTools.Register(registry, new Uri("http://localhost:8000"), Token);

        Assert.That(registry.GetRegisteredNames(), Is.EquivalentTo(new[]
        {
            "atlas_start", "atlas_graph", "atlas_expand", "atlas_evidence", "atlas_open",
        }));
        Assert.Multiple(() =>
        {
            Assert.That(registry.TryGet("atlas_start", out var start) && start!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("atlas_graph", out var graph) && !graph!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("atlas_expand", out var expand) && expand!.RequiresApproval, Is.True);
            Assert.That(registry.TryGet("atlas_evidence", out var evidence) && !evidence!.RequiresApproval, Is.True);
        });
    }

    [Test]
    public void RejectsMissingTokenAndPublicUrls()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        Assert.Multiple(() =>
        {
            Assert.Throws<ArgumentException>(() =>
                KeyHoundAtlasTools.Register(registry, new Uri("http://localhost:8000"), "short"));
            Assert.Throws<ArgumentException>(() =>
                KeyHoundAtlasTools.Register(registry, new Uri("https://example.com"), Token));
        });
    }

    [Test]
    public void RejectsMalformedIdentifiersBeforeSending()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        KeyHoundAtlasTools.Register(registry, new Uri("http://localhost:8000"), Token);
        Assert.That(registry.TryGet("atlas_graph", out var graph), Is.True);
        Assert.That(registry.TryGet("atlas_evidence", out var evidence), Is.True);
        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await graph!.Handler!(new() { ["run_id"] = "../bad" }, CancellationToken.None));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await evidence!.Handler!(new() { ["link_id"] = "../bad" }, CancellationToken.None));
        });
    }
}
