// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public class OllamaClientAttachmentTests
{
    [Test]
    public void BuildWireContent_withoutAttachments_returnsPlainString()
    {
        var message = new AgentMessage { Role = MessageRole.User, Content = "hello" };

        var content = OllamaClient.BuildWireContent(message);

        Assert.That(content, Is.EqualTo("hello"));
    }

    [Test]
    public void BuildWireContent_withImageAttachment_returnsOpenAiStyleContentParts()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"chat_attachment_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+k2X8AAAAASUVORK5CYII="));

        try
        {
            var message = new AgentMessage
            {
                Role = MessageRole.User,
                Content = "describe this",
                Attachments = [ChatAttachment.FromPath(path)],
            };

            var content = OllamaClient.BuildWireContent(message);
            var json = JsonSerializer.Serialize(content);

            Assert.That(json, Does.Contain("\"type\":\"text\""));
            Assert.That(json, Does.Contain("\"type\":\"image_url\""));
            Assert.That(json, Does.Contain("data:image/png;base64,"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
