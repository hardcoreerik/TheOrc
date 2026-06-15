// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using OrchestratorSetup.Models;

namespace OrchestratorSetup.ViewModels;

/// <summary>
/// Central coordinator for the installer wizard. Holds navigation state,
/// the shared <see cref="InstallerState"/>, and the loaded model catalogue.
/// All pages receive a reference to this ViewModel via their constructor.
/// </summary>
public class InstallerViewModel : INotifyPropertyChanged
{
    // ── Pages enum ────────────────────────────────────────────────────────────

    public enum Page
    {
        Welcome        = 0,
        License        = 1,
        HardwareDetect = 2,
        DotNetCheck    = 3,
        InstallPath    = 4,
        Profile        = 5,
        ModelSelect    = 6,
        OllamaCheck    = 7,
        Download       = 8,
        Complete       = 9,
    }

    private static readonly int PageCount = Enum.GetValues<Page>().Length;

    // ── State ─────────────────────────────────────────────────────────────────

    public InstallerState State { get; } = new();

    private Page _currentPage = Page.Welcome;
    public Page CurrentPage
    {
        get => _currentPage;
        private set { _currentPage = value; OnPropertyChanged(); OnPropertyChanged(nameof(PageIndex)); OnPropertyChanged(nameof(ProgressPercent)); OnPropertyChanged(nameof(CanGoBack)); OnPropertyChanged(nameof(CanGoNext)); }
    }

    public int PageIndex     => (int)CurrentPage;
    public double ProgressPercent => (double)PageIndex / (PageCount - 1) * 100.0;

    public bool CanGoBack => CurrentPage > Page.Welcome && CurrentPage < Page.Download;
    public bool CanGoNext => CurrentPage < Page.Download;

    // ── Model catalogue (loaded from bundled manifest) ────────────────────────

    public List<ModelEntry>          AllModels           { get; } = [];
    public List<SelectableModelEntry> AllSelectableModels { get; } = [];
    public ModelEntry?               RecommendedModel    { get; private set; }

    // True once ApplyBundleDefaults() has run — prevents resetting user edits
    // when UpdateRecommendedModel() is re-called by a profile change.
    private bool _bundleDefaultsApplied = false;

    // ── Construction ──────────────────────────────────────────────────────────

