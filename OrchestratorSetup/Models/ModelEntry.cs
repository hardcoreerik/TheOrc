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
}
