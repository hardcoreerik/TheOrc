// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

internal static class NativePromptBuilder
{
    private static readonly JsonSerializerOptions _compactJson = new() { WriteIndented = false };

    internal static List<AgentMessage> PrepareMessages(
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools)
    {
        var messages = history.ToList();

        if (tools is not { Count: > 0 })
            return messages;

        var toolJson = JsonSerializer.Serialize(tools, _compactJson);
        var toolBlock = $"\n\nAvailable tools (call as JSON):\n{toolJson}";
        var sysIdx = messages.FindIndex(m => m.Role == MessageRole.System);
        if (sysIdx >= 0)
            messages[sysIdx] = messages[sysIdx].WithContent(messages[sysIdx].Content + toolBlock);
        else
            messages.Insert(0, new AgentMessage
            {
                Role = MessageRole.System,
                Content = toolBlock,
            });

        return messages;
    }

    internal static string BuildChatMLPrompt(IEnumerable<AgentMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.Append("<|im_start|>").Append(ToRoleString(msg.Role)).Append('\n');
            sb.Append(msg.Content ?? "");
            sb.AppendLine("<|im_end|>");
        }

        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    internal static string BuildGemma4Prompt(IReadOnlyList<AgentMessage> messages)
    {
        var sb = new StringBuilder("<bos>");
        foreach (var msg in messages)
        {
            var role = msg.Role switch
            {
                MessageRole.System => "system",
                MessageRole.Assistant => "model",
                _ => "user",
            };
            sb.Append("<|turn>").Append(role).Append('\n');
            if (msg.Role == MessageRole.Tool)
                sb.Append("Tool result:\n");
            sb.Append((msg.Content ?? "").Trim()).Append("<turn|>\n");
        }

        // Gemma 4's template uses an empty thought channel to request a direct answer.
        sb.Append("<|turn>model\n<|channel>thought\n<channel|>");
        return sb.ToString();
    }

    internal static List<AgentMessage> FoldSystemIntoFirstUser(IReadOnlyList<AgentMessage> messages)
    {
        var systemText = string.Join("\n\n", messages
            .Where(message => message.Role == MessageRole.System)
            .Select(message => message.Content)
            .Where(content => !string.IsNullOrWhiteSpace(content)));
        if (systemText.Length == 0)
            return messages.ToList();

        var folded = messages.Where(message => message.Role != MessageRole.System).ToList();
        var userIndex = folded.FindIndex(message => message.Role == MessageRole.User);
        var prefix = $"System instructions:\n{systemText}\n\nUser input:\n";
        if (userIndex >= 0)
            folded[userIndex] = folded[userIndex].WithContent(prefix + folded[userIndex].Content);
        else
            folded.Insert(0, new AgentMessage { Role = MessageRole.User, Content = prefix });
        return folded;
    }

    internal static string ToRoleString(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Tool => "tool",
        _ => "assistant",
    };
}
