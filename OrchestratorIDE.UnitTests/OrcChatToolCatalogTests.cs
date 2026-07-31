// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Research;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public class OrcChatToolCatalogTests
{
    private readonly Dictionary<string, string?> _savedEnvironment = new();

    [SetUp]
    public void SetUp()
    {
        foreach (var name in new[]
                 {
                     "THEORC_CASEFORGE_URL", "THEORC_CASEFORGE_TOKEN", "THEORC_CASEFORGE_WORKSPACE_URL",
                     "THEORC_ARTFORGE_URL", "THEORC_ARTFORGE_TOKEN", "THEORC_ARTFORGE_WORKSPACE_URL",
                     "THEORC_KEYHOUND_URL", "THEORC_KEYHOUND_TOKEN", "THEORC_KEYHOUND_WORKSPACE_URL",
                 })
        {
            _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var (name, value) in _savedEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        _savedEnvironment.Clear();
    }

    [Test]
    public void CreateWorkspaceTools_exposesTheTopTenChatTools()
    {
        var root = TestContext.CurrentContext.WorkDirectory;
        var tools = OrcChatToolCatalog.CreateWorkspaceTools(root);
        var names = tools.Select(t => t.Name).ToList();

        Assert.That(names, Is.EquivalentTo(OrcChatToolCatalog.TopToolNames));
    }

    [Test]
    public void BuildReactInstructions_mentionsEveryToolName()
    {
        var root = TestContext.CurrentContext.WorkDirectory;
        var tools = OrcChatToolCatalog.CreateWorkspaceTools(root);
        var prompt = OrcChatToolCatalog.BuildReactInstructions(tools);

        foreach (var name in OrcChatToolCatalog.TopToolNames)
            Assert.That(prompt, Does.Contain(name));
    }

    [Test]
    public void CreateWorkspaceTools_addsCaseForgeOnlyWhenConfigurationIsValid()
    {
        var root = TestContext.CurrentContext.WorkDirectory;
        Environment.SetEnvironmentVariable("THEORC_CASEFORGE_URL", "http://localhost:8788");
        Environment.SetEnvironmentVariable("THEORC_CASEFORGE_TOKEN", "test-token-012345");

        var names = OrcChatToolCatalog.CreateWorkspaceTools(root).Select(t => t.Name).ToList();

        Assert.That(names, Does.Contain("model3d_create"));
        Assert.That(names, Does.Contain("model3d_status"));
    }

    [Test]
    public void CreateWorkspaceTools_ignoresMalformedOptionalCaseForgeWorkspaceUrl()
    {
        var root = TestContext.CurrentContext.WorkDirectory;
        Environment.SetEnvironmentVariable("THEORC_CASEFORGE_URL", "http://localhost:8788");
        Environment.SetEnvironmentVariable("THEORC_CASEFORGE_TOKEN", "test-token-012345");
        Environment.SetEnvironmentVariable("THEORC_CASEFORGE_WORKSPACE_URL", "not a URL");

        var names = OrcChatToolCatalog.CreateWorkspaceTools(root).Select(t => t.Name).ToList();

        Assert.That(names, Does.Not.Contain("model3d_create"));
    }

    [Test]
    public void CreateWorkspaceTools_addsArtForgeOnlyWhenConfigurationIsValid()
    {
        var root = TestContext.CurrentContext.WorkDirectory;
        Environment.SetEnvironmentVariable("THEORC_ARTFORGE_URL", "http://localhost:8288");
        Environment.SetEnvironmentVariable("THEORC_ARTFORGE_TOKEN", "test-device-token-012345");

        var names = OrcChatToolCatalog.CreateWorkspaceTools(root).Select(t => t.Name).ToList();

        Assert.That(names, Does.Contain("image_create"));
        Assert.That(names, Does.Contain("image_gallery"));
    }

    [Test]
    public void CreateWorkspaceTools_addsKeyHoundOnlyWhenConfigurationIsValid()
    {
        var root = TestContext.CurrentContext.WorkDirectory;
        Environment.SetEnvironmentVariable("THEORC_KEYHOUND_URL", "http://localhost:8000");
        Environment.SetEnvironmentVariable("THEORC_KEYHOUND_TOKEN", "test-keyhound-token-0123456789abcdef");

        var names = OrcChatToolCatalog.CreateWorkspaceTools(root).Select(t => t.Name).ToList();

        Assert.That(names, Does.Contain("atlas_start"));
        Assert.That(names, Does.Contain("atlas_evidence"));
    }

    [Test]
    public void BuildReactInstructions_addsOnlyConfiguredIntegrationWorkflows()
    {
        var root = TestContext.CurrentContext.WorkDirectory;
        Environment.SetEnvironmentVariable("THEORC_ARTFORGE_URL", "http://localhost:8288");
        Environment.SetEnvironmentVariable("THEORC_ARTFORGE_TOKEN", "test-device-token-012345");

        var tools = OrcChatToolCatalog.CreateWorkspaceTools(root);
        var prompt = OrcChatToolCatalog.BuildReactInstructions(tools);

        Assert.That(prompt, Does.Contain("Image workflow: call image_create"));
        Assert.That(prompt, Does.Not.Contain("3D workflow: call model3d_create"));
        Assert.That(prompt, Does.Not.Contain("Atlas workflow: call atlas_start"));
    }
}
