// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Research;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public class OrcChatToolCatalogTests
{
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
}
