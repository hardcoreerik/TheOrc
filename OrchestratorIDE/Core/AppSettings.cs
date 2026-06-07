using System.Text.Json;

namespace OrchestratorIDE.Core;

// ── Inference backend selection ──────────────────────────────────────────────

/// <summary>
/// Which inference runtime The Orc connects to.
/// Ollama   — an external Ollama service (existing behaviour).
/// LlamaCpp — llama-server.exe managed directly by The Orc (installer path).
/// </summary>
public enum InferenceBackend { Ollama, LlamaCpp }

/// <summary>
/// User-configurable settings. Persisted to %APPDATA%\OrchestratorIDE\settings.json.
/// </summary>
public class AppSettings
{
    // ── Inference backend ─────────────────────────────────────────────────
    /// <summary>Which runtime to use. Default: Ollama (backwards-compatible).</summary>
    public InferenceBackend Backend { get; set; } = InferenceBackend.Ollama;

    // ── Ollama (Backend == Ollama) ────────────────────────────────────────
    public string OllamaHost    { get; set; } = "http://localhost:11434";

    // ── llama.cpp (Backend == LlamaCpp) ──────────────────────────────────
    /// <summary>Folder that contains llama-server.exe (and its CUDA/Vulkan DLLs).</summary>
    public string LlamaCppRuntimePath { get; set; } = "";

    /// <summary>Absolute path to the .gguf model file to load.</summary>
    public string LlamaCppModelPath   { get; set; } = "";

    /// <summary>Port the llama-server listens on. Default: 8080.</summary>
    public int    LlamaCppPort        { get; set; } = 8080;

    /// <summary>
    /// GPU layers to offload.
    ///  -1  = offload ALL layers (default — uses full GPU)
    ///   0  = CPU only
    ///  N>0 = offload exactly N layers
    /// </summary>
    public int    LlamaCppGpuLayers   { get; set; } = -1;

    /// <summary>Context window size in tokens. Default: 8192.</summary>
    public int    LlamaCppContextSize { get; set; } = 8192;

    /// <summary>CPU threads for token generation. 0 = auto-detect.</summary>
    public int    LlamaCppThreads     { get; set; } = 0;

    // ── Derived helper ────────────────────────────────────────────────────
    /// <summary>Base URL that OllamaClient should point at for the active backend.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string InferenceBaseUrl => Backend == InferenceBackend.LlamaCpp
        ? $"http://127.0.0.1:{LlamaCppPort}"
        : OllamaHost;

    // ── Agent behaviour ──────────────────────────────────────────────────
    public string DefaultModel  { get; set; } = "qwen2.5-coder:14b";
    public int    MaxStepsOverride { get; set; } = 0;   // 0 = use model profile default
    public bool   AutoVerify    { get; set; } = true;
    public bool   AutoCheckpoint { get; set; } = true;  // git commit before every Execute run

    // ── Workspace ────────────────────────────────────────────────────────
    public string DefaultWorkspace { get; set; } = "";

    /// <summary>
    /// Most-recently-used workspace folders, newest first. Max 10 entries.
    /// Used to auto-open the last workspace on startup and power the Open Recent menu.
    /// </summary>
    public List<string> RecentWorkspaces { get; set; } = [];

    // ── UI ────────────────────────────────────────────────────────────────
    public bool   ShowActivityLog  { get; set; } = true;
    public double ActivityLogHeight { get; set; } = 180;

    // ── Detected hardware (written by installer, read by app) ────────────
    /// <summary>GPU display name, e.g. "NVIDIA GeForce RTX 2070 SUPER".</summary>
    public string DetectedGpuName     { get; set; } = "";

    /// <summary>VRAM in GB as detected at install time.</summary>
    public double DetectedVramGb      { get; set; } = 0;

    /// <summary>Runtime variant chosen by the installer: cuda12, cuda11, vulkan, avx2, cpu.</summary>
    public string DetectedRuntime     { get; set; } = "";

    /// <summary>CUDA version string, e.g. "12.4". Empty if not NVIDIA.</summary>
    public string DetectedCudaVersion { get; set; } = "";

    // ── Personalisation (set by first-run wizard, editable in Settings) ──
    /// <summary>User's preferred name or handle shown to the agent.</summary>
    public string AgentUserName       { get; set; } = "";

    /// <summary>Any extra context the user wants the agent to always know.</summary>
    public string AgentExtraContext   { get; set; } = "";

    /// <summary>Set to true once the first-run personalisation wizard has completed.</summary>
    public bool   FirstRunComplete    { get; set; } = false;

    // ── Updates ───────────────────────────────────────────────────────────
    /// <summary>Whether to silently check GitHub for newer releases on startup.</summary>
    public bool      CheckForUpdates        { get; set; } = true;

    /// <summary>UTC timestamp of the last successful update check (null = never checked).</summary>
    public DateTime? LastUpdateCheckUtc     { get; set; } = null;

    /// <summary>Tag version string from the last check, e.g. "1.1.0". Empty = unknown.</summary>
    public string    LastKnownLatestVersion { get; set; } = "";

    // ── Persistence ───────────────────────────────────────────────────────
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "settings.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AppSettings>(text, _json) ?? new AppSettings();
            }
        }
        catch { /* corrupt settings — use defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, _json));
        }
        catch { /* non-fatal */ }
    }
}
