// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class SkillLoaderTests
{
    private readonly List<string> _tempRoots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _tempRoots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best-effort temp cleanup on Windows */ }
        }
        _tempRoots.Clear();
    }

    [Test]
    public async Task ScanAndLoadAllAsync_RegistersEveryValidToolFromAManifest()
    {
        var root = NewTempRoot();
        WriteManifest(root, "example", """
        {
          "name": "example",
          "base_url_default": "http://127.0.0.1:9999",
          "tools": [
            {
              "name": "example_health",
              "description": "Check liveness.",
              "method": "GET",
              "path": "/health",
              "requires_approval": false
            },
            {
              "name": "example_create",
              "description": "Create a thing.",
              "method": "POST",
              "path": "/v1/things",
              "params": [
                { "name": "kind", "type": "string", "description": "thing kind", "required": true }
              ]
            }
          ]
        }
        """);

        var registry = new ToolRegistry(new ApprovalQueue());
        var results = await SkillLoader.ScanAndLoadAllAsync(registry, root);

        Assert.Multiple(() =>
        {
            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Ok, Is.True);
            Assert.That(registry.TryGet("example_health", out var health) && !health!.RequiresApproval, Is.True);
            // requires_approval omitted -> defaults true (fail-toward-caution).
            Assert.That(registry.TryGet("example_create", out var create) && create!.RequiresApproval, Is.True);
            Assert.That(create!.Required, Is.EquivalentTo(new[] { "kind" }));
        });
    }

    [Test]
    public async Task ScanAndLoadAllAsync_SkipsInvalidEntriesButLoadsTheRestOfTheManifest()
    {
        var root = NewTempRoot();
        WriteManifest(root, "partial", """
        {
          "name": "partial",
          "base_url_default": "http://127.0.0.1:9999",
          "tools": [
            { "name": "", "description": "missing a name", "method": "GET", "path": "/x" },
            { "name": "partial_ok", "description": "This one is fine.", "method": "GET", "path": "/ok" }
          ]
        }
        """);

        var registry = new ToolRegistry(new ApprovalQueue());
        var results = await SkillLoader.ScanAndLoadAllAsync(registry, root);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Ok, Is.True);
            Assert.That(registry.TryGet("partial_ok", out var ok) && ok is not null, Is.True);
            Assert.That(registry.GetRegisteredNames(), Does.Not.Contain(""));
        });
    }

    [Test]
    public async Task ScanAndLoadAllAsync_RejectsAPublicBaseUrl()
    {
        var root = NewTempRoot();
        WriteManifest(root, "public", """
        {
          "name": "public",
          "base_url_default": "https://example.com",
          "tools": [
            { "name": "public_call", "description": "Should never register.", "method": "GET", "path": "/x" }
          ]
        }
        """);

        var registry = new ToolRegistry(new ApprovalQueue());
        var results = await SkillLoader.ScanAndLoadAllAsync(registry, root);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Ok, Is.False);
            Assert.That(registry.TryGet("public_call", out _), Is.False);
        });
    }

    [Test]
    public async Task ScanAndLoadAllAsync_RegistersAPathTemplatedEntryWithItsPathParamDeclared()
    {
        // docs/NATIVE_RUNTIME_V2_SPEC.md Phase E: a declared path like "/v1/jobs/{job_id}" must
        // register cleanly, with the path-param name showing up as a normal declared/required
        // param -- registration itself doesn't care that the token is embedded in the path
        // string rather than the query/body; substitution only happens at invoke time.
        var root = NewTempRoot();
        WriteManifest(root, "pathparam", """
        {
          "name": "pathparam",
          "base_url_default": "http://127.0.0.1:9999",
          "tools": [
            {
              "name": "pathparam_status",
              "description": "Get status by id.",
              "method": "GET",
              "path": "/v1/jobs/{job_id}",
              "params": [
                { "name": "job_id", "type": "string", "description": "job id", "required": true }
              ],
              "requires_approval": false
            }
          ]
        }
        """);

        var registry = new ToolRegistry(new ApprovalQueue());
        var results = await SkillLoader.ScanAndLoadAllAsync(registry, root);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Ok, Is.True);
            Assert.That(registry.TryGet("pathparam_status", out var tool) && tool is not null, Is.True);
            Assert.That(registry.TryGet("pathparam_status", out var tool2) ? tool2!.Required : [],
                Is.EquivalentTo(new[] { "job_id" }));
        });
    }

    [Test]
    public async Task InvokingAPathTemplatedEntry_WithoutItsPathArgument_ThrowsBeforeAnyHttpCall()
    {
        // Port 9999 has no listener in this test process -- if the missing-argument check didn't
        // fire first, this would surface as a connection-refused exception, not ArgumentException.
        // Asserting the specific exception type proves the failure happens synchronously before
        // any HTTP call is attempted (no live-HTTP dependency in this test).
        var root = NewTempRoot();
        WriteManifest(root, "pathparam2", """
        {
          "name": "pathparam2",
          "base_url_default": "http://127.0.0.1:9999",
          "tools": [
            {
              "name": "pathparam2_status",
              "description": "Get status by id.",
              "method": "GET",
              "path": "/v1/jobs/{job_id}",
              "params": [
                { "name": "job_id", "type": "string", "description": "job id", "required": true }
              ],
              "requires_approval": false
            }
          ]
        }
        """);

        var registry = new ToolRegistry(new ApprovalQueue());
        await SkillLoader.ScanAndLoadAllAsync(registry, root);
        registry.TryGet("pathparam2_status", out var tool);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await tool!.Handler!(new Dictionary<string, object?>(), CancellationToken.None));
    }

    [Test]
    public async Task ScanAndLoadAllAsync_ReturnsEmptyWhenNoSkillsDirectoryExists()
    {
        var root = NewTempRoot();
        var registry = new ToolRegistry(new ApprovalQueue());

        var results = await SkillLoader.ScanAndLoadAllAsync(registry, root);

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task ScanAndLoadAllAsync_ReportsInvalidJsonWithoutThrowing()
    {
        var root = NewTempRoot();
        WriteManifest(root, "broken", "{ not valid json");

        var registry = new ToolRegistry(new ApprovalQueue());
        var results = await SkillLoader.ScanAndLoadAllAsync(registry, root);

        Assert.Multiple(() =>
        {
            Assert.That(results[0].Ok, Is.False);
            Assert.That(results[0].Error, Does.Contain("Invalid JSON"));
        });
    }

    private void WriteManifest(string workspaceRoot, string skillName, string json)
    {
        var dir = Path.Combine(workspaceRoot, ".orc", "skills", skillName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tools.json"), json);
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-skill-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }
}
