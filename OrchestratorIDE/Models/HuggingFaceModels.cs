// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Models;

// ── HuggingFace API response models ─────────────────────────────────────────
// All public API — no authentication required.
// Search: GET https://huggingface.co/api/models?search={q}&filter=gguf&sort=downloads
// Files:  GET https://huggingface.co/api/models/{id}
// README: GET https://huggingface.co/{id}/resolve/main/README.md

public class HfModelSearchResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("likes")]
    public int Likes { get; set; }

    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("pipeline_tag")]
    public string? PipelineTag { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTime? LastModified { get; set; }

    [JsonPropertyName("siblings")]
    public List<HfFileSibling>? Siblings { get; set; }
}

public class HfFileSibling
{
    [JsonPropertyName("rfilename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

public class HfModelDetail : HfModelSearchResult
{
    [JsonPropertyName("cardData")]
    public HfCardData? CardData { get; set; }
}

public class HfCardData
{
    [JsonPropertyName("language")]
    public List<string>? Language { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }
}

// ── Unified search result shown in the downloader UI ────────────────────────

/// <summary>
/// A single result card in the Model Downloader.
/// May come from HuggingFace live search, our curated catalog, or both merged.
/// </summary>
public class ModelSearchResult
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public string   Id              { get; set; } = "";   // curated ID or HF id
    public string   Name            { get; set; } = "";
    public string   HuggingFaceId   { get; set; } = "";   // HF repo path
    public string   OllamaName      { get; set; } = "";   // Ollama pull tag
    public string   Publisher       { get; set; } = "";
    public string   Architecture    { get; set; } = "";

    // ── Source tracking ───────────────────────────────────────────────────────
    /// <summary>Whether this result was enhanced by our curated catalog.</summary>
    public bool IsCurated           { get; set; }
    /// <summary>Whether this result came from a live HF search hit.</summary>
    public bool IsFromHuggingFace   { get; set; }
    /// <summary>Whether this model is in the Ollama library.</summary>
    public bool IsFromOllama        { get; set; }

    // ── Curated metadata (null for uncurated HF results) ─────────────────────
    public string   Description     { get; set; } = "";
    public string   IntendedUse     { get; set; } = "";
    public string   ToolUseNotes    { get; set; } = "";
    public string[] SwarmRoles      { get; set; } = [];
    public bool     SwarmCapable    { get; set; }
    public int      QualityStars    { get; set; }
    public string   RecommendedQuant { get; set; } = "";

    // ── Hardware profile ──────────────────────────────────────────────────────
    public int      VramMinGb           { get; set; }
    public int      VramRecommendedGb   { get; set; }
    public bool     CpuOk               { get; set; }
    public int      ContextK            { get; set; }

    // ── Files (resolved from HF API when user opens detail view) ─────────────
    public List<GgufVariant> Variants   { get; set; } = [];

    // ── HF stats ─────────────────────────────────────────────────────────────
    public int      HfDownloads     { get; set; }
    public int      HfLikes         { get; set; }
    public DateTime? LastUpdated    { get; set; }

    // ── Computed display helpers ──────────────────────────────────────────────

    public string StarsDisplay =>
        QualityStars > 0
            ? new string('★', QualityStars) + new string('☆', 5 - QualityStars)
            : "—";

    public string VramDisplay =>
        VramMinGb == 0 && CpuOk ? "CPU / Any"
        : VramMinGb == 0        ? "Unknown"
        : $"{VramMinGb}–{VramRecommendedGb} GB VRAM";

    public string DownloadsDisplay =>
        HfDownloads >= 1_000_000 ? $"{HfDownloads / 1_000_000.0:F1}M ↓"
        : HfDownloads >= 1_000   ? $"{HfDownloads / 1_000}K ↓"
        : HfDownloads > 0        ? $"{HfDownloads} ↓"
        : "";

    public string PrimaryRoleDisplay =>
        SwarmRoles.Length > 0
            ? string.Join(" · ", SwarmRoles.Select(r =>
                r switch { "boss" => "Boss", "worker" => "Worker", "researcher" => "Researcher", _ => r }))
            : "General";

    public bool HasOllama      => !string.IsNullOrEmpty(OllamaName);
    public bool HasHuggingFace => !string.IsNullOrEmpty(HuggingFaceId);

    public string ContextDisplay =>
        ContextK >= 1 ? $"{ContextK}K ctx" : "";
}

/// <summary>
/// A single downloadable GGUF quantization variant of a model.
/// Resolved from the HF repo's file listing.
/// </summary>
public class GgufVariant
{
    public string Filename      { get; set; } = "";
    public string QuantLabel    { get; set; } = "";   // e.g. "Q4_K_M"
    public long   SizeBytes     { get; set; }
    public int    VramEstimateGb { get; set; }        // computed from size + overhead
    public bool   IsRecommended { get; set; }
    public string DownloadUrl   { get; set; } = "";

    // ── Display helpers ───────────────────────────────────────────────────────

    public string SizeDisplay =>
        SizeBytes >= 1_073_741_824
            ? $"{SizeBytes / 1_073_741_824.0:F1} GB"
            : $"{SizeBytes / 1_048_576.0:F0} MB";

    public string QualityHint => QuantLabel switch
    {
        var q when q.StartsWith("Q2") => "☆☆  Low quality",
        var q when q.StartsWith("Q3") => "★★☆  Acceptable",
        var q when q.StartsWith("Q4") => "★★★  Balanced",
        var q when q.StartsWith("Q5") => "★★★★  Good",
        var q when q.StartsWith("Q6") => "★★★★☆  Very good",
        var q when q.StartsWith("Q8") => "★★★★★  Near-lossless",
        var q when q.StartsWith("F16") => "★★★★★  Lossless",
        _ => ""
    };
}
