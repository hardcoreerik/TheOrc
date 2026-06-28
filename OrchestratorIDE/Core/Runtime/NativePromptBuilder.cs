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

    internal static string ToRoleString(MessageRole role) => role switch
    {
        MessageRole.System => "system",
        MessageRole.User => "user",
        MessageRole.Tool => "tool",
        _ => "assistant",
    };
}
