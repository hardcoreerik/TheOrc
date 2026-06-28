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

    [Test]
    public void BuildGemma4Prompt_Uses_Native_Turns_And_Disables_Thinking()
    {
        var prompt = NativePromptBuilder.BuildGemma4Prompt(
        [
            new AgentMessage { Role = MessageRole.System, Content = "Return JSON only." },
            new AgentMessage { Role = MessageRole.User, Content = "Source text" },
            new AgentMessage { Role = MessageRole.Assistant, Content = "Prior answer" },
            new AgentMessage { Role = MessageRole.Tool, Content = "Tool output" },
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.StartWith("<bos><|turn>system\nReturn JSON only.<turn|>\n"));
            Assert.That(prompt, Does.Contain("<|turn>user\nSource text<turn|>"));
            Assert.That(prompt, Does.Contain("<|turn>model\nPrior answer<turn|>"));
            Assert.That(prompt, Does.Contain("<|turn>user\nTool result:\nTool output<turn|>"));
            Assert.That(prompt, Does.EndWith("<|turn>model\n<|channel>thought\n<channel|>"));
        });
    }
}