    public InstallerViewModel()
    {
        LoadManifest();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public void GoNext()
    {
        if ((int)CurrentPage < PageCount - 1)
            CurrentPage = (Page)((int)CurrentPage + 1);
    }

    public void GoBack()
    {
        if ((int)CurrentPage > 0)
            CurrentPage = (Page)((int)CurrentPage - 1);
    }

    public void NavigateTo(Page page) => CurrentPage = page;

    // ── Hardware detection result ─────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="Pages.HardwareDetectPage"/> once async detection finishes.
    /// Writes results into <see cref="InstallerState"/> and immediately refreshes
    /// the model recommendation so the ModelSelect page starts with the right pick.
    /// </summary>
    public void ApplyHardwareInfo(Services.HardwareDetector.HardwareInfo info)
    {
        State.DetectedGpuName        = info.GpuName;
        State.DetectedVramGb         = info.VramGb;
        State.DetectedGpuVendor      = info.Vendor;
        State.CudaVersion            = info.CudaVersion;
        State.SelectedRuntimeVariant = info.RuntimeVariant;

        // Reset any previously auto-selected model so UpdateRecommendedModel
        // can pick again with the real VRAM value.
        if (string.IsNullOrEmpty(State.SelectedModelId) ||
            State.SelectedModelId == "qwen25-coder-7b-q5")
        {
            State.SelectedModelId        = "";
            State.SelectedModelUrl       = "";
            State.SelectedModelSizeBytes = 0;
        }

        UpdateRecommendedModel();
        OnPropertyChanged(nameof(State));
    }

    // ── Model recommendation ──────────────────────────────────────────────────

    /// <summary>
    /// Called after hardware detection (Phase F) and profile selection to
    /// pick the best model for the user's hardware and coding style.
    ///
    /// Priority order:
    ///   1. Profile override (security → Hermes, game → DeepSeek, etc.)
    ///   2. NVIDIA hardware → NVIDIA Nemotron Mini (4–7 GB) or Google Gemma 4 (8–11 GB)
    ///   3. Generic VRAM tier (non-NVIDIA or partner model not in manifest)
    ///   4. Ultimate fallback — qwen25-coder-7b-q5
    /// </summary>
    public void UpdateRecommendedModel()
    {
        var vram    = State.DetectedVramGb;
        var profile = State.SelectedProfileId;
        var vendor  = State.DetectedGpuVendor; // "nvidia", "amd", "intel", "none"

        ModelEntry? pick = null;

        // ── 1. Profile override ──────────────────────────────────────────────
        var overrideMap = new Dictionary<string, (int minVram, string modelId)>
        {
            ["security"] = (10, "hermes4-14b-q5"),
            ["game"]     = (8,  "deepseek-coder-v2-lite-q5"),
            ["systems"]  = (8,  "deepseek-coder-v2-lite-q5"),
            ["uiux"]     = (4,  "phi4-mini-q8"),
            ["mobile"]   = (4,  "phi4-mini-q8"),
        };
        if (overrideMap.TryGetValue(profile, out var ov) && vram >= ov.minVram)
            pick = AllModels.FirstOrDefault(m => m.Id == ov.modelId);

        // ── 2. NVIDIA hardware — prefer NVIDIA + Google partner models ───────
        // NVIDIA Nemotron Mini runs best on NVIDIA hardware (4–7 GB).
        // Google Gemma 4 12B is excellent for NVIDIA users with 8–11 GB VRAM.
        // For higher VRAM, fall through to the Qwen flagship tiers.
        if (pick is null && vendor == "nvidia")
        {
            (int ceiling, string id)[] nvidiaTiers =
            [
                (3,  "qwen25-coder-1-5b-q8"),   // < 4 GB → tiny; Nemotron needs 4+ GB
                (7,  "nemotron-mini-4b-q5"),     // 4–7 GB  → NVIDIA Nemotron Mini  ★
                (11, "gemma4-12b-q4"),            // 8–11 GB → Google Gemma 4 12B    ★
                (16, "qwen25-coder-14b-q4"),      // 12–16 GB → Qwen 14B
                (20, "qwen25-coder-32b-q3"),      // 16–20 GB → Qwen 32B Q3
                (99, "qwen25-coder-32b-q4"),      // 20+ GB  → Qwen 32B Q4
            ];
            foreach (var (ceiling, id) in nvidiaTiers)
            {
                if (vram <= ceiling)
                {
                    pick = AllModels.FirstOrDefault(m => m.Id == id);
                    if (pick is not null) break;
                }
            }
        }

        // ── 3. Generic VRAM tier (non-NVIDIA, or partner model missing) ──────
        // NOTE: 8 GB tier → Q5 7B (not Q8) — Q8 7B needs 10 GB+; would OOM on 8 GB laptops
        if (pick is null)
        {
            (int ceiling, string id)[] tiers =
            [
                (2,  "qwen25-coder-1-5b-q8"),
                (4,  "qwen25-coder-3b-q8"),
                (6,  "qwen25-coder-7b-q5"),
                (8,  "qwen25-coder-7b-q5"),
                (10, "qwen25-coder-7b-q8"),
                (14, "qwen25-coder-14b-q4"),
                (20, "qwen25-coder-32b-q3"),
                (99, "qwen25-coder-32b-q4"),
            ];
            foreach (var (ceiling, id) in tiers)
            {
                if (vram <= ceiling)
                {
                    pick = AllModels.FirstOrDefault(m => m.Id == id);
                    break;
                }
            }
        }

        // ── 4. Ultimate fallback ─────────────────────────────────────────────
        pick ??= AllModels.FirstOrDefault(m => m.Id == "qwen25-coder-7b-q5")
              ?? AllModels.FirstOrDefault();

        RecommendedModel = pick;

        if (pick is not null && string.IsNullOrEmpty(State.SelectedModelId))
        {
            State.SelectedModelId        = pick.Id;
            State.SelectedModelUrl       = pick.Url;
            State.SelectedModelSizeBytes = pick.SizeBytes;
            State.SelectedOllamaModel    = pick.OllamaName ?? "qwen2.5-coder:7b";
        }

        OnPropertyChanged(nameof(RecommendedModel));

        // Apply bundle pre-selection now that we have a recommendation.
        // No-ops if user has already made manual selections.
        ApplyBundleDefaults();
    }

    /// <summary>Apply the user's explicit model choice (legacy single-select path).</summary>
    public void SelectModel(ModelEntry model)
    {
        State.SelectedModelId        = model.Id;
        State.SelectedModelUrl       = model.Url;
        State.SelectedModelSizeBytes = model.SizeBytes;
        State.SelectedOllamaModel    = model.OllamaName ?? "qwen2.5-coder:7b";

        OnPropertyChanged(nameof(RecommendedModel));
    }

    // ── Multi-model / Swarm Bundle ────────────────────────────────────────────

    /// <summary>
    /// Pre-selects the Swarm Bundle (Worker + Boss) and labels each entry with
    /// its intended role. Called automatically from UpdateRecommendedModel() after
    /// hardware is known. No-ops on repeat calls to preserve user edits.
    /// </summary>
    public void ApplyBundleDefaults()
    {
        if (_bundleDefaultsApplied)  return;
        if (RecommendedModel is null) return;
        if (!AllSelectableModels.Any()) return;

        // Clear prior state
        foreach (var e in AllSelectableModels) { e.IsSelected = false; e.RoleLabel = null; }

        // ── Worker = recommended model ───────────────────────────────────────
        var worker = AllSelectableModels.FirstOrDefault(e => e.Id == RecommendedModel.Id);
        if (worker is not null) { worker.IsSelected = true; worker.RoleLabel = "Worker · Coder"; }

        // ── Boss = phi4-mini-q8 → nemotron-mini → first swarm-capable ≠ worker
        SelectableModelEntry? boss = null;
        foreach (var id in new[] { "phi4-mini-q8", "nemotron-mini-4b-q5" })
        {
            boss = AllSelectableModels.FirstOrDefault(e => e.Id == id && e.Id != worker?.Id);
            if (boss is not null) break;
        }
        boss ??= AllSelectableModels.FirstOrDefault(e => e.SwarmCapable && e.Id != worker?.Id);

        if (boss is not null) { boss.IsSelected = true; boss.RoleLabel = "Boss · Orchestrator"; }

        // ── Researcher = smallest model ≠ worker ≠ boss (opt-in, not pre-checked) ─
        SelectableModelEntry? researcher = null;
        foreach (var id in new[] { "qwen25-coder-1-5b-q8", "qwen25-coder-3b-q8" })
        {
            researcher = AllSelectableModels.FirstOrDefault(
                e => e.Id == id && e.Id != worker?.Id && e.Id != boss?.Id);
            if (researcher is not null) break;
        }
        if (researcher is not null) { researcher.RoleLabel = "Researcher"; }

        _bundleDefaultsApplied = true;
        SyncSelectionToState();
    }

    /// <summary>
    /// Toggle a model's IsSelected state by ID and sync to InstallerState.
    /// Called by checkbox handlers in ModelPage.
    /// </summary>
    public void ToggleModel(string id, bool selected)
    {
        var entry = AllSelectableModels.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;
        entry.IsSelected = selected;
        SyncSelectionToState();
        OnPropertyChanged(nameof(SelectedModelCount));
        OnPropertyChanged(nameof(SelectedTotalSizeDisplay));
    }

    /// <summary>
    /// Pushes current IsSelected state from AllSelectableModels into
    /// InstallerState.SelectedModels and updates the legacy single-model fields.
    /// </summary>
    public void SyncSelectionToState()
    {
        State.SelectedModels = AllSelectableModels
            .Where(e => e.IsSelected)
            .Select(e => e.Model)
            .ToList();

        State.ModelRoles = AllSelectableModels
            .Where(e => e.IsSelected && e.HasRoleLabel)
            .ToDictionary(e => e.Id, e => e.RoleLabel!);

        // Keep legacy fields pointing at the Worker model
        var primary = AllSelectableModels.FirstOrDefault(
                          e => e.IsSelected && e.RoleLabel != null && e.RoleLabel.Contains("Worker"))
                   ?? AllSelectableModels.FirstOrDefault(e => e.IsSelected);

        if (primary is not null)
        {
            State.SelectedModelId        = primary.Id;
            State.SelectedModelUrl       = primary.Model.Url;
            State.SelectedModelSizeBytes = primary.Model.SizeBytes;
            State.SelectedOllamaModel    = primary.Model.OllamaName ?? "qwen2.5-coder:7b";
        }

        OnPropertyChanged(nameof(SelectedModelCount));
        OnPropertyChanged(nameof(SelectedTotalSizeDisplay));
    }

    // ── Selection summary for UI ──────────────────────────────────────────────

    /// <summary>Number of models currently checked.</summary>
    public int SelectedModelCount => AllSelectableModels.Count(e => e.IsSelected);

    /// <summary>Human-readable total download size of all checked models.</summary>
    public string SelectedTotalSizeDisplay
    {
        get
        {
            long total = AllSelectableModels
                .Where(e => e.IsSelected)
                .Sum(e => e.Model.SizeBytes);
            return total >= 1_073_741_824
                ? $"{total / 1_073_741_824.0:F1} GB"
                : $"{total / 1_048_576.0:F0} MB";
        }
    }

    // ── Manifest loading ──────────────────────────────────────────────────────

    private void LoadManifest()
    {
        try
        {
            // Reads from disk (dev mode) or embedded resource (production single-file)
            var json = Services.EmbeddedResources.ReadManifestJson();
            if (json is null) return;

            var root = JsonNode.Parse(json);
            var models = root?["models"]?.AsArray();
            if (models is null) return;

            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

            foreach (var node in models)
            {
                if (node is null) continue;
                // Skip comment-only nodes
                if (node["id"] is null) continue;

                AllModels.Add(new ModelEntry
                {
                    Id                = node["id"]!.GetValue<string>(),
                    Name              = node["name"]?.GetValue<string>()        ?? "",
                    Description       = node["description"]?.GetValue<string>() ?? "",
                    Publisher         = node["publisher"]?.GetValue<string>()   ?? "",
                    Quantization      = node["quantization"]?.GetValue<string>() ?? "",
                    ParametersB       = node["parameters_b"]?.GetValue<double>() ?? 0,
                    VramMinGb         = node["vram_min_gb"]?.GetValue<int>()    ?? 0,
                    VramRecommendedGb = node["vram_recommended_gb"]?.GetValue<int>() ?? 0,
                    SizeBytes         = node["size_bytes"]?.GetValue<long>()    ?? 0,
                    Url               = node["url"]?.GetValue<string>()         ?? "",
                    Sha256            = node["sha256"]?.GetValue<string?>(),
                    QualityStars      = node["quality_stars"]?.GetValue<int>()  ?? 0,
                    CpuOk             = node["cpu_ok"]?.GetValue<bool>()        ?? false,
                    ContextK          = node["context_k"]?.GetValue<int>()      ?? 4,
                    Profiles          = node["profiles"]?.AsArray().Select(p => p?.GetValue<string>() ?? "").ToArray() ?? [],
                    Tags              = node["tags"]?.AsArray().Select(t => t?.GetValue<string>() ?? "").ToArray() ?? [],
                    OllamaName        = node["ollama_name"]?.GetValue<string?>(),
                    SwarmCapable      = node["swarm_capable"]?.GetValue<bool>() ?? false,
                });
            }
        }
        catch
        {
            // Non-fatal — UI will show an empty model list with a warning
        }

        // Build the selectable wrappers now that AllModels is populated
        AllSelectableModels.Clear();
        foreach (var m in AllModels)
            AllSelectableModels.Add(new SelectableModelEntry(m));
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
