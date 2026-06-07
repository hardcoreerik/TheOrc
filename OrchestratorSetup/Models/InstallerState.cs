namespace OrchestratorSetup.Models;

/// <summary>
/// Accumulates every choice the user makes during the installation wizard.
/// Passed by reference through all pages and consumed by the installer logic
/// (Phase E–G) to drive downloads, file placement, and shortcut creation.
/// </summary>
public class InstallerState
{
    // ── Paths ─────────────────────────────────────────────────────────────────

    /// <summary>Where OrchestratorIDE.exe and its support files are installed.</summary>
    public string AppInstallPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "OrchestratorIDE");

    /// <summary>Where GGUF model files are stored. Can be a different drive from the app.</summary>
    public string ModelStoragePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "OrchestratorIDE", "Models");

    // ── Hardware (populated by HardwareDetector — Phase F) ───────────────────

    public string  DetectedGpuName    { get; set; } = "Unknown";
    public int     DetectedVramGb     { get; set; } = 0;
    public string  DetectedGpuVendor  { get; set; } = "unknown"; // "nvidia", "amd", "intel", "none"
    public string  CudaVersion        { get; set; } = "";        // e.g. "12.2" or ""
    public string  SelectedRuntimeVariant { get; set; } = "cpu"; // cuda12 | cuda11 | vulkan | avx2 | cpu

    // ── Profile ───────────────────────────────────────────────────────────────

    /// <summary>The profile key selected by the user (e.g. "web", "security").</summary>
    public string SelectedProfileId { get; set; } = "web";

    // ── Model ─────────────────────────────────────────────────────────────────

    /// <summary>The model manifest entry ID chosen by the user.</summary>
    public string SelectedModelId { get; set; } = "qwen25-coder-7b-q5";

    /// <summary>Direct HuggingFace GGUF download URL for the selected model.</summary>
    public string SelectedModelUrl { get; set; } = "";

    /// <summary>Expected file size in bytes (for progress display).</summary>
    public long SelectedModelSizeBytes { get; set; } = 0;

    // ── Ollama conflict handling ──────────────────────────────────────────────

    public bool OllamaDetected       { get; set; } = false;
    public bool OllamaRunning        { get; set; } = false;

    /// <summary>
    /// true  → use the existing Ollama service (skip llama.cpp download).
    /// false → install llama.cpp and ignore / stop Ollama.
    /// </summary>
    public bool UseExistingOllama    { get; set; } = false;

    // ── Shortcuts & launch ────────────────────────────────────────────────────

    public bool CreateDesktopShortcut  { get; set; } = true;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool LaunchAfterInstall     { get; set; } = true;

    // ── App exe delivery ──────────────────────────────────────────────────────

    /// <summary>
    /// Download URL for OrchestratorIDE.exe (resolved from manifest at install time).
    /// Points at the GitHub Releases "latest" asset so old installers always pull
    /// the current app version.
    /// </summary>
    public string AppDownloadUrl  { get; set; } = "";

    /// <summary>Expected app exe size in bytes (for progress display).</summary>
    public long   AppSizeBytes    { get; set; } = 0;

    /// <summary>Full path where OrchestratorIDE.exe is placed after download.</summary>
    public string AppExePath      => Path.Combine(AppInstallPath, "OrchestratorIDE.exe");

    // ── Download state (Phase E) ──────────────────────────────────────────────

    /// <summary>Overall bytes downloaded so far across all artefacts.</summary>
    public long TotalBytesDownloaded { get; set; } = 0;

    /// <summary>Total bytes to download (runtime zip + model).</summary>
    public long TotalBytesToDownload { get; set; } = 0;

    public bool InstallationComplete { get; set; } = false;
    public string? InstallationError  { get; set; } = null;

    // ── Computed ──────────────────────────────────────────────────────────────

    /// <summary>Runtime download URL built from manifest at model-selection time.</summary>
    public string RuntimeDownloadUrl  { get; set; } = "";

    /// <summary>Path where llama-server.exe ends up after the runtime zip is extracted.</summary>
    public string LlamaRuntimeExtractPath =>
        Path.Combine(AppInstallPath, "Runtime", "llama");

    /// <summary>Full path for the downloaded model file.</summary>
    public string ModelFilePath =>
        Path.Combine(ModelStoragePath,
                     Path.GetFileName(SelectedModelUrl.Length > 0 ? SelectedModelUrl : "model.gguf"));
}
