namespace OrchestratorSetup.Models;

/// <summary>
/// A single model entry loaded from model-manifest.json.
/// Displayed on the Model Selection page.
/// </summary>
public class ModelEntry
{
    public string   Id                  { get; set; } = "";
    public string   Name                { get; set; } = "";
    public string   Description         { get; set; } = "";
    public string   Publisher           { get; set; } = "";
    public string   Quantization        { get; set; } = "";
    public double   ParametersB         { get; set; }
    public int      VramMinGb           { get; set; }
    public int      VramRecommendedGb   { get; set; }
    public long     SizeBytes           { get; set; }
    public string   Url                 { get; set; } = "";
    public string?  Sha256              { get; set; }
    public int      QualityStars        { get; set; }
    public bool     CpuOk               { get; set; }
    public int      ContextK            { get; set; }
    public string[] Profiles            { get; set; } = [];
    public string[] Tags                { get; set; } = [];

    /// <summary>
    /// Ollama model tag used for <c>ollama pull</c>, e.g. "qwen2.5-coder:7b".
    /// Null if this model is not available in the Ollama library.
    /// </summary>
    public string?  OllamaName          { get; set; }

    // ── Computed display helpers ──────────────────────────────────────────────

    public string SizeDisplay =>
        SizeBytes >= 1_073_741_824
            ? $"{SizeBytes / 1_073_741_824.0:F1} GB"
            : $"{SizeBytes / 1_048_576.0:F0} MB";

    public string VramDisplay =>
        VramMinGb == 0
            ? "CPU only (no GPU needed)"
            : $"{VramMinGb}–{VramRecommendedGb} GB VRAM";

    public string StarsDisplay => new string('★', QualityStars) + new string('☆', 5 - QualityStars);

    public string ContextDisplay => $"{ContextK}K context";

    public bool FitsInVram(int availableVramGb) =>
        CpuOk || availableVramGb >= VramMinGb;

    // ── Partner badge ─────────────────────────────────────────────────────────

    /// <summary>
    /// Short partner label shown as a coloured chip in the model list.
    /// Returns null for generic / community models.
    /// </summary>
    public string? PartnerBadge => Publisher.ToUpperInvariant() switch
    {
        var p when p.StartsWith("NVIDIA") => "NVIDIA",
        var p when p.StartsWith("GOOGLE") => "GOOGLE",
        _ => null
    };

    /// <summary>Badge background hex colour.</summary>
    public string PartnerBadgeBg => Publisher.ToUpperInvariant() switch
    {
        var p when p.StartsWith("NVIDIA") => "#182800",
        var p when p.StartsWith("GOOGLE") => "#001828",
        _ => "#1A1A1A"
    };

    /// <summary>Badge foreground hex colour.</summary>
    public string PartnerBadgeFg => Publisher.ToUpperInvariant() switch
    {
        var p when p.StartsWith("NVIDIA") => "#76B900",
        var p when p.StartsWith("GOOGLE") => "#4A9FD9",
        _ => "#888888"
    };

    /// <summary>Whether this model has a partner badge to display.</summary>
    public bool HasPartnerBadge => PartnerBadge is not null;

    /// <summary>
    /// True when this model can only be installed via <c>ollama pull</c>
    /// (no direct GGUF download URL is available).
    /// </summary>
    public bool OllamaOnly => string.IsNullOrWhiteSpace(Url);

    /// <summary>
    /// True when this model has been validated for Swarm multi-agent mode.
    /// Phase 1: NVIDIA Nemotron Mini only. Expand as more models are validated.
    /// </summary>
    public bool SwarmCapable { get; set; }
}
