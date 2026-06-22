// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorSetup.Services;

namespace OrchestratorSetup.Models;

/// <summary>
/// Accumulates every choice the user makes during the installation wizard.
/// Passed by reference through all pages and consumed by the installer logic
/// (Phase E–G) to drive downloads, file placement, and shortcut creation.
/// </summary>
public class InstallerState
{
    // ── Paths ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where the app and its support files are installed. Defaults now come from
    /// PlatformInstaller.Current.DefaultAppDir -- this used to hardcode
    /// SpecialFolder.LocalApplicationData + "OrchestratorIDE" regardless of OS, which on
    /// macOS/Linux resolved to a DIFFERENT folder than MacPlatformInstaller/
    /// LinuxPlatformInstaller's own DefaultAppDir ("TheOrc"/"theorc") -- the same
    /// folder-name-drift bug already found and fixed once in LinuxPlatformInstaller's
    /// removeUserData step (2026-06-21); fixing it here too so a fresh macOS install
    /// doesn't reintroduce it via a different code path. Nothing differed between paths
    /// while only WindowsPlatformInstaller existed (Phase 2); now that Linux/macOS have
    /// real, different defaults, wiring this through is the actual fix, not deferred work.
    /// </summary>
    public string AppInstallPath { get; set; } = PlatformInstaller.Current.DefaultAppDir;

    /// <summary>Where GGUF model files are stored. Can be a different drive from the app.</summary>
    public string ModelStoragePath { get; set; } = PlatformInstaller.Current.DefaultModelDir;

    // ── Hardware (populated by HardwareDetector — Phase F) ───────────────────

    public string  DetectedGpuName    { get; set; } = "Unknown";
    public int     DetectedVramGb     { get; set; } = 0;
    public string  DetectedGpuVendor  { get; set; } = "unknown"; // "nvidia", "amd", "intel", "none"
    public string  CudaVersion        { get; set; } = "";        // e.g. "12.2" or ""
    public string  SelectedRuntimeVariant { get; set; } = "cpu"; // cuda12 | cuda11 | vulkan | avx2 | cpu | metal (macOS)

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

    // ── Multi-model swarm selection ───────────────────────────────────────────

    /// <summary>
    /// All models selected by the user. The first entry is always the primary
    /// Worker model (used for single-model llama.cpp path). Additional entries
    /// (Boss, Researcher) are downloaded and wired into AppSettings for Ollama
    /// swarm mode. Populated by InstallerViewModel.SyncSelectionToState().
    /// </summary>
    public List<ModelEntry> SelectedModels { get; set; } = [];

    /// <summary>
    /// Role assignments keyed by model ID:
    ///   "Worker · Coder", "Boss · Orchestrator", "Researcher"
    /// Written by InstallerViewModel.SyncSelectionToState() from the checkbox UI.
    /// </summary>
    public Dictionary<string, string> ModelRoles { get; set; } = new();

    /// <summary>
    /// Returns the full download destination path for any model in the list.
    /// Uses <see cref="ModelStoragePath"/> as the base directory.
    /// </summary>
    public string GetModelFilePath(ModelEntry model)
        => Path.Combine(ModelStoragePath,
               Path.GetFileName(model.Url.Length > 0 ? model.Url : $"{model.Id}.gguf"));

    // ── .NET SDK ──────────────────────────────────────────────────────────────

    /// <summary>True if dotnet --version returned 10.x at detection time.</summary>
    public bool DotNetSdkDetected    { get; set; } = false;

    // ── Ollama handling ───────────────────────────────────────────────────────

    public bool OllamaDetected       { get; set; } = false;
    public bool OllamaRunning        { get; set; } = false;

    /// <summary>
    /// HIVE MIND enrollment (spec §8): expose this PC''s AI to other TheOrc
    /// machines on the same private network. Sets OLLAMA_HOST=0.0.0.0 and adds
    /// Private-profile firewall rules for Ollama (11434) + the hive port (7077).
    /// </summary>
    public bool JoinHiveMind         { get; set; } = true;

    /// <summary>
    /// true  → use Ollama as the backend (existing OR freshly installed).
    /// false → install llama.cpp and ignore Ollama.
    /// </summary>
    public bool UseExistingOllama    { get; set; } = false;

    /// <summary>
    /// true  → installer should download OllamaSetup.exe and run it silently,
    ///          then pull the selected model via <c>ollama pull</c>.
    /// </summary>
    public bool InstallOllama        { get; set; } = false;

    /// <summary>
    /// The Ollama model tag to pull after installation, e.g. "qwen2.5-coder:7b".
    /// Populated from <see cref="ModelEntry.OllamaName"/> when the user picks a model.
    /// </summary>
    public string SelectedOllamaModel { get; set; } = "qwen2.5-coder:7b";

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

    /// <summary>
    /// Full path where the app binary is placed after download -- "OrchestratorIDE.exe" on
    /// Windows, "OrchestratorIDE" (no extension) on Linux/macOS, via
    /// PlatformInstaller.Current.LaunchCommand (the same OS-aware naming Phase 2/4/5 already
    /// established for launchers/uninstall; this was the one remaining hardcoded ".exe" call
    /// site, deferred until there was a real non-Windows download pipeline to wire it to).
    /// </summary>
    public string AppExePath => PlatformInstaller.Current.LaunchCommand(AppInstallPath);

    /// <summary>
    /// Where the app binary would sit if the user extracted it next to the installer itself
    /// (portable layout). Distinct from <see cref="AppExePath"/>, which is the final installed
    /// location — a stale binary already present there (from a prior install) must NOT be
    /// treated as "already downloaded" during an upgrade.
    /// </summary>
    public string PortableAppExePath =>
        PlatformInstaller.Current.LaunchCommand(AppContext.BaseDirectory);

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

    /// <summary>
    /// Full path for the primary (Worker) model file.
    /// Uses the first entry in <see cref="SelectedModels"/> when available;
    /// falls back to the legacy <see cref="SelectedModelUrl"/> field.
    /// </summary>
    public string ModelFilePath
    {
        get
        {
            var primaryUrl = SelectedModels.FirstOrDefault()?.Url;
            var url = primaryUrl is { Length: > 0 } ? primaryUrl
                    : SelectedModelUrl.Length > 0    ? SelectedModelUrl
                    : "model.gguf";
            return Path.Combine(ModelStoragePath, Path.GetFileName(url));
        }
    }
}
