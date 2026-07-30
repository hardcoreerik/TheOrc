// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Phase 0 of docs/NATIVE_BROWSER_AUTOMATION_SPEC.md: a capability-gated tool must be excluded
/// from ToolRegistry.GetForProfile's advertised list AND refuse to execute via ExecuteAsync when
/// its capability isn't available -- both with an explicit, human-readable reason, never a silent
/// omission. Uses a synthetic capability-gated tool since no real capability-gated tool exists yet
/// (Phase 1 adds the first one, browser automation) -- this locks down the plumbing itself.
/// </summary>
[TestFixture]
public sealed class NativeToolCapabilityTests
{
    // Reset in both SetUp and TearDown (grok-review follow-up): NativeToolCapabilities is
    // process-wide static state, so a SetUp-only reset would still leak into a test that runs
    // after one whose own TearDown was skipped (a prior test crashing mid-body), and a
    // TearDown-only reset leaves the FIRST test in a run depending on whatever state happened to
    // exist before it. Cheap to do both; only one is actually redundant on any given clean run.
    [SetUp]
    public void SetUp() => NativeToolCapabilities.ResetForTest();

    [TearDown]
    public void TearDown() => NativeToolCapabilities.ResetForTest();

    private static ModelProfile FullToolSetProfile() => new(
        ModelId: "test-model", Name: "Test Model", ContextTokens: 4096, NativeToolUse: true,
        Strengths: [], ToolSet: ToolSet.Full, PromptStyle: PromptStyle.Agent);

    private static ToolDefinition CapabilityGatedTool(NativeToolCapability capability) => new()
    {
        Name = "gated_tool",
        Description = "A synthetic tool requiring a capability, for Phase 0 plumbing tests.",
        RequiredCapability = capability,
        Handler = (_, _) => Task.FromResult("[OK] ran"),
    };

    [Test]
    public void MarkAvailable_ThenHas_ReturnsTrue()
    {
        NativeToolCapabilities.MarkAvailable(NativeToolCapability.BrowserAutomation);
        Assert.That(NativeToolCapabilities.Has(NativeToolCapability.BrowserAutomation), Is.True);
    }

    [Test]
    public void Has_ReturnsFalse_ForCapabilityNeverMarkedAvailable()
    {
        Assert.That(NativeToolCapabilities.Has(NativeToolCapability.BrowserAutomation), Is.False);
    }

    [Test]
    public void MarkUnavailable_RecordsReason_AndHasReturnsFalse()
    {
        NativeToolCapabilities.MarkAvailable(NativeToolCapability.BrowserAutomation);
        NativeToolCapabilities.MarkUnavailable(NativeToolCapability.BrowserAutomation,
            "Playwright browsers are not installed; run `playwright install`.");

        Assert.That(NativeToolCapabilities.Has(NativeToolCapability.BrowserAutomation), Is.False);
        Assert.That(NativeToolCapabilities.Reason(NativeToolCapability.BrowserAutomation),
            Does.Contain("playwright install"));
    }

    [Test]
    public void GetForProfile_ExcludesCapabilityGatedTool_WhenCapabilityUnavailable()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        registry.Register(CapabilityGatedTool(NativeToolCapability.BrowserAutomation));

        var tools = registry.GetForProfile(FullToolSetProfile());

        Assert.That(tools.Select(t => t.Name), Does.Not.Contain("gated_tool"));
    }

    [Test]
    public void GetForProfile_IncludesCapabilityGatedTool_WhenCapabilityAvailable()
    {
        NativeToolCapabilities.MarkAvailable(NativeToolCapability.BrowserAutomation);
        var registry = new ToolRegistry(new ApprovalQueue());
        registry.Register(CapabilityGatedTool(NativeToolCapability.BrowserAutomation));

        var tools = registry.GetForProfile(FullToolSetProfile());

        Assert.That(tools.Select(t => t.Name), Does.Contain("gated_tool"));
    }

    [Test]
    public void GetForProfile_IncludesUngatedTools_Unaffected()
    {
        var registry = new ToolRegistry(new ApprovalQueue());
        registry.Register(new ToolDefinition
        {
            Name = "plain_tool",
            Handler = (_, _) => Task.FromResult("[OK]"),
        });

        var tools = registry.GetForProfile(FullToolSetProfile());

        Assert.That(tools.Select(t => t.Name), Does.Contain("plain_tool"));
    }

    [Test]
    public async Task ExecuteAsync_RefusesExplicitly_WhenCapabilityUnavailable_EvenIfRegistered()
    {
        // Defense-in-depth case: the tool IS registered (a model could still emit a call for it,
        // e.g. from a stale prompt or hallucination) but GetForProfile would never have advertised
        // it. ExecuteAsync must refuse loudly with the recorded reason, not silently run it and
        // not fall back to the generic "tool not found" message (misleading -- it IS registered).
        NativeToolCapabilities.MarkUnavailable(NativeToolCapability.BrowserAutomation,
            "Playwright browsers are not installed.");
        var registry = new ToolRegistry(new ApprovalQueue());
        registry.Register(CapabilityGatedTool(NativeToolCapability.BrowserAutomation));

        var call = new ToolCall { Name = "gated_tool", Arguments = [] };
        var result = await registry.ExecuteAsync(call, CancellationToken.None);

        Assert.That(result, Does.StartWith("[UNAVAILABLE]"));
        Assert.That(result, Does.Contain("Playwright browsers are not installed."));
        Assert.That(call.Status, Is.EqualTo(ToolCallStatus.Failed));
    }

    [Test]
    public async Task ExecuteAsync_RunsNormally_WhenCapabilityAvailable()
    {
        NativeToolCapabilities.MarkAvailable(NativeToolCapability.BrowserAutomation);
        var registry = new ToolRegistry(new ApprovalQueue());
        registry.Register(CapabilityGatedTool(NativeToolCapability.BrowserAutomation));

        var call = new ToolCall { Name = "gated_tool", Arguments = [] };
        var result = await registry.ExecuteAsync(call, CancellationToken.None);

        Assert.That(result, Is.EqualTo("[OK] ran"));
        Assert.That(call.Status, Is.EqualTo(ToolCallStatus.Complete));
    }

    [Test]
    public void ToolResult_Subtypes_CarrySummaryThroughBaseType()
    {
        ToolResult text = new TextToolResult("plain summary");
        ToolResult artifact = new ArtifactToolResult("exported", "/tmp/out.pdf", "application/pdf");
        ToolResult screenshot = new ScreenshotToolResult("captured", "/tmp/shot.png", 1024, 768);

        Assert.That(text.Summary, Is.EqualTo("plain summary"));
        Assert.That(artifact.Summary, Is.EqualTo("exported"));
        Assert.That(screenshot.Summary, Is.EqualTo("captured"));
        Assert.That(((ArtifactToolResult)artifact).MimeType, Is.EqualTo("application/pdf"));
        Assert.That(((ScreenshotToolResult)screenshot).Width, Is.EqualTo(1024));
    }
}
