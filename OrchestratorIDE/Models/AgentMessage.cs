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
}
