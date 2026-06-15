// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Models;

/// <summary>
/// A model entry from our hand-curated catalog (Resources/curated-models.json).
/// Enriches live HuggingFace search results with human-verified descriptions,
/// swarm-role recommendations, and tool-use compatibility notes.
/// </summary>
public class CuratedModelEntry
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string   Id              { get; set; } = "";
    public string   Name            { get; set; } = "";

    /// <summary>
    /// HuggingFace repo path (e.g. "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF").
    /// Used to look up the live file listing. Verified at runtime — if 404,
    /// the entry is hidden and flagged rather than crashing.
    /// </summary>
    public string   HuggingFaceId   { get; set; } = "";

    /// <summary>Ollama pull tag (e.g. "qwen2.5-coder:7b"). Empty = not in Ollama library.</summary>
    public string   OllamaName      { get; set; } = "";

    // ── Provenance ────────────────────────────────────────────────────────────
    public string   Publisher       { get; set; } = "";
    public string   Architecture    { get; set; } = "";
    public double   ParametersB     { get; set; }

    // ── Capabilities ──────────────────────────────────────────────────────────
    public int      ContextK        { get; set; }
    public string   Description     { get; set; } = "";
    public string   IntendedUse     { get; set; } = "";
    public string   ToolUse         { get; set; } = "";

    // ── Swarm compatibility ───────────────────────────────────────────────────
    /// <summary>Roles this model is suited for: "worker", "boss", "researcher".</summary>
    public string[] SwarmRoles      { get; set; } = [];
    public bool     SwarmCapable    { get; set; }

    // ── Hardware profile ──────────────────────────────────────────────────────
    public int      VramMinGb           { get; set; }
    public int      VramRecommendedGb   { get; set; }
    public bool     CpuOk               { get; set; }
    public string   RecommendedQuant    { get; set; } = "";
    public int      QualityStars        { get; set; }

    // ── Tags ──────────────────────────────────────────────────────────────────
    public string[] Tags            { get; set; } = [];

    // ── Runtime state (not persisted — set during search) ────────────────────

    /// <summary>True when the HF repo was found during the last verify pass.</summary>
    public bool     HfRepoVerified  { get; set; } = true;

    // ── Computed display helpers ──────────────────────────────────────────────

    public string StarsDisplay =>
        new string('★', QualityStars) + new string('☆', 5 - QualityStars);

    public string VramDisplay =>
        VramMinGb == 0
            ? "CPU only"
            : $"{VramMinGb}–{VramRecommendedGb} GB VRAM";

    public string ParameterDisplay =>
        ParametersB >= 1.0 ? $"{ParametersB:F1}B params" : $"{ParametersB * 1000:F0}M params";

    public string PrimaryRoleDisplay =>
        SwarmRoles.Length > 0
            ? string.Join(" · ", SwarmRoles.Select(r =>
                r switch { "boss" => "Boss", "worker" => "Worker", "researcher" => "Researcher", _ => r }))
            : "Library only";

    public bool HasOllama => !string.IsNullOrEmpty(OllamaName);
    public bool HasHuggingFace => !string.IsNullOrEmpty(HuggingFaceId);

    public string ContextDisplay => ContextK >= 1 ? $"{ContextK}K ctx" : "";
}
