// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Swarm;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// ToolcallerService is the runtime client for the trained theorc-toolcaller
/// specialist. Two contracts matter:
///  1. BuildSystemPrompt must serialize (role, tools) EXACTLY like
///     export_toolcaller_dataset.py's SYSTEM_TEMPLATE + render_tools_block —
///     the model was trained on that byte pattern and silently degrades on drift.
///  2. ParseDecision/ToToolCall must never let an invented tool or malformed
///     decision reach the execution loop.
/// </summary>
[TestFixture]
public sealed class ToolcallerServiceTests
{
    private static List<ToolDefinition> SampleTools() =>
    [
        new()
        {
            Name        = "read_file",
            Description = "Read the contents of a file.",
            Parameters  = new() { ["path"] = new ToolParameter("string", "File path relative to workspace root, or absolute.") },
            Required    = ["path"],
        },
        new()
        {
            Name        = "list_files",
            Description = "List files in a directory.",
            Parameters  = [],
            Required    = [],
        },
    ];

    // ── Prompt fidelity ─────────────────────────────────────────────────────

    [Test]
    public void BuildSystemPrompt_MatchesTrainingSerialization()
    {
        var prompt = ToolcallerService.BuildSystemPrompt(SwarmWorkerRole.UIDeveloper, SampleTools());

        // Header and role token exactly as the exporter writes them.
        Assert.That(prompt, Does.StartWith("You are theorc-toolcaller, TheOrc's tool-proposal specialist."));
        Assert.That(prompt, Does.Contain("Role: ui_developer"));

        // Tools block: exporter's "- name: desc" + 4-space parameter lines with
        // "(type, required)" marker, and the "(no parameters)" placeholder.
        Assert.That(prompt, Does.Contain("- read_file: Read the contents of a file."));
        Assert.That(prompt, Does.Contain("    - path (string, required): File path relative to workspace root, or absolute."));
        Assert.That(prompt.Replace("\r\n", "\n"),
                    Does.Contain("- list_files: List files in a directory.\n    (no parameters)"));

        // Decision-shape contract lines the model was trained against.
        Assert.That(prompt, Does.Contain("\"decision\": \"call\""));
        Assert.That(prompt, Does.Contain("\"decision\": \"clarify\", \"reason_code\": \"<missing_required_argument|ambiguous_target|ambiguous_intent>\""));
        Assert.That(prompt, Does.Contain("You propose; you never execute."));
    }

    [TestCase(SwarmWorkerRole.Researcher,  "researcher")]
    [TestCase(SwarmWorkerRole.Coder,       "coder")]
    [TestCase(SwarmWorkerRole.UIDeveloper, "ui_developer")]
    [TestCase(SwarmWorkerRole.Tester,      "tester")]
    public void BuildSystemPrompt_UsesTrainingRoleVocabulary(SwarmWorkerRole role, string token)
    {
        var prompt = ToolcallerService.BuildSystemPrompt(role, SampleTools());
        Assert.That(prompt, Does.Contain($"Role: {token}"));
    }

    // ── Decision parsing ────────────────────────────────────────────────────

    [Test]
    public void ParseDecision_ValidCall_ExtractsToolAndArguments()
    {
        var d = ToolcallerService.ParseDecision(
            """{"decision": "call", "tool": "read_file", "arguments": {"path": "src/app.cs"}}""");

        Assert.That(d, Is.Not.Null);
        Assert.That(d!.Kind, Is.EqualTo("call"));
        Assert.That(d.Tool, Is.EqualTo("read_file"));
        Assert.That(d.Arguments!["path"], Is.EqualTo("src/app.cs"));
    }

    [Test]
    public void ParseDecision_ClarifyWithReasonCode_Parses()
    {
        var d = ToolcallerService.ParseDecision(
            """{"decision": "clarify", "reason_code": "missing_required_argument"}""");

        Assert.That(d, Is.Not.Null);
        Assert.That(d!.Kind, Is.EqualTo("clarify"));
        Assert.That(d.ReasonCode, Is.EqualTo("missing_required_argument"));
    }

