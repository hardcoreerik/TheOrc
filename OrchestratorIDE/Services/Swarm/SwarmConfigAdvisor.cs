using System.Diagnostics;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.Swarm;

// ── Hardware detection ────────────────────────────────────────────────────────

public record GpuInfo(string Name, int VramGb, int Index);

public record HardwareProfile(
    IReadOnlyList<GpuInfo> Gpus,
    int TotalVramGb,
    bool HasNvidiaSmi
)
{
    public static HardwareProfile Unknown => new([], 0, false);

    /// <summary>Friendly summary for display in the UI.</summary>
    public string Summary =>
        Gpus.Count == 0
            ? "No GPU detected (CPU only)"
            : string.Join(" + ", Gpus.Select(g => $"{g.Name} ({g.VramGb} GB)"))
              + $" = {TotalVramGb} GB VRAM";
}

// ── Recommendation output ─────────────────────────────────────────────────────

public enum ConfigSource { BenchmarkBased, ObservedBest, FallbackMinimal }

public record SwarmModelConfig(
    string BossModel,
    string CoderModel,
    string ResearcherModel,
    string TesterModel,
    int    EstimatedVramGb,
    double PredictedQualityScore,   // 0–10
    ConfigSource Source,
    string Reasoning
)
{
    public bool IsEmpty => string.IsNullOrEmpty(BossModel);

    /// <summary>Human-readable tier label.</summary>
    public string TierLabel => PredictedQualityScore switch
    {
        >= 8.0 => "🏆 Elite",
        >= 6.5 => "⭐ Strong",
        >= 5.0 => "✅ Capable",
        >= 3.5 => "⚠ Limited",
        _      => "🐌 Minimal",
    };
}

// ── Advisor ───────────────────────────────────────────────────────────────────

/// <summary>
/// Given the user's detected hardware and the models they have installed in Ollama,
/// recommends the optimal boss / coder / researcher / tester role assignments.
///
/// Selection strategy (in priority order):
///   1. If observed metrics exist (≥2 completed runs), surface the highest
///      QualityScore config that fits in available VRAM.
///   2. Otherwise, score each available model per role using profile benchmarks
///      and pack the best-scoring combination that fits in total VRAM.
///   3. Fallback: use the lightest available model for all roles.
/// </summary>
public static class SwarmConfigAdvisor
{
    // ── Hardware detection ────────────────────────────────────────────────────

    /// <summary>
    /// Detects installed GPUs and total VRAM via nvidia-smi.
    /// Returns HardwareProfile.Unknown if nvidia-smi is unavailable.
    /// </summary>
    public static async Task<HardwareProfile> DetectHardwareAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=index,name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return HardwareProfile.Unknown;

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return HardwareProfile.Unknown;

