// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class NativePromptBuilderTests
{
    [Test]
    public void FoldSystemIntoFirstUser_Preserves_Instructions_And_Removes_System_Role()
    {
        var messages = new List<AgentMessage>
        {
            new() { Role = MessageRole.System, Content = "Return JSON only." },
            new() { Role = MessageRole.User, Content = "Source text" },
        };

        var folded = NativePromptBuilder.FoldSystemIntoFirstUser(messages);

        Assert.Multiple(() =>
        {
            Assert.That(folded, Has.Count.EqualTo(1));
            Assert.That(folded[0].Role, Is.EqualTo(MessageRole.User));
            Assert.That(folded[0].Content, Does.Contain("Return JSON only."));
            Assert.That(folded[0].Content, Does.EndWith("Source text"));
            Assert.That(messages[1].Content, Is.EqualTo("Source text"));
        });
    }
}
