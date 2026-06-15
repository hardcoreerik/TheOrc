// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Models;

/// <summary>
/// A complete training plan produced by the Pit Boss wizard.
/// Saved to training_pit/plans/{PlanId}.json.
/// Drives dataset generation, LoRA training, and optional HIVEMIND distribution.
/// </summary>
public sealed class TrainingPlan
{
    // ── Identity ─────────────────────────────────────────────────────────────
    public string PlanId      { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    // ── What to train ────────────────────────────────────────────────────────
    /// <summary>Human-readable one-liner goal, e.g. "Better C# code reviewer — Senior architect tier"</summary>
    public string Goal    { get; init; } = "";
    /// <summary>Persona description that becomes the system prompt anchor in generated examples.</summary>
    public string Persona { get; init; } = "";
    /// <summary>Response style, e.g. "terse, technical, formal"</summary>
    public string Style   { get; init; } = "";
    /// <summary>Primary programming languages. Empty = language-agnostic.</summary>
    public List<string> Languages { get; init; } = [];

    // ── Task mix (must sum to ~1.0) ──────────────────────────────────────────
    /// <summary>Weights per task_type, e.g. {"code_review": 0.6, "bugfix": 0.3, "docs": 0.1}</summary>
    public Dictionary<string, double> TaskMix { get; init; } = [];

    // ── Dataset configuration ────────────────────────────────────────────────
    /// <summary>Total number of training examples to generate.</summary>
    public int    DatasetTarget   { get; init; } = 800;
    /// <summary>Generation backend: "cerebras" | "ollama" | "manual"</summary>
    public string DatasetSource   { get; init; } = "cerebras";
    /// <summary>Ollama model used for local generation (if DatasetSource == "ollama").</summary>
    public string DatasetGenModel { get; init; } = "qwen2.5-coder:14b";

    // ── Training configuration ────────────────────────────────────────────────
    public string BaseModel   { get; init; } = "qwen2.5-coder:14b";
    /// <summary>Directory name under training_pit/outputs/</summary>
    public string AdapterName { get; init; } = "";
    /// <summary>LoRA rank. Higher = more capacity, more VRAM, slower convergence.</summary>
    public int    LoraRank    { get; init; } = 16;
    /// <summary>Training epochs.</summary>
    public int    Epochs      { get; init; } = 3;
    public double LearningRate { get; init; } = 2e-4;

    // ── Estimates ────────────────────────────────────────────────────────────
    /// <summary>Estimated hours for dataset generation phase.</summary>
    public double EstDatasetHours { get; init; }
    /// <summary>Estimated hours for LoRA training phase.</summary>
    public double EstTrainHours   { get; init; }

    // ── HIVEMIND distribution (null = local only) ────────────────────────────
    public HiveStrategy? Hive { get; init; }

    // ── Execution state ──────────────────────────────────────────────────────
    public PlanPhase Phase        { get; set; } = PlanPhase.Ready;
    public string    DatasetFile  { get; set; } = "";  // populated after dataset gen
    public string    AdapterPath  { get; set; } = "";  // populated after training

    // ── Free notes (from Pit Boss synthesis) ────────────────────────────────
    public string Notes { get; init; } = "";

    // ── Derived helpers ───────────────────────────────────────────────────────
    [JsonIgnore]
    public string EstimateText
    {
        get
        {
            var parts = new List<string>();
            if (EstDatasetHours > 0) parts.Add($"~{EstDatasetHours:F0}h dataset gen");
            if (EstTrainHours   > 0) parts.Add($"~{EstTrainHours:F0}h training");
            return parts.Count > 0 ? string.Join("  +  ", parts) : "estimate TBD";
        }
    }

    [JsonIgnore]
    public string LanguageText => Languages.Count > 0 ? string.Join(", ", Languages) : "any";

    [JsonIgnore]
    public string PlanFileName => $"plan_{PlanId}.json";
}

public sealed class HiveStrategy
{
    public bool         Enabled       { get; init; }
    /// <summary>Node IDs or names assigned to dataset generation.</summary>
    public List<string> DatasetNodes  { get; init; } = [];
    /// <summary>Node assigned to LoRA training (needs most VRAM).</summary>
    public string       TrainingNode  { get; init; } = "";
    /// <summary>Node assigned to eval runs.</summary>
    public string       EvalNode      { get; init; } = "";
}

public enum PlanPhase
{
    Ready,          // plan confirmed, nothing started
    GeneratingData, // dataset gen running
    Training,       // LoRA training running
    Evaluating,     // eval running
    Complete,       // adapter promoted
    Failed,
}
