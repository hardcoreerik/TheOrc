namespace OrchestratorIDE.Models;

public enum ToolCallStatus { Pending, AwaitingApproval, Approved, Rejected, Running, Complete, Failed }

public class ToolCall
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public Dictionary<string, object?> Arguments { get; set; } = [];
    public string? Result { get; set; }
    public ToolCallStatus Status { get; set; } = ToolCallStatus.Pending;
    public bool RequiresApproval { get; set; } = false;
    public string? DiffPreview { get; set; }   // populated for write_file
    public string? ExplainWhy { get; set; }    // agent's reason for this call
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
