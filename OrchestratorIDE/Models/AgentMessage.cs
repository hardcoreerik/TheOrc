// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Models;

public enum MessageRole { User, Assistant, System, Tool }
public enum MessageStatus { Pending, Streaming, Complete, Error }

public class AgentMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public MessageStatus Status { get; set; } = MessageStatus.Pending;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public List<ToolCall> ToolCalls { get; set; } = [];
    public string? ToolCallId { get; set; }   // for role=Tool responses
    public int TokenCount { get; set; }
    public List<ChatAttachment> Attachments { get; set; } = [];

    /// <summary>
    /// Returns a copy of this message with <paramref name="content"/> substituted.
    /// All other fields are preserved; ToolCalls list is shallow-copied so mutations
    /// to the returned message's list don't affect the original.
    /// Centralises field copying — add new fields here when AgentMessage grows.
    /// </summary>
    public AgentMessage WithContent(string content) => new()
    {
        Id         = Id,
        Role       = Role,
        Content    = content,
        Status     = Status,
        Timestamp  = Timestamp,
        ToolCalls  = new List<ToolCall>(ToolCalls),
        ToolCallId = ToolCallId,
        TokenCount = TokenCount,
        Attachments = new List<ChatAttachment>(Attachments),
    };
}
