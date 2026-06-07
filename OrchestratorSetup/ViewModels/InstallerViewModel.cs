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
        Welcome       = 0,
        License       = 1,
        HardwareDetect= 2,
        InstallPath   = 3,
        Profile       = 4,
        ModelSelect   = 5,
        OllamaCheck   = 6,
        Download      = 7,
        Complete      = 8,
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

    public List<ModelEntry> AllModels      { get; } = [];
    public ModelEntry?      RecommendedModel { get; private set; }

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
    /// Falls back to the 7B Q5 balanced model if nothing matches.
    /// </summary>
    public void UpdateRecommendedModel()
    {
        var vram    = State.DetectedVramGb;
        var profile = State.SelectedProfileId;

        // 1. Check profile override table
        ModelEntry? pick = null;
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

        // 2. VRAM tier fallback
        // NOTE: 8 GB tier → Q5 7B (not Q8) — Q8 7B needs 10 GB+, would OOM on 8 GB laptops
        if (pick is null)
        {
            var tiers = new[] { (2, "qwen25-coder-1-5b-q8"), (4, "qwen25-coder-3b-q8"),
                                (6, "qwen25-coder-7b-q5"),   (8, "qwen25-coder-7b-q5"),
                                (10,"qwen25-coder-7b-q8"),   (14,"qwen25-coder-14b-q4"),
                                (20,"qwen25-coder-32b-q3"),  (99,"qwen25-coder-32b-q4") };
            foreach (var (ceiling, id) in tiers)
            {
                if (vram <= ceiling)
                {
                    pick = AllModels.FirstOrDefault(m => m.Id == id);
                    break;
                }
            }
        }

        // 3. Ultimate fallback
        pick ??= AllModels.FirstOrDefault(m => m.Id == "qwen25-coder-7b-q5")
              ?? AllModels.FirstOrDefault();

        RecommendedModel = pick;

        if (pick is not null && string.IsNullOrEmpty(State.SelectedModelId))
        {
            State.SelectedModelId   = pick.Id;
            State.SelectedModelUrl  = pick.Url;
            State.SelectedModelSizeBytes = pick.SizeBytes;
        }

        OnPropertyChanged(nameof(RecommendedModel));
    }

    /// <summary>Apply the user's explicit model choice.</summary>
    public void SelectModel(ModelEntry model)
    {
        State.SelectedModelId        = model.Id;
        State.SelectedModelUrl       = model.Url;
        State.SelectedModelSizeBytes = model.SizeBytes;
        OnPropertyChanged(nameof(RecommendedModel));
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
                });
            }
        }
        catch
        {
            // Non-fatal — UI will show an empty model list with a warning
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
