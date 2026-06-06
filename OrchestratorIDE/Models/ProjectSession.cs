namespace OrchestratorIDE.Models;

public class ProjectSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string WorkspaceRoot { get; set; } = "";
    public string ActiveModel { get; set; } = "qwen2.5-coder:14b";
    public List<AgentMessage> Messages { get; set; } = [];
    public List<string> ActiveRules { get; set; } = [];
    public AgentMode Mode { get; set; } = AgentMode.Plan;
    public string? PlanText { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public int TotalTokensUsed { get; set; }
    public string? LastCheckpointSha { get; set; }
}

public enum AgentMode { Plan, Execute }