            var gpus  = new List<GpuInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[0].Trim(), out var idx))    continue;
                var name = parts[1].Trim();
                if (!int.TryParse(parts[2].Trim(), out var vramMb)) continue;
                gpus.Add(new GpuInfo(name, vramMb / 1024, idx));
            }

            return gpus.Count > 0
                ? new HardwareProfile(gpus, gpus.Sum(g => g.VramGb), HasNvidiaSmi: true)
                : HardwareProfile.Unknown;
        }
        catch { return HardwareProfile.Unknown; }
    }

    // ── Primary recommendation ────────────────────────────────────────────────

    /// <summary>
    /// Returns the recommended swarm configuration for the given hardware
    /// and the list of Ollama model IDs the user has installed.
    /// </summary>
    public static SwarmModelConfig Recommend(
        HardwareProfile hardware,
        IReadOnlyList<string> installedModels)
    {
        if (installedModels.Count == 0)
            return Fallback("No models installed", hardware);

        var vram = hardware.TotalVramGb;

        // ── Strategy 1: Use observed best config if enough data exists ────────
        var observed = SwarmMetricsStore.BestConfigForVram(vram, minRuns: 2);
        if (observed is not null)
        {
            // Verify the models are still installed
            if (installedModels.Contains(observed.BossModel)   &&
                installedModels.Contains(observed.CoderModel)  &&
                installedModels.Contains(observed.ResearcherModel))
            {
                // Tester: pick the lightest available model (structured output only)
                var tester = PickBestForRole(installedModels, vram, RoleKind.Tester,
                    exclude: null, preferFast: true) ?? observed.CoderModel;

                return new SwarmModelConfig(
                    BossModel:               observed.BossModel,
                    CoderModel:              observed.CoderModel,
                    ResearcherModel:         observed.ResearcherModel,
                    TesterModel:             tester,
                    EstimatedVramGb:         observed.TotalVramGb,
                    PredictedQualityScore:   observed.QualityScore * 10,
                    Source:                  ConfigSource.ObservedBest,
                    Reasoning: $"Best observed config across {observed.RunCount} runs " +
                               $"(pass rate: {observed.TesterPassRate:P0}, " +
                               $"success rate: {observed.SuccessRate:P0})"
                );
            }
        }

        // ── Strategy 2: Score-based selection from profile benchmarks ─────────
        return RecommendFromProfiles(installedModels, vram, hardware);
    }

    // ── Profile-based selection ───────────────────────────────────────────────

    private enum RoleKind { Boss, Coder, Researcher, Tester }

    private static SwarmModelConfig RecommendFromProfiles(
        IReadOnlyList<string> models, int vramGb, HardwareProfile hw)
    {
        // Tier labels for reasoning string
        string Tier(int score) => score switch
        {
            >= 8 => "elite",
            >= 6 => "strong",
            >= 5 => "capable",
            _    => "limited"
        };

        // Pick boss first (highest BossScore that fits)
        var boss = PickBestForRole(models, vramGb, RoleKind.Boss);
        if (boss is null) return Fallback("No model fits in available VRAM", hw);

        var bossProfile = ModelProfiles.Get(boss);
        var remaining   = vramGb - bossProfile.MinVramGb;

        // Coder: highest CoderScore in remaining VRAM
        // Can be same model as boss — Ollama handles it via KV cache sharing
        var coder        = PickBestForRole(models, remaining > 0 ? remaining : vramGb, RoleKind.Coder)
                        ?? boss;
        var coderProfile = ModelProfiles.Get(coder);

        // Researcher: highest ResearcherScore; can be lightweight (releases coder VRAM sooner)
        // Prefer same model as coder (already loaded) unless a stronger researcher fits
        var researcherVram = Math.Max(remaining - coderProfile.MinVramGb, coderProfile.MinVramGb);
        var researcher     = PickBestForRole(models, researcherVram, RoleKind.Researcher, prefer: coder)
                          ?? coder;

        // Tester: lightest model with decent TesterScore (JSON output only)
        var tester = PickBestForRole(models, vramGb, RoleKind.Tester, preferFast: true)
                  ?? coder;

        var bp = ModelProfiles.Get(boss);
        var cp = ModelProfiles.Get(coder);
        var rp = ModelProfiles.Get(researcher);
        var tp = ModelProfiles.Get(tester);

        // Estimate VRAM: boss + coder unique loads (researcher & tester may reuse coder weights)
        var estVram = bp.MinVramGb + (coder != boss ? cp.MinVramGb : 0);
        estVram = Math.Min(estVram, vramGb);

        var quality = (bp.BossScore * 0.30 + cp.CoderScore * 0.35 +
                       rp.ResearcherScore * 0.20 + tp.TesterScore * 0.15);

        var reasoning = vramGb >= 80
            ? $"High-VRAM config ({vramGb} GB): {Tier(bp.BossScore)} boss + {Tier(cp.CoderScore)} coder — optimal for large models."
            : vramGb >= 24
            ? $"Mid-range config ({vramGb} GB): {Tier(bp.BossScore)} boss + {Tier(cp.CoderScore)} coder. Upgrade to 48+ GB for elite tier."
            : vramGb >= 8
            ? $"Constrained config ({vramGb} GB): using {bp.ParamsBillions}B boss + {cp.ParamsBillions}B coder. Dual-GPU would unlock higher quality."
            : $"Minimal config: <8 GB VRAM detected — consider a larger GPU for significantly better swarm quality.";

        return new SwarmModelConfig(
            BossModel:             boss,
            CoderModel:            coder,
            ResearcherModel:       researcher,
            TesterModel:           tester,
            EstimatedVramGb:       estVram,
            PredictedQualityScore: quality,
            Source:                ConfigSource.BenchmarkBased,
            Reasoning:             reasoning
        );
    }

    /// <summary>
    /// Picks the best available model for a given role.
    /// Filters by MinVramGb ≤ vramBudget.
    /// </summary>
    private static string? PickBestForRole(
        IReadOnlyList<string> models,
        int vramBudget,
        RoleKind role,
        string? exclude    = null,
        string? prefer     = null,
        bool preferFast    = false)
    {
        // Score each candidate
        var scored = models
            .Where(m => m != exclude)
            .Select(m => (Model: m, Profile: ModelProfiles.Get(m)))
            .Where(x => x.Profile.MinVramGb <= Math.Max(vramBudget, 1))
            .Select(x =>
            {
                var score = role switch
                {
                    RoleKind.Boss       => x.Profile.BossScore       * 1.0,
                    RoleKind.Coder      => x.Profile.CoderScore       * 1.0,
                    RoleKind.Researcher => x.Profile.ResearcherScore  * 1.0,
                    RoleKind.Tester     => x.Profile.TesterScore      * 1.0,
                    _                   => x.Profile.SwarmScore,
                };
                // Bonus for preferred model (same model already loaded)
                if (x.Model == prefer) score += 0.5;
                // Tester: prefer fast models to keep verification cheap
                if (preferFast && x.Profile.Speed == SpeedTier.Fast) score += 1.0;
                return (x.Model, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        return scored.FirstOrDefault().Model;
    }

    private static SwarmModelConfig Fallback(string reason, HardwareProfile hw)
    {
        return new SwarmModelConfig(
            BossModel:             "",
            CoderModel:            "",
            ResearcherModel:       "",
            TesterModel:           "",
            EstimatedVramGb:       0,
            PredictedQualityScore: 0,
            Source:                ConfigSource.FallbackMinimal,
            Reasoning:             reason
        );
    }

    // ── Tier summary (for UI tooltip) ─────────────────────────────────────────

    /// <summary>
    /// Returns a multi-line textual breakdown of what the recommendation would look like
    /// at different VRAM tiers, for the "what if I had more VRAM?" tooltip.
    /// </summary>
    public static string GetVramTierSummary(IReadOnlyList<string> installedModels)
    {
        var tiers = new[] { 8, 16, 24, 48, 96, 192 };
        var sb    = new System.Text.StringBuilder();

        var fakeHw = new HardwareProfile([], 0, false);

        foreach (var vram in tiers)
        {
            var cfg = RecommendFromProfiles(installedModels, vram,
                new HardwareProfile([], vram, false));
            if (cfg.IsEmpty) continue;

            var boss  = ShortName(cfg.BossModel);
            var coder = ShortName(cfg.CoderModel);
            sb.AppendLine($"{vram,4} GB VRAM  →  Boss: {boss,-26} Coder: {coder,-26}  [{cfg.TierLabel}]");
        }

        return sb.Length > 0 ? sb.ToString().TrimEnd() : "No profiles available.";
    }

    private static string ShortName(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return "(none)";
        // For hf.co/... models, extract the filename part
        if (modelId.Contains('/'))
        {
            var seg = modelId.Split('/').Last();
            var m   = Regex.Match(seg, @"([A-Za-z0-9\.\-]+)-GGUF:(.+)$");
            if (m.Success) return $"{m.Groups[1].Value}:{m.Groups[2].Value}";
            return seg.Length > 28 ? seg[..28] + "…" : seg;
        }
        return modelId.Length > 28 ? modelId[..28] + "…" : modelId;
    }
}