    [Test]
    public void ParseDecision_JsonEmbeddedInProse_StillExtracts()
    {
        var d = ToolcallerService.ParseDecision(
            """Sure! Here is my decision: {"decision": "no_tool"} — hope that helps.""");

        Assert.That(d, Is.Not.Null);
        Assert.That(d!.Kind, Is.EqualTo("no_tool"));
    }

    [TestCase("")]
    [TestCase("no json here at all")]
    [TestCase("""{"decision": "execute_everything"}""")]   // invented decision kind
    [TestCase("""{"tool": "read_file"}""")]                // missing decision field
    public void ParseDecision_Garbage_ReturnsNull(string raw)
        => Assert.That(ToolcallerService.ParseDecision(raw), Is.Null);

    // ── ToToolCall safety ───────────────────────────────────────────────────

    [Test]
    public void ToToolCall_InventedTool_IsRejected()
    {
        var d = ToolcallerService.ParseDecision(
            """{"decision": "call", "tool": "format_disk", "arguments": {}}""");
        Assert.That(d, Is.Not.Null);
        Assert.That(ToolcallerService.ToToolCall(d!, SampleTools()), Is.Null);
    }

    [Test]
    public void ToToolCall_NonCallDecision_IsNull()
    {
        var d = ToolcallerService.ParseDecision("""{"decision": "no_tool"}""");
        Assert.That(ToolcallerService.ToToolCall(d!, SampleTools()), Is.Null);
    }

    [Test]
    public void ToToolCall_ValidCall_MarksTextFormatAndRepairProvenance()
    {
        var d = ToolcallerService.ParseDecision(
            """{"decision": "call", "tool": "read_file", "arguments": {"path": "a.txt"}}""");
        var call = ToolcallerService.ToToolCall(d!, SampleTools());

        Assert.That(call, Is.Not.Null);
        Assert.That(call!.Name, Is.EqualTo("read_file"));
        Assert.That(call.IsTextFormat, Is.True);
        Assert.That(call.ExplainWhy, Does.Contain("repair lane"));
    }

    // ── Unknown-tool gap (found 2026-07-21): live tools outside the trained v0 set ─────
    // (fetch_url, get_outline -- part of the capture-only v1 inventory, never trained or
    // evaluated) must never reach the model's prompt, and must never execute even if a
    // decision somehow names one anyway.

    private static ToolDefinition FetchUrlTool() => new()
    {
        Name        = "fetch_url",
        Description = "Fetch the contents of a URL.",
        Parameters  = new() { ["url"] = new ToolParameter("string", "The URL to fetch.") },
        Required    = ["url"],
    };

    [Test]
    public void FilterToKnownTools_DropsToolsOutsideTrainedSet()
    {
        var tools = SampleTools().Append(FetchUrlTool()).ToList();
        var known = ToolcallerService.FilterToKnownTools(tools);

        Assert.That(known.Select(t => t.Name), Is.EquivalentTo(new[] { "read_file", "list_files" }));
    }

    [Test]
    public void FilterToKnownTools_AllToolsUnknown_ReturnsEmpty()
    {
        var known = ToolcallerService.FilterToKnownTools([FetchUrlTool()]);
        Assert.That(known, Is.Empty);
    }

    [Test]
    public void BuildSystemPrompt_NeverRendersAToolOutsideTheTrainedSet()
    {
        var tools = SampleTools().Append(FetchUrlTool()).ToList();
        var prompt = ToolcallerService.BuildSystemPrompt(
            SwarmWorkerRole.Researcher, ToolcallerService.FilterToKnownTools(tools));

        Assert.That(prompt, Does.Not.Contain("fetch_url"));
    }

    [Test]
    public void ToToolCall_ToolOutsideTrainedSet_IsRejectedEvenWhenLiveAndPresent()
    {
        // Simulates a decision that somehow names fetch_url despite ProposeAsync never
        // showing it to the model -- the defense-in-depth layer, not the primary guard.
        var d = ToolcallerService.ParseDecision(
            """{"decision": "call", "tool": "fetch_url", "arguments": {"url": "https://example.com"}}""");
        var liveTools = SampleTools().Append(FetchUrlTool()).ToList(); // fetch_url IS live/real here

        Assert.That(d, Is.Not.Null);
        Assert.That(ToolcallerService.ToToolCall(d!, liveTools), Is.Null);
    }
}
