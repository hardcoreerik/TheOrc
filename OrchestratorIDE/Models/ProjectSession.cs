// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Models;

public class ProjectSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string WorkspaceRoot { get; set; } = "";

    /// <summary>
    /// True only when the user explicitly opened a folder this session.
    /// Loaded defaults do NOT count — the agent must not write files until confirmed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsWorkspaceConfirmed { get; set; } = false;
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
