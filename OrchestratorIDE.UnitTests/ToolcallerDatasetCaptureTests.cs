// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Swarm;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ToolcallerDatasetCaptureTests
{
    private readonly List<string> _tempDirs = [];
    private bool _originalIsEnabled;

    [SetUp]
    public void SetUp()
    {
        _originalIsEnabled = ToolcallerDatasetCapture.IsEnabled;
        // Tests exercise the enabled-capture behavior explicitly; the production default is
        // now off (opt-in), so force it on here rather than relying on that default.
        ToolcallerDatasetCapture.IsEnabled = true;
    }

    [TearDown]
    public void TearDown()
    {
        ToolcallerDatasetCapture.IsEnabled = _originalIsEnabled;
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
        _tempDirs.Clear();
    }

    [Test]
    public async Task StageCallAsync_WritesCaptureMatchingSchemaShape()
    {
        var stagingDir = NewTempDir();
        var workspaceRoot = NewTempDir();
        var task = new SwarmTask { Title = "Write config", Description = "Create the approved config file.", Role = SwarmWorkerRole.Coder };
        var call = new ToolCall { Name = "write_file", Arguments = new() { ["path"] = "config/example.json", ["content"] = "{}" } };
        var availableTools = new List<ToolDefinition>
        {
            new() { Name = "write_file", Description = "Write content to a file.", Parameters = new() },
            new() { Name = "read_file", Description = "Read a file.", Parameters = new() },
        };

        await ToolcallerDatasetCapture.StageCallAsync(
            "20260703_120000", task, "qwen2.5-coder:14b", call, availableTools, workspaceRoot, stagingDir);

        var file = Directory.GetFiles(stagingDir, "toolcaller_capture_*.json").Single();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("schema_version").GetString(), Is.EqualTo("toolcaller-v0"));
            Assert.That(root.GetProperty("example_id").GetString(), Does.StartWith("tc_20260703_120000_"));
            Assert.That(root.GetProperty("lineage_group_id").GetString(), Is.EqualTo(root.GetProperty("example_id").GetString()));
            Assert.That(root.GetProperty("role").GetString(), Is.EqualTo("coder"));
            Assert.That(root.GetProperty("request").GetString(), Is.EqualTo("Create the approved config file."));
            Assert.That(root.GetProperty("available_tools").EnumerateArray().Select(e => e.GetString()),
                Is.EquivalentTo(new[] { "write_file", "read_file" }));
            Assert.That(root.GetProperty("approval_state").GetString(), Is.EqualTo("approved"));

            var expected = root.GetProperty("expected");
            Assert.That(expected.GetProperty("decision").GetString(), Is.EqualTo("call"));
            Assert.That(expected.GetProperty("tool").GetString(), Is.EqualTo("write_file"));
            Assert.That(expected.GetProperty("arguments").GetProperty("path").GetString(), Is.EqualTo("config/example.json"));

            var policy = root.GetProperty("policy_outcome");
            Assert.That(policy.GetProperty("evaluated").GetBoolean(), Is.True);
            Assert.That(policy.GetProperty("risk_level").GetString(), Is.EqualTo("write_workspace"));
            Assert.That(policy.GetProperty("policy_gap_tool").GetBoolean(), Is.False);

            Assert.That(root.GetProperty("review_status").GetString(), Is.EqualTo("pending"));
            Assert.That(root.GetProperty("split").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task StageCallAsync_FlagsPolicyGapTool_ForGrepCodeAndAskUser()
    {
        var stagingDir = NewTempDir();
        var workspaceRoot = NewTempDir();
        var task = new SwarmTask { Title = "Find usages", Description = "Find usages of Foo.", Role = SwarmWorkerRole.Researcher };
        var call = new ToolCall { Name = "grep_code", Arguments = new() { ["pattern"] = "Foo" } };
        var availableTools = new List<ToolDefinition> { new() { Name = "grep_code", Description = "Search.", Parameters = new() } };

        await ToolcallerDatasetCapture.StageCallAsync(
            "20260703_130000", task, "qwen2.5-coder:14b", call, availableTools, workspaceRoot, stagingDir);

        var file = Directory.GetFiles(stagingDir, "toolcaller_capture_*.json").Single();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));

        Assert.That(doc.RootElement.GetProperty("policy_outcome").GetProperty("policy_gap_tool").GetBoolean(), Is.True);
    }

    [Test]
    public async Task StageNoToolAsync_WritesNoToolDecision_WithNullPolicyOutcome()
    {
        var stagingDir = NewTempDir();
        var task = new SwarmTask { Title = "Explain", Description = "Explain what this function does.", Role = SwarmWorkerRole.Tester };
        var availableTools = new List<ToolDefinition> { new() { Name = "read_file", Description = "Read.", Parameters = new() } };

        await ToolcallerDatasetCapture.StageNoToolAsync(
            "20260703_140000", task, "qwen2.5-coder:14b",
            "This function validates the input and returns a normalized result.",
            availableTools, stagingDir);

        var file = Directory.GetFiles(stagingDir, "toolcaller_capture_*.json").Single();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("expected").GetProperty("decision").GetString(), Is.EqualTo("no_tool"));
            Assert.That(root.GetProperty("expected").GetProperty("tool").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("policy_outcome").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task StageNoToolAsync_SkipsTrivialContent()
    {
        var stagingDir = NewTempDir();
        var task = new SwarmTask { Title = "x", Description = "x", Role = SwarmWorkerRole.Tester };

        await ToolcallerDatasetCapture.StageNoToolAsync(
            "20260703_150000", task, "qwen2.5-coder:14b", "OK.", [], stagingDir);

        Assert.That(Directory.Exists(stagingDir) && Directory.GetFiles(stagingDir).Length > 0, Is.False);
    }

    [Test]
    public async Task StageCallAsync_DoesNothing_WhenDisabled()
    {
        ToolcallerDatasetCapture.IsEnabled = false;
        var stagingDir = NewTempDir();
        var task = new SwarmTask { Title = "x", Description = "x", Role = SwarmWorkerRole.Coder };
        var call = new ToolCall { Name = "read_file", Arguments = new() { ["path"] = "a.txt" } };

        await ToolcallerDatasetCapture.StageCallAsync(
            "20260703_160000", task, "m", call, [], NewTempDir(), stagingDir);

        Assert.That(Directory.Exists(stagingDir) && Directory.GetFiles(stagingDir).Length > 0, Is.False);
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orc-toolcaller-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // ── v1 chat capture (StageChatDecisionAsync) ────────────────────────────────

    [Test]
    public async Task StageChatDecisionAsync_Call_WritesV1CaptureWithChatRole()
    {
        var stagingDir = NewTempDir();
        var workspaceRoot = NewTempDir();
        var call = new ToolCall { Name = "write_file", Arguments = new() { ["path"] = "notes.md", ["content"] = "hi" } };
        var availableTools = new List<ToolDefinition>
        {
            new() { Name = "write_file", Description = "Write.", Parameters = new() },
            new() { Name = "web_search", Description = "Search the web.", Parameters = new() },
        };

        await ToolcallerDatasetCapture.StageChatDecisionAsync(
            "chat_20260713_120000", "qwen2.5:7b", "Save these notes to a file.",
            availableTools, [call], workspaceRoot, stagingDir);

        var file = Directory.GetFiles(stagingDir, "toolcaller_v1_chat_*.json").Single();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("schema_version").GetString(), Is.EqualTo("toolcaller-v1"));
            Assert.That(root.GetProperty("example_id").GetString(), Does.StartWith("tcv1_chat_20260713_120000_"));
            Assert.That(root.GetProperty("role").GetString(), Is.EqualTo("chat"));
            Assert.That(root.GetProperty("request").GetString(), Is.EqualTo("Save these notes to a file."));
            Assert.That(root.GetProperty("available_tools").EnumerateArray().Select(e => e.GetString()),
                Is.EquivalentTo(new[] { "write_file", "web_search" }));
            Assert.That(root.GetProperty("approval_state").GetString(), Is.EqualTo("n/a"));

            var expected = root.GetProperty("expected");
            Assert.That(expected.GetProperty("decision").GetString(), Is.EqualTo("call"));
            Assert.That(expected.GetProperty("tool").GetString(), Is.EqualTo("write_file"));

            var policy = root.GetProperty("policy_outcome");
            Assert.That(policy.GetProperty("evaluated").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task StageChatDecisionAsync_MultipleCalls_WritesOneCapturePerCall()
    {
        var stagingDir = NewTempDir();
        var calls = new List<ToolCall>
        {
            new() { Name = "read_file", Arguments = new() { ["path"] = "a.txt" } },
            new() { Name = "grep_code", Arguments = new() { ["pattern"] = "foo" } },
        };

        await ToolcallerDatasetCapture.StageChatDecisionAsync(
            "chat_run", "m", "Read a.txt and search for foo.", [], calls, null, stagingDir);

        Assert.That(Directory.GetFiles(stagingDir, "toolcaller_v1_chat_*.json"), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task StageChatDecisionAsync_NoTool_WritesNoToolDecision()
    {
        var stagingDir = NewTempDir();

        await ToolcallerDatasetCapture.StageChatDecisionAsync(
            "chat_run", "m", "What does a NullReferenceException mean in C#?",
            [], [], null, stagingDir);

        var file = Directory.GetFiles(stagingDir, "toolcaller_v1_chat_*.json").Single();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("expected").GetProperty("decision").GetString(), Is.EqualTo("no_tool"));
            Assert.That(root.GetProperty("policy_outcome").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task StageChatDecisionAsync_SkipsTrivialRequest()
    {
        var stagingDir = NewTempDir();

        await ToolcallerDatasetCapture.StageChatDecisionAsync(
            "chat_run", "m", "ok", [], [], null, stagingDir);

        Assert.That(Directory.Exists(stagingDir) && Directory.GetFiles(stagingDir).Length > 0, Is.False);
    }

    [Test]
    public async Task StageChatDecisionAsync_DoesNothing_WhenDisabled()
    {
        ToolcallerDatasetCapture.IsEnabled = false;
        var stagingDir = NewTempDir();

        await ToolcallerDatasetCapture.StageChatDecisionAsync(
            "chat_run", "m", "A perfectly long enough request to pass the triviality filter.",
            [], [], null, stagingDir);

        Assert.That(Directory.Exists(stagingDir) && Directory.GetFiles(stagingDir).Length > 0, Is.False);
    }

    [Test]
    public async Task StageChatDecisionAsync_NeverWritesToV0StagingConvention()
    {
        // v0 and v1 must be trivially distinguishable by filename alone, even without
        // opening the file -- an exporter scanning a shared directory must never mix them.
        var stagingDir = NewTempDir();

        await ToolcallerDatasetCapture.StageChatDecisionAsync(
            "chat_run", "m", "A perfectly long enough request to pass the triviality filter.",
            [], [], null, stagingDir);

        var files = Directory.GetFiles(stagingDir);
        Assert.That(files, Has.All.Matches<string>(f => Path.GetFileName(f).StartsWith("toolcaller_v1_chat_")));
        Assert.That(files, Has.None.Matches<string>(f => Path.GetFileName(f).StartsWith("toolcaller_capture_")));
    }
}
